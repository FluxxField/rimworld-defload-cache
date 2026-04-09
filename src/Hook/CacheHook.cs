using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Static entry points called from IL injected by IlInjector.
    ///
    /// Stage A: HookFired logs that ApplyPatches was entered.
    /// Stage B: HookFired also computes and stashes the modlist fingerprint.
    /// Stage C: SaveToCache (postfix) stamps mod attribution, serializes the
    ///          merged doc, and writes the cache to disk.
    /// Stage D: TryLoadCached (prefix) checks for a cache file matching the
    ///          current fingerprint. On hit, replaces the working XmlDocument
    ///          with the cached version, rebuilds assetlookup from the
    ///          embedded attribution attributes, and returns true — the
    ///          injected brtrue then branches past the original body AND
    ///          SaveToCache directly to ret.
    /// </summary>
    public static class CacheHook
    {
        private static string? _currentFingerprint;

        /// <summary>
        /// Called by injected IL at the top of ApplyPatches. Computes and
        /// stashes the fingerprint. TryLoadCached (Stage D) reads this stash
        /// to decide cache-hit vs miss without recomputing.
        ///
        /// IMPORTANT: IlInjector resolves this by <c>nameof(CacheHook.HookFired)</c>.
        /// </summary>
        public static void HookFired()
        {
            try
            {
                Log.Message("hook fired — Verse.LoadedModManager.ApplyPatches entered");

                var sw = Stopwatch.StartNew();
                _currentFingerprint = ModlistFingerprint.Compute();
                sw.Stop();

                Log.Message($"fingerprint = {_currentFingerprint} (computed in {sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                Log.Error("HookFired threw — falling back to no-op", ex);
                _currentFingerprint = null;
            }
        }

        /// <summary>
        /// Called by injected IL immediately after HookFired. Returns true if
        /// a valid cache file exists for the current fingerprint; the
        /// injected brtrue then branches past the original body and
        /// SaveToCache directly to ret. Returns false on miss; execution
        /// falls through to the normal ApplyPatches body.
        ///
        /// On hit: mutates xmlDoc in place (RemoveAll + ImportNode from cached
        /// doc) and rebuilds assetlookup from the embedded data-defloadcache-mod
        /// attributes using real LoadableXmlAsset instances from the original
        /// assetlookup (LoadableXmlAsset's fields are readonly so we can't
        /// construct synthetic instances).
        ///
        /// IMPORTANT: IlInjector resolves this by <c>nameof(CacheHook.TryLoadCached)</c>.
        /// </summary>
        public static bool TryLoadCached(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            bool docMutated = false;
            try
            {
                if (_currentFingerprint == null)
                {
                    Log.Warning("TryLoadCached: no fingerprint cached, falling through to normal load");
                    return false;
                }
                if (xmlDoc == null)
                {
                    Log.Warning("TryLoadCached: xmlDoc is null, falling through");
                    return false;
                }

                if (!CacheStorage.TryRead(_currentFingerprint, out var bytes) || bytes == null)
                {
                    Log.Message("cache MISS — running normal ApplyPatches");
                    return false;
                }

                var sw = Stopwatch.StartNew();

                // Build packageId -> LoadableXmlAsset map from the existing
                // assetlookup BEFORE we mutate xmlDoc. Pick the first
                // LoadableXmlAsset encountered per mod — they're all
                // functionally equivalent for the fields ParseAndProcessXML
                // actually reads (.mod).
                var packageIdToAsset = new Dictionary<string, LoadableXmlAsset>();
                foreach (var kvp in assetlookup)
                {
                    var asset = kvp.Value;
                    if (asset?.mod?.PackageId == null) continue;
                    if (!packageIdToAsset.ContainsKey(asset.mod.PackageId))
                    {
                        packageIdToAsset[asset.mod.PackageId] = asset;
                    }
                }

                // Deserialize the cached doc
                XmlDocument cachedDoc = CacheFormat.Deserialize(bytes);

                // === POINT OF NO RETURN ===
                // After RemoveAll(), xmlDoc is mutated. If anything throws
                // between here and return true, the caller's xmlDoc is in a
                // partially-populated state. Returning false would let the
                // original ApplyPatches body run on corrupted state. The
                // catch block detects this via docMutated and rethrows.
                docMutated = true;
                xmlDoc.RemoveAll();
                foreach (XmlNode child in cachedDoc.ChildNodes)
                {
                    if (child.NodeType == XmlNodeType.XmlDeclaration) continue;
                    XmlNode imported = xmlDoc.ImportNode(child, deep: true);
                    xmlDoc.AppendChild(imported);
                }

                assetlookup.Clear();
                int rebuilt = ModAttributionTagger.RebuildAssetLookup(xmlDoc, assetlookup, packageIdToAsset);

                sw.Stop();
                Log.Message($"cache HIT — deserialized + populated in {sw.ElapsedMilliseconds}ms, {rebuilt} assetlookup entries rebuilt. Skipping original ApplyPatches body.");

                return true;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                if (docMutated)
                {
                    Log.Error("TryLoadCached threw AFTER xmlDoc was already mutated — cannot recover, rethrowing", ex);
                    throw;
                }
                Log.Error("TryLoadCached threw — falling through to normal ApplyPatches", ex);
                return false;
            }
        }

        /// <summary>
        /// Called by injected IL before every ret instruction in ApplyPatches.
        /// On cache-miss runs, this stamps attribution, serializes, and writes
        /// the cache. On cache-hit runs, the brtrue in TryLoadCached's injected
        /// prefix jumps PAST this call directly to ret — so SaveToCache is
        /// NOT invoked on cache-hit (no no-op check needed here).
        ///
        /// The "cache file already exists, skipping save" guard IS still
        /// present as belt-and-suspenders for the edge case where TryLoadCached
        /// returned false but the cache file was created between the check and
        /// the write.
        ///
        /// IMPORTANT: IlInjector resolves this by <c>nameof(CacheHook.SaveToCache)</c>.
        /// </summary>
        public static void SaveToCache(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            try
            {
                if (_currentFingerprint == null)
                {
                    Log.Warning("SaveToCache: no fingerprint cached, skipping save");
                    return;
                }
                if (xmlDoc == null)
                {
                    Log.Warning("SaveToCache: xmlDoc is null, skipping save");
                    return;
                }

                string existingPath = CacheStorage.PathForFingerprint(_currentFingerprint);
                if (System.IO.File.Exists(existingPath))
                {
                    Log.Message("SaveToCache: cache file already exists for this fingerprint, skipping save");
                    return;
                }

                var sw = Stopwatch.StartNew();
                ModAttributionTagger.StampAttributions(xmlDoc, assetlookup);
                byte[] bytes = CacheFormat.Serialize(xmlDoc);
                ModAttributionTagger.UnstampAttributions(xmlDoc);

                string metaJson = "{"
                    + $"\"timestamp\":\"{DateTime.UtcNow:o}\","
                    + $"\"modCount\":{LoadedModManager.RunningModsListForReading.Count},"
                    + $"\"rimworldVersion\":\"{RimWorld.VersionControl.CurrentVersionString}\","
                    + $"\"cacheFormatVersion\":{ModlistFingerprint.CacheFormatVersion},"
                    + $"\"sizeBytes\":{bytes.Length}"
                    + "}";

                CacheStorage.Write(_currentFingerprint, bytes, metaJson);
                sw.Stop();
                Log.Message($"SaveToCache: stamped + serialized + wrote in {sw.ElapsedMilliseconds}ms ({bytes.Length / 1024} KB)");
            }
            catch (Exception ex)
            {
                Log.Error("SaveToCache threw — cache not saved (game continues normally)", ex);
            }
        }
    }
}
