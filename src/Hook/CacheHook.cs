using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
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
        internal static bool CacheHitOccurred;
        internal static bool LastRunWasMiss;

        internal static string? CurrentFingerprint => _currentFingerprint;

        /// <summary>
        /// Pipeline-wide stopwatch. Starts when HookFired enters (top of
        /// ApplyPatches), reports cumulative elapsed at each subsequent stage.
        /// Gives wall-clock timing for the entire def-loading pipeline.
        /// </summary>
        private static readonly Stopwatch _pipelineSw = new Stopwatch();

        /// <summary>
        /// Called by injected IL at the top of LoadModXML (Stage G). Computes
        /// the fingerprint early and checks if a cache file exists. If yes,
        /// returns true — the injected brtrue skips LoadModXML's body and
        /// returns an empty List, so LoadModXML + CombineIntoUnifiedXML are
        /// effectively no-ops. The cached doc is loaded later in TryLoadCached.
        ///
        /// IMPORTANT: IlInjector resolves this by <c>nameof(CacheHook.ShouldSkipLoadModXML)</c>.
        /// </summary>
        public static bool ShouldSkipLoadModXML()
        {
            try
            {
                _pipelineSw.Restart();

                var settings = DefLoadCacheMod.Settings;

                // One-shot skip: "Test without cache" button or validation failure
                if (settings != null && settings.skipNextLaunch)
                {
                    settings.skipNextLaunch = false;
                    try { LoadedModManager.GetMod<DefLoadCacheMod>()?.WriteSettings(); } catch { }
                    Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] skipNextLaunch was set — running full uncached load this launch");
                    return false;
                }

                if (settings != null && (!settings.cacheEnabled || !settings.skipModFileLoading))
                {
                    Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] Stage G disabled in settings, running normally");
                    return false;
                }

                Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] [{DateTime.Now:HH:mm:ss.fff}] Stage G — LoadModXML entered");

                var sw = Stopwatch.StartNew();
                _currentFingerprint = ModlistFingerprint.Compute();
                sw.Stop();

                Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] fingerprint = {_currentFingerprint} (computed in {sw.ElapsedMilliseconds}ms)");

                if (CacheStorage.Exists(_currentFingerprint))
                {
                    Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] [{DateTime.Now:HH:mm:ss.fff}] Stage G cache EXISTS — skipping LoadModXML file I/O");
                    return true;
                }

                Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] Stage G cache MISS — LoadModXML will run normally");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("ShouldSkipLoadModXML threw — falling back to normal LoadModXML", ex);
                _currentFingerprint = null;
                return false;
            }
        }

        /// <summary>
        /// Called by injected IL at the top of ApplyPatches. On Stage G hit,
        /// fingerprint is already computed — just logs entry. On Stage G miss,
        /// computes the fingerprint here (same as before Stage G existed).
        ///
        /// IMPORTANT: IlInjector resolves this by <c>nameof(CacheHook.HookFired)</c>.
        /// </summary>
        public static void HookFired()
        {
            try
            {
                // If Stage G already computed the fingerprint and started the
                // pipeline stopwatch, don't restart — just log entry.
                if (_currentFingerprint != null)
                {
                    Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] [{DateTime.Now:HH:mm:ss.fff}] hook fired — ApplyPatches entered (fingerprint already computed by Stage G)");
                    return;
                }

                _pipelineSw.Restart();
                Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] [{DateTime.Now:HH:mm:ss.fff}] hook fired — Verse.LoadedModManager.ApplyPatches entered");

                var sw = Stopwatch.StartNew();
                _currentFingerprint = ModlistFingerprint.Compute();
                sw.Stop();

                Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] fingerprint = {_currentFingerprint} (computed in {sw.ElapsedMilliseconds}ms)");
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
                var settings = DefLoadCacheMod.Settings;
                if (settings != null && (!settings.cacheEnabled || !settings.skipPatchApplication))
                {
                    Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] Stage D disabled in settings, running normally");
                    return false;
                }
                if (settings != null && settings.skipNextLaunch)
                {
                    settings.skipNextLaunch = false;
                    try { LoadedModManager.GetMod<DefLoadCacheMod>()?.WriteSettings(); } catch { }
                    Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] skipNextLaunch was set — running normal ApplyPatches");
                    return false;
                }
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

                if (!CacheStorage.Exists(_currentFingerprint))
                {
                    Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] cache MISS — running normal ApplyPatches");
                    return false;
                }

                var sw = Stopwatch.StartNew();

                // Build packageId -> LoadableXmlAsset map. On normal runs
                // (Stage G miss), assetlookup is populated by
                // CombineIntoUnifiedXML and we pick real instances. On Stage G
                // hit, assetlookup is empty (LoadModXML was skipped), so we
                // create minimal synthetic instances from RunningModsListForReading
                // using FormatterServices to bypass the readonly constructor.
                var packageIdToAsset = new Dictionary<string, LoadableXmlAsset>();
                if (assetlookup.Count > 0)
                {
                    foreach (var kvp in assetlookup)
                    {
                        var asset = kvp.Value;
                        if (asset?.mod?.PackageId == null) continue;
                        if (!packageIdToAsset.ContainsKey(asset.mod.PackageId))
                        {
                            packageIdToAsset[asset.mod.PackageId] = asset;
                        }
                    }
                }
                else
                {
                    // Stage G hit path: build synthetic LoadableXmlAsset per mod.
                    // ParseAndProcessXML reads asset.mod (for def.modContentPack)
                    // and asset.name (for def.fileName). We set both via reflection.
                    foreach (var mod in LoadedModManager.RunningModsListForReading)
                    {
                        if (mod?.PackageId == null) continue;
                        var asset = CreateSyntheticAsset(mod);
                        if (asset != null)
                        {
                            packageIdToAsset[mod.PackageId] = asset;
                        }
                    }
                    Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] built {packageIdToAsset.Count} synthetic LoadableXmlAssets for Stage G cache hit");
                }

                // === POINT OF NO RETURN ===
                // xmlDoc.Load() replaces all content. If anything throws
                // after this point, the caller's xmlDoc is in a corrupt
                // state. Returning false would let the original ApplyPatches
                // body run on garbage. The catch block detects this via
                // docMutated and rethrows.
                //
                // Stream directly from file → gzip → xmlDoc.Load to avoid
                // creating a second XmlDocument and deep-copying 45k+ nodes.
                using (var stream = CacheStorage.OpenReadStream(_currentFingerprint))
                {
                    if (stream == null)
                    {
                        Log.Warning("TryLoadCached: cache file vanished between Exists and OpenReadStream");
                        return false;
                    }
                    docMutated = true;
                    xmlDoc.Load(stream);
                }

                assetlookup.Clear();
                int rebuilt = ModAttributionTagger.RebuildAssetLookup(xmlDoc, assetlookup, packageIdToAsset, out var actualCountsByMod);

                // Validate node counts against baseline in meta.json
                CacheValidator.Validate(_currentFingerprint!, actualCountsByMod);

                sw.Stop();
                Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] cache HIT — deserialized + populated in {sw.ElapsedMilliseconds}ms, {rebuilt} assetlookup entries rebuilt. Skipping original ApplyPatches body.");

                CacheHitOccurred = true;
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
        /// Called by injected IL at the top of ClearCachedPatches. Returns true
        /// on cache-hit runs so the injected brtrue skips the entire method body,
        /// preventing thousands of spurious "patch operation failed" log entries
        /// from patches that were never executed (because ApplyPatches was skipped).
        ///
        /// IMPORTANT: IlInjector resolves this by <c>nameof(CacheHook.ShouldSkipClearPatches)</c>.
        /// </summary>
        public static bool ShouldSkipClearPatches()
        {
            if (CacheHitOccurred)
            {
                Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] [{DateTime.Now:HH:mm:ss.fff}] ClearCachedPatches skipped — cache hit, patches were never executed");
                _pipelineSw.Stop();
                return true;
            }
            return false;
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

                // Collect per-mod node counts from the attributed doc for validation
                var nodeCountsByMod = CountNodesByMod(xmlDoc);
                int totalNodeCount = 0;
                foreach (var kvp in nodeCountsByMod)
                    totalNodeCount += kvp.Value;

                byte[] bytes = CacheFormat.Serialize(xmlDoc);
                ModAttributionTagger.UnstampAttributions(xmlDoc);

                // Build meta.json with node counts for post-load validation
                var metaSb = new System.Text.StringBuilder();
                metaSb.Append("{");
                metaSb.Append($"\"timestamp\":\"{DateTime.UtcNow:o}\",");
                metaSb.Append($"\"modCount\":{LoadedModManager.RunningModsListForReading.Count},");
                metaSb.Append($"\"rimworldVersion\":\"{RimWorld.VersionControl.CurrentVersionString}\",");
                metaSb.Append($"\"cacheFormatVersion\":{ModlistFingerprint.CacheFormatVersion},");
                metaSb.Append($"\"sizeBytes\":{bytes.Length},");
                metaSb.Append($"\"totalNodeCount\":{totalNodeCount},");
                metaSb.Append("\"nodeCountsByMod\":{");
                bool first = true;
                foreach (var kvp in nodeCountsByMod)
                {
                    if (!first) metaSb.Append(',');
                    metaSb.Append($"\"{kvp.Key.Replace("\"", "\\\"")}\":{kvp.Value}");
                    first = false;
                }
                metaSb.Append("}}");

                CacheStorage.Write(_currentFingerprint, bytes, metaSb.ToString());
                sw.Stop();
                LastRunWasMiss = true;
                Log.Message($"[T+{_pipelineSw.ElapsedMilliseconds}ms] [{DateTime.Now:HH:mm:ss.fff}] SaveToCache: stamped + serialized + wrote in {sw.ElapsedMilliseconds}ms ({bytes.Length / 1024} KB, {totalNodeCount} nodes across {nodeCountsByMod.Count} mods)");
                _pipelineSw.Stop();
            }
            catch (Exception ex)
            {
                Log.Error("SaveToCache threw — cache not saved (game continues normally)", ex);
            }
        }

        /// <summary>
        /// Reflection fields for setting readonly members on LoadableXmlAsset.
        /// Cached once for performance. Used only on Stage G cache-hit path.
        /// </summary>
        private static readonly FieldInfo? _assetModField =
            typeof(LoadableXmlAsset).GetField("mod", BindingFlags.Public | BindingFlags.Instance);
        private static readonly FieldInfo? _assetNameField =
            typeof(LoadableXmlAsset).GetField("name", BindingFlags.Public | BindingFlags.Instance);

        /// <summary>
        /// Creates a minimal LoadableXmlAsset with only the mod and name fields
        /// set. Uses FormatterServices.GetUninitializedObject to bypass the
        /// constructor (which does file I/O), then sets readonly fields via
        /// reflection. ParseAndProcessXML only reads asset.mod and asset.name.
        /// </summary>
        private static LoadableXmlAsset? CreateSyntheticAsset(ModContentPack mod)
        {
            try
            {
                var asset = (LoadableXmlAsset)FormatterServices.GetUninitializedObject(typeof(LoadableXmlAsset));
                _assetModField?.SetValue(asset, mod);
                _assetNameField?.SetValue(asset, "cached");
                return asset;
            }
            catch (Exception ex)
            {
                Log.Error($"CreateSyntheticAsset failed for {mod.PackageId}", ex);
                return null;
            }
        }

        /// <summary>
        /// Counts top-level element nodes per mod packageId from a doc that has
        /// already been stamped with data-defloadcache-mod attributes.
        /// </summary>
        private static Dictionary<string, int> CountNodesByMod(XmlDocument doc)
        {
            var counts = new Dictionary<string, int>();
            if (doc?.DocumentElement == null) return counts;

            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (!(node is XmlElement element)) continue;

                string packageId = element.GetAttribute(ModAttributionTagger.AttributeName);
                if (string.IsNullOrEmpty(packageId))
                    continue; // Skip unattributed nodes — RebuildAssetLookup also skips them

                if (counts.ContainsKey(packageId))
                    counts[packageId]++;
                else
                    counts[packageId] = 1;
            }

            return counts;
        }
    }
}
