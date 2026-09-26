using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Matches every hunk of HSA's source diff to the IL methods it changed: a method
// matches when each identifier/string the hunk adds is referenced more often in
// W's body than in M's, and each one it removes less often.
static class Match
{
    record Hunk(string cls, string id, string header, Dictionary<string, int> gained, Dictionary<string, int> lost, string text, List<string>? touched = null, string? enclosing = null);

    // tokens go on one line of the detail file (string literals may hold line breaks and tabs)
    static string OneLine(string t) => t.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    // HSA's methods named like the one the hunk sits in, whose build differs from the game's
    static List<MethodDefinition> ByEnclosing(Hunk h, List<TypeDefinition> wts, List<TypeDefinition> mts)
    {
        var found = new List<MethodDefinition>();
        if (h.enclosing == null) return found;
        var mByName = new Dictionary<string, MethodDefinition>();
        foreach (var mt in mts) foreach (var me in mt.Methods) mByName.TryAdd(NormCg(me.FullName), me);
        foreach (var wt in wts.Where(t => !Port.IsCompilerGenerated(t)))
            foreach (var wm in wt.Methods.Where(m => m.Name == h.enclosing || m.Name == "get_" + h.enclosing || m.Name == "set_" + h.enclosing))
                if (mByName.TryGetValue(NormCg(wm.FullName), out var mm) && Shape(wm) != Shape(mm)) found.Add(wm);
        return found;
    }

    // Opcodes and operands, for telling whether HSA's compiled method differs from the game's
    static string Shape(MethodDefinition m) => !m.HasBody ? "" : string.Join(";", m.Body.Instructions.Select(i => i.OpCode.Code + " " + NormCg(i.Operand switch
    {
        MemberReference r => r.Name, Instruction => "", Instruction[] => "", VariableDefinition => "", ParameterDefinition => "", _ => i.Operand?.ToString() ?? ""
    })));

    static readonly Regex CgNum = new(@"(>[a-z]__)\d+(_\d+)?");
    static string NormCg(string s) => CgNum.Replace(s, "$1N");

    static IEnumerable<string> Tokens(MethodDefinition m)
    {
        if (!m.HasBody) yield break;
        foreach (var i in m.Body.Instructions)
            switch (i.Operand)
            {
                case string s: yield return "S:" + s; break;
                case MethodReference r:
                    yield return r.Name;
                    yield return r.DeclaringType.GetElementType().Name.Split('`')[0];
                    if (r.Name.StartsWith("get_") || r.Name.StartsWith("set_") || r.Name.StartsWith("add_")) yield return r.Name.Substring(4);
                    if (r.Name == ".ctor") yield return r.DeclaringType.Name;
                    break;
                case FieldReference f: yield return f.Name; yield return f.DeclaringType.GetElementType().Name.Split('`')[0]; break;
                case TypeReference t: yield return t.GetElementType().Name.Split('`')[0]; break;
            }
    }

    static TypeDefinition Top(TypeDefinition t) { while (t.DeclaringType != null) t = t.DeclaringType; return t; }

    public static void Run(string wPath, string mPath, string hunksPath, string? selectedPath = null)
    {
        var W = ModuleDefinition.ReadModule(wPath); var M = ModuleDefinition.ReadModule(mPath);
        var hunks = JsonSerializer.Deserialize<List<Hunk>>(File.ReadAllText(hunksPath))!;
        var universe = new HashSet<string>();
        foreach (var mod in new[] { W, M })
            foreach (var t in Analyze.AllTypes(mod))
            {
                universe.Add(t.Name);
                foreach (var f in t.Fields) universe.Add(f.Name);
                foreach (var p in t.Properties) universe.Add(p.Name);
                foreach (var me in t.Methods) { universe.Add(me.Name); foreach (var x in Tokens(me)) universe.Add(x); }
            }
        var wByTop = Analyze.AllTypes(W).GroupBy(t => Top(t).FullName).ToDictionary(g => g.Key, g => g.ToList());
        var mByTop = Analyze.AllTypes(M).GroupBy(t => Top(t).FullName).ToDictionary(g => g.Key, g => g.ToList());
        var matched = new SortedSet<string>(); var detail = new List<string>(); var added = new SortedSet<string>(); var unmatched = new List<string>();
        var tokCache = new Dictionary<MethodDefinition, Dictionary<string, int>>();
        var cuCache = new Dictionary<string, HashSet<string>>();
        HashSet<string> ClassUniverse(string cls, List<TypeDefinition> wts, List<TypeDefinition> mts)
        {
            if (cuCache.TryGetValue(cls, out var u)) return u;
            u = new HashSet<string>();
            foreach (var t in wts.Concat(mts))
            {
                foreach (var f in t.Fields) u.Add(f.Name);
                foreach (var me in t.Methods) { u.Add(me.Name); foreach (var x in Count(me).Keys) u.Add(x); }
            }
            return cuCache[cls] = u;
        }
        Dictionary<string, int> Count(MethodDefinition m)
        {
            if (!tokCache.TryGetValue(m, out var c)) { c = Tokens(m).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()); tokCache[m] = c; }
            return c;
        }
        foreach (var h in hunks)
        {
            if (!wByTop.TryGetValue(h.cls, out var wts) || !mByTop.TryGetValue(h.cls, out var mts)) { unmatched.Add($"NOCLASS {h.id}"); continue; }
            var cu = ClassUniverse(h.cls, wts, mts);
            var gained = h.gained.Where(kv => cu.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var lost = h.lost.Where(kv => cu.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            // members HSA declares in this hunk
            bool declared = false;
            foreach (var wt in wts.Where(t => !Port.IsCompilerGenerated(t)))
            {
                var mt = mts.FirstOrDefault(x => x.FullName == wt.FullName);
                if (mt == null) continue;
                foreach (var me in wt.Methods)
                {
                    var bare = me.Name.StartsWith("get_") || me.Name.StartsWith("set_") ? me.Name.Substring(4) : me.Name;
                    if ((gained.ContainsKey(me.Name) || gained.ContainsKey(bare)) && !mt.Methods.Any(x => x.FullName == me.FullName)
                        && me.Name != "BeginInvoke" && me.Name != "EndInvoke") { added.Add("M " + me.FullName); declared = true; }
                }
                foreach (var f in wt.Fields)
                    if (gained.ContainsKey(f.Name) && !mt.Fields.Any(x => x.Name == f.Name)) { added.Add("F " + f.FullName); declared = true; }
            }
            if (gained.Count == 0 && lost.Count == 0 && h.touched is { Count: > 0 })
            {
                // operator-only change (a?.b, ??, !): the methods using every name on the changed
                // lines whose HSA build differs from the game's
                var names = h.touched.Where(cu.Contains).ToList();
                var ops = new List<MethodDefinition>();
                if (names.Count > 0)
                {
                    var mByName = new Dictionary<string, MethodDefinition>();
                    foreach (var mt in mts) foreach (var me in mt.Methods) mByName.TryAdd(NormCg(me.FullName), me);
                    foreach (var wt in wts) foreach (var wm in wt.Methods)
                    {
                        if (!mByName.TryGetValue(NormCg(wm.FullName), out var mm)) continue;
                        var cw = Count(wm);
                        if (names.All(n => cw.ContainsKey(n)) && Shape(wm) != Shape(mm)) ops.Add(wm);
                    }
                }
                if (ops.Count == 0) ops = ByEnclosing(h, wts, mts);
                if (ops.Count == 0) { if (!declared) unmatched.Add($"NOTOKENS {h.id} {h.header}\n{h.text}"); continue; }
                foreach (var x in ops) { matched.Add(x.FullName); detail.Add($"{x.FullName}\t{h.id}\tgained=[] lost=[]"); }
                continue;
            }
            if (gained.Count == 0 && lost.Count == 0)
            {
                var enc = ByEnclosing(h, wts, mts);
                if (enc.Count > 0) { foreach (var x in enc) { matched.Add(x.FullName); detail.Add($"{x.FullName}\t{h.id}\tgained=[] lost=[]"); } continue; }
                if (!declared) unmatched.Add($"NOTOKENS {h.id} {h.header}\n{h.text}");
                continue;
            }
            // pair methods W <-> M
            var mIndex = new Dictionary<string, List<MethodDefinition>>();
            foreach (var mt in mts) foreach (var me in mt.Methods) { var k = NormCg(me.FullName); if (!mIndex.TryGetValue(k, out var l)) mIndex[k] = l = new(); l.Add(me); }
            var hits = new List<MethodDefinition>();
            foreach (var wt in wts) foreach (var wm in wt.Methods)
            {
                if (!mIndex.TryGetValue(NormCg(wm.FullName), out var cands) || cands.Count != 1) continue;
                var mm = cands[0];
                var cw = Count(wm); var cm = Count(mm);
                bool ok = gained.All(kv => cw.GetValueOrDefault(kv.Key) > cm.GetValueOrDefault(kv.Key))
                       && lost.All(kv => cm.GetValueOrDefault(kv.Key) > cw.GetValueOrDefault(kv.Key));
                if (ok) hits.Add(wm);
            }
            // no method carries the names (e.g. the compiler dropped code after an early return):
            // the method the hunk sits in
            if (hits.Count == 0) hits.AddRange(ByEnclosing(h, wts, mts));
            if (hits.Count == 0) { if (!declared) unmatched.Add($"NOMATCH {h.id} {h.header} gained=[{string.Join(",", gained.Keys)}] lost=[{string.Join(",", lost.Keys)}]\n{h.text}"); }
            else foreach (var x in hits) { matched.Add(x.FullName); detail.Add($"{x.FullName}\t{h.id}\tgained=[{string.Join(",", gained.Keys.Select(OneLine))}] lost=[{string.Join(",", lost.Keys.Select(OneLine))}]"); }
        }
        // coverage: for hunks without a single matching method, check token by
        // token that some already-selected method of the class carries the change
        if (selectedPath != null)
        {
            var sel = File.ReadAllLines(selectedPath).ToHashSet();
            var uncovered = new List<string>();
            foreach (var h in hunks)
            {
                if (!wByTop.TryGetValue(h.cls, out var wts) || !mByTop.TryGetValue(h.cls, out var mts)) continue;
                var mIdx = new Dictionary<string, MethodDefinition>();
                foreach (var mt in mts) foreach (var me in mt.Methods) mIdx.TryAdd(NormCg(me.FullName), me);
                var selW = wts.SelectMany(t => t.Methods).Where(m => sel.Contains(m.FullName) || Port.IsCompilerGenerated(m.DeclaringType)).ToList();
                var miss = new List<string>();
                var cu = ClassUniverse(h.cls, wts, mts);
                var declaredNames = wts.SelectMany(t => t.Methods.Where(m => sel.Contains(m.FullName)).Select(m => m.Name.StartsWith("get_") || m.Name.StartsWith("set_") ? m.Name.Substring(4) : m.Name)
                    .Concat(t.Fields.Select(f => f.Name)).Concat(new[] { t.Name })).ToHashSet();
                foreach (var (t, _) in h.gained.Where(kv => cu.Contains(kv.Key) && !declaredNames.Contains(kv.Key) && !kv.Key.StartsWith("S:") == true || kv.Key.StartsWith("S:")))
                    if (!declaredNames.Contains(t) && !selW.Any(m => Count(m).GetValueOrDefault(t) > (mIdx.TryGetValue(NormCg(m.FullName), out var mm) ? Count(mm).GetValueOrDefault(t) : 0))) miss.Add("+" + t);
                foreach (var (t, _) in h.lost.Where(kv => cu.Contains(kv.Key)))
                    if (!selW.Any(m => mIdx.TryGetValue(NormCg(m.FullName), out var mm) && Count(mm).GetValueOrDefault(t) > Count(m).GetValueOrDefault(t))) miss.Add("-" + t);
                if (miss.Count > 0) uncovered.Add($"{h.id} {h.header} missing=[{string.Join(",", miss)}]\n{h.text}");
            }
            File.WriteAllText("hunk_uncovered.txt", string.Join("\n\n", uncovered));
            Console.WriteLine($"coverage: {uncovered.Count} hunks not fully reflected in selected methods");
        }
        File.WriteAllLines("hunk_matched.txt", matched); File.WriteAllLines("hunk_matched_detail.txt", detail);
        File.WriteAllLines("hunk_added.txt", added);
        File.WriteAllText("hunk_unmatched.txt", string.Join("\n\n", unmatched));
        Console.WriteLine($"hunks {hunks.Count}: matched methods {matched.Count}, HSA-declared members {added.Count}, unmatched hunks {unmatched.Count}");
    }
}
