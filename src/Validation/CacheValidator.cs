using System.Collections.Generic;
using System.Text.RegularExpressions;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Validates a cache-hit load by comparing per-mod XML node counts
    /// against the baseline stored in meta.json during the cache-miss
    /// save. On mismatch: deletes the bad cache, sets skipNextLaunch,
    /// and logs the discrepancy. The next launch is guaranteed clean.
    /// </summary>
    internal static class CacheValidator
    {
        /// <summary>
        /// Result of the last validation run. Read by StatusBlockEmitter.
        /// null = no validation ran (cache miss or no meta available).
        /// </summary>
        internal static bool? LastValidationPassed;
        internal static int LastExpectedTotal;
        internal static int LastActualTotal;

        /// <summary>
        /// Validates the loaded cache document against the baseline node
        /// counts in meta.json. Returns true if valid or if validation
        /// cannot be performed (missing meta, old format). Returns false
        /// on mismatch, caller should still return true from TryLoadCached
        /// (doc is already mutated) but the bad cache is cleaned up.
        /// </summary>
        internal static bool Validate(
            string fingerprint,
            Dictionary<string, int> actualCountsByMod)
        {
            string? metaJson = CacheStorage.ReadMeta(fingerprint);
            if (metaJson == null)
            {
                Log.Message("CacheValidator: no meta.json found, skipping validation");
                LastValidationPassed = null;
                return true;
            }

            // Parse totalNodeCount from meta
            int? expectedTotal = ParseInt(metaJson, "totalNodeCount");
            if (expectedTotal == null)
            {
                // Old format (v3), no node counts stored, skip validation
                Log.Message("CacheValidator: meta.json has no totalNodeCount (old format), skipping validation");
                LastValidationPassed = null;
                return true;
            }

            // Count actual top-level element nodes
            int actualTotal = 0;
            foreach (var kvp in actualCountsByMod)
                actualTotal += kvp.Value;

            LastExpectedTotal = expectedTotal.Value;
            LastActualTotal = actualTotal;

            // Check total first
            if (actualTotal != expectedTotal.Value)
            {
                Log.Error($"CacheValidator: total node count mismatch, expected {expectedTotal.Value}, got {actualTotal}");
                OnValidationFailed(fingerprint);
                return false;
            }

            // Parse per-mod counts from meta and compare
            var expectedCounts = ParseNodeCountsByMod(metaJson);
            if (expectedCounts != null)
            {
                var mismatches = new List<string>();
                foreach (var kvp in expectedCounts)
                {
                    int actualCount;
                    actualCountsByMod.TryGetValue(kvp.Key, out actualCount);
                    if (actualCount != kvp.Value)
                    {
                        mismatches.Add($"  {kvp.Key}: expected {kvp.Value}, got {actualCount}");
                    }
                }

                // Also check for mods in actual that aren't in expected
                foreach (var kvp in actualCountsByMod)
                {
                    if (!expectedCounts.ContainsKey(kvp.Key))
                    {
                        mismatches.Add($"  {kvp.Key}: expected 0 (not in baseline), got {kvp.Value}");
                    }
                }

                if (mismatches.Count > 0)
                {
                    Log.Error("CacheValidator: per-mod node count mismatches:\n" + string.Join("\n", mismatches));
                    OnValidationFailed(fingerprint);
                    return false;
                }
            }

            Log.Message($"CacheValidator: validation PASSED, {actualTotal} nodes match baseline");
            LastValidationPassed = true;
            return true;
        }

        private static void OnValidationFailed(string fingerprint)
        {
            LastValidationPassed = false;

            // Delete the bad cache
            string cachePath = CacheStorage.PathForFingerprint(fingerprint);
            try { System.IO.File.Delete(cachePath); } catch { }
            string metaPath = CacheStorage.MetaPathForFingerprint(fingerprint);
            try { System.IO.File.Delete(metaPath); } catch { }

            // Force next launch to skip cache
            var settings = DefLoadCacheMod.Settings;
            if (settings != null)
            {
                settings.skipNextLaunch = true;
                try
                {
                    LoadedModManager.GetMod<DefLoadCacheMod>()?.WriteSettings();
                }
                catch { }
            }

            Log.Error("Cache validation failed. The bad cache has been deleted. " +
                      "Next launch will load normally without cache. Please restart your game.");
        }

        /// <summary>
        /// Parses an integer value from a JSON string by key name.
        /// Simple regex-based, no JSON library needed.
        /// </summary>
        private static int? ParseInt(string json, string key)
        {
            var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int val))
                return val;
            return null;
        }

        /// <summary>
        /// Parses the nodeCountsByMod object from meta.json.
        /// Returns null if the key doesn't exist.
        /// </summary>
        private static Dictionary<string, int>? ParseNodeCountsByMod(string json)
        {
            // Find "nodeCountsByMod":{...}
            int keyIdx = json.IndexOf("\"nodeCountsByMod\"");
            if (keyIdx < 0) return null;

            int braceStart = json.IndexOf('{', keyIdx + "\"nodeCountsByMod\"".Length);
            if (braceStart < 0) return null;

            // Find matching closing brace
            int depth = 0;
            int braceEnd = -1;
            for (int i = braceStart; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) { braceEnd = i; break; } }
            }
            if (braceEnd < 0) return null;

            string inner = json.Substring(braceStart + 1, braceEnd - braceStart - 1);
            var result = new Dictionary<string, int>();

            // Match "key":value pairs
            var matches = Regex.Matches(inner, "\"([^\"]+)\"\\s*:\\s*(\\d+)");
            foreach (Match m in matches)
            {
                if (int.TryParse(m.Groups[2].Value, out int count))
                {
                    result[m.Groups[1].Value] = count;
                }
            }

            return result;
        }

        /// <summary>
        /// Parses a string value from a JSON string by key name.
        /// Used by StatusBlockEmitter to read the timestamp.
        /// </summary>
        internal static string? ParseString(string json, string key)
        {
            var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
