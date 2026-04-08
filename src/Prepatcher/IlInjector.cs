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
                il.InsertBefore(ret, ldArgDoc);
                il.InsertBefore(ret, ldArgLookup);
                il.InsertBefore(ret, callSave);

                // Fix up exception handler boundaries. In Cecil, ExceptionHandler
                // TryEnd/HandlerEnd are *exclusive* pointers to the first
                // instruction AFTER the protected region. If any handler's end
                // pointer referenced the original ret, inserting instructions
                // before the ret would implicitly enlarge the handler to cover
                // our injected code. Re-pointing the end to our first injected
                // instruction (ldArgDoc) preserves the original extent so our
                // SaveToCache call runs OUTSIDE the exception handler region.
                foreach (var handler in applyPatchesMethod.Body.ExceptionHandlers)
                {
                    if (handler.TryEnd == ret) handler.TryEnd = ldArgDoc;
                    if (handler.HandlerEnd == ret) handler.HandlerEnd = ldArgDoc;
                    if (handler.FilterStart == ret) handler.FilterStart = ldArgDoc;
                }
            }

            System.Console.WriteLine($"[DefLoadCache] FreePatch: injected HookFired + {retInstructions.Count} SaveToCache call(s) into ApplyPatches");
        }
    }
}
