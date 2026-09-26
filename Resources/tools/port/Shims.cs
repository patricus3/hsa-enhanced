using Mono.Cecil;
using Mono.Cecil.Cil;

// The Mac build ships a stripped mscorlib/System. The few BCL members HSA uses
// that were stripped are replaced by static methods of HsaCompat (added to
// Assembly-CSharp itself), written in IL on top of members the Mac BCL keeps.
static class Shims
{
    public static void Apply(ModuleDefinition M, IEnumerable<MethodDefinition> touched, TextWriter log, bool windows = false)
    {
        var corlib = M.AssemblyResolver.Resolve((AssemblyNameReference)M.TypeSystem.CoreLibrary).MainModule;
        TypeDefinition CT(string n) => corlib.GetType(n) ?? throw new Exception("corlib lacks " + n);
        MethodReference Im(string type, string name, params string[] ps)
        {
            var t = CT(type);
            var md = t.Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == ps.Length &&
                        x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(ps))
                     ?? throw new Exception($"Mac corlib lacks {type}::{name}({string.Join(",", ps)})");
            return M.ImportReference(md);
        }

        var cls = new TypeDefinition("", "HsaCompat", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, M.TypeSystem.Object);
        M.Types.Add(cls);
        var dateTime = M.ImportReference(CT("System.DateTime"));
        var str = M.TypeSystem.String;
        var strArr = new ArrayType(str);
        var envTarget = M.TypeSystem.Int32; // the enum itself was stripped; it is an int on the stack

        MethodDefinition Add(string name, TypeReference ret, params TypeReference[] ps)
        {
            var m = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, ret);
            foreach (var p in ps) m.Parameters.Add(new ParameterDefinition(p));
            cls.Methods.Add(m); return m;
        }

        // DateTime.Today => DateTime.Now.Date
        var today = Add("get_Today", dateTime);
        { var il = today.Body.GetILProcessor(); var v = new VariableDefinition(dateTime); today.Body.Variables.Add(v); today.Body.InitLocals = true;
          il.Emit(OpCodes.Call, Im("System.DateTime", "get_Now")); il.Emit(OpCodes.Stloc, v); il.Emit(OpCodes.Ldloca, v);
          il.Emit(OpCodes.Call, Im("System.DateTime", "get_Date")); il.Emit(OpCodes.Ret); }
        // Environment.GetEnvironmentVariable(name, target) => per-process variable
        var getEnv = Add("GetEnvironmentVariable", str, str, envTarget);
        { var il = getEnv.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0);
          il.Emit(OpCodes.Call, Im("System.Environment", "GetEnvironmentVariable", "System.String")); il.Emit(OpCodes.Ret); }
        var setEnv = Add("SetEnvironmentVariable", M.TypeSystem.Void, str, str, envTarget);
        // only used by HSA to help Windows find Tolk.dll; nothing to do on macOS
        setEnv.Body.GetILProcessor().Emit(OpCodes.Ret);
        // File.WriteAllLines(path, lines) => File.WriteAllText(path, string.Join("\n", lines) + "\n")
        var wal = Add("WriteAllLines", M.TypeSystem.Void, str, strArr);
        { var il = wal.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, "\n"); il.Emit(OpCodes.Ldarg_1);
          il.Emit(OpCodes.Call, Im("System.String", "Join", "System.String", "System.String[]"));
          il.Emit(OpCodes.Ldstr, "\n"); il.Emit(OpCodes.Call, Im("System.String", "Concat", "System.String", "System.String"));
          il.Emit(OpCodes.Call, Im("System.IO.File", "WriteAllText", "System.String", "System.String")); il.Emit(OpCodes.Ret); }

        // Members with no IL-friendly replacement live in HSACompat.dll (compat/)
        var compatAsm = new AssemblyNameReference("HSACompat", new Version(0, 0, 0, 0));
        M.AssemblyReferences.Add(compatAsm);
        var shimType = new TypeReference("HSACompat", "Shim", M, compatAsm);
        var rsplit = new MethodReference("RegexSplit", strArr, shimType) { HasThis = false };
        rsplit.Parameters.Add(new ParameterDefinition(str)); rsplit.Parameters.Add(new ParameterDefinition(str));

        var redirects = new Dictionary<string, MethodReference>
        {
            ["System.String[] System.Text.RegularExpressions.Regex::Split(System.String,System.String)"] = rsplit,
            ["System.DateTime System.DateTime::get_Today()"] = today,
            ["System.String System.Environment::GetEnvironmentVariable(System.String,System.EnvironmentVariableTarget)"] = getEnv,
            ["System.Void System.Environment::SetEnvironmentVariable(System.String,System.String,System.EnvironmentVariableTarget)"] = setEnv,
            ["System.Void System.IO.File::WriteAllLines(System.String,System.String[])"] = wal,
        };
        int n = 0;
        foreach (var m in touched)
        {
            if (!m.HasBody) continue;
            foreach (var i in m.Body.Instructions)
                if (i.Operand is MethodReference r && redirects.TryGetValue(r.FullName, out var to)) { i.Operand = to; if (i.OpCode == OpCodes.Callvirt) i.OpCode = OpCodes.Call; n++; }
            // System.Net.WebClient was stripped from the Mac System.dll; HSA only keeps it in a local
            foreach (var v in m.Body.Variables)
                if (v.VariableType.FullName == "System.Net.WebClient")
                {
                    bool used = m.Body.Instructions.Any(i => i.Operand is MemberReference r && r.FullName.Contains("System.Net.WebClient"));
                    if (used) log.WriteLine($"SHIM WARNING: WebClient really used in {m.FullName}");
                    v.VariableType = M.TypeSystem.Object; n++;
                }
        }
        // P/Invokes into Windows system DLLs came along with Windows-only game code
        // (e.g. HearthstoneApplication.SetWindowText via user32.dll). Turn them into
        // managed no-ops returning default values. On Windows they are real and stay.
        if (windows) { log.WriteLine($"shims: {n} references redirected to HsaCompat, Windows P/Invokes kept (Windows build)"); return; }
        var winDlls = new[] { "user32", "kernel32", "gdi32", "advapi32", "shell32", "ole32", "dwmapi", "winmm", "ntdll", "shcore", "psapi", "version", "setupapi", "hid", "xinput" };
        int nulled = 0;
        foreach (var m in touched.Where(m => m.HasPInvokeInfo).ToList())
        {
            var mod = m.PInvokeInfo.Module.Name.ToLowerInvariant().Replace(".dll", "");
            if (!winDlls.Contains(mod)) { log.WriteLine($"SHIM WARNING: non-Windows P/Invoke left as is: {m.FullName} -> {m.PInvokeInfo.Module.Name}"); continue; }
            m.PInvokeInfo = null;
            m.IsPInvokeImpl = false; m.IsPreserveSig = false;
            m.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
            var il = m.Body.GetILProcessor();
            m.Body.InitLocals = true;
            var rt = m.ReturnType;
            if (rt.MetadataType == MetadataType.Void) { }
            else if (rt.IsValueType || rt.IsGenericParameter)
            {
                var v = new VariableDefinition(rt); m.Body.Variables.Add(v);
                il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Initobj, rt); il.Emit(OpCodes.Ldloc, v);
            }
            else il.Emit(OpCodes.Ldnull);
            // out/ref parameters stay untouched (callers treat the call as failed)
            il.Emit(OpCodes.Ret);
            nulled++;
            log.WriteLine($"shim: Windows P/Invoke {m.FullName} -> no-op");
        }
        log.WriteLine($"shims: {n} references redirected to HsaCompat, {nulled} Windows P/Invokes turned into no-ops");
    }
}
