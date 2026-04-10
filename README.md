# DefLoadCache

A RimWorld performance mod that caches the results of mod XML loading and patch application, dramatically reducing launch time on repeat loads.

## The Problem

Every time RimWorld launches with mods, it:
1. **Reads thousands of XML files** from every mod's `Defs/` folder
2. **Applies thousands of XML patches** (compatibility patches, balance changes, etc.)
3. Both steps are done from scratch every single launch, even if nothing changed

On large mod lists (500+), this takes **10-15 minutes**. Steps 1 and 2 alone can account for 6+ minutes.

## The Solution

DefLoadCache intercepts the mod loading pipeline and caches the fully-patched def tree to disk. On subsequent launches with the same mod list:

- **Skips reading mod XML files.** Uses the cached result instead of reading thousands of files from disk
- **Skips applying XML patches.** Uses the cached post-patch def tree instead of re-running every XPath operation

The cache automatically invalidates when anything changes: mods added/removed, mod versions updated, DLLs changed, or RimWorld updated.

## Performance

Tested on a 576-mod Combat Extended milsim load order:

| Metric | Without Cache | With Cache | Improvement |
|--------|--------------|------------|-------------|
| Total launch time | ~14 min | ~7:40 | **45% faster** |
| Mod XML loading + patching | ~6 min | ~23 sec | **15.6x faster** |
| Fingerprint computation | - | 98-341ms | - |
| Cache size on disk | - | ~7 MB | - |

### What DefLoadCache skips on cached launches
- `LoadModXML`, reading and parsing 10,000+ XML files from disk
- `ApplyPatches`, running thousands of XPath patch operations

### What still runs every launch
- Texture, audio, and string loading (~2:45)
- `ParseAndProcessXML` + cross-reference resolution (~2:50)
- Harmony patch application
- Static constructors (OgreStack, etc.)

## Requirements

- **[Prepatcher](https://steamcommunity.com/sharedfiles/filedetails/?id=2934420800).** Required dependency. DefLoadCache uses Prepatcher's `[FreePatch]` system to inject IL into the mod loading pipeline.
- **RimWorld 1.6**

## Installation

1. Install [Prepatcher](https://steamcommunity.com/sharedfiles/filedetails/?id=2934420800) from Steam Workshop
2. Install DefLoadCache
3. Place DefLoadCache anywhere in your mod list (after Prepatcher and Harmony)
4. Launch RimWorld. The first launch builds the cache (normal speed), subsequent launches are faster

## Settings

Access via **Options > Mod Settings > DefLoadCache**:

- **Enable DefLoadCache.** Master switch. Turn off if you experience issues.
- **Skip reading mod files on repeat launches.** Controls whether cached mod XML is used instead of reading from disk. Disable if you're actively editing mod XML files.
- **Skip applying XML patches on repeat launches.** Controls whether the cached post-patch def tree is used. Disable if you're developing/debugging XML patches.
- **Saved mod list profiles.** How many different mod list caches to keep (default 10). Useful if you switch between multiple mod lists using RimPy.
- **Clear all cached data.** Deletes all cache files. Next launch will rebuild from scratch.
- **Test without cache (next launch only).** Temporarily disables the cache for one launch to help isolate issues. The cache is preserved. If the issue goes away, it was cache-related. If it persists, DefLoadCache is not involved. Automatically re-enables after one launch.
- **Write diagnostic snapshot.** Developer tool for verifying cache correctness.

## How It Works

DefLoadCache uses [Prepatcher](https://steamcommunity.com/sharedfiles/filedetails/?id=2934420800)'s `[FreePatch]` API to inject IL instructions into RimWorld's mod loading pipeline at compile time (before the CLR verifies `Assembly-CSharp.dll`). This allows intercepting methods that are normally inaccessible to Harmony.

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
- Per-mod: `Defs/`, `Patches/` file counts, total byte sizes, and **latest modification timestamps** (respecting `LoadFolders.xml` version-specific paths)
- Per-mod: `Assemblies/*.dll` file counts, sizes, and modification timestamps
- Cache format version

Any change to any file, including same-size content edits (e.g., changing `cost=1000` to `cost=9000`), updates the file's modification timestamp, which changes the fingerprint and triggers a full cache rebuild.

Any change to any of these causes a cache miss and full rebuild.

### Safety & Self-Validation

DefLoadCache is designed to never cause problems for other mods. If something goes wrong, it catches itself:

- **Post-load validation.** On every cache hit, the mod compares per-mod def counts against the baseline stored when the cache was built. If any counts don't match, the bad cache is automatically deleted and the next launch runs uncached.
- **Self-healing.** A bad cache never survives two launches. Validation failure triggers automatic cache deletion and forces a clean uncached load on the next restart.
- **First-error notice.** If any error occurs in the log during a cache-hit launch, DefLoadCache emits a notice reminding the player to test without the cache before filing bug reports on other mods.
- **Structured status block.** Every launch emits a clearly delimited block in the log showing cache state, validation result, and troubleshooting guidance. Modders reviewing player logs can instantly see whether DefLoadCache was involved.
- **Fail-safe error handling.** Every public entry point is wrapped in `try/catch`. If anything throws, the game falls back to normal loading.
- **Point-of-no-return detection.** If the cached document is corrupted mid-load (after the XML doc has been mutated), the error is logged and rethrown rather than silently producing bad state.
- **Corrupt cache cleanup.** Cache files that fail to deserialize are automatically deleted.
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

DefLoadCache includes a built-in diagnostic dump tool. When enabled in settings, it writes a sorted snapshot of every loaded def (type, defName, mod, label) to a text file. Running this on both a cache-miss and cache-hit launch produces identical output (55,241 defs verified on a 576-mod list).

## Building from Source

```bash
# Clone
git clone https://github.com/FluxxField/rimworld-defload-cache.git
cd rimworld-defload-cache

# Build (requires .NET SDK, references RimWorld assemblies via HintPath)
dotnet build -c Release

# Output: Assemblies/DefLoadCache.dll
```

The `.csproj` references RimWorld's `Assembly-CSharp.dll`, `UnityEngine.CoreModule.dll`, and Prepatcher's `0Harmony.dll` + `0PrepatcherAPI.dll` via `HintPath`. You may need to adjust these paths for your installation.

Uses [Krafs.Publicizer](https://github.com/krafs/Publicizer) to access Mono.Cecil types bundled inside `0Harmony.dll` (the same approach used by [Performance Fish](https://github.com/bbradson/Performance-Fish)).

## Roadmap

DefLoadCache currently saves ~6 minutes on a 576-mod list by caching mod XML loading and patch application. There's more on the table.

### Near-term improvements
- ~~**Content-aware fingerprinting.**~~ **Done!** The fingerprint now includes file modification timestamps. Same-size content changes invalidate the cache automatically.
- **Per-file content checksums.** Maintain a persistent map of file path → mtime + content checksum. Only recompute checksums when a file's mtime changes. If Steam re-downloads a mod but the content is byte-for-byte identical, the checksum stays the same and the cache survives. This improves cache hit rates on large modlists where workshop mods get frequent metadata-only updates.
- **Binary cache format.** Replace gzipped XML with a compact binary format (MessagePack or similar) for faster deserialization. Current cache-hit deserialization is ~2.3 seconds; a binary format could cut this significantly.
- **Parallel fingerprint hashing.** Fingerprint computation is already parallelized across mods, but individual About.xml reads could be further optimized or eliminated.
- **`ErrorCheckPatches` skip on cache hit.** The 7.6 second gap between LoadModXML skip and ApplyPatches entry includes patch config validation that's unnecessary on cached launches.

### Potential future features
- **Deferred texture loading.** Load textures in background threads while the user is at the main menu instead of blocking startup. Textures not needed until gameplay could be loaded on-demand, saving ~2-3 minutes of startup time.
- **Cached parsed Def objects (Tier 2).** Skip `ParseAndProcessXML` and cross-reference resolution entirely by caching the built `DefDatabase` object graph. This is where the remaining ~3 minutes lives, but requires careful Harmony patch invalidation tracking.
- **Prepatcher-based profiler with mod attribution.** A runtime profiler that uses Cecil to instrument Harmony-patched methods and attribute per-tick cost to specific mods. "Top 10 mods by tick cost" with drill-down to specific methods. Directly reuses the Prepatcher + Cecil infrastructure from DefLoadCache.
- **Incremental patching.** On mod list changes where only new patches are added (none removed/modified), apply only the new patches to the cached doc instead of re-running everything.

### The bigger vision

RimWorld mod loading is structurally identical to a compiler pipeline: source files (mod XML) are parsed, merged, transformed (patches), and compiled into runtime objects (DefDatabase). DefLoadCache is Phase 1 of a broader **modlist compiler** project, caching the intermediate representation. Future phases would add profiling, static analysis, and optimization passes using the same Prepatcher + Cecil foundation.

## License

MIT

## Credits

- **FluxxField.** Author
- **[Harmony](https://steamcommunity.com/workshop/filedetails/?id=2009463077)** by Brrainz. The patching library that Prepatcher and most RimWorld mods are built on.
- **[Prepatcher](https://steamcommunity.com/sharedfiles/filedetails/?id=2934420800)** by Zetrith. The `[FreePatch]` IL injection system that makes this possible. Thanks to Jikulopo for maintaining a fork while Zetrith was away.
- **[Krafs.Publicizer](https://github.com/krafs/Publicizer).** Compile-time publicizer for accessing internal Cecil types
- **[Performance Fish](https://github.com/bbradson/Performance-Fish)** by bbradson. Inspiration for the Publicizer + HintPath pattern
