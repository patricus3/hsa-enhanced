using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // The main menu: every button the box and its ribbon show, named as the game names them, in
    // the order the box shows them (top to bottom, left to right). Kept current while open (the
    // Black Market and lucky draw buttons appear late), on the same option.
    static class Hub
    {
        static HubUI s_ui;

        internal static bool InHub
        {
            get
            {
                var scenes = SceneMgr.Get();
                return scenes != null && scenes.GetMode() == SceneMgr.Mode.HUB && !scenes.IsTransitioning() && scenes.IsSceneLoaded()
                    && Box.Get() != null && GameState.Get() == null;
            }
        }

        // every frame
        internal static void Tick()
        {
            if (s_ui != null && !s_ui.Alive) s_ui = null;
            if (s_ui == null && InHub)
            {
                var ui = new HubUI();
                if (!ui.Refresh(true)) return;     // the box is still opening
                s_ui = ui;
#if WITHOUT_HSA
                Generic.Yield();
#endif
                Focus.PushBase(ui);
                if (ui.Focused) ui.Read();
            }
            if (s_ui != null) s_ui.Refresh(false);
        }
    }

    class HubUI : Core.Screen
    {
        Menu m_menu;
        float m_next;
        string m_signature;

        internal override bool Alive { get { return Hub.InHub; } }

        static List<GameButton> Buttons()
        {
            var found = Ui.ButtonsIn(Box.Get(), new[] { "m_Ribbons" }, v => v is RibbonButtonsUI);
            var unique = new List<GameButton>();
            var labels = new List<string>();
            foreach (var b in found)
            {
                if (Labels.SimilarToAny(labels, b.Label)) continue;
                labels.Add(b.Label);
                unique.Add(b);
            }
            Ui.SortByScreen(unique);
            return unique;
        }

        // true when there is something to offer
        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_menu != null;
            m_next = Time.unscaledTime + 1f;
            List<GameButton> buttons;
            try { buttons = Buttons(); } catch (Exception e) { Log.Error(e); return m_menu != null; }
            if (buttons.Count == 0) return m_menu != null;
            var sig = new System.Text.StringBuilder();
            foreach (var b in buttons) sig.Append(b.Label).Append('\n');
            try { sig.Append(ShopUI.Balances()); } catch { }
            if (m_menu != null && sig.ToString() == m_signature) return true;
            m_signature = sig.ToString();
            var key = m_menu == null ? null : m_menu.KeyAt(m_menu.Index);
            var at = m_menu == null ? 0 : m_menu.Index;
            var menu = new Menu(this, SceneNames.Of(SceneMgr.Mode.HUB));
            foreach (var b in buttons)
            {
                var button = b;
                menu.AddOption(button.Label, () => { Log.Info("main menu: " + button.Label); button.Click(); }, button.Label);
            }
            // what you have (gold, runestones), last
            string have = null;
            try { have = ShopUI.Balances(); } catch (Exception e) { Log.Error(e); }
            if (!string.IsNullOrEmpty(have)) menu.AddOption(have, () => Speech.Say(have), "balances");
            var k = menu.IndexOfKey(key);
            menu.Index = k >= 0 ? k : at;
            m_menu = menu;
            Log.Once("main menu: " + m_signature.Replace("\n", " | "));
            return true;
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
