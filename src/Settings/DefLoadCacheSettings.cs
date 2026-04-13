using Verse;

namespace FluxxField.DefLoadCache
{
    internal class DefLoadCacheSettings : ModSettings
    {
        /// <summary>
        /// Master gate for all experimental features. When false, experimental
        /// settings don't render in the UI and all experimental code paths are
        /// skipped. Flip to true for development and testing. The compiler
        /// optimizes away dead code when false.
        /// </summary>
        internal const bool ExperimentalEnabled = false;

        public bool cacheEnabled = true;
        public bool skipModFileLoading = true;
        public bool skipPatchApplication = true;
        public bool diagnosticDumpEnabled = false;
        public bool skipNextLaunch = false;

        // --- Experimental settings (gated by ExperimentalEnabled) ---
        public bool perModHashing = false;
        public bool includeModSettingsInFingerprint = false;
        public bool checkpointEnabled = false;

        // Legacy field kept only for backwards compat when loading old settings files
        private int _legacyMaxCachedProfiles = 10;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref cacheEnabled, "cacheEnabled", true);
            Scribe_Values.Look(ref skipModFileLoading, "skipModFileLoading", true);
            Scribe_Values.Look(ref skipPatchApplication, "skipPatchApplication", true);
            Scribe_Values.Look(ref _legacyMaxCachedProfiles, "maxCachedProfiles", 10);
            Scribe_Values.Look(ref diagnosticDumpEnabled, "diagnosticDumpEnabled", false);
            Scribe_Values.Look(ref skipNextLaunch, "skipNextLaunch", false);

            if (ExperimentalEnabled)
            {
                Scribe_Values.Look(ref perModHashing, "perModHashing", false);
                Scribe_Values.Look(ref includeModSettingsInFingerprint, "includeModSettingsInFingerprint", false);
                Scribe_Values.Look(ref checkpointEnabled, "checkpointEnabled", false);
            }

            base.ExposeData();
        }
    }
}
