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
    /// Static entry points called by LoadPipelineHooks' Harmony prefixes.
    ///
    /// SaveToCache (miss) stamps mod attribution, serializes the merged doc,
    /// and writes the cache to disk. TryLoadCached (hit) replaces the working
    /// XmlDocument with the cached version and rebuilds assetlookup from the
    /// embedded attribution attributes; patching and saving are then skipped.
    /// </summary>
    public static class CacheHook
    {
        private static string? _currentFingerprint;
        internal static bool CacheHitOccurred;
        internal static bool LastRunWasMiss;
        internal static bool CheckpointUsed;
        internal static int CheckpointModIndex = -1;
        internal static int CheckpointModsReplayed;
        internal static int CheckpointTotalMods;

        internal static string? CurrentFingerprint => _currentFingerprint;

        /// <summary>
        /// Resets per-load state. A process can run the load pipeline more than
        /// once (RimWorld's reset-and-retry after a load exception), and state
        /// from the previous attempt must not leak into the next one.
        /// </summary>
        internal static void BeginLoad()
        {
            _currentFingerprint = null;
            CacheHitOccurred = false;
            LastRunWasMiss = false;
            CheckpointUsed = false;
            CheckpointModIndex = -1;
            CheckpointModsReplayed = 0;
            CheckpointTotalMods = 0;
            CacheValidator.LastValidationPassed = null;
            CacheValidator.LastExpectedTotal = 0;
            CacheValidator.LastActualTotal = 0;
        }

        /// <summary>
        /// Pipeline-wide stopwatch. Starts when HookFired enters (top of
        /// ApplyPatches), reports cumulative elapsed at each subsequent stage.
        /// Gives wall-clock timing for the entire def-loading pipeline.
        /// </summary>
        private static readonly Stopwatch _pipelineSw = new Stopwatch();

        /// <summary>
        /// Prefix on LoadModXML (Stage G). Computes
        /// the fingerprint early and checks if a cache file exists. If yes,
        /// returns true; the prefix then skips LoadModXML's body and
        /// returns an empty List, so LoadModXML + CombineIntoUnifiedXML are
        /// effectively no-ops. The cached doc is loaded later in TryLoadCached.
        ///
        /// Called from LoadPipelineHooks.
        /// </summary>
        /// <summary>
        /// Complete replacement for the vanilla ApplyPatches method body.
        /// The ApplyPatches prefix calls this and skips the original. All logic lives here in managed C# instead
        /// of fragile IL injection.
        ///
        /// On cache hit: loads cached data, skips everything.
        /// On cache miss: iterates mods individually (not SelectMany),
        /// applies each mod's patches with the same try/catch as vanilla,
        /// then saves the cache. The per-mod loop gives us a natural
        /// boundary for future checkpoint support.
        ///
        /// Called from LoadPipelineHooks.
        /// </summary>
        public static void ApplyPatchesReplacement(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            try
            {
                _pipelineSw.Restart();

                // Compute fingerprint if not already done by ShouldSkipLoadModXML
                if (_currentFingerprint == null)
                {
                    var fpSw = Stopwatch.StartNew();
                    _currentFingerprint = ModlistFingerprint.Compute();
                    fpSw.Stop();
                    Log.Message($"Fingerprint computed in {fpSw.ElapsedMilliseconds}ms: {_currentFingerprint}");
                }

                // Try loading from cache
                if (TryLoadCached(xmlDoc, assetlookup))
                    return;

                // Cache miss: apply patches per-mod
                var mods = LoadedModManager.RunningModsListForReading;
                int startFrom = 0;

                // Experimental: try to resume from a checkpoint
                List<(string packageId, string hash)>? perModHashes = null;
                if (DefLoadCacheSettings.ExperimentalEnabled
                    && DefLoadCacheMod.Settings != null
                    && DefLoadCacheMod.Settings.checkpointEnabled)
                {
                    perModHashes = ModlistFingerprint.ComputePerModHashes();
                    int checkpointIndex = CheckpointStorage.FindLatestValidCheckpoint(perModHashes);
                    if (checkpointIndex >= 0)
                    {
                        if (CheckpointStorage.LoadCheckpoint(xmlDoc, checkpointIndex))
                        {
                            // Rebuild assetlookup from the checkpoint's embedded
                            // attribution stamps, same way TryLoadCached does it
                            var packageIdToAsset = new Dictionary<string, LoadableXmlAsset>();
                            foreach (var mod in mods)
                            {
                                if (mod?.PackageId == null) continue;
                                var asset = CreateSyntheticAsset(mod);
                                if (asset != null && !packageIdToAsset.ContainsKey(mod.PackageId))
                                    packageIdToAsset[mod.PackageId] = asset;
                            }
                            assetlookup.Clear();
                            ModAttributionTagger.RebuildAssetLookup(xmlDoc, assetlookup, packageIdToAsset, out _);

                            startFrom = checkpointIndex + 1;
                            CheckpointUsed = true;
                            CheckpointModIndex = checkpointIndex;
                            CheckpointModsReplayed = mods.Count - startFrom;
                            CheckpointTotalMods = mods.Count;
                            Log.Message($"Loaded checkpoint at mod index {checkpointIndex} ({mods[checkpointIndex].PackageId}), replaying from index {startFrom} ({CheckpointModsReplayed} mods to replay)");
                        }
                    }

                    if (startFrom == 0)
                    {
                        // No valid checkpoint. This happens when mods were removed
                        // or reordered (checkpoints carry defs from all mods present
                        // when built, so they can't be used for a smaller/different
                        // modlist). Clear old checkpoints and do a full rebuild.
                        CheckpointStorage.ClearAll();
                        Log.Message("No valid checkpoint found (mod removed or reordered), running full rebuild");
                    }
                }

                // Apply patches from startFrom onward
                for (int i = startFrom; i < mods.Count; i++)
                {
                    var mod = mods[i];
                    foreach (var patch in mod.Patches)
                    {
                        try
                        {
                            patch.Apply(xmlDoc);
                        }
                        catch (System.Exception ex)
                        {
                            Verse.Log.Error("Error in patch.Apply(): " + ex);
                        }
                    }

                    // Experimental: save checkpoints at intervals
                    if (DefLoadCacheSettings.ExperimentalEnabled
                        && DefLoadCacheMod.Settings != null
                        && DefLoadCacheMod.Settings.checkpointEnabled
                        && perModHashes != null)
                    {
                        // Checkpoint after Core+DLCs (first ~4 mods), then every 50 mods
                        bool isCoreDlcBoundary = i < 10 && i + 1 < mods.Count
                            && !mods[i + 1].PackageId.StartsWith("ludeon.");
                        bool isInterval = (i + 1) % 50 == 0;

                        if (isCoreDlcBoundary || isInterval)
                        {
                            // Stamp attribution into checkpoint so we can rebuild
                            // the assetlookup when loading it later
                            ModAttributionTagger.StampAttributions(xmlDoc, assetlookup);
                            CheckpointStorage.SaveCheckpoint(xmlDoc, i, perModHashes);
                            ModAttributionTagger.UnstampAttributions(xmlDoc);
                        }
                    }
                }

                SaveToCache(xmlDoc, assetlookup);
            }
            catch (System.Exception ex)
            {
                Verse.Log.Error("[DefLoadCache] ApplyPatchesReplacement threw, game may not load correctly: " + ex);
            }
        }

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
                    Log.Message("skipNextLaunch was set, running full uncached load this launch");
                    return false;
                }

                if (settings != null && (!settings.cacheEnabled || !settings.skipModFileLoading))
                {
                    return false;
                }

                var sw = Stopwatch.StartNew();
                _currentFingerprint = ModlistFingerprint.Compute();
                sw.Stop();

                Log.Message($"Fingerprint computed in {sw.ElapsedMilliseconds}ms: {_currentFingerprint}");

                if (CacheStorage.Exists(_currentFingerprint))
                {
                    Log.Message("Cache found, skipping mod file loading");
                    return true;
                }

                Log.Message("No cache found, loading mod files normally");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("ShouldSkipLoadModXML threw, falling back to normal LoadModXML", ex);
                _currentFingerprint = null;
                return false;
            }
        }

        /// <summary>
        /// Called by ApplyPatchesReplacement. Returns true if
        /// a valid cache file exists for the current fingerprint, in which
        /// case patching and SaveToCache are skipped. Returns false on miss.
        ///
        /// On hit: mutates xmlDoc in place (RemoveAll + ImportNode from cached
        /// doc) and rebuilds assetlookup from the embedded data-defloadcache-mod
        /// attributes using real LoadableXmlAsset instances from the original
        /// assetlookup (LoadableXmlAsset's fields are readonly so we can't
        /// construct synthetic instances).
        ///
        /// Called from LoadPipelineHooks.
        /// </summary>
        public static bool TryLoadCached(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            try
            {
                var settings = DefLoadCacheMod.Settings;
                if (settings != null && (!settings.cacheEnabled || !settings.skipPatchApplication))
                {
                    return false;
                }
                if (settings != null && settings.skipNextLaunch)
                {
                    settings.skipNextLaunch = false;
                    try { LoadedModManager.GetMod<DefLoadCacheMod>()?.WriteSettings(); } catch { }
                    Log.Message("skipNextLaunch was set, running normal ApplyPatches");
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
                    Log.Message("Cache MISS, applying patches normally");
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
                    Log.Message($"Built {packageIdToAsset.Count} mod asset mappings for cache hit");
                }

                // Validate the cache file is readable before loading into the
                // caller's doc. Read the entire stream once to verify the GZip
                // decompresses and the binary XML parses without error. If this
                // fails, the caller's xmlDoc is untouched and we fall back.
                // This costs one extra file read (~3-7MB, under 100ms) but avoids
                // both the crash-on-corrupt scenario and the 2x memory spike from
                // loading into a temp doc + deep copying.
                using (var validationStream = CacheStorage.OpenReadStream(_currentFingerprint))
                {
                    if (validationStream == null)
                    {
                        Log.Warning("TryLoadCached: cache file vanished between Exists and validation read");
                        return false;
                    }

                    if (!CacheFormat.TryValidate(validationStream))
                    {
                        Log.Error("TryLoadCached: cache file failed validation (corrupt or wrong format), deleting");
                        try { System.IO.File.Delete(CacheStorage.PathForFingerprint(_currentFingerprint)); } catch { }
                        try { System.IO.File.Delete(CacheStorage.MetaPathForFingerprint(_currentFingerprint)); } catch { }
                        return false;
                    }
                }

                // Validation passed. Load directly into the caller's doc.
                using (var stream = CacheStorage.OpenReadStream(_currentFingerprint))
                {
                    if (stream == null)
                    {
                        Log.Warning("TryLoadCached: cache file vanished between validation and load");
                        return false;
                    }
                    CacheFormat.LoadInto(xmlDoc, stream);
                }

                assetlookup.Clear();
                int rebuilt = ModAttributionTagger.RebuildAssetLookup(xmlDoc, assetlookup, packageIdToAsset, out var actualCountsByMod);

                // Validate node counts against baseline in meta.json
                CacheValidator.Validate(_currentFingerprint!, actualCountsByMod);

                MarkPatchModsAsLoaded();

                sw.Stop();
                Log.Message($"Cache HIT, loaded {rebuilt} defs from cache in {sw.ElapsedMilliseconds}ms");

                CacheHitOccurred = true;
                return true;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Log.Error("TryLoadCached threw, falling through to normal ApplyPatches", ex);
                return false;
            }
        }

        /// <summary>
        /// Prefix on ErrorCheckPatches. On cache-hit
        /// runs, patch config validation is wasted work since ApplyPatches will
        /// be skipped entirely. Saves ~7 seconds on large modlists.
        ///
        /// Based on work by CriDos (https://github.com/CriDos/rimworld-defload-cache).
        ///
        /// Called from LoadPipelineHooks.
        /// </summary>
        public static bool ShouldSkipErrorCheckPatches()
        {
            try
            {
                var settings = DefLoadCacheMod.Settings;
                if (settings != null && (!settings.cacheEnabled || !settings.skipPatchApplication))
                    return false;

                if (settings != null && settings.skipNextLaunch)
                    return false;

                // If fingerprint hasn't been computed yet (Stage G disabled),
                // compute it now to check if a cache exists.
                if (_currentFingerprint == null)
                {
                    _currentFingerprint = ModlistFingerprint.Compute();
                }

                if (_currentFingerprint != null && CacheStorage.Exists(_currentFingerprint))
                {
                    Log.Message("ErrorCheckPatches skipped, cache hit makes patch validation unnecessary");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Error("ShouldSkipErrorCheckPatches threw, falling back to normal ErrorCheckPatches", ex);
                return false;
            }
        }

        /// <summary>
        /// Prefix on ClearCachedPatches. Returns true on cache-hit runs so the
        /// prefix skips the entire method body,
        /// preventing thousands of spurious "patch operation failed" log entries
        /// from patches that were never executed (because ApplyPatches was skipped).
        ///
        /// Called from LoadPipelineHooks.
        /// </summary>
        public static bool ShouldSkipClearPatches()
        {
            try
            {
                if (CacheHitOccurred)
                {
                    _pipelineSw.Stop();
                    Log.Message($"Loading complete, total pipeline time: {_pipelineSw.ElapsedMilliseconds}ms");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("ShouldSkipClearPatches threw, falling back to normal ClearCachedPatches", ex);
                return false;
            }
        }

        /// <summary>
        /// Called at the end of ApplyPatchesReplacement on a miss.
        /// On cache-miss runs, this stamps attribution, serializes, and writes
        /// the cache. Not invoked on cache-hit runs.
        ///
        /// The "cache file already exists, skipping save" guard IS still
        /// present as belt-and-suspenders for the edge case where TryLoadCached
        /// returned false but the cache file was created between the check and
        /// the write.
        ///
        /// Called from LoadPipelineHooks.
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

                // Build meta.json first (need sizeBytes after write)
                // sizeBytes will be 0 initially, updated after streaming write
                var metaSb = new System.Text.StringBuilder();
                metaSb.Append("{");
                metaSb.Append($"\"timestamp\":\"{DateTime.UtcNow:o}\",");
                metaSb.Append($"\"modCount\":{LoadedModManager.RunningModsListForReading.Count},");
                metaSb.Append($"\"rimworldVersion\":\"{RimWorld.VersionControl.CurrentVersionString}\",");
                metaSb.Append($"\"cacheFormatVersion\":{ModlistFingerprint.CacheFormatVersion},");
                metaSb.Append($"\"totalNodeCount\":{totalNodeCount},");
                metaSb.Append("\"nodeCountsByMod\":{");
                bool first = true;
                foreach (var kvp in nodeCountsByMod)
                {
                    if (!first) metaSb.Append(',');
                    metaSb.Append($"\"{kvp.Key.Replace("\"", "\\\"")}\":{kvp.Value}");
                    first = false;
                }
                metaSb.Append("},");
                metaSb.Append("\"profileName\":null,");
                metaSb.Append("\"modList\":[");
                var mods = LoadedModManager.RunningModsListForReading;
                for (int i = 0; i < mods.Count; i++)
                {
                    if (i > 0) metaSb.Append(',');
                    metaSb.Append($"\"{mods[i].PackageId?.Replace("\"", "\\\"") ?? "<null>"}\"");
                }
                metaSb.Append("]");

                // Experimental: compute and store per-mod hashes for future
                // checkpoint-based incremental rebuild support
                if (DefLoadCacheSettings.ExperimentalEnabled)
                {
                    var perModHashes = ModlistFingerprint.ComputePerModHashes();
                    metaSb.Append(",\"perModHashes\":{");
                    bool firstHash = true;
                    foreach (var (packageId, hash) in perModHashes)
                    {
                        if (!firstHash) metaSb.Append(',');
                        metaSb.Append($"\"{packageId.Replace("\"", "\\\"")}\":\"{hash}\"");
                        firstHash = false;
                    }
                    metaSb.Append("}");
                    Log.Message($"Per-mod hashes computed for {perModHashes.Count} mods");
                }

                metaSb.Append("}");

                // Stream directly to disk. No byte[] allocation, keeping
                // memory flat even for large modlists (50-100MB uncompressed).
                long sizeBytes = CacheStorage.Write(_currentFingerprint, xmlDoc, metaSb.ToString());
                ModAttributionTagger.UnstampAttributions(xmlDoc);

                sw.Stop();
                LastRunWasMiss = true;
                _pipelineSw.Stop();
                Log.Message($"Cache saved, {totalNodeCount} defs from {nodeCountsByMod.Count} mods ({sizeBytes / 1024} KB) in {sw.ElapsedMilliseconds}ms. Total pipeline time: {_pipelineSw.ElapsedMilliseconds}ms");

                // Run PatchOperation audit on cache miss (gated by const flag)
                PatchOperationAudit.RunAudit();
            }
            catch (Exception ex)
            {
                Log.Error("SaveToCache threw, cache not saved (game continues normally)", ex);
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
#if RW17
                var asset = (LoadableXmlAsset)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LoadableXmlAsset));
#else
                var asset = (LoadableXmlAsset)FormatterServices.GetUninitializedObject(typeof(LoadableXmlAsset));
#endif
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

        private static readonly FieldInfo? _loadedAnyPatchesField =
            typeof(ModContentPack).GetField("loadedAnyPatches", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// On a cache hit nothing reads ModContentPack.Patches, so vanilla's
        /// loadedAnyPatches flag stays false. Patch-only mods then fail
        /// AnyContentLoaded() and RimWorld logs a false "did not load any
        /// content" error. Their patches ARE in the cached doc, so set the flag
        /// for any mod with patch XML in its load folders. Checks file existence
        /// only; parsing the patches would undo the skip.
        /// </summary>
        private static void MarkPatchModsAsLoaded()
        {
            if (_loadedAnyPatchesField == null)
            {
                Log.Warning("loadedAnyPatches field not found, patch-only mods may log 'did not load any content'");
                return;
            }

            int marked = 0;
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                try
                {
                    if (mod?.foldersToLoadDescendingOrder == null) continue;
                    foreach (var folder in mod.foldersToLoadDescendingOrder)
                    {
                        string patchesDir = System.IO.Path.Combine(folder, "Patches");
                        if (System.IO.Directory.Exists(patchesDir)
                            && System.IO.Directory.EnumerateFiles(patchesDir, "*.xml", System.IO.SearchOption.AllDirectories).GetEnumerator().MoveNext())
                        {
                            _loadedAnyPatchesField.SetValue(mod, true);
                            marked++;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"MarkPatchModsAsLoaded: skipped {mod?.PackageId}: {ex.Message}");
                }
            }
            Log.Message($"Marked {marked} mods with patch files as loaded");
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
                    continue; // Skip unattributed nodes, RebuildAssetLookup also skips them

                if (counts.ContainsKey(packageId))
                    counts[packageId]++;
                else
                    counts[packageId] = 1;
            }

            return counts;
        }
    }
}
