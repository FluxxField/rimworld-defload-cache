using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Draws a small cache status overlay on the main menu so players
    /// can see at a glance whether the cache was used and what happened.
    /// Persists on screen as long as they're on the main menu.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class MainMenuOverlay
    {
        private static string? _statusText;

        static MainMenuOverlay()
        {
            try
            {
                BuildStatusText();

                var harmony = new Harmony("fluxxfield.defloadcache.mainmenu");
                var target = AccessTools.Method(typeof(MainMenuDrawer), "DoMainMenuControls");
                if (target == null)
                {
                    Log.Warning("MainMenuOverlay: could not find MainMenuDrawer.DoMainMenuControls");
                    return;
                }

                harmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(MainMenuOverlay), nameof(Postfix)));
            }
            catch (Exception ex)
            {
                Log.Error($"MainMenuOverlay: failed to install patch: {ex}");
            }
        }

        private static void BuildStatusText()
        {
            if (CacheHook.CacheHitOccurred)
            {
                string profileInfo = "";
                if (CacheHook.CurrentFingerprint != null)
                {
                    string? meta = CacheStorage.ReadMeta(CacheHook.CurrentFingerprint);
                    if (meta != null)
                    {
                        string? profile = CacheValidator.ParseString(meta, "profileName");
                        if (profile != null)
                            profileInfo = $" (profile: {profile})";
                    }
                }

                if (CacheValidator.LastValidationPassed == true)
                {
                    _statusText = $"DefLoadCache: Loaded {CacheValidator.LastActualTotal:N0} defs from cache{profileInfo}";
                }
                else if (CacheValidator.LastValidationPassed == false)
                {
                    _statusText = "DefLoadCache: Cache validation failed, will rebuild next launch";
                }
                else
                {
                    _statusText = $"DefLoadCache: Loaded from cache{profileInfo}";
                }
            }
            else if (CacheHook.LastRunWasMiss)
            {
                _statusText = "DefLoadCache: Cache built. Next launch will use cached data if mod list does not change.";
            }
        }

        private static void Postfix()
        {
            if (_statusText == null) return;

            try
            {
                var style = new GUIStyle(Text.fontStyles[0])
                {
                    fontSize = 12,
                    alignment = UnityEngine.TextAnchor.LowerRight,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.6f) }
                };

                float padding = 10f;
                float height = 20f;
                float bottomOffset = 30f; // Space for Loading Progress "Game took X to load" text
                var rect = new Rect(padding, UI.screenHeight - height - padding - bottomOffset,
                    UI.screenWidth - padding * 2, height);

                GUI.Label(rect, _statusText, style);
            }
            catch { }
        }
    }
}
