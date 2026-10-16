using Verse;

namespace FluxxField.DefLoadCache
{
    internal class DefLoadCacheSettings : ModSettings
    {
        public bool cacheEnabled = true;
        public bool skipModFileLoading = true;
        public bool skipPatchApplication = true;
        public bool diagnosticDumpEnabled = false;
        public bool skipNextLaunch = false;

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
            base.ExposeData();
        }
    }
}
