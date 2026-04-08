using System;
using System.Diagnostics;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Static entry points called from IL injected by IlInjector.
    /// Stage B: HookFired now also computes and logs the modlist fingerprint
    /// with Stopwatch timing so we can verify it's fast enough (&lt;5 sec).
    /// Still no cache write/read.
    /// </summary>
    public static class CacheHook
    {
        /// <summary>
        /// Called by injected IL at the top of
        /// Verse.LoadedModManager.ApplyPatches. Logs the hook-fired message,
        /// then computes and logs the modlist fingerprint with a Stopwatch
        /// so we can verify it's fast.
        ///
        /// IMPORTANT: IlInjector.InjectApplyPatchesHook resolves this method by
        /// name via <c>nameof(CacheHook.HookFired)</c>. If you rename or remove
        /// this method, update the injector in the same commit or the cache
        /// plumbing breaks silently (the method-not-found path logs to
        /// Console.WriteLine but doesn't crash).
        /// </summary>
        public static void HookFired()
        {
            try
            {
                Log.Message("hook fired — Verse.LoadedModManager.ApplyPatches entered");

                var sw = Stopwatch.StartNew();
                string fingerprint = ModlistFingerprint.Compute();
                sw.Stop();

                Log.Message($"fingerprint = {fingerprint} (computed in {sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                Log.Error("HookFired threw — falling back to no-op", ex);
            }
        }
    }
}
