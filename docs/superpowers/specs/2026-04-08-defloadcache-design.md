# DefLoadCache — Design

**Status:** Approved for plan-writing
**Date:** 2026-04-08
**Project:** rimworld-defload-cache (standalone — not part of the MilSim Compatibility Patches umbrella)

## Context

A heavily modded RimWorld (500+ mods) takes ~10 minutes to launch. Most of that time is spent in `Verse.LoadedModManager.ApplyPatches`, which walks the merged def tree applying every mod's `<Patch>` operations via XPath. The XPath walk is O(patches × defs) — for 500 mods that's tens of millions of operations, and it's deterministic: the inputs are identical every launch, but RimWorld redoes all the work from scratch every time.

This is the largest pure-waste component of modded RimWorld startup. Several other optimizations exist (Prepatcher, Performance Optimizer, Performance Fish), but none of them cache the post-patch def tree to disk.

## Problem (specific)

Repeat launches of the same modlist redo identical XML parse + patch work even though the inputs haven't changed. There is no built-in mechanism to skip the work. Mod authors can't intervene at the right time because regular `Mod` class constructors run AFTER the load is complete — by the time mod code can execute, the slow work is already done.

The only intervention point that runs early enough to short-circuit the loader is **Prepatcher's `[FreePatch]` mechanism**, which rewrites Assembly-CSharp's IL before the CLR verifies and loads it.

## Goals

- Build a Tier 1 proof-of-concept that caches the **merged post-patch `XmlDocument`** to disk and skips `ApplyPatches` on cache-hit launches.
- Prove that the FreePatch + managed-hook architecture works on RimWorld 1.6 with the user's installed Prepatcher version.
- Measure the actual speedup so the user can decide whether to graduate the work toward a public release (Approach C) or sit on it as a personal tool.
- Never crash the game and never silently corrupt def state. On any failure, fall back to normal loading.

## Non-goals (scope discipline for the POC)

- **Multi-version RimWorld support.** 1.6 only. Not 1.5, not 1.7.
- **Public release polish.** No settings UI, no in-game cache management button, no Steam Workshop publication.
- **Compatibility with arbitrary mods.** Tested only against a curated minimal modlist.
- **Bulletproof invalidation.** The fingerprint catches the common cases; rare edge cases (in-place file edits that preserve file count and total size) are tolerated. User can force-invalidate by deleting the cache folder.
- **Caching beyond `ApplyPatches`.** `LoadModXML` and `CombineIntoUnifiedXML` still run on cache-hit launches. Hooking them is a follow-up if Stage F's speedup is unsatisfying.
- **Long-term save game safety.** "Doesn't immediately crash" is the bar; playing a long game on cached defs is out of scope for the POC validation pass.
- **Caching parsed Def objects (Tier 2/3).** Cached state stops at the post-patch XmlDocument. `ParseAndProcessXML` and cross-reference resolution still run normally on every launch — that's where Harmony-patched-def compatibility lives.

## Architecture

### Mod identity

- **Mod name:** DefLoadCache
- **packageId:** `fluxxfield.defloadcache`
- **Author:** FluxxField
- **Target RimWorld version:** 1.6 only
- **Hard dependencies:**
  - **Prepatcher** (`jikulopo.prepatcher`) — provides `[FreePatch]`, bundles Harmony 2.4.2 + PrepatcherAPI 1.2.0 + Mono.Cecil
  - Core RimWorld 1.6
- **Load order:** after Prepatcher, before everything else. The whole point is to modify Assembly-CSharp before any other mod observes the loaded state.

### Build toolchain

- **.NET SDK:** dotnet 8.0.419, installed via the official `dotnet-install.sh` script in WSL at `~/.dotnet`
- **Project target:** `net48` (RimWorld's Mono runtime is .NET Framework 4.8 compatible). dotnet 8 cross-compiles to `net48` without issue.
- **Reference assemblies** (private, hint-pathed in `.csproj`, never committed to git):
  - `Assembly-CSharp.dll` — `/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/RimWorldWin64_Data/Managed/`
  - `UnityEngine.CoreModule.dll` — same folder
  - `0Harmony.dll`, `0PrepatcherAPI.dll` — from Prepatcher's workshop folder `Assemblies/` directory
- **Build output:** single `DefLoadCache.dll` written to the mod's `Assemblies/` folder

### Single-assembly design

Prepatcher loads our `DefLoadCache.dll` during its rewriting phase, scans it for `[FreePatch]` attributes, and invokes those methods. Those same methods inject IL that calls back into other classes in **the same DLL**. When Assembly-CSharp runs and hits our injected call, the CLR resolves it to managed code in the now-loaded `DefLoadCache.dll`. We do NOT need a separate "Cecil-side early" assembly and "managed-side late" assembly — one DLL is enough.

### Component topology

```
DefLoadCache/
├── src/
│   ├── Prepatcher/
│   │   └── IlInjector.cs              # [FreePatch] method. ~40 lines of Mono.Cecil.
│   │                                   # Finds Verse.LoadedModManager.ApplyPatches,
│   │                                   # inserts call to CacheHook.TryLoadCached at
│   │                                   # the start (with brtrue.s skip), and a call
│   │                                   # to CacheHook.SaveToCache at the end. The
│   │                                   # ONLY file that touches Cecil.
│   │
│   ├── Hook/
│   │   └── CacheHook.cs                # Static class. Public entry points called
│   │                                   # from injected IL:
│   │                                   #   - TryLoadCached() : bool  (prefix)
│   │                                   #   - SaveToCache()   : void  (postfix)
│   │                                   # Pure orchestration. Each method ~20 lines.
│   │
│   ├── Fingerprint/
│   │   └── ModlistFingerprint.cs       # Computes a stable, fast hash of the input
│   │                                   # set: mod packageIds + versions + Defs/
│   │                                   # Patches/ file counts + total bytes + load
│   │                                   # order + RimWorld version. SHA256.
│   │
│   ├── Cache/
│   │   ├── CacheStorage.cs             # Disk I/O. Reads/writes cache files in
│   │   │                               # %LocalAppDataLow%\Ludeon Studios\
│   │   │                               # RimWorld by Ludeon Studios\DefLoadCache\
│   │   └── CacheFormat.cs              # Serialization. XmlDocument <-> compressed
│   │                                   # bytes. System.IO.Compression.GZipStream.
│   │
│   ├── Reflection/
│   │   └── LoaderState.cs              # Reads/writes the private merged-doc field
│   │                                   # on Verse.LoadedModManager. Isolated so the
│   │                                   # rest of the code works with plain
│   │                                   # XmlDocument and doesn't know about
│   │                                   # RimWorld's private state.
│   │
│   └── Log.cs                          # Verse.Log wrapper with [DefLoadCache]
│                                       # prefix on every message.
│
├── DefLoadCache.csproj                 # dotnet 8, targets net48
├── About/
│   ├── About.xml                       # Mod metadata
│   └── Manifest.xml                    # Optional, for RimPy
├── Assemblies/                         # Build output (DefLoadCache.dll lands here)
├── .gitignore                          # bin/, obj/, *.user, /Assemblies/*.dll
└── docs/superpowers/{specs,plans}/
```

**Why this decomposition:**

- `IlInjector.cs` is the only file that touches Mono.Cecil. Small, boring, unlikely to change. Blast radius for Cecil API quirks is one file.
- `CacheHook.cs` is pure orchestration: read like a recipe, easy to reason about.
- `ModlistFingerprint.cs` is the most likely source of correctness bugs. Isolated so it's testable in pure-data mode without launching the game.
- `CacheStorage` + `CacheFormat` split separates "where files live" from "how they're encoded."
- `LoaderState.cs` isolates ALL reflection against RimWorld's private state. If RimWorld renames the field in 1.7, exactly one file changes.
- `Log.cs` catches the "where do log lines go" question once.

**Total: 7 source files. POC scope, no test project — validation happens via in-game launches.**

## Cache lifecycle

### Hook point: `Verse.LoadedModManager.ApplyPatches` only

Confirmed via `strings` scan of `Assembly-CSharp.dll`:
- `Verse.LoadedModManager.LoadModXML` exists
- `Verse.LoadedModManager.CombineIntoUnifiedXML` exists
- `Verse.LoadedModManager.ApplyPatches` exists ← our target
- `Verse.LoadedModManager.ParseAndProcessXML` exists
- `Verse.PlayDataLoader.DoPlayLoad` is the top-level entry

We hook `ApplyPatches` only. On cache-hit, `LoadModXML` and `CombineIntoUnifiedXML` STILL RUN — we accept that cost. `ApplyPatches` is the single largest contributor to load time and the simplest state surface to intercept. If Stage F shows the speedup is too small, we'd hook earlier in a follow-up.

### Write path (cache miss)

```
[normal LoadModXML runs]                    # not touched
[normal CombineIntoUnifiedXML runs]         # not touched
[normal ApplyPatches runs to completion]    # postfix fires AFTER
  └── CacheHook.SaveToCache()
        ├── Compute fingerprint (cached from earlier TryLoadCached call)
        ├── LoaderState.GetMergedDoc() → XmlDocument
        ├── CacheFormat.Serialize(doc)     # XmlDocument → byte[] via GZipStream
        ├── CacheStorage.Write(fingerprint, bytes)
        │     ├── Write to <fingerprint>.xml.gz.tmp
        │     ├── Write <fingerprint>.meta.json sidecar
        │     └── Atomic File.Move .tmp → .xml.gz
        └── CacheStorage.Prune()            # keep 3 most-recent .xml.gz files
```

### Read path (cache hit)

```
[normal LoadModXML runs]
[normal CombineIntoUnifiedXML runs]
[ApplyPatches called]                       # prefix fires at entry
  └── CacheHook.TryLoadCached()
        ├── Compute fingerprint (store in static for postfix reuse)
        ├── If <fingerprint>.xml.gz absent → return false
        ├── CacheStorage.Read(fingerprint) → byte[]
        ├── CacheFormat.Deserialize(bytes) → XmlDocument
        ├── LoaderState.SetMergedDoc(doc)   # write into LoadedModManager's field
        ├── Log "cache HIT, skipped ApplyPatches"
        └── Return true                     # injected brtrue.s branches to ret
[normal ParseAndProcessXML runs]            # reads our cached doc
[normal cross-reference resolution runs]
```

The critical insight: cache-hit doesn't just SKIP `ApplyPatches`, it **replaces** the working document with the post-patch cached version. From `ParseAndProcessXML`'s perspective, nothing looks different — the doc came from the same field it always reads. Only the path to that field changed.

### Fingerprint inputs

Concatenated in deterministic (sorted) order, then SHA256 hashed.

| Input | Source | Why |
|---|---|---|
| RimWorld version | `VersionControl.CurrentVersionString` | Different versions parse XML differently |
| Active DLC list | `ModLister.AllInstalledMods` filtered active | DLC presence changes which defs exist |
| Per active mod (load order): `packageId` | `ModContentPack.PackageId` | Mod added/removed |
| Per active mod: `<modVersion>` from About.xml | parsed from About.xml | Mod updated |
| Per active mod: file count + total bytes of `Defs/` (recursive) | filesystem walk | Mod updated silently without bumping version |
| Per active mod: file count + total bytes of `Patches/` (recursive) | filesystem walk | Same |
| DefLoadCache cache format version | `CacheFormat.Version` constant | Invalidate when WE change format |

**Trade-offs deliberately accepted:**

- **No file content hashing.** Hashing 500 mods' XML content takes 30-60 seconds, defeating the POC's purpose. We use `(file count + total bytes)` as a structural proxy. Misses in-place edits that preserve both — rare in practice.
- **No mtime hashing.** Spurious invalidation from `touch`, git checkouts, Steam Cloud sync, backup software. Worse UX than missing the rare edge case.
- **No mod settings hashing.** Most mod settings don't affect def loading; the few that do are uncommon enough to tolerate.

**Speed target:** fingerprint computation completes in <5 seconds on a 500-mod load order. If it takes longer, the POC is broken — log the time at every launch in Stage B.

### Storage location

```
%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\DefLoadCache\
├── e4a1b7c9f2d83e4b.xml.gz            # current cache (gzipped XmlDocument bytes)
├── e4a1b7c9f2d83e4b.meta.json         # diagnostic sidecar
├── 3b2c1a9d8e7f6c5b.xml.gz            # previous cache
├── 3b2c1a9d8e7f6c5b.meta.json
├── 1a2b3c4d5e6f7a8b.xml.gz            # oldest kept
└── 1a2b3c4d5e6f7a8b.meta.json
```

- **Why this folder:** RimWorld's persistent user data dir. User-writable without admin. Backed up with the rest of RimWorld config.
- **Pruning rule:** keep 3 most-recent `.xml.gz`, delete older. Three gives a small rollback buffer for users toggling between modlists.
- **Size budget:** 10-50 MB per cache file expected. 3 files = 30-150 MB total.
- **`.meta.json` contents:** timestamp, mod count, RimWorld version, cache format version. Purely diagnostic — the cache LOOKUP uses the filename hash, so a corrupt or missing JSON doesn't break loading.

### Atomic write (crash safety)

Standard write-temp-then-rename pattern:

```csharp
var tmp = cachePath + ".tmp";
using (var fs = File.Create(tmp))
using (var gz = new GZipStream(fs, CompressionMode.Compress))
    doc.Save(gz);
File.Move(tmp, cachePath);    // atomic on NTFS for same-volume rename
```

If the game crashes between `File.Create` and `File.Move`, the `.tmp` file is orphaned but the previous `.xml.gz` is untouched. Orphaned `.tmp` files get swept on the next successful save.

### Invalidation scenarios

| Scenario | Detection | Behavior |
|---|---|---|
| Mod added/removed | Fingerprint includes mod list | Cache miss → normal load → save new cache |
| Mod updated (version bump) | Fingerprint includes `<modVersion>` | Cache miss → reload → save new cache |
| Mod file edited (changed file count or total bytes) | Fingerprint includes structural counts | Cache miss |
| RimWorld version bump | Fingerprint includes `VersionControl.CurrentVersionString` | All caches stale, get pruned over time |
| Cache file corrupted on disk | Exception during deserialize | Catch, delete bad file, cache miss, normal load |
| Cache format version bump | Fingerprint includes our version constant | All old caches stale, auto-pruned |
| User wants to force regenerate | Manual delete of cache folder | Next launch is a clean cache miss |

### Fallback path — RULE ZERO

**Never crash the game because of our code.** Every public entry point (`TryLoadCached`, `SaveToCache`) is wrapped in try/catch at the outermost level. On any exception:

1. Log via `Verse.Log.Error` with `[DefLoadCache]` prefix
2. In `TryLoadCached`: return `false`, normal `ApplyPatches` runs
3. In `SaveToCache`: swallow, continue (defs are already loaded)
4. If a cache file is corrupt: delete it so we don't repeatedly try to read it

**Degraded mode = "as if DefLoadCache wasn't installed."** The user sees a longer load time and red text in the log, but the game loads normally and saves are safe.

**This line cannot be crossed:** any failure mode that crashes the game OR silently corrupts the def database is unacceptable, even for a POC.

## Stages A-F

Each stage is a separate commit boundary with an independently verifiable success criterion. Stages run in order; failure of one stage blocks moving to the next until resolved.

| Stage | Deliverable | Success criterion | Primary failure mode |
|---|---|---|---|
| **A** Empty plumbing | `IlInjector.cs` finds `Verse.LoadedModManager.ApplyPatches`, injects ONE call to `CacheHook.HookFired()`, which calls `Verse.Log.Message("[DefLoadCache] hook fired")`. No cache logic. Build, deploy, launch on test modlist. | Log line appears in `Player.log`. | Prepatcher's `[FreePatch]` doesn't recognize our attribute, IL injection silently fails, or we target the wrong method. **Tells us the architecture isn't viable.** |
| **B** Fingerprint | `ModlistFingerprint.Compute()` walks active mods, hashes inputs into a SHA256 hex string. Hook logs the result. Still no cache. | (1) Hash <5 sec on test modlist. (2) Identical across two consecutive launches. (3) Changes when a mod is enabled/disabled. | Hash unstable (hidden non-determinism) or too slow. Recoverable. |
| **C** Cache write | `SaveToCache()` postfix reads merged doc via `LoaderState`, gzip-serializes, atomic-writes. | (1) Cache file exists after first launch. (2) Size 5-50 MB. (3) Reading back with `gzip -d` produces valid XML. (4) `.meta.json` parses. | `LoaderState` field name wrong, or serialization throws. Recoverable. |
| **D** Cache read | `TryLoadCached()` prefix deserializes cached doc, writes into `LoadedModManager` field, returns true. Original `ApplyPatches` body skipped. | (1) Second launch hits cache. (2) Game reaches main menu. (3) Test colony spawns, no missing-def warnings. | Cached doc loaded but `ParseAndProcessXML` chokes, or downstream consumers read DIFFERENT state we didn't populate. Hardest stage. |
| **E** Correctness | After `ParseAndProcessXML`, dump per-type `DefDatabase<T>` counts + sample field checksums to a diagnostic log. Run twice (cache disabled, then enabled), diff. | Counts match exactly. Sampled checksums match (excluding known non-deterministic fields). | Round-trip lossy somewhere. Recoverable by adjusting serialization or hooking at a different stage. |
| **F** Benchmark | Wrap `ApplyPatches` with `Stopwatch` in prefix and postfix. Log `"ApplyPatches: <ms>ms (cache HIT/MISS)"`. Run 3x each, mean. | **POC viability bar: ≥50% reduction in mean `ApplyPatches` wall-clock on test modlist.** Or ≥5 seconds absolute savings. | Speedup small or negative. Means hook point was wrong. Recoverable by hooking earlier (Stage G). |

### Test modlist (for stages A-F)

A **minimal test profile** of ~15 mods, NOT the full 576-mod load order:

- Core + Royalty + Ideology + Biotech + Anomaly + Odyssey (whichever DLCs are active)
- Harmony
- HugsLib
- JecsTools (only if a tested mod requires it)
- **Prepatcher** (REQUIRED — provides our hook mechanism)
- Combat Extended + CE Armors
- Packs Are Not Belts
- The Dead Man's Switch (base only — not the 9 expansion modules)
- Ancient urban ruins
- Ratnik-3 Prototype Armor
- No Version Warning
- **DefLoadCache** (this mod)
- **MilSim Compatibility Patches** (the user's other mod — useful because it exercises the patches phase)

**Why minimal:** iteration loop is ~30-60 seconds per launch instead of 10 minutes. Affordable for stage A debugging where we may need 20 launches in a day. Still triggers `ApplyPatches` (CE + PANB + MilSim ship patches). Still measurable speedup.

**Stretch validation after Stage F:** ONE launch of the full 576-mod modlist as a smoke test. If it crashes, triage compat. If it works, informal confirmation that the speedup scales.

### POC overall success criteria

After Stage F completes, the POC is **viable** if all three hold:

1. Stages A-D pass (proves the FreePatch + managed-hook architecture works on RimWorld 1.6)
2. Stage E correctness holds (cache-hit `DefDatabase` matches cache-miss within tolerance)
3. Stage F shows ≥50% reduction in `ApplyPatches` wall-clock on the test modlist

If all three pass: there is concrete evidence the approach is technically achievable, and we have a working POC. The user can then **decide** whether to graduate toward Approach C (public release with bulletproof invalidation) or sit on it as a personal tool.

If any stage fails: each failure mode has a clear next investigation. No state where "we worked for two weeks and don't know what to do next."

## Risks

1. **The jikulopo Prepatcher fork may behave differently from Zetrith's original.** The wiki I read (https://github.com/Zetrith/Prepatcher/wiki/Free-patching) describes the original. The fork *might* have changed the FreePatch attribute signature, the Cecil version, or the execution timing. **Stage A surfaces this immediately** — if the hook never fires, this is the first thing to investigate.

2. **RimWorld 1.6 loader internals may differ from 1.4/1.5.** The method names confirmed via `strings` scan match the historical names, but their internal behavior could have changed (e.g., the merged doc might no longer live in a single private field). **Stage C surfaces this.**

3. **Lossy `XmlDocument` round-trip.** RimWorld's loader may use XmlDocument features that don't survive `XmlWriter.Save` and `XmlReader.Read` (CDATA sections, processing instructions, namespace declarations, comments at unusual positions, etc.). **Stage E is designed to detect this.**

4. **Speedup is smaller than expected.** If `ApplyPatches` is only 30% of the def-load phase rather than 60%, the POC's bar of "≥50% reduction in `ApplyPatches`" may not translate to a meaningful total-load-time speedup. **Stage F's benchmark settles this.**

5. **`LoadedModManager`'s merged-doc storage may not be a single field.** It might be a method-local variable in `ApplyPatches`, in which case our reflection approach fails. We'd have to use Cecil to capture the value at a different point. **Stage C surfaces this.**

6. **Mono.Cecil version conflicts.** Prepatcher bundles a specific Mono.Cecil version, and our `[FreePatch]` method needs to use that exact version. If we accidentally reference a different version (e.g., from Mono.Cecil NuGet package), runtime type mismatch. Mitigation: reference the bundled `0PrepatcherAPI.dll` which exposes the right Cecil types.

## Open questions to resolve during plan-writing

1. **Exact FreePatch API surface for the jikulopo fork.** The wiki documents `static void Patch(ModuleDefinition module)`. We assume that holds for the fork. Verify by reading `0PrepatcherAPI.dll` symbols (or the GitHub source) before writing the plan's first task.

2. **The exact name of the merged-doc field on `LoadedModManager`.** Confirmed methods exist; the field that holds the working merged XmlDocument needs to be located by inspecting `Assembly-CSharp.dll` (probably via `ilspycmd` or similar). Alternative: use Cecil to inject a capture point that intercepts the doc as it's passed between methods.

3. **Whether `ApplyPatches` is `static` or `instance`.** Affects the IL injection: `call` vs. `callvirt`, and whether the injected hook needs an instance reference.

4. **How `Verse.Log.Message` behaves at the exact moment our prefix runs.** If `Log` isn't fully initialized yet, our prefix's logging may itself crash. Mitigation: defer `Log.Message` calls behind a guard that catches and writes to `Console.WriteLine` if `Log` isn't ready.

5. **`DefDatabase<T>.AllDefs.Count` vs. `Count` of every loaded def type.** Stage E needs a stable, comprehensive enumeration. Verify this enumerable is stable across runs before relying on it for correctness comparison.

6. **`LoaderState` reflection target — `BindingFlags.NonPublic | BindingFlags.Static` vs `Instance`?** Determined by whether the field is static or instance. Inspect first.

## Validation approach

Stage-by-stage in-game testing on the minimal test modlist. No unit test project for the POC because the only meaningful test environment is a running RimWorld instance — pure-data unit tests for `ModlistFingerprint` are a candidate for later but not required for POC viability.

For Stage E specifically, the validation diagnostic is:

```csharp
[StaticConstructorOnStartup]
public static class DiagnosticDump
{
    static DiagnosticDump()
    {
        var sb = new StringBuilder();
        foreach (var defType in typeof(Def).AllSubclassesNonAbstract().OrderBy(t => t.FullName))
        {
            var dbType = typeof(DefDatabase<>).MakeGenericType(defType);
            var allDefs = (IEnumerable)dbType.GetProperty("AllDefs").GetValue(null);
            var count = allDefs.Cast<object>().Count();
            sb.AppendLine($"{defType.FullName}\t{count}");
            // sample 10 defs, hash their salient fields
            // ...
        }
        File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, "DefLoadCache_diag.txt"), sb.ToString());
    }
}
```

This runs after the load completes. Diff two consecutive runs (one cache-miss, one cache-hit) to verify `DefDatabase` equivalence.

## Out of scope (cross-references)

This project is intentionally narrow. The following are explicitly NOT this POC's job:

| Excluded | Status |
|---|---|
| Caching `LoadModXML` and `CombineIntoUnifiedXML` outputs | Possible Stage G if Stage F speedup is unsatisfying |
| Tier 2/3 caching (parsed Def objects, post-cross-ref state) | Possible v2 if Tier 1 ships and is loved |
| Public Steam Workshop release | Possible after POC validates and bulletproof invalidation is built |
| Compatibility with arbitrary user modlists | Out of scope for POC; tested only on the curated test modlist |
| Settings UI / in-game cache management | Out of scope; manual delete-the-folder for now |
| Multi-version RimWorld support | 1.6 only; explicit non-goal |
| Save game safety beyond "doesn't immediately crash" | Out of scope; long-game testing not part of POC validation |
