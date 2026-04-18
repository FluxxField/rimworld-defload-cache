using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Computes a stable SHA256 hash of the active modlist's structural state.
    /// Inputs: RimWorld version + per-mod package/version + per-file metadata
    /// (relative path, byte length, mtime) for every load-relevant file + cache
    /// format version. Ordered by mod load order and file relative path.
    ///
    /// Per-mod fragments are collected in parallel for speed on large modlists
    /// (500+). The final hash is computed sequentially in load order using
    /// incremental TransformBlock to avoid building a giant string in memory.
    ///
    /// Per-file fingerprinting approach based on work by CriDos
    /// (https://github.com/CriDos/rimworld-defload-cache).
    /// </summary>
    internal static class ModlistFingerprint
    {
        /// <summary>
        /// Bump this when the cache format changes. All caches with a different
        /// version are invalidated.
        /// </summary>
        public const int CacheFormatVersion = 5;

        internal static string Compute()
        {
            var mods = LoadedModManager.RunningModsListForReading;
            if (mods == null)
            {
                using (var shaNull = SHA256.Create())
                {
                    AppendHashText(shaNull, "rimworld=" + VersionControl.CurrentVersionString + '\n');
                    AppendHashText(shaNull, "cacheformat=" + CacheFormatVersion + '\n');
                    AppendHashText(shaNull, "modcount=<null>\n");
                    return FinalizeHashToHex(shaNull);
                }
            }

            // Collect per-mod fingerprint fragments in parallel. Each mod's
            // fragment is a self-contained string built independently so there
            // are no shared-state races. Results are indexed by load order
            // position so they can be fed into the final hash deterministically.
            var fragments = new string[mods.Count];

            Parallel.For(0, mods.Count, i =>
            {
                fragments[i] = BuildModFragment(mods[i]);
            });

            // Feed fragments into SHA256 incrementally in load order.
            // No giant concatenated string needed.
            using (var sha = SHA256.Create())
            {
                AppendHashText(sha, "rimworld=" + VersionControl.CurrentVersionString + '\n');
                AppendHashText(sha, "cacheformat=" + CacheFormatVersion + '\n');
                AppendHashText(sha, "modcount=" + mods.Count + '\n');

                foreach (var fragment in fragments)
                {
                    AppendHashText(sha, fragment);
                }

                // Experimental: include mod settings config files in fingerprint
                // so that changing a mod setting that affects XML patching
                // (e.g., VEF toggles) invalidates the cache automatically.
                // TODO: mod settings fingerprinting disabled for now.
                // RimWorld writes all mod config files on every exit, which
                // updates their mtime and invalidates the cache every launch.
                // Need to hash file content instead of mtime to make this work.
                // if (DefLoadCacheSettings.ExperimentalEnabled)
                // {
                //     AppendHashText(sha, AppendConfigFolderStats());
                // }

                return FinalizeHashToHex(sha);
            }
        }

        /// <summary>
        /// Computes individual SHA256 hashes for each mod's fingerprint fragment.
        /// Returns a list of (packageId, hash) pairs in load order. Used by the
        /// checkpoint system to find where the mod list diverges from the cache.
        /// Only called when the experimental perModHashing flag is enabled.
        /// </summary>
        internal static List<(string packageId, string hash)> ComputePerModHashes()
        {
            var result = new List<(string, string)>();
            var mods = LoadedModManager.RunningModsListForReading;
            if (mods == null) return result;

            var fragments = new string[mods.Count];
            Parallel.For(0, mods.Count, i =>
            {
                fragments[i] = BuildModFragment(mods[i]);
            });

            for (int i = 0; i < mods.Count; i++)
            {
                using (var sha = SHA256.Create())
                {
                    AppendHashText(sha, fragments[i]);
                    string hash = FinalizeHashToHex(sha);
                    result.Add((mods[i].PackageId ?? "<no-id>", hash));
                }
            }

            return result;
        }

        private static string BuildModFragment(ModContentPack mod) =>
            BuildModFragmentFromDisk(
                mod.PackageId ?? "<no-id>",
                mod.RootDir,
                (IList<string>)(mod.foldersToLoadDescendingOrder ?? new List<string>()));

        /// <summary>
        /// Pure function: takes mod identity primitives and a list of load folders,
        /// walks disk, returns a deterministic fingerprint fragment string. No
        /// RimWorld types — testable in isolation.
        /// </summary>
        internal static string BuildModFragmentFromDisk(
            string packageId,
            string modRootDir,
            IList<string> loadFolders)
        {
            var sb = new StringBuilder();

            sb.Append("mod=").Append(packageId).Append('\n');
            AppendPerFileStats(sb, "about", Path.Combine(modRootDir, "About"), "About.xml");

            // Walk the actual load folders (e.g. "1.6/", "Common/") that RimWorld
            // populates from LoadFolders.xml — not hardcoded paths — so version-
            // scoped layouts fingerprint correctly.
            foreach (var folder in loadFolders)
            {
                string folderLabel = RelativeLabel(modRootDir, folder);
                AppendPerFileStats(sb, folderLabel + "/defs",       Path.Combine(folder, "Defs"),       "*.xml");
                AppendPerFileStats(sb, folderLabel + "/patches",    Path.Combine(folder, "Patches"),    "*.xml");
                AppendPerFileStats(sb, folderLabel + "/assemblies", Path.Combine(folder, "Assemblies"), "*.dll");
            }

            // Root-level Assemblies (legacy fallback for mods without version folders).
            // Modern mods version-scope DLLs into <version>/Assemblies/, which is
            // already walked above per load folder. DLL changes can introduce new
            // PatchOperation subclasses or runtime-generated defs, so they must
            // affect the fingerprint.
            AppendPerFileStats(sb, "assemblies", Path.Combine(modRootDir, "Assemblies"), "*.dll");

            return sb.ToString();
        }

        /// <summary>
        /// Produces a human-readable label for a path relative to a root
        /// directory (e.g. "1.6" or "Common"). Falls back to the full path
        /// if it isn't under the root.
        /// </summary>
        private static string RelativeLabel(string rootDir, string path)
        {
            try
            {
                if (path.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = path.Substring(rootDir.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return string.IsNullOrEmpty(rel) ? "root" : rel;
                }
            }
            catch { }
            return path;
        }

        /// <summary>
        /// Appends per-file metadata (relative path, size, sha256-of-content) for
        /// every file matching the search pattern. Sorted by relative path for
        /// determinism. Content hashing replaces mtime so the fragment is invariant
        /// to mtime-only changes (e.g., Steam re-downloads) and sensitive to true
        /// content changes regardless of whether mtime updated.
        /// </summary>
        private static void AppendPerFileStats(StringBuilder sb, string label, string folderPath, string searchPattern)
        {
            if (!Directory.Exists(folderPath))
            {
                sb.Append(label).Append("=<none>\n");
                return;
            }

            var files = EnumerateSortedFiles(folderPath, searchPattern);
            if (files.Count == 0)
            {
                sb.Append(label).Append("=<none>\n");
                return;
            }

            foreach (var fi in files)
            {
                string relativePath = RelativeLabel(folderPath, fi.FullName);
                try
                {
                    string sha = ComputeFileSha256(fi.FullName);
                    sb.Append(label).Append(':').Append(relativePath)
                      .Append(",bytes:").Append(fi.Length)
                      .Append(",sha256:").Append(sha)
                      .Append('\n');
                }
                catch
                {
                    sb.Append(label).Append(':').Append(relativePath).Append("=<error>\n");
                }
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                return BytesToHex(hash);
            }
        }

        private static List<FileInfo> EnumerateSortedFiles(string folderPath, string searchPattern)
        {
            try
            {
                var files = new List<FileInfo>();
                foreach (var fi in new DirectoryInfo(folderPath).EnumerateFiles(searchPattern, SearchOption.AllDirectories))
                {
                    files.Add(fi);
                }
                files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName));
                return files;
            }
            catch
            {
                return new List<FileInfo>();
            }
        }

        /// <summary>
        /// Builds a fingerprint fragment for the Config/ folder. Includes the
        /// mtime of every Mod_*.xml file so that any settings change invalidates
        /// the cache. Returns the fragment as a string to feed into the hash.
        /// </summary>
        private static string AppendConfigFolderStats()
        {
            try
            {
                string configDir = Path.Combine(GenFilePaths.SaveDataFolderPath, "Config");
                var sb = new StringBuilder();
                sb.Append("config=");

                if (!Directory.Exists(configDir))
                {
                    sb.Append("<none>\n");
                    return sb.ToString();
                }

                // Only include Mod_*.xml files, not KeyPrefs, ModsConfig, etc.
                var files = new List<FileInfo>();
                foreach (var fi in new DirectoryInfo(configDir).EnumerateFiles("Mod_*.xml"))
                {
                    files.Add(fi);
                }
                files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

                foreach (var fi in files)
                {
                    try
                    {
                        sb.Append(fi.Name).Append(',')
                          .Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                    }
                    catch { }
                }

                return sb.ToString();
            }
            catch
            {
                return "config=<error>\n";
            }
        }

        private static void AppendHashText(HashAlgorithm hash, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length == 0) return;
            hash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
        }

        private static string FinalizeHashToHex(HashAlgorithm hash)
        {
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BytesToHex(hash.Hash);
        }

        private static string BytesToHex(byte[] bytes)
        {
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }
    }
}
