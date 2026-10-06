using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Tavern Brawl (and Heroic Brawl, the Brawliseum), from the brawl screen's own texts and buttons:
    //   the brawl: its name, rules, when it ends, wins (and losses of a session), the reward;
    //     Play (or Choose a hero: the hero picker is the deck tray's), cancel the search, Create Deck /
    //     Edit Deck (the deck is then edited on the collection screen), Retire a session (Enter twice)
    //   the entry of a session brawl: its texts, Continue, the buy buttons (real money: Enter twice)
    //   the rewards of a finished session: open the chest, Done
    static class TavernBrawl
    {
        static TavernBrawlUI s_ui;

        internal static bool InBrawl
        {
            get
            {
                var scenes = SceneMgr.Get();
                if (scenes == null || scenes.GetMode() != SceneMgr.Mode.TAVERN_BRAWL || scenes.IsTransitioning() || !scenes.IsSceneLoaded() || GameState.Get() != null) return false;
                var display = TavernBrawlDisplay.Get();
                var mgr = TavernBrawlManager.Get();
                if (display == null || mgr == null || !mgr.IsCurrentBrawlAllDataReady) return false;
                if (display.IsInDeckEditMode()) return false;          // the collection screen's
                if (DeckTray.Shown() != null) return false;            // the hero picker
                return true;
            }
        }

        // every frame
        internal static void Tick()
        {
            if (s_ui != null && !s_ui.Alive) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && InBrawl)
            {
                var ui = new TavernBrawlUI();
                if (!ui.Refresh(true)) return;
                s_ui = ui;
                Generic.Yield();
                Focus.PushBase(ui);
                if (ui.Focused) ui.Read();
            }
            if (s_ui != null) s_ui.Refresh(false);
        }
    }

    class TavernBrawlUI : Core.Screen
    {
        Menu m_menu;
        string m_key, m_signature;
        float m_next, m_armedUntil;
        string m_armed;

        internal override bool Alive { get { return TavernBrawl.InBrawl; } }

        static TavernBrawlManager TBM { get { return TavernBrawlManager.Get(); } }

        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_menu != null;
            m_next = Time.unscaledTime + 0.4f;
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
                Log.Info("brawl: " + key + " '" + title + "': " + GameButton.Describe(items));
                if (Focused) m_menu.StartReading();
            }
            return true;
        }

        bool Build(out string key, out string title, List<GameButton> items)
        {
            key = null; title = "";
            var display = TavernBrawlDisplay.Get();
            // the rewards of a finished session
            var heroic = UnityEngine.Object.FindObjectOfType<HeroicBrawlRewardDisplay>();
            var chest = UnityEngine.Object.FindObjectOfType<ChestRewardDisplay>();
            if (heroic != null || chest != null) return Rewards(display, heroic, chest, ref key, ref title, items);
            var store = TavernBrawlStore.Get();
            if (store != null && store.gameObject.activeInHierarchy && store.IsOpen()) return Store(store, ref key, ref title, items);
            var mode = Ref.Get(display, "m_currentlyShowingMode");
            if (mode == null || Convert.ToInt32(mode) != 2) return false;
            return Brawl(display, ref key, ref title, items);
        }

        static string Text(UberText t) { return t == null || !t.gameObject.activeInHierarchy ? null : Str.Clean(Ui.ShownText(t.Text)); }

        static bool Shown(Component c) { return c != null && c.gameObject.activeInHierarchy; }

        // ---- the brawl --------------------------------------------------------------------------

        bool Brawl(TavernBrawlDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            var mission = TBM.CurrentMission();
            key = "brawl:" + (mission == null ? 0 : mission.missionId);
            title = Str.Join(Text(display.m_TavernBrawlHeadline), Text(display.m_chalkboardHeader));
            var anchor = (Component)display;
            Add(items, anchor, Text(display.m_chalkboardInfo), null);
            Add(items, anchor, Text(display.m_chalkboardEndInfo), null);
            if (display.m_numWins != null && display.m_numWins.gameObject.activeInHierarchy)
            {
                var record = Str.Join(Str.Word("GLUE_WINS"), TBM.GamesWon.ToString());
                if (TBM.IsCurrentSeasonSessionBased && mission != null)
                    record = Str.Join(Str.Word("GLUE_WINS"), TBM.GamesWon + "/" + mission.maxWins, Str.Word("GLUE_LOSSES"), TBM.GamesLost + "/" + mission.maxLosses);
                Add(items, anchor, record, null);
            }
            if (Shown(display.m_rewardChest)) Add(items, anchor, Text(display.m_rewardsText), null);

            var finding = GameMgr.Get() != null && GameMgr.Get().IsFindingGame();
            if (finding)
                Add(items, anchor, Str.Word("GLOBAL_CANCEL"), () => { Log.Info("brawl: cancel the search"); GameMgr.Get().CancelFindGame(); });
            else if (Shown(display.m_playButton) && display.m_playButton.IsEnabled())
            {
                var label = Text(display.m_playButton.m_newPlayButtonText);
                Add(items, display.m_playButton, string.IsNullOrEmpty(label) ? Str.Word("GLUE_BRAWL") : label, () => { Log.Info("brawl: play"); display.m_playButton.TriggerRelease(); });
            }
            else if (Shown(display.m_playButton) && !TBM.HasValidDeckForCurrent() && mission != null && mission.canCreateDeck && TBM.HasCreatedDeck())
                Add(items, anchor, Str.Join(Str.Word("GLUE_BRAWL"), Str.Unavailable), null);
            if (Shown(display.m_createDeckButton))
                Add(items, display.m_createDeckButton, Str.Word("GLUE_COLLECTION_NEW_DECK"), () => { Log.Info("brawl: create deck"); display.m_createDeckButton.TriggerRelease(); });
            if (Shown(display.m_editDeckButton) && display.m_editDeckButton.IsEnabled())
            {
                var label = Text(display.m_editText);
                Add(items, display.m_editDeckButton, string.IsNullOrEmpty(label) ? Str.Word("GLUE_EDIT") : label, () => { Log.Info("brawl: edit deck"); display.m_editDeckButton.TriggerRelease(); });
            }
            DeckLine(items, anchor);
            // a session's locked deck can be retired: the game's question, Enter twice
            var session = TBM.CurrentSession;
            if (TBM.IsCurrentSeasonSessionBased && session != null && session.DeckLocked && !finding)
            {
                var label = Str.Word("GLUE_TAVERN_BRAWL_RETIRE_CONFIRM_HEADER");
                Add(items, anchor, label, () => Twice("retire", Str.Join(label, Str.Game(Convert.ToInt32(TBM.CurrentSeasonBrawlMode) == 1 ? "GLUE_TAVERN_BRAWL_RETIRE_CONFIRM_DESC" : "GLUE_BRAWLISEUM_RETIRE_CONFIRM_DESC")), () =>
                {
                    Log.Info("brawl: retire");
                    Network.Get().TavernBrawlRetire();
                }));
            }
            return true;
        }

        // the brawl's deck, when it has one
        static void DeckLine(List<GameButton> items, Component anchor)
        {
            CollectionDeck deck = null;
            try { deck = TBM.CurrentDeck(); } catch { }
            if (deck == null) return;
            var names = new List<string>();
            foreach (var s in deck.GetSlots())
            {
                var def = s == null ? null : s.GetEntityDef();
                if (def != null) names.Add(Str.Join(Str.Clean(def.GetName()), s.Count > 1 ? "x" + s.Count : null));
            }
            Add(items, anchor, Str.Join(Str.Clean(deck.Name), deck.GetTotalCardCount().ToString(), string.Join("; ", names.ToArray())), null);
        }

        // ---- the entry of a session brawl ------------------------------------------------------

        bool Store(TavernBrawlStore store, ref string key, ref string title, List<GameButton> items)
        {
            var anchor = (Component)store;
            var buttons = new List<UIBButton>();
            foreach (var b in new UIBButton[] { store.m_ContinueButton, store.m_ContinueButtonPRTB, store.m_buyWithGoldButton, store.m_buyWithVCButton, store.m_buyWithMoneyButton })
                if (Shown(b)) buttons.Add(b);
            key = "store:" + buttons.Count + ":" + (buttons.Count > 0 ? buttons[0].name : "");
            title = Text(store.m_ChalkboardTitleText) ?? Str.Word("GLOBAL_TAVERN_BRAWL");
            Add(items, anchor, Text(store.m_ChalkboardDescriptionText), null);
            Add(items, anchor, Text(store.m_EndsInTextChalk) ?? Text(store.m_EndsInTextPaper), null);
            foreach (var button in buttons)
            {
                var b = button;
                var label = Str.Clean(Ui.LabelOf(b));
                if (string.IsNullOrEmpty(label)) label = b == store.m_buyWithGoldButton ? Str.Word("GLUE_SHOP_GOLD") : Str.Word("GLUE_TAVERN_BRAWL_PRERELEASE_CONTINUE");
                if (b == store.m_buyWithMoneyButton || b == store.m_buyWithVCButton)
                    Add(items, b, label, () => Twice("buy " + b.name, label, () => { Log.Info("brawl: buy " + label); b.TriggerRelease(); }));
                else Add(items, b, label, () => { Log.Info("brawl: " + label); b.TriggerRelease(); });
            }
            return true;
        }

        // ---- the rewards of a session -----------------------------------------------------------

        bool Rewards(TavernBrawlDisplay display, HeroicBrawlRewardDisplay heroic, ChestRewardDisplay chest, ref string key, ref string title, List<GameButton> items)
        {
            key = "rewards:" + (heroic != null) + ":" + (chest != null && Shown(chest.m_rewardChest));
            title = Str.Join(Str.Word("GLOBAL_TAVERN_BRAWL"), Str.Word("GLUE_WINS"), TBM.GamesWon.ToString());
            var root = (Component)heroic ?? chest;
            var rewards = new List<string>();
            var list = TBM.CurrentSessionRewards;
            if (list != null) foreach (var r in list) foreach (var s in RewardText.Describe(r)) rewards.Add(s);
            Add(items, root, Str.Join(rewards.ToArray()), null);
            foreach (var t in Ui.TextsUnder(root.gameObject)) Add(items, root, t.Value, null);
            if (chest != null && Shown(chest.m_rewardChest))
                Add(items, chest.m_rewardChest, Str.Word("GLUE_LOADINGSCREEN_OPEN_APP_STORE"), () => { Log.Info("brawl: open the chest"); chest.m_rewardChest.TriggerRelease(); });
            if (heroic != null && Shown(heroic.m_DoneButton))
                Add(items, heroic.m_DoneButton, Str.Word("GLOBAL_DONE"), () => { Log.Info("brawl: rewards done"); heroic.m_DoneButton.TriggerRelease(); });
            return true;
        }

        // spending and giving up: Enter twice within a few seconds
        void Twice(string what, string said, Action act)
        {
            if (m_armed != what || Time.unscaledTime > m_armedUntil)
            {
                m_armed = what;
                m_armedUntil = Time.unscaledTime + 6f;
                Speech.Say(Str.Join(said, Str.Word("GLOBAL_CONFIRM"), Keys.Enter.Name));
                return;
            }
            m_armed = null;
            act();
        }

        static void Add(List<GameButton> items, Component anchor, string label, Action click)
        {
            if (string.IsNullOrEmpty(label)) return;
            var said = label;
            items.Add(new GameButton { Target = anchor, Label = label, Click = click ?? (() => Speech.Say(said)) });
        }

        void Back()
        {
            Log.Info("brawl: back");
            var store = TavernBrawlStore.Get();
            if (store != null && store.IsOpen() && Shown(store.m_backButton)) { store.m_backButton.TriggerRelease(); return; }
            Navigation.GoBack();
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
