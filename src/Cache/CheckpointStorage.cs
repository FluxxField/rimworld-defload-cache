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
    /// IMPORTANT: Checkpoints are only valid when the current modlist is a
    /// strict prefix match of the checkpoint's modlist. If any mod was removed
    /// or reordered, ALL checkpoints are invalidated because the checkpoint
    /// document contains defs from all mods that were present when it was built.
    /// Checkpoints can only be used when mods are added after the checkpoint
    /// position or updated in place.
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

                // Build meta with an ORDERED list of (packageId, hash) pairs.
                // Order matters because prefix-match validation compares by position.
                var metaSb = new System.Text.StringBuilder();
                metaSb.Append("{");
                metaSb.Append($"\"modIndex\":{modIndex},");
                metaSb.Append($"\"timestamp\":\"{DateTime.UtcNow:o}\",");
                metaSb.Append("\"modSequence\":[");
                for (int i = 0; i <= modIndex && i < perModHashes.Count; i++)
                {
                    var (packageId, hash) = perModHashes[i];
                    if (i > 0) metaSb.Append(',');
                    metaSb.Append($"{{\"id\":\"{packageId.Replace("\"", "\\\"")}\",\"hash\":\"{hash}\"}}");
                }
                metaSb.Append("]}");

                File.WriteAllText(metaPath, metaSb.ToString());
                Log.Message($"Checkpoint saved at mod index {modIndex}");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to save checkpoint at index {modIndex}: {ex}");
            }
        }

        /// <summary>
        /// Finds the latest valid checkpoint using strict prefix matching.
        /// The checkpoint's mod sequence (packageId + hash at each position)
        /// must be an exact prefix of the current modlist. If any mod was
        /// removed or reordered, the checkpoint is invalid because its
        /// document contains defs from the old modlist configuration.
        ///
        /// Returns the mod index to start replaying from, or -1 if no
        /// valid checkpoint exists.
        /// </summary>
        internal static int FindLatestValidCheckpoint(
            List<(string packageId, string hash)> currentHashes)
        {
            if (!Directory.Exists(CheckpointRoot))
                return -1;

            // Find all checkpoint meta files and sort by mod index descending
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
                    if (modIndex >= currentHashes.Count)
                        continue;

                    string meta = File.ReadAllText(metaPath);
                    var storedSequence = ParseModSequence(meta);
                    if (storedSequence == null || storedSequence.Count != modIndex + 1)
                        continue;

                    // Strict prefix match: every position must have the same
                    // packageId AND hash. This catches removal (different packageId
                    // at same position) and updates (same packageId, different hash).
                    bool valid = true;
                    for (int i = 0; i < storedSequence.Count; i++)
                    {
                        if (i >= currentHashes.Count
                            || !string.Equals(storedSequence[i].packageId, currentHashes[i].packageId, StringComparison.OrdinalIgnoreCase)
                            || storedSequence[i].hash != currentHashes[i].hash)
                        {
                            valid = false;
                            break;
                        }
                    }

                    if (valid)
                    {
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

        /// <summary>
        /// Parses the ordered modSequence array from checkpoint meta.json.
        /// Returns a list of (packageId, hash) in load order.
        /// </summary>
        private static List<(string packageId, string hash)>? ParseModSequence(string json)
        {
            int keyIdx = json.IndexOf("\"modSequence\"");
            if (keyIdx < 0) return null;

            int bracketStart = json.IndexOf('[', keyIdx);
            if (bracketStart < 0) return null;

            int bracketEnd = json.IndexOf(']', bracketStart);
            if (bracketEnd < 0) return null;

            string inner = json.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
            var result = new List<(string, string)>();

            // Match {"id":"...","hash":"..."} objects
            var matches = Regex.Matches(inner, "\"id\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"hash\"\\s*:\\s*\"([^\"]+)\"");
            foreach (Match m in matches)
            {
                result.Add((m.Groups[1].Value, m.Groups[2].Value));
            }

            return result.Count > 0 ? result : null;
        }
    }
}
