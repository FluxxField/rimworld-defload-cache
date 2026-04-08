using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
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

            // Find ApplyPatches by name (matches the first method with that name)
            var applyPatchesMethod = loadedModManagerType.Methods
                .FirstOrDefault(m => m.Name == "ApplyPatches");
            if (applyPatchesMethod == null)
            {
                System.Console.WriteLine("[DefLoadCache] FreePatch: ApplyPatches method not found");
                return;
            }

            // Resolve the managed hook target via reflection on our own assembly
            MethodInfo? hookFiredMethod = typeof(CacheHook).GetMethod(
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
