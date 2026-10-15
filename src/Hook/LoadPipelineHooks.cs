using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// RimWorld 1.7 replacement for Prepatcher/IlInjector. Installs Harmony
    /// prefixes on the four LoadedModManager pipeline methods, each calling the
    /// same CacheHook entry point the 1.6 IL injection calls, so cache behavior
    /// is identical across versions.
    ///
    /// Timing: LoadAllActiveMods runs CreateModClasses (which constructs
    /// DefLoadCacheMod, which calls Install) before LoadModXML, ErrorCheckPatches,
    /// ApplyPatches and ClearCachedPatches, so all four are patched before they
    /// first execute.
    ///
    /// Mirrors IlInjector one-to-one:
    ///   LoadModXML         → skip, return empty list   (ShouldSkipLoadModXML)
    ///   ErrorCheckPatches  → skip                       (ShouldSkipErrorCheckPatches)
    ///   ApplyPatches       → body replaced              (ApplyPatchesReplacement)
    ///   ClearCachedPatches → skip                       (ShouldSkipClearPatches)
    /// </summary>
    internal static class LoadPipelineHooks
    {
        private const string HarmonyId = "fluxxfield.defloadcache";

        internal static void Install()
        {
            try
            {
                var harmony = new Harmony(HarmonyId);
                Patch(harmony, "LoadModXML", nameof(LoadModXMLPrefix));
                Patch(harmony, "ErrorCheckPatches", nameof(ErrorCheckPatchesPrefix));
                Patch(harmony, "ApplyPatches", nameof(ApplyPatchesPrefix));
                Patch(harmony, "ClearCachedPatches", nameof(ClearCachedPatchesPrefix));
            }
            catch (Exception ex)
            {
                // A partial install is safe: every prefix falls back to running
                // the original, and ApplyPatches only skips vanilla when patched.
                Log.Error("LoadPipelineHooks: failed to install Harmony patches, game loads normally", ex);
            }
        }

        private static void Patch(Harmony harmony, string target, string prefix)
        {
            var original = AccessTools.Method(typeof(LoadedModManager), target);
            if (original == null)
            {
                Log.Warning($"LoadPipelineHooks: LoadedModManager.{target} not found, hook not installed");
                return;
            }
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(LoadPipelineHooks), prefix));
            Log.Message($"Harmony: installed {prefix} on LoadedModManager.{target}");
        }

        private static bool LoadModXMLPrefix(ref List<LoadableXmlAsset> __result)
        {
            if (!CacheHook.ShouldSkipLoadModXML()) return true;
            __result = new List<LoadableXmlAsset>();
            return false;
        }

        private static bool ErrorCheckPatchesPrefix() => !CacheHook.ShouldSkipErrorCheckPatches();

        private static bool ApplyPatchesPrefix(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            CacheHook.ApplyPatchesReplacement(xmlDoc, assetlookup);
            return false;
        }

        private static bool ClearCachedPatchesPrefix() => !CacheHook.ShouldSkipClearPatches();
    }
}
