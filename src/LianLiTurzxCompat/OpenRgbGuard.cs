using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

public sealed class OpenRgbGuardPlan {
    public bool Compatible {get;set;}
    public string Reason {get;set;}
    public string State {get;set;}
    public string Fingerprint {get;set;}
    public string SourceSha256 {get;set;}
    public string PatchedSha256 {get;set;}
    public byte[] PatchedBytes {get;set;}
    public string Method {get;set;}
    public string AssemblyVersion {get;set;}
}
public static class TurzxOpenRgbGuard {
    static string Hash(byte[] data) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant(); }
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> input) {
        foreach(var type in input) { yield return type; foreach(var nested in Types(type.NestedTypes))yield return nested; }
    }
    static bool LedGetter(Instruction i) { var m=i.Operand as MethodReference;return i.OpCode==OpCodes.Callvirt&&m!=null&&m.DeclaringType.FullName=="lcd207.LEDController"&&m.Name=="get_ledUsb"; }
    static string Stable(string value) { return Regex.Replace(value,@"<>c__DisplayClass\d+_\d+","<>display"); }
    static string Fingerprint(MethodDefinition method,int skip) {
        var code=method.Body.Instructions.Skip(skip).ToArray();
        var b=new StringBuilder();
        b.Append(method.ReturnType.FullName).Append('|').Append(String.Join(",",method.Parameters.Select(p=>p.ParameterType.FullName))).Append('|');
        foreach(var variable in method.Body.Variables)b.Append("local:").Append(variable.VariableType.FullName).Append(';');
        foreach(var i in code) {
            b.Append(i.OpCode.Name).Append(':');
            var target=i.Operand as Instruction;
            var targets=i.Operand as Instruction[];
            var member=i.Operand as MemberReference;
            var variable=i.Operand as VariableDefinition;
            var parameter=i.Operand as ParameterDefinition;
            if(target!=null)b.Append(Array.IndexOf(code,target));
            else if(targets!=null)b.Append(String.Join(",",targets.Select(t=>Array.IndexOf(code,t))));
            else if(member!=null)b.Append(Stable(member.FullName));
            else if(variable!=null)b.Append("v").Append(variable.Index);
            else if(parameter!=null)b.Append("p").Append(parameter.Index);
            else if(i.Operand!=null)b.Append(Convert.ToString(i.Operand,System.Globalization.CultureInfo.InvariantCulture));
            b.Append(';');
        }
        if(method.Body.ExceptionHandlers.Count!=0)throw new InvalidOperationException("OpenRGB callback exception layout changed");
        return Hash(Encoding.UTF8.GetBytes(b.ToString()));
    }
    public static OpenRgbGuardPlan Inspect(string path,string expectedFingerprint) {
        var plan=new OpenRgbGuardPlan {Compatible=false,State="Unsupported"};
        try {
            var bytes=File.ReadAllBytes(path);
            if(bytes.Length>64*1024*1024)throw new InvalidOperationException("Service image exceeds supported size");
            plan.SourceSha256=Hash(bytes);
            var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(path));
            var options=new ReaderParameters {InMemory=true,ReadSymbols=false,AssemblyResolver=resolver,ReadingMode=ReadingMode.Deferred};
            using(var stream=new MemoryStream(bytes,false))using(var assembly=AssemblyDefinition.ReadAssembly(stream,options)) {
                if(assembly.Name.HasPublicKey)throw new InvalidOperationException("Strong-name signed service is unsupported");
                plan.AssemblyVersion=assembly.Name.Version.ToString();
                var candidates=Types(assembly.MainModule.Types).Where(t=>t.FullName.StartsWith("LConnectService.Products.OpenRgb.OpenRgbController/",StringComparison.Ordinal))
                    .SelectMany(t=>t.Methods).Where(m=>Regex.IsMatch(m.Name,@"^<initCoreAsync>b__\d+$")&&m.HasBody&&m.Parameters.Count==1&&m.Parameters[0].ParameterType.FullName=="lcd207.LEDController"&&m.ReturnType.FullName=="System.Void"&&m.Body.Instructions.Any(LedGetter)).ToArray();
                if(candidates.Length!=1)throw new InvalidOperationException("Expected one OpenRGB LCD callback");
                var method=candidates[0];var code=method.Body.Instructions;
                plan.Method=method.FullName;
                bool guarded=code.Count>5&&code[0].OpCode==OpCodes.Ldarg_1&&LedGetter(code[1])&&code[2].OpCode==OpCodes.Brtrue&&Object.ReferenceEquals(code[2].Operand,code[4])&&code[3].OpCode==OpCodes.Ret;
                int skip=guarded?4:0;
                var original=code[skip];var ctor=original.Operand as MethodReference;
                if(original.OpCode!=OpCodes.Newobj||ctor==null||ctor.DeclaringType.FullName!="slv3.WinUsb"||ctor.Name!=".ctor")throw new InvalidOperationException("OpenRGB callback entry changed");
                if(code.Skip(skip).Count(LedGetter)!=6)throw new InvalidOperationException("OpenRGB callback getter layout changed");
                plan.Fingerprint=Fingerprint(method,skip);
                if(!String.IsNullOrEmpty(expectedFingerprint)&&!String.Equals(plan.Fingerprint,expectedFingerprint,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("OpenRGB callback changed; manual review required");
                if(guarded) {plan.Compatible=true;plan.State="Patched";plan.PatchedSha256=plan.SourceSha256;plan.PatchedBytes=bytes;return plan;}
                var getter=(MethodReference)code.First(LedGetter).Operand;
                var il=method.Body.GetILProcessor();
                il.InsertBefore(original,Instruction.Create(OpCodes.Ldarg_1));
                il.InsertBefore(original,Instruction.Create(OpCodes.Callvirt,getter));
                il.InsertBefore(original,Instruction.Create(OpCodes.Brtrue,original));
                il.InsertBefore(original,Instruction.Create(OpCodes.Ret));
                using(var output=new MemoryStream()) {
                    assembly.Write(output,new WriterParameters {WriteSymbols=false,Timestamp=0,DeterministicMvid=true});
                    plan.PatchedBytes=output.ToArray();
                }
                plan.PatchedSha256=Hash(plan.PatchedBytes);plan.State="Native";plan.Compatible=true;
            }
        } catch(Exception error) {plan.Reason=error.GetType().Name+": "+error.Message;plan.PatchedBytes=null;}
        return plan;
    }
}
