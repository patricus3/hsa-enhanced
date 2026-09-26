using System;
using System.Collections.Generic;
using Accessibility;
using UnityEngine;

namespace HSAEnhanced
{
    // The hub (main) menu: HSA builds its own options (with its extra logic for Play,
    // packs, shop, ...); every other button the game shows on the box and its ribbon is
    // added here, found by walking Box's fields, and kept in sync while the hub is open.
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
            var extra = ExtraOptions.Of(menu);
            // new game buttons go before HSA's own help / game menu / social entries
            return extra.Update(menu, Discover(menu, extra), () => MenuEdit.IndexOfText(menu, LocalizedText.HUB_HELP_OPTION), immediate);
        }

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
            var found = Ui.ButtonsIn(box, new string[0], v => v is RibbonButtonsUI);
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
