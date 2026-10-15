using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Installs Harmony prefixes on the four LoadedModManager pipeline methods,
    /// each calling a CacheHook entry point. Used on both 1.6 and 1.7. (Before
    /// 1.3.0 this was done with Prepatcher [FreePatch] IL injection; Prepatcher
    /// scans every mod DLL and crashes on mods with missing soft dependencies.)
    ///
    /// Timing: LoadAllActiveMods runs CreateModClasses (which constructs
    /// DefLoadCacheMod, which calls Install) before LoadModXML, ErrorCheckPatches,
    /// ApplyPatches and ClearCachedPatches, so all four are patched before they
    /// first execute.
    ///
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

        /// <summary>
        /// Harmony patches outlive a mod-list reset: when RimWorld recovers
        /// from a load exception it resets ModsConfig and reloads in the same
        /// process, and DefLoadCache may no longer be in the list. Decided once
        /// per load at LoadModXML (the first hooked step); when false, every
        /// prefix lets vanilla run.
        /// </summary>
        private static bool _activeThisLoad;

        private static bool LoadModXMLPrefix(ref List<LoadableXmlAsset> __result)
        {
            CacheHook.BeginLoad();
            _activeThisLoad = DefLoadCacheMod.OwnContent != null
                && LoadedModManager.RunningModsListForReading.Contains(DefLoadCacheMod.OwnContent);
            if (!_activeThisLoad)
            {
                Log.Message("DefLoadCache is not in the active mod list for this load (mod list was reset), loading normally");
                return true;
            }

            if (!CacheHook.ShouldSkipLoadModXML()) return true;
            __result = new List<LoadableXmlAsset>();
            return false;
        }

        private static bool ErrorCheckPatchesPrefix() => !_activeThisLoad || !CacheHook.ShouldSkipErrorCheckPatches();

        private static bool ApplyPatchesPrefix(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            if (!_activeThisLoad) return true;
            CacheHook.ApplyPatchesReplacement(xmlDoc, assetlookup);
            return false;
        }

        private static bool ClearCachedPatchesPrefix() => !_activeThisLoad || !CacheHook.ShouldSkipClearPatches();
    }
}
