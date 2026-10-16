# DefLoadCache

A RimWorld mod that caches the result of mod XML loading and patch application, so repeat launches with the same mod list skip that work. Supports RimWorld 1.6.

## The Problem

Every time RimWorld launches with mods, it:
1. **Reads thousands of XML files** from every mod's `Defs/` folder
2. **Applies thousands of XML patches** (compatibility patches, balance changes, etc.)
3. Both steps are done from scratch every single launch, even if nothing changed

On large mod lists these two steps alone can take several minutes.

## The Solution

DefLoadCache intercepts the mod loading pipeline and caches the fully-patched def tree to disk. On subsequent launches with the same mod list:

- **Skips reading mod XML files.** Uses the cached result instead of reading thousands of files from disk
- **Skips applying XML patches.** Uses the cached post-patch def tree instead of re-running every XPath operation

The cache automatically invalidates when anything changes: mods added/removed, mod versions updated, DLLs changed, or RimWorld updated.

**You won't see a speedup on the first launch, or on any launch after your mod list changes.** Those launches run the full loading pipeline and then save the result to disk, which adds a few seconds. Launches after that with the same mod list are faster. Steam updating a mod also counts as a change.

**Hardware note:** DefLoadCache benefits most from an SSD and 16GB+ RAM. On an HDD or with limited RAM, the cache read/write overhead may reduce the time savings. The mod tries to optimize for this (streaming I/O, no large memory allocations), but disk speed is a major factor.

## Performance

Measured launch-to-main-menu times. Your numbers depend on your mod list and hardware.

| Setup | First launch (builds cache) | Repeat launch (uses cache) | Cache size |
|-------|----------------------------|----------------------------|------------|
| RimWorld 1.6, 414 mods (Combat Extended milsim) | ~9:10 | ~2:40 | ~7 MB |
| RimWorld 1.7, 177 mods (1.3.0 test build; 1.7 not yet released) | ~1:46 | ~0:55 | - |

### What DefLoadCache skips on cached launches
- `LoadModXML`, reading and parsing 10,000+ XML files from disk
- `ApplyPatches`, running thousands of XPath patch operations

### What still runs every launch
- Texture, audio, and string loading (~2:45)
- `ParseAndProcessXML` + cross-reference resolution (~2:50)
- Harmony patch application
- Static constructors (OgreStack, etc.)

## Requirements

- **RimWorld 1.6** (1.7 support will return in a later update)
- **[Harmony](https://steamcommunity.com/workshop/filedetails/?id=2009463077).** Prepatcher is no longer needed as of 1.3.1.

## Installation

1. Install [Harmony](https://steamcommunity.com/workshop/filedetails/?id=2009463077) from Steam Workshop
2. Install DefLoadCache
3. Place DefLoadCache anywhere in your mod list after Harmony
4. Launch RimWorld. The first launch builds the cache (normal speed), subsequent launches are faster

## Settings

Access via **Options > Mod Settings > DefLoadCache**:

- **Enable DefLoadCache.** Master switch. Turn off if you experience issues.
- **Skip reading mod files on repeat launches.** Controls whether cached mod XML is used instead of reading from disk. Disable if you're actively editing mod XML files.
- **Skip applying XML patches on repeat launches.** Controls whether the cached post-patch def tree is used. Disable if you're developing/debugging XML patches.
- **Clear all cached data.** Deletes all cache files. Next launch will rebuild from scratch.
- **Test without cache (next launch only).** Temporarily disables the cache for one launch to help isolate issues. The cache is preserved. If the issue goes away, it was cache-related. If it persists, DefLoadCache is not involved. Automatically re-enables after one launch.
- **Write diagnostic snapshot.** Developer tool for verifying cache correctness.

## How It Works

DefLoadCache intercepts four methods in RimWorld's mod loading pipeline (`LoadedModManager.LoadModXML`, `ErrorCheckPatches`, `ApplyPatches`, `ClearCachedPatches`). All four run after mod classes are constructed, so the hooks are installed before they first execute.

The hooks are plain Harmony prefixes, installed from the mod's constructor (`src/Hook/LoadPipelineHooks.cs`), on both 1.6 and 1.7. They call the entry points in `CacheHook`.

Versions before 1.3.0 used [Prepatcher](https://steamcommunity.com/sharedfiles/filedetails/?id=2934420800)'s `[FreePatch]` IL injection instead. That was dropped because Prepatcher scans every mod DLL in the load order, and crashes the launch when a mod has an optional dependency that isn't installed (seen with Star Ring Corporation without Humanoid Alien Races).

### Cache Pipeline

**First launch (cache miss):**
1. Compute a SHA256 fingerprint of the active mod list (packageIds, versions, file counts, DLL sizes)
2. No cache file matches → let RimWorld load normally
3. After patches are applied, stamp mod attribution on each def node, serialize the merged XML document to a gzipped file, then strip the attribution from the live document

**Subsequent launches (cache hit):**
1. Compute fingerprint, which matches an existing cache file
2. Skip `LoadModXML` entirely (return empty list)
3. Skip `ApplyPatches`, streaming the cached document directly into the existing `XmlDocument`
4. Rebuild the `assetlookup` dictionary from embedded mod attribution data
5. `ParseAndProcessXML` runs normally on the cached document

### Cache Invalidation

The fingerprint includes:
- RimWorld version
- Per-mod: packageId, version from `About.xml`
- Per-mod: the path, size and modification time of every file under `Defs/` and `Patches/` (respecting `LoadFolders.xml` version-specific paths)
- Per-mod: the same for `Assemblies/*.dll` in every load folder
- Cache format version

File contents are not hashed. Any edit to a file updates its modification time, so it changes the fingerprint and triggers a full rebuild. The flip side is that a Steam re-download also triggers a rebuild, even if the files are byte-for-byte identical.

### Safety & Self-Validation

DefLoadCache is designed to never cause problems for other mods. If something goes wrong, it catches itself:

- **Post-load validation.** On every cache hit, the mod compares per-mod def counts against the baseline stored when the cache was built. If any counts don't match, the bad cache is automatically deleted and the next launch runs uncached.
- **Self-healing.** A bad cache never survives two launches. Validation failure triggers automatic cache deletion and forces a clean uncached load on the next restart.
- **Pre-load cache validation.** Before loading a cache file, the mod reads through the entire file to verify it decompresses and parses correctly. If the file is corrupt or in the wrong format, it's deleted and the game loads normally. The caller's document is never touched until the cache is proven valid.
- **First-error notice.** If any error occurs in the log during a cache-hit launch, DefLoadCache emits a notice reminding the player to test without the cache before filing bug reports on other mods.
- **Structured status block.** Every launch emits a clearly delimited block in the log showing cache state, validation result, and troubleshooting guidance. Modders reviewing player logs can instantly see whether DefLoadCache was involved.
- **Full fallback coverage.** Every public entry point is wrapped in `try/catch`. If anything throws at any point, the game falls back to normal loading. The mod should never crash the game under any circumstances.
- **Memory-conscious design.** Cache files are streamed directly to/from disk without buffering the full document in memory. No temporary copies or memory doubling during cache reads or writes.
- **Zero side effects.** The mod can be disabled at any time with no impact on other mods.

### For Mod Authors

If a player files a bug report with DefLoadCache installed, search their log for `DefLoadCache Status`. You'll see:

```
[DefLoadCache] ══════════════════════════════════════════════════
[DefLoadCache]   DefLoadCache Status
[DefLoadCache]   Result:      Cache HIT
[DefLoadCache]   Fingerprint: abc123...
[DefLoadCache]   Cache built: 2026-04-10 15:57 UTC
[DefLoadCache]   Defs loaded: 16,439 (expected: 16,439 ✓)
[DefLoadCache]   Validation:  PASSED
[DefLoadCache]
[DefLoadCache]   If you are filing a bug report for another mod,
[DefLoadCache]   please test with DefLoadCache disabled first.
[DefLoadCache]   Mod Settings → DefLoadCache → "Test without
[DefLoadCache]   cache (next launch only)", then restart.
[DefLoadCache] ══════════════════════════════════════════════════
```

If validation shows PASSED, DefLoadCache served the correct data. If there's any doubt, ask the player to click "Test without cache (next launch only)" in mod settings and reproduce the issue.

## Correctness Verification

DefLoadCache includes a built-in diagnostic dump tool. When enabled in settings, it writes a sorted snapshot of every loaded def (type, defName, mod, label) to a text file. Running this on both a cache-miss and cache-hit launch produces identical output (55,245 defs verified on a 414-mod list).

## Building from Source

```bash
# Clone
git clone https://github.com/FluxxField/rimworld-defload-cache.git
cd rimworld-defload-cache

# Build for RimWorld 1.7 (requires .NET 10 SDK)
dotnet build -c Release -f net10.0   # Output: 1.7/Assemblies/DefLoadCache.dll

# Build for RimWorld 1.6 (requires a RimWorld 1.6 install)
dotnet build -c Release -f net48     # Output: 1.6/Assemblies/DefLoadCache.dll
```

Each build needs only its own version's game assemblies, referenced via `HintPath`. Copy `DefLoadCache.local.props.example` to `DefLoadCache.local.props` to set paths for your machine:

- **1.7:** `Assembly-CSharp.dll`, `UnityStubs.dll`, `Waylith.dll` from the game root, and `0Harmony.dll` from the Harmony mod's `1.7/Assemblies`.
- **1.6:** `Assembly-CSharp.dll` + Unity modules from `RimWorldWin64_Data/Managed`, and `0Harmony.dll` from the Harmony mod's `1.6/Assemblies`.

`LoadFolders.xml` makes each RimWorld version load only its own `1.x/Assemblies`. When packaging, the mod root must not contain an `Assemblies/` folder.

## Compatibility

- **Works alongside** [Faster Game Loading](https://steamcommunity.com/sharedfiles/filedetails/?id=3652938473), [Image Opt](https://steamcommunity.com/sharedfiles/filedetails/?id=3543873568), Performance Fish and HugsLib. They speed up other parts of loading; DefLoadCache only covers XML loading and patch application.
- **MissileGirl** works with its own XML cache (Gagarin) turned off. Two caches over the same step conflict.
- **Star Ring Corporation** works as of 1.3.1. Its About.xml may still list DefLoadCache as incompatible until its authors update it; that entry dates from when DefLoadCache needed Prepatcher.
- **[Visual XML Patches](https://steamcommunity.com/sharedfiles/filedetails/?id=3570531169) is not compatible.** It records patch results while patches run, and on a cached launch patches don't run, so it has nothing to show. Disable DefLoadCache (or use "Test without cache") while using it.

## Known Limitations

- Mod settings are not tracked. If another mod's settings turn its XML patches on or off, click "Clear all cached data" after changing them.
- Mods that generate or change def XML from code during loading, instead of through normal Defs and Patches files, may not be reflected in the cache.
- Only XML loading and patching are cached. Textures, sounds and def processing still run every launch.
- Any mod list change, including a Steam update to one mod, rebuilds the whole cache.

## Development Notes

1.3.0 was pulled after users reported black main menus. Besides replacing Prepatcher, it shipped changes that had been committed after the 1.2.0 release but never released (a main menu status overlay, a rewrite of how cached patches are applied, and an experimental checkpoint-based incremental rebuild). 1.3.1 starts again from the released 1.2.0 code and adds only the Prepatcher replacement and a few small fixes. The unreleased work is kept on the `archive/unreleased-experiments-2026-04` branch.

RimWorld 1.7 support (the `net10.0` target) builds from this source but has not been re-tested since 1.3.0, so 1.3.1 ships for 1.6 only.

## License

MIT

## Credits

- **FluxxField.** Author
- **[CriDos](https://github.com/CriDos/rimworld-defload-cache)** (Maxim G.) Per-file fingerprinting, streaming hash, and ErrorCheckPatches skip.
- **[Harmony](https://steamcommunity.com/workshop/filedetails/?id=2009463077)** by Brrainz. The patching library DefLoadCache hooks the loading pipeline with.
- **[Prepatcher](https://steamcommunity.com/sharedfiles/filedetails/?id=2934420800)** by Zetrith. Its `[FreePatch]` IL injection powered versions before 1.3.0. Thanks to Jikulopo for maintaining a fork while Zetrith was away.
- **[Krafs.Publicizer](https://github.com/krafs/Publicizer)** and **[Performance Fish](https://github.com/bbradson/Performance-Fish)** by bbradson. The build setup for versions before 1.3.0.
