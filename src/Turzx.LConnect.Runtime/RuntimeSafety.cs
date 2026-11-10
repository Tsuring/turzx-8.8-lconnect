using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Turzx.LConnect.Runtime
{
    public static class RuntimeSafety
    {
        public const string ApprovedFingerprint = "4EC87B42798662F95B3049315A4688139389C133F5CA4FEE0D2150F0089C77DF";

        // Replaying an initializer is allowed only for the verified data-only graph.
        public static string Fingerprint(string directory)
        {
            var text = new StringBuilder();
            using (var core = AssemblyDefinition.ReadAssembly(Path.Combine(directory, "L-Connect.Core.dll")))
            using (var sdk = AssemblyDefinition.ReadAssembly(Path.Combine(directory, "lianli.lcd207.dll")))
            {
                Add(text, core.MainModule.GetType("LConnectCore.Devices.KnownDevices"), true);
                Add(text, core.MainModule.GetType("LConnectCore.Devices.USBDeviceID"), false);
                Add(text, core.MainModule.GetType("LConnectCore.Devices.USBDeviceExtendedID"), false);
                Add(text, sdk.MainModule.GetType("lcd207.WinUsbLed"), true);
                Add(text, sdk.MainModule.GetType("lcd207.LEDController"), true);
            }
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
        }

        private static void Add(StringBuilder text, TypeDefinition type, bool initializerOnly)
        {
            if (type == null) throw new InvalidDataException("Runtime safety type is missing.");
            var methods = type.Methods.Where(m => m.HasBody && (!initializerOnly || (m.IsConstructor && m.IsStatic))).OrderBy(m => m.FullName, StringComparer.Ordinal).ToArray();
            if (methods.Length == 0 || (initializerOnly && methods.Length != 1)) throw new InvalidDataException("Runtime safety method layout changed.");
            foreach (var method in methods)
            {
                text.Append(method.FullName).Append('|').Append((int)method.Attributes).Append('|').Append(method.Body.InitLocals).Append('|').Append(method.Body.MaxStackSize).Append('\n');
                foreach (var local in method.Body.Variables) text.Append("local:").Append(local.VariableType.FullName).Append('\n');
                var code = method.Body.Instructions.ToArray();
                foreach (var instruction in code)
                {
                    text.Append(instruction.OpCode.Name).Append(':');
                    var branch = instruction.Operand as Instruction;
                    var branches = instruction.Operand as Instruction[];
                    var member = instruction.Operand as MemberReference;
                    var variable = instruction.Operand as VariableDefinition;
                    var parameter = instruction.Operand as ParameterDefinition;
                    if (branch != null) text.Append(Array.IndexOf(code, branch));
                    else if (branches != null) text.Append(String.Join(",", branches.Select(b => Array.IndexOf(code, b))));
                    else if (member != null) text.Append(member.FullName);
                    else if (variable != null) text.Append("v").Append(variable.Index);
                    else if (parameter != null) text.Append("p").Append(parameter.Index);
                    else if (instruction.Operand != null) text.Append(Convert.ToString(instruction.Operand, CultureInfo.InvariantCulture));
                    text.Append('\n');
                }
                foreach (var handler in method.Body.ExceptionHandlers)
                    text.Append("eh:").Append(handler.HandlerType).Append('|').Append(Array.IndexOf(code, handler.TryStart)).Append('|')
                        .Append(Array.IndexOf(code, handler.TryEnd)).Append('|').Append(Array.IndexOf(code, handler.HandlerStart)).Append('|')
                        .Append(Array.IndexOf(code, handler.HandlerEnd)).Append('|').Append(Array.IndexOf(code, handler.FilterStart)).Append('|')
                        .Append(handler.CatchType == null ? "-" : handler.CatchType.FullName).Append('\n');
            }
        }
    }
}
