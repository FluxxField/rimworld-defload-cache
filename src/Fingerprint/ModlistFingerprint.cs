using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Computes a stable SHA256 hash of the active modlist's structural state.
    /// Inputs: RimWorld version + per-mod (packageId, version, Defs/ file count + total bytes,
    /// Patches/ file count + total bytes) + cache format version. Sorted by load order.
    /// Hash must recompute in &lt; 5 seconds on a 500-mod load order.
    /// </summary>
    internal static class ModlistFingerprint
    {
        /// <summary>
        /// Bump this when the cache format changes. All caches with a different
        /// version are invalidated.
        /// </summary>
        public const int CacheFormatVersion = 1;

        internal static string Compute()
        {
            var sb = new StringBuilder();
            sb.Append("rimworld=").Append(VersionControl.CurrentVersionString).Append('\n');
            sb.Append("cacheformat=").Append(CacheFormatVersion).Append('\n');

            var mods = LoadedModManager.RunningModsListForReading;
            if (mods == null)
            {
                // Defensive: should never happen in normal RimWorld startup, but
                // return a stable sentinel so HookFired's try/catch doesn't need
                // to handle NRE from .Count below.
                sb.Append("modcount=<null>\n");
                using (var shaNull = SHA256.Create())
                {
                    return BytesToHex(shaNull.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
                }
            }
            sb.Append("modcount=").Append(mods.Count).Append('\n');

            foreach (var mod in mods)
            {
                sb.Append("mod=").Append(mod.PackageId ?? "<no-id>").Append('\n');
                sb.Append("modversion=").Append(GetModVersion(mod)).Append('\n');
                AppendFolderStats(sb, "defs", Path.Combine(mod.RootDir, "Defs"));
                AppendFolderStats(sb, "patches", Path.Combine(mod.RootDir, "Patches"));
            }

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BytesToHex(hash);
            }
        }

        private static string GetModVersion(ModContentPack mod)
        {
            // ModContentPack does not expose modVersion directly; read from About/About.xml
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

        private static void AppendFolderStats(StringBuilder sb, string label, string folderPath)
        {
            int count = 0;
            long totalBytes = 0;
            if (Directory.Exists(folderPath))
            {
                // Use DirectoryInfo.EnumerateFiles so .Length is populated from the
                // same directory-listing syscall as the enumeration. Directory.EnumerateFiles
                // would force a second stat() per file, doubling filesystem round-trips
                // on NTFS-through-WSL.
                foreach (var fi in new DirectoryInfo(folderPath).EnumerateFiles("*.xml", SearchOption.AllDirectories))
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
