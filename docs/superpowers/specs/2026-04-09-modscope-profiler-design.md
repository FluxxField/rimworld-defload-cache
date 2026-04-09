# ModScope: Profiler — Design Spec

## Goal

A RimWorld runtime profiler mod that automatically attributes tick cost to specific mods and presents a live "top 10 mods by tick cost" scoreboard with drill-down to individual methods and Harmony patches. Fills a gap no existing mod covers: answering "which of my 200+ mods is killing performance?" without manual investigation.

## Context

ModScope: Profiler is Phase 2 of the ModScope suite (Phase 1 = DefLoadCache, shipped). It is a separate mod, separate repo, separate Workshop page. It shares the Prepatcher + Cecil foundation with DefLoadCache but has no code dependency on it.

The profiler is the "eyes" of the suite. It produces data that future ModScope tools consume:
- **ModScope: Analyzer** (Phase 3) — reads profiler exports, detects optimization opportunities
- **ModScope: Optimizer** (Phase 4) — reads analyzer reports, applies Cecil rewrites

Data flows one direction: Profiler → Analyzer → Optimizer. The Profiler never depends on downstream tools.

## Competitive Landscape

| Capability | Dub's Perf Analyzer | RocketMan | Perf Fish | **ModScope: Profiler** |
|---|---|---|---|---|
| Method-level profiling | Yes | No | No | Yes |
| Per-mod cost roll-up | No (manual) | No | No | **Automatic** |
| Top-N mod ranking | No | No | No | **Yes** |
| Harmony patch attribution | Partial | No | No | **Automatic** |
| Transpiler separation | No (collapsed) | No | No | **Yes** |
| Export for tooling | No | No | No | **Yes** |

## Architecture

Three layers:

### 1. Instrumentation Layer (Cecil via `[FreePatch]`)

Injects `Stopwatch` timing IL into tick pipeline entry points at load time via Prepatcher's `[FreePatch]`. Each injected site records elapsed ticks into a pre-allocated ring buffer keyed by method + calling assembly.

A `static bool ProfilingEnabled` guard at each injection site allows the master switch to disable collection with ~1ns overhead (single branch instruction).

**MVP instrumented methods:**
- `Pawn.Tick`
- `Thing.Tick`
- `MapComponentTick`
- `WorldComponentTick`

**Future instrumentation roadmap:**

v1.1 — Update/render pipeline:
- `ThingOverlays.ThingOverlaysOnGUI`
- `MapDrawer.MapMeshDrawerUpdate`
- `DynamicDrawManager.DrawDynamic`

v1.2 — Known bottlenecks:
- `PathFinder.FindPath`
- `GenSight.LineOfSight`
- `RegionTraverser`
- `StatWorker.GetValue`
- `GenClosest.ClosestThingReachable`

v1.3 — Job/AI pipeline:
- `JobGiver` subclasses
- `ThinkNode.TryIssueJobPackage`
- `Toils_*`

v1.4 — Harmony overhead isolation:
- Instrument the Harmony trampoline to measure dispatch overhead vs patch body cost

**Injection pattern:**

```
// Injected at method entry:
if (!ProfilingEnabled) goto originalBody;
stopwatch = Profiler.StartTiming(methodId);

// Original method body

// Injected before each ret:
Profiler.StopTiming(methodId, stopwatch, callingAssembly);
```

**Difference from DPA:** DPA uses Harmony transpilers to replace `Call`/`CallVirt` IL with profiling wrappers. ModScope uses Cecil `[FreePatch]` which runs before Harmony, avoids Harmony dispatch overhead in measurements, and doesn't interfere with existing patch chains.

### 2. Attribution Layer (built at startup)

Two data sources combined:

**Assembly → Mod mapping:**
```csharp
var assemblyToMod = new Dictionary<Assembly, ModContentPack>();
foreach (var mod in LoadedModManager.RunningModsListForReading)
{
    foreach (var assembly in mod.assemblies.loadedAssemblies)
    {
        assemblyToMod[assembly] = mod;
    }
}
```

**Harmony patch registry:**
For each instrumented method, query `Harmony.GetPatchInfo(method)` to get the list of prefixes, postfixes, and transpilers with their owner assemblies and priorities.

Combined attribution: any code executing during a tick gets attributed to the mod that owns the assembly it lives in. Harmony patches are additionally tagged with their patch type and priority for the drill-down view.

### 3. Presentation Layer

**IMGUI Overlay (hotkey toggle, default F10):**

Floating semi-transparent panel showing:
- Total tick time and TPS
- Top N mods ranked by ms/tick (configurable: 5/10/15/20)
- Trend arrows (▲▼─) comparing last 1s vs previous 1s
- Export and Settings buttons
- Click any mod row to open drill-down window

Updates every 60 ticks (once per second). Draggable, resizable.

**Native RimWorld Window (drill-down):**

Opens on mod click. Two tabs:

Methods tab: per-method breakdown with ms/tick, calls/tick, peak ms, sorted by cost.

Patches tab: list of all Harmony patches this mod has registered, with target method, type (prefix/postfix/transpiler), and priority.

## Data Collection

**Ring buffer:**
- Per-method `long[]` array, 600 entries (10 seconds at 60 TPS)
- Stores raw `Stopwatch.ElapsedTicks` (converted to ms on read)
- Pre-allocated at startup, zero per-tick allocations
- Thread-safe via `Interlocked.Increment` on write index

**ModProfile aggregation:**
- Total ms/tick (rolling average over ring buffer)
- Per-method breakdown sorted by cost
- Per-patch breakdown within each method
- Call count per tick
- Peak ms (highest single-tick cost in buffer)
- Trend (latest 60 ticks vs previous 60)

**Performance budget:**
- Target: <1% TPS impact when collecting, <0.1% when disabled
- ~20ns per `Stopwatch.Start/Stop` × 12,000 calls/sec (200 pawns × 60 TPS) = ~0.24ms/sec overhead
- IMGUI overlay redraws every 60 ticks only
- If overhead exceeds budget, fall back to sampling (every Nth tick)

## Export Format

Triggered by button in overlay or settings. Writes timestamped JSON to `<RimWorld save data>/ModScope/profiles/`.

```json
{
  "timestamp": "2026-04-09T12:00:00Z",
  "rimworldVersion": "1.6.4633",
  "modCount": 576,
  "ticksProfiled": 600,
  "averageTPS": 54.2,
  "totalMsPerTick": 18.4,
  "fingerprint": "<sha256 modlist fingerprint>",
  "mods": [
    {
      "packageId": "ceteam.combatextended",
      "name": "Combat Extended",
      "totalMsPerTick": 4.21,
      "percentOfTotal": 22.9,
      "trend": "up",
      "methods": [
        {
          "method": "Verse.Pawn_HealthTracker::HealthTick",
          "msPerTick": 2.10,
          "callsPerTick": 120,
          "peakMs": 0.04,
          "patches": [
            {
              "type": "Prefix",
              "owner": "ceteam.combatextended",
              "ownerName": "Combat Extended",
              "priority": 400
            }
          ]
        }
      ]
    }
  ]
}
```

The `fingerprint` field uses the same algorithm as DefLoadCache, linking profiling data to a specific modlist configuration. This is the data contract that ModScope: Analyzer will consume.

## Settings

Access via **Options → Mod Settings → ModScope: Profiler**:

- **Enable profiling** — master switch. When off, instrumentation is injected but the static bool guard skips all timing. Near-zero overhead. Default: on.
- **Overlay hotkey** — key to show/hide the live scoreboard. Default: F10.
- **Overlay position** — top-right, top-left, bottom-right, bottom-left. Default: top-right.
- **Update interval** — how often the overlay refreshes: every 30/60/120 ticks. Default: 60.
- **Mods shown in overlay** — 5/10/15/20. Default: 10.

## Project Identity

- **Mod name:** ModScope: Profiler
- **PackageId:** fluxxfield.modscope.profiler
- **Author:** FluxxField
- **Repository:** FluxxField/rimworld-modscope-profiler (new repo)
- **License:** MIT
- **Dependencies:** Prepatcher (required)
- **Load after:** Prepatcher, Harmony

## File Structure

```
About/
  About.xml
  Preview.png
Assemblies/
  ModScopeProfiler.dll
src/
  Prepatcher/           — [FreePatch] IL injection into tick methods
  Instrumentation/      — Stopwatch wrappers, ring buffers, ProfilingEnabled guard
  Attribution/          — Assembly→Mod mapping, Harmony registry walk
  UI/                   — IMGUI overlay, native drill-down Window
  Export/               — JSON serialization, file management
  Settings/             — Mod + ModSettings subclasses
  Log.cs                — Prefixed logger ([ModScope] prefix)
```

## Safety

- **Rule Zero:** every entry point in `try/catch`. Failure = profiler silently disables, game continues normally.
- **Read-only:** profiler never mutates game state. Pure observation.
- **No save interaction:** safe to add/remove mid-playthrough, any time.
- **Graceful degradation:** if Cecil injection fails for a method (signature changed in RimWorld update), that method is skipped with a log warning. Other methods still profiled.
- **Master switch:** static bool guard. Disabled = one branch instruction per call (~1ns). No runtime cost.

## Non-Goals (MVP)

- Optimization recommendations (ModScope: Analyzer)
- Automatic code rewrites (ModScope: Optimizer)
- Profiling of LoadModXML / ApplyPatches (DefLoadCache territory)
- Profiling texture/audio loading
- Multi-threaded profiling
- Save game analysis
