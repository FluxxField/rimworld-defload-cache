using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Stage E correctness validation. Dumps a sorted snapshot of the entire
    /// DefDatabase to a text file. Run on both a cache-miss and cache-hit
    /// launch, then diff the two files. If identical, the cache is correct.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class DiagnosticDump
    {
        static DiagnosticDump()
        {
            if (DefLoadCacheMod.Settings == null || !DefLoadCacheMod.Settings.diagnosticDumpEnabled)
                return;

            try
            {
                WriteDump();
            }
            catch (Exception ex)
            {
                Log.Error($"DiagnosticDump failed: {ex}");
            }
        }

        private static void WriteDump()
        {
            string suffix = CacheHook.CacheHitOccurred ? "cache-hit" : "cache-miss";
            string path = Path.Combine(CacheStorage.CacheRoot, $"diagnostic-{suffix}.txt");

            Directory.CreateDirectory(CacheStorage.CacheRoot);

            var lines = new List<string>();

            // Iterate all def types and all defs within each type
            foreach (var defType in GenDefDatabase.AllDefTypesWithDatabases())
            {
                foreach (var def in GenDefDatabase.GetAllDefsInDatabaseForDef(defType))
                {
                    string modId = def.modContentPack?.PackageId ?? "<null>";
                    string modName = def.modContentPack?.Name ?? "<null>";
                    string fileName = def.fileName ?? "<null>";
                    lines.Add($"{defType.Name}\t{def.defName}\t{modId}\t{modName}\t{fileName}\t{def.label ?? "<null>"}");
                }
            }

            lines.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.AppendLine($"# DefLoadCache Diagnostic Dump");
            sb.AppendLine($"# Generated: {DateTime.Now:o}");
            sb.AppendLine($"# Mode: {suffix}");
            sb.AppendLine($"# Fingerprint: {CacheHook.CurrentFingerprint ?? "<null>"}");
            sb.AppendLine($"# Total defs: {lines.Count}");
            sb.AppendLine($"# Format: DefType\\tdefName\\tmodPackageId\\tmodName\\tfileName\\tlabel");
            sb.AppendLine();
            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }

            File.WriteAllText(path, sb.ToString());
            Log.Message($"Stage E diagnostic dump written: {path} ({lines.Count} defs)");
        }
    }
}
