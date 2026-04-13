using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prepatcher;

namespace FluxxField.DefLoadCache.Prepatcher
{
    /// <summary>
    /// Dumps the IL of ApplyPatches to a log file so we can understand its
    /// internal loop structure for checkpoint injection. Gated behind a const.
    /// </summary>
    public static class IlDumper
    {
        internal const bool DumpEnabled = false;

        [FreePatch]
        private static void DumpApplyPatchesIL(ModuleDefinition module)
        {
            if (!DumpEnabled) return;

            var loadedModManagerType = module.GetType("Verse.LoadedModManager");
            if (loadedModManagerType == null) return;

            var method = loadedModManagerType.Methods
                .FirstOrDefault(m => m.Name == "ApplyPatches");
            if (method == null) return;

            var sb = new StringBuilder();
            sb.AppendLine("=== ApplyPatches IL Dump ===");
            sb.AppendLine($"Parameters: {string.Join(", ", method.Parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"))}");
            sb.AppendLine($"Return type: {method.ReturnType.Name}");
            sb.AppendLine($"Locals: {string.Join(", ", method.Body.Variables.Select(v => $"[{v.Index}] {v.VariableType.Name}"))}");
            sb.AppendLine($"Instructions: {method.Body.Instructions.Count}");
            sb.AppendLine($"Exception handlers: {method.Body.ExceptionHandlers.Count}");
            sb.AppendLine();

            // Dump exception handlers
            foreach (var handler in method.Body.ExceptionHandlers)
            {
                sb.AppendLine($"  Handler: {handler.HandlerType} Try[{handler.TryStart?.Offset:X4}-{handler.TryEnd?.Offset:X4}] Handler[{handler.HandlerStart?.Offset:X4}-{handler.HandlerEnd?.Offset:X4}]");
            }
            sb.AppendLine();

            // Dump all instructions with method call details
            foreach (var inst in method.Body.Instructions)
            {
                string extra = "";
                if (inst.Operand is MethodReference mr)
                {
                    extra = $" // {mr.DeclaringType.Name}.{mr.Name}";
                }
                else if (inst.Operand is FieldReference fr)
                {
                    extra = $" // {fr.DeclaringType.Name}.{fr.Name}";
                }
                else if (inst.Operand is TypeReference tr)
                {
                    extra = $" // {tr.Name}";
                }
                else if (inst.Operand is Instruction target)
                {
                    extra = $" -> IL_{target.Offset:X4}";
                }

                sb.AppendLine($"  IL_{inst.Offset:X4}: {inst.OpCode} {inst.Operand}{extra}");
            }

            System.Console.WriteLine(sb.ToString());
        }
    }
}
