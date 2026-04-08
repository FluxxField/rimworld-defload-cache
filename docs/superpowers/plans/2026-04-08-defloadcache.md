# DefLoadCache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Tier 1 POC of DefLoadCache — a Prepatcher-based mod that intercepts `Verse.LoadedModManager.ApplyPatches`, serializes the post-patch `XmlDocument` to disk, and short-circuits the XPath walk on cache-hit launches. Validate the speedup via Stages A through F.

**Architecture:** Single C# assembly targeting `net48`. A `[FreePatch]` method uses Mono.Cecil to inject IL into `ApplyPatches` that calls a managed hook in the same assembly. The hook checks a fingerprint-keyed disk cache and either short-circuits the original method body (cache hit) or runs it normally and saves the result afterward (cache miss). Mod attribution (`assetlookup`) is preserved by embedding source-mod packageId attributes on each top-level def node when caching, and rebuilding the lookup from those attributes when loading.

**Tech Stack:** .NET SDK 8 (cross-compile to net48), Mono.Cecil + MonoMod.Utils + HarmonyLib (all bundled in Prepatcher's `0Harmony.dll`), Prepatcher's `[FreePatch]` API, RimWorld 1.6 reference assemblies. Validation via in-game launches; no unit test project for the POC.

---

## Reference data — verified during plan writing

### Verified facts about the load pipeline (from decompiled `Assembly-CSharp.dll`)

```csharp
// Verse.LoadedModManager — public static class. All members are static.

public static void ApplyPatches(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
{
    foreach (PatchOperation item in runningMods.SelectMany((ModContentPack rm) => rm.Patches))
    {
        try { item.Apply(xmlDoc); }
        catch (Exception ex) { Log.Error("Error in patch.Apply(): " + ex); }
    }
}

public static XmlDocument CombineIntoUnifiedXML(List<LoadableXmlAsset> xmls, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
{
    XmlDocument xmlDocument = new XmlDocument();
    xmlDocument.AppendChild(xmlDocument.CreateElement("Defs"));
    foreach (LoadableXmlAsset xml in xmls)
    {
        // ... validation ...
        foreach (XmlNode childNode in xml.xmlDoc.DocumentElement.ChildNodes)
        {
            XmlNode xmlNode = xmlDocument.ImportNode(childNode, deep: true);
            assetlookup[xmlNode] = xml;                       // <-- nodes get mod attribution here
            xmlDocument.DocumentElement.AppendChild(xmlNode);
        }
    }
    return xmlDocument;
}

public static void ParseAndProcessXML(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup, bool hotReload = false)
{
    XmlNodeList childNodes = xmlDoc.DocumentElement.ChildNodes;
    // ...
    for (int i = 0; i < list.Count; i++)
    {
        if (list[i].NodeType == XmlNodeType.Element)
        {
            LoadableXmlAsset value = null;
            assetlookup.TryGetValue(list[i], out value);                 // <-- consults mod attribution
            XmlInheritance.TryRegister(list[i], value?.mod);             // <-- passes mod to inheritance
        }
    }
    // ... continues to load defs ...
}
```

**Critical implications:**

1. **The merged XmlDocument is a method local in `LoadAllActiveMods`, not a static field.** It's passed as a parameter to `ApplyPatches` and then to `ParseAndProcessXML`. **No reflection is needed** to access it — our hook receives it via the IL injection point.
2. **`assetlookup` is the second parameter to `ApplyPatches`.** Our hook also receives it.
3. **Replacing `xmlDoc`'s contents from a cached version makes `assetlookup` stale** (the cached XmlNode instances are not the original instances that were registered as keys). `ParseAndProcessXML` would then call `XmlInheritance.TryRegister(node, null)` for every node, losing mod attribution.
4. **Solution:** during cache WRITE, before serializing, walk every top-level def node and stamp it with an attribute `data-defloadcache-mod="<packageId>"` looked up from the existing `assetlookup`. During cache LOAD, walk the deserialized doc, read each node's `data-defloadcache-mod` attribute, look up the corresponding `LoadableXmlAsset` (or construct a synthetic one), and put it into `assetlookup` keyed by the new XmlNode reference. This rebuilds the lookup so `ParseAndProcessXML` works exactly as if the doc came from `CombineIntoUnifiedXML`.
5. **Mod fingerprinting** uses `LoadedModManager.RunningModsListForReading` which is a public static `List<ModContentPack>`. No reflection needed.

### Verified Prepatcher API (from decompiled `0PrepatcherAPI.dll` + `PrepatcherImpl.dll`)

```csharp
// 0PrepatcherAPI.dll
namespace Prepatcher;
[AttributeUsage(AttributeTargets.Method)]
public class FreePatchAttribute : Attribute { }

// PrepatcherImpl.dll line 2261-2273 — how Prepatcher invokes our methods
private static bool InvokePatcher(MethodInfo patcher, Mono.Cecil.ModuleDefinition moduleToPatch)
{
    try
    {
        object obj = patcher.Invoke(null, new object[1] { moduleToPatch });
        return obj == null || (bool)obj;
    }
    catch (Exception value) { /* ... */ }
}

// PrepatcherImpl.dll line 2295 — how Prepatcher finds patch methods
private static IEnumerable<MethodInfo> FindAllFreePatches(Assembly patcherAsm) =>
    from type in patcherAsm.GetTypes()
    where AccessTools.IsStatic(type)                                     // <-- type must be static
    from m in AccessTools.GetDeclaredMethods(type)
    where IsDefinedSafe<FreePatchAttribute>(m) || IsDefinedSafe<FreePatchAllAttribute>(m)
    select m;
```

**Concrete `[FreePatch]` requirements:**
- Must be a `static` method (any visibility)
- Must be in a `static` class
- Signature: takes one `Mono.Cecil.ModuleDefinition` parameter
- Returns `void` (treated as "modified") or `bool` (true = modified, false = not)
- Mono.Cecil and MonoMod.Utils are both bundled inside Prepatcher's `0Harmony.dll`, so referencing `0Harmony.dll` is sufficient at build time

### Verified Prepatcher's own usage example (`PrepatcherImpl.dll` line 606-619)

```csharp
public static class AssemblyLoadingFreePatch
{
    [FreePatch]
    private static void ReplaceAssemblyLoading(Mono.Cecil.ModuleDefinition module)
    {
        Mono.Cecil.TypeDefinition type = module.GetType("Verse.ModAssemblyHandler");
        Mono.Cecil.MethodDefinition methodDefinition = MonoMod.Utils.Extensions.FindMethod(type, "ReloadAll");
        foreach (Mono.Cecil.Cil.Instruction instruction in methodDefinition.Body.Instructions)
        {
            if (instruction.Operand is Mono.Cecil.MethodReference { Name: "LoadFrom" })
            {
                instruction.Operand = module.ImportReference(typeof(AssemblyLoadingFreePatch).GetMethod("LoadFile"));
            }
        }
    }
}
```

This is the canonical pattern we copy. Note `MonoMod.Utils.Extensions.FindMethod(type, "ReloadAll")` for finding methods by name.

### Verified workshop & install paths

- **RimWorld install:** `/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/`
- **Assembly-CSharp:** `/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/RimWorldWin64_Data/Managed/Assembly-CSharp.dll`
- **Local Mods folder:** `/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/` (writable from WSL — verified earlier)
- **Prepatcher workshop folder:** `/mnt/c/Program Files (x86)/Steam/steamapps/workshop/content/294100/3563469557/`
- **Prepatcher Assemblies (root, used for 1.6):** `/mnt/c/Program Files (x86)/Steam/steamapps/workshop/content/294100/3563469557/Assemblies/`
- **`0Harmony.dll`:** `/mnt/c/Program Files (x86)/Steam/steamapps/workshop/content/294100/3563469557/Assemblies/0Harmony.dll`
- **`0PrepatcherAPI.dll`:** `/mnt/c/Program Files (x86)/Steam/steamapps/workshop/content/294100/3563469557/Assemblies/0PrepatcherAPI.dll`
- **`UnityEngine.CoreModule.dll`:** `/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/RimWorldWin64_Data/Managed/UnityEngine.CoreModule.dll`
- **RimWorld user data folder (cache lives here):** `/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/`
- **Player.log location:** `/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`

### .NET toolchain

- `dotnet 8.0.419` SDK at `~/.dotnet`, on PATH via `~/.zshrc`
- `.NET 6.0.36` runtime also installed (for `ilspycmd`)
- `ilspycmd 8.2.0.7535` installed as a global tool at `~/.dotnet/tools/ilspycmd`

---

## Pre-flight: minimal test modlist setup

Before any task runs, the user needs to set up the test modlist profile in RimWorld. This is a one-time manual step:

1. Launch RimWorld with the current full modlist
2. From the Mods menu → "Save mod list" → name it `Full MilSim`
3. Disable everything except: Core, all enabled DLCs, Harmony, HugsLib, Prepatcher, Combat Extended, CE Armors, Packs Are Not Belts, The Dead Man's Switch (base only — disable the 9 expansion modules), Ancient urban ruins, Ratnik-3 Prototype Armor, No Version Warning, MilSim Compatibility Patches
4. From the Mods menu → "Save mod list" → name it `DefLoadCache POC Test`
5. Quit RimWorld
6. (DefLoadCache will be added in Task A6 below)

To switch back to your full modlist later: Mods menu → "Load mod list" → `Full MilSim`.

---

## File structure (everything that gets created)

```
rimworld-defload-cache/
├── About/
│   └── About.xml                                      # Task A2
├── DefLoadCache.csproj                                # Task A1
├── .gitignore                                         # Task A1
├── src/
│   ├── Log.cs                                         # Task A3
│   ├── Hook/
│   │   └── CacheHook.cs                               # Task A4 (stub) → A5 expanded → C/D/F expanded
│   ├── Prepatcher/
│   │   └── IlInjector.cs                              # Task A5 (Stage A version) → C/D expanded
│   ├── Fingerprint/
│   │   └── ModlistFingerprint.cs                      # Task B1
│   ├── Cache/
│   │   ├── CacheStorage.cs                            # Task C2
│   │   └── CacheFormat.cs                             # Task C1
│   ├── ModAttribution/
│   │   └── ModAttributionTagger.cs                    # Task C3 (write side) + Task D2 (read side)
│   └── Diagnostics/
│       └── DiagnosticDump.cs                          # Task E1
├── Assemblies/                                        # Build output (gitignored)
│   └── DefLoadCache.dll                               # generated by `dotnet build`
└── docs/
    └── superpowers/
        ├── specs/
        │   └── 2026-04-08-defloadcache-design.md      # already exists
        └── plans/
            └── 2026-04-08-defloadcache.md              # this file
```

---

## Stage A — Empty plumbing proof

Goal: prove that `[FreePatch]` finds our method, our IL injection hits `ApplyPatches`, and our managed hook fires when the game loads.

### Task A1: Create `.csproj` and `.gitignore`

**Files:**
- Create: `/home/keenan/github/rimworld-defload-cache/DefLoadCache.csproj`
- Create: `/home/keenan/github/rimworld-defload-cache/.gitignore`

- [ ] **Step 1: Write the `.csproj`**

Write `/home/keenan/github/rimworld-defload-cache/DefLoadCache.csproj` with EXACT contents:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>latest</LangVersion>
    <RootNamespace>FluxxField.DefLoadCache</RootNamespace>
    <AssemblyName>DefLoadCache</AssemblyName>
    <OutputPath>Assemblies\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="Assembly-CSharp">
      <HintPath>/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/RimWorldWin64_Data/Managed/Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/RimWorldWin64_Data/Managed/UnityEngine.CoreModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>/mnt/c/Program Files (x86)/Steam/steamapps/workshop/content/294100/3563469557/Assemblies/0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0PrepatcherAPI">
      <HintPath>/mnt/c/Program Files (x86)/Steam/steamapps/workshop/content/294100/3563469557/Assemblies/0PrepatcherAPI.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write the `.gitignore`**

Write `/home/keenan/github/rimworld-defload-cache/.gitignore` with EXACT contents:

```
# Build output
bin/
obj/
Assemblies/*.dll
Assemblies/*.pdb

# IDE
.vs/
.vscode/
*.user
*.suo
.idea/

# OS
.DS_Store
Thumbs.db
```

- [ ] **Step 3: Verify the .csproj parses cleanly**

Run:
```bash
cd /home/keenan/github/rimworld-defload-cache
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH:$HOME/.dotnet/tools"
dotnet restore DefLoadCache.csproj 2>&1 | tail -10
```
Expected: `Restored ... DefLoadCache.csproj`. No errors.

- [ ] **Step 4: Commit**

```bash
cd /home/keenan/github/rimworld-defload-cache
git add DefLoadCache.csproj .gitignore
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add csproj targeting net48 with RimWorld + Prepatcher refs"
```

---

### Task A2: Write `About/About.xml`

**Files:**
- Create: `/home/keenan/github/rimworld-defload-cache/About/About.xml`

- [ ] **Step 1: Create the About folder and write About.xml**

```bash
mkdir -p /home/keenan/github/rimworld-defload-cache/About
```

Write `/home/keenan/github/rimworld-defload-cache/About/About.xml` with EXACT contents:

```xml
<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <name>DefLoadCache</name>
  <author>FluxxField</author>
  <packageId>fluxxfield.defloadcache</packageId>
  <supportedVersions>
    <li>1.6</li>
  </supportedVersions>
  <description>POC: caches the merged post-patch XmlDocument from Verse.LoadedModManager.ApplyPatches to disk and short-circuits the XPath walk on cache-hit launches. Targets the largest single contributor to modded RimWorld load time.

Tier 1 cache (XmlDocument level only). LoadModXML and CombineIntoUnifiedXML still run on cache-hit. ParseAndProcessXML and cross-reference resolution always run normally.

Hard depends on Prepatcher (uses [FreePatch] to inject IL into ApplyPatches before the CLR verifies Assembly-CSharp).

Status: experimental POC. Brittle invalidation. Not for general use.</description>
  <modDependencies>
    <li>
      <packageId>jikulopo.prepatcher</packageId>
      <displayName>Prepatcher</displayName>
      <steamWorkshopUrl>steam://url/CommunityFilePage/3563469557</steamWorkshopUrl>
    </li>
  </modDependencies>
  <loadAfter>
    <li>jikulopo.prepatcher</li>
    <li>brrainz.harmony</li>
  </loadAfter>
</ModMetaData>
```

- [ ] **Step 2: Verify XML well-formedness**

```bash
python3 -c "import xml.etree.ElementTree as ET; ET.parse('/home/keenan/github/rimworld-defload-cache/About/About.xml'); print('OK')"
```
Expected: `OK`

- [ ] **Step 3: Commit**

```bash
cd /home/keenan/github/rimworld-defload-cache
git add About/About.xml
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add About.xml with mod metadata and Prepatcher dependency"
```

---

### Task A3: Write `src/Log.cs`

**Files:**
- Create: `/home/keenan/github/rimworld-defload-cache/src/Log.cs`

- [ ] **Step 1: Create the src folder and write Log.cs**

```bash
mkdir -p /home/keenan/github/rimworld-defload-cache/src
```

Write `/home/keenan/github/rimworld-defload-cache/src/Log.cs` with EXACT contents:

```csharp
using System;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Thin wrapper around Verse.Log that prefixes every message with [DefLoadCache]
    /// and falls back to Console.WriteLine if Verse.Log is not yet initialized.
    /// </summary>
    internal static class Log
    {
        private const string Prefix = "[DefLoadCache] ";

        public static void Message(string msg)
        {
            try { Verse.Log.Message(Prefix + msg); }
            catch { Console.WriteLine(Prefix + msg); }
        }

        public static void Warning(string msg)
        {
            try { Verse.Log.Warning(Prefix + msg); }
            catch { Console.WriteLine(Prefix + "WARN " + msg); }
        }

        public static void Error(string msg, Exception? ex = null)
        {
            var full = Prefix + msg + (ex != null ? "\n" + ex : "");
            try { Verse.Log.Error(full); }
            catch { Console.WriteLine("ERROR " + full); }
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

```bash
cd /home/keenan/github/rimworld-defload-cache
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build DefLoadCache.csproj 2>&1 | tail -15
```
Expected: `Build succeeded.` with `0 Error(s)`.

If build fails with "Verse.Log not found": the `Assembly-CSharp.dll` reference path in the csproj is wrong. Stop and fix the HintPath.

- [ ] **Step 3: Commit**

```bash
cd /home/keenan/github/rimworld-defload-cache
git add src/Log.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add Log helper that prefixes Verse.Log messages with [DefLoadCache]"
```

---

### Task A4: Write `src/Hook/CacheHook.cs` (stub with HookFired)

**Files:**
- Create: `/home/keenan/github/rimworld-defload-cache/src/Hook/CacheHook.cs`

- [ ] **Step 1: Create the Hook folder and write CacheHook.cs**

```bash
mkdir -p /home/keenan/github/rimworld-defload-cache/src/Hook
```

Write `/home/keenan/github/rimworld-defload-cache/src/Hook/CacheHook.cs` with EXACT contents:

```csharp
using System.Collections.Generic;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Static entry points called from IL injected by IlInjector.
    /// Stage A: only HookFired() exists, called from the entry of ApplyPatches
    /// to prove the plumbing works.
    /// Later stages add TryLoadCached() and SaveToCache().
    /// </summary>
    public static class CacheHook
    {
        /// <summary>
        /// Stage A plumbing proof. Called by injected IL at the top of
        /// Verse.LoadedModManager.ApplyPatches. Logs a single message and returns.
        /// </summary>
        public static void HookFired()
        {
            Log.Message("hook fired — Verse.LoadedModManager.ApplyPatches entered");
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

```bash
cd /home/keenan/github/rimworld-defload-cache
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build DefLoadCache.csproj 2>&1 | tail -10
```
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
cd /home/keenan/github/rimworld-defload-cache
git add src/Hook/CacheHook.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add CacheHook stub with HookFired entry point for Stage A"
```

---

### Task A5: Write `src/Prepatcher/IlInjector.cs` (Stage A version)

**Files:**
- Create: `/home/keenan/github/rimworld-defload-cache/src/Prepatcher/IlInjector.cs`

- [ ] **Step 1: Create the Prepatcher folder and write IlInjector.cs**

```bash
mkdir -p /home/keenan/github/rimworld-defload-cache/src/Prepatcher
```

Write `/home/keenan/github/rimworld-defload-cache/src/Prepatcher/IlInjector.cs` with EXACT contents:

```csharp
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using Prepatcher;

namespace FluxxField.DefLoadCache.Prepatcher
{
    /// <summary>
    /// Stage A: injects a single IL call to CacheHook.HookFired() at the top
    /// of Verse.LoadedModManager.ApplyPatches.
    ///
    /// Runs during Prepatcher's assembly rewriting phase, BEFORE the CLR
    /// verifies Assembly-CSharp.dll. At this point Assembly-CSharp is a
    /// Mono.Cecil ModuleDefinition we can mutate freely.
    /// </summary>
    public static class IlInjector
    {
        [FreePatch]
        private static void InjectApplyPatchesHook(ModuleDefinition module)
        {
            // Find Verse.LoadedModManager
            var loadedModManagerType = module.GetType("Verse.LoadedModManager");
            if (loadedModManagerType == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: Verse.LoadedModManager not found in module");
                return;
            }

            // Find ApplyPatches(XmlDocument, Dictionary<XmlNode, LoadableXmlAsset>)
            var applyPatchesMethod = Extensions.FindMethod(loadedModManagerType, "ApplyPatches");
            if (applyPatchesMethod == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: ApplyPatches method not found");
                return;
            }

            // Resolve the managed hook target via reflection on our own assembly
            MethodInfo hookFiredMethod = typeof(CacheHook).GetMethod(
                nameof(CacheHook.HookFired),
                BindingFlags.Public | BindingFlags.Static);
            if (hookFiredMethod == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: CacheHook.HookFired not found via reflection");
                return;
            }

            // Import the managed method reference into the target module
            MethodReference hookFiredRef = module.ImportReference(hookFiredMethod);

            // Inject `call CacheHook::HookFired()` at the very top of the method body
            ILProcessor ilProcessor = applyPatchesMethod.Body.GetILProcessor();
            Instruction firstInstruction = applyPatchesMethod.Body.Instructions[0];
            Instruction callInstruction = ilProcessor.Create(OpCodes.Call, hookFiredRef);
            ilProcessor.InsertBefore(firstInstruction, callInstruction);

            System.Console.WriteLine("[DefLoadCache] FreePatch: injected HookFired call into ApplyPatches");
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

```bash
cd /home/keenan/github/rimworld-defload-cache
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build DefLoadCache.csproj 2>&1 | tail -15
```
Expected: `Build succeeded.` with `0 Error(s)`. The build also produces `Assemblies/DefLoadCache.dll`.

If build fails with "Mono.Cecil not found" or "MonoMod.Utils not found": these types live inside the `0Harmony.dll` reference. Verify the HintPath in the csproj points at the right `0Harmony.dll`. Both namespaces are bundled.

If build fails with "Prepatcher not found": the `0PrepatcherAPI.dll` reference is wrong.

- [ ] **Step 3: Verify the DLL was produced**

```bash
ls -la /home/keenan/github/rimworld-defload-cache/Assemblies/DefLoadCache.dll
```
Expected: file exists, size somewhere between 5-50 KB.

- [ ] **Step 4: Commit**

```bash
cd /home/keenan/github/rimworld-defload-cache
git add src/Prepatcher/IlInjector.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add IlInjector with FreePatch that injects HookFired call into ApplyPatches"
```

---

### Task A6: Deploy to RimWorld Mods folder + Manual in-game verification

**Files:**
- Copy: `/home/keenan/github/rimworld-defload-cache/{About,Assemblies}` → `/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/DefLoadCache/`

- [ ] **Step 1: Copy the mod into RimWorld's local Mods folder**

```bash
RW_MODS="/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods"
SRC="/home/keenan/github/rimworld-defload-cache"
DEST="$RW_MODS/DefLoadCache"

rm -rf "$DEST"
mkdir -p "$DEST"
cp -r "$SRC/About" "$DEST/"
cp -r "$SRC/Assemblies" "$DEST/"

echo "=== Deployed files ==="
find "$DEST" -type f | sort
```
Expected:
```
.../About/About.xml
.../Assemblies/DefLoadCache.dll
```

- [ ] **Step 2: Add DefLoadCache to the test modlist**

Manual step (you, the user). Launch RimWorld → Mods menu → load the `DefLoadCache POC Test` modlist → enable `DefLoadCache` → position it after Prepatcher → click Save and reload.

- [ ] **Step 3: Check Player.log for the hook message**

After RimWorld finishes loading to the main menu, run:

```bash
grep -E "(DefLoadCache|FreePatch: injected)" "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log" | head -10
```
Expected output (both lines should appear):
```
[DefLoadCache] FreePatch: injected HookFired call into ApplyPatches
[DefLoadCache] hook fired — Verse.LoadedModManager.ApplyPatches entered
```

The first line is the Cecil injection confirming. The second line is the runtime call confirming that the IL we injected actually executed when ApplyPatches was called.

If the **first line is missing**: Prepatcher didn't pick up our `[FreePatch]`. Check that DefLoadCache is enabled and loads after Prepatcher in the load order. Check that `0PrepatcherAPI.dll` was correctly referenced at compile time.

If the **first line is present but second line is missing**: the IL injection ran but our code didn't execute. Possibilities: we targeted the wrong method, the injected instruction was at the wrong index, or our managed hook threw an exception silently.

If both lines are present: **Stage A succeeds.** Stage A's question — "is FreePatch hookability viable?" — is answered YES.

- [ ] **Step 4: Commit a milestone marker**

```bash
cd /home/keenan/github/rimworld-defload-cache
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit --allow-empty -m "Stage A complete: empty plumbing proof verified in-game"
```

---

## Stage B — Modlist fingerprint

Goal: prove we can compute a stable, sensitive, fast (<5 sec) hash of the active modlist.

### Task B1: Write `src/Fingerprint/ModlistFingerprint.cs`

**Files:**
- Create: `/home/keenan/github/rimworld-defload-cache/src/Fingerprint/ModlistFingerprint.cs`

- [ ] **Step 1: Create the Fingerprint folder and write ModlistFingerprint.cs**

```bash
mkdir -p /home/keenan/github/rimworld-defload-cache/src/Fingerprint
```

Write `/home/keenan/github/rimworld-defload-cache/src/Fingerprint/ModlistFingerprint.cs` with EXACT contents:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Computes a stable SHA256 hash of the active modlist's structural state.
    /// Inputs: RimWorld version + per-mod (packageId, version, Defs/ file count + total bytes,
    /// Patches/ file count + total bytes) + cache format version. Sorted by load order.
    /// Hash recomputes in &lt; 5 seconds on a 500-mod load order.
    /// </summary>
    internal static class ModlistFingerprint
    {
        /// <summary>
        /// Bump this when the cache format changes. All caches with a different
        /// version are invalidated.
        /// </summary>
        public const int CacheFormatVersion = 1;

        public static string Compute()
        {
            var sb = new StringBuilder();
            sb.Append("rimworld=").Append(VersionControl.CurrentVersionString).Append('\n');
            sb.Append("cacheformat=").Append(CacheFormatVersion).Append('\n');

            var mods = LoadedModManager.RunningModsListForReading;
            sb.Append("modcount=").Append(mods.Count).Append('\n');

            foreach (var mod in mods)
            {
                sb.Append("mod=").Append(mod.PackageId ?? "<no-id>").Append('\n');
                sb.Append("modversion=").Append(GetModVersion(mod)).Append('\n');
                AppendFolderStats(sb, "defs", Path.Combine(mod.RootDir, "Defs"));
                AppendFolderStats(sb, "patches", Path.Combine(mod.RootDir, "Patches"));
            }

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BytesToHex(hash);
            }
        }

        private static string GetModVersion(ModContentPack mod)
        {
            // ModContentPack does not expose modVersion directly; read from About/About.xml
            try
            {
                string aboutPath = Path.Combine(mod.RootDir, "About", "About.xml");
                if (!File.Exists(aboutPath)) return "<no-about>";
                var doc = new System.Xml.XmlDocument();
                doc.Load(aboutPath);
                var node = doc.SelectSingleNode("//ModMetaData/modVersion");
                return node?.InnerText?.Trim() ?? "<no-version>";
            }
            catch
            {
                return "<error>";
            }
        }

        private static void AppendFolderStats(StringBuilder sb, string label, string folderPath)
        {
            int count = 0;
            long totalBytes = 0;
            if (Directory.Exists(folderPath))
            {
                foreach (var file in Directory.EnumerateFiles(folderPath, "*.xml", SearchOption.AllDirectories))
                {
                    count++;
                    try { totalBytes += new FileInfo(file).Length; }
                    catch { /* unreadable file, skip its size */ }
                }
            }
            sb.Append(label).Append("=count:").Append(count).Append(",bytes:").Append(totalBytes).Append('\n');
        }

        private static string BytesToHex(byte[] bytes)
        {
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }
    }
}
```

- [ ] **Step 2: Update CacheHook to call ModlistFingerprint.Compute() and log it**

Edit `/home/keenan/github/rimworld-defload-cache/src/Hook/CacheHook.cs`. Replace its entire contents with:

```csharp
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Static entry points called from IL injected by IlInjector.
    /// Stage B: HookFired now also computes and logs the modlist fingerprint.
    /// </summary>
    public static class CacheHook
    {
        public static void HookFired()
        {
            Log.Message("hook fired — Verse.LoadedModManager.ApplyPatches entered");

            var sw = Stopwatch.StartNew();
            string fingerprint;
            try
            {
                fingerprint = ModlistFingerprint.Compute();
            }
            catch (System.Exception ex)
            {
                Log.Error("fingerprint computation threw", ex);
                return;
            }
            sw.Stop();

            Log.Message($"fingerprint = {fingerprint} (computed in {sw.ElapsedMilliseconds}ms)");
        }
    }
}
```

- [ ] **Step 3: Build**

```bash
cd /home/keenan/github/rimworld-defload-cache
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build DefLoadCache.csproj 2>&1 | tail -10
```
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 4: Re-deploy and test**

```bash
cp /home/keenan/github/rimworld-defload-cache/Assemblies/DefLoadCache.dll \
   "/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/DefLoadCache/Assemblies/DefLoadCache.dll"
```

Then launch RimWorld with the `DefLoadCache POC Test` modlist. After it loads to the main menu, run:

```bash
grep "DefLoadCache" "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log" | grep -E "(fingerprint|hook fired)"
```
Expected output:
```
[DefLoadCache] hook fired — Verse.LoadedModManager.ApplyPatches entered
[DefLoadCache] fingerprint = <64-char hex> (computed in <ms>ms)
```

**Validate Stage B:**
- The hex string must be 64 chars (SHA256 in hex)
- The computed-in time must be **<5000 ms** on the test modlist
- Quit the game and immediately relaunch it (no changes). The fingerprint should be **byte-identical** between the two runs. Compare:
  ```bash
  grep "fingerprint =" "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log" | tail -2
  ```
- Now disable one mod (e.g., Ratnik-3 in the mod list), launch again, check that the fingerprint **changes**.
- Re-enable Ratnik-3 to restore the test modlist.

If the time is over 5 seconds, the fingerprint is too slow — need to optimize (parallelize, skip larger directories, etc.). Stop and report.

If the fingerprint is unstable across consecutive runs with no changes, something in the input is non-deterministic — investigate which input. Stop and report.

- [ ] **Step 5: Commit**

```bash
cd /home/keenan/github/rimworld-defload-cache
git add src/Fingerprint/ModlistFingerprint.cs src/Hook/CacheHook.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add ModlistFingerprint with structural hash + integrate into CacheHook"
```

- [ ] **Step 6: Stage B milestone**

```bash
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit --allow-empty -m "Stage B complete: fingerprint stable, sensitive, fast"
```

---

## Stage C — Cache write (no read yet)

Goal: after a cache-miss launch, a cache file exists on disk that round-trips through gzip into a valid XML doc, and the merged doc has mod-attribution attributes embedded for Stage D's lookup rebuild.

### Task C1: Write `src/Cache/CacheFormat.cs`

**Files:**
- Create: `/home/keenan/github/rimworld-defload-cache/src/Cache/CacheFormat.cs`

- [ ] **Step 1: Create the Cache folder and write CacheFormat.cs**

```bash
mkdir -p /home/keenan/github/rimworld-defload-cache/src/Cache
```

Write `/home/keenan/github/rimworld-defload-cache/src/Cache/CacheFormat.cs` with EXACT contents:

```csharp
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// XmlDocument &lt;-&gt; gzipped UTF-8 byte stream. No mod-attribution
    /// processing — that lives in ModAttributionTagger.
    /// </summary>
    internal static class CacheFormat
    {
        public static byte[] Serialize(XmlDocument doc)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                using (var writer = XmlWriter.Create(gz, new XmlWriterSettings
                {
                    Encoding = System.Text.Encoding.UTF8,
                    Indent = false,
                    OmitXmlDeclaration = false,
                }))
                {
                    doc.Save(writer);
                }
                return ms.ToArray();
            }
        }

        public static XmlDocument Deserialize(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var reader = XmlReader.Create(gz))
            {
                var doc = new XmlDocument();
                doc.Load(reader);
                return doc;
            }
        }
    }
}
```

- [ ] **Step 2: Build**

```bash
cd /home/keenan/github/rimworld-defload-cache
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build DefLoadCache.csproj 2>&1 | tail -10
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/Cache/CacheFormat.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add CacheFormat for gzip XmlDocument round-trip"
```

---

### Task C2: Write `src/Cache/CacheStorage.cs`

**Files:**
- Create: `/home/keenan/github/rimworld-defload-cache/src/Cache/CacheStorage.cs`

- [ ] **Step 1: Write CacheStorage.cs**

Write `/home/keenan/github/rimworld-defload-cache/src/Cache/CacheStorage.cs` with EXACT contents:

```csharp
using System;
using System.IO;
using System.Linq;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Disk I/O for cache files. Atomic writes via temp + rename.
    /// Pruning keeps the 3 most-recent .xml.gz files.
    /// </summary>
    internal static class CacheStorage
    {
        private const int MaxCachedFilesToKeep = 3;

        public static string CacheRoot
        {
            get
            {
                // GenFilePaths.SaveDataFolderPath returns the RimWorld user data folder
                // ("%LOCALAPPDATALOW%\Ludeon Studios\RimWorld by Ludeon Studios" on Windows)
                return Path.Combine(GenFilePaths.SaveDataFolderPath, "DefLoadCache");
            }
        }

        public static string PathForFingerprint(string fingerprint)
        {
            return Path.Combine(CacheRoot, fingerprint + ".xml.gz");
        }

        public static string MetaPathForFingerprint(string fingerprint)
        {
            return Path.Combine(CacheRoot, fingerprint + ".meta.json");
        }

        public static bool TryRead(string fingerprint, out byte[] bytes)
        {
            bytes = null;
            string p = PathForFingerprint(fingerprint);
            if (!File.Exists(p)) return false;
            try
            {
                bytes = File.ReadAllBytes(p);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"failed to read cache {p}", ex);
                try { File.Delete(p); } catch { }
                return false;
            }
        }

        public static void Write(string fingerprint, byte[] bytes, string metaJson)
        {
            try
            {
                Directory.CreateDirectory(CacheRoot);
            }
            catch (Exception ex)
            {
                Log.Error($"could not create cache root {CacheRoot}", ex);
                return;
            }

            string finalPath = PathForFingerprint(fingerprint);
            string tmpPath = finalPath + ".tmp";

            try
            {
                File.WriteAllBytes(tmpPath, bytes);

                // .NET Framework's File.Move throws if dest exists; use Replace if it does.
                if (File.Exists(finalPath))
                {
                    File.Replace(tmpPath, finalPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmpPath, finalPath);
                }

                File.WriteAllText(MetaPathForFingerprint(fingerprint), metaJson);

                Log.Message($"cache written: {finalPath} ({bytes.Length / 1024} KB)");

                Prune();
            }
            catch (Exception ex)
            {
                Log.Error($"failed to write cache {finalPath}", ex);
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
        }

        public static void Prune()
        {
            try
            {
                if (!Directory.Exists(CacheRoot)) return;

                var files = Directory.GetFiles(CacheRoot, "*.xml.gz")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTimeUtc)
                    .ToList();

                // Delete orphaned .tmp files
                foreach (var orphan in Directory.GetFiles(CacheRoot, "*.tmp"))
                {
                    try { File.Delete(orphan); } catch { }
                }

                // Keep the most-recent N, delete the rest
                foreach (var fi in files.Skip(MaxCachedFilesToKeep))
                {
                    try
                    {
                        File.Delete(fi.FullName);
                        string metaPath = Path.ChangeExtension(fi.FullName, null) + ".meta.json";
                        if (File.Exists(metaPath)) File.Delete(metaPath);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Error("prune failed", ex);
            }
        }
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build DefLoadCache.csproj 2>&1 | tail -10
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/Cache/CacheStorage.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add CacheStorage with atomic writes and 3-file prune"
```

---

### Task C3: Write `src/ModAttribution/ModAttributionTagger.cs` (write side)

**Files:**
- Create: `/home/keenan/github/rimworld-defload-cache/src/ModAttribution/ModAttributionTagger.cs`

- [ ] **Step 1: Create the ModAttribution folder and write ModAttributionTagger.cs**

```bash
mkdir -p /home/keenan/github/rimworld-defload-cache/src/ModAttribution
```

Write `/home/keenan/github/rimworld-defload-cache/src/ModAttribution/ModAttributionTagger.cs` with EXACT contents:

```csharp
using System.Collections.Generic;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Embeds source-mod packageId attributes onto top-level def nodes when
    /// caching, and rebuilds the assetlookup dictionary from those attributes
    /// when loading. This preserves mod attribution across the cache round-trip
    /// because XmlNode identity does not survive serialization.
    /// </summary>
    internal static class ModAttributionTagger
    {
        /// <summary>The attribute name we stamp on each top-level def node.</summary>
        public const string AttributeName = "data-defloadcache-mod";

        /// <summary>
        /// Walks the merged doc's top-level def nodes and stamps each with a
        /// data-defloadcache-mod attribute pulled from the existing assetlookup.
        /// Mutates the doc in place. Call BEFORE serialization.
        /// </summary>
        public static void StampAttributions(XmlDocument doc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            if (doc?.DocumentElement == null) return;
            int stamped = 0;
            int missing = 0;
            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (node is not XmlElement element) continue;

                if (assetlookup.TryGetValue(node, out var asset) && asset?.mod?.PackageId != null)
                {
                    element.SetAttribute(AttributeName, asset.mod.PackageId);
                    stamped++;
                }
                else
                {
                    missing++;
                }
            }
            Log.Message($"ModAttributionTagger: stamped {stamped} nodes, {missing} had no mod attribution");
        }

        /// <summary>
        /// Walks a deserialized doc and rebuilds assetlookup from the embedded
        /// data-defloadcache-mod attributes. Returns the count of successfully
        /// rebuilt entries.
        /// </summary>
        public static int RebuildAssetLookup(XmlDocument doc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            if (doc?.DocumentElement == null) return 0;

            // Build a packageId -> ModContentPack lookup once
            var modsByPackageId = new Dictionary<string, ModContentPack>();
            foreach (var m in LoadedModManager.RunningModsListForReading)
            {
                if (m.PackageId != null && !modsByPackageId.ContainsKey(m.PackageId))
                {
                    modsByPackageId[m.PackageId] = m;
                }
            }

            int rebuilt = 0;
            int missingMod = 0;
            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (node is not XmlElement element) continue;

                string? packageId = element.GetAttribute(AttributeName);
                if (string.IsNullOrEmpty(packageId)) continue;

                // Strip the cache attribute so it doesn't pollute the live doc
                element.RemoveAttribute(AttributeName);

                if (modsByPackageId.TryGetValue(packageId, out var mod))
                {
                    // Construct a synthetic LoadableXmlAsset that carries the mod reference.
                    // ParseAndProcessXML only reads the .mod field; other fields can be null.
                    var synthetic = new LoadableXmlAsset(name: "<defloadcache>", fullFolderPath: "", contents: "")
                    {
                        mod = mod
                    };
                    assetlookup[node] = synthetic;
                    rebuilt++;
                }
                else
                {
                    missingMod++;
                }
            }
            Log.Message($"ModAttributionTagger: rebuilt {rebuilt} assetlookup entries, {missingMod} packageIds not in active mods");
            return rebuilt;
        }
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build DefLoadCache.csproj 2>&1 | tail -15
```
Expected: `Build succeeded.`

If build fails because `LoadableXmlAsset`'s constructor signature is wrong: decompile `Verse.LoadableXmlAsset` from `Assembly-CSharp.dll` to find the actual constructor, and update the call site. Use:
```bash
grep -A 30 "class LoadableXmlAsset" /tmp/acsharp-full.cs | head -40
```

If `LoadableXmlAsset.mod` is read-only: use the constructor that accepts a mod parameter, OR use reflection to set the field.

- [ ] **Step 3: Commit**

```bash
git add src/ModAttribution/ModAttributionTagger.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add ModAttributionTagger to preserve mod attribution across cache round-trip"
```

---

### Task C4: Update CacheHook + IlInjector for Stage C (cache write postfix)

**Files:**
- Modify: `/home/keenan/github/rimworld-defload-cache/src/Hook/CacheHook.cs`
- Modify: `/home/keenan/github/rimworld-defload-cache/src/Prepatcher/IlInjector.cs`

- [ ] **Step 1: Replace CacheHook.cs with the Stage C version**

Write `/home/keenan/github/rimworld-defload-cache/src/Hook/CacheHook.cs` with EXACT contents:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Static entry points called from IL injected by IlInjector.
    /// Stage C: SaveToCache postfix runs after ApplyPatches and persists the
    /// merged post-patch document to disk.
    /// </summary>
    public static class CacheHook
    {
        // Cached so SaveToCache can reuse the value computed in TryLoadCached / HookFired
        private static string? _currentFingerprint;

        /// <summary>
        /// Stage A/B compatibility: still called at the start of ApplyPatches
        /// to log + compute fingerprint. The fingerprint is stashed for SaveToCache
        /// to reuse without recomputing.
        /// </summary>
        public static void HookFired()
        {
            try
            {
                Log.Message("hook fired — Verse.LoadedModManager.ApplyPatches entered");

                var sw = Stopwatch.StartNew();
                _currentFingerprint = ModlistFingerprint.Compute();
                sw.Stop();

                Log.Message($"fingerprint = {_currentFingerprint} (computed in {sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                Log.Error("HookFired threw — falling back to no-op", ex);
                _currentFingerprint = null;
            }
        }

        /// <summary>
        /// Stage C: called by injected IL at the END of ApplyPatches.
        /// Reads the now-fully-patched merged doc, stamps mod attribution onto
        /// each top-level node, serializes, atomically writes to disk.
        /// Receives xmlDoc and assetlookup as the parameters of ApplyPatches itself.
        /// </summary>
        public static void SaveToCache(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            try
            {
                if (_currentFingerprint == null)
                {
                    Log.Warning("SaveToCache: no fingerprint cached, skipping save");
                    return;
                }
                if (xmlDoc == null)
                {
                    Log.Warning("SaveToCache: xmlDoc is null, skipping save");
                    return;
                }

                var sw = Stopwatch.StartNew();
                ModAttributionTagger.StampAttributions(xmlDoc, assetlookup);
                byte[] bytes = CacheFormat.Serialize(xmlDoc);
                sw.Stop();

                string metaJson = "{"
                    + $"\"timestamp\":\"{DateTime.UtcNow:o}\","
                    + $"\"modCount\":{LoadedModManager.RunningModsListForReading.Count},"
                    + $"\"rimworldVersion\":\"{VersionControl.CurrentVersionString}\","
                    + $"\"cacheFormatVersion\":{ModlistFingerprint.CacheFormatVersion},"
                    + $"\"sizeBytes\":{bytes.Length}"
                    + "}";

                CacheStorage.Write(_currentFingerprint, bytes, metaJson);
                Log.Message($"SaveToCache: serialized + wrote in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Log.Error("SaveToCache threw — cache not saved (game continues normally)", ex);
            }
        }
    }
}
```

- [ ] **Step 2: Replace IlInjector.cs with the Stage C version**

The Stage C version injects TWO calls: `HookFired()` at the start of ApplyPatches (no parameters), and `SaveToCache(xmlDoc, assetlookup)` immediately before EVERY `ret` instruction in the method body. Multiple rets exist if the method has finally blocks; we wrap each with our save call.

Write `/home/keenan/github/rimworld-defload-cache/src/Prepatcher/IlInjector.cs` with EXACT contents:

```csharp
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using Prepatcher;

namespace FluxxField.DefLoadCache.Prepatcher
{
    /// <summary>
    /// Stage C: injects HookFired() at the start AND SaveToCache(xmlDoc, assetlookup)
    /// before every ret in Verse.LoadedModManager.ApplyPatches. The save call passes
    /// the method's two parameters via ldarg.0 and ldarg.1.
    ///
    /// Stage D will further extend this to wrap HookFired with a TryLoadCached
    /// prefix that can short-circuit the method body via brtrue.
    /// </summary>
    public static class IlInjector
    {
        [FreePatch]
        private static void InjectApplyPatchesHook(ModuleDefinition module)
        {
            var loadedModManagerType = module.GetType("Verse.LoadedModManager");
            if (loadedModManagerType == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: Verse.LoadedModManager not found in module");
                return;
            }

            var applyPatchesMethod = Extensions.FindMethod(loadedModManagerType, "ApplyPatches");
            if (applyPatchesMethod == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: ApplyPatches method not found");
                return;
            }

            // Resolve our managed hook targets
            MethodInfo hookFiredMethod = typeof(CacheHook).GetMethod(
                nameof(CacheHook.HookFired),
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo saveToCacheMethod = typeof(CacheHook).GetMethod(
                nameof(CacheHook.SaveToCache),
                BindingFlags.Public | BindingFlags.Static);
            if (hookFiredMethod == null || saveToCacheMethod == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: required hook methods not found via reflection");
                return;
            }

            MethodReference hookFiredRef = module.ImportReference(hookFiredMethod);
            MethodReference saveToCacheRef = module.ImportReference(saveToCacheMethod);

            ILProcessor il = applyPatchesMethod.Body.GetILProcessor();

            // 1. Inject `call CacheHook::HookFired()` at the very top
            Instruction firstInstruction = applyPatchesMethod.Body.Instructions[0];
            Instruction callHookFired = il.Create(OpCodes.Call, hookFiredRef);
            il.InsertBefore(firstInstruction, callHookFired);

            // 2. Inject `ldarg.0; ldarg.1; call CacheHook::SaveToCache` before EVERY ret
            // Snapshot the ret positions FIRST so we don't iterate while mutating.
            var retInstructions = applyPatchesMethod.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ret)
                .ToList();

            foreach (var ret in retInstructions)
            {
                var ldArgDoc = il.Create(OpCodes.Ldarg_0);
                var ldArgLookup = il.Create(OpCodes.Ldarg_1);
                var callSave = il.Create(OpCodes.Call, saveToCacheRef);
                il.InsertBefore(ret, ldArgDoc);
                il.InsertBefore(ret, ldArgLookup);
                il.InsertBefore(ret, callSave);
            }

            System.Console.WriteLine($"[DefLoadCache] FreePatch: injected HookFired + {retInstructions.Count} SaveToCache call(s) into ApplyPatches");
        }
    }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build DefLoadCache.csproj 2>&1 | tail -15
```
Expected: `Build succeeded.`

- [ ] **Step 4: Re-deploy and test**

```bash
cp /home/keenan/github/rimworld-defload-cache/Assemblies/DefLoadCache.dll \
   "/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/DefLoadCache/Assemblies/DefLoadCache.dll"
```

Delete any existing cache to start fresh:
```bash
rm -rf "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/DefLoadCache"
```

Launch RimWorld with the test modlist. Wait for it to reach the main menu.

- [ ] **Step 5: Verify the cache file exists**

```bash
ls -la "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/DefLoadCache/"
```
Expected: at least `<fingerprint>.xml.gz` and `<fingerprint>.meta.json`. Size of the .xml.gz should be 1-50 MB.

- [ ] **Step 6: Verify the cache file is valid gzipped XML**

```bash
CACHE_DIR="/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/DefLoadCache"
GZ_FILE=$(ls -t "$CACHE_DIR"/*.xml.gz | head -1)
gzip -dc "$GZ_FILE" | head -3
```
Expected: an XML declaration line and the start of `<Defs ...>` content. If `gzip -dc` fails, the file is corrupt.

- [ ] **Step 7: Verify the cache contains mod-attribution attributes**

```bash
gzip -dc "$GZ_FILE" | grep -c 'data-defloadcache-mod=' | head -1
```
Expected: a number > 0 (one attribute per top-level def node, so likely thousands).

- [ ] **Step 8: Check Player.log for save messages**

```bash
grep -E "DefLoadCache.*(stamped|cache written|SaveToCache)" "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log"
```
Expected:
```
[DefLoadCache] ModAttributionTagger: stamped <N> nodes, <M> had no mod attribution
[DefLoadCache] cache written: <path> (<KB>)
[DefLoadCache] SaveToCache: serialized + wrote in <ms>ms
```

If `<M>` (missing attribution count) is large (hundreds of nodes), that's a warning sign — maybe many nodes were inserted by patches and aren't in assetlookup. Investigate which nodes are missing attribution and whether it matters.

- [ ] **Step 9: Commit**

```bash
git add src/Hook/CacheHook.cs src/Prepatcher/IlInjector.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add SaveToCache postfix injection: serialize merged doc with mod attribution"
```

- [ ] **Step 10: Stage C milestone**

```bash
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit --allow-empty -m "Stage C complete: cache write produces valid gzipped XML on disk"
```

---

## Stage D — Cache read (the actual short-circuit)

Goal: on a cache-hit launch, our prefix loads the cached doc, replaces `xmlDoc`'s contents, rebuilds `assetlookup`, and short-circuits the original `ApplyPatches` body.

### Task D1: Update CacheHook with TryLoadCached prefix

**Files:**
- Modify: `/home/keenan/github/rimworld-defload-cache/src/Hook/CacheHook.cs`

- [ ] **Step 1: Replace CacheHook.cs with the Stage D version**

Write `/home/keenan/github/rimworld-defload-cache/src/Hook/CacheHook.cs` with EXACT contents:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Static entry points called from IL injected by IlInjector.
    /// Stage D: TryLoadCached prefix can short-circuit ApplyPatches by
    /// returning true (the injected brtrue branches to ret).
    /// </summary>
    public static class CacheHook
    {
        private static string? _currentFingerprint;

        /// <summary>
        /// No-op shim kept temporarily so the Stage C IL injection still
        /// resolves to a real method after this Task (D1) rewrites CacheHook
        /// but BEFORE Task D2 rewrites IlInjector to call TryLoadCached
        /// instead. After D2, this method becomes unused (but harmless to
        /// keep — Cecil can't tell it's dead).
        /// </summary>
        public static void HookFired() { }

        /// <summary>
        /// Called by injected IL at the START of ApplyPatches.
        /// Returns true to skip the original method body (cache hit).
        /// Returns false to let it run normally (cache miss).
        /// On exception: logs and returns false (degraded to normal load).
        ///
        /// xmlDoc and assetlookup are the parameters of ApplyPatches itself,
        /// passed via ldarg.0 and ldarg.1 in the injected IL.
        /// </summary>
        public static bool TryLoadCached(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            try
            {
                Log.Message("hook fired — Verse.LoadedModManager.ApplyPatches entered");

                var swFp = Stopwatch.StartNew();
                _currentFingerprint = ModlistFingerprint.Compute();
                swFp.Stop();
                Log.Message($"fingerprint = {_currentFingerprint} (computed in {swFp.ElapsedMilliseconds}ms)");

                if (!CacheStorage.TryRead(_currentFingerprint, out var bytes))
                {
                    Log.Message("cache MISS — running normal ApplyPatches");
                    return false;
                }

                var swLoad = Stopwatch.StartNew();
                XmlDocument cachedDoc = CacheFormat.Deserialize(bytes);
                swLoad.Stop();

                // Replace xmlDoc's contents with the cached doc's nodes.
                // We can't reassign the parameter (it's the caller's local), so we
                // mutate the passed XmlDocument in place by clearing and re-importing.
                xmlDoc.RemoveAll();
                foreach (XmlNode child in cachedDoc.ChildNodes)
                {
                    XmlNode imported = xmlDoc.ImportNode(child, deep: true);
                    xmlDoc.AppendChild(imported);
                }

                // Rebuild assetlookup from the embedded mod-attribution attributes.
                // (After importing, we need to walk the LIVE xmlDoc, not cachedDoc,
                // because the imported nodes are different instances.)
                int rebuilt = ModAttributionTagger.RebuildAssetLookup(xmlDoc, assetlookup);

                Log.Message($"cache HIT — deserialized + populated in {swLoad.ElapsedMilliseconds}ms, " +
                            $"{rebuilt} assetlookup entries rebuilt. Skipping original ApplyPatches.");

                return true;
            }
            catch (Exception ex)
            {
                Log.Error("TryLoadCached threw — falling back to normal ApplyPatches", ex);
                return false;
            }
        }

        /// <summary>
        /// Called by injected IL at the END of ApplyPatches.
        /// Saves the just-computed merged doc to disk if we ran the normal path.
        /// On a cache-hit run, this still gets called but we detect the hit and
        /// skip writing (the cache file already exists with this fingerprint).
        /// </summary>
        public static void SaveToCache(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            try
            {
                if (_currentFingerprint == null)
                {
                    Log.Warning("SaveToCache: no fingerprint cached, skipping save");
                    return;
                }
                if (xmlDoc == null)
                {
                    Log.Warning("SaveToCache: xmlDoc is null, skipping save");
                    return;
                }

                // If a cache file already exists for this fingerprint, this run was
                // a cache hit and we've already populated xmlDoc from cache. Don't
                // re-serialize and re-write the same data.
                string existingPath = CacheStorage.PathForFingerprint(_currentFingerprint);
                if (System.IO.File.Exists(existingPath))
                {
                    Log.Message("SaveToCache: cache file already exists for this fingerprint, skipping save");
                    return;
                }

                var sw = Stopwatch.StartNew();
                ModAttributionTagger.StampAttributions(xmlDoc, assetlookup);
                byte[] bytes = CacheFormat.Serialize(xmlDoc);
                sw.Stop();

                string metaJson = "{"
                    + $"\"timestamp\":\"{DateTime.UtcNow:o}\","
                    + $"\"modCount\":{LoadedModManager.RunningModsListForReading.Count},"
                    + $"\"rimworldVersion\":\"{VersionControl.CurrentVersionString}\","
                    + $"\"cacheFormatVersion\":{ModlistFingerprint.CacheFormatVersion},"
                    + $"\"sizeBytes\":{bytes.Length}"
                    + "}";

                CacheStorage.Write(_currentFingerprint, bytes, metaJson);
                Log.Message($"SaveToCache: serialized + wrote in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Log.Error("SaveToCache threw — cache not saved (game continues normally)", ex);
            }
        }
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build DefLoadCache.csproj 2>&1 | tail -10
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/Hook/CacheHook.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add TryLoadCached prefix that mutates xmlDoc in place from cache"
```

---

### Task D2: Update IlInjector to inject TryLoadCached prefix with brtrue branch

**Files:**
- Modify: `/home/keenan/github/rimworld-defload-cache/src/Prepatcher/IlInjector.cs`

- [ ] **Step 1: Replace IlInjector.cs with the Stage D version**

Write `/home/keenan/github/rimworld-defload-cache/src/Prepatcher/IlInjector.cs` with EXACT contents:

```csharp
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using Prepatcher;

namespace FluxxField.DefLoadCache.Prepatcher
{
    /// <summary>
    /// Stage D: at the start of ApplyPatches, inject:
    ///     ldarg.0
    ///     ldarg.1
    ///     call bool CacheHook::TryLoadCached(XmlDocument, Dictionary)
    ///     brtrue.s &lt;final ret&gt;
    /// And before each ret, inject SaveToCache(xmlDoc, assetlookup) as before.
    /// </summary>
    public static class IlInjector
    {
        [FreePatch]
        private static void InjectApplyPatchesHook(ModuleDefinition module)
        {
            var loadedModManagerType = module.GetType("Verse.LoadedModManager");
            if (loadedModManagerType == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: Verse.LoadedModManager not found");
                return;
            }

            var applyPatchesMethod = Extensions.FindMethod(loadedModManagerType, "ApplyPatches");
            if (applyPatchesMethod == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: ApplyPatches not found");
                return;
            }

            MethodInfo tryLoadCachedMi = typeof(CacheHook).GetMethod(
                nameof(CacheHook.TryLoadCached),
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo saveToCacheMi = typeof(CacheHook).GetMethod(
                nameof(CacheHook.SaveToCache),
                BindingFlags.Public | BindingFlags.Static);
            if (tryLoadCachedMi == null || saveToCacheMi == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: hook methods not found via reflection");
                return;
            }

            MethodReference tryLoadCachedRef = module.ImportReference(tryLoadCachedMi);
            MethodReference saveToCacheRef = module.ImportReference(saveToCacheMi);

            ILProcessor il = applyPatchesMethod.Body.GetILProcessor();

            // 1. Snapshot existing ret positions BEFORE any mutation.
            var retInstructions = applyPatchesMethod.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ret)
                .ToList();

            // 2. We need a target for brtrue. Use the LAST ret in the method as
            // the jump target — branching there from the prefix bypasses the entire
            // body. Then SaveToCache calls will still fire on cache-miss runs because
            // they're injected before EACH ret, but the cache-hit path branches over
            // them via brtrue (which goes directly to the last ret).
            //
            // Important: when we branch to the last ret, we skip the SaveToCache
            // calls. CacheHook.SaveToCache itself detects "cache file already exists
            // for this fingerprint" and skips writing in that case, but we shouldn't
            // even reach it on cache-hit. The SaveToCache calls before earlier rets
            // (in finally blocks) are reached if the original method body runs and
            // its finally fires; on cache-hit we skip all of them.
            Instruction finalRet = retInstructions.Last();

            // 3. Build the prefix:
            //    ldarg.0
            //    ldarg.1
            //    call bool CacheHook::TryLoadCached(XmlDocument, Dictionary)
            //    brtrue finalRet
            Instruction firstInstruction = applyPatchesMethod.Body.Instructions[0];
            var ldArg0 = il.Create(OpCodes.Ldarg_0);
            var ldArg1 = il.Create(OpCodes.Ldarg_1);
            var callTryLoad = il.Create(OpCodes.Call, tryLoadCachedRef);
            var brTrueFinal = il.Create(OpCodes.Brtrue, finalRet);

            il.InsertBefore(firstInstruction, ldArg0);
            il.InsertBefore(firstInstruction, ldArg1);
            il.InsertBefore(firstInstruction, callTryLoad);
            il.InsertBefore(firstInstruction, brTrueFinal);

            // 4. Inject SaveToCache before each existing ret (cache-miss path).
            foreach (var ret in retInstructions)
            {
                var ldArgDocSave = il.Create(OpCodes.Ldarg_0);
                var ldArgLookupSave = il.Create(OpCodes.Ldarg_1);
                var callSave = il.Create(OpCodes.Call, saveToCacheRef);
                il.InsertBefore(ret, ldArgDocSave);
                il.InsertBefore(ret, ldArgLookupSave);
                il.InsertBefore(ret, callSave);
            }

            System.Console.WriteLine($"[DefLoadCache] FreePatch: injected TryLoadCached prefix + {retInstructions.Count} SaveToCache postfixes into ApplyPatches");
        }
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build DefLoadCache.csproj 2>&1 | tail -15
```
Expected: `Build succeeded.`

- [ ] **Step 3: Re-deploy and test cache miss + cache hit**

```bash
cp /home/keenan/github/rimworld-defload-cache/Assemblies/DefLoadCache.dll \
   "/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/DefLoadCache/Assemblies/DefLoadCache.dll"

# Delete any existing cache so first launch is a guaranteed miss
rm -rf "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/DefLoadCache"
```

**Launch 1 (cache miss):** Start RimWorld with the test modlist. Wait for the main menu. Expected log lines:
```
[DefLoadCache] hook fired — Verse.LoadedModManager.ApplyPatches entered
[DefLoadCache] fingerprint = <hex> (computed in <ms>ms)
[DefLoadCache] cache MISS — running normal ApplyPatches
[DefLoadCache] ModAttributionTagger: stamped <N> nodes, <M> had no mod attribution
[DefLoadCache] SaveToCache: serialized + wrote in <ms>ms
[DefLoadCache] cache written: <path> (<KB>)
```

Quit RimWorld.

**Launch 2 (cache hit):** Start RimWorld again. Expected log lines:
```
[DefLoadCache] hook fired — Verse.LoadedModManager.ApplyPatches entered
[DefLoadCache] fingerprint = <hex> (same as launch 1)
[DefLoadCache] cache HIT — deserialized + populated in <ms>ms, <N> assetlookup entries rebuilt. Skipping original ApplyPatches.
```

Critical checks:
1. The fingerprint must MATCH between launch 1 and launch 2
2. Launch 2 must say "cache HIT"
3. Game must reach the main menu without errors
4. Spawn a test colony in launch 2 — pawns generate, items appear, no missing-def warnings about patched defs

If launch 2 says "cache MISS" instead of "cache HIT", the cache file isn't being read or the fingerprint changed. Inspect the cache directory and the fingerprint values.

If launch 2 reaches main menu but spawns a broken colony (missing items, errors about defs), the cached doc isn't equivalent to the live-patched doc. Investigate why — likely a mod attribution issue.

If launch 2 hangs or crashes during load, the cached doc is structurally broken. Stop and investigate.

- [ ] **Step 4: Commit**

```bash
git add src/Prepatcher/IlInjector.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add brtrue prefix injection for cache-hit short-circuit"
```

- [ ] **Step 5: Stage D milestone**

```bash
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit --allow-empty -m "Stage D complete: cache hit short-circuits ApplyPatches successfully"
```

---

## Stage E — Correctness validation

Goal: prove the cache-hit path produces a `DefDatabase` that is equivalent to the cache-miss path.

### Task E1: Write `src/Diagnostics/DiagnosticDump.cs`

**Files:**
- Create: `/home/keenan/github/rimworld-defload-cache/src/Diagnostics/DiagnosticDump.cs`

- [ ] **Step 1: Create the Diagnostics folder and write DiagnosticDump.cs**

```bash
mkdir -p /home/keenan/github/rimworld-defload-cache/src/Diagnostics
```

Write `/home/keenan/github/rimworld-defload-cache/src/Diagnostics/DiagnosticDump.cs` with EXACT contents:

```csharp
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Dumps a structural snapshot of every Def loaded into DefDatabase&lt;T&gt;
    /// after the load completes. Used for Stage E equivalence checking: a
    /// cache-miss launch and a cache-hit launch should produce identical dumps.
    ///
    /// Runs via [StaticConstructorOnStartup] which fires AFTER def load.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class DiagnosticDump
    {
        static DiagnosticDump()
        {
            try
            {
                string outPath = Path.Combine(
                    GenFilePaths.SaveDataFolderPath,
                    "DefLoadCache_diag_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".txt");

                var sb = new StringBuilder();
                sb.AppendLine("# DefLoadCache diagnostic dump");
                sb.AppendLine($"# timestamp: {DateTime.UtcNow:o}");
                sb.AppendLine($"# rimworld: {VersionControl.CurrentVersionString}");
                sb.AppendLine();

                int totalDefs = 0;
                int defTypes = 0;

                // Find all DefDatabase<T> for every concrete subtype of Def
                foreach (var defType in typeof(Def).Assembly.GetTypes()
                    .Where(t => typeof(Def).IsAssignableFrom(t) && !t.IsAbstract)
                    .OrderBy(t => t.FullName))
                {
                    Type dbType = typeof(DefDatabase<>).MakeGenericType(defType);
                    var allDefsProp = dbType.GetProperty("AllDefs", BindingFlags.Public | BindingFlags.Static);
                    if (allDefsProp == null) continue;
                    var enumerable = allDefsProp.GetValue(null) as IEnumerable;
                    if (enumerable == null) continue;

                    var defsList = enumerable.Cast<Def>().OrderBy(d => d.defName).ToList();
                    if (defsList.Count == 0) continue;
                    defTypes++;
                    totalDefs += defsList.Count;

                    sb.Append(defType.FullName).Append('\t').Append(defsList.Count).AppendLine();

                    // Hash a sample of defs to catch lossy round-trips
                    using (var sha = SHA256.Create())
                    {
                        var sample = defsList.Take(20);
                        foreach (var def in sample)
                        {
                            string fingerprint =
                                def.defName + "|" +
                                (def.label ?? "") + "|" +
                                (def.description ?? "").Length + "|" +
                                (def.modContentPack?.PackageId ?? "<no-mod>");
                            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(fingerprint));
                            string hex = BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
                            sb.Append("  ").Append(def.defName).Append(' ').Append(hex).AppendLine();
                        }
                    }
                }

                sb.AppendLine();
                sb.AppendLine($"# total def types: {defTypes}");
                sb.AppendLine($"# total defs:      {totalDefs}");

                File.WriteAllText(outPath, sb.ToString());
                Log.Message($"DiagnosticDump: wrote {totalDefs} defs across {defTypes} types to {Path.GetFileName(outPath)}");
            }
            catch (Exception ex)
            {
                Log.Error("DiagnosticDump threw", ex);
            }
        }
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build DefLoadCache.csproj 2>&1 | tail -10
```
Expected: `Build succeeded.`

- [ ] **Step 3: Re-deploy and run the comparison**

```bash
cp /home/keenan/github/rimworld-defload-cache/Assemblies/DefLoadCache.dll \
   "/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/DefLoadCache/Assemblies/DefLoadCache.dll"
```

**Run 1 — cache MISS (force fresh):**
```bash
rm -rf "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/DefLoadCache"
rm -f "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/DefLoadCache_diag_"*.txt
```
Launch RimWorld. Wait for main menu. Quit.

**Run 2 — cache HIT (no changes):**
Launch RimWorld again. Wait for main menu. Quit.

- [ ] **Step 4: Diff the two diagnostic dumps**

```bash
DIAG_DIR="/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios"
ls -t "$DIAG_DIR"/DefLoadCache_diag_*.txt | head -2
DIAG_RUN1=$(ls -t "$DIAG_DIR"/DefLoadCache_diag_*.txt | head -2 | tail -1)   # older = run 1 (miss)
DIAG_RUN2=$(ls -t "$DIAG_DIR"/DefLoadCache_diag_*.txt | head -1)              # newer = run 2 (hit)

echo "Run 1 (miss): $DIAG_RUN1"
echo "Run 2 (hit):  $DIAG_RUN2"
echo ""
diff "$DIAG_RUN1" "$DIAG_RUN2" | head -50
```

**Expected:** the diff is empty (or shows ONLY the timestamp lines at the top of each file). Per-type def counts must match. Per-def sample hashes must match.

If the diff shows different def counts: Stage E fails. Investigate which def types differ. Likely cause: cached doc is missing nodes or has extra nodes vs. live-patched doc.

If the diff shows different sample hashes for the same def: Stage E fails. The serialized doc has different content than the original. Likely cause: serialization is lossy somehow.

If the diff is empty (or only timestamps differ): **Stage E succeeds.**

- [ ] **Step 5: Commit**

```bash
git add src/Diagnostics/DiagnosticDump.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add DiagnosticDump for Stage E equivalence validation"
```

- [ ] **Step 6: Stage E milestone**

```bash
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit --allow-empty -m "Stage E complete: cache-hit DefDatabase matches cache-miss"
```

---

## Stage F — Benchmark

Goal: measure the actual `ApplyPatches` wall-clock speedup. POC viable iff ≥50% reduction.

### Task F1: Add Stopwatch instrumentation around the original ApplyPatches body

**Files:**
- Modify: `/home/keenan/github/rimworld-defload-cache/src/Hook/CacheHook.cs`
- Modify: `/home/keenan/github/rimworld-defload-cache/src/Prepatcher/IlInjector.cs`

For Stage F, we want to measure how long the ORIGINAL `ApplyPatches` body would have run, and compare against the cache-hit path's "deserialize + populate" time.

The simplest measurement strategy: log a timestamp at HookFired (start of ApplyPatches) and at SaveToCache (end of cache-miss path) or after TryLoadCached returns true (end of cache-hit path). The duration between is the per-launch ApplyPatches wall-clock.

CacheHook.cs already does this implicitly via the existing log messages. Let's add explicit Stopwatch wrappers and a final summary line.

- [ ] **Step 1: Replace CacheHook.cs with the Stage F version (adds explicit timing log)**

Write `/home/keenan/github/rimworld-defload-cache/src/Hook/CacheHook.cs` with EXACT contents:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    public static class CacheHook
    {
        private static string? _currentFingerprint;
        private static Stopwatch? _wholeApplyPatches;
        private static bool _wasCacheHit;

        public static bool TryLoadCached(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            try
            {
                _wholeApplyPatches = Stopwatch.StartNew();
                _wasCacheHit = false;

                Log.Message("hook fired — Verse.LoadedModManager.ApplyPatches entered");

                var swFp = Stopwatch.StartNew();
                _currentFingerprint = ModlistFingerprint.Compute();
                swFp.Stop();
                Log.Message($"fingerprint = {_currentFingerprint} (computed in {swFp.ElapsedMilliseconds}ms)");

                if (!CacheStorage.TryRead(_currentFingerprint, out var bytes))
                {
                    Log.Message("cache MISS — running normal ApplyPatches");
                    return false;
                }

                var swLoad = Stopwatch.StartNew();
                XmlDocument cachedDoc = CacheFormat.Deserialize(bytes);
                swLoad.Stop();

                xmlDoc.RemoveAll();
                foreach (XmlNode child in cachedDoc.ChildNodes)
                {
                    XmlNode imported = xmlDoc.ImportNode(child, deep: true);
                    xmlDoc.AppendChild(imported);
                }

                int rebuilt = ModAttributionTagger.RebuildAssetLookup(xmlDoc, assetlookup);

                _wasCacheHit = true;
                _wholeApplyPatches.Stop();

                Log.Message($"cache HIT — deserialized + populated in {swLoad.ElapsedMilliseconds}ms, " +
                            $"{rebuilt} assetlookup entries rebuilt. Total ApplyPatches replacement: " +
                            $"{_wholeApplyPatches.ElapsedMilliseconds}ms (vs original XPath walk).");

                return true;
            }
            catch (Exception ex)
            {
                Log.Error("TryLoadCached threw — falling back to normal ApplyPatches", ex);
                return false;
            }
        }

        public static void SaveToCache(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            try
            {
                if (_wholeApplyPatches != null && _wholeApplyPatches.IsRunning)
                {
                    _wholeApplyPatches.Stop();
                    Log.Message($"BENCHMARK: ApplyPatches (cache MISS) took {_wholeApplyPatches.ElapsedMilliseconds}ms total");
                }

                if (_currentFingerprint == null)
                {
                    Log.Warning("SaveToCache: no fingerprint cached, skipping save");
                    return;
                }
                if (xmlDoc == null)
                {
                    Log.Warning("SaveToCache: xmlDoc is null, skipping save");
                    return;
                }

                string existingPath = CacheStorage.PathForFingerprint(_currentFingerprint);
                if (System.IO.File.Exists(existingPath))
                {
                    Log.Message("SaveToCache: cache file already exists for this fingerprint, skipping save");
                    return;
                }

                var sw = Stopwatch.StartNew();
                ModAttributionTagger.StampAttributions(xmlDoc, assetlookup);
                byte[] bytes = CacheFormat.Serialize(xmlDoc);
                sw.Stop();

                string metaJson = "{"
                    + $"\"timestamp\":\"{DateTime.UtcNow:o}\","
                    + $"\"modCount\":{LoadedModManager.RunningModsListForReading.Count},"
                    + $"\"rimworldVersion\":\"{VersionControl.CurrentVersionString}\","
                    + $"\"cacheFormatVersion\":{ModlistFingerprint.CacheFormatVersion},"
                    + $"\"sizeBytes\":{bytes.Length}"
                    + "}";

                CacheStorage.Write(_currentFingerprint, bytes, metaJson);
                Log.Message($"SaveToCache: serialized + wrote in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Log.Error("SaveToCache threw — cache not saved (game continues normally)", ex);
            }
        }
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build DefLoadCache.csproj 2>&1 | tail -10
```
Expected: `Build succeeded.`

- [ ] **Step 3: Re-deploy and run the benchmark**

```bash
cp /home/keenan/github/rimworld-defload-cache/Assemblies/DefLoadCache.dll \
   "/mnt/c/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/DefLoadCache/Assemblies/DefLoadCache.dll"
```

Run THREE cache-miss launches (delete cache between each) and THREE cache-hit launches:

```bash
# Run 1 - miss
rm -rf "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/DefLoadCache"
# Launch RimWorld, wait for main menu, quit. Note the BENCHMARK line in the log.

# Run 2 - miss
rm -rf "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/DefLoadCache"
# Launch, quit.

# Run 3 - miss
rm -rf "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/DefLoadCache"
# Launch, quit.

# Run 4 - hit (uses the cache from a previous run; recreate it first)
# Launch, quit.

# Run 5 - hit
# Launch, quit.

# Run 6 - hit
# Launch, quit.
```

After all six runs, gather the timing data:

```bash
LOG="/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log"
echo "=== MISS runs ==="
grep "BENCHMARK: ApplyPatches (cache MISS)" "$LOG"
echo ""
echo "=== HIT runs ==="
grep "Total ApplyPatches replacement" "$LOG"
```

- [ ] **Step 4: Compute means and decide POC viability**

Manually average the three MISS values and the three HIT values. Compute the ratio:

```
mean_miss = (miss1 + miss2 + miss3) / 3
mean_hit  = (hit1 + hit2 + hit3) / 3
speedup   = (mean_miss - mean_hit) / mean_miss * 100  (percent reduction)
```

**POC viability bar:** `speedup ≥ 50%` OR `(mean_miss - mean_hit) ≥ 5000ms`

- If both met → **POC viable.** Document the numbers in the commit message and proceed to Stage F final commit.
- If neither met → POC not viable on the test modlist with the chosen hook point. Document the disappointing numbers honestly in the commit message. Consider Stage G follow-up: hook earlier in the load pipeline.
- If correctness held but speedup is borderline → document and decide with the user whether to graduate or stop.

- [ ] **Step 5: Commit**

```bash
git add src/Hook/CacheHook.cs
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit -m "Add Stopwatch benchmark for ApplyPatches cache-hit vs miss"
```

- [ ] **Step 6: Stage F milestone with measurements**

After running the benchmark, use the actual measurements:

```bash
# Replace MISS_MEAN_MS and HIT_MEAN_MS with the actual measured numbers
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit --allow-empty -m "$(cat <<'EOF'
Stage F complete: benchmark on minimal test modlist

ApplyPatches (cache MISS, mean of 3 runs): MISS_MEAN_MS ms
ApplyPatches (cache HIT,  mean of 3 runs): HIT_MEAN_MS ms
Speedup: PERCENT% reduction (ABS_MS ms saved)

POC viability: VIABLE | NOT_VIABLE
EOF
)"
```

---

## Stretch validation (after Stage F)

If Stage F is viable, run ONE launch on the full 576-mod modlist as a smoke test:

```bash
# Switch to Full MilSim modlist in RimWorld
# Delete cache to force a fresh build
rm -rf "/mnt/c/Users/Keenan/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/DefLoadCache"
# Launch RimWorld, wait the full ~10 minutes, observe behavior
# Quit
# Launch again, this time it should hit cache
# Note the speedup
```

If the full modlist crashes, we've found a compatibility bug somewhere in the additional 561 mods. Triage by:
1. Checking which def caused the crash (Player.log will say)
2. Identifying the source mod
3. Considering whether the crash is fixable in DefLoadCache or whether it's a fundamental incompat with that mod

If the full modlist works and shows good speedup, document the numbers separately:
```bash
git -c user.name="FluxxField" -c user.email="fluxxfield@local" commit --allow-empty -m "$(cat <<'EOF'
Stretch validation: full 576-mod modlist results

ApplyPatches (cache MISS): X ms
ApplyPatches (cache HIT):  Y ms
Speedup: Z% reduction

Notes: any compatibility issues, warnings, etc.
EOF
)"
```

---

## Self-review checklist (run after writing the plan)

- **Spec coverage:** Every section of the spec has a corresponding task (or set of tasks) in this plan. Architecture → Tasks A1-A5. Cache lifecycle → Tasks C1-C4 + D1-D2. Stages A-F → labeled stage headers. Risks → mostly mitigated by stage-by-stage failure detection. Open questions from the spec → resolved via decompilation results in the Reference Data section.
- **Placeholders:** No "TBD", "implement later", or "see Task N" without code. Every code-changing step has the literal file contents to write. The benchmark commit message has placeholder values (`MISS_MEAN_MS`, `HIT_MEAN_MS`, `PERCENT`, `ABS_MS`, `VIABLE`/`NOT_VIABLE`) which the implementer fills in with measured values — that's correct because we can't know the values until the benchmark runs.
- **Type consistency:** `CacheHook.HookFired()` (Stage A/B) gradually evolves into `CacheHook.TryLoadCached(...)` (Stage D). Each stage's plan rewrites the file completely so no stale references survive. The `IlInjector` class similarly evolves; each stage rewrites it. Consistent use of `XmlDocument`, `Dictionary<XmlNode, LoadableXmlAsset>`, `MethodReference`, `Instruction` throughout.
- **Compile order:** Each task is independently compilable. Adding a file in stage X never requires modifications to a file from stage X+1 (which doesn't exist yet).
- **One known unknown:** the `LoadableXmlAsset` constructor signature in Task C3. The plan flags this and tells the implementer to decompile the type if the constructor call doesn't compile. Acceptable because we can't know the exact constructor without inspecting it, and it's a one-line fix.
