using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Hearthstone.UI;
using UnityEngine;

namespace HSAEnhanced
{
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
                        return Str.Locked;
            var peg = c as PegUIElement;
            if (peg != null && !peg.IsEnabled()) return Str.Unavailable;
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
                    // (m_isShowing: e.g. the Pre-release Tavern Brawl button is in the box all the
                    // time and shown only while such a brawl is on; its click does nothing otherwise)
                    if (n.IndexOf("EnabledAndVisible", StringComparison.OrdinalIgnoreCase) >= 0 || n == "m_isEnabled" || n == "m_boxAllowsVisibility"
                        || n == "m_isShowing" || n == "m_isShown" || n == "m_isVisible")
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
        // (a text of symbols only, such as an info button's "?", names nothing: the tooltip does)
        internal static string LabelOf(Component c)
        {
            if (c == null) return "";
            var s = OwnText(c);
            if (HasWords(s)) return s;
            var clickable = c as Clickable;
            if (clickable != null) { s = WidgetText(clickable); if (HasWords(s)) return s; }
            s = TooltipHeadline(c);
            return HasWords(s) ? s : "";
        }

        internal static bool HasWords(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var ch in s) if (char.IsLetterOrDigit(ch)) return true;
            return false;
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
            if (peg != null && peg) { Log.Info("press (widget) " + clickable.name); Core.Click.Peg(peg); }
            else { Log.Info("press (mouse) " + clickable.name); Core.Click.Mouse(clickable); }
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
            if (peg != null) return () => Core.Click.Peg(peg);
            var widget = c as Widget;
            if (widget != null)
            {
                // a widget holding a single clickable: press that one
                var inner = widget.GetComponentsInChildren<Clickable>(false);
                if (inner.Length == 1) return () => Press(inner[0]);
                return () => Core.Click.WidgetClicked(widget);
            }
            return () => Core.Click.Mouse(c);
        }

        // The values of `owner`'s fields that are a T (a live object), found by type, not by name
        internal static List<T> FieldsOfType<T>(object owner) where T : class
        {
            var found = new List<T>();
            if (owner == null) return found;
            for (var t = owner.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(T).IsAssignableFrom(f.FieldType) && !f.FieldType.IsAssignableFrom(typeof(T))) continue;
                    object v;
                    try { v = f.GetValue(owner); } catch { continue; }
                    var x = v as T;
                    if (x == null) continue;
                    if (x is UnityEngine.Object && !(x as UnityEngine.Object)) continue;
                    found.Add(x);
                }
            return found;
        }

        internal static T FieldOfType<T>(object owner) where T : class
        {
            var all = FieldsOfType<T>(owner);
            return all.Count == 0 ? null : all[0];
        }

        // Every button held in the fields of `owner` (Box, RibbonButtonsUI, ...) that is on screen.
        // Fields named in `skip` are left out; nested holders listed in `descend` are walked too.
        internal static List<GameButton> ButtonsIn(object owner, ICollection<string> skip, Func<object, bool> descend)
        {
            var found = new List<GameButton>();
            var seen = new HashSet<GameObject>();
            Collect(owner, skip, descend, found, seen, 0);
            return PreferListened(found);
        }

        // Two buttons that read the same (the box has a Pre-release Tavern Brawl button of its own
        // and one on its ribbon, and only the ribbon's is listened to): the one the game listens to
        // for a click stays, in the place of the first
        static List<GameButton> PreferListened(List<GameButton> found)
        {
            var result = new List<GameButton>();
            foreach (var b in found)
            {
                int same = result.FindIndex(x => Labels.Norm(x.Label) == Labels.Norm(b.Label));
                if (same < 0) { result.Add(b); continue; }
                if (Listened(b.Target) > Listened(result[same].Target))
                {
                    Log.Once("same button twice, the one the game listens to kept: " + GameButton.Describe(new List<GameButton> { b }));
                    result[same] = b;
                }
            }
            return result;
        }

        // For the log: a button's own flags and which of its parts are missing, and the box's state
        // (why a press did nothing)
        internal static string StateText(Component c)
        {
            var parts = new List<string>();
            try
            {
                for (var t = c == null ? null : c.GetType(); t != null && t != typeof(PegUIElement) && t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        object v;
                        try { v = f.GetValue(c); } catch { continue; }
                        if (f.FieldType == typeof(bool)) parts.Add(f.Name + "=" + v);
                        else if (!f.FieldType.IsValueType && (v == null || (v is UnityEngine.Object && !(UnityEngine.Object)v))) parts.Add(f.Name + " missing");
                    }
                var box = Box.Get();
                if (box != null) parts.Add("box with buttons=" + box.IsInStateWithButtons());
                if (SceneMgr.Get() != null) parts.Add("scene " + SceneMgr.Get().GetMode());
                parts.Add("online=" + Network.IsLoggedIn());
            }
            catch (Exception e) { parts.Add(e.GetType().Name); }
            return string.Join(", ", parts.ToArray());
        }

        // 2: the game listens to its release (a click); 1: its widget has event listeners; 0: neither
        internal static int Listened(Component c)
        {
            try
            {
                var peg = c as PegUIElement;
                var map = peg == null ? null : Ref.Get(peg, "m_eventListeners") as IEnumerable;
                if (map != null)
                    foreach (var entry in map)
                    {
                        var t = entry.GetType();
                        var key = t.GetProperty("Key"); var value = t.GetProperty("Value");
                        if (key == null || value == null) break;
                        var k = key.GetValue(entry, null);
                        var list = value.GetValue(entry, null) as ICollection;
                        if (k is UIEventType && (UIEventType)k == UIEventType.RELEASE && list != null && list.Count > 0) return 2;
                    }
                var widget = c == null ? null : c.GetComponent<Widget>();
                if (widget != null && Ref.Get(widget, "m_eventListeners") != null) return 1;
            }
            catch { }
            return 0;
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
                    // a widget holding several buttons (the rank display: medal and rewards chest): each of
                    // its buttons, pressed through its own click; the widget as a whole takes no click
                    if (c is Widget)
                    {
                        if (WhyHidden(c) != null || !seen.Add(c.gameObject)) continue;
                        foreach (var inner in ClickablesUnder(c.gameObject, null))
                            if (seen.Add(inner.Object)) found.Add(inner);
                        continue;
                    }
                    // hidden or disabled: left out (a button the game has disabled does nothing)
                    // (the button's own flags count too when a widget holds it: the box's Pre-release
                    // Tavern Brawl widget stays up while its button is hidden)
                    var why = WhyHidden(shownBy) ?? (shownBy != c && !StateFlagsAllow(c) ? "hidden by its state" : null);
                    if (why == "hidden by its state") Log.Once("not shown by the game (its state): " + t.Name + "." + f.Name);
                    if (why == "disabled") Log.Once("disabled by the game, left out: " + t.Name + "." + f.Name);
                    if (why != null || !seen.Add(c.gameObject)) continue;
                    var label = LabelOf(c);
                    if (label.Length == 0) label = GameNameFor(f.Name);
                    // no text the game has for it: left out rather than named in English
                    if (label.Length == 0) { Log.Once("button without a game text left out: " + t.Name + "." + f.Name); continue; }
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
                if (!Screens.IsScreen(mb) || !mb.isActiveAndEnabled) continue;
                foreach (var b in ButtonsIn(mb, new string[0], v => false))
                    if (objects.Add(b.Object) && !Labels.SimilarToAny(labels, b.Label)) { labels.Add(b.Label); found.Add(b); }
            }
            foreach (var b in ClickablesOnScreen(go => go == bar || go == box))
                if (!objects.Contains(b.Object) && !Labels.SimilarToAny(labels, b.Label)) { labels.Add(b.Label); found.Add(b); }
            var why = new List<string>();
            foreach (var kv in s_leftOut) why.Add(kv.Key + " " + kv.Value);
            Log.Once("screen scan (" + (SceneMgr.Get() == null ? "?" : SceneMgr.Get().GetMode().ToString()) + "): " + found.Count + " buttons; left out: " + (why.Count == 0 ? "none" : string.Join(", ", why.ToArray())));
            // reading order: top to bottom, left to right
            SortByScreen(found);
            return found;
        }

        internal static float ScreenOrderOf(Component c) { return ScreenOrder(c); }

        // (text object, text) for each text shown under `root` that is not on a button, in reading order
        internal static List<KeyValuePair<Component, string>> TextsUnder(GameObject root)
        {
            var found = new List<KeyValuePair<Component, string>>();
            if (root == null) return found;
            var seen = new HashSet<string>();
            foreach (var ut in root.GetComponentsInChildren<UberText>(false))
            {
                if (ut == null || !ut.isActiveAndEnabled) continue;
                if (ut.GetComponentInParent<PegUIElement>() != null || ut.GetComponentInParent<Clickable>() != null) continue;
                var s = ShownText(ut.Text);
                if (!HasWords(s) || !seen.Add(s)) continue;
                found.Add(new KeyValuePair<Component, string>(ut, s));
            }
            var keyed = new List<KeyValuePair<float, int>>();
            for (int i = 0; i < found.Count; i++) keyed.Add(new KeyValuePair<float, int>(ScreenOrder(found[i].Key), i));
            keyed.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
            var sorted = new List<KeyValuePair<Component, string>>();
            foreach (var k in keyed) sorted.Add(found[k.Value]);
            return sorted;
        }

        // Stable sort into reading order (buttons in the same place keep their order)
        internal static void SortByScreen(List<GameButton> buttons)
        {
            var keyed = new List<KeyValuePair<float, int>>();
            for (int i = 0; i < buttons.Count; i++) keyed.Add(new KeyValuePair<float, int>(ScreenOrder(buttons[i].Target), i));
            keyed.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
            var sorted = new List<GameButton>();
            foreach (var k in keyed) sorted.Add(buttons[k.Value]);
            buttons.Clear(); buttons.AddRange(sorted);
        }

        // Reading order on screen: rows top to bottom (about a twentieth of the screen high),
        // left to right in a row; seen through the camera that draws the element
        static float ScreenOrder(Component c)
        {
            var p = ScreenPoint(c);
            if (p == null) return 0;
            float row = Mathf.Max(20f, Screen.height / 20f);
            return -Mathf.Round(p.Value.y / row) * 100000f + p.Value.x;
        }

        // Where the element is on screen, in pixels; null when no camera draws it
        internal static Vector3? ScreenPoint(Component c)
        {
            if (c == null || !c) return null;
            var layer = 1 << c.gameObject.layer;
            Camera best = null;
            foreach (var cam in Camera.allCameras)
                if (cam != null && cam.isActiveAndEnabled && (cam.cullingMask & layer) != 0 && (best == null || cam.depth > best.depth)) best = cam;
            if (best == null) best = Camera.main;
            if (best == null) return null;
            var p = best.WorldToScreenPoint(c.transform.position);
            if (p.z < 0) return null;     // behind the camera
            return p;
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

        static bool InVillage()
        {
            var scenes = SceneMgr.Get();
            var mode = scenes == null ? SceneMgr.Mode.INVALID : scenes.GetMode();
            return mode == SceneMgr.Mode.LETTUCE_VILLAGE || mode == SceneMgr.Mode.LETTUCE_MAP;
        }

        static void LeftOut(string why) { int n; s_leftOut.TryGetValue(why, out n); s_leftOut[why] = n + 1; }

        static void Add(Component c, Func<GameObject, bool> exclude, bool visibleOnly, List<GameButton> found, HashSet<GameObject> seen, HashSet<string> labels)
        {
            var why = WhyHidden(c);
            if (why != null) { LeftOut(why); return; }
            if (visibleOnly && !IsVisible(c)) { LeftOut("not drawn"); return; }
            // the village Campfire's task cards stay drawn behind other screens (its own menu reads them)
            if (visibleOnly && c.GetComponentInParent<Hearthstone.LettuceVillageTaskBoard>() != null) { LeftOut("campfire task"); return; }
            // the village's popups (the Campfire's task list and the others) stay loaded after the
            // village is left: none of their buttons belong to the screens after it
            if (!InVillage() && c.GetComponentInParent<Hearthstone.LettuceVillagePopupManager>() != null) { LeftOut("village popup"); return; }
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

    // the game's screen objects a screen reader is attached to (Hearthstone Access's screens)
    static class Screens
    {
        internal static bool IsScreen(object o)
        {
            return false;
        }
    }

    // the game's own name for a screen (its button's headline)
    static class SceneNames
    {
        internal static string Of(SceneMgr.Mode mode)
        {
            switch (mode)
            {
                case SceneMgr.Mode.HUB: return Str.Word("ACCESSIBILITY_HUB_MAIN_MENU_TITLE");
                case SceneMgr.Mode.TOURNAMENT: return Str.Word("GLUE_TOURNAMENT", "GLOBAL_PLAY");
                case SceneMgr.Mode.COLLECTIONMANAGER: return Str.Word("GLUE_MY_COLLECTION");
                case SceneMgr.Mode.PACKOPENING: return Str.Word("GLUE_OPEN_PACKS");
                case SceneMgr.Mode.ADVENTURE: return Str.Word("GLUE_ADVENTURE");
                case SceneMgr.Mode.TAVERN_BRAWL: return Str.Word("GLOBAL_TAVERN_BRAWL", "GLUE_TOOLTIP_BUTTON_TAVERN_BRAWL_HEADLINE");
                case SceneMgr.Mode.BACON: return Str.Word("GLUE_BACON");
                case SceneMgr.Mode.LETTUCE_VILLAGE: return Str.Word("GLUE_MERCENARIES");
                case SceneMgr.Mode.GAME_MODE: return Str.Word("GLUE_TOOLTIP_BUTTON_GAME_MODES_HEADLINE");
                case SceneMgr.Mode.DRAFT: return Str.Word("GLOBAL_ARENA");
                case SceneMgr.Mode.CREDITS: return Str.Word("GLOBAL_CREDITS", "GLUE_CREDITS");
                default: return "";
            }
        }
    }
}
