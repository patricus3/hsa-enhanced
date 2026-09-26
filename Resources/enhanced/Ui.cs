using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Accessibility;
using Hearthstone.UI;
using UnityEngine;

namespace HSAEnhanced
{
    static class Str
    {
        // Text from the game's string tables (ACCESSIBILITY_ENHANCED.txt is appended to
        // every locale's ACCESSIBILITY.txt); English when a key is missing.
        internal static string T(string key, string english, params object[] args)
        {
            try
            {
                if (GameStrings.HasKey(key)) return args.Length == 0 ? GameStrings.Get(key) : GameStrings.Format(key, args);
            }
            catch (Exception e) { Log.Error(e); }
            return args.Length == 0 ? english : string.Format(english, args);
        }

        // A game string, or null when the table lacks it
        internal static string Game(string key, params object[] args)
        {
            if (!GameStrings.HasKey(key)) return null;
            return Clean(args.Length == 0 ? GameStrings.Get(key) : GameStrings.Format(key, args));
        }

        static readonly Regex Placeholder = new Regex(@"<PH>\s*");

        // Spoken form of on-screen text: markup and the "<PH>" placeholder marker removed
        internal static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = Placeholder.Replace(text, "");
            text = AccessibilityUtils.CurateText(text);
            return text.TrimEnd('.', ':', ' ');
        }

        internal static string Join(params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(p);
            }
            return sb.ToString();
        }

        // m_blackMarketButtonController -> "Black market"
        internal static string Humanize(string fieldName)
        {
            var n = fieldName;
            if (n.StartsWith("m_")) n = n.Substring(2);
            foreach (var suffix in new[] { "ButtonController", "ButtonWidget", "Button", "Ribbon", "Widget" })
                if (n.EndsWith(suffix) && n.Length > suffix.Length) { n = n.Substring(0, n.Length - suffix.Length); break; }
            n = Regex.Replace(n, "([a-z0-9])([A-Z])", "$1 $2");
            n = n.ToLowerInvariant().Trim();
            return n.Length == 0 ? "" : char.ToUpperInvariant(n[0]) + n.Substring(1);
        }
    }

    // A button the game shows, found by walking the game's own objects rather than a list
    class GameButton
    {
        internal Component Target;   // what gets clicked
        internal string Label;
        internal Action Click;
        internal GameObject Object { get { return Target.gameObject; } }

        // "label [Type at path]" for each button, for the log
        internal static string Describe(List<GameButton> buttons)
        {
            var parts = new List<string>();
            foreach (var b in buttons) parts.Add(b.Label + " [" + b.Target.GetType().Name + " at " + Path(b.Target.transform) + "]");
            return parts.Count == 0 ? "nothing" : string.Join(" | ", parts.ToArray());
        }

        static string Path(Transform t)
        {
            var names = new List<string>();
            for (int i = 0; t != null && i < 4; t = t.parent, i++) names.Insert(0, t.name);
            return string.Join("/", names.ToArray());
        }
    }

    static class Ui
    {
        // Is this element on screen and usable right now?
        internal static bool IsShown(Component c) { return WhyHidden(c) == null; }

        // null when shown, else the reason (for the diagnostics in the log)
        internal static string WhyHidden(Component c)
        {
            if (c == null || !c.gameObject.activeInHierarchy) return "inactive";
            var b = c as Behaviour;
            if (b != null && !b.enabled) return "component off";
            var peg = c as PegUIElement;
            if (peg != null && !peg.IsEnabled()) return "disabled";
            var widget = c as Widget;
            if (widget != null && !widget.IsActive) return "widget inactive";
            var col = c.GetComponent<Collider>();
            if (col != null && !col.enabled) return "collider off";
            return StateFlagsAllow(c) ? null : "hidden by its state";
        }

        // "locked" when the element or what holds it is marked locked (m_Locked, m_isLocked, ...),
        // "unavailable" when the game has the button disabled, else null
        static readonly Regex LockedField = new Regex("^m_(is)?locked$", RegexOptions.IgnoreCase);

        internal static string StateOf(Component c)
        {
            if (c == null || !c) return null;
            var coin = c as AdventureBossCoin;
            if (coin != null) return Missions.State(coin);
            // the button's own lock flag only (flags of what surrounds it belong to other things)
            for (var type = c.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(bool) && LockedField.IsMatch(f.Name) && (bool)f.GetValue(c))
                        return Str.T("ACCESSIBILITY_ENH_LOCKED", "locked");
            var peg = c as PegUIElement;
            if (peg != null && !peg.IsEnabled()) return Str.T("ACCESSIBILITY_ENH_UNAVAILABLE", "unavailable");
            return null;
        }

        // Drawn by some camera this frame (buttons of screens out of view still exist)
        internal static bool IsVisible(Component c)
        {
            bool any = false;
            foreach (var r in c.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled) continue;
                any = true;
                if (r.isVisible) return true;
            }
            return !any;
        }

        // Button classes that hide themselves through widget states keep the truth in flags
        // such as m_buttonEnabledAndVisible / m_isEnabled / m_boxAllowsVisibility.
        internal static bool StateFlagsAllow(object o)
        {
            if (o == null) return true;
            for (var t = o.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType != typeof(bool)) continue;
                    var n = f.Name;
                    if (n.IndexOf("EnabledAndVisible", StringComparison.OrdinalIgnoreCase) >= 0 || n == "m_isEnabled" || n == "m_boxAllowsVisibility")
                        if (!(bool)f.GetValue(o)) return false;
                }
            return true;
        }

        // Turns a field value (button, widget, controller, game object) into the clickable component
        internal static Component Resolve(object v)
        {
            if (v == null) return null;
            if (v is GameObject)
            {
                var go = (GameObject)v;
                return (Component)go.GetComponent<PegUIElement>() ?? go.GetComponent<Widget>();
            }
            if (v is PegUIElement || v is Clickable) return (Component)v;
            if (v is Widget)
            {
                // a widget that is one button: that button (it knows its label and its event)
                var w = (Widget)v;
                var pegs = w.GetComponentsInChildren<PegUIElement>(false);
                if (pegs.Length == 1) return pegs[0];
                var clicks = w.GetComponentsInChildren<Clickable>(false);
                if (clicks.Length == 1) return clicks[0];
                // nothing to press in it (e.g. the box's event decoration): not a button
                if (pegs.Length == 0 && clicks.Length == 0) return null;
                return w;
            }
            var c = v as Component;
            if (c == null) return null;
            // controllers such as BlackMarketButtonController / LuckyDrawButtonController
            var getButton = Ref.Method(c.GetType(), "GetButton", 0);
            if (getButton != null && typeof(Component).IsAssignableFrom(getButton.ReturnType))
            {
                if (!StateFlagsAllow(c)) return null;
                return getButton.Invoke(c, null) as Component;
            }
            return null;
        }

        // What the player sees written on the element: its own text, then (widget buttons) the
        // data bound to its widget, then an icon button's tooltip headline
        internal static string LabelOf(Component c)
        {
            if (c == null) return "";
            var s = OwnText(c);
            if (s.Length > 0) return s;
            var clickable = c as Clickable;
            if (clickable != null) { s = WidgetText(clickable); if (s.Length > 0) return s; }
            return TooltipHeadline(c);
        }

        static string OwnText(Component c)
        {
            // the button's own GetText() first; some throw when they have no text object (UIBButton)
            try
            {
                var getText = Ref.Method(c.GetType(), "GetText", 0);
                if (getText != null && getText.ReturnType == typeof(string))
                {
                    var s = Str.Clean(getText.Invoke(c, null) as string);
                    if (s.Length > 0) return s;
                }
            }
            catch { }
            try
            {
                foreach (var ut in c.GetComponentsInChildren<UberText>(false))
                {
                    var s = ShownText(ut.Text);
                    if (s.Length > 0 && !IsNumber(s)) return s;
                }
                // a button without text in a container that holds only it: the container's text
                var t = c.transform.parent;
                for (int up = 0; t != null && up < 2; up++, t = t.parent)
                {
                    if (t.GetComponentsInChildren<PegUIElement>(false).Length + t.GetComponentsInChildren<Clickable>(false).Length > 1) break;
                    foreach (var ut in t.GetComponentsInChildren<UberText>(false))
                    {
                        var s = ShownText(ut.Text);
                        if (s.Length > 0 && !IsNumber(s)) return s;
                    }
                }
            }
            catch { }
            return "";
        }

        static readonly Regex StringKey = new Regex("^[A-Z][A-Z0-9_]*_[A-Z0-9_]+$");

        // Text as shown; a text still holding its string key (GLOBAL_BACK) is looked up, and a text
        // box's placeholder ("Uber Text", what a new one says before the game fills it) is no text
        internal static string ShownText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var t = text.Trim();
            if (t == "Uber Text" || t == "New Text") return "";
            if (StringKey.IsMatch(t) && GameStrings.HasKey(t)) t = GameStrings.Get(t);
            return Str.Clean(t);
        }

        static readonly Dictionary<string, string> s_tooltips = new Dictionary<string, string>();

        internal static void SetTooltips(string table)
        {
            if (string.IsNullOrEmpty(table)) return;
            foreach (var entry in table.Split('|'))
            {
                var eq = entry.IndexOf('=');
                if (eq > 0) s_tooltips[entry.Substring(0, eq)] = entry.Substring(eq + 1);
            }
        }

        // An icon button's tooltip title: from the table built out of the game's code, or a
        // *TOOLTIP*HEADLINE* string field of its class
        static string TooltipHeadline(Component c)
        {
            try
            {
                for (var t = c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    string key;
                    if (s_tooltips.TryGetValue(t.FullName, out key))
                    {
                        var s = Str.Game(key);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                for (var t = c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (f.FieldType == typeof(string) && f.Name.IndexOf("TOOLTIP", StringComparison.OrdinalIgnoreCase) >= 0 && f.Name.IndexOf("HEADLINE", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var s = Str.Game(f.GetValue(null) as string ?? "");
                            if (!string.IsNullOrEmpty(s)) return s;
                        }
            }
            catch { }
            return "";
        }

        static readonly string[] NameProperties = { "DisplayName", "Name", "Title", "ButtonText", "Text", "Label", "Header", "HeaderText", "ShortName", "BoosterName" };

        // Widget API: the data models bound to the widget this clickable belongs to, when the
        // widget is that one button (a list item, a tile), not a whole page
        static string WidgetText(Clickable c)
        {
            var owner = Ref.Get<WidgetTemplate>(c, "m_owner");
            if (owner == null) return "";
            var behaviors = Ref.Get<IList>(owner, "m_widgetBehaviors");
            int clickables = 0;
            if (behaviors != null) foreach (var b in behaviors) if (b is Clickable) clickables++;
            if (clickables > 1) return "";
            var ctx = Ref.Get<DataContext>(owner, "m_dataContext");
            if (ctx == null) return "";
            foreach (var dm in ctx.GetDataModels())
            {
                var s = DataModelText(dm);
                if (s.Length > 0) return s;
            }
            return "";
        }

        internal static string DataModelText(IDataModel dm)
        {
            if (dm == null) return "";
            foreach (var n in NameProperties)
            {
                var p = dm.GetType().GetProperty(n, BindingFlags.Instance | BindingFlags.Public);
                if (p == null || p.PropertyType != typeof(string)) continue;
                try
                {
                    var s = Str.Clean(p.GetValue(dm, null) as string);
                    if (s.Length > 0) return s;
                }
                catch { }
            }
            return "";
        }

        static bool IsNumber(string s)
        {
            foreach (var ch in s) if (!char.IsDigit(ch) && ch != ' ' && ch != '/' && ch != ',' && ch != '.') return false;
            return true;
        }

        // Widget API press: the Clickable's own input element (GetOrCreatePegUIElement; it may not
        // exist until asked for) is pressed and released, so the Clickable's OnClick / OnRelease run
        // and the widget fires its real events. The virtual mouse only when there is no element.
        internal static void Press(Component c)
        {
            var clickable = c as Clickable;
            if (clickable == null) { ClickOf(c)(); return; }
            PegUIElement peg = null;
            try
            {
                var get = Ref.Method(typeof(Clickable), "GetOrCreatePegUIElement", 0);   // private in the game
                peg = get != null ? get.Invoke(clickable, null) as PegUIElement : Ref.Get<PegUIElement>(clickable, "m_pegUiElement");
            }
            catch (Exception e) { Log.Error(e); }
            if (peg != null && peg) { Log.Info("press (widget) " + clickable.name); AccessibleWidgetUtils.ClickButton(peg); }
            else { Log.Info("press (mouse) " + clickable.name); AccessibleInputMgr.Click(clickable); }
        }

        // Buttons that show no text of their own, named with the game's own words for them
        static string GameNameFor(string field)
        {
            if (field.IndexOf("SetRotation", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string year = null;
                try
                {
                    // SetRotationManager.Get() (or the service), then its year name
                    var get = Ref.Method(typeof(SetRotationManager), "Get", 0);
                    object mgr = get != null ? get.Invoke(null, null) : null;
                    if (mgr == null) { Blizzard.T5.Services.ServiceManager.TryGet<SetRotationManager>(out var service); mgr = service; }
                    if (mgr != null) year = ((SetRotationManager)mgr).GetActiveSetRotationYearLocalizedString();
                }
                catch { }
                return Str.Join(Str.Game("GLOBAL_SET_ROTATION_ROLLOVER_HEADER"), Str.Clean(year));
            }
            // the game's own text for the button, found by its field name in the game's string
            // tables (current language): m_luckyDrawButtonController -> GLUE_TOOLTIP_BUTTON_LUCKY_DRAW_HEADLINE ...
            var core = Regex.Replace(field, "^m_", "");
            core = Regex.Replace(core, "(ButtonController|ButtonWidget|ButtonObject|Button|Widget|Controller|Ribbon|Reference|Ref)$", "");
            if (core.Length == 0) return "";
            var snake = Regex.Replace(core, "([a-z0-9])([A-Z])", "$1_$2").ToUpperInvariant();
            foreach (var key in new[] { "GLUE_TOOLTIP_BUTTON_" + snake + "_HEADLINE", "GLUE_" + snake + "_BUTTON", "GLUE_" + snake + "_PHONE_BUTTON",
                                        "GLOBAL_" + snake, "GLUE_" + snake, "GLUE_" + snake + "_TITLE", "GLOBAL_" + snake + "_TITLE", "GLUE_" + snake + "_HEADER" })
            {
                try { if (GameStrings.HasKey(key)) { var s = Str.Clean(GameStrings.Get(key)); if (s.Length > 0) return s; } } catch { }
            }
            return "";
        }

        // Presses the element the way the game expects it to be pressed
        internal static Action ClickOf(Component c)
        {
            if (c is Clickable) return () => Press(c);
            // widget-driven buttons (BlackMarketButton, ...) react to their BUTTON_CLICKED event
            var handle = Ref.Method(c.GetType(), "HandleEvent", 1);
            var clicked = Ref.Field(c.GetType(), "BUTTON_CLICKED");
            if (handle != null && clicked != null && clicked.FieldType == typeof(string))
            {
                var ev = (string)clicked.GetValue(null);
                return () => handle.Invoke(c, new object[] { ev });
            }
            var peg = c as PegUIElement;
            if (peg != null) return () => AccessibleWidgetUtils.ClickButton(peg);
            var widget = c as Widget;
            if (widget != null)
            {
                // a widget holding a single clickable: press that one
                var inner = widget.GetComponentsInChildren<Clickable>(false);
                if (inner.Length == 1) return () => Press(inner[0]);
                return () => AccessibleWidgetUtils.TriggerButtonClicked(widget);
            }
            return () => AccessibleInputMgr.Click(c);
        }

        // Every button held in the fields of `owner` (Box, RibbonButtonsUI, ...) that is on screen.
        // Fields named in `skip` are left out; nested holders listed in `descend` are walked too.
        internal static List<GameButton> ButtonsIn(object owner, ICollection<string> skip, Func<object, bool> descend)
        {
            var found = new List<GameButton>();
            var seen = new HashSet<GameObject>();
            Collect(owner, skip, descend, found, seen, 0);
            return found;
        }

        static void Collect(object owner, ICollection<string> skip, Func<object, bool> descend, List<GameButton> found, HashSet<GameObject> seen, int depth)
        {
            if (owner == null || depth > 2) return;
            for (var t = owner.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (skip.Contains(f.Name)) continue;
                    object v;
                    try { v = f.GetValue(owner); } catch { continue; }
                    if (v == null || v is string) continue;
                    // a map of name -> button (e.g. an adventure book's chapter buttons): the name labels it
                    if (AddNamedButtons(v, found, seen)) continue;
                    // a list of buttons (e.g. a menu's m_allButtons): each one
                    if (v is IList && !(v is Array && ((Array)v).Rank != 1))
                    {
                        foreach (var item in (IList)v)
                        {
                            var bc = item as Component;
                            if (bc == null || !bc || !(bc is PegUIElement || bc is Clickable) || !IsShown(bc) || !seen.Add(bc.gameObject)) continue;
                            var bl = LabelOf(bc);
                            if (bl.Length > 0) found.Add(new GameButton { Target = bc, Label = bl, Click = ClickOf(bc) });
                        }
                        continue;
                    }
                    if (v is IEnumerable) continue;
                    if (v is UnityEngine.Object && !(UnityEngine.Object)v) continue;
                    if (descend(v)) { Collect(v, skip, descend, found, seen, depth + 1); continue; }
                    Component c;
                    try { c = Resolve(v); } catch { continue; }
                    // a widget is shown or not as a whole; the button inside it gives label and press
                    var shownBy = v is Widget ? (Component)v : c;
                    if (c == null) continue;
                    // disabled but on screen: listed as unavailable (the game answers a press on it, e.g.
                    // the box says why its shop is closed)
                    var why = WhyHidden(shownBy);
                    bool disabled = why == "disabled";
                    if ((why != null && !disabled) || !seen.Add(c.gameObject)) continue;
                    var label = LabelOf(c);
                    if (label.Length == 0) label = GameNameFor(f.Name);
                    if (label.Length == 0) label = Str.Humanize(f.Name);
                    if (label.Length == 0) continue;
                    if (disabled) label = Str.Join(label, Str.T("ACCESSIBILITY_ENH_UNAVAILABLE", "unavailable"));
                    found.Add(new GameButton { Target = c, Label = label, Click = ClickOf(c) });
                }
        }

        // Entries with a string Key and a button Value (Dictionary, the game's Map): true when `v` is one
        static bool AddNamedButtons(object v, List<GameButton> found, HashSet<GameObject> seen)
        {
            var items = v as IEnumerable;
            if (items == null || v is IList) return false;
            bool isMap = false;
            try
            {
                foreach (var item in items)
                {
                    if (item == null) continue;
                    var t = item.GetType();
                    var key = t.GetProperty("Key"); var value = t.GetProperty("Value");
                    if (key == null || value == null) return isMap;
                    isMap = true;
                    var name = key.GetValue(item, null) as string;
                    var c = value.GetValue(item, null) as Component;
                    if (string.IsNullOrEmpty(name) || c == null || !c || !(c is PegUIElement || c is Clickable)) continue;
                    if (!IsShown(c) || !seen.Add(c.gameObject)) continue;
                    var label = Str.Clean(name);
                    if (label.Length > 0) found.Add(new GameButton { Target = c, Label = label, Click = ClickOf(c) });
                }
            }
            catch { }
            return isMap;
        }

        // Buttons under `root` that show a text (widget Clickables first, then the older
        // PegUIElement buttons), except those under `exclude` objects
        internal static List<GameButton> ClickablesUnder(GameObject root, Func<GameObject, bool> exclude)
        {
            if (root == null) return new List<GameButton>();
            return Collect(root.GetComponentsInChildren<Clickable>(false), root.GetComponentsInChildren<PegUIElement>(false), exclude, false);
        }

        // Every button of the screen the player sees: visible to a camera, without the
        // Battle.net bar (HSA has keys for it) and without the box when it is out of view
        internal static List<GameButton> ScreenButtons()
        {
            var bar = BnetBar.Get() == null ? null : BnetBar.Get().gameObject;
            var box = Box.Get() == null || SceneMgr.Get() == null || SceneMgr.Get().GetMode() == SceneMgr.Mode.HUB ? null : Box.Get().gameObject;
            s_leftOut.Clear();
            var found = new List<GameButton>();
            var labels = new List<string>();
            var objects = new HashSet<GameObject>();
            // buttons the shown screen components hold (by name, in lists): a chapter button beats
            // a page-sized click area that happens to show the chapter's title
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (!(mb is AccessibleScreen) || !mb.isActiveAndEnabled) continue;
                foreach (var b in ButtonsIn(mb, new string[0], v => false))
                    if (objects.Add(b.Object) && !Labels.SimilarToAny(labels, b.Label)) { labels.Add(b.Label); found.Add(b); }
            }
            foreach (var b in ClickablesOnScreen(go => go == bar || go == box))
                if (!objects.Contains(b.Object) && !Labels.SimilarToAny(labels, b.Label)) { labels.Add(b.Label); found.Add(b); }
            var why = new List<string>();
            foreach (var kv in s_leftOut) why.Add(kv.Key + " " + kv.Value);
            Log.Once("screen scan (" + (SceneMgr.Get() == null ? "?" : SceneMgr.Get().GetMode().ToString()) + "): " + found.Count + " buttons; left out: " + (why.Count == 0 ? "none" : string.Join(", ", why.ToArray())));
            // reading order: top to bottom, left to right
            found.Sort((a, b) => ScreenOrder(a.Target).CompareTo(ScreenOrder(b.Target)));
            return found;
        }

        internal static float ScreenOrderOf(Component c) { return ScreenOrder(c); }

        static float ScreenOrder(Component c)
        {
            var cam = Camera.main;
            if (cam == null) return 0;
            var p = cam.WorldToScreenPoint(c.transform.position);
            return -Mathf.Round(p.y / 40f) * 100000f + p.x;
        }

        // Every button on screen (visible to a camera), same rules
        internal static List<GameButton> ClickablesOnScreen(Func<GameObject, bool> exclude)
        {
            return Collect(UnityEngine.Object.FindObjectsByType<Clickable>(FindObjectsSortMode.None),
                           UnityEngine.Object.FindObjectsByType<PegUIElement>(FindObjectsSortMode.None), exclude, true);
        }

        static List<GameButton> Collect(IEnumerable<Clickable> clickables, IEnumerable<PegUIElement> pegs, Func<GameObject, bool> exclude, bool visibleOnly)
        {
            var found = new List<GameButton>();
            var seen = new HashSet<GameObject>();
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var widgetPegs = new HashSet<PegUIElement>();
            foreach (var c in clickables)
            {
                var peg = Ref.Get<PegUIElement>(c, "m_pegUiElement");
                if (peg != null) widgetPegs.Add(peg);
                if (!Ref.Get<bool>(c, "m_active")) continue;         // the Clickable's own active state
                Add(c, exclude, visibleOnly, found, seen, labels);
            }
            foreach (var p in pegs)
                if (!widgetPegs.Contains(p)) Add(p, exclude, visibleOnly, found, seen, labels);
            return found;
        }

        // why buttons were left out in the last scan, for the log
        static readonly Dictionary<string, int> s_leftOut = new Dictionary<string, int>();

        static void LeftOut(string why) { int n; s_leftOut.TryGetValue(why, out n); s_leftOut[why] = n + 1; }

        static void Add(Component c, Func<GameObject, bool> exclude, bool visibleOnly, List<GameButton> found, HashSet<GameObject> seen, HashSet<string> labels)
        {
            var why = WhyHidden(c);
            if (why != null) { LeftOut(why); return; }
            if (visibleOnly && !IsVisible(c)) { LeftOut("not drawn"); return; }
            if (!seen.Add(c.gameObject)) return;
            if (exclude != null && UnderAny(c.transform, exclude)) { LeftOut("bar or box out of view"); return; }
            var label = LabelOf(c);
            if (label.Length == 0) { LeftOut("no text"); return; }
            if (!labels.Add(label)) return;
            var target = c;
            found.Add(new GameButton { Target = c, Label = label, Click = () => Press(target) });
        }

        static bool UnderAny(Transform t, Func<GameObject, bool> exclude)
        {
            for (; t != null; t = t.parent) if (exclude(t.gameObject)) return true;
            return false;
        }
    }

    // Reaches into AccessibleMenu's option list (private in HSA) to insert and remove options
    static class MenuEdit
    {
        static readonly FieldInfo Options = Ref.Field(typeof(AccessibleMenu), "m_options");
        static readonly FieldInfo Index = Ref.Field(typeof(AccessibleMenu), "m_curOptionIdx");

        internal static IList List(AccessibleMenu menu) { return Options == null ? null : Options.GetValue(menu) as IList; }

        internal static string TextOf(object option)
        {
            var text = Ref.Get(option, "m_text") as string;
            if (text != null) return text;
            var get = Ref.Get(option, "m_getText") as AccessibleMenu.GetTextDelegate;
            try { return get == null ? "" : get(); } catch { return ""; }
        }

        // Adds options at `position` (end when negative); returns the option objects added
        internal static List<object> Insert(AccessibleMenu menu, int position, IList<GameButton> buttons)
        {
            var list = List(menu);
            var added = new List<object>();
            if (list == null) return added;
            foreach (var b in buttons)
            {
                var button = b;
                menu.AddOption(button.Label, () => { try { Log.Info("option: " + button.Label); button.Click(); } catch (Exception e) { Log.Error(e); } });
                added.Add(list[list.Count - 1]);
            }
            if (position >= 0 && position < list.Count - added.Count)
            {
                foreach (var o in added) list.Remove(o);
                for (int i = 0; i < added.Count; i++) list.Insert(position + i, added[i]);
            }
            return added;
        }

        internal static void Remove(AccessibleMenu menu, IEnumerable<object> options)
        {
            var list = List(menu);
            if (list == null) return;
            foreach (var o in options) list.Remove(o);
            ClampIndex(menu);
        }

        // names of the methods an option runs (its delegate fields)
        internal static HashSet<string> ActionNames(object option)
        {
            var names = new HashSet<string>();
            for (var t = option.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    Delegate d;
                    try { d = f.GetValue(option) as Delegate; } catch { continue; }
                    if (d == null) continue;
                    foreach (var one in d.GetInvocationList()) if (one.Method != null) names.Add(one.Method.Name);
                }
            return names;
        }

        internal static void ClampIndex(AccessibleMenu menu)
        {
            var list = List(menu);
            if (list == null || Index == null) return;
            var i = (int)Index.GetValue(menu);
            if (i >= list.Count) Index.SetValue(menu, Math.Max(0, list.Count - 1));
        }

        internal static int GetIndex(AccessibleMenu menu) { return Index == null ? 0 : (int)Index.GetValue(menu); }

        internal static void SetIndex(AccessibleMenu menu, int index) { if (Index != null) Index.SetValue(menu, index); }

        static readonly FieldInfo BackAction = Ref.Field(typeof(AccessibleMenu), "m_goBackAction");

        internal static Action GetBack(AccessibleMenu menu) { return BackAction == null ? null : BackAction.GetValue(menu) as Action; }

        internal static void SetBack(AccessibleMenu menu, Action back) { if (BackAction != null) BackAction.SetValue(menu, back); }

        internal static int IndexOfText(AccessibleMenu menu, string text)
        {
            var list = List(menu);
            if (list == null || string.IsNullOrEmpty(text)) return -1;
            for (int i = 0; i < list.Count; i++) if (TextOf(list[i]) == text) return i;
            return -1;
        }
    }
}
