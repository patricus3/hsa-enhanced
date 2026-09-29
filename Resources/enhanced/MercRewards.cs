using System;
using System.Collections.Generic;
using Accessibility;
using UnityEngine;

namespace HSAEnhanced
{
    // After a Mercenaries battle the end screen shows what the mercenaries got (their experience, a
    // level up) on its own overlay, which waits for its own click; HSA does not know it, and its Enter
    // on the end screen does nothing while it is up. It is read here from the texts it shows (each
    // mercenary, "+N xp", "Level up!"), and Continue closes it the way the game does.
    static class MercRewards
    {
        static MercRewardsUI s_ui;

        // from FallbackWatcher, twice a second
        internal static void Tick()
        {
            try
            {
                MercenariesExperienceTwoScoop shown = null;
                foreach (var s in UnityEngine.Object.FindObjectsByType<MercenariesExperienceTwoScoop>(FindObjectsSortMode.None))
                    if (s != null && s.isActiveAndEnabled && Ref.Get(s, "m_onClosedCallback") != null) { shown = s; break; }
                if (shown == null) { Hide(); return; }
                if (s_ui != null && s_ui.Scoop == shown) { s_ui.Refresh(); return; }
                var texts = Texts(shown);
                if (texts.Count == 0) return;       // still loading
                Hide();
                s_ui = new MercRewardsUI(shown);
                Log.Info("mercenaries: rewards " + string.Join(" | ", texts.ToArray()));
                AccessibilityMgr.ShowUI(s_ui);
                s_ui.Start();
            }
            catch (Exception e) { Log.Error(e); Hide(); }
        }

        static void Hide()
        {
            if (s_ui == null) return;
            var ui = s_ui;
            s_ui = null;
            AccessibilityMgr.HideUI(ui);
        }

        internal static List<string> Texts(MercenariesExperienceTwoScoop scoop)
        {
            var found = new List<string>();
            foreach (var t in Ui.TextsUnder(scoop.gameObject)) found.Add(t.Value);
            return found;
        }

        internal static void Close(MercenariesExperienceTwoScoop scoop)
        {
            Log.Info("mercenaries: rewards closed");
            Hide();
            Ref.Call(scoop, "OnClosed");
        }
    }

    class MercRewardsUI : AccessibleUI
    {
        internal readonly MercenariesExperienceTwoScoop Scoop;
        AccessibleMenu m_menu;
        string m_shown;

        internal MercRewardsUI(MercenariesExperienceTwoScoop scoop)
        {
            Scoop = scoop;
            Build();
        }

        void Build()
        {
            var texts = MercRewards.Texts(Scoop);
            m_shown = string.Join("\n", texts.ToArray());
            var keep = m_menu == null ? 0 : MenuEdit.GetIndex(m_menu);
            m_menu = MenuEdit.Carry(m_menu, new AccessibleMenu(this, LocalizedText.UI_POPUP, () => MercRewards.Close(Scoop)));
            var ui = this;
            foreach (var t in texts) { var text = t; m_menu.AddOption(text, () => AccessibilityMgr.Output(ui, text)); }
            m_menu.AddOption(Str.Word("GLOBAL_CONTINUE"), () => MercRewards.Close(Scoop));
            m_menu.SetIndex(Math.Max(0, Math.Min(keep, m_menu.GetNumItems() - 1)));
        }

        // the texts fill in as the overlay animates: the menu follows (the player stays where they are)
        internal void Refresh()
        {
            var texts = MercRewards.Texts(Scoop);
            if (string.Join("\n", texts.ToArray()) == m_shown) return;
            Build();
        }

        internal void Start() { m_menu.StartReading(); }

        public void HandleAccessibleInput() { m_menu.HandleAccessibleInput(); }

        public string GetAccessibleHelp() { return m_menu.GetHelp(); }
    }
}
