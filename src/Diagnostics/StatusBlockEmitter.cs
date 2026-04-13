using System;
using RimWorld;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Emits a structured status block to the RimWorld log after all defs
    /// are loaded. Runs on every launch: cache hit, miss, or disabled.
    /// Modders can search for this block in player logs to instantly see
    /// whether DefLoadCache was involved in a reported issue.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class StatusBlockEmitter
    {
        private const string Border = "══════════════════════════════════════════════════";

        static StatusBlockEmitter()
        {
            try
            {
                EmitStatusBlock();
            }
            catch (Exception ex)
            {
                Log.Error($"StatusBlockEmitter failed: {ex}");
            }
        }

        private static void EmitStatusBlock()
        {
            string result;
            string fingerprint = CacheHook.CurrentFingerprint ?? "N/A";
            string cacheBuilt = "N/A";
            string defsLine;
            string validationLine;

            if (CacheHook.CacheHitOccurred)
            {
                string? profileName = null;

                // Read timestamp and profile name from meta.json
                if (CacheHook.CurrentFingerprint != null)
                {
                    string? meta = CacheStorage.ReadMeta(CacheHook.CurrentFingerprint);
                    if (meta != null)
                    {
                        string? ts = CacheValidator.ParseString(meta, "timestamp");
                        if (ts != null)
                        {
                            // Parse ISO 8601 and format for display
                            if (DateTime.TryParse(ts, null,
                                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                            {
                                cacheBuilt = dt.ToString("yyyy-MM-dd HH:mm UTC");
                            }
                            else
                            {
                                cacheBuilt = ts;
                            }
                        }

                        profileName = CacheValidator.ParseString(meta, "profileName");
                    }
                }

                result = profileName != null
                    ? $"Cache HIT (profile: {profileName})"
                    : "Cache HIT";

                // Validation result
                if (CacheValidator.LastValidationPassed == true)
                {
                    defsLine = $"Defs loaded: {CacheValidator.LastActualTotal:N0} (expected: {CacheValidator.LastExpectedTotal:N0} ✓)";
                    validationLine = "Validation:  PASSED";
                }
                else if (CacheValidator.LastValidationPassed == false)
                {
                    defsLine = $"Defs loaded: {CacheValidator.LastActualTotal:N0} (expected: {CacheValidator.LastExpectedTotal:N0} MISMATCH)";
                    validationLine = "Validation:  FAILED, cache deleted, next launch will be clean";
                }
                else
                {
                    defsLine = "Defs loaded: (no baseline available)";
                    validationLine = "Validation:  SKIPPED, no baseline in meta (old cache format)";
                }
            }
            else if (CacheHook.LastRunWasMiss)
            {
                result = "Cache MISS (full load)";
                defsLine = "Defs loaded: full pipeline ran";
                validationLine = "Validation:  N/A, no cache used";
            }
            else
            {
                result = "DISABLED or not triggered";
                defsLine = "Defs loaded: full pipeline ran";
                validationLine = "Validation:  N/A, cache not active";
            }

            // Get our version from the mod's content pack
            string modVersion = "unknown";
            try
            {
                var mod = LoadedModManager.GetMod<DefLoadCacheMod>();
                if (mod?.Content?.ModMetaData?.ModVersion != null)
                    modVersion = mod.Content.ModMetaData.ModVersion;
            }
            catch { }

            Log.Message(Border);
            Log.Message($"  DefLoadCache Status (v{modVersion})");
            Log.Message($"  Result:      {result}");
            Log.Message($"  Fingerprint: {fingerprint}");
            Log.Message($"  Cache built: {cacheBuilt}");
            Log.Message($"  {defsLine}");
            Log.Message($"  {validationLine}");
            Log.Message("");
            Log.Message("  If you are filing a bug report for another mod,");
            Log.Message("  please test with DefLoadCache disabled first.");
            Log.Message("  Mod Settings → DefLoadCache → \"Test without");
            Log.Message("  cache (next launch only)\", then restart.");
            Log.Message(Border);

            // Show in-game notification so users know what happened
            try
            {
                if (CacheHook.CacheHitOccurred)
                {
                    Messages.Message(
                        "DefLoadCache: Loaded from cache. Your launch was faster thanks to cached data.",
                        MessageTypeDefOf.SilentInput, false);
                }
                else if (CacheHook.LastRunWasMiss)
                {
                    Messages.Message(
                        "DefLoadCache: Cache built for the first time. Your next launch will be faster.",
                        MessageTypeDefOf.TaskCompletion, false);
                }
            }
            catch { }
        }
    }
}
