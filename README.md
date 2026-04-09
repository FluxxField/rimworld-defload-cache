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

- **Skips reading mod XML files** — uses the cached result instead of reading thousands of files from disk
- **Skips applying XML patches** — uses the cached post-patch def tree instead of re-running every XPath operation

The cache automatically invalidates when anything changes: mods added/removed, mod versions updated, DLLs changed, or RimWorld updated.

## Performance

Tested on a 576-mod Combat Extended milsim load order:

| Metric | Without Cache | With Cache | Improvement |
|--------|--------------|------------|-------------|
| Total launch time | ~14 min | ~7:40 | **45% faster** |
| Mod XML loading + patching | ~6 min | ~23 sec | **15.6x faster** |
| Fingerprint computation | — | 98-341ms | — |
| Cache size on disk | — | ~7 MB | — |

### What DefLoadCache skips on cached launches
- `LoadModXML` — reading and parsing 10,000+ XML files from disk
- `ApplyPatches` — running thousands of XPath patch operations

### What still runs every launch
- Texture, audio, and string loading (~2:45)
- `ParseAndProcessXML` + cross-reference resolution (~2:50)
- Harmony patch application
- Static constructors (OgreStack, etc.)

## Requirements

- **[Prepatcher](https://steamcommunity.com/sharedfiles/filedetails/?id=3563469557)** — required dependency. DefLoadCache uses Prepatcher's `[FreePatch]` system to inject IL into the mod loading pipeline.
- **RimWorld 1.6**

## Installation

1. Install [Prepatcher](https://steamcommunity.com/sharedfiles/filedetails/?id=3563469557) from Steam Workshop
2. Install DefLoadCache
3. Place DefLoadCache anywhere in your mod list (after Prepatcher and Harmony)
4. Launch RimWorld — the first launch builds the cache (normal speed), subsequent launches are faster

## Settings

Access via **Options > Mod Settings > DefLoadCache**:

- **Enable DefLoadCache** — master switch. Turn off if you experience issues.
- **Skip reading mod files on repeat launches** — controls whether cached mod XML is used instead of reading from disk. Disable if you're actively editing mod XML files.
- **Skip applying XML patches on repeat launches** — controls whether the cached post-patch def tree is used. Disable if you're developing/debugging XML patches.
- **Saved mod list profiles** — how many different mod list caches to keep (default 10). Useful if you switch between multiple mod lists using RimPy.
- **Clear all cached data** — deletes all cache files. Next launch will rebuild from scratch.
- **Write diagnostic snapshot** — developer tool for verifying cache correctness.

## How It Works

DefLoadCache uses [Prepatcher](https://steamcommunity.com/sharedfiles/filedetails/?id=3563469557)'s `[FreePatch]` API to inject IL instructions into RimWorld's mod loading pipeline at compile time (before the CLR verifies `Assembly-CSharp.dll`). This allows intercepting methods that are normally inaccessible to Harmony.

### Cache Pipeline

**First launch (cache miss):**
1. Compute a SHA256 fingerprint of the active mod list (packageIds, versions, file counts, DLL sizes)
2. No cache file matches → let RimWorld load normally
3. After patches are applied, stamp mod attribution on each def node, serialize the merged XML document to a gzipped file, then strip the attribution from the live document

**Subsequent launches (cache hit):**
1. Compute fingerprint — matches existing cache file
2. Skip `LoadModXML` entirely (return empty list)
3. Skip `ApplyPatches` — stream the cached document directly into the existing `XmlDocument`
4. Rebuild the `assetlookup` dictionary from embedded mod attribution data
5. `ParseAndProcessXML` runs normally on the cached document

### Cache Invalidation

The fingerprint includes:
- RimWorld version
- Per-mod: packageId, version, `Defs/` and `Patches/` file counts and sizes (respecting `LoadFolders.xml`), `Assemblies/*.dll` sizes
- Cache format version

Any change to any of these causes a cache miss and full rebuild.

### Safety

- Every public entry point is wrapped in `try/catch` — if anything throws, the game falls back to normal loading
- If the cached document is corrupted mid-load (after the point of no return), the error is logged and rethrown rather than silently producing bad state
- Corrupt cache files are automatically deleted
- The mod can be disabled at any time with zero side effects

## Correctness Verification

DefLoadCache includes a built-in diagnostic dump tool (Stage E). When enabled in settings, it writes a sorted snapshot of every loaded def (type, defName, mod, label) to a text file. Running this on both a cache-miss and cache-hit launch produces identical output (55,241 defs verified on a 576-mod list).

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
- ~~**Content-aware fingerprinting**~~ — **Done!** The fingerprint now includes file modification timestamps. Same-size content changes invalidate the cache automatically.
- **Binary cache format** — replace gzipped XML with a compact binary format (MessagePack or similar) for faster deserialization. Current cache-hit deserialization is ~2.3 seconds; a binary format could cut this significantly.
- **Parallel fingerprint hashing** — fingerprint computation is already parallelized across mods, but individual About.xml reads could be further optimized or eliminated.
- **`ErrorCheckPatches` skip on cache hit** — the 7.6 second gap between LoadModXML skip and ApplyPatches entry includes patch config validation that's unnecessary on cached launches.

### Potential future features
- **Deferred texture loading** — load textures in background threads while the user is at the main menu instead of blocking startup. Textures not needed until gameplay could be loaded on-demand, saving ~2-3 minutes of startup time.
- **Cached parsed Def objects (Tier 2)** — skip `ParseAndProcessXML` and cross-reference resolution entirely by caching the built `DefDatabase` object graph. This is where the remaining ~3 minutes lives, but requires careful Harmony patch invalidation tracking.
- **Prepatcher-based profiler with mod attribution** — a runtime profiler that uses Cecil to instrument Harmony-patched methods and attribute per-tick cost to specific mods. "Top 10 mods by tick cost" with drill-down to specific methods. Directly reuses the Prepatcher + Cecil infrastructure from DefLoadCache.
- **Incremental patching** — on mod list changes where only new patches are added (none removed/modified), apply only the new patches to the cached doc instead of re-running everything.

### The bigger vision

RimWorld mod loading is structurally identical to a compiler pipeline: source files (mod XML) are parsed, merged, transformed (patches), and compiled into runtime objects (DefDatabase). DefLoadCache is Phase 1 of a broader **modlist compiler** project — caching the intermediate representation. Future phases would add profiling, static analysis, and optimization passes using the same Prepatcher + Cecil foundation.

## License

MIT

## Credits

- **FluxxField** — author
- **[Prepatcher](https://steamcommunity.com/sharedfiles/filedetails/?id=3563469557)** by jikulopo — the `[FreePatch]` IL injection system that makes this possible
- **[Krafs.Publicizer](https://github.com/krafs/Publicizer)** — compile-time publicizer for accessing internal Cecil types
- **[Performance Fish](https://github.com/bbradson/Performance-Fish)** by bbradson — inspiration for the Publicizer + HintPath pattern
