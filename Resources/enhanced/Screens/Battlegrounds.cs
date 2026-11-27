using System;
using System.Collections;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Battlegrounds' lobby, from the game's own data and options: rating, first places, solo / duos,
    // Play, Switch to duos or solo, the collection, the shop. A match is the match screen's, with
    // BgPlay's Battlegrounds keys (Screens/BgPlay.cs).
    static class Battlegrounds
    {
        static BgLobbyUI s_lobby;

        internal static bool InLobby
        {
            get
            {
                var scenes = SceneMgr.Get();
                if (scenes == null || scenes.GetMode() != SceneMgr.Mode.BACON || scenes.IsTransitioning() || !scenes.IsSceneLoaded() || GameState.Get() != null) return false;
                var display = BaconDisplay.Get();
                string why;
                return display != null && display.IsFinishedLoading(out why);
            }
        }

        // every frame
        internal static void Tick()
        {
            if (s_lobby != null && !s_lobby.Alive) { var old = s_lobby; s_lobby = null; Focus.Pop(old); }
            if (s_lobby == null && InLobby)
            {
                var ui = new BgLobbyUI();
                if (ui.Refresh(true)) { s_lobby = ui; Generic.Yield(); Focus.PushBase(ui); if (ui.Focused) ui.Read(); }
            }
            if (s_lobby != null) s_lobby.Refresh(false);
        }
    }

    // a menu rebuilt from the game's data when it changes, read out when the step changes
    abstract class BgUI : Core.Screen
    {
        protected Menu m_menu;
        string m_key, m_signature;
        float m_next;

        protected abstract bool Build(out string key, out string title, List<GameButton> items);
        protected abstract void Back();

        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_menu != null;
            m_next = Time.unscaledTime + 0.3f;
            string key, title; var items = new List<GameButton>();
            if (!Build(out key, out title, items)) return m_menu != null;
            var sig = key + "\n" + title + "\n" + string.Join("\n", items.ConvertAll(b => b.Label).ToArray());
            bool newStep = key != m_key;
            if (!newStep && sig == m_signature) return true;
            var at = m_menu == null || newStep ? 0 : m_menu.Index;
            var menu = new Menu(this, title, Back);
            foreach (var b in items) { var click = b.Click; menu.AddOption(b.Label, () => click()); }
            menu.Index = at;
            m_menu = menu;
            m_signature = sig;
            m_key = key;
            if (newStep)
            {
                Log.Info("battlegrounds: " + key + " '" + title + "': " + GameButton.Describe(items));
                if (Focused) m_menu.StartReading();
            }
            return true;
        }

        protected static void Add(List<GameButton> items, Component anchor, string label, Action click)
        {
            if (string.IsNullOrEmpty(label)) return;
            var said = label;
            items.Add(new GameButton { Target = anchor, Label = label, Click = click ?? (() => Speech.Say(said)) });
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }

    // ---- the lobby ----------------------------------------------------------------------------

    class BgLobbyUI : BgUI
    {
        internal override bool Alive { get { return Battlegrounds.InLobby; } }

        protected override bool Build(out string key, out string title, List<GameButton> items)
        {
            key = null; title = "";
            var display = BaconDisplay.Get();
            var model = display == null ? null : display.GetBaconLobbyDataModel();
            if (model == null) return false;
            bool duos = model.BattlegroundsInDuosMode;
            key = "lobby:" + duos;
            title = Str.Join(SceneNames.Of(SceneMgr.Mode.BACON), duos ? Str.Word("GLUE_BACON_INFO_POPUP_DUOS_RULES") : null);
            Add(items, display, Str.Join(Str.Words(Str.Word("GLUE_BACON_RATING_LABEL"), model.Rating.ToString()), Str.Words(Str.Word("GLUE_BACON_FIRST_PLACE_LABEL"), model.FirstPlaceFinishes.ToString())), null);
            var finding = GameMgr.Get() != null && GameMgr.Get().IsFindingGame();
            if (!finding)
                Add(items, display, Str.Word("GLOBAL_PLAY"), () => { Log.Info("battlegrounds: play"); display.PlayButtonRelease(null); });
            else
                Add(items, display, Str.Word("GLOBAL_CANCEL"), () => { Log.Info("battlegrounds: cancel the queue"); GameMgr.Get().CancelFindGame(); });
            Add(items, display, Str.Clean(duos ? model.FooterTextDuos : model.FooterText), null);
            Add(items, display, Str.Word("GLUE_BACON_LOBBY_TOGGLE_TUTORIAL"), () => { Log.Info("battlegrounds: solo / duos"); display.ToggleDuosRelease(null); });
            if (model.BattlegroundsSkinsEnabled)
                Add(items, display, Str.Word("GLUE_BACON_COLLECTION"), () => { Log.Info("battlegrounds: collection"); Ref.Call(display, "OpenBattlegroundsCollection"); });
            if (model.ShopOpen)
                Add(items, display, Str.Word("GLUE_STORE_HEADLINE"), () => { Log.Info("battlegrounds: shop"); display.OpenBattlegroundsShop("battlegrounds"); });
            return true;
        }

        protected override void Back()
        {
            Log.Info("battlegrounds: back");
            var display = BaconDisplay.Get();
            if (display != null) display.BackButtonRelease(null); else Navigation.GoBack();
        }
    }
}
