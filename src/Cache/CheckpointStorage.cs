using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Manages checkpoint files for incremental rebuild. Each checkpoint is
    /// a snapshot of the XmlDocument at a specific point in the mod iteration,
    /// stored as a gzipped binary XML file with a meta sidecar.
    ///
    /// Storage layout:
    ///   DefLoadCache/checkpoints/checkpoint_{modIndex}.xml.gz
    ///   DefLoadCache/checkpoints/checkpoint_{modIndex}.meta.json
    ///
    /// Only active when ExperimentalEnabled is true.
    /// </summary>
    internal static class CheckpointStorage
    {
        private static string CheckpointRoot =>
            Path.Combine(CacheStorage.CacheRoot, "checkpoints");

        /// <summary>
        /// Saves a checkpoint of the document state after mod at the given index.
        /// Includes per-mod hashes up to this point for validation on reload.
        /// </summary>
        internal static void SaveCheckpoint(XmlDocument doc, int modIndex,
            List<(string packageId, string hash)> perModHashes)
        {
            try
            {
                Directory.CreateDirectory(CheckpointRoot);

                string cachePath = Path.Combine(CheckpointRoot, $"checkpoint_{modIndex}.xml.gz");
                string metaPath = Path.Combine(CheckpointRoot, $"checkpoint_{modIndex}.meta.json");

                CacheFormat.SerializeToFile(doc, cachePath);

                // Build meta with the per-mod hashes up to this index
                var metaSb = new System.Text.StringBuilder();
                metaSb.Append("{");
                metaSb.Append($"\"modIndex\":{modIndex},");
                metaSb.Append($"\"timestamp\":\"{DateTime.UtcNow:o}\",");
                metaSb.Append("\"perModHashes\":{");
                bool first = true;
                for (int i = 0; i <= modIndex && i < perModHashes.Count; i++)
                {
                    var (packageId, hash) = perModHashes[i];
                    if (!first) metaSb.Append(',');
                    metaSb.Append($"\"{packageId.Replace("\"", "\\\"")}\":\"{hash}\"");
                    first = false;
                }
                metaSb.Append("}}");

                File.WriteAllText(metaPath, metaSb.ToString());
                Log.Message($"Checkpoint saved at mod index {modIndex}");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to save checkpoint at index {modIndex}: {ex}");
            }
        }

        /// <summary>
        /// Finds the latest valid checkpoint by comparing stored per-mod hashes
        /// against the current per-mod hashes. Returns the mod index to start
        /// replaying from, or -1 if no valid checkpoint exists.
        /// </summary>
        internal static int FindLatestValidCheckpoint(
            List<(string packageId, string hash)> currentHashes)
        {
            if (!Directory.Exists(CheckpointRoot))
                return -1;

            // Find all checkpoint meta files and sort by mod index descending
            // so we check the latest checkpoints first
            var checkpoints = new List<(int modIndex, string metaPath)>();
            foreach (var file in Directory.GetFiles(CheckpointRoot, "checkpoint_*.meta.json"))
            {
                var match = Regex.Match(Path.GetFileName(file), @"checkpoint_(\d+)\.meta\.json");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int idx))
                {
                    checkpoints.Add((idx, file));
                }
            }

            checkpoints.Sort((a, b) => b.modIndex.CompareTo(a.modIndex));

            foreach (var (modIndex, metaPath) in checkpoints)
            {
                try
                {
                    // The checkpoint is only valid if mod index is within current list
                    if (modIndex >= currentHashes.Count)
                        continue;

                    string meta = File.ReadAllText(metaPath);
                    var storedHashes = ParsePerModHashes(meta);
                    if (storedHashes == null)
                        continue;

                    // Verify every mod hash from 0 to modIndex matches
                    bool valid = true;
                    for (int i = 0; i <= modIndex; i++)
                    {
                        if (i >= currentHashes.Count)
                        {
                            valid = false;
                            break;
                        }

                        string currentKey = currentHashes[i].packageId;
                        string currentHash = currentHashes[i].hash;

                        if (!storedHashes.TryGetValue(currentKey, out string? storedHash)
                            || storedHash != currentHash)
                        {
                            valid = false;
                            break;
                        }
                    }

                    if (valid)
                    {
                        // Also verify the cache file exists
                        string cachePath = Path.Combine(CheckpointRoot,
                            $"checkpoint_{modIndex}.xml.gz");
                        if (File.Exists(cachePath))
                            return modIndex;
                    }
                }
                catch { }
            }

            return -1;
        }

        /// <summary>
        /// Loads a checkpoint's document state into the provided XmlDocument.
        /// Returns true on success.
        /// </summary>
        internal static bool LoadCheckpoint(XmlDocument doc, int modIndex)
        {
            try
            {
                string cachePath = Path.Combine(CheckpointRoot,
                    $"checkpoint_{modIndex}.xml.gz");
                if (!File.Exists(cachePath))
                    return false;

                using (var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: 65536))
                using (var gz = new System.IO.Compression.GZipStream(fs,
                    System.IO.Compression.CompressionMode.Decompress))
                {
                    CacheFormat.LoadInto(doc, gz);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load checkpoint at index {modIndex}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Deletes all checkpoint files. Called when a full rebuild happens
        /// with a new mod list structure.
        /// </summary>
        internal static void ClearAll()
        {
            try
            {
                if (Directory.Exists(CheckpointRoot))
                {
                    foreach (var file in Directory.GetFiles(CheckpointRoot))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        private static Dictionary<string, string>? ParsePerModHashes(string json)
        {
            int keyIdx = json.IndexOf("\"perModHashes\"");
            if (keyIdx < 0) return null;

            int braceStart = json.IndexOf('{', keyIdx + "\"perModHashes\"".Length);
            if (braceStart < 0) return null;

            int depth = 0;
            int braceEnd = -1;
            for (int i = braceStart; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) { braceEnd = i; break; } }
            }
            if (braceEnd < 0) return null;

            string inner = json.Substring(braceStart + 1, braceEnd - braceStart - 1);
            var result = new Dictionary<string, string>();

            var matches = Regex.Matches(inner, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\"");
            foreach (Match m in matches)
            {
                result[m.Groups[1].Value] = m.Groups[2].Value;
            }

            return result.Count > 0 ? result : null;
        }
    }
}
