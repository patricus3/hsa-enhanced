using Mono.Cecil;
static class Dump
{
    public static void Api(string path)
    {
        var m = ModuleDefinition.ReadModule(path);
        foreach (var t in Analyze.AllTypes(m).Where(t => t.IsPublic || t.IsNestedPublic))
        {
            Console.WriteLine($"type {t.FullName} : {t.BaseType?.FullName}");
            foreach (var me in t.Methods) Console.WriteLine($"  {me.FullName} pinvoke={(me.HasPInvokeInfo ? me.PInvokeInfo.Module.Name + ":" + me.PInvokeInfo.EntryPoint : "-")}");
        }
    }
    public static void Il(string path, string method)
    {
        var m = ModuleDefinition.ReadModule(path);
        foreach (var t in Analyze.AllTypes(m)) foreach (var me in t.Methods)
            if (me.FullName.Contains(method) && me.HasBody) { Console.WriteLine(me.FullName); foreach (var i in me.Body.Instructions) Console.WriteLine("  " + i); }
    }
    public static void Field(string path, string type, string name)
    {
        var m = ModuleDefinition.ReadModule(path);
        foreach (var t in Analyze.AllTypes(m).Where(t => t.FullName == type))
        {
            foreach (var f in t.Fields.Where(f => f.Name == name))
            {
                Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(path))}: {f.FullName} attrs={f.Attributes} const={f.Constant}");
                foreach (var a in f.CustomAttributes) Console.WriteLine($"    [{a.AttributeType.FullName}({string.Join(", ", a.ConstructorArguments.Select(x => x.Value))})]");
            }
            foreach (var me in t.Methods.Where(x => x.Name == name)) Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(path))}: {me.FullName} attrs={me.Attributes}");
        }
    }
    public static void Users(string path, string needle)
    {
        var m = ModuleDefinition.ReadModule(path);
        foreach (var t in Analyze.AllTypes(m)) foreach (var me in t.Methods)
        {
            if (!me.HasBody) continue;
            var hits = me.Body.Instructions.Where(i => (i.Operand is MemberReference r && r.FullName.Contains(needle)) || (i.Operand is string str && str == needle)).Select(i => i.Operand.ToString()).Distinct().ToList();
            if (me.Body.Variables.Any(v => v.VariableType.FullName.Contains(needle))) hits.Add("(local)");
            if (hits.Count > 0) Console.WriteLine($"{me.FullName}\n    {string.Join("\n    ", hits)}");
        }
    }
}
static class Audit
{
    public static void Run(string outPath, string vanillaPath)
    {
        var o = ModuleDefinition.ReadModule(outPath); var v = ModuleDefinition.ReadModule(vanillaPath);
        var vp = Analyze.AllTypes(v).SelectMany(t => t.Methods).Where(m => m.HasPInvokeInfo).Select(m => m.FullName).ToHashSet();
        foreach (var m in Analyze.AllTypes(o).SelectMany(t => t.Methods).Where(m => m.HasPInvokeInfo && !vp.Contains(m.FullName)))
            Console.WriteLine($"NEW PINVOKE {m.FullName} -> {m.PInvokeInfo.Module.Name}:{m.PInvokeInfo.EntryPoint}");
        var vs = Analyze.AllTypes(v).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Where(i => i.Operand is string).Select(i => (string)i.Operand).ToHashSet();
        foreach (var m in Analyze.AllTypes(o).SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var i in m.Body.Instructions)
                if (i.Operand is string s && !vs.Contains(s) && (s.Contains('\\') && !s.Contains('\n') && s.Length < 120 || s.Contains("../") || s.Contains(".dll") || s.Contains(".exe") || s.Contains("C:") || s.Contains("AppData") || s.Contains("Registry")))
                    Console.WriteLine($"STRING {m.FullName}: {s.Replace("\n", "\\n")}");
        foreach (var m in Analyze.AllTypes(o).SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var i in m.Body.Instructions)
                if (i.Operand is MethodReference r && (r.DeclaringType.FullName.StartsWith("Microsoft.Win32") || r.DeclaringType.FullName.Contains("Registry") || r.Name == "get_platform" && m.DeclaringType.Namespace == "Accessibility"))
                    Console.WriteLine($"PLATFORM {m.FullName}: {r.FullName}");
    }
}
static class LostCalls
{
    // For transplanted methods: member refs the Mac body has that W's body lacks,
    // minus what HSA's diff says it removed in that class.
    public static void Run(string wPath, string mPath, string selectedPath, string hunksPath)
    {
        var W = ModuleDefinition.ReadModule(wPath); var M = ModuleDefinition.ReadModule(mPath);
        var sel = File.ReadAllLines(selectedPath).ToHashSet();
        var hunks = System.Text.Json.JsonDocument.Parse(File.ReadAllText(hunksPath)).RootElement;
        var removedByClass = new Dictionary<string, HashSet<string>>();
        foreach (var h in hunks.EnumerateArray())
        {
            var c = h.GetProperty("cls").GetString()!;
            if (!removedByClass.TryGetValue(c, out var set)) removedByClass[c] = set = new();
            foreach (var p in h.GetProperty("lost").EnumerateObject()) set.Add(p.Name);
        }
        var mm = Analyze.AllTypes(M).SelectMany(t => t.Methods).GroupBy(m => m.FullName).ToDictionary(g => g.Key, g => g.First());
        static IEnumerable<string> Refs(MethodDefinition m) => !m.HasBody ? Array.Empty<string>() :
            m.Body.Instructions.Where(i => i.Operand is MethodReference || i.Operand is FieldReference || i.Operand is string)
             .Select(i => i.Operand is MemberReference r ? r.Name : "S:" + i.Operand);
        int n = 0;
        foreach (var wm in Analyze.AllTypes(W).SelectMany(t => t.Methods).Where(m => sel.Contains(m.FullName) && !Port.IsCompilerGenerated(m.DeclaringType)))
        {
            if (!mm.TryGetValue(wm.FullName, out var m)) continue;
            var top = wm.DeclaringType; while (top.DeclaringType != null) top = top.DeclaringType;
            removedByClass.TryGetValue(top.FullName, out var removed);
            // include HSA helpers the method calls (code HSA moved out of it)
            var wr = Refs(wm).ToHashSet();
            var seen = new HashSet<MethodDefinition> { wm }; var q = new Queue<MethodDefinition>(); q.Enqueue(wm);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (!cur.HasBody) continue;
                foreach (var i in cur.Body.Instructions)
                    if (i.Operand is MethodReference r && r.DeclaringType.GetElementType().Scope == W && r.Resolve() is MethodDefinition d
                        && (!mm.ContainsKey(d.FullName) || Port.IsCompilerGenerated(d.DeclaringType)) && seen.Add(d) && seen.Count < 40)
                    { foreach (var x in Refs(d)) wr.Add(x); q.Enqueue(d); }
            }
            var lost = Refs(m).Where(x => !wr.Contains(x) && (removed == null || !removed.Contains(x)) && !x.StartsWith("<")).Distinct().ToList();
            if (lost.Count == 0) continue;
            n++;
            Console.WriteLine($"{wm.FullName}\n    lost: {string.Join(", ", lost.Take(12))}");
        }
        Console.WriteLine($"methods losing Mac references: {n}");
    }
}
static class TypeInfo
{
    public static void Run(string path, string type)
    {
        var m = ModuleDefinition.ReadModule(path);
        var t = Analyze.AllTypes(m).First(x => x.FullName == type);
        Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(path))}: {t.FullName} : {t.BaseType?.FullName} attrs={t.Attributes}");
        foreach (var i in t.Interfaces) Console.WriteLine($"   iface {i.InterfaceType.FullName}");
        foreach (var me in t.Methods.Where(x => x.IsVirtual || x.IsAbstract)) Console.WriteLine($"   {me.Attributes}  {me.FullName} body={me.HasBody}");
    }
}
static class Abstracts
{
    // Every concrete class must implement all abstract methods of its bases and
    // all methods of its interfaces (by name+signature, incl. inherited ones).
    public static int Run(string path)
    {
        var m = ModuleDefinition.ReadModule(path);
        int bad = 0;
        static string Sig(MethodDefinition x) => x.Name + "(" + string.Join(",", x.Parameters.Select(p => p.ParameterType.FullName)) + ")";
        foreach (var t in Analyze.AllTypes(m).Where(t => t.IsClass && !t.IsInterface && !t.IsAbstract))
        {
            var chain = new List<TypeDefinition>();
            for (TypeDefinition? x = t; x != null && x.Module == m; x = x.BaseType != null && x.BaseType.GetElementType().Scope == m && x.BaseType.Resolve() is TypeDefinition b ? b : null) chain.Add(x);
            var impl = chain.SelectMany(c => c.Methods.Where(me => !me.IsAbstract)).Select(Sig).ToHashSet();
            foreach (var c in chain)
                foreach (var am in c.Methods.Where(me => me.IsAbstract))
                    if (!impl.Contains(Sig(am))) { Console.WriteLine($"MISSING {t.FullName} lacks {Sig(am)} (abstract in {c.FullName})"); bad++; }
            foreach (var c in chain)
                foreach (var ii in c.Interfaces)
                    if (ii.InterfaceType.GetElementType().Scope == m && ii.InterfaceType.Resolve() is TypeDefinition it)
                        foreach (var im in it.Methods)
                            if (!impl.Contains(Sig(im)) && !chain.Any(cc => cc.Methods.Any(me => me.Overrides.Any(o => o.Name == im.Name))))
                            { Console.WriteLine($"MISSING {t.FullName} lacks {Sig(im)} (interface {it.FullName})"); bad++; }
        }
        Console.WriteLine($"abstract/interface gaps: {bad}");
        return bad;
    }
}

static class Fields
{
    public static void Run(string path, string type)
    {
        var m = ModuleDefinition.ReadModule(path);
        foreach (var t in Analyze.AllTypes(m).Where(t => t.FullName == type))
            foreach (var f in t.Fields) Console.WriteLine($"{f.FieldType.FullName} {f.Name}");
    }
}
