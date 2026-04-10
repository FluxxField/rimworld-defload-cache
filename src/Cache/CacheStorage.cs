using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Disk I/O for cache files. Atomic writes via temp + rename (File.Replace
    /// when destination exists, File.Move otherwise). Pruning keeps the 3
    /// most-recent .xml.gz files.
    /// </summary>
    internal static class CacheStorage
    {
        public static string CacheRoot
        {
            get
            {
                // GenFilePaths.SaveDataFolderPath returns RimWorld's user data folder:
                // "%LOCALAPPDATALOW%\Ludeon Studios\RimWorld by Ludeon Studios" on Windows
                return Path.Combine(GenFilePaths.SaveDataFolderPath, "DefLoadCache");
            }
        }

        public static string PathForFingerprint(string fingerprint)
        {
            return Path.Combine(CacheRoot, fingerprint + ".xml.gz");
        }

        public static string MetaPathForFingerprint(string fingerprint)
        {
            return Path.Combine(CacheRoot, fingerprint + ".meta.json");
        }

        /// <summary>
        /// Reads the meta.json sidecar for a given fingerprint. Returns null
        /// if the file doesn't exist or can't be read.
        /// </summary>
        public static string? ReadMeta(string fingerprint)
        {
            string p = MetaPathForFingerprint(fingerprint);
            if (!File.Exists(p)) return null;
            try
            {
                return File.ReadAllText(p);
            }
            catch (Exception ex)
            {
                Log.Error($"failed to read meta {p}", ex);
                return null;
            }
        }

        public static bool TryRead(string fingerprint, out byte[]? bytes)
        {
            bytes = null;
            string p = PathForFingerprint(fingerprint);
            if (!File.Exists(p)) return false;
            try
            {
                bytes = File.ReadAllBytes(p);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"failed to read cache {p}", ex);
                try { File.Delete(p); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Returns true if a cache file exists for this fingerprint.
        /// </summary>
        public static bool Exists(string fingerprint)
        {
            return File.Exists(PathForFingerprint(fingerprint));
        }

        /// <summary>
        /// Opens the cache file as a streaming GZip-decompressing reader.
        /// Caller is responsible for disposing the returned stream.
        /// Returns null if the file doesn't exist or can't be opened.
        /// </summary>
        public static Stream? OpenReadStream(string fingerprint)
        {
            string p = PathForFingerprint(fingerprint);
            if (!File.Exists(p)) return null;
            try
            {
                var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 65536);
                return new System.IO.Compression.GZipStream(fs,
                    System.IO.Compression.CompressionMode.Decompress);
            }
            catch (Exception ex)
            {
                Log.Error($"failed to open cache stream {p}", ex);
                try { File.Delete(p); } catch { }
                return null;
            }
        }

        public static void Write(string fingerprint, byte[] bytes, string metaJson)
        {
            try
            {
                Directory.CreateDirectory(CacheRoot);
            }
            catch (Exception ex)
            {
                Log.Error($"could not create cache root {CacheRoot}", ex);
                return;
            }

            string finalPath = PathForFingerprint(fingerprint);
            string tmpPath = finalPath + ".tmp";

            try
            {
                File.WriteAllBytes(tmpPath, bytes);

                // .NET Framework File.Move throws if destination exists; use File.Replace in that case.
                if (File.Exists(finalPath))
                {
                    File.Replace(tmpPath, finalPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmpPath, finalPath);
                }

                File.WriteAllText(MetaPathForFingerprint(fingerprint), metaJson);

                Log.Message($"Cache file written ({bytes.Length / 1024} KB)");

                Prune();
            }
            catch (Exception ex)
            {
                Log.Error($"failed to write cache {finalPath}", ex);
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
        }

        public static void Prune()
        {
            try
            {
                if (!Directory.Exists(CacheRoot)) return;

                // Delete orphaned .tmp files first
                foreach (var orphan in Directory.GetFiles(CacheRoot, "*.tmp"))
                {
                    try { File.Delete(orphan); } catch { }
                }

                // Load all saved .rml profiles from RimWorld's ModLists folder
                var profiles = LoadSavedProfiles();

                // Read all cache meta files and match against profiles
                var cacheFiles = Directory.GetFiles(CacheRoot, "*.xml.gz")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .ToList();

                // Group caches: profile-tagged vs untagged
                var profileCaches = new Dictionary<string, List<FileInfo>>();
                var untaggedCaches = new List<FileInfo>();
                int matched = 0;

                foreach (var fi in cacheFiles)
                {
                    string name = Path.GetFileName(fi.FullName);
                    if (!name.EndsWith(".xml.gz")) continue;

                    string fingerprint = name.Substring(0, name.Length - ".xml.gz".Length);
                    string? meta = ReadMeta(fingerprint);
                    if (meta == null)
                    {
                        untaggedCaches.Add(fi);
                        continue;
                    }

                    // Parse the modList from meta.json
                    var modList = ParseModList(meta);
                    if (modList == null)
                    {
                        // Old cache without modList, treat as untagged
                        untaggedCaches.Add(fi);
                        continue;
                    }

                    // Try to match against a saved profile
                    string? profileName = null;
                    foreach (var kvp in profiles)
                    {
                        if (ModListsMatch(modList, kvp.Value))
                        {
                            profileName = kvp.Key;
                            break;
                        }
                    }

                    if (profileName != null)
                    {
                        // Write profileName back to meta.json
                        WriteProfileName(fingerprint, meta, profileName);
                        matched++;

                        if (!profileCaches.ContainsKey(profileName))
                            profileCaches[profileName] = new List<FileInfo>();
                        profileCaches[profileName].Add(fi);
                    }
                    else
                    {
                        untaggedCaches.Add(fi);
                    }
                }

                // Prune: keep newest per profile, keep one untagged
                int removed = 0;
                foreach (var kvp in profileCaches)
                {
                    foreach (var fi in kvp.Value.Skip(1)) // Already sorted by date desc
                    {
                        DeleteCacheFile(fi);
                        removed++;
                    }
                }

                foreach (var fi in untaggedCaches.Skip(1))
                {
                    DeleteCacheFile(fi);
                    removed++;
                }

                if (matched > 0 || removed > 0)
                {
                    var profileNames = string.Join(", ", profileCaches.Keys);
                    Log.Message($"Prune: matched {matched} caches to profiles ({profileNames}), removed {removed} stale caches");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Prune failed", ex);
            }
        }

        private static void DeleteCacheFile(FileInfo fi)
        {
            try
            {
                File.Delete(fi.FullName);
                string name = Path.GetFileName(fi.FullName);
                if (name.EndsWith(".xml.gz"))
                {
                    string fingerprint = name.Substring(0, name.Length - ".xml.gz".Length);
                    string metaPath = MetaPathForFingerprint(fingerprint);
                    if (File.Exists(metaPath)) File.Delete(metaPath);
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads all .rml profile files from RimWorld's ModLists folder.
        /// Returns a dictionary of profile name to packageId list.
        /// </summary>
        private static Dictionary<string, List<string>> LoadSavedProfiles()
        {
            var profiles = new Dictionary<string, List<string>>();
            try
            {
                string modListsDir = Path.Combine(GenFilePaths.SaveDataFolderPath, "ModLists");
                if (!Directory.Exists(modListsDir)) return profiles;

                foreach (var file in Directory.GetFiles(modListsDir, "*.rml"))
                {
                    try
                    {
                        string profileName = Path.GetFileNameWithoutExtension(file);
                        var doc = new XmlDocument();
                        doc.Load(file);

                        var ids = new List<string>();
                        var nodes = doc.SelectNodes("savedModList/modList/ids/li");
                        if (nodes != null)
                        {
                            foreach (XmlNode node in nodes)
                            {
                                if (!string.IsNullOrEmpty(node.InnerText))
                                    ids.Add(node.InnerText);
                            }
                        }

                        if (ids.Count > 0)
                            profiles[profileName] = ids;
                    }
                    catch { }
                }
            }
            catch { }
            return profiles;
        }

        /// <summary>
        /// Parses the modList array from a meta.json string.
        /// Returns null if the key doesn't exist.
        /// </summary>
        private static List<string>? ParseModList(string json)
        {
            int keyIdx = json.IndexOf("\"modList\"");
            if (keyIdx < 0) return null;

            int bracketStart = json.IndexOf('[', keyIdx);
            if (bracketStart < 0) return null;

            int bracketEnd = json.IndexOf(']', bracketStart);
            if (bracketEnd < 0) return null;

            string inner = json.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
            var result = new List<string>();

            var matches = System.Text.RegularExpressions.Regex.Matches(inner, "\"([^\"]+)\"");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                result.Add(m.Groups[1].Value);
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// Compares two mod lists for exact match (same packageIds, same order).
        /// </summary>
        private static bool ModListsMatch(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Writes the profileName field into an existing meta.json file.
        /// </summary>
        private static void WriteProfileName(string fingerprint, string currentMeta, string profileName)
        {
            try
            {
                string escaped = profileName.Replace("\"", "\\\"");
                string updated;

                int idx = currentMeta.IndexOf("\"profileName\"");
                if (idx < 0) return;

                // Find the colon after the key
                int colonIdx = currentMeta.IndexOf(':', idx + "\"profileName\"".Length);
                if (colonIdx < 0) return;

                // Find the start of the value (skip whitespace)
                int valueStart = colonIdx + 1;
                while (valueStart < currentMeta.Length && currentMeta[valueStart] == ' ') valueStart++;

                // Find the end of the value (null or "string")
                int valueEnd;
                if (currentMeta[valueStart] == 'n') // null
                {
                    valueEnd = valueStart + 4;
                }
                else if (currentMeta[valueStart] == '"')
                {
                    valueEnd = currentMeta.IndexOf('"', valueStart + 1) + 1;
                }
                else return;

                updated = currentMeta.Substring(0, valueStart) + $"\"{escaped}\"" + currentMeta.Substring(valueEnd);
                File.WriteAllText(MetaPathForFingerprint(fingerprint), updated);
            }
            catch { }
        }
    }
}
