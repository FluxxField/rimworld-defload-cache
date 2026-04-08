using System;
using System.Reflection;
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
    ///
    /// Implementation note: Mono.Cecil types are internal inside 0Harmony.dll
    /// (ILRepack merged). We cannot reference them with static typing from a
    /// third-party mod assembly. Instead we receive ModuleDefinition as object
    /// and drive all Cecil operations through reflection / dynamic dispatch.
    /// This is the standard pattern for third-party [FreePatch] consumers.
    /// </summary>
    public static class IlInjector
    {
        [FreePatch]
        private static void InjectApplyPatchesHook(object module)
        {
            try
            {
                DoInject(module);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: unhandled exception — " + ex);
            }
        }

        private static void DoInject(object module)
        {
            // ── Locate Verse.LoadedModManager in the module ──────────────────
            // module.GetType(string fullName) → TypeDefinition
            var moduleType = module.GetType();
            var getTypeMethod = moduleType.GetMethod("GetType", new[] { typeof(string) });
            if (getTypeMethod == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: ModuleDefinition.GetType(string) not found");
                return;
            }

            object? loadedModManagerType = getTypeMethod.Invoke(module, new object[] { "Verse.LoadedModManager" });
            if (loadedModManagerType == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: Verse.LoadedModManager not found in module");
                return;
            }

            // ── Locate ApplyPatches via MonoMod.Utils.Extensions.FindMethod ──
            // MonoMod.Utils.Extensions lives inside 0Harmony.dll (also internal).
            // We find it by scanning loaded assemblies for the type at runtime.
            Type? extensionsType = FindMonoModExtensions();
            if (extensionsType == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: MonoMod.Utils.Extensions not found in any loaded assembly");
                return;
            }

            var findMethodMI = extensionsType.GetMethod(
                "FindMethod",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { loadedModManagerType.GetType(), typeof(string) },
                null);
            if (findMethodMI == null)
            {
                // Try overload without binder / modifiers
                foreach (var m in extensionsType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "FindMethod")
                    {
                        var parms = m.GetParameters();
                        if (parms.Length == 2 && parms[1].ParameterType == typeof(string))
                        {
                            findMethodMI = m;
                            break;
                        }
                    }
                }
            }

            if (findMethodMI == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: MonoMod.Utils.Extensions.FindMethod not found");
                return;
            }

            object? applyPatchesMethod = findMethodMI.Invoke(null, new object[] { loadedModManagerType, "ApplyPatches" });
            if (applyPatchesMethod == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: ApplyPatches method not found via FindMethod");
                return;
            }

            // ── Reflect our own CacheHook.HookFired ──────────────────────────
            MethodInfo? hookFiredMethod = typeof(CacheHook).GetMethod(
                nameof(CacheHook.HookFired),
                BindingFlags.Public | BindingFlags.Static);
            if (hookFiredMethod == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: CacheHook.HookFired not found via reflection");
                return;
            }

            // ── Import HookFired into the target module ───────────────────────
            // module.ImportReference(MethodBase) → MethodReference
            var importRefMethod = moduleType.GetMethod(
                "ImportReference",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(MethodBase) },
                null);
            if (importRefMethod == null)
            {
                // Fallback: find any ImportReference overload that takes a MethodBase
                foreach (var m in moduleType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "ImportReference")
                    {
                        var parms = m.GetParameters();
                        if (parms.Length == 1 && (parms[0].ParameterType == typeof(MethodBase) || parms[0].ParameterType.IsAssignableFrom(typeof(MethodInfo))))
                        {
                            importRefMethod = m;
                            break;
                        }
                    }
                }
            }

            if (importRefMethod == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: ModuleDefinition.ImportReference(MethodBase) not found");
                return;
            }

            object hookFiredRef = importRefMethod.Invoke(module, new object[] { hookFiredMethod })!;

            // ── Get the ILProcessor for ApplyPatches ──────────────────────────
            // applyPatchesMethod.Body.GetILProcessor()
            var applyPatchesType = applyPatchesMethod.GetType();
            var bodyProp = applyPatchesType.GetProperty("Body");
            if (bodyProp == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: MethodDefinition.Body property not found");
                return;
            }

            object body = bodyProp.GetValue(applyPatchesMethod)!;
            var getILProcessorMethod = body.GetType().GetMethod("GetILProcessor");
            if (getILProcessorMethod == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: MethodBody.GetILProcessor() not found");
                return;
            }

            object ilProcessor = getILProcessorMethod.Invoke(body, null)!;

            // ── Get the first instruction ─────────────────────────────────────
            // body.Instructions[0]
            var instructionsProp = body.GetType().GetProperty("Instructions");
            if (instructionsProp == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: MethodBody.Instructions not found");
                return;
            }

            object instructions = instructionsProp.GetValue(body)!;
            // Instructions implements IList/ICollection; index via reflection
            var getItemMethod = instructions.GetType().GetMethod("get_Item") ??
                                instructions.GetType().GetMethod("Get");
            // Fallback: use IList interface
            System.Collections.IList? instructionList = instructions as System.Collections.IList;
            object? firstInstruction = instructionList?[0];
            if (firstInstruction == null && getItemMethod != null)
                firstInstruction = getItemMethod.Invoke(instructions, new object[] { 0 });
            if (firstInstruction == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: failed to get first instruction");
                return;
            }

            // ── Find the OpCodes.Call field ───────────────────────────────────
            // OpCodes lives in Mono.Cecil.Cil, also internal in 0Harmony
            object? opCodeCall = FindOpCodeCall();
            if (opCodeCall == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: OpCodes.Call not found");
                return;
            }

            // ── Create the call instruction ───────────────────────────────────
            // ilProcessor.Create(OpCode, MethodReference) → Instruction
            Type ilProcessorType = ilProcessor.GetType();
            MethodInfo? createMethod = null;
            foreach (var m in ilProcessorType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "Create")
                {
                    var parms = m.GetParameters();
                    if (parms.Length == 2)
                    {
                        createMethod = m;
                        break;
                    }
                }
            }

            if (createMethod == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: ILProcessor.Create(OpCode, MethodReference) not found");
                return;
            }

            object callInstruction = createMethod.Invoke(ilProcessor, new object[] { opCodeCall, hookFiredRef })!;

            // ── InsertBefore first instruction ────────────────────────────────
            var insertBeforeMethod = ilProcessorType.GetMethod(
                "InsertBefore",
                BindingFlags.Public | BindingFlags.Instance);
            if (insertBeforeMethod == null)
            {
                Console.WriteLine("[DefLoadCache] FreePatch: ILProcessor.InsertBefore not found");
                return;
            }

            insertBeforeMethod.Invoke(ilProcessor, new object[] { firstInstruction, callInstruction });

            Console.WriteLine("[DefLoadCache] FreePatch: injected HookFired call into ApplyPatches");
        }

        /// <summary>
        /// Finds the MonoMod.Utils.Extensions type by scanning all assemblies
        /// currently loaded in the AppDomain. It lives inside 0Harmony.dll as
        /// an internal type.
        /// </summary>
        private static Type? FindMonoModExtensions()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.FullName == "MonoMod.Utils.Extensions")
                            return t;
                    }
                }
                catch { /* skip assemblies we can't inspect */ }
            }
            return null;
        }

        /// <summary>
        /// Finds the OpCodes.Call value by scanning loaded assemblies for
        /// Mono.Cecil.Cil.OpCodes (internal in 0Harmony.dll).
        /// </summary>
        private static object? FindOpCodeCall()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.FullName == "Mono.Cecil.Cil.OpCodes")
                        {
                            var callField = t.GetField("Call",
                                BindingFlags.Public | BindingFlags.Static);
                            if (callField != null)
                                return callField.GetValue(null);
                        }
                    }
                }
                catch { /* skip */ }
            }
            return null;
        }
    }
}
