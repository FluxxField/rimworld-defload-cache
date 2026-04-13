using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace FluxxField.DefLoadCache
{
    internal class DefLoadCacheMod : Mod
    {
        public static DefLoadCacheSettings Settings { get; private set; } = null!;
        private Vector2 _scrollPosition;

        public DefLoadCacheMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DefLoadCacheSettings>();
        }

        public override string SettingsCategory() => "DefLoadCache";

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            // Scrollable settings content. The view rect is taller than the
            // window to accommodate all sections including experimental features.
            var viewRect = new Rect(0, 0, inRect.width - 16f, 900f);
            Widgets.BeginScrollView(inRect, ref _scrollPosition, viewRect);

            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            // --- Master toggle ---
            listing.CheckboxLabeled(
                "Enable DefLoadCache",
                ref Settings.cacheEnabled,
                "Master switch. When off, the mod does nothing and the game loads normally. " +
                "Turn this off if you experience any issues after changing your mod list.");

            if (Settings.cacheEnabled)
            {
                listing.Gap();

                listing.CheckboxLabeled(
                    "Skip reading mod files on repeat launches",
                    ref Settings.skipModFileLoading,
                    "On the first launch with a mod list, RimWorld reads thousands of XML files " +
                    "from every mod folder. This takes several minutes on large mod lists. " +
                    "When enabled, repeat launches skip this step entirely and use the " +
                    "cached result instead.\n\n" +
                    "Disable this if you are actively editing mod XML files and want changes " +
                    "to take effect without clearing the cache.");

                listing.Gap();

                listing.CheckboxLabeled(
                    "Skip applying XML patches on repeat launches",
                    ref Settings.skipPatchApplication,
                    "After reading mod files, RimWorld applies thousands of XML patches " +
                    "(compatibility patches, balance changes, etc). This is the slowest " +
                    "step, often 5+ minutes on large mod lists. When enabled, repeat " +
                    "launches use the cached post-patch result instead of re-running " +
                    "every patch.\n\n" +
                    "Disable this if you are developing or debugging XML patches.");
            }

            listing.Gap();
            listing.GapLine();
            listing.Gap();

            // --- Cache info ---
            listing.Label("Cache Information", tooltip: "Details about the current cache state.");
            listing.Gap(4f);

            string cacheRoot = CacheStorage.CacheRoot;
            if (Directory.Exists(cacheRoot))
            {
                var cacheFiles = Directory.GetFiles(cacheRoot, "*.xml.gz");
                long totalBytes = 0;
                var profileNames = new System.Collections.Generic.List<string>();
                int untaggedCount = 0;

                foreach (var f in cacheFiles)
                {
                    try { totalBytes += new FileInfo(f).Length; } catch { }

                    // Read profile name from meta
                    string name = Path.GetFileName(f);
                    if (name.EndsWith(".xml.gz"))
                    {
                        string fingerprint = name.Substring(0, name.Length - ".xml.gz".Length);
                        string? meta = CacheStorage.ReadMeta(fingerprint);
                        if (meta != null)
                        {
                            string? profile = CacheValidator.ParseString(meta, "profileName");
                            if (profile != null)
                                profileNames.Add(profile);
                            else
                                untaggedCount++;
                        }
                        else
                        {
                            untaggedCount++;
                        }
                    }
                }

                listing.Label($"  Location: {cacheRoot}");
                if (profileNames.Count > 0)
                    listing.Label($"  Cached profiles: {profileNames.Count} ({string.Join(", ", profileNames)})");
                if (untaggedCount > 0)
                    listing.Label($"  Active list caches: {untaggedCount}");
                listing.Label($"  Disk usage: {totalBytes / 1024} KB ({totalBytes / 1024 / 1024} MB)");
            }
            else
            {
                listing.Label("  No cache files found.");
            }

            listing.Gap(4f);

            // Last run info
            if (CacheHook.CacheHitOccurred)
            {
                listing.Label("  Last launch: Used cached data (fast launch)");
            }
            else if (CacheHook.LastRunWasMiss)
            {
                listing.Label("  Last launch: Built fresh cache (first launch with this mod list)");
            }

            listing.Gap();

            // --- Clear cache button ---
            if (listing.ButtonText("Clear all cached data"))
            {
                if (Directory.Exists(cacheRoot))
                {
                    foreach (var f in Directory.GetFiles(cacheRoot))
                    {
                        try { File.Delete(f); } catch { }
                    }
                    Messages.Message("DefLoadCache: all cache files deleted. Next launch will rebuild the cache.",
                        MessageTypeDefOf.TaskCompletion, false);
                }
            }
            listing.Gap(4f);
            listing.Label("  Use this if the game behaves oddly after changing mods. The next\n" +
                          "  launch will take longer as the cache is rebuilt.",
                tooltip: "Clearing the cache forces RimWorld to do a full load on the next launch.");

            listing.Gap();

            // --- Test without cache (one-launch skip) ---
            if (listing.ButtonText("Test without cache (next launch only)",
                "Temporarily disables the cache for one launch to help isolate issues. " +
                "The cache is preserved. If the issue goes away, it was cache-related. " +
                "If it persists, DefLoadCache is not involved."))
            {
                Settings.skipNextLaunch = true;
                Messages.Message("DefLoadCache will skip the cache on next launch. Restart to test.",
                    MessageTypeDefOf.TaskCompletion, false);
            }
            listing.Gap(4f);
            listing.Label("  Use this to check if a bug you're experiencing is caused by\n" +
                          "  DefLoadCache. The cache is kept, only one launch is affected.",
                tooltip: "After one uncached launch, the cache is automatically re-enabled.");

            listing.Gap();
            listing.GapLine();
            listing.Gap();

            // --- Advanced / Developer ---
            listing.Label("Developer Options");
            listing.Gap(4f);
            listing.CheckboxLabeled(
                "Write diagnostic snapshot after loading",
                ref Settings.diagnosticDumpEnabled,
                "Writes a detailed list of every loaded def to a file after the game " +
                "finishes loading. This is used to verify that cached launches produce " +
                "identical results to normal launches. Only enable this if asked to " +
                "by a mod developer for debugging purposes.\n\n" +
                "Files are saved in the cache folder as diagnostic-cache-hit.txt " +
                "and diagnostic-cache-miss.txt.");

            if (DefLoadCacheSettings.ExperimentalEnabled)
            {
                listing.Gap();
                listing.GapLine();
                listing.Gap();

                listing.Label("Experimental Features",
                    tooltip: "These features are under active development and may not be stable. " +
                    "Use at your own risk.");
                listing.Gap(4f);

                listing.CheckboxLabeled(
                    "Include mod settings in fingerprint",
                    ref Settings.includeModSettingsInFingerprint,
                    "Include each mod's settings file in the cache fingerprint. " +
                    "This ensures the cache is invalidated when you change mod settings " +
                    "that affect XML patching (e.g., VEF toggles, Tweaks Galore options).\n\n" +
                    "Without this, changing a mod setting that affects patches requires " +
                    "manually clearing the cache.");

                listing.Gap();

                listing.CheckboxLabeled(
                    "Per-mod fingerprint hashing",
                    ref Settings.perModHashing,
                    "Compute and store a separate fingerprint hash for each mod instead of " +
                    "one combined hash. This is the foundation for future incremental " +
                    "rebuild support where only changed mods trigger a partial cache rebuild.\n\n" +
                    "No behavior change yet, just stores extra data for future use.");
            }

            listing.End();
            Widgets.EndScrollView();
        }
    }
}
