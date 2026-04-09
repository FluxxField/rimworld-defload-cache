using System;
using System.IO;
using System.Linq;
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
        private static int MaxCachedFilesToKeep =>
            DefLoadCacheMod.Settings?.maxCachedProfiles ?? 10;

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

                Log.Message($"cache written: {finalPath} ({bytes.Length / 1024} KB)");

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

                // Keep the most-recent N .xml.gz files; delete the rest and their meta sidecars
                var files = Directory.GetFiles(CacheRoot, "*.xml.gz")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .ToList();

                foreach (var fi in files.Skip(MaxCachedFilesToKeep))
                {
                    try
                    {
                        File.Delete(fi.FullName);
                        // Derive the meta sidecar path from the fingerprint (the
                        // filename stem before .xml.gz). Path.ChangeExtension only
                        // strips ONE extension, which would leave ".xml.meta.json".
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
            }
            catch (Exception ex)
            {
                Log.Error("prune failed", ex);
            }
        }
    }
}
