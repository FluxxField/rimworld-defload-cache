using System;
using System.Collections.Concurrent;
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
    /// Inputs: RimWorld version + per-mod (packageId, version, load-folder Defs/
    /// Patches/ file counts + total bytes, Assemblies/*.dll counts + bytes) +
    /// cache format version. Ordered by load order.
    ///
    /// Per-mod stats are collected in parallel via Parallel.ForEach for speed on
    /// large modlists (500+). The final hash is computed sequentially in load
    /// order to ensure determinism.
    /// </summary>
    internal static class ModlistFingerprint
    {
        /// <summary>
        /// Bump this when the cache format changes. All caches with a different
        /// version are invalidated.
        /// </summary>
        public const int CacheFormatVersion = 2;

        internal static string Compute()
        {
            var sb = new StringBuilder();
            sb.Append("rimworld=").Append(VersionControl.CurrentVersionString).Append('\n');
            sb.Append("cacheformat=").Append(CacheFormatVersion).Append('\n');

            var mods = LoadedModManager.RunningModsListForReading;
            if (mods == null)
            {
                sb.Append("modcount=<null>\n");
                using (var shaNull = SHA256.Create())
                {
                    return BytesToHex(shaNull.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
                }
            }
            sb.Append("modcount=").Append(mods.Count).Append('\n');

            // Collect per-mod fingerprint fragments in parallel. Each mod's
            // fragment is a self-contained string built independently so there
            // are no shared-state races. Results are indexed by load order
            // position to reassemble deterministically.
            var fragments = new string[mods.Count];

            Parallel.For(0, mods.Count, i =>
            {
                var mod = mods[i];
                var msb = new StringBuilder();

                msb.Append("mod=").Append(mod.PackageId ?? "<no-id>").Append('\n');
                msb.Append("modversion=").Append(GetModVersion(mod)).Append('\n');

                // Walk the actual load folders (e.g. "1.6/", "Common/") instead
                // of hardcoded root-level Defs/Patches. foldersToLoadDescendingOrder
                // is populated from LoadFolders.xml and reflects what RimWorld
                // actually loads.
                var loadFolders = mod.foldersToLoadDescendingOrder;
                if (loadFolders != null)
                {
                    foreach (var folder in loadFolders)
                    {
                        string folderLabel = RelativeLabel(mod.RootDir, folder);
                        AppendFolderStats(msb, folderLabel + "/defs", Path.Combine(folder, "Defs"), "*.xml");
                        AppendFolderStats(msb, folderLabel + "/patches", Path.Combine(folder, "Patches"), "*.xml");
                    }
                }

                // Assemblies — DLL changes can introduce new PatchOperation
                // subclasses or runtime-generated defs. Walk root Assemblies/
                // since that's where RimWorld loads them from regardless of
                // LoadFolders.xml.
                AppendFolderStats(msb, "assemblies", Path.Combine(mod.RootDir, "Assemblies"), "*.dll");

                fragments[i] = msb.ToString();
            });

            // Reassemble in load order (deterministic)
            foreach (var fragment in fragments)
            {
                sb.Append(fragment);
            }

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BytesToHex(hash);
            }
        }

        private static string GetModVersion(ModContentPack mod)
        {
            try
            {
                string aboutPath = Path.Combine(mod.RootDir, "About", "About.xml");
                if (!File.Exists(aboutPath)) return "<no-about>";
                var doc = new System.Xml.XmlDocument();
                doc.Load(aboutPath);
                var node = doc.SelectSingleNode("//ModMetaData/modVersion");
                return node?.InnerText?.Trim() ?? "<no-version>";
            }
            catch
            {
                return "<error>";
            }
        }

        /// <summary>
        /// Produces a human-readable label for a load folder relative to the mod
        /// root (e.g. "1.6" or "Common"). Falls back to the full path if the
        /// folder isn't under the root.
        /// </summary>
        private static string RelativeLabel(string rootDir, string folder)
        {
            try
            {
                if (folder.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = folder.Substring(rootDir.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return string.IsNullOrEmpty(rel) ? "root" : rel;
                }
            }
            catch { }
            return folder;
        }

        private static void AppendFolderStats(StringBuilder sb, string label, string folderPath, string searchPattern)
        {
            int count = 0;
            long totalBytes = 0;
            if (Directory.Exists(folderPath))
            {
                foreach (var fi in new DirectoryInfo(folderPath).EnumerateFiles(searchPattern, SearchOption.AllDirectories))
                {
                    count++;
                    try { totalBytes += fi.Length; }
                    catch { /* unreadable file, skip its size */ }
                }
            }
            sb.Append(label).Append("=count:").Append(count).Append(",bytes:").Append(totalBytes).Append('\n');
        }

        private static string BytesToHex(byte[] bytes)
        {
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }
    }
}
