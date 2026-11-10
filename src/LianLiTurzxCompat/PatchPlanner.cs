using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Plans only the two known USB PID changes. It reads assemblies from memory and
// never executes or writes target code. Call Inspect once in each PowerShell process.
public static class TurzxPatchPlanner
{
    const string CoreName = "L-Connect.Core.dll";
    const string SdkName = "lianli.lcd207.dll";
    const int NativePid = 0xA088, CompatiblePid = 0x0088, VendorId = 0x1CBE;
    const int EnumControllerValue = 17043456, MaximumFileSize = 64 * 1024 * 1024;
    static readonly object Gate = new object();
    static bool used;

    public static Plan Inspect(string corePath, string sdkPath, string dependencyDirectory, string expectedProtocolFingerprint)
    {
        Plan plan = new Plan();
        lock (Gate)
        {
            if (used) { plan.Reason = "Inspect may be called only once per process because Mono.Cecil module identities are cached."; return plan; }
            used = true;
        }
        try
        {
            if (String.IsNullOrWhiteSpace(corePath) || String.IsNullOrWhiteSpace(sdkPath) || String.IsNullOrWhiteSpace(dependencyDirectory))
                throw new InvalidDataException("Core path, SDK path, and dependency directory are required.");
            if (!String.Equals(Path.GetFileName(corePath), CoreName, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetFileName(sdkPath), SdkName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Input filenames must be L-Connect.Core.dll and lianli.lcd207.dll.");
            if (!Directory.Exists(dependencyDirectory)) throw new DirectoryNotFoundException("Dependency directory does not exist.");
            if (!String.IsNullOrWhiteSpace(expectedProtocolFingerprint) && !IsHash(expectedProtocolFingerprint.Trim()))
                throw new InvalidDataException("Expected protocol fingerprint must be a SHA-256 hex string or empty.");

            byte[] coreBytes = ReadBytes(corePath), sdkBytes = ReadBytes(sdkPath);
            PeSections corePe = new PeSections(coreBytes), sdkPe = new PeSections(sdkBytes);
            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetFullPath(dependencyDirectory));
            using (MemoryStream coreStream = new MemoryStream(coreBytes, false))
            using (MemoryStream sdkStream = new MemoryStream(sdkBytes, false))
            using (AssemblyDefinition core = AssemblyDefinition.ReadAssembly(coreStream, Reader(resolver)))
            using (AssemblyDefinition sdk = AssemblyDefinition.ReadAssembly(sdkStream, Reader(resolver)))
            {
                if (core.Name.HasPublicKey || sdk.Name.HasPublicKey) throw new InvalidDataException("Strong-name signed assemblies are unsupported.");
                string fingerprint = ProtocolFingerprint(sdk.MainModule);
                plan.ProtocolFingerprint = fingerprint;
                if (!String.IsNullOrWhiteSpace(expectedProtocolFingerprint) &&
                    !String.Equals(fingerprint, expectedProtocolFingerprint.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    plan.Reason = "SDK protocol fingerprint differs from the approved baseline.";
                    return plan;
                }
                Target coreTarget = FindCoreTarget(core.MainModule);
                Target sdkTarget = FindSdkTarget(sdk.MainModule);
                plan.Files.Add(MakeFile(CoreName, coreBytes, core.Name.Version, coreTarget, corePe));
                plan.Files.Add(MakeFile(SdkName, sdkBytes, sdk.Name.Version, sdkTarget, sdkPe));
                plan.Compatible = true;
                plan.Reason = String.IsNullOrWhiteSpace(expectedProtocolFingerprint)
                    ? "Compatible structure found; protocol fingerprint captured without an expected baseline."
                    : "Compatible structure and approved protocol fingerprint verified.";
            }
            resolver.Dispose();
        }
        catch (Exception error)
        {
            plan.Compatible = false;
            plan.Files.Clear();
            plan.Reason = ErrorText(error);
        }
        return plan;
    }

    public sealed class Plan
    {
        public bool Compatible { get; set; }
        public string Reason { get; set; }
        public string ProtocolFingerprint { get; set; }
        public List<FilePlan> Files { get; set; }
        public Plan() { Files = new List<FilePlan>(); }
    }
    public sealed class FilePlan
    {
        public string Name { get; set; }
        public string State { get; set; }
        public string SourceSha256 { get; set; }
        public string PatchedSha256 { get; set; }
        public byte[] PatchedBytes { get; set; }
        public List<Change> Changes { get; set; }
        public string AssemblyVersion { get; set; }
        public FilePlan() { Changes = new List<Change>(); }
    }
    public sealed class Change
    {
        public int Offset { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public string Method { get; set; }
        public int IlOffset { get; set; }
    }
    sealed class Target { public MethodDefinition Method; public Instruction Instruction; public int Pid; }

    static ReaderParameters Reader(IAssemblyResolver resolver)
    {
        // Do not resolve unrelated .NET Framework/WPF attributes in a modern .NET host.
        return new ReaderParameters { InMemory = true, ReadSymbols = false, AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred };
    }
    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }
    static IEnumerable<Instruction> Code(MethodDefinition method)
    {
        if (!method.HasBody) throw new InvalidDataException("Expected method body is missing: " + method.FullName);
        return method.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop);
    }
    static MethodDefinition StaticConstructor(TypeDefinition type)
    {
        MethodDefinition method = type.Methods.SingleOrDefault(m => m.IsConstructor && m.IsStatic);
        if (method == null || !method.HasBody) throw new InvalidDataException("Static constructor is missing: " + type.FullName);
        return method;
    }
    static bool IntConstant(Instruction i, out int value)
    {
        value = 0;
        if (i.OpCode == OpCodes.Ldc_I4) { value = (int)i.Operand; return true; }
        if (i.OpCode == OpCodes.Ldc_I4_S) { value = Convert.ToSByte(i.Operand, CultureInfo.InvariantCulture); return true; }
        if (i.OpCode == OpCodes.Ldc_I4_M1) { value = -1; return true; }
        if (i.OpCode == OpCodes.Ldc_I4_0) value = 0;
        else if (i.OpCode == OpCodes.Ldc_I4_1) value = 1;
        else if (i.OpCode == OpCodes.Ldc_I4_2) value = 2;
        else if (i.OpCode == OpCodes.Ldc_I4_3) value = 3;
        else if (i.OpCode == OpCodes.Ldc_I4_4) value = 4;
        else if (i.OpCode == OpCodes.Ldc_I4_5) value = 5;
        else if (i.OpCode == OpCodes.Ldc_I4_6) value = 6;
        else if (i.OpCode == OpCodes.Ldc_I4_7) value = 7;
        else if (i.OpCode == OpCodes.Ldc_I4_8) value = 8;
        else return false;
        return true;
    }
    static bool IsPatchable(Instruction i) { return i != null && i.OpCode == OpCodes.Ldc_I4; }

    static Target FindCoreTarget(ModuleDefinition module)
    {
        FieldDefinition[] enumFields = AllTypes(module.Types).Where(t => t.IsEnum)
            .SelectMany(t => t.Fields.Where(f => f.Name == "UniversalScreen8p8Inch" && f.HasConstant &&
                Convert.ToInt32(f.Constant, CultureInfo.InvariantCulture) == EnumControllerValue)).ToArray();
        if (enumFields.Length != 1) throw new InvalidDataException("Expected one controller enum field with value 17043456.");
        FieldDefinition enumField = enumFields[0];
        if (!enumField.HasConstant || Convert.ToInt32(enumField.Constant, CultureInfo.InvariantCulture) != EnumControllerValue)
            throw new InvalidDataException("Controller enum value changed from 17043456.");
        TypeDefinition known = module.GetType("LConnectCore.Devices.KnownDevices");
        if (known == null) throw new InvalidDataException("KnownDevices type is missing.");
        MethodDefinition cctor = StaticConstructor(known);
        List<Instruction> code = Code(cctor).ToList();
        List<Target> matches = new List<Target>();
        for (int i = 0; i + 3 < code.Count; i++)
        {
            int enumValue, vid, pid;
            if (!IntConstant(code[i], out enumValue) || enumValue != EnumControllerValue ||
                !IntConstant(code[i + 1], out vid) || vid != VendorId ||
                !IntConstant(code[i + 2], out pid) || (pid != NativePid && pid != CompatiblePid) ||
                code[i + 3].OpCode != OpCodes.Newobj) continue;
            MethodReference ctor = code[i + 3].Operand as MethodReference;
            if (ctor == null || ctor.Name != ".ctor" || ctor.DeclaringType.FullName != "LConnectCore.Devices.USBDeviceID") continue;
            matches.Add(new Target { Method = cctor, Instruction = code[i + 2], Pid = pid });
        }
        if (matches.Count != 1) throw new InvalidDataException("Expected one KnownDevices enum/VID/PID/USBDeviceID sequence.");
        if (!IsPatchable(matches[0].Instruction)) throw new InvalidDataException("Core PID is not a 32-bit ldc.i4 operand.");
        return matches[0];
    }

    static Target FindSdkTarget(ModuleDefinition module)
    {
        TypeDefinition type = module.GetType("lcd207.WinUsbLed");
        if (type == null) throw new InvalidDataException("lcd207.WinUsbLed type is missing.");
        MethodDefinition cctor = StaticConstructor(type);
        List<Instruction> code = Code(cctor).ToList();
        List<int> pidAt = new List<int>(), vidAt = new List<int>(), pidValues = new List<int>(), vidValues = new List<int>();
        for (int i = 1; i < code.Count; i++)
        {
            if (code[i].OpCode != OpCodes.Stsfld) continue;
            FieldReference field = code[i].Operand as FieldReference;
            int value;
            if (field == null || field.DeclaringType.FullName != "lcd207.WinUsbLed" || !IntConstant(code[i - 1], out value)) continue;
            if (field.Name == "LCD_88inchPid") { pidAt.Add(i); pidValues.Add(value); }
            if (field.Name == "LCD_88inchVid") { vidAt.Add(i); vidValues.Add(value); }
        }
        if (pidAt.Count != 1 || vidAt.Count != 1 || vidAt[0] != pidAt[0] + 2 || vidValues[0] != VendorId)
            throw new InvalidDataException("Expected one adjacent LCD_88inchPid / LCD_88inchVid assignment pair.");
        if (pidValues[0] != NativePid && pidValues[0] != CompatiblePid)
            throw new InvalidDataException("LCD_88inchPid has an unknown value.");
        Target target = new Target { Method = cctor, Instruction = code[pidAt[0] - 1], Pid = pidValues[0] };
        if (!IsPatchable(target.Instruction)) throw new InvalidDataException("SDK PID is not a 32-bit ldc.i4 operand.");
        return target;
    }

    static FilePlan MakeFile(string name, byte[] source, Version version, Target target, PeSections pe)
    {
        int bodyOffset, ilStart;
        int ilLength = target.Method.Body.CodeSize;
        bodyOffset = pe.MethodBody(target.Method.RVA, ilLength, out ilStart);
        int at = checked(ilStart + target.Instruction.Offset);
        if (at + 5 > source.Length || source[at] != 0x20 || BitConverter.ToInt32(source, at + 1) != target.Pid)
            throw new InvalidDataException("Decoded Cecil PID instruction disagrees with raw PE method body.");
        byte[] output = (byte[])source.Clone();
        FilePlan file = new FilePlan {
            Name = name, State = target.Pid == NativePid ? "Native" : "Patched",
            SourceSha256 = Hash(source), AssemblyVersion = version == null ? "" : version.ToString()
        };
        if (target.Pid == NativePid)
        {
            BitConverter.GetBytes(CompatiblePid).CopyTo(output, at + 1);
            List<int> changed = new List<int>();
            for (int i = 0; i < source.Length; i++) if (source[i] != output[i]) changed.Add(i);
            if (changed.Count != 1 || changed[0] != at + 2 || source[at + 2] != 0xA0 || output[at + 2] != 0)
                throw new InvalidDataException("Planned output is not exactly the A0-to-00 PID operand change.");
            file.Changes.Add(new Change { Offset = changed[0], Before = "A0", After = "00",
                Method = target.Method.DeclaringType.FullName + "::.cctor", IlOffset = target.Instruction.Offset });
        }
        file.PatchedBytes = output;
        file.PatchedSha256 = Hash(output);
        return file;
    }

    static string ProtocolFingerprint(ModuleDefinition module)
    {
        TypeDefinition winUsb = module.GetType("lcd207.WinUsb"), led = module.GetType("lcd207.LEDController");
        if (winUsb == null || led == null) throw new InvalidDataException("Required SDK protocol types are missing.");
        StringBuilder b = new StringBuilder();
        List<FieldDefinition> rvaFields = new List<FieldDefinition>();
        int initializeArrayCalls = 0;
        AddProtocolType(b, winUsb, new[] { "GetBaseCmdBuf", "Encrypt", "GetVer", "PushImage", "SendAndRead" },
            new Dictionary<string, int> { { "GetBaseCmdBuf", 1 }, { "Encrypt", 2 }, { "GetVer", 1 }, { "PushImage", 2 }, { "SendAndRead", 1 } },
            rvaFields, ref initializeArrayCalls);
        AddProtocolType(b, led, new[] { "SetSensorData", "RenderSensor", "UpdateSensorValues", "UpdateLCD" },
            new Dictionary<string, int>(), rvaFields, ref initializeArrayCalls);
        if (initializeArrayCalls == 0 || rvaFields.Count == 0)
            throw new InvalidDataException("Protocol fingerprint did not include the InitializeArray static key data.");
        foreach (FieldDefinition field in rvaFields.OrderBy(f => f.FullName, StringComparer.Ordinal))
        {
            if (!field.HasFieldRVA || field.InitialValue == null || field.InitialValue.Length < 8)
                throw new InvalidDataException("InitializeArray field RVA data is absent or shorter than 8 bytes: " + field.FullName);
            TypeDefinition blobType = null;
            try { blobType = field.FieldType.Resolve(); } catch { }
            int declaredSize = blobType == null ? 0 : blobType.ClassSize;
            if (declaredSize > 0 && field.InitialValue.Length < declaredSize)
                throw new InvalidDataException("Static key data is shorter than its declared type size: " + field.FullName);
            b.Append("rva|").Append(field.FullName).Append('|').Append(field.InitialValue.Length).Append('|')
                .Append(BitConverter.ToString(field.InitialValue).Replace("-", "")).Append('\n');
        }
        return Hash(Encoding.UTF8.GetBytes(b.ToString()));
    }

    static void AddProtocolType(StringBuilder b, TypeDefinition type, string[] names, Dictionary<string, int> requiredCounts,
        List<FieldDefinition> rvaFields, ref int initializeArrayCalls)
    {
        b.Append("type|").Append(type.FullName).Append('\n');
        foreach (string name in names)
        {
            MethodDefinition[] methods = type.Methods.Where(m => m.Name == name).OrderBy(m => m.FullName, StringComparer.Ordinal).ToArray();
            if (methods.Length == 0) throw new InvalidDataException("Required protocol method missing: " + type.FullName + "." + name);
            int required;
            if (requiredCounts.TryGetValue(name, out required) && methods.Length != required)
                throw new InvalidDataException("Protocol overload count changed: " + type.FullName + "." + name);
            foreach (MethodDefinition method in methods)
            {
                if (!method.HasBody) throw new InvalidDataException("Protocol method has no CIL body: " + method.FullName);
                MethodBody body = method.Body;
                Instruction[] code = body.Instructions.ToArray();
                b.Append("method|").Append(method.FullName).Append('|').Append((int)method.Attributes).Append('|')
                    .Append(body.MaxStackSize).Append('|').Append(body.InitLocals ? '1' : '0').Append('\n');
                foreach (VariableDefinition v in body.Variables)
                    b.Append("local|").Append(v.Index).Append('|').Append(v.VariableType.FullName).Append('\n');
                for (int i = 0; i < code.Length; i++)
                {
                    Instruction insn = code[i];
                    b.Append("il|").Append(i).Append('|').Append(insn.OpCode.Name).Append('|').Append(Operand(insn, code)).Append('\n');
                    if ((insn.OpCode == OpCodes.Call || insn.OpCode == OpCodes.Callvirt) &&
                        insn.Operand is MethodReference &&
                        ((MethodReference)insn.Operand).DeclaringType.FullName == "System.Runtime.CompilerServices.RuntimeHelpers" &&
                        ((MethodReference)insn.Operand).Name == "InitializeArray")
                    {
                        initializeArrayCalls++;
                        FieldDefinition key = null;
                        for (int j = i - 1; j >= Math.Max(0, i - 4); j--)
                        {
                            if (code[j].OpCode != OpCodes.Ldtoken) continue;
                            FieldReference reference = code[j].Operand as FieldReference;
                            if (reference != null) { try { key = reference.Resolve(); } catch { } }
                            break;
                        }
                        if (key == null || !key.HasFieldRVA || key.InitialValue == null || key.InitialValue.Length < 8)
                            throw new InvalidDataException("InitializeArray no longer references an inspectable 8-byte static RVA field.");
                        if (!rvaFields.Any(f => f.MetadataToken.ToInt32() == key.MetadataToken.ToInt32())) rvaFields.Add(key);
                    }
                    FieldReference fieldReference = insn.Operand as FieldReference;
                    if (insn.OpCode == OpCodes.Ldtoken && fieldReference != null)
                    {
                        FieldDefinition field = fieldReference as FieldDefinition;
                        if (field == null) { try { field = fieldReference.Resolve(); } catch { } }
                        if (field != null && field.HasFieldRVA && field.InitialValue != null && field.InitialValue.Length > 0 &&
                            !rvaFields.Any(f => f.MetadataToken.ToInt32() == field.MetadataToken.ToInt32())) rvaFields.Add(field);
                    }
                }
                foreach (ExceptionHandler e in body.ExceptionHandlers)
                    b.Append("eh|").Append(e.HandlerType).Append('|').Append(Index(code, e.TryStart)).Append('|').Append(Index(code, e.TryEnd))
                        .Append('|').Append(Index(code, e.HandlerStart)).Append('|').Append(Index(code, e.HandlerEnd)).Append('|')
                        .Append(Index(code, e.FilterStart)).Append('|').Append(e.CatchType == null ? "-" : e.CatchType.FullName).Append('\n');
            }
        }
    }

    static string Operand(Instruction insn, Instruction[] code)
    {
        if (insn.Operand == null) return "-";
        Instruction target = insn.Operand as Instruction;
        if (target != null) return Index(code, target).ToString(CultureInfo.InvariantCulture);
        Instruction[] targets = insn.Operand as Instruction[];
        if (targets != null) return String.Join(",", targets.Select(t => Index(code, t).ToString(CultureInfo.InvariantCulture)).ToArray());
        MemberReference member = insn.Operand as MemberReference;
        if (member != null) return member.FullName;
        VariableDefinition variable = insn.Operand as VariableDefinition;
        if (variable != null) return "v" + variable.Index.ToString(CultureInfo.InvariantCulture);
        ParameterDefinition parameter = insn.Operand as ParameterDefinition;
        if (parameter != null) return "p" + parameter.Index.ToString(CultureInfo.InvariantCulture);
        string text = insn.Operand as string;
        if (text != null) return "\"" + text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("|", "\\|") + "\"";
        CallSite callSite = insn.Operand as CallSite;
        if (callSite != null) return callSite.ReturnType.FullName + "(" + String.Join(",", callSite.Parameters.Select(p => p.ParameterType.FullName).ToArray()) + ")";
        return Convert.ToString(insn.Operand, CultureInfo.InvariantCulture);
    }
    static int Index(Instruction[] code, Instruction insn) { return insn == null ? -1 : Array.IndexOf(code, insn); }

    sealed class PeSections
    {
        readonly byte[] bytes;
        readonly List<Section> sections = new List<Section>();
        struct Section { public uint Va, Size, Raw; }
        public PeSections(byte[] source)
        {
            bytes = source;
            if (bytes.Length < 256) throw new InvalidDataException("PE input is too small.");
            int pe = BitConverter.ToInt32(bytes, 0x3c);
            Need(bytes, pe, 24);
            if (BitConverter.ToUInt32(bytes, pe) != 0x00004550) throw new InvalidDataException("PE signature missing.");
            int count = BitConverter.ToUInt16(bytes, pe + 6), optionalSize = BitConverter.ToUInt16(bytes, pe + 20);
            int start = checked(pe + 24 + optionalSize); Need(bytes, start, checked(count * 40));
            for (int i = 0; i < count; i++)
            {
                int p = start + 40 * i;
                sections.Add(new Section { Va = BitConverter.ToUInt32(bytes, p + 12), Size = BitConverter.ToUInt32(bytes, p + 16), Raw = BitConverter.ToUInt32(bytes, p + 20) });
            }
        }
        public int MethodBody(int rva, int codeSize, out int ilStart)
        {
            if (rva <= 0) throw new InvalidDataException("Target method RVA is missing.");
            int body = Rva(rva, 1), header, actualCodeSize;
            int kind = bytes[body] & 3;
            if (kind == 2) { header = 1; actualCodeSize = bytes[body] >> 2; }
            else if (kind == 3)
            {
                Need(bytes, body, 12);
                header = ((BitConverter.ToUInt16(bytes, body) >> 12) & 15) * 4;
                if (header < 12) throw new InvalidDataException("Fat method header malformed.");
                Need(bytes, body, header); actualCodeSize = BitConverter.ToInt32(bytes, body + 4);
            }
            else throw new InvalidDataException("Unknown CIL method header.");
            if (actualCodeSize != codeSize) throw new InvalidDataException("Cecil CIL size differs from raw PE method body.");
            Need(bytes, body + header, codeSize); ilStart = body + header; return body;
        }
        int Rva(int rva, int length)
        {
            foreach (Section s in sections)
            {
                if ((ulong)rva >= s.Va && (ulong)rva - s.Va < s.Size)
                {
                    ulong delta = (ulong)rva - s.Va, off = (ulong)s.Raw + delta;
                    if (delta + (ulong)length > s.Size || off + (ulong)length > (ulong)bytes.Length)
                        throw new InvalidDataException("RVA extends beyond raw PE section.");
                    return checked((int)off);
                }
            }
            throw new InvalidDataException("RVA not found in raw PE sections.");
        }
    }

    static byte[] ReadBytes(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Assembly input does not exist.", path);
        FileInfo info = new FileInfo(path);
        if (info.Length < 1 || info.Length > MaximumFileSize) throw new InvalidDataException("Input assembly must be 1 byte through 64 MiB.");
        return File.ReadAllBytes(path);
    }
    static string Hash(byte[] data) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", ""); }
    static bool IsHash(string s) { return s != null && s.Length == 64 && s.All(Uri.IsHexDigit); }
    static string ErrorText(Exception e)
    {
        while (e is System.Reflection.TargetInvocationException && e.InnerException != null) e = e.InnerException;
        return e.GetType().Name + ": " + e.Message;
    }
    static void Need(byte[] b, int offset, int count)
    {
        if (offset < 0 || count < 0 || (long)offset + count > b.Length) throw new InvalidDataException("Malformed or truncated PE data.");
    }
}
