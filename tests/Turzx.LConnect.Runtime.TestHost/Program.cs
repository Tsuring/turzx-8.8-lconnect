using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Turzx.LConnect.Runtime;

namespace Turzx.LConnect.Runtime.TestHost
{
    internal static class Program
    {
        private sealed class ToyRegistry
        {
            public static readonly Dictionary<int, int> Values;
            public static int Runs;
            static ToyRegistry() { Runs++; Values = new Dictionary<int, int> { { 1, 0xA088 } }; }
        }

        private sealed class ToyPid
        {
            public static int Value = 0xA088;
            public static int Runs;
            static ToyPid() { Runs++; }
        }

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "safety")
                { Console.WriteLine(RuntimeSafety.Fingerprint(args[1])); return 0; }
                if (args.Length == 1 && args[0] == "validate")
                {
                    if (AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName != ".NETFramework,Version=v4.8")
                        throw new InvalidOperationException("The loader changed the host framework compatibility policy.");
                    var xml = new System.Xml.XmlDocument();
                    xml.LoadXml("<!DOCTYPE root SYSTEM 'file:///turzx-missing-external-subset.dtd'><root />");
                    if (Assembly.GetEntryAssembly() != typeof(Program).Assembly)
                        throw new InvalidOperationException("The CLR did not retain the intended entry assembly.");
                    if (!RuntimeEntryPoint.Ready) throw new InvalidOperationException(RuntimeEntryPoint.Failure ?? "Automatic bootstrap did not initialize the runtime loader.");
                    if (!RuntimeEntryPoint.OpenRgbGuardApplied || RuntimeEntryPoint.CorePid != 0x0088 || RuntimeEntryPoint.SdkPid != 0x0088)
                        throw new InvalidOperationException("Runtime memory patches are incomplete.");
                    object closure = FormatterServices.GetUninitializedObject(RuntimeEntryPoint.OpenRgbCallback.DeclaringType);
                    object lcd = FormatterServices.GetUninitializedObject(RuntimeEntryPoint.LcdControllerType);
                    int before = RuntimeEntryPoint.SkippedRgbControllers;
                    RuntimeEntryPoint.OpenRgbCallback.Invoke(closure, new[] { lcd });
                    if (RuntimeEntryPoint.SkippedRgbControllers != before + 1) throw new InvalidOperationException("The patched callback did not skip the display without RGB.");
                    Console.WriteLine("PASS: automatic pre-Main loader; original vendor files unchanged; Core/SDK PID=0088; reverse lookup valid; actual OpenRGB callback skips a display without RGB.");
                    return 0;
                }
                if (args.Length == 1 && args[0] == "expect-failure")
                {
                    if (RuntimeEntryPoint.Ready || !RuntimeEntryPoint.RollbackComplete || RuntimeEntryPoint.OpenRgbGuardApplied)
                        throw new InvalidOperationException("Failed initialization did not cleanly remove memory hooks.");
                    var core = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "L-Connect.Core");
                    var sdk = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "lianli.lcd207");
                    if (core != null)
                    {
                        var known = core.GetType("LConnectCore.Devices.KnownDevices", true);
                        var method = known.GetMethod("GetDeviceID");
                        var id = method.Invoke(null, new[] { Enum.ToObject(method.GetParameters()[0].ParameterType, 17043456) });
                        if (Convert.ToInt32(id.GetType().GetField("ProductID").GetValue(id)) != 0xA088) throw new InvalidOperationException("Core registry rollback failed.");
                    }
                    if (sdk != null && Convert.ToInt32(sdk.GetType("lcd207.WinUsbLed", true).GetField("LCD_88inchPid").GetValue(null)) != 0xA088)
                        throw new InvalidOperationException("SDK initializer rollback failed.");
                    Console.WriteLine("PASS: deliberate partial initialization failure removes hooks and restores both original in-memory PID registries.");
                    return 0;
                }
                if (args.Length == 1 && args[0] == "toy")
                {
                    RuntimeHelpers.RunClassConstructor(typeof(ToyRegistry).TypeHandle);
                    RuntimeHelpers.RunClassConstructor(typeof(ToyPid).TypeHandle);
                    var harmony = new Harmony("turzx.lconnect.test.constructor-replay");
                    var transpiler = new HarmonyMethod(typeof(Program).GetMethod("ToyTranspiler", BindingFlags.Static | BindingFlags.NonPublic));
                    harmony.Patch(typeof(ToyRegistry).TypeInitializer, transpiler: transpiler);
                    harmony.Patch(typeof(ToyPid).TypeInitializer, transpiler: transpiler);
                    typeof(ToyRegistry).TypeInitializer.Invoke(null, null);
                    typeof(ToyPid).TypeInitializer.Invoke(null, null);
                    if (ToyRegistry.Values[1] != 0x0088 || ToyPid.Value != 0x0088 || ToyRegistry.Runs != 2 || ToyPid.Runs != 2)
                        throw new InvalidOperationException("Static data was not rebuilt with the memory-only PID change.");
                    Console.WriteLine("PASS: managed static-constructor replay updates readonly registry data and mutable SDK-style PID fields.");
                    return 0;
                }
                throw new ArgumentException("Expected toy; device-independent runtime validation is added separately.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static IEnumerable<CodeInstruction> ToyTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            int changes = 0;
            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Ldc_I4 && code.operand is int && (int)code.operand == 0xA088)
                { code.operand = 0x0088; changes++; }
                yield return code;
            }
            if (changes != 1) throw new InvalidOperationException("Expected one toy PID constant.");
        }
    }
}
