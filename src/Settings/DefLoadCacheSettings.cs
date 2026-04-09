using Verse;

namespace FluxxField.DefLoadCache
{
    internal class DefLoadCacheSettings : ModSettings
    {
        public bool cacheEnabled = true;
        public int maxCachedProfiles = 10;
        public bool diagnosticDumpEnabled = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref cacheEnabled, "cacheEnabled", true);
            Scribe_Values.Look(ref maxCachedProfiles, "maxCachedProfiles", 10);
            Scribe_Values.Look(ref diagnosticDumpEnabled, "diagnosticDumpEnabled", false);
            base.ExposeData();
        }
    }
}
