using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Hooks the four LoadedModManager pipeline methods with Harmony. Used on
    /// both 1.6 and 1.7. Before 1.3.0 these hooks were IL injected into the
    /// method bodies by Prepatcher; Prepatcher scans every mod DLL and crashes
    /// on mods with missing soft dependencies, so it was dropped.
    ///
    /// The hooks reproduce the injected IL as closely as Harmony allows:
    ///
    ///   - Injected code sat INSIDE the original body, so it ran only when the
    ///     original ran. If another mod's prefix skips the original (MissileGirl's
    ///     Gagarin cache does), our hook must stand down too. Every hook checks
    ///     __runOriginal.
    ///   - Injected code ran after every other mod's prefix and before every
    ///     other mod's postfix. Our prefixes use the lowest priority (run last)
    ///     and our postfix the highest (runs first).
    ///
    /// Injected IL this replaces:
    ///   LoadModXML         if (ShouldSkipLoadModXML()) return new List();
    ///   ErrorCheckPatches  if (ShouldSkipErrorCheckPatches()) return;
    ///   ClearCachedPatches if (ShouldSkipClearPatches()) return;
    ///   ApplyPatches       HookFired(); if (TryLoadCached(..)) return;
    ///                      [body]; SaveToCache(..) before every ret
    ///
    /// Timing: LoadAllActiveMods runs CreateModClasses (which constructs
    /// DefLoadCacheMod, which calls Install) before all four methods.
    /// </summary>
    internal static class LoadPipelineHooks
    {
        private const string HarmonyId = "fluxxfield.defloadcache";

        internal static void Install()
        {
            try
            {
                var harmony = new Harmony(HarmonyId);
                Patch(harmony, "LoadModXML", nameof(LoadModXMLPrefix), null);
                Patch(harmony, "ErrorCheckPatches", nameof(ErrorCheckPatchesPrefix), null);
                Patch(harmony, "ApplyPatches", nameof(ApplyPatchesPrefix), nameof(ApplyPatchesPostfix));
                Patch(harmony, "ClearCachedPatches", nameof(ClearCachedPatchesPrefix), null);
            }
            catch (Exception ex)
            {
                // A partial install is safe: each hook only skips work when its
                // own cache check succeeds, and otherwise lets vanilla run.
                Log.Error("LoadPipelineHooks: failed to install Harmony patches, game loads normally", ex);
            }
        }

        private static void Patch(Harmony harmony, string target, string prefix, string? postfix)
        {
            var original = AccessTools.Method(typeof(LoadedModManager), target);
            if (original == null)
            {
                Log.Warning($"LoadPipelineHooks: LoadedModManager.{target} not found, hook not installed");
                return;
            }
            harmony.Patch(original,
                prefix: new HarmonyMethod(typeof(LoadPipelineHooks), prefix) { priority = int.MinValue },
                postfix: postfix == null ? null : new HarmonyMethod(typeof(LoadPipelineHooks), postfix) { priority = int.MaxValue });
            Log.Message($"Harmony: hooked LoadedModManager.{target}");
        }

        /// <summary>
        /// Harmony patches outlive a mod-list reset: when RimWorld recovers
        /// from a load exception it resets ModsConfig and reloads in the same
        /// process, and DefLoadCache may no longer be in the list. Decided once
        /// per load at LoadModXML (the first hooked step); when false, every
        /// hook lets vanilla run.
        /// </summary>
        private static bool _activeThisLoad;

        private static bool LoadModXMLPrefix(ref List<LoadableXmlAsset> __result, bool __runOriginal)
        {
            CacheHook.BeginLoad();
            _activeThisLoad = DefLoadCacheMod.OwnContent != null
                && LoadedModManager.RunningModsListForReading.Contains(DefLoadCacheMod.OwnContent);
            if (!_activeThisLoad)
            {
                Log.Message("DefLoadCache is not in the active mod list for this load (mod list was reset), loading normally");
                return true;
            }
            if (!__runOriginal) return true;

            if (!CacheHook.ShouldSkipLoadModXML()) return true;
            __result = new List<LoadableXmlAsset>();
            return false;
        }

        private static bool ErrorCheckPatchesPrefix(bool __runOriginal)
        {
            if (!_activeThisLoad || !__runOriginal) return true;
            return !CacheHook.ShouldSkipErrorCheckPatches();
        }

        private static bool ApplyPatchesPrefix(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup,
            bool __runOriginal, out bool __state)
        {
            // __state: true when the original body is about to run with our
            // hook live, so the postfix should save the result.
            __state = false;
            if (!_activeThisLoad || !__runOriginal) return true;

            CacheHook.HookFired();
            if (CacheHook.TryLoadCached(xmlDoc, assetlookup)) return false;

            __state = true;
            return true;
        }

        private static void ApplyPatchesPostfix(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup,
            bool __runOriginal, bool __state)
        {
            if (__state && __runOriginal) CacheHook.SaveToCache(xmlDoc, assetlookup);
        }

        private static bool ClearCachedPatchesPrefix(bool __runOriginal)
        {
            if (!_activeThisLoad || !__runOriginal) return true;
            return !CacheHook.ShouldSkipClearPatches();
        }
    }
}
