using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prepatcher;

namespace FluxxField.DefLoadCache.Prepatcher
{
    /// <summary>
    /// Stage C: injects HookFired() at the start AND SaveToCache(xmlDoc, assetlookup)
    /// before every ret in Verse.LoadedModManager.ApplyPatches. The save call
    /// passes the method's two parameters via ldarg.0 and ldarg.1.
    ///
    /// Stage D will further extend this to wrap the HookFired call with a
    /// TryLoadCached prefix that short-circuits the method body via brtrue.
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
            MethodInfo? saveToCacheMethod = typeof(CacheHook).GetMethod(
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

            // Guard against empty method body
            if (applyPatchesMethod.Body.Instructions.Count == 0)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: ApplyPatches body is empty (abstract or unresolved?)");
                return;
            }

            // 1. Inject `call CacheHook::HookFired()` at the very top
            Instruction firstInstruction = applyPatchesMethod.Body.Instructions[0];
            Instruction callHookFired = il.Create(OpCodes.Call, hookFiredRef);
            il.InsertBefore(firstInstruction, callHookFired);

            // 2. Inject `ldarg.0; ldarg.1; call CacheHook::SaveToCache` before EVERY ret.
            // Snapshot the ret positions FIRST so we don't iterate while mutating.
            var retInstructions = applyPatchesMethod.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ret)
                .ToList();

            foreach (var ret in retInstructions)
            {
                var ldArgDoc = il.Create(OpCodes.Ldarg_0);
                var ldArgLookup = il.Create(OpCodes.Ldarg_1);
                var callSave = il.Create(OpCodes.Call, saveToCacheRef);

                // CRITICAL: before inserting, re-point any branch whose operand
                // is this ret to target our first injected instruction instead.
                // Otherwise, leave.s/br instructions that previously landed on
                // the ret will continue to target the ret directly, bypassing
                // our inserted SaveToCache call and leaving it as dead code.
                //
                // In the ApplyPatches foreach-with-try/catch body, each catch
                // block ends with `leave.s ret_target` — without this fixup,
                // SaveToCache is never reached and no cache file is written.
                foreach (var inst in applyPatchesMethod.Body.Instructions)
                {
                    if (inst.Operand == ret)
                    {
                        inst.Operand = ldArgDoc;
                    }
                }

                // Similarly for multi-target switch instructions.
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

                // Fix up exception handler boundaries. TryEnd/HandlerEnd are
                // *exclusive* pointers — they point to the first instruction
                // AFTER the protected region. Re-pointing them to ldArgDoc
                // preserves the original extent so our SaveToCache call runs
                // OUTSIDE the exception handler region.
                foreach (var handler in applyPatchesMethod.Body.ExceptionHandlers)
                {
                    if (handler.TryStart == ret) handler.TryStart = ldArgDoc;
                    if (handler.TryEnd == ret) handler.TryEnd = ldArgDoc;
                    if (handler.HandlerStart == ret) handler.HandlerStart = ldArgDoc;
                    if (handler.HandlerEnd == ret) handler.HandlerEnd = ldArgDoc;
                    if (handler.FilterStart == ret) handler.FilterStart = ldArgDoc;
                }

                // Now safe to insert.
                il.InsertBefore(ret, ldArgDoc);
                il.InsertBefore(ret, ldArgLookup);
                il.InsertBefore(ret, callSave);
            }

            System.Console.WriteLine($"[DefLoadCache] FreePatch: injected HookFired + {retInstructions.Count} SaveToCache call(s) into ApplyPatches");
        }
    }
}
