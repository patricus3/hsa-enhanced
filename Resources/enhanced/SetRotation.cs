using System;
using System.Collections.Generic;
using Hearthstone.UI;
using UnityEngine;

namespace HSAEnhanced
{
    // The new year's set rotation intro ("Year of the ..."), as a sighted player gets it. HSA turns
    // it off (ShouldShowSetRotationIntro / HasSeenStandardModeTutorial / the box's rotation button);
    // the port keeps the game's own versions (tools/port/Hunks.cs), and each step is read here:
    //   the box: its rotation button is in the main menu with everything else the box shows (HubMenu)
    //   the clock: the Standard / year banners and the button banner, each waiting for a click
    //   the play screen: the switch-format walkthrough (the format picker is read by FormatPickerOpened)
    // Every text the step shows is an option, and its button (or Continue for the clock) presses it.
    static class SetRotation
    {
        static SetRotationUI s_ui;

        // from FallbackWatcher, twice a second
        internal static void Tick()
        {
            if (!true || GameState.Get() != null) { Hide(); return; }
            string key, title; List<GameButton> items;
            Current(out key, out title, out items);
            if (key == null) { Hide(); return; }
            if (s_ui != null && s_ui.Key == key) { s_ui.Update(items); return; }
            Hide();
            Log.Info("set rotation: " + key + ": " + title + " | " + GameButton.Describe(items));
            s_ui = new SetRotationUI(key, title, items);
            Core.Focus.Push(s_ui);
            s_ui.Start();
        }

        // an intro step is up (the fallback menu leaves it to us)
        internal static bool Active { get { return s_ui != null; } }

        // the intro is under way on the play screen (the clock, the tutorial): the game has its
        // back button disabled, and leaving would cut the intro off
        internal static bool Running
        {
            get
            {
                var clock = SetRotationClock.Get();
                if (clock != null && clock && clock.gameObject.activeInHierarchy) return true;
                var tray = DeckPickerTrayDisplay.Get();
                var state = tray == null || !tray ? null : Ref.Get(tray, "m_setRotationTutorialState");
                return state != null && state.ToString() != "INACTIVE";
            }
        }

        static void Hide()
        {
            if (s_ui == null) return;
            var ui = s_ui;
            s_ui = null;
            Core.Focus.Pop(ui);
        }

        static void Current(out string key, out string title, out List<GameButton> items)
        {
            key = null; title = null; items = new List<GameButton>();
            if (Clock(ref key, ref title, items)) return;
            SwitchFormat(ref key, ref title, items);
        }

        // the clock waits for a click on its click catcher at each banner
        static bool Clock(ref string key, ref string title, List<GameButton> items)
        {
            var clock = SetRotationClock.Get();
            if (clock == null || !clock || !clock.gameObject.activeInHierarchy) return false;
            var catcher = clock.m_clickCatcher;
            if (catcher == null || !catcher.gameObject.activeInHierarchy) return false;
            // the texts on show: the overlay's banner and details, else the button's banner
            var overlay = clock.m_overlayText == null ? null : clock.m_overlayText.gameObject;
            GameObject shown = overlay != null && overlay.activeInHierarchy ? overlay
                             : clock.m_ButtonBanner != null && clock.m_ButtonBanner.activeInHierarchy ? clock.m_ButtonBanner : null;
            var texts = Ui.TextsUnder(shown);
            if (texts.Count == 0) return false;     // still coming in
            var lines = new List<string>();
            foreach (var t in texts) lines.Add(t.Value);
            key = "clock:" + (shown == overlay ? "overlay:" : "banner:") + string.Join("|", lines.ToArray());
            title = lines[0];
            for (int i = 1; i < lines.Count; i++) AddText(items, texts[i].Key, lines[i]);
            var c = clock;
            items.Add(new GameButton
            {
                Target = catcher, Label = Str.Word("GLOBAL_CONTINUE"),
                Click = () => { Log.Info("set rotation: clock continues"); Ref.Set(c, "m_clickCaptured", true); }
            });
            return true;
        }

        // the play screen asks for the format switch (its popup says so, the button glows)
        static bool SwitchFormat(ref string key, ref string title, List<GameButton> items)
        {
            if (SceneMgr.Get() == null || SceneMgr.Get().GetMode() != SceneMgr.Mode.TOURNAMENT) return false;
            var tray = DeckPickerTrayDisplay.Get();
            if (tray == null || !tray) return false;
            var state = Ref.Get(tray, "m_setRotationTutorialState");
            if (state == null || state.ToString() != "SWITCH_MODE_WALKTHROUGH") return false;
            if (tray.IsModeSwitchShowing) return false;     // the format picker is up
            var button = Ref.Get<Component>(tray, "m_switchFormatButton");
            if (button == null || !button || !Ui.IsShown(button)) return false;
            var popup = Ref.Get<Component>(tray, "m_switchFormatPopup");
            var said = Str.Game("GLUE_TOURNAMENT_SWITCH_MODE");
            if (popup != null && popup)
                foreach (var t in Ui.TextsUnder(popup.gameObject)) { said = t.Value; break; }
            var label = Ui.LabelOf(button);
            if (label.Length == 0) label = Str.Word("GLUE_TOURNAMENT_SWITCH_MODE");
            key = "switch:" + label;
            title = said;
            items.Add(new GameButton { Target = button, Label = label, Click = Ui.ClickOf(button) });
            return true;
        }

        static void AddText(List<GameButton> items, Component target, string text)
        {
            items.Add(new GameButton { Target = target, Label = text, Click = () => { if (s_ui != null) s_ui.Say(text); } });
        }
    }

    // One intro step: its title (the headline) is said first, then its texts and its button
    class SetRotationUI : Core.Screen
    {
        internal readonly string Key;
        readonly Core.Menu m_menu;
        string m_signature;

        internal SetRotationUI(string key, string title, List<GameButton> items)
        {
            Key = key;
            // said on opening: the headline and every text of the step (the options repeat them one by one)
            var said = new List<string> { title };
            foreach (var b in items) if (b.Label != title && !(b.Target is PegUIElement) && !(b.Target is Clickable)) said.Add(b.Label);
            m_menu = new Core.Menu(this, Str.Join(said.ToArray()), null);
            Fill(items);
        }

        void Fill(List<GameButton> items)
        {
            var sig = new System.Text.StringBuilder();
            foreach (var b in items)
            {
                var press = b.Click;
                var label = b.Label;
                m_menu.AddOption(label, () => press());
                sig.Append(label).Append('\n');
            }
            m_signature = sig.ToString();
            // the button (last) is where the cursor starts: Enter does what the step waits for
            m_menu.Index = m_menu.Count - 1;
        }

        internal void Update(List<GameButton> items)
        {
            var sig = new System.Text.StringBuilder();
            foreach (var b in items) sig.Append(b.Label).Append('\n');
            if (sig.ToString() == m_signature) return;
            m_menu.Clear();
            Fill(items);
        }

        internal void Start() { m_menu.StartReading(); }

        internal override bool HandleKey() { return m_menu.HandleKey(); }

        internal override string Help() { return m_menu.GetHelp(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
