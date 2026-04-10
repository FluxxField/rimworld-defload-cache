using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// On cache-hit launches, patches Verse.Log.Error so that the FIRST
    /// error logged in the session is followed by a notice that
    /// DefLoadCache is active. Modders reading a player's log will see
    /// the notice immediately after the error they're investigating.
    ///
    /// Only installed on cache-hit launches. If the cache wasn't used,
    /// there's nothing to warn about.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ErrorNoticeHook
    {
        private static bool _fired;

        static ErrorNoticeHook()
        {
            if (!CacheHook.CacheHitOccurred) return;

            try
            {
                var harmony = new Harmony("fluxxfield.defloadcache.errornotice");
                var target = AccessTools.Method(typeof(Verse.Log), "Error",
                    new[] { typeof(string) });

                if (target == null)
                {
                    Log.Warning("ErrorNoticeHook: could not find Verse.Log.Error(string), skipping patch");
                    return;
                }

                harmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(ErrorNoticeHook), nameof(Postfix)));
            }
            catch (Exception ex)
            {
                Log.Error($"ErrorNoticeHook: failed to install patch: {ex}");
            }
        }

        private static void Postfix()
        {
            if (_fired) return;
            _fired = true;

            // Safe to call Log.Message here since we're patching Log.Error, not Log.Message
            Log.Message("NOTE: DefLoadCache is active and used cached data this launch. " +
                        "If investigating a bug, please test with DefLoadCache disabled " +
                        "first. Mod Settings \u2192 DefLoadCache \u2192 \"Test without " +
                        "cache (next launch only)\", then restart.");
        }
    }
}
