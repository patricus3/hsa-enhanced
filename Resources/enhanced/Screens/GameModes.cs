using System;
using Hearthstone.DataModels;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // The game modes screen (Game Modes on the main menu): every mode the game offers, in its order,
    // named as its button names it, with what the button shows (new, early access, beta, download).
    // Enter goes into the mode the way choosing it and pressing the game's Choose button does (a mode
    // that cannot be entered gets the game's own popup saying why). Backspace: the game's back.
    static class GameModes
    {
        static GameModesUI s_ui;

        internal static bool InGameModes
        {
            get
            {
                var scenes = SceneMgr.Get();
                if (scenes == null || scenes.GetMode() != SceneMgr.Mode.GAME_MODE || scenes.IsTransitioning() || !scenes.IsSceneLoaded() || GameState.Get() != null) return false;
                var display = GameModeDisplay.Get();
                return display != null && display.IsFinishedLoading;
            }
        }

        // every frame
        internal static void Tick()
        {
            if (s_ui != null && !s_ui.Alive) s_ui = null;
            if (s_ui == null && InGameModes)
            {
                var ui = new GameModesUI();
                if (!ui.Refresh(true)) return;     // the modes are not there yet
                s_ui = ui;
                Generic.Yield();
                Focus.PushBase(ui);
                if (ui.Focused) ui.Read();
            }
            if (s_ui != null) s_ui.Refresh(false);
        }
    }

    class GameModesUI : Core.Screen
    {
        Menu m_menu;
        float m_next;
        string m_signature;

        internal override bool Alive { get { return GameModes.InGameModes; } }

        static GameModeSceneDataModel Model()
        {
            var display = GameModeDisplay.Get();
            return display == null ? null : display.GetGameModeSceneDataModel();
        }

        static string Flags(GameModeButtonDataModel b)
        {
            return Str.Join(
                b.IsNew ? Str.Word("GLUE_COLLECTION_CARD_NEW") : null,
                b.IsEarlyAccess ? Str.Word("GLUE_GAME_MODES_POPUP_EARLY_ACCESS") : null,
                b.IsBeta ? Str.Word("GLUE_GAME_MODES_POPUP_BETA") : null,
                b.IsDownloading ? Str.Word("GLUE_TOOLTIP_DOWNLOAD_HEADER") :
                    b.IsDownloadRequired ? Str.Word("GLUE_GAME_MODE_TOOLTIP_DOWNLOAD_REQUIRED_TITLE") : null);
        }

        // true when there is something to offer
        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_menu != null;
            m_next = Time.unscaledTime + 1f;
            var model = Model();
            if (model == null || model.GameModeButtons == null || model.GameModeButtons.Count == 0) return m_menu != null;
            var sig = new System.Text.StringBuilder();
            foreach (var b in model.GameModeButtons) if (b != null) sig.Append(b.GameModeRecordId).Append(b.Name).Append(Flags(b)).Append('\n');
            if (m_menu != null && sig.ToString() == m_signature) return true;
            m_signature = sig.ToString();
            var key = m_menu == null ? null : m_menu.KeyAt(m_menu.Index);
            var menu = new Menu(this, Str.Word("GLUE_GAME_MODES_POPUP_HEADER"), Back);
            int last = -1;
            foreach (var button in model.GameModeButtons)
            {
                var b = button;
                if (b == null) continue;
                var name = Str.Clean(b.Name);
                if (name.Length == 0) continue;
                menu.AddOption(Str.Join(name, Flags(b)), () => Choose(b), b.GameModeRecordId);
                if (b.GameModeRecordId == model.LastSelectedGameModeRecordId) last = menu.Count - 1;
            }
            var k = menu.IndexOfKey(key);
            menu.Index = k >= 0 ? k : Math.Max(0, last);
            m_menu = menu;
            Log.Once("game modes: " + m_signature.Replace("\n", " | "));
            return true;
        }

        // as choosing the mode and pressing Choose: the game checks it can be entered (and says why not)
        static void Choose(GameModeButtonDataModel b)
        {
            var display = GameModeDisplay.Get();
            if (display == null) return;
            Log.Info("game modes: " + b.Name);
            Ref.Set(display, "m_selectedGameModeButtonDataModel", b);
            Ref.Call(display, "NavigateToSelectedMode");
        }

        static void Back()
        {
            Log.Info("game modes: back");
            Navigation.GoBack();
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
