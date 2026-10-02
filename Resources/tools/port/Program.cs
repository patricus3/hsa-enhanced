using Mono.Cecil;
using Mono.Cecil.Cil;

switch (args[0])
{
    case "analyze": Analyze.Run(args[1], args[2]); break;
    case "enums":
        {
            var W = ModuleDefinition.ReadModule(args[1]); var M = ModuleDefinition.ReadModule(args[2]);
            foreach (var (name, (w, m)) in Enums.Diff(W, M))
            {
                int moved = w.Count(kv => m.TryGetValue(kv.Key, out var v) && v != kv.Value), gone = w.Keys.Count(k => !m.ContainsKey(k));
                Console.WriteLine($"{name}: W {w.Count} M {m.Count} changed {moved} missingInM {gone}");
                foreach (var k in w.Keys.Where(k => !m.ContainsKey(k))) Console.WriteLine($"    only W: {k} = {w[k]}");
                foreach (var k in m.Keys.Where(k => !w.ContainsKey(k))) Console.WriteLine($"    only M: {k} = {m[k]}");
            }
            break;
        }
    case "check": Check.Run(args[1], args.Skip(2).ToArray()); break;
    case "field": Dump.Field(args[1], args[2], args[3]); break;
    case "diffmethods": DiffMethods.Run(args[1], args[2], args[3], args[4]); break;
    case "match": Match.Run(args[1], args[2], args[3], args.Length > 4 ? args[4] : null); break;
    case "audit": Audit.Run(args[1], args[2]); break;
    case "lostcalls": LostCalls.Run(args[1], args[2], args[3], args[4]); break;
    case "type": TypeInfo.Run(args[1], args[2]); break;
    case "abstracts": Environment.Exit(Abstracts.Run(args[1]) > 0 ? 1 : 0); break;
    case "fields": Fields.Run(args[1], args[2]); break;
    case "il": Dump.Il(args[1], args[2]); break;
    case "api": Dump.Api(args[1]); break;
    case "users": Dump.Users(args[1], args[2]); break;
    case "port":
        {
            // --windows: the target is the Windows build of the game (keep its Windows P/Invokes)
            var pa = args.Where(a => a != "--windows").ToArray();
            RunPort(pa[1], pa[2], pa[3], pa.Length > 4 ? pa[4] : null, args.Contains("--windows"));
            break;
        }
    case "hunks": Hunks.Extract(args[1], args[2]); break;
    case "seeds": Hunks.Seeds(args[1], args[2]); break;
    case "speech": Enhance.RetargetSpeech(args[1], args[2]); break;
    case "expose": Enhance.Expose(args[1], args[2]); break;
    case "hook": Environment.Exit(Enhance.Hook(args[1], args[2], args[3], args[4], args.Contains("--hsa-menus"), args.Contains("--without-hsa")) ? 0 : 1); break;
    default: Console.WriteLine("unknown command"); break;
}

static void RunPort(string wPath, string mPath, string outPath, string? refDir, bool windows)
{
    var macDir = refDir ?? Path.GetDirectoryName(Path.GetFullPath(mPath))!;
    var mRes = new DefaultAssemblyResolver(); mRes.AddSearchDirectory(macDir);
    var wRes = new DefaultAssemblyResolver(); wRes.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(wPath))!);
    var W = ModuleDefinition.ReadModule(wPath, new ReaderParameters { AssemblyResolver = wRes });
    var M = ModuleDefinition.ReadModule(mPath, new ReaderParameters { AssemblyResolver = mRes, ReadWrite = false, InMemory = true });
    var log = new StringWriter();
    var ef = new EnumFix(W, M, log);
    ef.AddMembersAndBuildMaps();
    foreach (var t in Analyze.AllTypes(W)) foreach (var me in t.Methods) ef.Process(me);
    log.WriteLine($"enum constants rewritten: {ef.rewritten}, methods using HSA-added enum members: {ef.usesAddedMembers.Count}");
    foreach (var me in ef.usesAddedMembers) log.WriteLine($"  uses added enum member: {me.FullName}");
    var p = new Port(W, M, log);
    p.TargetIsWindows = windows;
    foreach (var me in ef.usesAddedMembers) p.seeds.Add(me);
    p.LoadDiffSeeds("hunk_edit_seeds.txt", "hunk_added.txt");
    p.Select();
    var sel = new HashSet<MethodDefinition>(p.SelectedW());
    foreach (var (wm, text) in ef.warnings.Distinct().Where(x => sel.Contains(x.m))) log.WriteLine($"  ENUM WARNING: {text} in {wm.FullName}");
    File.WriteAllLines("selected_methods.txt", sel.Select(x => x.FullName).Concat(ef.usesAddedMembers.Select(x => x.FullName)).Distinct());
    File.WriteAllLines("edit_reasons.txt", p.reasons.Select(kv => $"{kv.Key.FullName}\t{kv.Value}"));
    p.CreateShells(); p.Fill();
    Shims.Apply(M, p.Touched().ToList(), log, windows);
    LogMerge.Apply(W, M, p.PulledGetterNames("Log"), log);
    Console.Write(log.ToString());
    foreach (var ar in M.AssemblyReferences) Console.WriteLine($"asmref {ar.FullName}");
    foreach (var g in p.problems.GroupBy(x => x).Take(40)) Console.WriteLine($"PROBLEM x{g.Count()}: {g.Key}");
    Console.WriteLine($"problems total {p.problems.Count}");

    // verify external references against the Mac assemblies
    var missing = new SortedDictionary<string, int>();
    void Check(MemberReference r)
    {
        try
        {
            if (r is GenericParameter) return;
            var scope = (r is TypeReference tr0 ? tr0 : r.DeclaringType)?.GetElementType().Scope;
            if (scope == null || scope == M) return;
            object? res = r switch { TypeReference t => t.Resolve(), FieldReference f => f.Resolve(), MethodReference m => m.Resolve(), _ => null };
            if (res == null) { var k = $"{scope.Name}: {r.FullName}"; missing[k] = missing.GetValueOrDefault(k) + 1; }
        }
        catch (AssemblyResolutionException e) { var k = $"ASSEMBLY {e.AssemblyReference.Name}"; missing[k] = missing.GetValueOrDefault(k) + 1; }
    }
    foreach (var m in p.Touched())
    {
        Check(m.ReturnType); foreach (var pa in m.Parameters) Check(pa.ParameterType);
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) if (i.Operand is MemberReference r) Check(r is GenericInstanceMethod g ? g.ElementMethod : r);
        foreach (var v in m.Body.Variables) Check(v.VariableType);
    }
    foreach (var t in p.TouchedTypes()) { if (t.BaseType != null) Check(t.BaseType); foreach (var f in t.Fields) Check(f.FieldType); }
    // the mod's own assemblies (speech, compat) are not in the game folder yet; `port check` looks at them later
    foreach (var own in missing.Keys.Where(k => k == "ASSEMBLY TolkDotNet" || k == "ASSEMBLY HSACompat" || k == "ASSEMBLY HSAPrism").ToList()) missing.Remove(own);
    File.WriteAllLines("missing_refs.txt", missing.Select(kv => $"{kv.Value}\t{kv.Key}"));
    Console.WriteLine($"missing external refs: {missing.Count} (see missing_refs.txt)");
    M.Write(outPath);
    Console.WriteLine($"written {outPath}");
}
