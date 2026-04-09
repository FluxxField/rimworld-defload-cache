using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prepatcher;

namespace FluxxField.DefLoadCache.Prepatcher
{
    /// <summary>
    /// Stages A+B+C+D IL injection into Verse.LoadedModManager.ApplyPatches.
    ///
    /// Layout after injection:
    ///
    ///   call HookFired                    (A/B: fingerprint computation)
    ///   ldarg.0                           (D: xmlDoc)
    ///   ldarg.1                           (D: assetlookup)
    ///   call TryLoadCached                (D: returns true on cache hit)
    ///   brtrue ret                        (D: on hit, jump past everything to ret)
    ///   &lt;original body&gt;                   (runs on cache miss)
    ///   ldarg.0                           (C: xmlDoc for SaveToCache)
    ///   ldarg.1                           (C: assetlookup for SaveToCache)
    ///   call SaveToCache                  (C: serialize + write on miss)
    ///   ret
    ///
    /// Cache-hit execution: HookFired → TryLoadCached → brtrue jumps directly
    /// to ret, skipping original body AND SaveToCache.
    ///
    /// Cache-miss execution: HookFired → TryLoadCached returns false → fall
    /// through to original body → leave.s branches from try/catch target
    /// ldArgDoc (Stage C retargeting) → SaveToCache runs → ret.
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

            var applyPatchesMethod = loadedModManagerType.Methods
                .FirstOrDefault(m => m.Name == "ApplyPatches");
            if (applyPatchesMethod == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: ApplyPatches method not found");
                return;
            }

            // Resolve managed hook targets via reflection
            MethodInfo? hookFiredMethod = typeof(CacheHook).GetMethod(
                nameof(CacheHook.HookFired),
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo? tryLoadCachedMethod = typeof(CacheHook).GetMethod(
                nameof(CacheHook.TryLoadCached),
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo? saveToCacheMethod = typeof(CacheHook).GetMethod(
                nameof(CacheHook.SaveToCache),
                BindingFlags.Public | BindingFlags.Static);
            if (hookFiredMethod == null || tryLoadCachedMethod == null || saveToCacheMethod == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: required hook methods not found via reflection");
                return;
            }

            MethodReference hookFiredRef = module.ImportReference(hookFiredMethod);
            MethodReference tryLoadCachedRef = module.ImportReference(tryLoadCachedMethod);
            MethodReference saveToCacheRef = module.ImportReference(saveToCacheMethod);

            ILProcessor il = applyPatchesMethod.Body.GetILProcessor();

            if (applyPatchesMethod.Body.Instructions.Count == 0)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: ApplyPatches body is empty (abstract or unresolved?)");
                return;
            }

            // Capture the original first instruction BEFORE any insertion
            Instruction firstInstruction = applyPatchesMethod.Body.Instructions[0];

            // 1. Stage A: inject `call HookFired()` at the very top
            Instruction callHookFired = il.Create(OpCodes.Call, hookFiredRef);
            il.InsertBefore(firstInstruction, callHookFired);

            // 2. Stage C: inject SaveToCache postfix before every ret, with
            //    full branch-retargeting and exception handler fixup.
            var retInstructions = applyPatchesMethod.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ret)
                .ToList();

            foreach (var ret in retInstructions)
            {
                var ldArgDoc = il.Create(OpCodes.Ldarg_0);
                var ldArgLookup = il.Create(OpCodes.Ldarg_1);
                var callSave = il.Create(OpCodes.Call, saveToCacheRef);

                // Retarget any branch (leave.s from catch blocks, etc.) that
                // targeted this ret to instead target our first injected
                // instruction. Otherwise those branches land directly on ret,
                // bypassing our SaveToCache injection as dead code.
                foreach (var inst in applyPatchesMethod.Body.Instructions)
                {
                    if (inst.Operand == ret)
                    {
                        inst.Operand = ldArgDoc;
                    }
                }

                // Also retarget multi-target switch Operand arrays
                foreach (var inst in applyPatchesMethod.Body.Instructions)
                {
                    if (inst.Operand is Instruction[] targets)
                    {
                        for (int i = 0; i < targets.Length; i++)
                        {
                            if (targets[i] == ret) targets[i] = ldArgDoc;
                        }
                    }
                }

                // Fix exception handler boundaries. TryEnd/HandlerEnd are
                // exclusive pointers; if they referenced ret, re-point to
                // ldArgDoc so our postfix runs outside the protected region.
                foreach (var handler in applyPatchesMethod.Body.ExceptionHandlers)
                {
                    if (handler.TryStart == ret) handler.TryStart = ldArgDoc;
                    if (handler.TryEnd == ret) handler.TryEnd = ldArgDoc;
                    if (handler.HandlerStart == ret) handler.HandlerStart = ldArgDoc;
                    if (handler.HandlerEnd == ret) handler.HandlerEnd = ldArgDoc;
                    if (handler.FilterStart == ret) handler.FilterStart = ldArgDoc;
                }

                il.InsertBefore(ret, ldArgDoc);
                il.InsertBefore(ret, ldArgLookup);
                il.InsertBefore(ret, callSave);
            }

            // 3. Stage D: inject TryLoadCached prefix AFTER the Stage C
            //    postfix work. Placement: between the HookFired call and the
            //    original first instruction.
            //
            //    The brtrue operand targets the LAST ret directly (Cecil
            //    preserves Instruction references, so even though we've
            //    inserted instructions before ret in Stage C, brtrue's
            //    operand is still ret — the branch lands past the whole
            //    SaveToCache postfix).
            //
            //    This step MUST run AFTER the Stage C retargeting loop above.
            //    If brtrue were inserted before that loop, the loop would
            //    helpfully retarget its operand from ret to ldArgDoc, which
            //    is the opposite of what we want.
            Instruction brtrueTarget = retInstructions[retInstructions.Count - 1];
            var ldArg0_D = il.Create(OpCodes.Ldarg_0);
            var ldArg1_D = il.Create(OpCodes.Ldarg_1);
            var callTryLoad = il.Create(OpCodes.Call, tryLoadCachedRef);
            var brtrueCacheHit = il.Create(OpCodes.Brtrue, brtrueTarget);

            il.InsertBefore(firstInstruction, ldArg0_D);
            il.InsertBefore(firstInstruction, ldArg1_D);
            il.InsertBefore(firstInstruction, callTryLoad);
            il.InsertBefore(firstInstruction, brtrueCacheHit);

            System.Console.WriteLine($"[DefLoadCache] FreePatch: injected HookFired + TryLoadCached prefix + {retInstructions.Count} SaveToCache postfix(es) into ApplyPatches");
        }

        /// <summary>
        /// Injects a skip-prefix into ClearCachedPatches so that on cache-hit
        /// runs the method is a no-op. Without this, ClearCachedPatches iterates
        /// every PatchOperation and logs "failed" for each one (because patches
        /// were never executed — ApplyPatches was skipped). On a 576-mod list
        /// that's 10k+ error log writes.
        ///
        /// Layout after injection:
        ///
        ///   call ShouldSkipClearPatches       (returns true on cache hit)
        ///   brtrue ret                        (skip entire method)
        ///   &lt;original body&gt;
        ///   ret
        /// </summary>
        [FreePatch]
        private static void InjectClearCachedPatchesSkip(ModuleDefinition module)
        {
            var loadedModManagerType = module.GetType("Verse.LoadedModManager");
            if (loadedModManagerType == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: Verse.LoadedModManager not found (ClearCachedPatches skip)");
                return;
            }

            var clearMethod = loadedModManagerType.Methods
                .FirstOrDefault(m => m.Name == "ClearCachedPatches");
            if (clearMethod == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: ClearCachedPatches method not found");
                return;
            }

            MethodInfo? shouldSkipMethod = typeof(CacheHook).GetMethod(
                nameof(CacheHook.ShouldSkipClearPatches),
                BindingFlags.Public | BindingFlags.Static);
            if (shouldSkipMethod == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: ShouldSkipClearPatches not found via reflection");
                return;
            }

            MethodReference shouldSkipRef = module.ImportReference(shouldSkipMethod);
            ILProcessor il = clearMethod.Body.GetILProcessor();

            if (clearMethod.Body.Instructions.Count == 0)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: ClearCachedPatches body is empty");
                return;
            }

            // Find the last ret to use as brtrue target
            var lastRet = clearMethod.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);

            Instruction firstInstruction = clearMethod.Body.Instructions[0];
            var callSkip = il.Create(OpCodes.Call, shouldSkipRef);
            var brSkip = il.Create(OpCodes.Brtrue, lastRet);

            il.InsertBefore(firstInstruction, callSkip);
            il.InsertBefore(firstInstruction, brSkip);

            System.Console.WriteLine("[DefLoadCache] FreePatch: injected ShouldSkipClearPatches prefix into ClearCachedPatches");
        }
    }
}
