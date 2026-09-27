using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Accessibility;
using Hearthstone.UI;
using UnityEngine;

namespace HSAEnhanced
{
    // Options added to an HSA menu from what the game shows. Updates keep the player's place:
    // nothing changes unless the shown buttons really changed (and stayed changed for two
    // checks, so buttons that flicker during animations do not move the cursor), and the
    // selected option stays selected.
    class ExtraOptions
    {
        static readonly ConditionalWeakTable<AccessibleMenu, ExtraOptions> s_of = new ConditionalWeakTable<AccessibleMenu, ExtraOptions>();

        List<object> m_added = new List<object>();
        string m_applied;
        string m_pending;

        internal static ExtraOptions Of(AccessibleMenu menu)
        {
            ExtraOptions x;
            if (!s_of.TryGetValue(menu, out x)) { x = new ExtraOptions(); s_of.Add(menu, x); }
            return x;
        }

        readonly Dictionary<object, Component> m_targets = new Dictionary<object, Component>();

        internal bool IsOurs(object option) { return m_added.Contains(option); }

        // the game's button one of our options presses
        internal Component TargetOf(object option)
        {
            Component c;
            return m_targets.TryGetValue(option, out c) ? c : null;
        }

        internal bool HasAdded { get { return m_added.Count > 0; } }

        static string TypeOf(object o) { return o == null ? "no parent" : o.GetType().Name; }

        // Labels of the menu's own (HSA) options
        internal List<string> OwnLabels(AccessibleMenu menu)
        {
            var labels = new List<string>();
            var list = MenuEdit.List(menu);
            if (list != null) foreach (var o in list) if (!IsOurs(o)) labels.Add(MenuEdit.TextOf(o));
            return labels;
        }

        // Puts `buttons` in place of the options added before; true when the menu changed.
        // `immediate` skips the two-check wait (a freshly built menu).
        internal bool Update(AccessibleMenu menu, List<GameButton> buttons, Func<int> position, bool immediate)
        {
            var labels = new List<string>();
            foreach (var b in buttons) labels.Add(b.Label);
            var sig = string.Join("\n", labels.ToArray());
            var list = MenuEdit.List(menu);
            if (list == null) return false;
            // HSA cleared or refilled the menu itself: what we added before is gone
            foreach (var o in m_added) if (!list.Contains(o)) { m_added.Clear(); m_targets.Clear(); m_applied = null; break; }
            if (sig == m_applied) { m_pending = null; return false; }
            if (!immediate && sig != m_pending) { m_pending = sig; return false; }
            m_pending = null;

            var index = MenuEdit.GetIndex(menu);
            var selected = index >= 0 && index < list.Count ? list[index] : null;
            var selectedLabel = selected == null ? null : MenuEdit.TextOf(selected);
            bool selectedWasOurs = selected != null && IsOurs(selected);

            foreach (var o in m_added) list.Remove(o);
            m_added = MenuEdit.Insert(menu, position(), buttons);
            m_targets.Clear();
            for (int i = 0; i < m_added.Count && i < buttons.Count; i++) m_targets[m_added[i]] = buttons[i].Target;
            m_applied = sig;
            Log.Once("menu '" + Ref.Get(menu, "m_menuName") + "' (" + TypeOf(Ref.Get(menu, "m_parent")) + ") has " + list.Count + " options, added: " + GameButton.Describe(buttons));

            // back to the same option: the same object, or our option with the same text
            int to = selected == null ? -1 : list.IndexOf(selected);
            if (to < 0 && selectedWasOurs)
                foreach (var o in m_added) if (MenuEdit.TextOf(o) == selectedLabel) { to = list.IndexOf(o); break; }
            if (to < 0) to = Math.Min(index, list.Count - 1);
            MenuEdit.SetIndex(menu, Math.Max(0, to));
            return true;
        }
    }

    static class Labels
    {
        internal static string Norm(string s)
        {
            return Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\w]+", " ").Trim();
        }

        // Same text, or one is the other with words added ("Play" / "Play ranked")
        internal static bool Similar(string a, string b)
        {
            a = Norm(a); b = Norm(b);
            if (a.Length == 0 || b.Length == 0) return a == b;
            if (a == b) return true;
            return (" " + a + " ").Contains(" " + b + " ") || (" " + b + " ").Contains(" " + a + " ");
        }

        internal static bool SimilarToAny(IEnumerable<string> labels, string label)
        {
            foreach (var l in labels) if (Similar(l, label)) return true;
            return false;
        }

        static string[] s_backWords;

        // "Back", "Cancel", "Close", ... in the game's language (and English)
        internal static bool IsBack(string label)
        {
            if (s_backWords == null)
            {
                var words = new List<string> { "back", "go back", "cancel", "close", "done", "exit", "leave" };
                foreach (var key in new[] { "GLOBAL_BACK", "GLOBAL_CANCEL", "GLOBAL_CLOSE", "GLOBAL_DONE", "GLOBAL_EXIT", "GLOBAL_LEAVE" })
                {
                    var s = Str.Game(key);
                    if (!string.IsNullOrEmpty(s)) words.Add(s);
                }
                try { words.Add(LocalizedText.SCREEN_GO_BACK); } catch { }
                s_backWords = words.ToArray();
            }
            var n = Norm(label);
            foreach (var w in s_backWords) if (n == Norm(w)) return true;
            return false;
        }
    }

    // Every HSA menu: the buttons the game shows around it are added, and a menu without a
    // way back gets one
    static class MenuAugment
    {
        internal static void BeforeRead(AccessibleMenu menu)
        {
            var parent = Owner(menu);
            if (parent is AccessibleHub) return;                 // HubMenu takes care of it
            if (parent is FallbackUI || parent is SetRotationUI || parent is AccessiblePlayScreen) return;   // ours, built from the screen already
            if (BuiltWholeByHsa(parent)) { if (GameState.Get() == null && Engine.Enabled) MarkLocked(menu); return; }
            var root = parent as Component;
            s_open = menu;
            if (GameState.Get() == null)
            {
                MarkLocked(menu);
                // a Death Knight deck's rune slots (HSA has none), with --use-hsa-menus too
                if (Runes.IsEditDeckMenu(parent, menu)) AddScreenButtons(menu, Runes.Buttons(menu), true);
                else if (!Engine.Enabled) { }                     // --use-hsa-menus: HSA's options only
                // an adventure chapter's missions: named, nothing from the page added (its click area)
                else if (parent is AdventureBookPageDisplay && Book.FillMissionMenu(menu)) { }
                else if (root != null && root) AddScreenButtons(menu, OwnerButtons(root), true);
                else if (WidgetOf(parent) != null) AddScreenButtons(menu, Ui.ClickablesUnder(WidgetOf(parent).gameObject, null), true);
                // HSA screens that are not game objects (adventures, ...): the whole screen, once it
                // stayed the same for two checks (right after a screen change the one left is still there)
                else if (TakesScreenButtons(parent)) AddScreenButtons(menu, Ui.ScreenButtons(), false);
            }
            // not during a match: the game's back there is the concede menu
            if (GameState.Get() == null && !(parent is AccessibleBlackMarket))
            {
                var hsaBack = MenuEdit.GetBack(menu);
                var go = root == null ? null : root.gameObject;
                if (hsaBack == null) MenuEdit.SetBack(menu, () => Back.Go(menu, go));
                else if (!Back.IsWrapped(hsaBack))
                    // HSA's own back first; if the same menu is still up a moment later, ours
                    MenuEdit.SetBack(menu, Back.Wrap(() => { hsaBack(); Back.Watch(menu, go); }));
            }
        }

        // The widget an HSA class that is not a game object works on (e.g. the journal's), if shown
        static Widget WidgetOf(object parent)
        {
            if (parent == null) return null;
            foreach (var f in parent.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (typeof(Widget).IsAssignableFrom(f.FieldType))
                {
                    var w = f.GetValue(parent) as Widget;
                    if (w != null && w && w.IsActive) return w;
                }
            return null;
        }

        static readonly ConditionalWeakTable<object, string> s_ownText = new ConditionalWeakTable<object, string>();

        static readonly ConditionalWeakTable<AccessibleMenu, List<KeyValuePair<int, object>>> s_hidden = new ConditionalWeakTable<AccessibleMenu, List<KeyValuePair<int, object>>>();

        // HSA options whose action presses something the game shows as locked get "locked" added
        // (e.g. HSA's mission list offers every mission); those whose button the game has disabled
        // are taken out, and put back in their place once it is enabled again. What an option
        // presses is read from the objects its action captured.
        static void MarkLocked(AccessibleMenu menu)
        {
            var list = MenuEdit.List(menu);
            if (list == null) return;
            List<KeyValuePair<int, object>> hidden;
            if (!s_hidden.TryGetValue(menu, out hidden)) { hidden = new List<KeyValuePair<int, object>>(); s_hidden.Add(menu, hidden); }
            var index = MenuEdit.GetIndex(menu);
            var selected = index >= 0 && index < list.Count ? list[index] : null;
            foreach (var h in hidden) list.Insert(Math.Min(h.Key, list.Count), h.Value);
            hidden.Clear();

            var extra = ExtraOptions.Of(menu);
            var disabled = new List<object>();
            foreach (var option in list)
            {
                if (extra.IsOurs(option)) continue;
                var click = Ref.Get(option, "m_onClickAction") as Action;
                var text = Ref.Get(option, "m_text") as string;
                if (click == null || text == null) continue;
                string own;
                if (!s_ownText.TryGetValue(option, out own)) { own = text; s_ownText.Add(option, own); }
                var state = Ui.StateOf(Captured(click.Target));
                if (state == Str.Unavailable) { disabled.Add(option); state = null; }
                Ref.Set(option, "m_text", state == null ? own : Str.Join(own, state));
            }
            // a menu of nothing but disabled options stays as it is
            if (disabled.Count > 0 && disabled.Count < list.Count)
            {
                foreach (var o in disabled)
                {
                    hidden.Add(new KeyValuePair<int, object>(list.IndexOf(o), o));
                    list.Remove(o);
                }
                Log.Once("menu '" + Ref.Get(menu, "m_menuName") + "': disabled options left out: " + disabled.Count);
            }
            int to = selected == null ? -1 : list.IndexOf(selected);
            MenuEdit.SetIndex(menu, to >= 0 ? to : Math.Max(0, Math.Min(index, list.Count - 1)));
        }

        // The game object an action presses: a component it closes over (or is a method of)
        static Component Captured(object target)
        {
            var c = target as Component;
            if (c != null) return c;
            if (target == null) return null;
            foreach (var f in target.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                var v = f.GetValue(target) as Component;
                if (v != null && v && (v is PegUIElement || v is Clickable)) return v;
            }
            return null;
        }

        static AccessibleMenu s_open;     // the HSA menu read last

        internal static bool IsOpen(AccessibleMenu menu) { return ReferenceEquals(s_open, menu); }

        // Who a menu belongs to: its parent, or for menus HSA made without one (e.g. the
        // adventure mission list) the screen it is read on
        static object Owner(AccessibleMenu menu)
        {
            return Ref.Get(menu, "m_parent") ?? Ref.Field(typeof(AccessibilityMgr), "s_curScreen")?.GetValue(null);
        }

        // The buttons under a game object that owns a menu, and those it holds in its fields
        // (lists included: buttons of a menu that are not under it in the scene)
        static List<GameButton> OwnerButtons(Component owner)
        {
            var options = owner as OptionsMenu;
            if (options != null) return OptionsScreen.Buttons(options);
            var found = Ui.ClickablesUnder(owner.gameObject, null);
            var labels = new HashSet<string>();
            foreach (var b in found) labels.Add(b.Label);
            foreach (var b in Ui.ButtonsIn(owner, new string[0], v => false))
                if (labels.Add(b.Label)) found.Add(b);
            return found;
        }

        // Buttons that appear after a menu opened (they slide in, load late): looked for again
        // every second while that menu has focus, through the same stable update
        internal static void Tick()
        {
            var menu = s_open;
            if (menu == null || GameState.Get() != null) return;
            var parent = Owner(menu);
            // the rune slots follow the deck (adding a runed card fills empty slots)
            if (Runes.IsEditDeckMenu(parent, menu))
            {
                if (AccessibilityMgr.IsCurrentlyFocused((AccessibleComponent)parent)) AddScreenButtons(menu, Runes.Buttons(menu), false);
                return;
            }
            if (!Engine.Enabled) return;
            if (parent == null || parent is AccessibleHub || parent is FallbackUI || parent is SetRotationUI || parent is AccessiblePlayScreen || BuiltWholeByHsa(parent) || !(parent is AccessibleComponent) || !AccessibilityMgr.IsCurrentlyFocused((AccessibleComponent)parent)) return;
            if (!IsCurrentMenuOf(parent, menu)) return;
            var root = parent as Component;
            if (parent is AdventureBookPageDisplay && Book.FillMissionMenu(menu)) return;
            if (root != null && root) AddScreenButtons(menu, OwnerButtons(root), false);
            else if (WidgetOf(parent) != null) AddScreenButtons(menu, Ui.ClickablesUnder(WidgetOf(parent).gameObject, null), false);
            else if (TakesScreenButtons(parent)) AddScreenButtons(menu, Ui.ScreenButtons(), false);
        }

        // The rune slots' labels right after one was changed
        internal static void Refresh(AccessibleMenu menu)
        {
            if (Runes.IsEditDeckMenu(Owner(menu), menu)) AddScreenButtons(menu, Runes.Buttons(menu), true);
        }

        // Screens whose menus get the buttons of the whole screen. Not the Black Market, and not the
        // collection: HSA gives each of its menus (a card's crafting, filters, decks, a deck) just
        // what belongs there, and the whole screen would add the deck tray and the crafting
        // checkboxes to every one of them
        static bool TakesScreenButtons(object parent)
        {
            return parent is AccessibleScreen && !(parent is AccessibleBlackMarket) && !(parent is AccessibleCollectionManager);
        }

        // Owners whose menus HSA builds whole, left as HSA makes them: the social list (HSA's menus
        // for the list, a friend, a challenge and a request; the buttons under the list are every
        // friend's challenge and chat buttons and its headers, which would be added to each of them)
        static bool BuiltWholeByHsa(object parent)
        {
            return parent is FriendListFrame;
        }

        // the menu is still one of its owner's (not an old one it replaced)
        static bool IsCurrentMenuOf(object parent, AccessibleMenu menu)
        {
            if (Ref.Get(menu, "m_parent") == null) return true;   // no owner of its own: the screen's
            foreach (var f in parent.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (f.FieldType == typeof(AccessibleMenu) && ReferenceEquals(f.GetValue(parent), menu)) return true;
            return false;
        }

        static void AddScreenButtons(AccessibleMenu menu, List<GameButton> shown, bool immediate)
        {
            var extra = ExtraOptions.Of(menu);
            var own = extra.OwnLabels(menu);
            var buttons = new List<GameButton>();
            bool hasBack = MenuEdit.GetBack(menu) != null;
            var title = Ref.Get(menu, "m_menuName") as string;
            var covered = CoveredTargets();
            foreach (var b in shown)
            {
                if (b.Target != null && covered.Contains(b.Target.gameObject)) continue;   // an option presses it already
                if (!string.IsNullOrEmpty(title) && Labels.Similar(title, b.Label) && Labels.Norm(title) == Labels.Norm(b.Label)) continue;
                if (hasBack && Labels.IsBack(b.Label)) continue;     // the menu goes back already
                if (!Labels.SimilarToAny(own, b.Label)) { own.Add(b.Label); buttons.Add(b); }
            }
            // in the order the screen shows them
            Ui.SortByScreen(buttons);
            // before a closing Back / Cancel option, if the menu ends with one
            extra.Update(menu, buttons, () =>
            {
                var list = MenuEdit.List(menu);
                int n = list == null ? 0 : list.Count;
                while (n > 0 && extra.IsOurs(list[n - 1])) n--;
                return n > 0 && Labels.IsBack(MenuEdit.TextOf(list[n - 1])) ? n - 1 : -1;
            }, immediate);
        }

        // Buttons the menu's own options press already, recognised by identity (any language):
        // the game modes screen's confirm button (choosing a mode presses it); its back button is
        // left out as a back button
        static HashSet<GameObject> CoveredTargets()
        {
            var set = new HashSet<GameObject>();
            try
            {
                var gm = GameModeDisplay.Get();
                var play = gm == null ? null : Ui.FieldOfType<PlayButton>(gm);
                if (play != null) set.Add(play.gameObject);
            }
            catch { }
            return set;
        }

        internal static void BeforeHorizontalRead(object menu)
        {
            if (Ref.Get(menu, "m_goBackAction") != null || GameState.Get() != null) return;
            var parent = Ref.Get(menu, "m_parent") as Component;
            Action back = () => Back.Go(null, parent == null ? null : parent.gameObject);
            Ref.Set(menu, "m_goBackAction", back);
        }
    }

    static class Back
    {
        static readonly HashSet<Action> s_wrapped = new HashSet<Action>();
        static AccessibleMenu s_watchMenu;
        static GameObject s_watchRoot;
        static float s_watchUntil;

        internal static Action Wrap(Action a) { s_wrapped.Add(a); return a; }
        internal static bool IsWrapped(Action a) { return s_wrapped.Contains(a); }

        internal static void Watch(AccessibleMenu menu, GameObject root)
        {
            s_watchMenu = menu; s_watchRoot = root; s_watchUntil = Time.unscaledTime + 0.5f;
        }

        // HSA's back did nothing: its menu is still the one read last and still has focus
        internal static void Tick()
        {
            var menu = s_watchMenu;
            if (menu == null || Time.unscaledTime < s_watchUntil) return;
            s_watchMenu = null;
            var parent = Ref.Get(menu, "m_parent") as AccessibleComponent;
            if (!MenuAugment.IsOpen(menu)) return;
            if (parent != null && !AccessibilityMgr.IsCurrentlyFocused(parent)) return;
            if (s_watchRoot != null && !s_watchRoot.activeInHierarchy) return;
            Log.Info("back: Hearthstone Access's back did nothing");
            Go(menu, s_watchRoot, true);
        }

        internal static void Go(AccessibleMenu menu, GameObject root) { Go(menu, root, false); }

        // The game's back navigation first (what Escape does), else the menu's own
        // Back/Cancel/Close option, else such a button on screen
        // afterHsa: HSA's own back option already ran (and did nothing)
        internal static void Go(AccessibleMenu menu, GameObject root, bool afterHsa)
        {
            try
            {
                if (SetRotation.Running) { Log.Info("back: not during the set rotation intro"); AccessibilityMgr.OutputNotification(Str.Join(Str.Back, Str.Unavailable)); return; }
                if (Navigation.GoBack()) { Log.Info("back: the game's navigation"); return; }
                // adventures go back through their own sub-screen stack
                if (SceneMgr.Get() != null && SceneMgr.Get().GetMode() == SceneMgr.Mode.ADVENTURE && AdventureConfig.Get() != null
                    && AdventureConfig.Get().CurrentSubScene != Assets.AdventureData.Adventuresubscene.CHOOSER)
                {
                    Log.Info("back: adventure sub-screen " + AdventureConfig.Get().CurrentSubScene);
                    AdventureConfig.Get().SubSceneGoBack(true);
                    return;
                }
                var list = menu == null ? null : MenuEdit.List(menu);
                if (list != null)
                    for (int i = list.Count - 1; i >= 0 && !afterHsa; i--)
                        if (Labels.IsBack(MenuEdit.TextOf(list[i])))
                        {
                            var click = Ref.Get(list[i], "m_onClickAction") as Action;
                            // not the menu's own back option (that would come straight back here)
                            if (click != null && !click.Equals(MenuEdit.GetBack(menu))) { Log.Info("back: option " + MenuEdit.TextOf(list[i])); click(); return; }
                        }
                foreach (var b in root != null ? Ui.ClickablesUnder(root, null) : Ui.ScreenButtons())
                    if (Labels.IsBack(b.Label)) { Log.Info("back: button " + b.Label); b.Click(); return; }
                // a popup: closed the way the game closes it (its Hide / Close); never a whole screen
                if (root != null && menu != null && Ref.Get(menu, "m_parent") is AccessibleUI)
                    foreach (var mb in root.GetComponents<MonoBehaviour>())
                        foreach (var name in new[] { "Close", "Hide", "Dismiss" })
                        {
                            var m = mb == null ? null : Ref.Method(mb.GetType(), name, 0);
                            if (m == null) continue;
                            Log.Info("back: " + mb.GetType().Name + "." + name + "()");
                            m.Invoke(mb, null);
                            return;
                        }
                // last: the main menu, the way the game's own home navigation goes
                var scenes = SceneMgr.Get();
                if (scenes != null && scenes.GetMode() != SceneMgr.Mode.HUB && scenes.GetMode() != SceneMgr.Mode.GAMEPLAY
                    && scenes.GetMode() != SceneMgr.Mode.LOGIN && scenes.GetMode() != SceneMgr.Mode.STARTUP && !scenes.IsTransitioning())
                {
                    Log.Info("back: to the main menu");
                    scenes.SetNextMode(SceneMgr.Mode.HUB);
                    return;
                }
                Log.Info("back: nothing to go back to");
            }
            catch (Exception e) { Log.Error(e); }
            AccessibilityMgr.OutputNotification(Str.Join(Str.Back, Str.Unavailable));
        }
    }
}
