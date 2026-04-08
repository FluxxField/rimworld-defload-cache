using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Static entry points called from IL injected by IlInjector.
    /// Stage C: HookFired computes the fingerprint and stashes it. SaveToCache
    /// is a new entry point called as a postfix at the end of ApplyPatches;
    /// it stamps mod attribution, serializes the doc, and writes the cache.
    /// No cache READ yet — that's Stage D.
    /// </summary>
    public static class CacheHook
    {
        // Stashed by HookFired so SaveToCache can reuse without recomputing.
        private static string? _currentFingerprint;

        /// <summary>
        /// Called by injected IL at the top of
        /// Verse.LoadedModManager.ApplyPatches. Computes and stashes the
        /// fingerprint.
        ///
        /// IMPORTANT: IlInjector.InjectApplyPatchesHook resolves this method by
        /// name via <c>nameof(CacheHook.HookFired)</c>. Renaming requires a
        /// matching injector update.
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
        /// Called by injected IL at the END of ApplyPatches (before every ret
        /// instruction). Reads the just-patched merged doc, stamps mod
        /// attribution onto each top-level node, serializes, atomically writes
        /// to disk.
        ///
        /// Receives xmlDoc and assetlookup as the parameters of ApplyPatches
        /// itself (passed via ldarg.0 and ldarg.1 in the injected IL).
        ///
        /// IMPORTANT: IlInjector.InjectApplyPatchesHook resolves this method by
        /// name via <c>nameof(CacheHook.SaveToCache)</c>.
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

                // If a cache file already exists for this fingerprint, this run
                // would have been a cache hit had Stage D been implemented.
                // For now (Stage C, write-only), skip the re-write to avoid
                // clobbering the same data repeatedly.
                string existingPath = CacheStorage.PathForFingerprint(_currentFingerprint);
                if (System.IO.File.Exists(existingPath))
                {
                    Log.Message("SaveToCache: cache file already exists for this fingerprint, skipping save");
                    return;
                }

                var sw = Stopwatch.StartNew();
                ModAttributionTagger.StampAttributions(xmlDoc, assetlookup);
                byte[] bytes = CacheFormat.Serialize(xmlDoc);
                sw.Stop();

                string metaJson = "{"
                    + $"\"timestamp\":\"{DateTime.UtcNow:o}\","
                    + $"\"modCount\":{LoadedModManager.RunningModsListForReading.Count},"
                    + $"\"rimworldVersion\":\"{RimWorld.VersionControl.CurrentVersionString}\","
                    + $"\"cacheFormatVersion\":{ModlistFingerprint.CacheFormatVersion},"
                    + $"\"sizeBytes\":{bytes.Length}"
                    + "}";

                CacheStorage.Write(_currentFingerprint, bytes, metaJson);
                Log.Message($"SaveToCache: serialized + wrote in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Log.Error("SaveToCache threw — cache not saved (game continues normally)", ex);
            }
        }
    }
}
