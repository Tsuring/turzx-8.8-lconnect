using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Turzx.LConnect.Bootstrap
{
    // This assembly deliberately has no third-party dependencies: it runs before Main.
    public sealed class CompatibilityDomainManager : AppDomainManager
    {
        public override void InitializeNewDomain(AppDomainSetup setup)
        {
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain()) return;
            string process = Process.GetCurrentProcess().ProcessName;
            bool test = process == "Turzx.LConnect.Runtime.TestHost";
            if (!test && process != "L-Connect-Service" && process != "L-Connect 3") return;
            if (test && String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TURZX_TEST_INSTALLATION"))) return;
            // The verified hosts target net48. Early dependency loading must not
            // cache legacy CLR compatibility switches before the entry attribute exists.
            setup.TargetFrameworkName = ".NETFramework,Version=v4.8";
            base.InitializeNewDomain(setup);
            string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lian-Li", "TURZX-Compatibility");
            string runtime = Path.Combine(home, "runtime");
            string installation = AppDomain.CurrentDomain.BaseDirectory;
            string dependencies = installation;
            string logs = Path.Combine(home, "logs");
            if (test)
            {
                runtime = RequiredTestVariable("TURZX_TEST_RUNTIME");
                installation = RequiredTestVariable("TURZX_TEST_INSTALLATION");
                dependencies = RequiredTestVariable("TURZX_TEST_DEPENDENCIES");
                logs = RequiredTestVariable("TURZX_TEST_LOG_DIRECTORY");
            }
            string startupLog = Path.Combine(logs, "startup-" + Process.GetCurrentProcess().Id + ".log");
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
            {
                try { Directory.CreateDirectory(logs); File.AppendAllText(startupLog, "UnhandledException " + args.ExceptionObject + Environment.NewLine); } catch { }
            };
            // Loading the entry EXE inside InitializeNewDomain breaks CLR startup
            // (FileLoadException/E_UNEXPECTED). Wait for the CLR to load it itself,
            // then install hooks synchronously before its Main can execute.
            string entryName = AssemblyName.GetAssemblyName(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, process + ".exe")).Name;
            AssemblyLoadEventHandler entryLoaded = null;
            entryLoaded = delegate(object sender, AssemblyLoadEventArgs args)
            {
                if (args.LoadedAssembly.GetName().Name != entryName) return;
                AppDomain.CurrentDomain.AssemblyLoad -= entryLoaded;
                InitializeRuntime(runtime, installation, dependencies, logs, test);
            };
            AppDomain.CurrentDomain.AssemblyLoad += entryLoaded;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != entryName) continue;
                AppDomain.CurrentDomain.AssemblyLoad -= entryLoaded;
                InitializeRuntime(runtime, installation, dependencies, logs, test);
                break;
            }
        }

        private static void InitializeRuntime(string runtime, string installation, string dependencies, string logs, bool test)
        {
            ResolveEventHandler resolver = null;
            bool unsafeState = false;
            try
            {
                string trustedDirectory = Path.GetFullPath(runtime);
                resolver = delegate(object sender, ResolveEventArgs args)
                {
                    string name = new AssemblyName(args.Name).Name;
                    if (name != "Turzx.LConnect.Runtime" && name != "0Harmony" && name != "Mono.Cecil" && !name.StartsWith("Mono.Cecil.", StringComparison.Ordinal)) return null;
                    string file = Path.Combine(trustedDirectory, name + ".dll");
                    return File.Exists(file) ? Assembly.LoadFrom(file) : null;
                };
                AppDomain.CurrentDomain.AssemblyResolve += resolver;
                Assembly engine = Assembly.LoadFrom(Path.Combine(trustedDirectory, "Turzx.LConnect.Runtime.dll"));
                Type entry = engine.GetType("Turzx.LConnect.Runtime.RuntimeEntryPoint", true);
                entry.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                    new object[] { installation, dependencies, logs, test });
                if (!(bool)entry.GetProperty("Ready").GetValue(null, null))
                {
                    AppDomain.CurrentDomain.AssemblyResolve -= resolver;
                    resolver = null;
                    unsafeState = !(bool)entry.GetProperty("RollbackComplete").GetValue(null, null);
                    if (unsafeState) throw new InvalidOperationException("Compatibility memory rollback was incomplete; stop before application Main.");
                }
            }
            catch (Exception error)
            {
                if (resolver != null) AppDomain.CurrentDomain.AssemblyResolve -= resolver;
                // A compatibility failure must not stop the original application's startup.
                try
                {
                    Directory.CreateDirectory(logs);
                    File.AppendAllText(Path.Combine(logs, "bootstrap-" + Process.GetCurrentProcess().Id + ".log"),
                        DateTime.UtcNow.ToString("o") + " BootstrapFailed " + error + Environment.NewLine);
                }
                catch { }
                if (unsafeState) throw;
            }
        }

        private static string RequiredTestVariable(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (String.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Missing isolated-test setting: " + name);
            return value;
        }
    }
}
