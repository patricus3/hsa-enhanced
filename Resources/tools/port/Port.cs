using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Transplants Hearthstone Access (compiled into the Windows Assembly-CSharp, W)
// into the macOS vanilla Assembly-CSharp (M).
//  H  = HSA-origin code: namespace Accessibility, plus W-only members/types and
//       compiler-generated types whose code references H (fixpoint).
//  E  = methods that exist in both but whose W body references H -> W body wins.
//  F  = forward closure: everything H/E need that M lacks is copied as well.
class Port
{
    readonly ModuleDefinition W, M;
    readonly TextWriter log;

    readonly Dictionary<string, TypeDefinition> mTypeByName = new();
    readonly HashSet<TypeDefinition> wOnlyTypes = new();

    // W definition -> M definition
    readonly Dictionary<TypeDefinition, TypeDefinition> typeMap = new();
    readonly Dictionary<FieldDefinition, FieldDefinition> fieldMap = new();
    readonly Dictionary<MethodDefinition, MethodDefinition> methodMap = new();

    // selection
    readonly HashSet<IMemberDefinition> H = new();
    readonly HashSet<MethodDefinition> edited = new();
    readonly HashSet<TypeDefinition> pullTypes = new();      // W-only types to copy wholesale
    readonly HashSet<FieldDefinition> pullFields = new();    // W-only fields on existing types
    readonly HashSet<MethodDefinition> pullMethods = new();  // W-only methods on existing types

    public readonly List<string> problems = new();
    public readonly HashSet<MethodDefinition> seeds = new();
    public readonly HashSet<IMemberDefinition> declaredByHsa = new();

    // Seeds from HSA's source diff (see Match / seeds.py): full names of methods
    // it edits, and "M name"/"F name" lines for members it declares.
    public void LoadDiffSeeds(string editsFile, string addedFile)
    {
        var methods = Analyze.AllTypes(W).SelectMany(t => t.Methods).GroupBy(m => m.FullName).ToDictionary(g => g.Key, g => g.First());
        var fields = Analyze.AllTypes(W).SelectMany(t => t.Fields).GroupBy(f => f.FullName).ToDictionary(g => g.Key, g => g.First());
        int e = 0, a = 0;
        if (File.Exists(editsFile))
            foreach (var l in File.ReadAllLines(editsFile))
                if (methods.TryGetValue(l.Trim(), out var m)) { seeds.Add(m); e++; }
        if (File.Exists(addedFile))
            foreach (var l in File.ReadAllLines(addedFile))
            {
                if (l.StartsWith("M ") && methods.TryGetValue(l[2..], out var m)) { declaredByHsa.Add(m); a++; }
                else if (l.StartsWith("F ") && fields.TryGetValue(l[2..], out var f)) { declaredByHsa.Add(f); a++; }
            }
        log.WriteLine($"diff seeds: {e} edited methods, {a} declared members");
    }
    readonly HashSet<MethodDefinition> staticFlips = new();
    public readonly Dictionary<MethodDefinition, string> reasons = new();

    public Port(ModuleDefinition w, ModuleDefinition m, TextWriter log)
    {
        W = w; M = m; this.log = log;
        foreach (var t in Analyze.AllTypes(M)) mTypeByName[t.FullName] = t;
        foreach (var t in Analyze.AllTypes(W))
        {
            if (mTypeByName.TryGetValue(t.FullName, out var mt) && !IsCompilerGenerated(t))
            {
                typeMap[t] = mt;
                var mf = mt.Fields.ToDictionary(f => f.Name);
                foreach (var f in t.Fields)
                    if (mf.TryGetValue(f.Name, out var x) && x.FieldType.FullName == f.FieldType.FullName) fieldMap[f] = x;
                var mm = new Dictionary<string, MethodDefinition>();
                foreach (var x in mt.Methods) mm[x.FullName] = x;
                foreach (var me in t.Methods)
                    if (mm.TryGetValue(me.FullName, out var x)) methodMap[me] = x;
            }
            else wOnlyTypes.Add(t);
        }
    }

    public static bool IsCompilerGenerated(TypeDefinition t)
    {
        for (var x = t; x != null; x = x.DeclaringType)
            if (x.Name.StartsWith("<")) return true;
        return false;
    }

    static TypeDefinition? TopCompilerGenerated(TypeDefinition t)
    {
        TypeDefinition? top = null;
        for (var x = t; x != null; x = x.DeclaringType)
            if (x.Name.StartsWith("<")) top = x;
        return top;
    }

    // ---------- reference walking ----------
    static IEnumerable<MemberReference> BodyRefs(MethodDefinition m)
    {
        if (!m.HasBody) yield break;
        foreach (var i in m.Body.Instructions)
            if (i.Operand is MemberReference r) yield return r;
        foreach (var v in m.Body.Variables) yield return v.VariableType;
        foreach (var h in m.Body.ExceptionHandlers) if (h.CatchType != null) yield return h.CatchType;
    }

    static IEnumerable<MemberReference> SigRefs(MethodDefinition m)
    {
        yield return m.ReturnType;
        foreach (var p in m.Parameters) yield return p.ParameterType;
        foreach (var o in m.Overrides) yield return o;
        foreach (var gp in m.GenericParameters) foreach (var c in gp.Constraints) yield return c.ConstraintType;
        foreach (var a in m.CustomAttributes) yield return a.Constructor;
    }

    static IEnumerable<MemberReference> TypeRefs(TypeDefinition t)
    {
        if (t.BaseType != null) yield return t.BaseType;
        foreach (var i in t.Interfaces) yield return i.InterfaceType;
        foreach (var gp in t.GenericParameters) foreach (var c in gp.Constraints) yield return c.ConstraintType;
        foreach (var a in t.CustomAttributes) yield return a.Constructor;
        foreach (var f in t.Fields) yield return f.FieldType;
    }

    // Every W definition mentioned by a reference (including generic arguments etc.)
    IEnumerable<IMemberDefinition> WDefs(MemberReference r)
    {
        switch (r)
        {
            case GenericInstanceMethod gim:
                foreach (var d in WDefs(gim.ElementMethod)) yield return d;
                foreach (var a in gim.GenericArguments) foreach (var d in WDefs(a)) yield return d;
                yield break;
            case MethodReference mr:
                foreach (var d in WDefs(mr.DeclaringType)) yield return d;
                if (mr.Module == W && mr.DeclaringType.GetElementType().Scope == W)
                {
                    var md = mr.Resolve(); if (md != null) yield return md;
                }
                foreach (var d in WDefs(mr.ReturnType)) yield return d;
                foreach (var p in mr.Parameters) foreach (var d in WDefs(p.ParameterType)) yield return d;
                yield break;
            case FieldReference fr:
                foreach (var d in WDefs(fr.DeclaringType)) yield return d;
                if (fr.DeclaringType.GetElementType().Scope == W) { var fd = fr.Resolve(); if (fd != null) yield return fd; }
                foreach (var d in WDefs(fr.FieldType)) yield return d;
                yield break;
            case GenericInstanceType git:
                foreach (var d in WDefs(git.ElementType)) yield return d;
                foreach (var a in git.GenericArguments) foreach (var d in WDefs(a)) yield return d;
                yield break;
            case TypeSpecification ts:
                foreach (var d in WDefs(ts.ElementType)) yield return d;
                yield break;
            case GenericParameter:
                yield break;
            case TypeReference tr:
                if (tr.Scope == W || tr is TypeDefinition { Module: var mod } && mod == W)
                {
                    var td = tr as TypeDefinition ?? tr.Resolve();
                    if (td != null) yield return td;
                }
                yield break;
        }
    }

    bool IsWOnlyMember(IMemberDefinition d) => d switch
    {
        TypeDefinition t => wOnlyTypes.Contains(t),
        FieldDefinition f => !fieldMap.ContainsKey(f),
        MethodDefinition m => !methodMap.ContainsKey(m),
        _ => false
    };

    bool InH(IMemberDefinition d)
    {
        if (H.Contains(d)) return true;
        var t = d as TypeDefinition ?? d.DeclaringType;
        for (var x = t; x != null; x = x.DeclaringType) if (H.Contains(x)) return true;
        return false;
    }

    // ---------- phase A: selection ----------
    public void Select()
    {
        // HSA turned some game methods static. Keep M's method for the game's own
        // callers and add W's version next to it under another name for HSA code.
        foreach (var f in methodMap.Where(kv => kv.Key.IsStatic != kv.Value.IsStatic).Select(kv => kv.Key).ToList())
        {
            methodMap.Remove(f); staticFlips.Add(f);
            log.WriteLine($"  static changed by HSA: {f.FullName} (static={f.IsStatic}) -> added as {f.Name}_hsaStatic");
        }
        foreach (var t in wOnlyTypes)
            if (t.Namespace == "Accessibility" || (t.DeclaringType != null && TopLevel(t).Namespace == "Accessibility"))
                H.Add(TopLevel(t));

        foreach (var d in declaredByHsa)
            if (IsWOnlyMember(d)) H.Add(d is MethodDefinition dm && TopCompilerGenerated(dm.DeclaringType) is TypeDefinition cgt ? cgt : d);
        var allMethods = Analyze.AllTypes(W).SelectMany(t => t.Methods).ToList();
        var allFields = Analyze.AllTypes(W).SelectMany(t => t.Fields).ToList();
        bool changed = true; int round = 0;
        while (changed)
        {
            changed = false; round++;
            foreach (var m in allMethods)
            {
                if (InH(m)) continue;
                IMemberDefinition? why = null;
                bool refsH = seeds.Contains(m);
                if (!refsH) { why = BodyRefs(m).Concat(SigRefs(m)).SelectMany(WDefs).FirstOrDefault(InH); refsH = why != null; }
                if (!refsH) continue;
                reasons[m] = seeds.Contains(m) ? "seed" : why!.FullName;
                var cg = TopCompilerGenerated(m.DeclaringType);
                // "<>c" is one shared container for all static lambdas of a class:
                // only the lambda itself is HSA's, not every method using that class
                if (cg != null && cg.Name == "<>c") { if (H.Add(m)) changed = true; continue; }
                if (cg != null) { if (H.Add(cg)) changed = true; continue; }
                if (!methodMap.ContainsKey(m)) { if (H.Add(m)) changed = true; }
                else if (edited.Add(m)) changed = true;
            }
            foreach (var f in allFields)
            {
                if (InH(f) || fieldMap.ContainsKey(f)) continue;
                if (WDefs(f.FieldType).Any(InH)) { H.Add(f); changed = true; }
            }
            foreach (var t in wOnlyTypes)
            {
                if (InH(t)) continue;
                if (TypeRefs(t).SelectMany(WDefs).Any(InH)) { H.Add(TopCompilerGenerated(t) ?? TopLevelWOnly(t)); changed = true; }
            }
        }
        log.WriteLine($"select: {round} rounds, H items {H.Count}, edited methods {edited.Count}");

        // forward closure
        var work = new Queue<IMemberDefinition>();
        void Need(IMemberDefinition d)
        {
            if (!IsWOnlyMember(d)) return;
            switch (d)
            {
                case TypeDefinition t:
                    var owner = TopLevelWOnly(t);
                    if (pullTypes.Add(owner)) work.Enqueue(owner);
                    break;
                case FieldDefinition f:
                    if (wOnlyTypes.Contains(f.DeclaringType)) Need(f.DeclaringType);
                    else if (pullFields.Add(f)) work.Enqueue(f);
                    break;
                case MethodDefinition m:
                    if (wOnlyTypes.Contains(m.DeclaringType)) Need(m.DeclaringType);
                    else if (pullMethods.Add(m)) work.Enqueue(m);
                    break;
            }
        }
        foreach (var d in H) Need(d);
        foreach (var m in edited) work.Enqueue(m);
        // Game classes that HSA re-parents or gives HSA interfaces: their W-only
        // virtual methods implement those contracts and are reached only through
        // virtual dispatch, so pull them explicitly.
        foreach (var (wt, mt) in typeMap.ToList())
        {
            if (wOnlyTypes.Contains(wt)) continue;
            bool reparent = wt.BaseType != null && mt.BaseType != null && wt.BaseType.FullName != mt.BaseType.FullName && WDefs(wt.BaseType).Any(InH);
            bool iface = wt.Interfaces.Any(i => !mt.Interfaces.Any(x => x.InterfaceType.FullName == i.InterfaceType.FullName) && WDefs(i.InterfaceType).Any(InH));
            if (!reparent && !iface) continue;
            foreach (var m in wt.Methods)
                if (m.IsVirtual && !methodMap.ContainsKey(m)) Need(m);
        }
        var allExisting = methodMap.Keys.ToList();
        bool grew = true;
        while (grew)
        {
        grew = false;
        while (work.Count > 0)
        {
            var d = work.Dequeue();
            IEnumerable<MemberReference> refs = d switch
            {
                TypeDefinition t => AllNested(t).SelectMany(x => TypeRefs(x).Concat(x.Methods.SelectMany(me => BodyRefs(me).Concat(SigRefs(me))))
                                     .Concat(x.Properties.SelectMany(p => p.CustomAttributes.Select(a => (MemberReference)a.Constructor)))),
                FieldDefinition f => new MemberReference[] { f.FieldType }.Concat(f.CustomAttributes.Select(a => (MemberReference)a.Constructor)),
                MethodDefinition m => BodyRefs(m).Concat(SigRefs(m)),
                _ => Array.Empty<MemberReference>()
            };
            foreach (var r in refs) foreach (var x in WDefs(r)) Need(x);
        }
        // Existing game code that initialises fields HSA added (e.g. Log..cctor
        // creating the "Accessibility" logger) must come along too.
        foreach (var m in allExisting)
        {
            if (edited.Contains(m) || !m.HasBody) continue;
            bool stores = m.Body.Instructions.Any(i => (i.OpCode.Code == Code.Stfld || i.OpCode.Code == Code.Stsfld) &&
                i.Operand is FieldReference fr && fr.DeclaringType.GetElementType().Scope == W && fr.Resolve() is FieldDefinition fd &&
                (pullFields.Contains(fd) || (wOnlyTypes.Contains(fd.DeclaringType) && !IsCompilerGenerated(fd.DeclaringType) && pullTypes.Contains(TopLevelWOnly(fd.DeclaringType)))) &&
                !wOnlyTypes.Contains(m.DeclaringType));
            if (!stores) continue;
            edited.Add(m); work.Enqueue(m); grew = true;
            log.WriteLine($"  edited because it initialises an HSA field: {m.FullName}");
        }
        }
        log.WriteLine($"closure: types {pullTypes.Count}, fields {pullFields.Count}, methods {pullMethods.Count}");
        foreach (var t in pullTypes.Where(t => !InH(t)).OrderBy(t => t.FullName))
            log.WriteLine($"  non-HSA type pulled: {t.FullName}");
        foreach (var m in pullMethods.Where(m => !InH(m)).OrderBy(m => m.FullName))
            log.WriteLine($"  non-HSA method pulled: {m.FullName}");
        foreach (var f in pullFields.Where(f => !InH(f)).OrderBy(f => f.FullName))
            log.WriteLine($"  non-HSA field pulled: {f.FullName}");
    }

    static TypeDefinition TopLevel(TypeDefinition t) { while (t.DeclaringType != null) t = t.DeclaringType; return t; }
    TypeDefinition TopLevelWOnly(TypeDefinition t)
    {
        while (t.DeclaringType != null && wOnlyTypes.Contains(t.DeclaringType)) t = t.DeclaringType;
        return t;
    }
    static IEnumerable<TypeDefinition> AllNested(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes) foreach (var x in AllNested(n)) yield return x;
    }

    // ---------- phase B: create shells ----------
    public void CreateShells()
    {
        foreach (var t in pullTypes.OrderBy(t => t.FullName))
        {
            var mt = ShellType(t, IsCompilerGenerated(t) ? "_hsa" : "");
            if (t.DeclaringType != null)
            {
                if (!typeMap.TryGetValue(t.DeclaringType, out var owner)) { problems.Add($"no owner for {t.FullName}"); continue; }
                owner.NestedTypes.Add(mt);
            }
            else
            {
                // (compiler-generated types come in renamed, e.g. <PrivateImplementationDetails>_hsa)
                if (mTypeByName.ContainsKey(mt.FullName)) problems.Add($"name clash {mt.FullName}");
                M.Types.Add(mt);
            }
        }
        foreach (var f in pullFields)
        {
            var owner = typeMap[f.DeclaringType];
            var nf = new FieldDefinition(f.Name, f.Attributes, M.TypeSystem.Object);
            owner.Fields.Add(nf); fieldMap[f] = nf;
        }
        foreach (var m in pullMethods)
        {
            var owner = typeMap[m.DeclaringType];
            var nm = ShellMethod(m);
            owner.Methods.Add(nm); methodMap[m] = nm;
        }
    }

    TypeDefinition ShellType(TypeDefinition t, string suffix)
    {
        var mt = new TypeDefinition(t.Namespace, t.Name + (t.Name.StartsWith("<") ? suffix : ""), t.Attributes);
        mt.PackingSize = t.PackingSize; mt.ClassSize = t.ClassSize;
        foreach (var gp in t.GenericParameters) mt.GenericParameters.Add(new GenericParameter(gp.Name, mt) { Attributes = gp.Attributes });
        typeMap[t] = mt;
        foreach (var f in t.Fields) { var nf = new FieldDefinition(f.Name, f.Attributes, M.TypeSystem.Object); mt.Fields.Add(nf); fieldMap[f] = nf; }
        foreach (var m in t.Methods) { var nm = ShellMethod(m); mt.Methods.Add(nm); methodMap[m] = nm; }
        foreach (var n in t.NestedTypes) mt.NestedTypes.Add(ShellType(n, suffix));
        return mt;
    }

    MethodDefinition ShellMethod(MethodDefinition m)
    {
        var nm = new MethodDefinition(staticFlips.Contains(m) ? m.Name + "_hsaStatic" : m.Name, m.Attributes, M.TypeSystem.Void) { ImplAttributes = m.ImplAttributes, SemanticsAttributes = m.SemanticsAttributes };
        foreach (var gp in m.GenericParameters) nm.GenericParameters.Add(new GenericParameter(gp.Name, nm) { Attributes = gp.Attributes });
        return nm;
    }

    // ---------- import ----------
    // Generic parameters are encoded by position only (VAR n / MVAR n), so
    // positional placeholders owned by dummy owners are enough.
    TypeReference? dummyType; MethodReference? dummyMethod;
    GenericParameter DummyTypeGp(int pos)
    {
        dummyType ??= new TypeReference("", "HsaDummy", M, M);
        while (dummyType.GenericParameters.Count <= pos) dummyType.GenericParameters.Add(new GenericParameter("T" + dummyType.GenericParameters.Count, dummyType));
        return dummyType.GenericParameters[pos];
    }
    GenericParameter DummyMethodGp(int pos)
    {
        dummyMethod ??= new MethodReference("HsaDummy", M.TypeSystem.Void, M.TypeSystem.Object);
        while (dummyMethod.GenericParameters.Count <= pos) dummyMethod.GenericParameters.Add(new GenericParameter("M" + dummyMethod.GenericParameters.Count, dummyMethod));
        return dummyMethod.GenericParameters[pos];
    }
    TypeReference T(TypeReference t)
    {
        switch (t)
        {
            case GenericParameter gp:
                return gp.Type == GenericParameterType.Method ? DummyMethodGp(gp.Position) : DummyTypeGp(gp.Position);
            case GenericInstanceType git:
                { var n = new GenericInstanceType(T(git.ElementType)); foreach (var a in git.GenericArguments) n.GenericArguments.Add(T(a)); return n; }
            case ArrayType at:
                { var n = new ArrayType(T(at.ElementType), at.Rank); if (at.Rank > 1) { n.Dimensions.Clear(); foreach (var d in at.Dimensions) n.Dimensions.Add(d); } return n; }
            case ByReferenceType br: return new ByReferenceType(T(br.ElementType));
            case PointerType pt: return new PointerType(T(pt.ElementType));
            case PinnedType pn: return new PinnedType(T(pn.ElementType));
            case RequiredModifierType rm: return new RequiredModifierType(T(rm.ModifierType), T(rm.ElementType));
            case OptionalModifierType om: return new OptionalModifierType(T(om.ModifierType), T(om.ElementType));
            case SentinelType st: return new SentinelType(T(st.ElementType));
        }
        if (t.Scope == W || (t is TypeDefinition td0 && td0.Module == W))
        {
            var td = t as TypeDefinition ?? t.Resolve();
            if (td != null && typeMap.TryGetValue(td, out var mt)) return mt;
            problems.Add($"unmapped W type {t.FullName}");
            return M.TypeSystem.Object;
        }
        return M.ImportReference(t);
    }

    FieldReference F(FieldReference f)
    {
        var dt = f.DeclaringType;
        if (dt is not GenericInstanceType && dt.GetElementType().Scope == W)
        {
            var fd = f.Resolve();
            if (fd != null && fieldMap.TryGetValue(fd, out var mf)) return mf;
            problems.Add($"unmapped W field {f.FullName}");
        }
        if (dt.GetElementType().Scope == W && dt is GenericInstanceType)
        {
            var fd = f.Resolve();
            if (fd != null && fieldMap.TryGetValue(fd, out var mf)) return new FieldReference(mf.Name, mf.FieldType, T(dt));
        }
        return new FieldReference(f.Name, T(f.FieldType), T(dt));
    }

    MethodReference Me(MethodReference r)
    {
        if (r is GenericInstanceMethod gim)
        {
            var n = new GenericInstanceMethod(Me(gim.ElementMethod));
            foreach (var a in gim.GenericArguments) n.GenericArguments.Add(T(a));
            return n;
        }
        var dt = r.DeclaringType;
        if (dt.GetElementType().Scope == W)
        {
            var md = r.Resolve();
            if (md == null || !methodMap.TryGetValue(md, out var mm)) { problems.Add($"unmapped W method {r.FullName}"); return M.ImportReference(r); }
            if (dt is not GenericInstanceType) return mm;
            var n = new MethodReference(mm.Name, mm.ReturnType, T(dt)) { HasThis = mm.HasThis, ExplicitThis = mm.ExplicitThis, CallingConvention = mm.CallingConvention };
            foreach (var gp in mm.GenericParameters) n.GenericParameters.Add(new GenericParameter(gp.Name, n));
            foreach (var p in mm.Parameters) n.Parameters.Add(new ParameterDefinition(p.ParameterType));
            return n;
        }
        var x = new MethodReference(r.Name, M.TypeSystem.Void, T(dt)) { HasThis = r.HasThis, ExplicitThis = r.ExplicitThis, CallingConvention = r.CallingConvention };
        foreach (var gp in r.GenericParameters) x.GenericParameters.Add(new GenericParameter(gp.Name, x));
        x.ReturnType = T(r.ReturnType);
        foreach (var p in r.Parameters) x.Parameters.Add(new ParameterDefinition(T(p.ParameterType)));
        return x;
    }

    CustomAttribute CA(CustomAttribute a)
    {
        var n = new CustomAttribute(Me(a.Constructor), a.GetBlob());
        return n;
    }

    // ---------- phase C: fill ----------
    public void Fill()
    {
        // declarations first (signatures of generic-instance member references are
        // built from them), then attributes and bodies
        foreach (var t in pullTypes) FillDecl(t);
        foreach (var f in pullFields) FillField(f, fieldMap[f]);
        foreach (var m in pullMethods) FillSig(m, methodMap[m]);
        foreach (var t in pullTypes) FillType(t);
        foreach (var f in pullFields) foreach (var a in f.CustomAttributes) fieldMap[f].CustomAttributes.Add(CA(a));
        foreach (var m in pullMethods) FillSigAttrs(m, methodMap[m]);
        foreach (var m in pullMethods) FillBody(m, methodMap[m]);
        foreach (var m in edited)
        {
            if (mergeInsert.Contains(m.FullName) && TryMergeInsert(m, methodMap[m])) continue;
            FillBody(m, methodMap[m]);
        }
        FixExistingTypes();
        // properties/events on existing types whose accessors got pulled
        foreach (var wt in pullMethods.Select(m => m.DeclaringType).Distinct())
        {
            var mt = typeMap[wt];
            foreach (var p in wt.Properties)
            {
                if (mt.Properties.Any(x => x.Name == p.Name)) continue;
                if (!(p.GetMethod != null && pullMethods.Contains(p.GetMethod) || p.SetMethod != null && pullMethods.Contains(p.SetMethod))) continue;
                mt.Properties.Add(CopyProp(p));
            }
        }
    }

    bool IsHsaType(TypeReference t) => WDefs(t).Any(d => d is TypeDefinition td && (InH(td) || pullTypes.Contains(TopLevelWOnly(td))));

    // Member access codes (same for fields and methods): 1 private, 2 famANDassem,
    // 3 assembly, 4 family, 5 famORassem, 6 public. Smallest access that allows
    // everything either side allows.
    static int Union(int a, int b)
    {
        if (a == b) return a;
        if (a == 6 || b == 6) return 6;
        bool asm = a is 2 or 3 or 5 || b is 2 or 3 or 5, fam = a is 2 or 4 or 5 || b is 2 or 4 or 5;
        bool asmFull = a is 3 or 5 || b is 3 or 5, famFull = a is 4 or 5 || b is 4 or 5;
        if (asmFull && famFull) return 5;
        if (asmFull) return 3;
        if (famFull) return 4;
        if (asm || fam) return 2;
        return Math.Max(a, b);
    }

    static int Rank(FieldAttributes a) => (int)(a & FieldAttributes.FieldAccessMask) switch { 6 => 5, 5 => 4, 4 => 3, 3 => 2, 2 => 1, _ => 0 };
    static int Rank(MethodAttributes a) => (int)(a & MethodAttributes.MemberAccessMask) switch { 6 => 5, 5 => 4, 4 => 3, 3 => 2, 2 => 1, _ => 0 };

    // HSA re-parents game classes onto its own bases, adds interfaces, and makes
    // private members it uses public / non-readonly. Mirror that on M.
    void FixExistingTypes()
    {
        int bases = 0, ifaces = 0, vis = 0, ro = 0;
        foreach (var (wt, mt) in typeMap.ToList())
        {
            if (pullTypes.Contains(TopLevelWOnly(wt)) && wOnlyTypes.Contains(wt)) continue; // copied wholesale
            if (wt.BaseType != null && mt.BaseType != null && wt.BaseType.FullName != mt.BaseType.FullName)
            {
                if (IsHsaType(wt.BaseType)) { mt.BaseType = T(wt.BaseType); bases++; }
                else log.WriteLine($"  base differs (version/platform, kept M): {mt.FullName}: W {wt.BaseType.FullName} M {mt.BaseType.FullName}");
            }
            foreach (var i in wt.Interfaces)
                if (!mt.Interfaces.Any(x => x.InterfaceType.FullName == i.InterfaceType.FullName) && IsHsaType(i.InterfaceType))
                { mt.Interfaces.Add(new InterfaceImplementation(T(i.InterfaceType))); ifaces++; }
            // HSA made some game classes abstract (e.g. DialogBase gets abstract
            // AccessibleUI members); a non-abstract class with abstract methods fails to load
            if (wt.IsAbstract && !mt.IsAbstract) { mt.IsAbstract = true; log.WriteLine($"  made abstract like W: {mt.FullName}"); }
            if (!wt.IsSealed && mt.IsSealed) { mt.IsSealed = false; log.WriteLine($"  unsealed like W: {mt.FullName}"); }
            // type visibility
            var wv = wt.Attributes & TypeAttributes.VisibilityMask; var mv = mt.Attributes & TypeAttributes.VisibilityMask;
            if (wv != mv && (wv == TypeAttributes.Public || wv == TypeAttributes.NestedPublic)) { mt.Attributes = (mt.Attributes & ~TypeAttributes.VisibilityMask) | wv; vis++; }
        }
        foreach (var (wf, mf) in fieldMap)
        {
            var na = (FieldAttributes)Union((int)(wf.Attributes & FieldAttributes.FieldAccessMask), (int)(mf.Attributes & FieldAttributes.FieldAccessMask));
            if (na != (mf.Attributes & FieldAttributes.FieldAccessMask)) { mf.Attributes = (mf.Attributes & ~FieldAttributes.FieldAccessMask) | na; vis++; }
            if (mf.IsInitOnly && !wf.IsInitOnly) { mf.IsInitOnly = false; ro++; }
        }
        const MethodAttributes vt = MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
        foreach (var (wm, mm) in methodMap)
            if (wm.IsVirtual && !mm.IsVirtual) { mm.Attributes = (mm.Attributes & ~vt) | (wm.Attributes & vt); vis++; }
        foreach (var (wm, mm) in methodMap)
        {
            var na = (MethodAttributes)Union((int)(wm.Attributes & MethodAttributes.MemberAccessMask), (int)(mm.Attributes & MethodAttributes.MemberAccessMask));
            if (na != (mm.Attributes & MethodAttributes.MemberAccessMask)) { mm.Attributes = (mm.Attributes & ~MethodAttributes.MemberAccessMask) | na; vis++; }
        }
        foreach (var t in Analyze.AllTypes(M).Where(t => !t.IsAbstract && !t.IsInterface && t.Methods.Any(x => x.IsAbstract)))
            problems.Add($"non-abstract type with abstract methods: {t.FullName}");
        log.WriteLine($"existing types: {bases} base types re-parented, {ifaces} interfaces added, {vis} visibilities widened, {ro} readonly removed");
    }

    PropertyDefinition CopyProp(PropertyDefinition p)
    {
        var np = new PropertyDefinition(p.Name, p.Attributes, T(p.PropertyType));
        if (p.GetMethod != null && methodMap.TryGetValue(p.GetMethod, out var g)) np.GetMethod = g;
        if (p.SetMethod != null && methodMap.TryGetValue(p.SetMethod, out var st)) np.SetMethod = st;
        foreach (var a in p.CustomAttributes) np.CustomAttributes.Add(CA(a));
        return np;
    }

    void FillDecl(TypeDefinition t)
    {
        var mt = typeMap[t];
        if (t.BaseType != null) mt.BaseType = T(t.BaseType);
        foreach (var i in t.Interfaces) mt.Interfaces.Add(new InterfaceImplementation(T(i.InterfaceType)));
        for (int k = 0; k < t.GenericParameters.Count; k++)
            foreach (var c in t.GenericParameters[k].Constraints) mt.GenericParameters[k].Constraints.Add(new GenericParameterConstraint(T(c.ConstraintType)));
        foreach (var f in t.Fields) FillField(f, fieldMap[f]);
        foreach (var m in t.Methods) FillSig(m, methodMap[m]);
        foreach (var n in t.NestedTypes) FillDecl(n);
    }

    void FillType(TypeDefinition t)
    {
        var mt = typeMap[t];
        foreach (var a in t.CustomAttributes) mt.CustomAttributes.Add(CA(a));
        foreach (var f in t.Fields) foreach (var a in f.CustomAttributes) fieldMap[f].CustomAttributes.Add(CA(a));
        foreach (var m in t.Methods) FillSigAttrs(m, methodMap[m]);
        foreach (var m in t.Methods) FillBody(m, methodMap[m]);
        foreach (var p in t.Properties) mt.Properties.Add(CopyProp(p));
        foreach (var e in t.Events)
        {
            var ne = new EventDefinition(e.Name, e.Attributes, T(e.EventType));
            if (e.AddMethod != null) ne.AddMethod = methodMap[e.AddMethod];
            if (e.RemoveMethod != null) ne.RemoveMethod = methodMap[e.RemoveMethod];
            if (e.InvokeMethod != null) ne.InvokeMethod = methodMap[e.InvokeMethod];
            mt.Events.Add(ne);
        }
        foreach (var n in t.NestedTypes) FillType(n);
    }

    void FillField(FieldDefinition f, FieldDefinition nf)
    {
        nf.FieldType = T(f.FieldType);
        if (f.HasConstant) nf.Constant = f.Constant;
        if (f.InitialValue is { Length: > 0 }) nf.InitialValue = f.InitialValue;
        if (f.HasLayoutInfo) nf.Offset = f.Offset;
    }

    void FillSig(MethodDefinition m, MethodDefinition nm)
    {
        for (int k = 0; k < m.GenericParameters.Count; k++)
            foreach (var c in m.GenericParameters[k].Constraints) nm.GenericParameters[k].Constraints.Add(new GenericParameterConstraint(T(c.ConstraintType)));
        nm.ReturnType = T(m.ReturnType);
        foreach (var p in m.Parameters)
        {
            var np = new ParameterDefinition(p.Name, p.Attributes, T(p.ParameterType));
            if (p.HasConstant) np.Constant = p.Constant;
            nm.Parameters.Add(np);
        }
        if (m.HasPInvokeInfo)
        {
            var mr = M.ModuleReferences.FirstOrDefault(x => x.Name == m.PInvokeInfo.Module.Name);
            if (mr == null) { mr = new ModuleReference(m.PInvokeInfo.Module.Name); M.ModuleReferences.Add(mr); }
            nm.PInvokeInfo = new PInvokeInfo(m.PInvokeInfo.Attributes, m.PInvokeInfo.EntryPoint, mr);
        }
    }

    // Methods where W's body would drop Mac-only code: keep M's body and insert
    // only HSA's straight-line addition(s), anchored after the same instruction.
    static readonly HashSet<string> mergeInsert = new()
    {
        "System.Void Hearthstone.HearthstoneApplication::PreInitialization()", // M has OSXWindowManagement.DisableTabBar
    };

    static string Key(Instruction i) => i.OpCode.Name + " " + (i.Operand is MemberReference r ? r.FullName : i.Operand is Instruction ? "" : i.Operand?.ToString());

    bool TryMergeInsert(MethodDefinition wm, MethodDefinition mm)
    {
        var wi = wm.Body.Instructions; var mi = mm.Body.Instructions;
        // stack depth before each W instruction, straight-line
        var depth = new int[wi.Count + 1];
        for (int k = 0; k < wi.Count; k++)
        {
            var i = wi[k];
            int pop = i.OpCode.Code switch
            {
                Code.Call or Code.Callvirt => ((MethodReference)i.Operand).Parameters.Count + (((MethodReference)i.Operand).HasThis ? 1 : 0),
                Code.Newobj => ((MethodReference)i.Operand).Parameters.Count,
                _ => i.OpCode.StackBehaviourPop switch { StackBehaviour.Pop0 => 0, StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1, StackBehaviour.Varpop => 0, _ => 2 }
            };
            int push = i.OpCode.Code switch
            {
                Code.Call or Code.Callvirt => ((MethodReference)i.Operand).ReturnType.MetadataType == MetadataType.Void ? 0 : 1,
                Code.Newobj => 1,
                _ => i.OpCode.StackBehaviourPush switch { StackBehaviour.Push0 => 0, StackBehaviour.Push1_push1 => 2, StackBehaviour.Varpush => 0, _ => 1 }
            };
            depth[k + 1] = depth[k] - pop + push;
        }
        bool any = false;
        for (int h = 0; h < wi.Count; h++)
        {
            if (!(wi[h].Operand is MemberReference r && WDefs(r).Any(InH))) continue;
            int st = h; while (st > 0 && depth[st] != 0) st--;
            int en = h; while (en < wi.Count && depth[en + 1] != 0) en++;
            if (st == 0 || en >= wi.Count) { log.WriteLine($"  merge: no stack-neutral block in {wm.FullName}"); return false; }
            var block = wi.Skip(st).Take(en - st + 1).ToList();
            if (block.Any(x => x.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)
                || wi.Any(x => x.Operand is Instruction t && block.Contains(t)))
            { log.WriteLine($"  merge: block has control flow in {wm.FullName}"); return false; }
            var anchorKey = Key(wi[st - 1]);
            var anchors = mi.Where(x => Key(x) == anchorKey).ToList();
            if (anchors.Count != 1) { log.WriteLine($"  merge: anchor '{anchorKey}' found {anchors.Count}x in M {wm.FullName}"); return false; }
            var il = mm.Body.GetILProcessor();
            var after = anchors[0];
            foreach (var x in block)
            {
                var n = Instruction.Create(OpCodes.Nop); n.OpCode = x.OpCode;
                n.Operand = x.Operand switch
                {
                    TypeReference tr => T(tr), FieldReference fr => F(fr), MethodReference mr => Me(mr),
                    ParameterDefinition p => p.Index < 0 ? mm.Body.ThisParameter : mm.Parameters[p.Index],
                    VariableDefinition => throw new InvalidOperationException("locals in merge block"),
                    _ => x.Operand
                };
                il.InsertAfter(after, n); after = n;
            }
            log.WriteLine($"  merge: inserted {block.Count} HSA instructions into Mac {wm.FullName} after {anchorKey}");
            any = true;
            h = en;
        }
        return any;
    }

    void FillSigAttrs(MethodDefinition m, MethodDefinition nm)
    {
        for (int k = 0; k < m.Parameters.Count; k++)
            foreach (var a in m.Parameters[k].CustomAttributes) nm.Parameters[k].CustomAttributes.Add(CA(a));
        foreach (var a in m.CustomAttributes) nm.CustomAttributes.Add(CA(a));
        foreach (var o in m.Overrides) nm.Overrides.Add(Me(o));
    }

    void FillBody(MethodDefinition m, MethodDefinition nm)
    {
        if (!m.HasBody) return;
        var b = nm.Body;
        b.Instructions.Clear(); b.Variables.Clear(); b.ExceptionHandlers.Clear();
        b.InitLocals = m.Body.InitLocals; b.MaxStackSize = m.Body.MaxStackSize;
        foreach (var v in m.Body.Variables) b.Variables.Add(new VariableDefinition(T(v.VariableType)));
        var map = new Dictionary<Instruction, Instruction>();
        foreach (var i in m.Body.Instructions)
        {
            var n = Instruction.Create(OpCodes.Nop);
            n.OpCode = i.OpCode;
            n.Operand = i.Operand switch
            {
                TypeReference tr => T(tr),
                FieldReference fr => F(fr),
                MethodReference mr => Me(mr),
                VariableDefinition v => b.Variables[v.Index],
                ParameterDefinition p => p.Index < 0 ? nm.Body.ThisParameter : nm.Parameters[p.Index],
                CallSite cs => cs,
                _ => i.Operand
            };
            map[i] = n; b.Instructions.Add(n);
        }
        foreach (var n in b.Instructions)
        {
            if (n.Operand is Instruction t) n.Operand = map[t];
            else if (n.Operand is Instruction[] ts) n.Operand = ts.Select(x => map[x]).ToArray();
        }
        foreach (var h in m.Body.ExceptionHandlers)
            b.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType)
            {
                TryStart = map[h.TryStart], TryEnd = h.TryEnd == null ? null : map[h.TryEnd],
                HandlerStart = map[h.HandlerStart], HandlerEnd = h.HandlerEnd == null ? null : map[h.HandlerEnd],
                FilterStart = h.FilterStart == null ? null : map[h.FilterStart],
                CatchType = h.CatchType == null ? null : T(h.CatchType)
            });
    }

    public HashSet<string> PulledGetterNames(string type) =>
        pullMethods.Where(m => m.DeclaringType.FullName == type && m.Name.StartsWith("get_")).Select(m => m.Name.Substring(4)).ToHashSet();
    public IEnumerable<MethodDefinition> SelectedW() =>
        edited.Concat(pullMethods).Concat(pullTypes.SelectMany(AllNested).SelectMany(t => t.Methods));
    public IEnumerable<MethodDefinition> Touched() =>
        edited.Select(m => methodMap[m]).Concat(pullMethods.Select(m => methodMap[m]))
              .Concat(pullTypes.SelectMany(t => AllNested(typeMap[t])).SelectMany(t => t.Methods));
    public IEnumerable<TypeDefinition> TouchedTypes() => pullTypes.SelectMany(t => AllNested(typeMap[t]));
}
