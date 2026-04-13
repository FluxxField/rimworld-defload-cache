using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Scans all loaded assemblies for PatchOperation subclasses and writes
    /// a report listing vanilla vs custom types and which mods own them.
    /// Gated behind a const flag so it compiles away in production.
    /// </summary>
    internal static class PatchOperationAudit
    {
        /// <summary>
        /// Set to true for development/testing. The compiler optimizes away
        /// all audit code when false. Replace with a settings toggle when
        /// ready to expose to users.
        /// </summary>
        internal const bool AuditEnabled = false;

        internal static void RunAudit()
        {
            if (!AuditEnabled) return;

            try
            {
                var patchOpType = typeof(PatchOperation);
                var vanillaTypes = new List<Type>();
                var customTypes = new List<(Type type, string modName)>();

                // Build a map of assembly name to mod name for attribution
                var assemblyToMod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in LoadedModManager.RunningModsListForReading)
                {
                    if (mod?.assemblies?.loadedAssemblies == null) continue;
                    foreach (var asm in mod.assemblies.loadedAssemblies)
                    {
                        string asmName = asm.GetName().Name;
                        if (!assemblyToMod.ContainsKey(asmName))
                        {
                            assemblyToMod[asmName] = mod.Name ?? mod.PackageId ?? "<unknown>";
                        }
                    }
                }

                // Scan all loaded assemblies for PatchOperation subclasses
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(t => t != null).ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var type in types)
                    {
                        if (type == null || type == patchOpType) continue;
                        if (!patchOpType.IsAssignableFrom(type)) continue;
                        if (type.IsAbstract) continue;

                        if (type.Namespace != null && type.Namespace.StartsWith("Verse"))
                        {
                            vanillaTypes.Add(type);
                        }
                        else
                        {
                            string asmName = type.Assembly.GetName().Name;
                            string modName = assemblyToMod.ContainsKey(asmName)
                                ? assemblyToMod[asmName]
                                : "<unknown mod>";
                            customTypes.Add((type, modName));
                        }
                    }
                }

                vanillaTypes.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
                customTypes.Sort((a, b) => string.Compare(a.type.FullName, b.type.FullName, StringComparison.Ordinal));

                var distinctMods = customTypes.Select(c => c.modName).Distinct().Count();

                // Write the report file
                var sb = new StringBuilder();
                sb.AppendLine("# PatchOperation Audit");
                sb.AppendLine($"# Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
                sb.AppendLine($"# Total types found: {vanillaTypes.Count + customTypes.Count}");
                sb.AppendLine($"# Custom (non-vanilla) types: {customTypes.Count}");
                sb.AppendLine($"# Mods with custom types: {distinctMods}");
                sb.AppendLine();

                sb.AppendLine($"## Vanilla PatchOperations ({vanillaTypes.Count} types)");
                foreach (var type in vanillaTypes)
                {
                    sb.AppendLine($"  {type.FullName}");
                }
                sb.AppendLine();

                sb.AppendLine($"## Custom PatchOperations ({customTypes.Count} types)");
                foreach (var (type, modName) in customTypes)
                {
                    sb.AppendLine($"  {type.FullName}  [mod: {modName}]");
                }

                string path = Path.Combine(CacheStorage.CacheRoot, "patchop-audit.txt");
                Directory.CreateDirectory(CacheStorage.CacheRoot);
                File.WriteAllText(path, sb.ToString());

                Log.Message($"PatchOperation audit: found {customTypes.Count} custom types from {distinctMods} mods (see patchop-audit.txt)");
            }
            catch (Exception ex)
            {
                Log.Error($"PatchOperation audit failed: {ex}");
            }
        }
    }
}
