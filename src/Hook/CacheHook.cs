namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Static entry points called from IL injected by IlInjector.
    /// Stage A: only HookFired() exists, called from the entry of ApplyPatches
    /// to prove the plumbing works.
    /// Later stages add TryLoadCached() and SaveToCache().
    /// </summary>
    public static class CacheHook
    {
        /// <summary>
        /// Stage A plumbing proof. Called by injected IL at the top of
        /// Verse.LoadedModManager.ApplyPatches. Logs a single message and returns.
        ///
        /// IMPORTANT: IlInjector.InjectApplyPatchesHook resolves this method by
        /// name via <c>nameof(CacheHook.HookFired)</c>. If you rename or remove
        /// this method, update the injector in the same commit or the cache
        /// plumbing breaks silently (the method-not-found path logs to
        /// Console.WriteLine but doesn't crash).
        /// </summary>
        public static void HookFired()
        {
            Log.Message("hook fired — Verse.LoadedModManager.ApplyPatches entered");
        }
    }
}
