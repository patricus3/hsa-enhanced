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
        internal static void AfterSetup(object hub, AccessibleMenu menu, string hsaFields)
        {
            if (!Engine.Enabled || menu == null || Box.Get() == null) return;
            Refresh(menu, true);
            var box = Box.Get().gameObject;
            var watcher = box.GetComponent<HubWatcher>() ?? box.AddComponent<HubWatcher>();
            watcher.Hub = hub as AccessibleComponent;
        }

        // true when the menu changed
        internal static bool Refresh(AccessibleMenu menu, bool immediate)
        {
            RemoveHelpEntries(menu);
            var extra = ExtraOptions.Of(menu);
            return extra.Update(menu, Discover(menu, extra), () => -1, immediate);
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
