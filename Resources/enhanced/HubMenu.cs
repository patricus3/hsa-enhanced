using System;
using System.Collections.Generic;
using Accessibility;
using UnityEngine;

namespace HSAEnhanced
{
    // The hub (main) menu: HSA builds its own options (with its extra logic for Play,
    // packs, shop, ...); every other button the game shows on the box and its ribbon is
    // added here, found by walking Box's fields, and kept in sync while the hub is open.
    // HSA's help / game menu / social pointers are removed.
    static class HubMenu
    {
        // HSA's hub method -> the Box fields it presses (read from its IL when the mod is built)
        static readonly Dictionary<string, string[]> s_pressed = new Dictionary<string, string[]>();

        internal static void AfterSetup(object hub, AccessibleMenu menu, string hsaFields)
        {
            if (!Engine.Enabled || menu == null || Box.Get() == null) return;
            if (s_pressed.Count == 0 && !string.IsNullOrEmpty(hsaFields))
                foreach (var entry in hsaFields.Split('|'))
                {
                    var eq = entry.IndexOf('=');
                    if (eq > 0) s_pressed[entry.Substring(0, eq)] = entry.Substring(eq + 1).Split('+');
                }
            Refresh(menu, true);
            var box = Box.Get().gameObject;
            var watcher = box.GetComponent<HubWatcher>() ?? box.AddComponent<HubWatcher>();
            watcher.Hub = hub as AccessibleComponent;
        }

        // true when the menu changed
        internal static bool Refresh(AccessibleMenu menu, bool immediate)
        {
            RemoveHelpEntries(menu);
            OneOptionPerButton(menu);
            var extra = ExtraOptions.Of(menu);
            bool changed = extra.Update(menu, Discover(menu, extra), () => -1, immediate);
            return Arrange(menu, extra, immediate) || changed;
        }

        static string s_pendingOrder;

        // The options in the order the box shows their buttons (top to bottom, left to right):
        // HSA's by the Box button their action presses, ours by the button they were made from.
        // An option with no button on screen stays right after the one before it. Applied once the
        // order held for two checks (the box moves while it opens), keeping the selected option.
        static bool Arrange(AccessibleMenu menu, ExtraOptions extra, bool immediate)
        {
            var list = MenuEdit.List(menu);
            if (list == null || list.Count < 2) return false;
            var box = Box.Get();
            var keys = new float[list.Count];
            bool any = false;
            for (int i = 0; i < list.Count; i++)
            {
                var target = extra.IsOurs(list[i]) ? extra.TargetOf(list[i]) : PressedBy(list[i], box);
                var p = target == null ? null : Ui.ScreenPoint(target);
                if (p != null) { keys[i] = Ui.ScreenOrderOf(target); any = true; }
                else keys[i] = i == 0 ? -3e38f : keys[i - 1];
            }
            if (!any) return false;
            var order = new List<int>();
            for (int i = 0; i < list.Count; i++) order.Add(i);
            order.Sort((a, b) => keys[a] != keys[b] ? keys[a].CompareTo(keys[b]) : a.CompareTo(b));
            bool same = true;
            for (int i = 0; i < order.Count; i++) if (order[i] != i) { same = false; break; }
            if (same) { s_pendingOrder = null; return false; }
            var sig = new System.Text.StringBuilder();
            foreach (var i in order) sig.Append(MenuEdit.TextOf(list[i])).Append('\n');
            if (!immediate && sig.ToString() != s_pendingOrder) { s_pendingOrder = sig.ToString(); return false; }
            s_pendingOrder = null;

            var index = MenuEdit.GetIndex(menu);
            var selected = index >= 0 && index < list.Count ? list[index] : null;
            var items = new List<object>();
            foreach (var i in order) items.Add(list[i]);
            list.Clear();
            foreach (var o in items) list.Add(o);
            if (selected != null) MenuEdit.SetIndex(menu, list.IndexOf(selected));
            Log.Once("main menu in the box's order: " + sig.ToString().Replace("\n", " | "));
            return true;
        }

        // options that stand for one Box button in place of several HSA options
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Component> s_merged = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Component>();

        // Several HSA options that press the same Box button (HSA's Ranked and Casual both press
        // Play) become that one button, named and pressed as the game shows it: the choice between
        // them is the game's own, on the screen the button opens (the format picker)
        static void OneOptionPerButton(AccessibleMenu menu)
        {
            var list = MenuEdit.List(menu);
            if (list == null) return;
            var box = Box.Get();
            var groups = new Dictionary<Component, List<object>>();
            var order = new List<Component>();
            foreach (var o in list)
            {
                Component merged;
                if (o == null || s_merged.TryGetValue(o, out merged)) continue;
                var c = PressedBy(o, box);
                if (c == null) continue;
                List<object> g;
                if (!groups.TryGetValue(c, out g)) { groups[c] = g = new List<object>(); order.Add(c); }
                g.Add(o);
            }
            foreach (var c in order)
            {
                var g = groups[c];
                if (g.Count < 2) continue;
                var label = Ui.LabelOf(c);
                if (label.Length == 0) label = MenuEdit.TextOf(g[0]);
                var at = list.IndexOf(g[0]);
                var index = MenuEdit.GetIndex(menu);
                bool selectedHere = index >= 0 && index < list.Count && g.Contains(list[index]);
                var press = Ui.ClickOf(c);
                menu.AddOption(label, () => { Log.Info("option: " + label); press(); });
                var option = list[list.Count - 1];
                list.RemoveAt(list.Count - 1);
                foreach (var o in g) list.Remove(o);
                list.Insert(Math.Min(at, list.Count), option);
                s_merged.Add(option, c);
                if (selectedHere) MenuEdit.SetIndex(menu, list.IndexOf(option));
                MenuEdit.ClampIndex(menu);
                Log.Once("main menu: " + g.Count + " options for one box button became '" + label + "'");
            }
        }

        // The Box button an HSA option presses: a Box field its action reads
        static Component PressedBy(object option, Box box)
        {
            Component mergedInto;
            if (s_merged.TryGetValue(option, out mergedInto)) return mergedInto;
            foreach (var name in MenuEdit.ActionNames(option))
            {
                string[] fields;
                if (!s_pressed.TryGetValue(name, out fields)) continue;
                foreach (var f in fields)
                {
                    Component c = null;
                    try { c = Ui.Resolve(Ref.Get(box, f)); } catch { }
                    if (c != null && c && Ui.IsShown(c)) return c;
                }
            }
            return null;
        }

        // HSA's help / game menu / social entries are only pointers to their shortcuts; they are
        // recognised by the action they run (any game language), not by their text
        static readonly HashSet<string> HelpActions = new HashSet<string> { "OnClickHelp", "OnClickGameMenu", "OnClickSocialMenu" };

        static void RemoveHelpEntries(AccessibleMenu menu)
        {
            var list = MenuEdit.List(menu);
            if (list == null) return;
            var drop = new List<object>();
            foreach (var o in list)
                if (o != null) foreach (var n in MenuEdit.ActionNames(o)) if (HelpActions.Contains(n)) { drop.Add(o); break; }
            if (drop.Count > 0) MenuEdit.Remove(menu, drop);
        }

        // Box / ribbon fields whose button HSA's own hub options already stand for (Play,
        // Game Modes, Arena, Battlegrounds, Collection, Open Packs, Journal, Shop); skipped by
        // identity, so no duplicate appears whatever the game's language
        static readonly string[] CoveredByHsa =
        {
            "m_PlayButton", "m_BattleGroundsButton", "m_GameModesButton", "m_ArenaButton",
            "m_CollectionButton", "m_OpenPacksButton", "m_StoreButton", "m_QuestLogButton",
            "m_journalButton", "m_journalButtonWidget", "m_questLogRibbon", "m_storeRibbon",
            "m_legacyQuestButtonGameObject",
            // the ribbon's list holds its buttons again; each one is reached through its own field
            "m_Ribbons",
        };

        // Every button the box and its ribbon show; only those whose text HSA's own options
        // already say are left out (no list of fields HSA "handles": its code hides some, e.g. the shop)
        static List<GameButton> Discover(AccessibleMenu menu, ExtraOptions extra)
        {
            var box = Box.Get();
            var bm = Hearthstone.BlackMarket.BlackMarketEventManager.Get();
            // (the reply itself is never read here: Network.Get...Response() takes it off the game's queue)
            string status = Ref.Get<bool>(bm, "m_isFetchingPlayerState") ? "asking the server" : "server answered";
            if (bm != null) Log.Once("Black Market: " + status + ", accessible " + bm.IsBlackMarketAccessible + ", event " + (bm.CurrentBlackMarketEvent == null ? "none" : bm.CurrentBlackMarketEvent.ID.ToString())
                                     + ", button shown by the game " + Ui.IsShown(Ui.Resolve(Ref.Get(box, "m_blackMarketButtonController"))));
            var found = Ui.ButtonsIn(box, CoveredByHsa, v => v is RibbonButtonsUI);
            var existing = extra.OwnLabels(menu);
            var result = new List<GameButton>();
            foreach (var b in found)
            {
                if (Labels.SimilarToAny(existing, b.Label)) continue;
                existing.Add(b.Label);
                result.Add(b);
            }
            return result;
        }
    }

    // Buttons such as the Black Market one load and appear after the hub menu is built;
    // this keeps the menu up to date while the hub has focus.
    class HubWatcher : MonoBehaviour
    {
        internal AccessibleComponent Hub;
        float m_next;

        void Update()
        {
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 1f;
            try
            {
                if (Hub == null || !AccessibilityMgr.IsCurrentlyFocused(Hub)) return;
                var menu = Ref.Get(Hub, "m_mainMenu") as AccessibleMenu;
                if (menu != null) HubMenu.Refresh(menu, false);
            }
            catch (Exception e) { Log.Error(e); enabled = false; }
        }
    }
}
