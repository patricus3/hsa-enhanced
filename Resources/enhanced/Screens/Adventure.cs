using System;
using System.Collections;
using System.Collections.Generic;
using Hearthstone.DataModels;
using Hearthstone.UI;
using HSAEnhanced.Core;
using PegasusShared;
using UnityEngine;

namespace HSAEnhanced
{
    // Solo Adventures on our core, from the game's adventure data:
    //   the chooser: every adventure and mode the game offers now (locked ones say why); Enter
    //     chooses it as clicking it and pressing Choose does
    //   a book page (Book of Heroes, Descent of Dragons, ...): the chapters with their state, or a
    //     chapter's missions; Enter opens the chapter / starts the mission
    //   the older adventures' mission display: every boss of every wing with its state; Enter starts
    //     it (the deck picker when it needs a deck)
    // The deck and opponent pickers are the deck tray's (DeckTray); anything else the generic reader's.
    static class Adventure
    {
        static AdventureUI s_ui;
        static float s_nextTry;     // a sub-screen not ours is looked at again twice a second

        internal static bool InAdventure
        {
            get
            {
                var scenes = SceneMgr.Get();
                return scenes != null && scenes.GetMode() == SceneMgr.Mode.ADVENTURE && !scenes.IsTransitioning() && scenes.IsSceneLoaded()
                    && GameState.Get() == null && AdventureConfig.Get() != null && DeckTray.Shown() == null;
            }
        }

        // every frame
        internal static void Tick()
        {
            if (s_ui != null && !s_ui.Alive) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && InAdventure && Time.unscaledTime >= s_nextTry)
            {
                s_nextTry = Time.unscaledTime + 0.5f;
                var ui = new AdventureUI();
                if (!ui.Refresh(true)) return;     // nothing of ours on this sub-screen (yet)
                s_ui = ui;
                Generic.Yield();
                Focus.PushBase(ui);
                if (ui.Focused) ui.Read();
            }
            if (s_ui != null) s_ui.Refresh(false);
        }
    }

    class AdventureUI : Core.Screen
    {
        Menu m_menu;
        string m_key, m_signature;
        float m_next;

        internal override bool Alive { get { return Adventure.InAdventure && m_key != null; } }

        // true when this sub-screen is one of ours and has something to offer
        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_menu != null;
            m_next = Time.unscaledTime + 0.5f;
            string key, title; List<GameButton> items;
            if (!Build(out key, out title, out items) || items.Count == 0)
            {
                if (key == null) m_key = null;     // a sub-screen that is not ours: the generic reader's
                return m_menu != null && m_key != null;
            }
            var sig = key + "\n" + title + "\n" + string.Join("\n", items.ConvertAll(b => b.Label).ToArray());
            bool newStep = key != m_key;
            if (!newStep && sig == m_signature) return true;
            var keep = m_menu == null || newStep ? null : m_menu.KeyAt(m_menu.Index);
            var at = m_menu == null || newStep ? 0 : m_menu.Index;
            var menu = new Menu(this, title, Back);
            foreach (var b in items) { var click = b.Click; menu.AddOption(b.Label, () => click(), b.Label); }
            var k = menu.IndexOfKey(keep);
            menu.Index = k >= 0 ? k : at;
            m_menu = menu;
            m_signature = sig;
            m_key = key;
            if (newStep)
            {
                Log.Info("adventure: " + key + " '" + title + "': " + GameButton.Describe(items));
                if (Focused) m_menu.StartReading();
            }
            return true;
        }

        static bool Build(out string key, out string title, out List<GameButton> items)
        {
            key = null; title = ""; items = new List<GameButton>();
            var config = AdventureConfig.Get();
            var sub = config.CurrentSubScene;
            if (sub == Assets.AdventureData.Adventuresubscene.CHOOSER) return Chooser(ref key, ref title, items);
            var page = BookPage();
            if (page != null) return Book(page, ref key, ref title, items);
            if (sub == Assets.AdventureData.Adventuresubscene.MISSION_DISPLAY) return Wings(ref key, ref title, items);
            return false;
        }

        // ---- the chooser ----------------------------------------------------------------------

        static bool Chooser(ref string key, ref string title, List<GameButton> items)
        {
            var scene = AdventureScene.Get();
            var all = scene == null ? null : scene.GetSortedAdventureDefs();
            if (all == null) return false;
            var adventures = new List<AdventureDef>();
            foreach (var d in all) if (d != null && d.IsActiveAndPlayable()) adventures.Add(d);
            adventures.Sort((a, b) => a.GetSortOrder().CompareTo(b.GetSortOrder()));
            key = "chooser";
            title = Str.Word("GLOBAL_ADVENTURE_TRAY_HEADER");
            var config = AdventureConfig.Get();
            foreach (var def in adventures)
            {
                var adventure = def.GetAdventureId();
                var name = Str.Clean(def.GetAdventureName());
                var modes = def.GetSortedSubDefs();
                if (modes == null) continue;
                foreach (var sub in modes)
                {
                    if (sub == null) continue;
                    var mode = sub.GetAdventureModeId();
                    bool playable = true;
                    try { playable = AdventureConfig.CanPlayMode(adventure, mode, true); } catch { }
                    string label = playable
                        ? Str.Join(name, Str.Clean(First(sub.GetShortName(), sub.GetDescription())))
                        : Str.Join(name, Str.Clean(First(sub.GetLockedShortName(), sub.GetShortName())), Str.Locked,
                                   Str.Clean(First(sub.GetRequirementsDescription(), sub.GetLockedDescription())));
                    items.Add(new GameButton { Target = scene, Label = label, Click = () => Choose(adventure, mode) });
                }
            }
            return true;
        }

        static string First(string a, string b) { return string.IsNullOrEmpty(Str.Clean(a)) ? b : a; }

        // What clicking the mode and then Choose does. The game saves the selection first; pressing
        // Choose while that save is pending leaves the chooser stuck, so Choose waits for it.
        static void Choose(AdventureDbId adventure, AdventureModeDbId mode)
        {
            var config = AdventureConfig.Get();
            if (config == null) return;
            if (config.GetSelectedAdventure() != adventure || config.GetSelectedMode() != mode) config.SetSelectedAdventureMode(adventure, mode);
            Jobs.Run(PressChooseWhenSaved());
        }

        static IEnumerator PressChooseWhenSaved()
        {
            var until = Time.unscaledTime + 5f;
            while (Time.unscaledTime < until && SavePending()) yield return null;
            yield return new WaitForSecondsRealtime(0.2f);
            var trays = UnityEngine.Object.FindObjectsByType<AdventureChooserTray>(FindObjectsSortMode.None);
            var tray = trays.Length == 0 ? null : trays[0];
            var choose = tray == null ? null : tray.GetComponentInChildren<PlayButton>(false);
            if (choose == null) { Log.Info("adventure: no Choose button"); yield break; }
            if (choose.IsEnabled()) { Log.Info("adventure: Choose"); choose.TriggerRelease(); }
            else Speech.Say(Ui.LabelOf(choose));
        }

        static bool SavePending()
        {
            var mgr = GameSaveDataManager.Get();
            var pending = mgr == null ? null : Ref.Get(mgr, "m_isRequestPendingForKey") as IDictionary;
            if (pending == null) return false;
            foreach (DictionaryEntry e in pending) if (e.Value is bool && (bool)e.Value) return true;
            return false;
        }

        // ---- book pages -----------------------------------------------------------------------

        static AdventureBookPageDisplay s_page;

        static AdventureBookPageDataModel BookPage()
        {
            s_page = null;
            foreach (var d in UnityEngine.Object.FindObjectsByType<AdventureBookPageDisplay>(FindObjectsSortMode.None))
            {
                var model = d == null || !d.isActiveAndEnabled ? null : Ref.Get<AdventureBookPageDataModel>(d, "m_pageDataModel");
                if (model != null) { s_page = d; return model; }
            }
            return null;
        }

        static bool Book(AdventureBookPageDataModel page, ref string key, ref string title, List<GameButton> items)
        {
            var display = s_page;
            if (page.PageType == AdventureBookPageType.MAP)
            {
                key = "book:map";
                title = Str.Clean(AdventureName());
                var map = Ref.Get(display, "m_chapterButtonClickablesNameMap") as IEnumerable;
                if (map == null) return false;
                foreach (var entry in map)
                {
                    var t = entry.GetType();
                    var name = t.GetProperty("Key") == null ? null : t.GetProperty("Key").GetValue(entry, null) as string;
                    var button = t.GetProperty("Value") == null ? null : t.GetProperty("Value").GetValue(entry, null) as Clickable;
                    if (string.IsNullOrEmpty(name) || button == null || !button) continue;
                    var chapter = ChapterNamed(page, name);
                    var label = Str.Join(Str.Clean(name), chapter == null ? null : ChapterState(chapter));
                    var b = button;
                    items.Add(new GameButton { Target = b, Label = label, Click = () => { Log.Info("adventure: chapter " + name); Ui.Press(b); } });
                }
                return true;
            }
            if (page.PageType == AdventureBookPageType.CHAPTER && page.ChapterData != null && page.ChapterData.Missions != null)
            {
                key = "book:chapter:" + page.ChapterData.Name;
                title = Str.Clean(page.ChapterData.Name);
                if (!string.IsNullOrEmpty(page.ChapterData.Description)) title = Str.Join(title, Str.Clean(page.ChapterData.Description));
                int n = 1;
                foreach (var m in page.ChapterData.Missions)
                {
                    if (m == null) continue;
                    var mission = m;
                    var state = mission.MissionState.ToString();
                    var label = Str.Join(n + ". " + ScenarioName(mission.ScenarioId), state == "LOCKED" ? Str.Locked : state == "COMPLETED" ? Str.Completed : null);
                    items.Add(new GameButton { Target = display, Label = label, Click = () =>
                    {
                        if (mission.MissionState.ToString() == "LOCKED") { Speech.Say(Str.Locked); return; }
                        Play(mission.ScenarioId);
                    } });
                    n++;
                }
                return true;
            }
            return false;
        }

        static string AdventureName()
        {
            var config = AdventureConfig.Get();
            var record = config == null ? null : GameDbf.Adventure.GetRecord((int)config.GetSelectedAdventure());
            return record == null || record.Name == null ? "" : record.Name.GetString();
        }

        static AdventureChapterDataModel ChapterNamed(AdventureBookPageDataModel page, string name)
        {
            if (page.AllChaptersData == null) return null;
            foreach (var c in page.AllChaptersData)
                if (c != null && Labels.Norm(c.Name) == Labels.Norm(name)) return c;
            return null;
        }

        static string ChapterState(AdventureChapterDataModel c)
        {
            if (c.TimeLocked) return Str.Join(Str.Locked, Str.Clean(c.TimeLockInfoMessage));
            if (!c.PlayerOwnsChapter) return Str.NotOwned;
            switch (c.ChapterState.ToString())
            {
                case "LOCKED": return Str.Join(Str.Locked, Str.Clean(c.UnlockChapterText));
                case "COMPLETED": return Str.Completed;
            }
            return null;
        }

        static string ScenarioName(ScenarioDbId id)
        {
            try
            {
                var record = GameDbf.Scenario.GetRecord((int)id);
                if (record != null)
                {
                    var name = record.Name == null ? null : record.Name.GetString();
                    if (string.IsNullOrEmpty(name) && record.OpponentName != null) name = record.OpponentName.GetString();
                    if (!string.IsNullOrEmpty(name)) return Str.Clean(name);
                }
            }
            catch (Exception e) { Log.Error(e); }
            return id.ToString();
        }

        // Starts a mission the way the game's play button does: the deck (or hero) picker when the
        // mission needs a deck, else the game against the AI
        static void Play(ScenarioDbId id)
        {
            var config = AdventureConfig.Get();
            Log.Info("adventure: mission " + id);
            config.SetMission(id, true);
            if (AdventureConfig.DoesMissionRequireDeck(id))
            {
                var subscene = !GameUtils.DoesAdventureModeUseDungeonCrawlFormat(config.GetSelectedMode()) || config.IsHeroSelectedBeforeDungeonCrawlScreenForSelectedAdventure()
                    ? config.SubSceneForPickingHeroForCurrentAdventure() : Assets.AdventureData.Adventuresubscene.DUNGEON_CRAWL;
                config.ChangeSubScene(subscene, true);
            }
            else GameMgr.Get().FindGame(GameType.GT_VS_AI, FormatType.FT_WILD, (int)id, 0, 0L, null, null, false, null, null, 0L, GameType.GT_UNKNOWN, 0);
        }

        // ---- the older adventures' wings ------------------------------------------------------

        static bool Wings(ref string key, ref string title, List<GameButton> items)
        {
            var wings = UnityEngine.Object.FindObjectsByType<AdventureWing>(FindObjectsSortMode.None);
            if (wings.Length == 0) return false;
            key = "wings";
            title = Str.Clean(AdventureName());
            var list = new List<AdventureWing>(wings);
            list.Sort((a, b) => Ui.ScreenOrderOf(a).CompareTo(Ui.ScreenOrderOf(b)));
            foreach (var wing in list)
            {
                var bosses = Ref.Get<IList>(wing, "m_BossCoins");
                if (bosses == null) continue;
                foreach (var boss in bosses)
                {
                    var coin = Ref.Get<AdventureBossCoin>(boss, "m_Coin");
                    var idObj = Ref.Get(boss, "m_MissionId");
                    if (coin == null || idObj == null) continue;
                    var id = (ScenarioDbId)Convert.ToInt32(idObj);
                    var why = Missions.WhyNot(coin);
                    var label = Str.Join(ScenarioName(id), why);
                    items.Add(new GameButton { Target = coin, Label = label, Click = () =>
                    {
                        if (why != null) { Speech.Say(why); return; }
                        Play(id);
                    } });
                }
            }
            return true;
        }

        // ---- back -----------------------------------------------------------------------------

        // the adventure's own sub-screen stack first, then the game's back (to Game Modes)
        static void Back()
        {
            var config = AdventureConfig.Get();
            if (config != null && config.CurrentSubScene != Assets.AdventureData.Adventuresubscene.CHOOSER)
            {
                Log.Info("adventure: back from " + config.CurrentSubScene);
                config.SubSceneGoBack(true);
                return;
            }
            Log.Info("adventure: back");
            Navigation.GoBack();
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
