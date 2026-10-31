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
    //   a book (Book of Heroes, Descent of Dragons, ...): the chapter list (every chapter, with its
    //     state; Enter turns to it), a chapter's missions (Enter starts one), the rewards page; Left /
    //     Right turn the pages as the book's arrows do, Backspace on a page goes to the chapter list
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
            // the book's pages turning: the new page is read once it lies open
            var book = Manager();
            if (book != null && book.ArePagesTurning()) { m_next = Time.unscaledTime + 0.1f; return m_menu != null && m_key != null; }
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
            if (choose.IsEnabled()) { Log.Info("adventure: Choose"); Core.Click.Peg(choose); }
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
        // The book as sighted players have it: its chapter list (the map page) and its pages, turned
        // with the book's arrows (Left / Right, Page Up / Page Down); Backspace on a chapter goes back
        // to the chapter list as the game's back does. Every adventure with a book works this way.

        static AdventureBookPageDisplay s_page;

        internal static AdventureBookPageManager Manager()
        {
            var m = AdventureBookPageManager.m_instance;
            return m != null && m.isActiveAndEnabled ? m : null;
        }

        static AdventureBookPageDataModel BookPage()
        {
            s_page = null;
            // the page the book shows now (it has two page objects; the other one is the last page)
            var mgr = Manager();
            if (mgr != null)
            {
                var current = Ref.Invoke(mgr, "GetCurrentPage") as AdventureBookPageDisplay;
                var model = current == null || !current ? null : Ref.Get<AdventureBookPageDataModel>(current, "m_pageDataModel");
                if (model != null) { s_page = current; return model; }
            }
            foreach (var d in UnityEngine.Object.FindObjectsByType<AdventureBookPageDisplay>(FindObjectsSortMode.None))
            {
                var model = d == null || !d.isActiveAndEnabled ? null : Ref.Get<AdventureBookPageDataModel>(d, "m_pageDataModel");
                if (model != null) { s_page = d; return model; }
            }
            return null;
        }

        static List<PageNode> Nodes(AdventureBookPageManager mgr)
        {
            return mgr == null ? null : Ref.Get<List<PageNode>>(mgr, "m_pageNodes");
        }

        static bool HasMap(AdventureBookPageManager mgr) { return mgr != null && Ref.Get<int>(mgr, "m_mapPageNumber") > 0; }

        // the chapters in book order, with their page numbers
        static List<KeyValuePair<int, ChapterPageData>> Chapters(AdventureBookPageManager mgr)
        {
            var list = new List<KeyValuePair<int, ChapterPageData>>();
            var nodes = Nodes(mgr);
            if (nodes == null) return list;
            for (int i = 0; i < nodes.Count; i++)
            {
                var c = nodes[i] == null ? null : nodes[i].PageData as ChapterPageData;
                if (c != null && c.WingRecord != null) list.Add(new KeyValuePair<int, ChapterPageData>(i + 1, c));
            }
            return list;
        }

        // a book with two campaigns (Galakrond's Awakening: the Explorers' and E.V.I.L.'s): which one
        static string Section(AdventureBookPageManager mgr, int section)
        {
            var chapters = Chapters(mgr);
            if (section < 0 || chapters.Count == 0 || !chapters.Exists(c => c.Value.BookSection != chapters[0].Value.BookSection)) return null;
            if (chapters[0].Value.Adventure == AdventureDbId.DRAGONS)
                return Str.Word(section == 1 ? "GLUE_ADVENTURE_CARDBACKPAGE_DRG_EVILCARD_NAME_EVIL" : "GLUE_ADVENTURE_CARDBACKPAGE_DRG_EVILCARD_NAME_GOOD");
            return (section + 1).ToString();
        }

        static string Loc(DbfLocValue v)
        {
            try { return v == null ? null : Str.Clean(v.GetString()); } catch { return null; }
        }

        // the chapter's place in its campaign (1 = its first chapter)
        static int Position(AdventureBookPageManager mgr, ChapterPageData c)
        {
            int n = 0;
            foreach (var entry in Chapters(mgr))
            {
                if (entry.Value.BookSection != c.BookSection) continue;
                n++;
                if (entry.Value == c) return n;
            }
            return Math.Max(1, c.ChapterNumber);
        }

        // Galakrond's Awakening: the chapter records have no names; the book shows the game's chapter titles
        // ("I: Battle at the Frozen Coast") and each campaign's own text for the chapter
        static string ChapterName(AdventureBookPageManager mgr, ChapterPageData c)
        {
            var name = Loc(c.WingRecord.Name);
            if (string.IsNullOrEmpty(name)) name = Loc(c.WingRecord.NameShort);
            if (!string.IsNullOrEmpty(name)) return name;
            int n = Position(mgr, c);
            if (c.Adventure == AdventureDbId.DRAGONS) name = Str.Word("GLUE_ADVENTURE_CHAPTER_TITLE_DRG_CHAPTER" + n);
            return string.IsNullOrEmpty(name) ? Str.Game("GLUE_SPJ_CHAPTER_LABEL", n) : name;
        }

        static string ChapterText(AdventureBookPageManager mgr, ChapterPageData c)
        {
            var text = Loc(c.WingRecord.Description);
            if (!string.IsNullOrEmpty(text) || c.Adventure != AdventureDbId.DRAGONS) return text;
            return Str.Word("GLUE_ADVENTURE_CHAPTERPAGE_DESCRIPTION_DRG_" + (c.BookSection == 1 ? "EVIL" : "GOOD") + "_CHAPTER" + Position(mgr, c));
        }

        static ChapterPageData ChapterOfWing(AdventureBookPageManager mgr, int wingId)
        {
            foreach (var entry in Chapters(mgr)) if (entry.Value.WingRecord.ID == wingId) return entry.Value;
            return null;
        }

        // a chapter's state: the page's own data when it has it (time locks, unlock texts), else progress
        static string ChapterState(ChapterPageData c, AdventureBookPageDataModel page)
        {
            if (page != null && page.AllChaptersData != null)
                foreach (var m in page.AllChaptersData)
                    if (m != null && m.WingId == c.WingRecord.ID) return ChapterState(m);
            try
            {
                var progress = AdventureProgressMgr.Get();
                if (progress == null) return null;
                if (!progress.OwnsWing(c.WingRecord.ID)) return Str.NotOwned;
                if (progress.IsWingComplete(c.Adventure, c.AdventureMode, (WingDbId)c.WingRecord.ID)) return Str.Completed;
            }
            catch (Exception e) { Log.Error(e); }
            return null;
        }

        static bool Book(AdventureBookPageDataModel page, ref string key, ref string title, List<GameButton> items)
        {
            var display = s_page;
            var mgr = Manager();
            if (page.PageType == AdventureBookPageType.MAP)
            {
                // the chapter list: every chapter of the book; Enter turns to its page
                key = "book:map";
                title = Str.Join(Str.Clean(AdventureName()), Speech.S("HSA_ENHANCED_ADVENTURE_CHAPTERS"), Str.Clean(page.NumChaptersCompletedText));
                foreach (var entry in Chapters(mgr))
                {
                    var c = entry.Value;
                    int pageNum = entry.Key;
                    var name = ChapterName(mgr, c);
                    var label = Str.Join(Section(mgr, c.BookSection), name, ChapterState(c, page));
                    items.Add(new GameButton { Target = display, Label = label, Click = () => FlipTo(pageNum, name) });
                }
                if (items.Count == 0) return false;
                return true;
            }
            if (page.PageType == AdventureBookPageType.CHAPTER && page.ChapterData != null && page.ChapterData.Missions != null)
            {
                var chapter = page.ChapterData;
                // the page's own data model often comes without the name and text: the book's chapter has them
                string name = Str.Clean(chapter.Name), description = Str.Clean(chapter.Description);
                var pageChapter = ChapterOfWing(mgr, chapter.WingId);
                if (string.IsNullOrEmpty(name) && pageChapter != null) name = ChapterName(mgr, pageChapter);
                if (string.IsNullOrEmpty(description) && pageChapter != null) description = ChapterText(mgr, pageChapter);
                key = "book:chapter:" + chapter.WingId + ":" + name;
                title = Str.Join(Section(mgr, (int)chapter.MoralAlignment), name, ChapterState(chapter), description);
                int n = 1;
                foreach (var m in chapter.Missions)
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
                if (items.Count == 0) items.Add(new GameButton { Target = display, Label = Str.Join(Str.Clean(chapter.Name), ChapterState(chapter)), Click = () => { } });
                return true;
            }
            if (page.PageType == AdventureBookPageType.REWARD)
            {
                // the rewards page: what it shows
                key = "book:reward:" + page.MoralAlignment;
                title = Str.Join(Section(mgr, (int)page.MoralAlignment), Str.Word("GLUE_ADVENTURE_REWARD_PAGE_TITLE_PROGRESS", "GLUE_ADVENTURE_REWARD_PAGE_TITLE"));
                // each number with what it counts, as the page's labels show it ("Bosses defeated, 8/12")
                var stats = new[]
                {
                    new KeyValuePair<string, string>("GLUE_ADVENTURE_REWARD_PAGE_STATISTIC_BOSSES", page.NumBossesDefeatedText),
                    new KeyValuePair<string, string>("GLUE_ADVENTURE_REWARD_PAGE_STATISTIC_CARDS", page.NumCardsCollectedText),
                };
                foreach (var stat in stats)
                {
                    var value = Str.Clean(stat.Value);
                    if (string.IsNullOrEmpty(value)) continue;
                    var label = Str.Join(Str.Word(stat.Key), value);
                    items.Add(new GameButton { Target = display, Label = label, Click = () => { } });
                }
                if (page.NumChaptersOwnedText != null)
                    foreach (var text in page.NumChaptersOwnedText)
                        if (!string.IsNullOrEmpty(Str.Clean(text))) items.Add(new GameButton { Target = display, Label = Str.Clean(text), Click = () => { } });
                if (items.Count == 0) items.Add(new GameButton { Target = display, Label = title, Click = () => { } });
                return true;
            }
            return false;
        }

        static void FlipTo(int pageNum, string name)
        {
            var mgr = Manager();
            if (mgr == null || mgr.ArePagesTurning()) return;
            if (!(Ref.Invoke(mgr, "CanUserTurnPages") is bool) || !(bool)Ref.Invoke(mgr, "CanUserTurnPages")) return;
            Log.Info("adventure: chapter " + name + " (page " + pageNum + ")");
            mgr.FlipToPage(pageNum, null, null);
        }

        // the book's arrows: false when this is no book
        internal static bool TurnPage(int step)
        {
            var mgr = Manager();
            if (mgr == null || BookPage() == null) return false;
            if (mgr.ArePagesTurning()) return true;
            bool has = Ref.Get<bool>(mgr, step > 0 ? "m_hasNextPage" : "m_hasPreviousPage");
            if (!has) { Speech.Say(Speech.S("HSA_ENHANCED_ADVENTURE_NO_MORE_PAGES"), true); return true; }
            var can = Ref.Invoke(mgr, "CanUserTurnPages");
            if (!(can is bool) || !(bool)can) return true;
            Log.Info("adventure: page " + (step > 0 ? "right" : "left"));
            Ref.Call(mgr, step > 0 ? "PageRight" : "PageLeft", null, null);
            return true;
        }

        // Backspace on a chapter (or rewards) page: the chapter list, as the game's back does
        static bool BackToMap()
        {
            var mgr = Manager();
            var page = mgr == null ? null : BookPage();
            if (page == null || page.PageType == AdventureBookPageType.MAP || !HasMap(mgr)) return false;
            if (mgr.ArePagesTurning()) return true;
            Log.Info("adventure: back to the chapters");
            return AdventureBookPageManager.NavigateToMapPage();
        }

        static string AdventureName()
        {
            var config = AdventureConfig.Get();
            var record = config == null ? null : GameDbf.Adventure.GetRecord((int)config.GetSelectedAdventure());
            return record == null || record.Name == null ? "" : record.Name.GetString();
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
            if (BackToMap()) return;
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

        bool InBook { get { return m_key != null && m_key.StartsWith("book:"); } }

        internal override bool HandleKey()
        {
            if (m_menu == null) return false;
            if (InBook)
            {
                if (Keys.Left.Pressed || Keys.PageUp.Pressed) return TurnPage(-1);
                if (Keys.Right.Pressed || Keys.PageDown.Pressed) return TurnPage(1);
            }
            return m_menu.HandleKey();
        }

        internal override string Help()
        {
            if (m_menu == null) return "";
            if (!InBook) return m_menu.Help();
            return Speech.S("HSA_ENHANCED_ADVENTURE_BOOK_HELP", Keys.Enter.Name, Keys.Left.Name, Keys.Right.Name, Keys.Back.Name);
        }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
