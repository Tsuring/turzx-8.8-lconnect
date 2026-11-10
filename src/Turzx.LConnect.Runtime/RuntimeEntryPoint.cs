using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using LianLiTurzxCompat;

namespace Turzx.LConnect.Runtime
{
    public static class RuntimeEntryPoint
    {
        private const string Owner = "turzx.lconnect.compatibility.runtime.v1";
        private static readonly object Gate = new object();
        private static bool started;
        private static string logFile;
        private static PropertyInfo ledUsb;
        private static int skippedRgb;
        private static readonly object SensorGate = new object();
        private static string lastSensorSample;
        private static int sensorSamples;
        public static bool Ready { get; private set; }
        public static bool RollbackComplete { get; private set; }
        public static string Failure { get; private set; }
        public static int CorePid { get; private set; }
        public static int SdkPid { get; private set; }
        public static bool OpenRgbGuardApplied { get; private set; }
        public static int SkippedRgbControllers { get { return skippedRgb; } }
        public static MethodInfo OpenRgbCallback { get; private set; }
        public static Type LcdControllerType { get; private set; }

        public static void Initialize(string installation, string dependencies, string logs, bool test)
        {
            lock (Gate)
            {
                if (started) return;
                started = true;
                ConstructorInfo coreInitializer = null, sdkInitializer = null;
                Harmony harmony = null;
                ResolveEventHandler dependencyResolver = null;
                var patchedMethods = new List<MethodBase>();
                try
                {
                    string name = Process.GetCurrentProcess().ProcessName;
                    if ((!test && name != "L-Connect-Service" && name != "L-Connect 3") || (test && name != "Turzx.LConnect.Runtime.TestHost"))
                        throw new InvalidOperationException("Runtime loader is restricted to the intended process names.");
                    if (IntPtr.Size != 8) throw new InvalidOperationException("This verified runtime target requires a 64-bit process.");
                    Directory.CreateDirectory(logs);
                    logFile = Path.Combine(logs, "runtime-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + Process.GetCurrentProcess().Id + ".log");
                    Log("BootstrapEntered before application Main");
                    string corePath = Path.Combine(installation, "L-Connect.Core.dll"), sdkPath = Path.Combine(installation, "lianli.lcd207.dll"), servicePath = Path.Combine(installation, "L-Connect-Service.exe");
                    var plan = TurzxPatchPlanner.Inspect(corePath, sdkPath, dependencies, ApprovedBaseline.Protocol);
                    if (!plan.Compatible) throw new InvalidDataException("PID/protocol guard: " + plan.Reason);
                    var rgb = TurzxOpenRgbGuard.Inspect(servicePath, ApprovedBaseline.OpenRgb);
                    if (!rgb.Compatible) throw new InvalidDataException("OpenRGB guard: " + rgb.Reason);
                    if (plan.Files.Any(f => f.State != "Native") || rgb.State != "Native") throw new InvalidDataException("Restore original vendor files before enabling the memory-only loader.");
                    string safety = RuntimeSafety.Fingerprint(installation);
                    if (!String.Equals(safety, RuntimeSafety.ApprovedFingerprint, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Data-initializer safety fingerprint changed: " + safety);

                    dependencyResolver = delegate(object sender, ResolveEventArgs args)
                    {
                        string simple = new AssemblyName(args.Name).Name;
                        if (simple.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
                        string file = Path.Combine(dependencies, simple + ".dll");
                        return File.Exists(file) ? Assembly.LoadFrom(file) : null;
                    };
                    if (test) AppDomain.CurrentDomain.AssemblyResolve += dependencyResolver;
                    Assembly core = LoadExact(corePath), sdk = LoadExact(sdkPath);
                    Type known = core.GetType("LConnectCore.Devices.KnownDevices", true);
                    Type usb = core.GetType("LConnectCore.Devices.USBDeviceID", true);
                    Type led = sdk.GetType("lcd207.WinUsbLed", true);
                    coreInitializer = known.TypeInitializer;
                    sdkInitializer = led.TypeInitializer;
                    harmony = new Harmony(Owner);
                    bool serviceScope = test || name == "L-Connect-Service";
                    if (serviceScope)
                    {
                        Assembly service = LoadExact(servicePath);
                        string signature = rgb.Method.Substring("System.Void ".Length);
                        int split = signature.IndexOf("::", StringComparison.Ordinal);
                        string typeName = signature.Substring(0, split).Replace('/', '+');
                        string methodName = signature.Substring(split + 2, signature.IndexOf('(') - split - 2);
                        LcdControllerType = sdk.GetType("lcd207.LEDController", true);
                        ledUsb = LcdControllerType.GetProperty("ledUsb", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (ledUsb == null || ledUsb.GetGetMethod(true) == null) throw new MissingMemberException("LED controller getter is missing.");
                        OpenRgbCallback = service.GetType(typeName, true).GetMethod(methodName,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { LcdControllerType }, null);
                        if (OpenRgbCallback == null) throw new MissingMethodException("Verified OpenRGB callback is missing.");
                        patchedMethods.Add(OpenRgbCallback);
                        harmony.Patch(OpenRgbCallback, prefix: Hook("OpenRgbPrefix"));
                        OpenRgbGuardApplied = true;
                        Log("OpenRgbPrefixInstalled");
                        MethodInfo sensor = LcdControllerType.GetMethod("UpdateSensorValues", BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { typeof(Dictionary<string, double>) }, null);
                        if (sensor == null || sensor.ReturnType.FullName != "System.Drawing.Bitmap") throw new MissingMethodException("Verified sensor-frame method is missing.");
                        patchedMethods.Add(sensor);
                        harmony.Patch(sensor, postfix: Hook("ObserveSensorFrame"));
                    }
                    // These verified initializers contain only static data construction. Harmony may
                    // initialize the original types while inspecting them; replay both after patching.
                    RuntimeHelpers.RunClassConstructor(known.TypeHandle);
                    RuntimeHelpers.RunClassConstructor(led.TypeHandle);
                    patchedMethods.Add(coreInitializer);
                    harmony.Patch(coreInitializer, transpiler: Hook("CoreTranspiler"));
                    patchedMethods.Add(sdkInitializer);
                    harmony.Patch(sdkInitializer, transpiler: Hook("SdkTranspiler"));
                    coreInitializer.Invoke(null, null);
                    if (test && Environment.GetEnvironmentVariable("TURZX_TEST_FAIL_AFTER_CORE") == "1") throw new InvalidOperationException("Deliberate isolated-test failure after Core initializer replay.");
                    sdkInitializer.Invoke(null, null);

                    MethodInfo getId = known.GetMethod("GetDeviceID", BindingFlags.Public | BindingFlags.Static);
                    object controller = Enum.ToObject(getId.GetParameters()[0].ParameterType, 17043456);
                    object id = getId.Invoke(null, new[] { controller });
                    if (id == null || Convert.ToInt32(usb.GetField("VendorID").GetValue(id)) != 0x1CBE) throw new InvalidOperationException("Core VID verification failed.");
                    CorePid = Convert.ToInt32(usb.GetField("ProductID").GetValue(id));
                    object reverse = known.GetMethod("GetControllerTypeID", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { id });
                    SdkPid = Convert.ToInt32(led.GetField("LCD_88inchPid", BindingFlags.Public | BindingFlags.Static).GetValue(null));
                    if (CorePid != 0x0088 || SdkPid != 0x0088 || Convert.ToInt32(reverse) != 17043456)
                        throw new InvalidOperationException("In-memory forward/reverse device lookup or SDK PID failed.");
                    foreach (var file in plan.Files) if (!Hash(Path.Combine(installation, file.Name)).Equals(file.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("A vendor file changed during startup.");
                    if (!Hash(servicePath).Equals(rgb.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Service file changed during startup.");
                    Ready = true;
                    Log("RuntimeReady CorePid=0088 SdkPid=0088 ReverseLookup=17043456 OpenRgbGuard=" + OpenRgbGuardApplied + " VendorFilesUnchanged=True");
                }
                catch (Exception error)
                {
                    Failure = error.ToString();
                    var rollbackErrors = new List<Exception>();
                    if (dependencyResolver != null) AppDomain.CurrentDomain.AssemblyResolve -= dependencyResolver;
                    try { if (harmony != null) harmony.UnpatchAll(Owner); } catch (Exception failure) { rollbackErrors.Add(failure); }
                    foreach (var method in patchedMethods)
                    {
                        try { if (Owned(method)) harmony.Unpatch(method, HarmonyPatchType.All, Owner); }
                        catch (Exception failure) { rollbackErrors.Add(failure); }
                    }
                    foreach (var initializer in new[] { coreInitializer, sdkInitializer })
                    {
                        if (initializer == null) continue;
                        try
                        {
                            if (Owned(initializer)) throw new InvalidOperationException("Initializer hook could not be removed.");
                            initializer.Invoke(null, null);
                        }
                        catch (Exception failure) { rollbackErrors.Add(failure); }
                    }
                    if (patchedMethods.Any(Owned)) rollbackErrors.Add(new InvalidOperationException("A compatibility hook remains installed."));
                    RollbackComplete = rollbackErrors.Count == 0;
                    foreach (var rollback in rollbackErrors) Failure += Environment.NewLine + "Memory rollback: " + rollback;
                    Ready = false;
                    OpenRgbGuardApplied = OpenRgbCallback != null && Owned(OpenRgbCallback);
                    Log("RuntimeFailed RollbackComplete=" + RollbackComplete + " " + Failure);
                }
            }
        }

        private static HarmonyMethod Hook(string name) { return new HarmonyMethod(typeof(RuntimeEntryPoint).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }
        private static bool Owned(MethodBase method) { var info = Harmony.GetPatchInfo(method); return info != null && info.Owners.Contains(Owner); }
        private static Assembly LoadExact(string path)
        {
            path = Path.GetFullPath(path);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic &&
                Path.GetFullPath(a.Location).Equals(path, StringComparison.OrdinalIgnoreCase)) ?? Assembly.LoadFrom(path);
            if (!Path.GetFullPath(assembly.Location).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("An assembly with the same identity was loaded from another location.");
            return assembly;
        }
        private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
        private static void Log(string text)
        {
            if (logFile == null) return;
            try { File.AppendAllText(logFile, DateTime.UtcNow.ToString("o") + " pid=" + Process.GetCurrentProcess().Id + " " + text + Environment.NewLine); } catch { }
        }
        private static bool OpenRgbPrefix(object __0)
        {
            bool present = __0 != null && ledUsb.GetValue(__0, null) != null;
            if (!present) { Interlocked.Increment(ref skippedRgb); Log("OpenRgbSkippedDisplayWithoutRgbController"); }
            return present;
        }
        private static void ObserveSensorFrame(Dictionary<string, double> __0, object __result)
        {
            // Observation only: the original method has already rendered and called UpdateLCD.
            if (__0 == null || __result == null || sensorSamples >= 3) return;
            try
            {
                string sample = String.Join(" ", __0.Where(p => p.Key.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) || p.Key.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value.ToString("0.###", CultureInfo.InvariantCulture)));
                if (sample.Length == 0) return;
                lock (SensorGate)
                {
                    if (sensorSamples >= 3 || sample == lastSensorSample) return;
                    lastSensorSample = sample;
                    sensorSamples++;
                    Log("SensorFrameObserved Sample=" + sensorSamples + " BitmapPresent=True " + sample);
                }
            }
            catch { }
        }
        private static bool Constant(CodeInstruction code, int value) { return code.opcode == OpCodes.Ldc_I4 && code.operand is int && (int)code.operand == value; }
        private static IEnumerable<CodeInstruction> CoreTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var all = instructions.ToList(); var code = all.Where(i => i.opcode != OpCodes.Nop).ToArray(); int matches = 0;
            for (int i = 0; i + 3 < code.Length; i++)
            {
                var ctor = code[i + 3].operand as ConstructorInfo;
                if (Constant(code[i], 17043456) && Constant(code[i + 1], 0x1CBE) && Constant(code[i + 2], 0xA088) && code[i + 3].opcode == OpCodes.Newobj && ctor != null && ctor.DeclaringType.FullName == "LConnectCore.Devices.USBDeviceID")
                { code[i + 2].operand = 0x0088; matches++; }
            }
            if (matches != 1) throw new InvalidOperationException("Expected one in-memory Universal Screen PID site.");
            Log("CoreInitializerTranspiled Sites=1"); return all;
        }
        private static IEnumerable<CodeInstruction> SdkTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var all = instructions.ToList(); var code = all.Where(i => i.opcode != OpCodes.Nop).ToArray(); int matches = 0;
            for (int i = 0; i + 1 < code.Length; i++)
            {
                var field = code[i + 1].operand as FieldInfo;
                if (Constant(code[i], 0xA088) && code[i + 1].opcode == OpCodes.Stsfld && field != null && field.Name == "LCD_88inchPid" && field.DeclaringType.FullName == "lcd207.WinUsbLed")
                { code[i].operand = 0x0088; matches++; }
            }
            if (matches != 1) throw new InvalidOperationException("Expected one in-memory SDK PID site.");
            Log("SdkInitializerTranspiled Sites=1"); return all;
        }
    }
}
