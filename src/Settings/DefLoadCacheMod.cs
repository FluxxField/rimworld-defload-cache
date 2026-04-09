using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace FluxxField.DefLoadCache
{
    internal class DefLoadCacheMod : Mod
    {
        public static DefLoadCacheSettings Settings { get; private set; } = null!;

        public DefLoadCacheMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DefLoadCacheSettings>();
        }

        public override string SettingsCategory() => "DefLoadCache";

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            // Enable/disable
            listing.CheckboxLabeled(
                "Enable caching",
                ref Settings.cacheEnabled,
                "When disabled, DefLoadCache will not read or write cache files. The game loads normally.");

            listing.Gap();

            // Cache retention slider
            Settings.maxCachedProfiles = (int)listing.SliderLabeled(
                $"Max cached profiles: {Settings.maxCachedProfiles}",
                Settings.maxCachedProfiles,
                1f, 20f,
                tooltip: "Number of modlist profile caches to keep on disk. Each is ~7 MB.");

            listing.Gap();

            // Diagnostic dump toggle
            listing.CheckboxLabeled(
                "Enable diagnostic dump (Stage E)",
                ref Settings.diagnosticDumpEnabled,
                "Writes a sorted DefDatabase snapshot to disk after loading. Used to verify cache correctness by comparing miss vs hit dumps.");

            listing.Gap();
            listing.GapLine();
            listing.Gap();

            // Cache info
            string cacheRoot = CacheStorage.CacheRoot;
            if (Directory.Exists(cacheRoot))
            {
                var files = Directory.GetFiles(cacheRoot, "*.xml.gz");
                long totalBytes = 0;
                foreach (var f in files)
                {
                    try { totalBytes += new FileInfo(f).Length; } catch { }
                }
                listing.Label($"Cache location: {cacheRoot}");
                listing.Label($"Cached profiles: {files.Length}");
                listing.Label($"Total cache size: {totalBytes / 1024} KB");
            }
            else
            {
                listing.Label("No cache files found.");
            }

            listing.Gap();

            // Last run info
            if (CacheHook.CacheHitOccurred)
            {
                listing.Label("Last launch: cache HIT");
            }
            else if (CacheHook.LastRunWasMiss)
            {
                listing.Label("Last launch: cache MISS (fresh cache written)");
            }

            listing.Gap();

            // Clear cache button
            if (listing.ButtonText("Clear all cached data"))
            {
                if (Directory.Exists(cacheRoot))
                {
                    foreach (var f in Directory.GetFiles(cacheRoot))
                    {
                        try { File.Delete(f); } catch { }
                    }
                    Messages.Message("DefLoadCache: all cache files deleted.", MessageTypeDefOf.TaskCompletion, false);
                }
            }

            listing.End();
        }
    }
}
