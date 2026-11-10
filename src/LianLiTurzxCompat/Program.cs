using System.Security.Cryptography;
using System.Text.Json;

namespace LianLiTurzxCompat;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record GeneratedFile(string Name, string State, string SourceSha256,
        string PatchedSha256, string AssemblyVersion, byte[] Bytes, object Changes);

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] || args is ["-h"])
        {
            Console.WriteLine("""
                LianLiTurzxCompat
                  inspect --input <directory> [--dependencies <directory>]
                  prepare --input <directory> --output <new-directory> [--dependencies <directory>]

                Input must contain L-Connect.Core.dll, lianli.lcd207.dll and L-Connect-Service.exe.
                inspect reads files only. prepare creates separate patched copies and manifest.json.
                This tool never installs files, restarts services or changes hardware firmware/USB IDs.
                """);
            return 0;
        }

        try
        {
            var (command, input, dependencies, output) = Parse(args);
            var pid = TurzxPatchPlanner.Inspect(Path.Combine(input, "L-Connect.Core.dll"),
                Path.Combine(input, "lianli.lcd207.dll"), dependencies, ApprovedBaseline.Protocol);
            if (!pid.Compatible) throw new InvalidOperationException("PID/protocol check: " + pid.Reason);
            var rgb = TurzxOpenRgbGuard.Inspect(Path.Combine(input, "L-Connect-Service.exe"), ApprovedBaseline.OpenRgb);
            if (!rgb.Compatible) throw new InvalidOperationException("OpenRGB check: " + rgb.Reason);

            var files = pid.Files.Select(f => new GeneratedFile(f.Name, f.State, f.SourceSha256,
                f.PatchedSha256, f.AssemblyVersion, f.PatchedBytes, f.Changes)).ToList();
            files.Add(new GeneratedFile("L-Connect-Service.exe", rgb.State, rgb.SourceSha256,
                rgb.PatchedSha256, rgb.AssemblyVersion, rgb.PatchedBytes,
                new { Method = rgb.Method, Guard = "if (o.ledUsb == null) return;" }));

            var report = new
            {
                Schema = 1,
                Compatible = true,
                BaselineSoftware = ApprovedBaseline.Software,
                BaselineSdk = ApprovedBaseline.Sdk,
                ProtocolFingerprint = pid.ProtocolFingerprint,
                OpenRgbFingerprint = rgb.Fingerprint,
                ChangesRequired = files.Count(f => f.State == "Native"),
                Files = files.Select(f => new
                {
                    f.Name, f.State, f.SourceSha256, f.PatchedSha256,
                    f.AssemblyVersion, PlannedBytes = f.Bytes.Length, f.Changes
                }).ToArray()
            };
            string serialized = JsonSerializer.Serialize(report, Json);

            if (command == "prepare")
            {
                // Finish every check before creating the output directory. No installed file is written.
                if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output must be a new directory.");
                if (Inside(output, input) || Inside(output, dependencies))
                    throw new IOException("Output must be outside the input and dependency directories.");
                foreach (var file in files)
                {
                    if (!Hash(file.Bytes).Equals(file.PatchedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Generated payload hash mismatch: " + file.Name);
                    if (!Hash(File.ReadAllBytes(Path.Combine(input, file.Name))).Equals(file.SourceSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Input changed during inspection: " + file.Name);
                }
                Directory.CreateDirectory(output);
                foreach (var file in files) WriteNew(Path.Combine(output, file.Name), file.Bytes);
                WriteNew(Path.Combine(output, "manifest.json"), System.Text.Encoding.UTF8.GetBytes(serialized));
            }
            Console.WriteLine(serialized);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Compatible = false, Error = error.Message }, Json));
            return 1;
        }
    }

    private static (string Command, string Input, string Dependencies, string Output) Parse(string[] args)
    {
        if (args[0] is not ("inspect" or "prepare")) throw new ArgumentException("Expected inspect or prepare; use --help.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--input" or "--dependencies" or "--output") || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Invalid option or missing value; use --help.");
            if (!options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate option: " + args[i]);
        }
        if (!options.TryGetValue("--input", out var source)) throw new ArgumentException("--input is required.");
        string input = Path.GetFullPath(source);
        string dependencies = Path.GetFullPath(options.GetValueOrDefault("--dependencies", input));
        if (!Directory.Exists(input) || !Directory.Exists(dependencies)) throw new DirectoryNotFoundException("Input/dependency directory does not exist.");
        if (args[0] == "inspect" && options.ContainsKey("--output")) throw new ArgumentException("inspect does not accept --output.");
        if (args[0] == "prepare" && !options.ContainsKey("--output")) throw new ArgumentException("prepare requires --output.");
        return (args[0], input, dependencies,
            options.TryGetValue("--output", out var target) ? Path.GetFullPath(target) : null);
    }

    private static bool Inside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalized = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(normalized, comparison) || path.StartsWith(normalized + Path.DirectorySeparatorChar, comparison);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
