# Profile-Aware Cache Pruning

**Date:** 2026-04-10
**Goal:** Make cache pruning smarter by associating caches with saved mod list profiles, so tweaking a mod list replaces the old cache instead of eating up extra slots.

## Motivation

Currently, every unique mod list creates a separate cache file. If a player tweaks their "MilSim" list 5 times, they end up with 5 cache files when they only need one. The `maxCachedProfiles` setting handles cleanup eventually, but it treats every fingerprint as a separate profile.

Players think of their mod list as a "profile" that they add and remove mods from. DefLoadCache should understand that and keep one cache per profile, replacing old ones when the list changes.

## How it works

RimWorld saves mod list profiles as `.rml` files in `{SaveDataFolder}/ModLists/`. The filename is the profile name (e.g., `Full MilSim.rml`). These files contain the full list of packageIds.

On each cache save, DefLoadCache stores the active mod list in meta.json. During pruning, it reads the `.rml` files and matches each cache's mod list against them. Caches that match a profile get tagged with the profile name. Pruning then keeps one cache per profile and one untagged cache for the active list.

## Component 1: Extended meta.json

Add two new fields to meta.json during `SaveToCache`:

- `modList`: array of packageIds from `LoadedModManager.RunningModsListForReading`
- `profileName`: string or null, populated by `Prune()` after matching

```json
{
  "timestamp": "2026-04-10T15:52:57Z",
  "modCount": 17,
  "rimworldVersion": "1.6.4633",
  "cacheFormatVersion": 4,
  "sizeBytes": 3109128,
  "totalNodeCount": 16439,
  "nodeCountsByMod": { ... },
  "profileName": "Full MilSim",
  "modList": [
    "zetrith.prepatcher",
    "brrainz.harmony",
    "ludeon.rimworld"
  ]
}
```

The `modList` is collected from `LoadedModManager.RunningModsListForReading` at save time. Order matches load order. No `CacheFormatVersion` bump needed since old caches without `modList` just won't participate in profile matching and get pruned by the fallback logic.

## Component 2: Profile matching in Prune

The new `Prune()` logic replaces the current "keep N most recent" approach:

1. Read all `.rml` files from RimWorld's `ModLists/` directory (found via `GenFilePaths.SaveDataFolderPath`)
2. Parse each `.rml` to extract the packageId list. The filename minus `.rml` extension is the profile name.
3. For each existing cache's meta.json, read the `modList` array
4. Compare each cache's mod list against each `.rml` profile using exact match (same packageIds, same order)
5. If a cache matches a profile, write the profile name into that cache's meta.json `profileName` field

Pruning rules:
- If two or more caches share the same profile name, keep the newest, delete the rest
- Untagged caches (no profile match): keep only the most recent one, delete the rest
- Profile-tagged caches are never pruned just for being old

No `maxCachedProfiles` limit. Total cache count = number of matched profiles + 1 untagged slot.

### Backwards compatibility

Old caches without a `modList` field in their meta.json can't participate in profile matching. They are treated as untagged and pruned by the "keep most recent untagged" rule. Over time they age out naturally as new caches replace them.

### .rml parsing

The `.rml` file format is XML:
```xml
<savedModList>
  <modList>
    <ids>
      <li>packageId1</li>
      <li>packageId2</li>
    </ids>
  </modList>
</savedModList>
```

Parse `savedModList/modList/ids/li` elements to get the packageId list.

## Component 3: Status block update

Update `StatusBlockEmitter` to show the profile name when available:

- Cache hit with profile: `Result: Cache HIT (profile: Full MilSim)`
- Cache hit without profile: `Result: Cache HIT`
- Cache miss: `Result: Cache MISS (full load)`

The profile name is read from the meta.json `profileName` field, which was set by the previous launch's Prune cycle.

Add a prune summary log message:
```
Prune: matched 2 caches to profiles (Full MilSim, DefLoadCache POC Test), removed 3 stale untagged caches
```

## Component 4: Settings UI changes

- Remove the `maxCachedProfiles` slider
- Remove the `maxCachedProfiles` field from `DefLoadCacheSettings` (keep it in `ExposeData` temporarily so loading old settings files doesn't throw, but don't use the value)
- Update the cache info section to show profile details:

```
  Cached profiles: 2 (Full MilSim, DefLoadCache POC Test)
  Active list cache: 1
  Disk usage: 21024 KB (20 MB)
```

## Files Changed

| File | Change |
|------|--------|
| `src/Cache/CacheStorage.cs` | Rewrite `Prune()` with profile matching. Add `.rml` parsing helper. Remove `MaxCachedFilesToKeep`. |
| `src/Hook/CacheHook.cs` | Add `modList` array to meta.json in `SaveToCache`. |
| `src/Diagnostics/StatusBlockEmitter.cs` | Show profile name in status block when available. |
| `src/Settings/DefLoadCacheSettings.cs` | Remove `maxCachedProfiles` field. |
| `src/Settings/DefLoadCacheMod.cs` | Remove slider. Update cache info to show profile names. |

## Files NOT Changed

`CacheValidator.cs`, `ModAttributionTagger.cs`, `ModlistFingerprint.cs`, `IlInjector.cs`, `CacheFormat.cs`, `ErrorNoticeHook.cs`, `DiagnosticDump.cs`, `Log.cs`

## Testing

1. **Profile match.** Save a vanilla mod list as "Test Profile". Launch (cache miss). Launch again (cache hit). Verify meta.json has `profileName: "Test Profile"`.
2. **Tweak and rebuild.** Add a mod, launch. Verify old "Test Profile" cache is deleted and replaced with new one tagged with the same profile (if the player re-saved the profile). If they didn't re-save, old tagged cache stays and new cache is untagged.
3. **Untagged pruning.** Without saving a profile, tweak the mod list 3 times. Verify only the most recent untagged cache survives.
4. **Multiple profiles.** Save two different mod lists as two profiles. Verify both caches coexist.
5. **Backwards compat.** Launch with old caches that have no `modList` in meta. Verify they get pruned normally without errors.
6. **Status block.** Verify profile name appears in the status block on cache hit after a prune cycle has tagged the cache.

## Future extensions

- RimSort profile support (read from `AppData/Local/RimSort/modlists/`)
- RimPy profile support (format TBD)
- These would be additional profile sources plugged into the same matching logic in Prune
