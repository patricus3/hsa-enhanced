using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Wires HSAEnhanced.dll (enhanced/) into the ported Assembly-CSharp:
//   expose: makes HSA's Accessibility namespace public so HSAEnhanced can be compiled against it
//   hook:   inserts calls into HSAEnhanced.Hooks at the end of a few methods
static class Enhance
{
    static DefaultAssemblyResolver Resolver(params string[] dirs)
    {
        var res = new DefaultAssemblyResolver();
        foreach (var d in dirs) res.AddSearchDirectory(Path.GetFullPath(d));
        return res;
    }

    // write next to the target, then swap, so a failure never leaves a truncated assembly
    static void Save(ModuleDefinition m, string path)
    {
        var tmp = path + ".tmp";
        m.Write(tmp);
        File.Move(tmp, path, true);
    }

    public static void Expose(string asmPath, string refDir)
    {
        var M = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { InMemory = true, AssemblyResolver = Resolver(Path.GetDirectoryName(Path.GetFullPath(asmPath))!, refDir) });
        int types = 0, members = 0;
        // virtual methods keep their visibility: an override may not be less visible than its base
        static bool Widen(MethodDefinition m) => (m.IsAssembly || m.IsFamilyOrAssembly) && !m.IsVirtual;
        foreach (var t in Analyze.AllTypes(M))
        {
            if (Port.IsCompilerGenerated(t)) continue;
            bool hsa = Ns(t) == "Accessibility" || Ns(t) == "" && (t.Name == "LocalizedText" || t.Name == "LocalizationUtils" || t.Name == "LocalizationKey");
            if (hsa)
            {
                if (!t.IsNested && t.IsNotPublic) { t.IsPublic = true; types++; }
                else if (t.IsNested && (t.IsNestedAssembly || t.IsNestedFamilyOrAssembly)) { t.IsNestedPublic = true; types++; }
                foreach (var f in t.Fields) if (f.IsAssembly || f.IsFamilyOrAssembly) { f.IsPublic = true; members++; }
            }
            // internal methods everywhere (HSA widens game methods such as GameModeDisplay.SelectMode to internal);
            // fields of game types stay as they are, Unity serializes public fields
            foreach (var m in t.Methods) if (Widen(m)) { m.IsPublic = true; members++; }
        }
        Save(M, asmPath);
        Console.WriteLine($"exposed {types} HSA types and {members} members");
    }

    static string Ns(TypeDefinition t) { while (t.DeclaringType != null) t = t.DeclaringType; return t.Namespace; }

    // (type, method, IL emitted before each ret; gets the method and the hook to call)
    // SeesReturn: the method returns a value and the emitted code leaves it on the stack (dup + hook)
    record Site(string Type, string Method, string HookName, Action<ILProcessor, MethodDefinition, MethodReference, List<Instruction>> Emit, bool AtStart = false, string? Params = null, bool SeesReturn = false);

    static FieldDefinition F(MethodDefinition m, string name) =>
        m.DeclaringType.Fields.FirstOrDefault(f => f.Name == name) ?? throw new Exception($"{m.DeclaringType.FullName} has no field {name}");

    static readonly Site[] Sites =
    {
        // hub main menu: HSA's hardcoded options + every other button the game shows;
        // gets the Box fields each of HSA's hub methods presses, read from its IL here
        new("Accessibility.AccessibleHub", "SetupMainMenu", "AfterHubMenu", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Ldfld, F(m, "m_mainMenu")));
            o.Add(il.Create(OpCodes.Ldstr, FieldsPressed(m.DeclaringType, "Box")));
            o.Add(il.Create(OpCodes.Call, hook));
        }),
        // game modes menu: rebuilt from the game's own list of game modes
        new("Accessibility.AccessibleGameModeScene", "SetupMainMenu", "GameModeMenu", (il, m, hook, o) =>
        {
            var menu = F(m, "m_mainMenu");
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Ldarg_0));
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Ldfld, F(m, "m_gameModeSceneDataModel")));
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Ldfld, menu));
            o.Add(il.Create(OpCodes.Call, hook));
            o.Add(il.Create(OpCodes.Castclass, menu.FieldType));
            o.Add(il.Create(OpCodes.Stfld, menu));
        }),
        // HSA started: fallback menus for screens and popups HSA does not know
        // (with the tooltip title each button class shows, read from the game's code)
        new("Accessibility.AccessibilityMgr", "Initialize", "OnAccessibilityInitialized", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Ldstr, TooltipTable(m.Module)));
            o.Add(il.Create(OpCodes.Ldstr, HsaMenus ? "hsa" : "enhanced"));
            o.Add(il.Create(OpCodes.Call, hook));
        }),
        // every HSA menu, when it starts reading: add what the game shows, and a way back
        new("Accessibility.AccessibleMenu", "StartReading", "BeforeMenuRead", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
        }, AtStart: true),
        new("Accessibility.AccessibleHorizontalMenu`1", "StartReading", "BeforeHorizontalMenuRead", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
        }, AtStart: true),
        // adventure chooser: every adventure and mode the game shows (HSA has a fixed list)
        new("Accessibility.AccessibleAdventureScene", "SetupAndReadChooseAdventureMenu", "ChooseAdventureMenu", (il, m, hook, o) =>
        {
            var original = m.Body.Instructions[0];
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
            o.Add(il.Create(OpCodes.Brfalse, original));
            o.Add(il.Create(OpCodes.Ret));
        }, AtStart: true),
        // the Play screen (ranked / casual, friendly challenge): laid out as the game shows it
        new("DeckPickerTrayDisplay", "RankedOnDeckPickerTrayDisplayReady", "PlayScreenReady", (il, m, hook, o) =>
        {
            var original = m.Body.Instructions[0];
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
            o.Add(il.Create(OpCodes.Brfalse, original));
            o.Add(il.Create(OpCodes.Ret));
        }, AtStart: true),
        new("DeckPickerTrayDisplay", "FriendlyOnDeckPickerTrayDisplayReady", "PlayScreenReady", (il, m, hook, o) =>
        {
            var original = m.Body.Instructions[0];
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
            o.Add(il.Create(OpCodes.Brfalse, original));
            o.Add(il.Create(OpCodes.Ret));
        }, AtStart: true),
        // adventures and practice: the deck tray and the opponent tray, the same way
        new("Accessibility.AccessibleAdventureScene", "OnDeckPickerTrayDisplayReady", "AdventureDeckTrayReady", (il, m, hook, o) =>
        {
            var original = m.Body.Instructions[0];
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
            o.Add(il.Create(OpCodes.Brfalse, original));
            o.Add(il.Create(OpCodes.Ret));
        }, AtStart: true),
        new("Accessibility.AccessibleAdventureScene", "OnPracticePickerTrayDisplayShown", "OpponentTrayShown", (il, m, hook, o) =>
        {
            var original = m.Body.Instructions[0];
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
            o.Add(il.Create(OpCodes.Brfalse, original));
            o.Add(il.Create(OpCodes.Ret));
        }, AtStart: true),
        // the game's format picker popup, read from its own buttons
        new("Accessibility.AccessibleFormatTypePickerPopup", "ReadPopup", "FormatPickerOpened", (il, m, hook, o) =>
        {
            var original = m.Body.Instructions[0];
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
            o.Add(il.Create(OpCodes.Brfalse, original));
            o.Add(il.Create(OpCodes.Ret));
        }, AtStart: true),
        // any screen: a second "find game" while one is under way is dropped (HSA screens can
        // press Play twice with one key, and Battle.net rejects the second request)
        new("GameMgr", "FindGameInternal", "SkipDuplicateFindGame", (il, m, hook, o) =>
        {
            var original = m.Body.Instructions[0];
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
            o.Add(il.Create(OpCodes.Brfalse, original));
            o.Add(il.Create(OpCodes.Ret));
        }, AtStart: true),
        // a match: a hand card is clicked only once the pointer is on it (while the hand moves after a
        // draw or discover HSA's click could land on the card next to it and play that one)
        new("Accessibility.AccessibleGameplay", "ClickCard", "BeforeClickCard", (il, m, hook, o) =>
        {
            var original = m.Body.Instructions[0];
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Ldarg_1)); o.Add(il.Create(OpCodes.Call, hook));
            o.Add(il.Create(OpCodes.Brfalse, original));
            o.Add(il.Create(OpCodes.Ret));
        }, AtStart: true, Params: "System.Boolean"),
        // what HSA says, and when it gives a screen focus: a screen that stays silent when it
        // gets focus handles nothing, and the fallback menu takes it over
        new("Accessibility.AccessibilityMgr", "Output", "OnSpeech", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
        }, AtStart: true, Params: "Accessibility.AccessibleComponent,System.String,System.Boolean"),
        // adventure missions: the game's own locks count (HSA's mission list offers locked
        // missions, and the server refuses to start them)
        new("AdventureMissionDisplay", "selectWing", "BeforeSelectMission", (il, m, hook, o) =>
        {
            var original = m.Body.Instructions[0];
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Ldarg_1)); o.Add(il.Create(OpCodes.Call, hook));
            o.Add(il.Create(OpCodes.Brfalse, original));
            o.Add(il.Create(OpCodes.Ret));
        }, AtStart: true),
        new("Accessibility.AccessibilityMgr", "NotifyScreenFocused", "OnScreenFocused", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Call, hook));
        }, AtStart: true),
        // Black Market: what the game asks the server and what comes back (read-only; the reply is
        // only looked at after the game itself has taken it)
        new("Hearthstone.BlackMarket.BlackMarketEventManager", "RequestBlackMarketPlayerState", "OnBlackMarketRequest", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Call, hook));
        }, AtStart: true),
        new("Hearthstone.BlackMarket.BlackMarketEventManager", "HandlePlayerStateRequestTimeout", "OnBlackMarketTimeout", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Call, hook));
        }, AtStart: true),
        new("Network", "GetBlackMarketPlayerStateResponse", "OnBlackMarketResponse", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Dup)); o.Add(il.Create(OpCodes.Call, hook));
        }, SeesReturn: true),
        // Lucky draw scene (Darkmoon Faire Treasures, ...)
        new("LuckyDrawDisplay", "Start", "OnLuckyDrawDisplayStart", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
        }),
        // Black Market scene
        new("BlackMarketDisplay", "Start", "OnBlackMarketDisplayStart", (il, m, hook, o) =>
        {
            o.Add(il.Create(OpCodes.Ldarg_0)); o.Add(il.Create(OpCodes.Call, hook));
        }),
    };

    // Windows: HSA's speech calls (DavyKager.Tolk in TolkDotNet) go to HSAPrism.Speech
    // (windows/speech), which talks to Prism directly; no Tolk library is left
    public static void RetargetSpeech(string asmPath, string refDir)
    {
        var M = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { InMemory = true, AssemblyResolver = Resolver(Path.GetDirectoryName(Path.GetFullPath(asmPath))!, refDir) });
        var tolk = M.AssemblyReferences.FirstOrDefault(a => a.Name == "TolkDotNet");
        if (tolk == null) { Console.WriteLine("speech: no Tolk reference"); return; }
        var prism = new AssemblyNameReference("HSAPrism", new Version(0, 0, 0, 0));
        int n = 0;
        foreach (var t in M.GetTypeReferences().Where(t => t.Scope == tolk).ToList())
        {
            if (t.FullName != "DavyKager.Tolk") throw new Exception("unexpected Tolk type " + t.FullName);
            t.Scope = prism; t.Namespace = "HSAPrism"; t.Name = "Speech"; n++;
        }
        M.AssemblyReferences.Remove(tolk);
        M.AssemblyReferences.Add(prism);
        Save(M, asmPath);
        Console.WriteLine($"speech: {n} Tolk type reference(s) now HSAPrism.Speech (Prism)");
    }

    // "Type=KEY|..." for each game class whose code shows exactly one tooltip headline
    // (GLUE_TOOLTIP_BUTTON_*_HEADLINE): icon buttons with no text get their name from it
    static string TooltipTable(ModuleDefinition module)
    {
        var rx = new System.Text.RegularExpressions.Regex("^GLUE_TOOLTIP_BUTTON_[A-Z0-9_]+_HEADLINE$");
        var keys = new Dictionary<string, HashSet<string>>();
        foreach (var t in Analyze.AllTypes(module))
        {
            var top = t; while (top.DeclaringType != null) top = top.DeclaringType;
            foreach (var me in t.Methods.Where(x => x.HasBody))
                foreach (var i in me.Body.Instructions)
                    // "..._UNAVAILABLE_HEADLINE" is a state, not a name
                    if (i.OpCode == OpCodes.Ldstr && i.Operand is string s && rx.IsMatch(s) && !s.Contains("_UNAVAILABLE_"))
                    {
                        if (!keys.TryGetValue(top.FullName, out var set)) keys[top.FullName] = set = new HashSet<string>();
                        set.Add(s);
                    }
        }
        var table = keys.Where(kv => kv.Value.Count == 1).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value.First());
        return string.Join("|", table);
    }

    // "Method=field+field|..." for each method of `t` (lambdas included): the fields of `owner` it
    // reads itself or through the methods of `t` it calls or hands on as delegates. An HSA menu
    // option runs one of these methods; the field is the game's button it stands for.
    static string FieldsPressed(TypeDefinition t, string owner)
    {
        var methods = new[] { t }.Concat(Analyze.AllTypes(t.Module).Where(x => x.DeclaringType != null && IsInside(x, t)))
            .SelectMany(x => x.Methods).Where(x => x.HasBody).ToList();
        var mine = new HashSet<MethodDefinition>(methods);
        var direct = new Dictionary<MethodDefinition, List<string>>();
        var calls = new Dictionary<MethodDefinition, List<MethodDefinition>>();
        foreach (var me in methods)
        {
            direct[me] = new List<string>(); calls[me] = new List<MethodDefinition>();
            foreach (var i in me.Body.Instructions)
            {
                if ((i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Ldflda) && i.Operand is FieldReference f && f.DeclaringType.FullName == owner && !direct[me].Contains(f.Name))
                    direct[me].Add(f.Name);
                if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Ldftn) && i.Operand is MethodReference r)
                {
                    MethodDefinition d = null;
                    try { d = r.Resolve(); } catch { }
                    if (d != null && mine.Contains(d)) calls[me].Add(d);
                }
            }
        }
        var entries = new List<string>();
        foreach (var me in methods)
        {
            if (me.Name == ".ctor" || me.Name == ".cctor") continue;
            var fields = new List<string>();
            var seen = new HashSet<MethodDefinition>();
            var todo = new Queue<MethodDefinition>(); todo.Enqueue(me);
            while (todo.Count > 0)
            {
                var x = todo.Dequeue();
                if (!seen.Add(x)) continue;
                foreach (var f in direct[x]) if (!fields.Contains(f)) fields.Add(f);
                foreach (var c in calls[x]) todo.Enqueue(c);
            }
            if (fields.Count > 0) entries.Add(me.Name + "=" + string.Join("+", fields));
        }
        return string.Join("|", entries.Distinct());
    }

    static bool IsInside(TypeDefinition x, TypeDefinition outer)
    {
        for (var d = x.DeclaringType; d != null; d = d.DeclaringType) if (d == outer) return true;
        return false;
    }

    // --hsa-menus: the installer's --use-hsa-menus (our menu system off)
    static bool HsaMenus;

    public static bool Hook(string asmPath, string enhancedPath, string outPath, string refDir, bool hsaMenus = false)
    {
        HsaMenus = hsaMenus;
        var res = Resolver(Path.GetDirectoryName(Path.GetFullPath(asmPath))!, Path.GetDirectoryName(Path.GetFullPath(enhancedPath))!, refDir);
        var M = ModuleDefinition.ReadModule(asmPath, new ReaderParameters { AssemblyResolver = res, InMemory = true });
        var E = ModuleDefinition.ReadModule(enhancedPath, new ReaderParameters { AssemblyResolver = res });
        var hooks = E.GetType("HSAEnhanced.Hooks") ?? throw new Exception("HSAEnhanced.Hooks not found");
        int done = 0;
        foreach (var s in Sites)
        {
            var type = M.GetType(s.Type);
            var target = type?.Methods.FirstOrDefault(x => x.Name == s.Method && x.HasBody
                && (s.Params == null || string.Join(",", x.Parameters.Select(p => p.ParameterType.FullName)) == s.Params));
            var hookDef = hooks.Methods.FirstOrDefault(x => x.Name == s.HookName);
            if (target == null || hookDef == null) { Console.WriteLine($"HOOK WARNING: {s.Type}::{s.Method} or hook {s.HookName} missing, skipped"); continue; }
            if (target.ReturnType.MetadataType != MetadataType.Void && !s.AtStart && !s.SeesReturn) throw new Exception($"{target.FullName} is not void");
            var hook = M.ImportReference(hookDef);
            target.Body.SimplifyMacros(); // long branches while inserting code
            var il = target.Body.GetILProcessor();
            if (s.AtStart)
            {
                var seq = new List<Instruction>();
                s.Emit(il, target, hook, seq);
                var first = target.Body.Instructions[0];
                foreach (var ins in seq) il.InsertBefore(first, ins);
            }
            else foreach (var ret in target.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
            {
                var seq = new List<Instruction>();
                s.Emit(il, target, hook, seq);
                // turn the ret itself into the first hook instruction, so branches to it run the hook
                ret.OpCode = seq[0].OpCode; ret.Operand = seq[0].Operand;
                var prev = ret;
                foreach (var ins in seq.Skip(1).Append(il.Create(OpCodes.Ret))) { il.InsertAfter(prev, ins); prev = ins; }
            }
            target.Body.OptimizeMacros();
            Console.WriteLine($"hooked {target.FullName} -> {s.HookName}");
            done++;
        }
        Save(M, outPath);
        Console.WriteLine($"hooks: {done} of {Sites.Length} installed");
        return true;
    }
}
