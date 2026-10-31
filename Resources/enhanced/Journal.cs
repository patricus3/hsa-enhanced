using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets;
using Hearthstone.DataModels;
using Hearthstone.Progression;
using UnityEngine;
using RewardTrack = Hearthstone.Progression.RewardTrack;

namespace HSAEnhanced
{
    // The journal, built from the game's own data instead of HSA's reader (which clicked tab buttons,
    // read screen objects by path and clicked the event letter open). Its parts:
    // - Event: the current special event (name, time left, descriptions, the track choice when it
    //   offers one, its quests and reward track), or when the next one starts.
    // - Quests: every active quest of each pool (daily, weekly, event): progress, reward XP, rewards,
    //   time left, the time until a new quest; reroll and abandon through QuestManager.
    // - Rewards Track: level and XP, every level with its free / Tavern Pass / PASS+ rewards and their
    //   state; claim (choose one rewards: the item chosen), through RewardTrack.ClaimReward.
    // - Achievements: categories and their groups with points and progress, each achievement with its
    //   progress, points and reward; claim through AchievementManager.ClaimAchievementReward.
    // - Journey: story pages and the action to claim a completed mission's rewards.
    // - Tavern Guide (new accounts): its quest sets and quests.
    // Rewards the game grants after a claim are shown by the game's own reward popups.
    static class Journal
    {
        static JournalUI s_ui;

        internal static bool Active { get { return s_ui != null; } }
        internal static bool OwnsJourneyScene
        {
            get
            {
                var scenes = SceneMgr.Get();
                return s_ui != null && (s_ui.OpeningJourney || s_ui.JourneyOpened)
                    && scenes != null && scenes.GetMode() == SceneMgr.Mode.JOURNAL;
            }
        }

        // start of HSA's AccessibleJournal.OnJournalOpened(widget, trackType); true: ours instead
        internal static bool Open()
        {
            if (!Engine.Enabled) return false;
            var popup = UnityEngine.Object.FindObjectOfType<JournalPopup>();
            var type = Global.RewardTrackType.GLOBAL;
            if (popup != null)
            {
                var t = Ref.Get(popup, "m_rewardTrackType");
                if (t is Global.RewardTrackType) type = (Global.RewardTrackType)t;
            }
            try { if (!GameUtils.HasCompletedApprentice() && RewardTrackManager.Get().IsApprenticeTrackReady()) type = Global.RewardTrackType.APPRENTICE; } catch { }
            Close();
            s_ui = new JournalUI(type);
            Log.Info("journal: ours (" + type + ")");
            Core.Focus.Push(s_ui);
            s_ui.ShowMain(0);
            return true;
        }

        // start of HSA's AccessibleJournal.OnJournalClosed()
        internal static void Close()
        {
            if (s_ui == null) return;
            if (OwnsJourneyScene)
            {
                Log.Info("journal: keeping reader active in JOURNAL scene");
                return;
            }
            // The game's close callback can arrive during the journal's opening transition.
            // Keep our menu while the popup is still active; Host.Screens will close it once
            // the popup actually leaves the hierarchy.
            var popup = UnityEngine.Object.FindObjectOfType<JournalPopup>();
            if (popup != null && popup.gameObject.activeInHierarchy)
            {
                Log.Info("journal: ignored close while popup remains shown");
                return;
            }
            var ui = s_ui;
            s_ui = null;
            Core.Focus.Pop(ui);
        }

        internal static string Word(string key) { return Str.Word(key); }

        // a reward list as the game words it (its description; else each item's name)
        internal static string Rewards(RewardListDataModel list)
        {
            if (list == null) return null;
            var d = Str.Clean(list.Description);
            if (d.Length > 0) return d;
            var names = new List<string>();
            if (list.Items != null)
                foreach (var item in list.Items)
                {
                    string n = null;
                    try { n = RewardUtils.GetName(item, false); } catch { }
                    n = Str.Clean(n);
                    if (n.Length > 0) names.Add(item.Quantity > 1 ? item.Quantity + " " + n : n);
                }
            return names.Count == 0 ? null : Str.Join(names.ToArray());
        }

        internal static string JourneyRewards(IEnumerable<RewardListDataModel> rewards)
        {
            if (rewards == null) return null;
            var descriptions = new List<string>();
            foreach (var reward in rewards)
            {
                var description = Rewards(reward);
                if (!string.IsNullOrEmpty(description)) descriptions.Add(description);
            }
            return descriptions.Count == 0 ? null : Str.Join(descriptions.ToArray());
        }

        internal static bool Online(Core.Screen speaker)
        {
            if (Network.IsLoggedIn()) return true;
            speaker.Say(Str.Join(Word("GLUE_OFFLINE_FEATURE_DISABLED_HEADER"), Word("GLUE_OFFLINE_FEATURE_DISABLED_BODY")));
            return false;
        }
    }

    class JournalUI : Core.Screen
    {
        readonly Global.RewardTrackType m_type;
        Core.Menu m_menu;
        Action m_rebuild;
        internal bool OpeningJourney;
        internal bool JourneyOpened;

        internal JournalUI(Global.RewardTrackType type) { m_type = type; }

        bool Battlegrounds { get { return m_type == Global.RewardTrackType.BATTLEGROUNDS; } }

        void Show(Core.Menu menu, int at, Action rebuild)
        {
            menu.Index = at;
            m_menu = menu;
            m_rebuild = rebuild;
            m_menu.StartReading();
        }

        int Index { get { return m_menu == null ? 0 : m_menu.Index; } }

        // after a request the game's data changes when the server answers: the menu is read again
        void RebuildSoon()
        {
            Core.Jobs.Run(RebuildAfter(Index));
        }

        IEnumerator RebuildAfter(int at)
        {
            yield return new WaitForSecondsRealtime(1.5f);
            if (!ReferenceEquals(this, s_current) || m_rebuild == null) yield break;
            m_rebuild();
            if (m_menu != null) m_menu.Index = at;
        }

        static JournalUI s_current;


        void Info(Core.Menu menu, string text)
        {
            text = Str.Clean(text);
            if (text.Length > 0) menu.AddOption(text, () => Say(text));
        }

        #region Main
        internal void ShowMain(int at)
        {
            s_current = this;
            // The Journal's set of native tabs changes with features and account state. Use the
            // controls the game actually created instead of maintaining a parallel tab list.
            if (FindJournalTray() != null) { ShowNativeTabs(at); return; }
            var menu = new Core.Menu(this, Journal.Word("GLUE_TOOLTIP_BUTTON_JOURNAL_HEADLINE"), () => Navigation.GoBack());
            if (!Battlegrounds && m_type != Global.RewardTrackType.APPRENTICE) AddEventEntry(menu);
            if (TavernGuideActive()) menu.AddOption(Journal.Word("GLUE_PROGRESSION_TAVERN_GUIDE_TITLE"), () => ShowTavernGuide(0));
            if (!TavernGuideActive() || Battlegrounds) menu.AddOption(Journal.Word("GLUE_PROGRESSION_QUESTS_TITLE"), () => ShowQuests(false, 0));
            menu.AddOption(Journal.Word("GLUE_PROGRESSION_REWARDS_TITLE"), () => ShowTrack(MainTrack(), false, 0));
            if (!Battlegrounds) menu.AddOption(AchievementsTitle(), () => ShowCategories(0));
            menu.AddOption(Str.Back, () => Navigation.GoBack());
            Show(menu, at, () => ShowMain(Index));
        }

        // Keep the game's live tab controls as the source of truth. Scanning only the tray avoids
        // flattening every text/button in the Journal popup into one noisy accessibility list.
        static JournalTrayDisplay FindJournalTray()
        {
            try { return UnityEngine.Object.FindObjectOfType<JournalTrayDisplay>(); } catch { return null; }
        }

        void ShowNativeTabs(int at)
        {
            var tray = FindJournalTray();
            if (tray == null) { ShowMain(0); return; }
            var menu = new Core.Menu(this, Journal.Word("GLUE_TOOLTIP_BUTTON_JOURNAL_HEADLINE"), () => Navigation.GoBack());
            // Discover the game's current tab enum, then filter modes that are not tabs in the
            // regular Journal. The tray hides its buttons during tab changes, so scanning its
            // transient Unity hierarchy can return an empty or misleading list.
            AddNativeTabValues(menu, tray);
            menu.AddOption(Str.Back, () => Navigation.GoBack());
            Show(menu, at, () => ShowNativeTabs(Index));
        }

        void AddNativeTabValues(Core.Menu menu, JournalTrayDisplay tray)
        {
            var type = tray.GetType().GetNestedType("JournalTab", BindingFlags.Public | BindingFlags.NonPublic);
            if (type == null || !type.IsEnum) return;
            Log.Info("journal: discovered native tabs " + string.Join(", ", Enum.GetNames(type)));
            var seen = new HashSet<ulong>();
            foreach (var name in Enum.GetNames(type))
            {
                if (string.IsNullOrEmpty(name) || name == "Unknown") continue;
                var tab = Enum.Parse(type, name);
                ulong value = Convert.ToUInt64(tab);
                // JournalTab contains aliases. Enum.GetValues repeats their value and
                // Enum.GetName returns the same canonical name for every alias.
                if (!seen.Add(value)) continue;
                if (!NativeTabAvailable(name, tray)) continue;
                string label = NativeTabLabel(name);
                var selectedTab = tab;
                var selectedName = name;
                menu.AddOption(label, () =>
                {
                    Log.Info("journal: selected tab " + selectedName);
                    if (selectedName == "Campaign")
                    {
                        OpeningJourney = true;
                        SelectNativeTab(selectedName, selectedTab, tray);
                        Core.Jobs.Run(OpenJourneyAfterTabChange());
                    }
                    else if (selectedName == "Event") ShowEvent(0);
                    else if (selectedName == "Quest") ShowQuests(false, 0);
                    else if (selectedName == "Reward") ShowTrack(MainTrack(), false, 0);
                    else if (selectedName == "Achievment") ShowCategories(0);
                    else if (selectedName == "Profile")
                    {
                        SelectNativeTab(selectedName, selectedTab, tray);
                        Core.Jobs.Run(ReadNativeJournalPage(label));
                    }
                });
            }
        }

        void SelectNativeTab(string name, object tab, JournalTrayDisplay tray)
        {
            var spj = SPJWidget.Get();
            var spjType = spj == null ? typeof(SPJWidget) : spj.GetType();
            var destinations = spjType.GetNestedType("SpjDeepLinkDestination", BindingFlags.Public | BindingFlags.NonPublic);
            string destination = name;
            if (name == "Campaign") destination = "Journey";
            else if (name == "Quest") destination = "Quests";
            else if (name == "Reward") destination = "Rewards";
            else if (name == "Achievment") destination = "Achievements";
            if (destinations != null && destinations.IsEnum)
            {
                var dest = Enum.Parse(destinations, destination);
                if (name == "Campaign")
                {
                    // The first Journey open needs its destination queued before the
                    // JOURNAL scene creates SPJWidget.
                    if (spj == null)
                    {
                        var setPending = typeof(SPJWidget).GetMethod(
                            "SetPendingDeepLinkDestination",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            null, new Type[] { destinations }, null);
                        if (setPending == null)
                        {
                            Log.Warn("journal: missing SPJWidget.SetPendingDeepLinkDestination");
                            return;
                        }
                        try
                        {
                            setPending.Invoke(null, new object[] { dest });
                            Log.Info("journal: queued Journey deep link before SPJWidget creation");
                        }
                        catch (TargetInvocationException e)
                        {
                            Log.Error(e);
                            return;
                        }
                        OpenJourneyScene();
                        return;
                    }
                }
                try { if (Ref.Call(spj, "SelectDestinationViaDeepLink", dest)) return; }
                catch (TargetInvocationException e) { Log.Error(e); }
            }
            Ref.Call(tray, "ForceChangeActiveTabViaDeepLink", tab);
        }

        static void OpenJourneyScene()
        {
            var sceneMgr = SceneMgr.Get();
            var modeType = typeof(SceneMgr).GetNestedType("Mode", BindingFlags.Public | BindingFlags.NonPublic);
            var handlerType = typeof(SceneMgr).GetNestedType("TransitionHandlerType", BindingFlags.Public | BindingFlags.NonPublic);
            if (sceneMgr == null || modeType == null || handlerType == null)
            {
                Log.Warn("journal: cannot open Journey scene; SceneMgr mode types are unavailable");
                return;
            }
            var mode = Enum.Parse(modeType, "JOURNAL");
            var handler = Enum.Parse(handlerType, "SCENEMGR");
            if (!Ref.Call(sceneMgr, "SetNextMode", mode, handler, null, null, false))
            {
                Log.Warn("journal: SceneMgr.SetNextMode was unavailable");
                return;
            }
            Log.Info("journal: requested JOURNAL scene with pending Journey deep link");
        }

        string NativeTabLabel(string name)
        {
            string text = null;
            if (name == "Campaign") text = Str.Word("GLUE_PROGRESSION_JOURNEY_TITLE", "GLUE_SPJ_REWARD_SCROLL_TITLE", "GLUE_PROGRESSION_JOURNEY_TAB_TITLE", "GLUE_JOURNEY_TITLE");
            else if (name == "Event") text = Journal.Word("GLUE_PROGRESSION_EVENT_TAB_TITLE");
            else if (name == "Quest") text = Journal.Word("GLUE_PROGRESSION_QUESTS_TITLE");
            else if (name == "Reward") text = Journal.Word("GLUE_PROGRESSION_REWARDS_TITLE");
            else if (name == "Achievment") text = AchievementsTitle();
            else if (name == "Profile") text = Str.Word("GLUE_PROFILE_TAB_TITLE", "GLUE_PROGRESSION_PROFILE_TITLE", "GLUE_PROFILE_TITLE");
            if (!string.IsNullOrEmpty(text)) return text;
            if (name == "Campaign") return "Journey";
            if (name == "Quest") return "Quests";
            if (name == "Reward") return "Rewards";
            if (name == "Achievment") return "Achievements";
            return SplitTabName(name);
        }

        static string AchievementsTitle()
        {
            var title = Str.Word("GLUE_PROGRESSION_ACHIEVEMENTS_TITLE", "GLUE_PROGRESSION_ACHIEVEMENTS_TAB_TITLE");
            return string.IsNullOrEmpty(title) ? "Achievements" : title;
        }

        IEnumerator ReadNativeJournalPage(string title)
        {
            yield return new WaitForSecondsRealtime(.75f);
            var spj = SPJWidget.Get();
            var texts = spj == null || spj.Widget == null ? new List<KeyValuePair<Component, string>>() : Ui.TextsUnder(spj.Widget.gameObject);
            var menu = new Core.Menu(this, title, () => ShowNativeTabs(0));
            foreach (var entry in texts)
            {
                var line = Str.Clean(entry.Value);
                if (!string.IsNullOrWhiteSpace(line)) menu.AddOption(line, () => Say(line));
            }
            menu.AddOption(Journal.Word("GLUE_TOOLTIP_BUTTON_JOURNAL_HEADLINE"), () => ShowNativeTabs(0));
            Show(menu, 0, () => ReadNativeJournalPage(title));
        }

        bool NativeTabAvailable(string name, JournalTrayDisplay tray)
        {
            var meta = Ref.Get(tray, "m_journalMetaDatamodel");
            if (name == "Event")
            {
                if (Ref.Get<bool>(meta, "m_EventActive") || Ref.Get<bool>(meta, "m_EventTabActive")) return true;
                try { return SpecialEventManager.Get()?.GetCurrentSpecialEvent(true) != null; } catch { return false; }
            }
            if (name == "ApprenticeTrack" || name == "TavernGuide") return false;
            // The game can expose Journey before its story/page data has loaded. Keep
            // its native tab discoverable; selecting it waits for the book to open.
            if (name == "Campaign") return true;
            // Only show tabs this reader can currently open. This also keeps future
            // internal enum values from becoming dead menu entries.
            return name == "Event" || name == "Quest" || name == "Reward" || name == "Achievment" || name == "Profile";
        }

        static string SplitTabName(string name)
        {
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) result.Append(' ');
                result.Append(name[i]);
            }
            return result.ToString();
        }

        IEnumerator OpenJourneyAfterTabChange()
        {
            var tabsMenu = m_menu;
            SPJQuestBook book = null;
            for (int i = 0; i < 40; i++)
            {
                if (!ReferenceEquals(m_menu, tabsMenu)) { OpeningJourney = false; yield break; }
                var spj = SPJWidget.Get();
                book = spj == null ? null : spj.QuestBook;
                if (book != null && book.IsOpen && book.BookDataModel != null && book.ActiveStoryDataModel != null) break;
                Log.Info("journal: waiting for Journey (" + i + "/40): spj=" + (spj != null) +
                    ", book=" + (book != null) +
                    (book != null ? (" open=" + book.IsOpen + " data=" + (book.BookDataModel != null) + " story=" + (book.ActiveStoryDataModel != null)) : ""));
                yield return new WaitForSecondsRealtime(.5f);
            }
            Log.Info("journal: Journey book state after tab selection: " +
                (book == null ? "no book" : "open=" + book.IsOpen + ", data=" + (book.BookDataModel != null) +
                ", story=" + (book.ActiveStoryDataModel != null)));
            if (book == null || !book.IsOpen || book.BookDataModel == null || book.ActiveStoryDataModel == null)
            {
                OpeningJourney = false;
                Log.Info("journal: Journey did not become ready; leaving the Journal tabs open");
                if (UnityEngine.Object.FindObjectOfType<JournalPopup>() == null) Journal.Close();
                yield break;
            }
            OpeningJourney = false;
            JourneyOpened = true;
            if (ReferenceEquals(m_menu, tabsMenu)) ShowJourney(0);
        }

        void ShowJourney(int at)
        {
            var spj = SPJWidget.Get();
            var book = spj == null ? null : spj.QuestBook;
            var story = book == null ? null : book.ActiveStoryDataModel;
            var data = book == null ? null : book.BookDataModel;
            if (story == null || data == null)
            {
                ShowNativeTabs(at);
                return;
            }
            string title = Str.Clean(story.Name);
            var menu = new Core.Menu(this, title, () => ShowNativeTabs(0));
            int left = data.LeftPageIndex, right = data.RightPageIndex;
            AddJourneyPage(menu, book, story, left);
            if (right != left) AddJourneyPage(menu, book, story, right);
            int pageCount = story.Pages == null ? 0 : story.Pages.Count;
            if (left >= 2) menu.AddOption(Core.Speech.S("HSA_ENHANCED_PREVIOUS_PAGE"), () => TurnJourneyPage(book, -1));
            if (right + 1 < pageCount) menu.AddOption(Core.Speech.S("HSA_ENHANCED_NEXT_PAGE"), () => TurnJourneyPage(book, 1));
            menu.AddOption(Str.Word("GLOBAL_BACK"), () => ShowNativeTabs(0));
            Show(menu, at, () => ShowJourney(Index));
        }

        void TurnJourneyPage(SPJQuestBook book, int direction)
        {
            if (book == null) return;
            book.Page(direction);
            Core.Jobs.Run(RefreshAfterJourneyPageTurn(book, Index));
        }

        IEnumerator RefreshAfterJourneyPageTurn(SPJQuestBook book, int at)
        {
            yield return null;
            for (int i = 0; i < 100 && Ref.Get<bool>(book, "m_isTurningPage"); i++)
                yield return new WaitForSecondsRealtime(.1f);
            if (book != null && book.ActiveStoryDataModel != null) ShowJourney(at);
        }

        void AddJourneyPage(Core.Menu menu, SPJQuestBook book, SpjStoryDataModel story, int index)
        {
            if (index < 0 || story.Pages == null || index >= story.Pages.Count) return;
            var page = story.Pages[index];
            var lines = new List<string>();
            if (page.TextElements != null)
                foreach (var element in page.TextElements)
                {
                    var text = Str.Clean(element.Text);
                    if (!string.IsNullOrWhiteSpace(text)) lines.Add(text);
                }
            if (page.StoryBlocks != null)
                foreach (var block in page.StoryBlocks)
                {
                    var text = Str.Clean(block.Text);
                    if (!string.IsNullOrWhiteSpace(text) && !lines.Contains(text)) lines.Add(text);
                }
            if (page.PageType == SpjPageType.MISSION)
            {
                var manager = SpjManager.Get();
                var state = manager == null ? null : manager.GetStoryProgressState(story.ID);
                var mission = state == null ? null : SpjUtils.CreateMissionDataModelFromState(state);
                if (mission != null)
                    AddJourneyMissionDetails(lines, mission);
                var data = book.BookDataModel;
                string field = index == data.RightPageIndex ? "m_rightPageContainer" : "m_leftPageContainer";
                var container = Ref.Get<SPJPageContainer>(book, field);
                var missionPage = container == null ? null : container.GetComponentInChildren<SpjMissionPage>(true);
                var liveMission = missionPage == null ? null : Ref.Get<SpjMissionDataModel>(missionPage, "m_missionDataModel");
                if (liveMission != null)
                    AddJourneyMissionDetails(lines, liveMission);
                if (container != null)
                    foreach (var text in Ui.TextsUnder(container.gameObject))
                        AddJourneyText(lines, text.Value);
                Log.Info("journal: mission page " + (index + 1) + " container=" + (container == null ? "none" : container.name)
                    + " component=" + (missionPage != null) + " model=" + (mission != null)
                    + " textLines=" + lines.Count);
                bool canComplete = mission != null &&
                    (mission.MissionState == SpjMissionState.COMPLETION_AVAILABLE_SUCCESS ||
                     mission.MissionState == SpjMissionState.COMPLETION_AVAILABLE_FAILURE);
                Log.Info("journal: Journey mission page " + (index + 1) + " pageId=" + page.MissionId +
                    ", activeMission=" + (mission == null ? "none" : mission.MissionId.ToString()) +
                    ", state=" + (mission == null ? "none" : mission.MissionState.ToString()) +
                    ", rewards=" + (mission == null || mission.MissionRewards == null ? 0 : mission.MissionRewards.Count) +
                    ", canComplete=" + canComplete);
                if (canComplete)
                {
                    string action = mission.MissionState == SpjMissionState.COMPLETION_AVAILABLE_SUCCESS
                        ? Str.Join(Str.Word("GLUE_SPJ_REWARD_CLAIM_LABEL").TrimEnd(':', ' '), Journal.JourneyRewards(mission.MissionRewards))
                        : Str.Word("GLUE_SPJ_BUTTON_LABEL_CONTINUE");
                    menu.AddOption(action, () => CompleteJourneyMission(mission));
                }
                else if (mission != null && mission.MissionState == SpjMissionState.SELECTION_AVAILABLE)
                    AddJourneyMissionChoices(menu, mission);
            }
            if (lines.Count == 0)
                lines.Add(Core.Speech.S("ACCESSIBILITY_LIST_NO_ITEMS"));
            string heading = Str.Game("GLUE_COLLECTION_PAGE_NUM", index + 1) ?? (index + 1).ToString();
            menu.AddOption(heading, () => Say(Str.Join(lines.ToArray())), lines: () => lines);
        }

        static void AddJourneyMissionDetails(List<string> lines, SpjMissionDataModel mission)
        {
            AddJourneyText(lines, mission.Name);
            AddJourneyText(lines, mission.Story == null ? null : mission.Story.Text);
            AddJourneyText(lines, mission.Description);
            AddJourneyText(lines, mission.CompletedDescription);
            AddJourneyText(lines, mission.ChoiceDescription);
            AddJourneyText(lines, mission.MultiQuotaContextDescription);
            AddJourneyText(lines, mission.TimeRemainingText);
            AddJourneyText(lines, Journal.JourneyRewards(mission.MissionRewards));
            if (mission.Stats != null)
                foreach (var stat in mission.Stats)
                    if (stat.ProgressText != null)
                        foreach (var progress in stat.ProgressText) AddJourneyText(lines, progress);
        }

        void AddJourneyMissionChoices(Core.Menu menu, SpjMissionDataModel mission)
        {
            if (mission.NextMissionOptions == null || mission.NextMissionOptions.Count == 0) return;
            if (!string.IsNullOrEmpty(mission.ChoiceDescription))
                menu.AddOption(mission.ChoiceDescription, () => Say(mission.ChoiceDescription));
            foreach (var option in mission.NextMissionOptions)
            {
                if (option == null) continue;
                var selected = option;
                var detail = Str.Join(selected.Description, JourneyStats(selected.Stats));
                menu.AddOption(Str.Join(Str.Word("GLUE_CHOOSE"), selected.Name, detail),
                    () => SelectJourneyMission(mission.StoryId, selected.MissionId));
            }
        }

        static string JourneyStats(IEnumerable<StatProgressDataModel> stats)
        {
            if (stats == null) return null;
            var lines = new List<string>();
            foreach (var stat in stats)
                if (stat != null && stat.ProgressText != null)
                    foreach (var text in stat.ProgressText)
                        if (!string.IsNullOrEmpty(Str.Clean(text))) lines.Add(Str.Clean(text));
            return lines.Count == 0 ? null : Str.Join(lines.ToArray());
        }

        void SelectJourneyMission(int storyId, int missionId)
        {
            if (!Journal.Online(this)) return;
            var manager = SpjManager.Get();
            var state = manager == null ? null : manager.GetStoryProgressState(storyId);
            var mission = state == null ? null : SpjUtils.CreateMissionDataModelFromState(state);
            bool available = state != null &&
                state.MissionState == NetCache.SpjStoryState.SpjMissionState.SELECTION_AVAILABLE &&
                mission != null && mission.NextMissionOptions != null;
            bool found = false;
            if (available)
                foreach (var option in mission.NextMissionOptions)
                    if (option != null && option.MissionId == missionId) { found = true; break; }
            if (!found)
            {
                Say(Str.Unavailable);
                ShowJourney(Index);
                return;
            }
            Log.Info("journal: selecting Journey mission " + missionId + " in story " + storyId);
            manager.SendConfirmMissionSelectRequest(storyId, missionId);
            int at = Index;
            m_rebuild = () => ShowJourney(at);
            RebuildSoon();
        }

        void CompleteJourneyMission(SpjMissionDataModel mission)
        {
            if (!Journal.Online(this)) return;
            var manager = SpjManager.Get();
            var state = manager == null ? null : manager.GetStoryProgressState(mission.StoryId);
            if (state == null || state.MissionId != mission.MissionId ||
                (state.MissionState != NetCache.SpjStoryState.SpjMissionState.COMPLETION_AVAILABLE_SUCCESS &&
                 state.MissionState != NetCache.SpjStoryState.SpjMissionState.COMPLETION_AVAILABLE_FAILURE))
            {
                Say(Str.Unavailable);
                ShowJourney(Index);
                return;
            }
            Log.Info("journal: completing Journey mission " + mission.MissionId);
            manager.SendMissionCompleteRequest(mission.MissionId);
            int at = Index;
            m_rebuild = () => ShowJourney(at);
            RebuildSoon();
        }

        static void AddJourneyText(List<string> lines, string text)
        {
            text = Str.Clean(text);
            if (!string.IsNullOrWhiteSpace(text) && !lines.Contains(text)) lines.Add(text);
        }

        static bool TavernGuideActive()
        {
            try { var t = TavernGuideManager.Get(); return t != null && t.IsTavernGuideActive(); } catch { return false; }
        }

        RewardTrack MainTrack() { return RewardTrackManager.Get().GetRewardTrack(m_type); }

        void AddEventEntry(Core.Menu menu)
        {
            var events = SpecialEventManager.Get();
            var current = events == null ? null : events.GetCurrentSpecialEvent(true);
            if (current != null)
            {
                var model = events.GetEventDataModelForCurrentEvent();
                menu.AddOption(Str.Join(Journal.Word("GLUE_PROGRESSION_EVENT_TAB_TITLE"),
                    model == null ? null : Str.Clean(model.Name), EventTimeLeft(),
                    model == null ? null : Str.Clean(model.ShortDescription),
                    model == null ? null : Str.Clean(model.LongDescription)), () => ShowEvent(0));
                return;
            }
            // none now: when the next one starts (as the event tab's door says)
            var upcoming = events == null ? null : events.GetUpcomingSpecialEvent(TimeSpan.FromDays(60));
            if (upcoming == null) return;
            var until = EventTimingManager.Get().GetTimeUntilEventStart(upcoming.EventTiming);
            var text = Str.Join(Journal.Word("GLUE_PROGRESSION_EVENT_TAB_TITLE"), upcoming.DisplayName == null ? null : Str.Clean(upcoming.DisplayName.GetString()),
                Str.Game("GLUE_PROGRESSION_EVENT_TAB_COMING_SOON", TimeUtils.GetCountdownTimerString(until)));
            menu.AddOption(text, () => Say(text));
        }

        static string EventTimeLeft()
        {
            var track = RewardTrackManager.Get().GetCurrentEventRewardTrack();
            if (track == null || !track.IsValid) return null;
            var left = track.GetTimeRemaining();
            if (left <= TimeSpan.Zero) return Str.Word("GLUE_PROGRESSION_EVENT_TAB_TIMER_OVER");
            return Str.Game("GLUE_PROGRESSION_EVENT_TAB_TIMER", TimeUtils.GetCountdownTimerString(left));
        }
        #endregion

        #region Event
        void ShowEvent(int at)
        {
            var model = SpecialEventManager.Get().GetEventDataModelForCurrentEvent();
            if (model == null) { ShowMain(0); return; }
            var menu = new Core.Menu(this, Str.Clean(model.Name), () => ShowMain(0));
            // The menu speaks its first row when opened. Put the event's full introduction there
            // so the description is announced immediately instead of requiring Down several times.
            Info(menu, Str.Join(EventTimeLeft(), model.ShortDescription, model.LongDescription,
                model.ShortConclusion, model.LongConclusion));
            var track = RewardTrackManager.Get().GetCurrentEventRewardTrack();
            bool chosen = model.ActiveTrackId != 0 || (track != null && track.IsValid);
            if (!chosen && !string.IsNullOrEmpty(model.ChooseTrackPrompt) && model.RewardTracks != null)
            {
                Info(menu, model.ChooseTrackPrompt);
                foreach (var id in model.RewardTracks)
                {
                    var trackId = id;
                    var record = GameDbf.RewardTrack.GetRecord(trackId);
                    if (record == null) continue;
                    var name = record.Name == null ? trackId.ToString() : Str.Clean(record.Name.GetString());
                    menu.AddOption(name, () => ShowEventTrackChoice(trackId, 0));
                }
            }
            else
            {
                Info(menu, model.ShortConclusion);
                menu.AddOption(Journal.Word("GLUE_PROGRESSION_QUESTS_TITLE"), () => ShowQuests(true, 0));
                if (track != null && track.IsValid) menu.AddOption(Journal.Word("GLUE_PROGRESSION_REWARDS_TITLE"), () => ShowTrack(track, true, 0));
            }
            Show(menu, at, () => ShowEvent(Index));
        }

        // one of the event's tracks to choose: its levels and rewards, and Choose
        void ShowEventTrackChoice(int trackId, int at)
        {
            var record = GameDbf.RewardTrack.GetRecord(trackId);
            var menu = new Core.Menu(this, record == null || record.Name == null ? "" : Str.Clean(record.Name.GetString()), () => ShowEvent(0));
            menu.AddOption(Journal.Word("GLUE_CHOOSE"), () =>
            {
                if (!Journal.Online(this)) return;
                Log.Info("journal: event track " + trackId);
                RewardTrackManager.Get().SetActiveEventRewardTrack(trackId);
                RebuildSoon();
                ShowEvent(0);
            });
            if (record != null && record.Levels != null)
            {
                var levels = new List<RewardTrackLevelDbfRecord>(record.Levels);
                levels.Sort((a, b) => a.Level - b.Level);
                foreach (var level in levels)
                {
                    var free = level.FreeRewardListRecord;
                    Info(menu, Str.Join(Str.Game("GLUE_PROGRESSION_REWARD_TRACK_POPUP_COMPLETE_LEVEL", level.Level),
                        free == null || free.Description == null ? null : free.Description.GetString()));
                }
            }
            Show(menu, at, () => ShowEventTrackChoice(trackId, Index));
        }
        #endregion

        #region Quests
        void ShowQuests(bool eventOnly, int at)
        {
            var menu = new Core.Menu(this, Journal.Word("GLUE_PROGRESSION_QUESTS_TITLE"), () => { if (eventOnly) ShowEvent(0); else ShowMain(0); });
            var quests = QuestManager.Get();
            var trackType = Battlegrounds ? QuestPool.RewardTrackType.BATTLEGROUNDS : QuestPool.RewardTrackType.GLOBAL;
            if (!eventOnly)
            {
                AddQuests(menu, quests.CreateActiveQuestsDataModel(QuestPool.QuestPoolType.NONE, trackType, true), null, eventOnly);
                AddQuests(menu, quests.CreateActiveQuestsDataModel(QuestPool.QuestPoolType.DAILY, trackType, true), "GLUE_PROGRESSION_QUEST_DAILY", eventOnly);
                AddQuests(menu, quests.CreateActiveQuestsDataModel(QuestPool.QuestPoolType.WEEKLY, trackType, true), "GLUE_PROGRESSION_QUEST_WEEKLY", eventOnly);
            }
            var eventTrack = Battlegrounds ? null : RewardTrackManager.Get().GetCurrentEventRewardTrack();
            if (eventTrack != null && eventTrack.IsValid)
            {
                var list = quests.CreateActiveQuestsDataModel(QuestPool.QuestPoolType.EVENT, (QuestPool.RewardTrackType)eventTrack.TrackDataModel.RewardTrackType,
                    eventTrack.TrackDataModel.Level < eventTrack.TrackDataModel.LevelHardCap);
                if (list != null && list.Quests != null) list.Quests.Sort(QuestManager.SortChainQuestsToFront);
                AddQuests(menu, list, "GLUE_PROGRESSION_QUEST_EVENT", eventOnly);
            }
            menu.AddOption(Str.Back, () => { if (eventOnly) ShowEvent(0); else ShowMain(0); });
            Show(menu, at, () => ShowQuests(eventOnly, Index));
        }

        void AddQuests(Core.Menu menu, QuestListDataModel list, string poolKey, bool eventOnly)
        {
            if (list == null || list.Quests == null) return;
            var pool = poolKey == null ? null : Journal.Word(poolKey);
            foreach (var q in list.Quests)
            {
                if (q == null) continue;
                var quest = q;
                if (quest.DisplayMode == QuestManager.QuestDisplayMode.NextQuestTime || (quest.Status == QuestManager.QuestStatus.UNKNOWN && !string.IsNullOrEmpty(quest.TimeUntilNextQuest)))
                {
                    var next = Str.Join(pool, Str.Clean(quest.TimeUntilNextQuest));
                    if (next.Length > 0) menu.AddOption(next, () => Say(next));
                    continue;
                }
                var label = Str.Join(pool, QuestLabel(quest));
                menu.AddOption(label, () => ShowQuest(quest, eventOnly, 0));
            }
            Info(menu, list.BankedQuestCountMessage);
        }

        static string QuestLabel(QuestDataModel q)
        {
            return Str.Join(Str.Clean(q.Name), Str.Clean(q.Description), Str.Clean(q.ProgressMessage),
                q.RewardTrackXp > 0 ? Str.Game("GLOBAL_PROGRESSION_REWARD_TRACK_XP", q.RewardTrackXp) : null,
                Journal.Rewards(q.Rewards), Str.Clean(q.TimeUntilExpiration));
        }

        void ShowQuest(QuestDataModel quest, bool eventOnly, int at)
        {
            Action back = () => ShowQuests(eventOnly, 0);
            var menu = new Core.Menu(this, Str.Clean(string.IsNullOrEmpty(quest.Name) ? quest.Description : quest.Name), back);
            Info(menu, QuestLabel(quest));
            if (quest.Status == QuestManager.QuestStatus.ACTIVE && !quest.IsChainQuest && quest.RerollCount > 0)
                menu.AddOption(Str.Join(Journal.Word("GLUE_BACON_REROLL"), quest.RerollCount.ToString()), () =>
                {
                    if (!Journal.Online(this)) return;
                    Reroll(quest.QuestId);
                    m_rebuild = () => ShowQuests(eventOnly, 0);
                    RebuildSoon();
                });
            if (quest.Abandonable)
                menu.AddOption(Journal.Word("GLUE_PROGRESSION_ABANDON_QUEST_HEADER"), () => AskAbandon(quest, eventOnly));
            menu.AddOption(Str.Back, back);
            Show(menu, at, () => ShowQuest(quest, eventOnly, Index));
        }

        // the quest's tile rerolls it, as its reroll button does (with its animation and sound): the
        // game's journal goes to its Quests tab for the tile to be there; with no tile, straight away
        static void Reroll(int questId)
        {
            if (RerollByTile(questId)) return;
            GameTab("QUEST_SELECTED");
            Core.Jobs.Run(RerollWhenTile(questId));
        }

        static QuestTile Tile(int questId)
        {
            foreach (var tile in UnityEngine.Object.FindObjectsOfType<QuestTile>())
            {
                var widget = tile == null ? null : Ref.Get<Hearthstone.UI.Widget>(tile, "m_widget");
                QuestDataModel model = null;
                try { model = widget == null ? null : widget.GetDataModel<QuestDataModel>(); } catch { }
                if (model != null && model.QuestId == questId) return tile;
            }
            return null;
        }

        static bool RerollByTile(int questId)
        {
            var tile = Tile(questId);
            var widget = tile == null ? null : Ref.Get<Hearthstone.UI.Widget>(tile, "m_widget");
            if (widget == null) return false;
            Log.Info("journal: reroll quest " + questId + " by its tile");
            widget.TriggerEvent("CLICKED_REROLL");
            return true;
        }

        static IEnumerator RerollWhenTile(int questId)
        {
            var until = Time.unscaledTime + 3f;
            while (Time.unscaledTime < until)
            {
                yield return null;
                if (RerollByTile(questId)) yield break;
            }
            Log.Info("journal: reroll quest " + questId + " (no tile)");
            QuestManager.Get().RerollQuest(questId);
        }

        // the game's journal behind ours: one of its tabs, as its tab button selects it
        static void GameTab(string tabEvent)
        {
            var popup = UnityEngine.Object.FindObjectOfType<JournalPopup>();
            var widget = popup == null ? null : Ref.Get<Hearthstone.UI.Widget>(popup, "m_widget");
            if (widget == null) return;
            Log.Info("journal: the game's journal to " + tabEvent);
            widget.TriggerEvent(tabEvent);
        }

        // the game's own question before abandoning (the tile asks the same)
        void AskAbandon(QuestDataModel quest, bool eventOnly)
        {
            if (!Journal.Online(this)) return;
            var info = new AlertPopup.PopupInfo
            {
                m_headerText = Journal.Word("GLUE_PROGRESSION_ABANDON_QUEST_HEADER"),
                m_text = Journal.Word("GLUE_PROGRESSION_ABANDON_QUEST_BODY"),
                m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_showAlertIcon = false,
                m_responseCallback = (response, data) =>
                {
                    if (response != AlertPopup.Response.CONFIRM) return;
                    Log.Info("journal: abandon quest " + quest.QuestId);
                    QuestManager.Get().AbandonQuest(quest.QuestId);
                    m_rebuild = () => ShowQuests(eventOnly, 0);
                    RebuildSoon();
                }
            };
            DialogManager.Get().ShowPopup(info);
        }
        #endregion

        #region Rewards Track
        // a track's state per level (the game builds its nodes from it)
        static Func<int, PegasusUtil.PlayerRewardTrackLevelState> LevelStates(RewardTrack track)
        {
            var state = Ref.Get(track, "m_rewardTrackLevelState");
            if (state == null) return null;
            try { return (Func<int, PegasusUtil.PlayerRewardTrackLevelState>)Delegate.CreateDelegate(typeof(Func<int, PegasusUtil.PlayerRewardTrackLevelState>), state, "GetState"); }
            catch (Exception e) { Log.Error(e); return null; }
        }

        static List<RewardTrackNodeDataModel> Nodes(RewardTrack track)
        {
            var nodes = new List<RewardTrackNodeDataModel>();
            var asset = track.RewardTrackAsset;
            var states = LevelStates(track);
            if (asset == null || asset.Levels == null || states == null) return nodes;
            var levels = new List<RewardTrackLevelDbfRecord>(asset.Levels);
            levels.Sort((a, b) => a.Level - b.Level);
            int xp = 0;
            foreach (var level in levels)
            {
                if (level == null) continue;
                xp += level.XpNeeded;
                try { nodes.Add(RewardTrackFactory.CreateRewardTrackNodeDataModel(asset, level, track.TrackDataModel, states, xp)); }
                catch (Exception e) { Log.Error(e); break; }
            }
            return nodes;
        }

        static IEnumerable<RewardTrackNodeRewardsDataModel> Tiers(RewardTrackNodeDataModel node)
        {
            if (node.FreeRewards != null) yield return node.FreeRewards;
            if (node.PaidRewards != null) yield return node.PaidRewards;
            if (node.PaidPremiumRewards != null) yield return node.PaidPremiumRewards;
        }

        static string TierName(RewardTrackNodeRewardsDataModel tier)
        {
            switch (tier.PaidType)
            {
                case PegasusShared.RewardTrackPaidType.RTPT_PAID: return Str.Word("GLUE_PROGRESSION_REWARD_TRACK_PASS");
                case PegasusShared.RewardTrackPaidType.RTPT_PAID_PREMIUM: return Str.Word("GLUE_PROGRESSION_REWARD_TRACK_PAID_PREMIUM_LABEL");
                default: return Str.Word("GLUE_PROGRESSION_REWARD_TRACK_FREE_LABEL");
            }
        }

        static string TierState(RewardTrackNodeRewardsDataModel tier, int level, int current)
        {
            if (tier.IsClaimed) return Str.Word("GLUE_PROGRESSION_REWARD_TRACK_POPUP_SKIN_CHOICE_CLAIMED");
            if (tier.IsClaimable) return Str.Word("GLUE_PROGRESSION_REWARD_TRACK_POPUP_SKIN_CHOICE_CTA");
            if (level > current) return Str.Game("GLUE_PROGRESSION_REWARD_TRACK_POPUP_INCOMPLETE_LEVEL", level);
            return Str.Word("GLUE_ADVENTURE_LOCKED");
        }

        static string TierLabel(RewardTrackNodeRewardsDataModel tier, int level, int current)
        {
            var rewards = Journal.Rewards(tier.Items);
            if (string.IsNullOrEmpty(rewards)) return null;
            return Str.Join(TierName(tier), TierState(tier, level, current), rewards);
        }

        void ShowTrack(RewardTrack track, bool isEvent, int at)
        {
            Action back = () => { if (isEvent) ShowEvent(0); else ShowMain(0); };
            if (track == null || !track.IsValid) { back(); return; }
            var data = track.TrackDataModel;
            var menu = new Core.Menu(this, Str.Clean(data.Name), back);
            int current = data.Level;
            Info(menu, Str.Join(Str.Game("GLUE_PROGRESSION_REWARD_TRACK_POPUP_COMPLETE_LEVEL", current),
                Str.Game(isEvent ? "GLUE_PROGRESSION_EVENT_TAB_REWARD_POPUP_INCOMPLETE_LEVEL" : "GLOBAL_PROGRESSION_REWARD_TRACK_XP", data.Xp) + " / " + data.XpNeeded,
                Str.Clean(data.XpBoostText)));
            if (isEvent) Info(menu, EventTimeLeft());
            var nodes = Nodes(track);
            // every reward that can be claimed now and needs no choice, one after the other
            var ready = new List<RewardTrackNodeRewardsDataModel>();
            foreach (var node in nodes) foreach (var tier in Tiers(node)) if (Claimable(track, tier) && (tier.Items == null || !tier.Items.ChooseOne)) ready.Add(tier);
            LogLevels(track, nodes);
            if (ready.Count > 0)
                menu.AddOption(Str.Join(Str.Word("GLUE_PROGRESSION_REWARD_TRACK_POPUP_SKIN_CHOICE_CTA"), ready.Count.ToString()), () =>
                {
                    if (!Journal.Online(this)) return;
                    var first = ready[0];
                    if (ClaimTier(track, first, 0)) RebuildSoon();
                });
            foreach (var n in nodes)
            {
                var node = n;
                var parts = new List<string> { Str.Game("GLUE_PROGRESSION_REWARD_TRACK_POPUP_COMPLETE_LEVEL", node.Level) };
                foreach (var tier in Tiers(node)) parts.Add(TierLabel(tier, node.Level, current));
                var label = Str.Join(parts.ToArray());
                menu.AddOption(label, () => ShowTrackLevel(track, isEvent, node.Level, 0));
            }
            // Keep an explicit menu item as well as the Backspace handler. On the track itself,
            // players need a keyboard-selectable way back to the journal/event screen.
            menu.AddOption(Str.Back, back);
            Show(menu, at, () => ShowTrack(track, isEvent, Index));
            Log.Info("journal: track " + track.RewardTrackId + " level " + current + ", " + nodes.Count + " levels, " + ready.Count + " to claim");
        }

        void ShowTrackLevel(RewardTrack track, bool isEvent, int level, int at)
        {
            RewardTrackNodeDataModel node = null;
            foreach (var n in Nodes(track)) if (n.Level == level) { node = n; break; }
            if (node == null) { ShowTrack(track, isEvent, 0); return; }
            int current = track.TrackDataModel.Level;
            var menu = new Core.Menu(this, Str.Game("GLUE_PROGRESSION_REWARD_TRACK_POPUP_COMPLETE_LEVEL", level), () => ShowTrack(track, isEvent, 0));
            foreach (var t in Tiers(node))
            {
                var tier = t;
                var label = TierLabel(tier, level, current);
                if (label == null) continue;
                if (Claimable(track, tier))
                    menu.AddOption(label, () => Claim(track, isEvent, tier));
                else menu.AddOption(label, () => Say(label));
                if (tier.Items != null && !string.IsNullOrEmpty(tier.Items.LongDescription)) Info(menu, tier.Items.LongDescription);
            }
            menu.AddOption(Str.Back, () => ShowTrack(track, isEvent, 0));
            Show(menu, at, () => ShowTrackLevel(track, isEvent, level, Index));
        }

        void Claim(RewardTrack track, bool isEvent, RewardTrackNodeRewardsDataModel tier)
        {
            if (!Journal.Online(this)) return;
            if (tier.Items != null && tier.Items.ChooseOne && tier.Items.Items != null)
            {
                ChooseOne(tier.Items, Str.Game("GLUE_PROGRESSION_REWARD_TRACK_POPUP_COMPLETE_LEVEL", tier.Level), () => ShowTrackLevel(track, isEvent, tier.Level, 0), item =>
                {
                    if (!ClaimTier(track, tier, item.AssetId)) return;
                    m_rebuild = () => ShowTrack(track, isEvent, 0);
                    RebuildSoon();
                });
                return;
            }
            if (ClaimTier(track, tier, 0)) RebuildSoon();
        }

        // a reward there is to claim: marked claimable, and one the game has at that level (the track's
        // data also makes empty slots, which the game refuses to claim)
        static bool Claimable(RewardTrack track, RewardTrackNodeRewardsDataModel tier)
        {
            if (tier == null || !tier.IsClaimable || tier.IsClaimed) return false;
            try
            {
                var exists = track.GetType().GetMethod("RewardExistsAtLevel", Any);
                if (exists != null && !(bool)exists.Invoke(track, new object[] { tier.Level, tier.PaidType })) return false;
            }
            catch { }
            return tier.Items != null && tier.Items.Items != null && tier.Items.Items.Count > 0;
        }

        static readonly HashSet<string> s_loggedTracks = new HashSet<string>();

        // what each level holds, once per track and level (for bug reports)
        static void LogLevels(RewardTrack track, List<RewardTrackNodeDataModel> nodes)
        {
            var key = track.RewardTrackId + ":" + track.TrackDataModel.Level;
            if (!s_loggedTracks.Add(key)) return;
            var parts = new List<string>();
            foreach (var node in nodes)
                foreach (var tier in Tiers(node))
                    parts.Add(node.Level + " " + tier.PaidType + " items " + (tier.Items == null || tier.Items.Items == null ? 0 : tier.Items.Items.Count)
                              + (tier.IsClaimable ? " claimable" : "") + (tier.IsClaimed ? " claimed" : "") + (Claimable(track, tier) ? " REAL" : ""));
            Log.Info("journal: track " + track.RewardTrackId + " levels: " + string.Join(" | ", parts.ToArray()));
        }

        static readonly System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

        // claims one reward the way the game's claim button does: the track of the reward's own type
        // and id. The game refuses some requests before sending them: said, and why in the log
        bool ClaimTier(RewardTrack shown, RewardTrackNodeRewardsDataModel tier, int chooseOneItem)
        {
            var track = RewardTrackManager.Get().GetRewardTrack((Global.RewardTrackType)tier.RewardTrackType) ?? shown;
            int id = tier.RewardTrackId != 0 ? tier.RewardTrackId : track.RewardTrackId;
            bool sent = false;
            try { sent = track.ClaimReward(id, tier.Level, tier.PaidType, chooseOneItem); } catch (Exception e) { Log.Error(e); }
            Log.Info("journal: claim track " + id + " (shown " + shown.RewardTrackId + ", reward's track " + track.RewardTrackId + ", type " + tier.RewardTrackType + ") level " + tier.Level + " " + tier.PaidType
                     + (chooseOneItem != 0 ? " item " + chooseOneItem : "") + ": " + (sent ? "sent" : "refused by the game (" + WhyRefused(track, id, tier) + ")"));
            if (!sent) Say(Str.Unavailable);
            return sent;
        }

        static string WhyRefused(RewardTrack track, int id, RewardTrackNodeRewardsDataModel tier)
        {
            var parts = new List<string>();
            try
            {
                var exists = track.GetType().GetMethod("RewardExistsAtLevel", Any);
                var status = track.GetType().GetMethod("GetRewardStatus", Any);
                if (exists != null) parts.Add("exists " + exists.Invoke(track, new object[] { tier.Level, tier.PaidType }));
                parts.Add("track level " + track.TrackDataModel.Level);
                if (status != null) parts.Add("status " + status.Invoke(track, new object[] { tier.Level, tier.PaidType }));
                parts.Add("owned " + track.HasOwnedRewardTrackPaidType(tier.PaidType));
                parts.Add("pending " + track.IsRewardClaimPending);
                parts.Add("same id " + (id == track.RewardTrackId));
            }
            catch (Exception e) { parts.Add(e.GetType().Name); }
            return string.Join(", ", parts.ToArray());
        }

        // a choose one reward: its items, Enter claims the one chosen
        void ChooseOne(RewardListDataModel list, string title, Action back, Action<RewardItemDataModel> claim)
        {
            var menu = new Core.Menu(this, title, back);
            Info(menu, list.Description);
            foreach (var i in list.Items)
            {
                var item = i;
                if (item == null) continue;
                string name = null;
                try { name = RewardUtils.GetName(item, false); } catch { }
                var label = Str.Join(Str.Clean(name), item.Quantity > 1 ? item.Quantity.ToString() : null, Str.Clean(item.StandaloneDescription));
                if (label.Length == 0) continue;
                menu.AddOption(label, () => claim(item));
            }
            menu.AddOption(Str.Back, back);
            Show(menu, 0, null);
        }
        #endregion

        #region Achievements
        void ShowCategories(int at)
        {
            var achievements = AchievementManager.Get();
            try { achievements.GetAchievementDataModel(1); } catch { }      // (loads every group's achievements)
            var menu = new Core.Menu(this, AchievementsTitle(), () => ShowMain(0));
            Info(menu, Str.Game("GLOBAL_PROGRESSION_POINTS", achievements.TotalPointsExcludeRetired));
            var recent = achievements.GetRecentlyCompletedAchievements();
            if (recent != null && recent.Count > 0)
                menu.AddOption(Journal.Word("GLUE_PROGRESSION_ACHIEVEMENTS_RECENTLY_COMPLETED"), () => ShowAchievementList(Journal.Word("GLUE_PROGRESSION_ACHIEVEMENTS_RECENTLY_COMPLETED"), Recent(recent), () => ShowCategories(0), 0));
            var categories = achievements.Categories == null ? null : achievements.Categories.Categories;
            if (categories != null)
                foreach (var c in categories)
                {
                    if (c == null) continue;
                    var category = c;
                    menu.AddOption(Str.Join(Str.Clean(category.Name), Stats(category.Stats)), () => ShowSubcategories(category, 0));
                }
            menu.AddOption(Str.Back, () => ShowMain(0));
            Show(menu, at, () => ShowCategories(Index));
        }

        // the recently completed ones in the game's order (current tiers, by status then date)
        static List<AchievementDataModel> Recent(Hearthstone.UI.DataModelList<AchievementDataModel> recent)
        {
            try { return new List<AchievementDataModel>(recent.GetCurrentSortedAchievements().SortByStatusThenClaimedDate()); }
            catch (Exception e) { Log.Error(e); return new List<AchievementDataModel>(recent); }
        }

        static string Stats(AchievementStatsDataModel s)
        {
            if (s == null) return null;
            return Str.Join(Str.Game("GLOBAL_PROGRESSION_POINTS", s.Points) + " / " + s.AvailablePoints,
                Str.Game("GLOBAL_PROGRESSION_PROGRESS_MESSAGE", s.CompletedAchievements, s.TotalAchievements),
                Str.Game("GLOBAL_PROGRESSION_ACHIEVEMENT_COMPLETION_PERCENTAGE", s.CompletionPercentage),
                s.Unclaimed > 0 ? Str.Join(Str.Word("GLUE_PROGRESSION_REWARD_TRACK_POPUP_SKIN_CHOICE_CTA"), s.Unclaimed.ToString()) : null);
        }

        void ShowSubcategories(AchievementCategoryDataModel category, int at)
        {
            var menu = new Core.Menu(this, Str.Clean(category.Name), () => ShowCategories(0));
            try { AchievementManager.Get().SelectCategory(category); } catch { }
            if (category.Subcategories != null && category.Subcategories.Subcategories != null)
                foreach (var s in category.Subcategories.Subcategories)
                {
                    if (s == null) continue;
                    var sub = s;
                    menu.AddOption(Str.Join(Str.Clean(sub.Name), sub.IsLocked ? Str.Locked : null, Stats(sub.Stats)), () =>
                    {
                        try { AchievementManager.Get().SelectSubcategory(sub); } catch { }
                        ShowSubcategory(category, sub, 0);
                    });
                }
            menu.AddOption(Str.Back, () => ShowCategories(0));
            Show(menu, at, () => ShowSubcategories(category, Index));
        }

        // a group as the game lays it out: its sections in order, each with its name and the achievements
        // the game shows in it (the current tier of a tiered one, in the game's own order)
        void ShowSubcategory(AchievementCategoryDataModel category, AchievementSubcategoryDataModel sub, int at)
        {
            Action back = () => ShowSubcategories(category, 0);
            var menu = new Core.Menu(this, Str.Clean(sub.Name), back);
            var sections = sub.Sections == null || sub.Sections.Sections == null ? new List<AchievementSectionDataModel>() : new List<AchievementSectionDataModel>(sub.Sections.Sections);
            foreach (var section in sections)
            {
                if (section == null) continue;
                Hearthstone.UI.DataModelList<AchievementDataModel> shown = null;
                try { shown = Hearthstone.Progression.Section.SetDisplayedAchievements(section); } catch (Exception e) { Log.Error(e); }
                if (shown == null || shown.Count == 0) continue;
                if (sections.Count > 1) Info(menu, section.Name);
                foreach (var a in shown) AddAchievement(menu, a, () => ShowSubcategory(category, sub, 0));
            }
            menu.AddOption(Str.Back, back);
            Show(menu, at, () => ShowSubcategory(category, sub, Index));
        }

        void AddAchievement(Core.Menu menu, AchievementDataModel a, Action back)
        {
            if (a == null) return;
            var achievement = a;
            var label = AchievementLabel(achievement);
            Log.Once("journal: achievement e.g. " + label);
            if (achievement.Status == AchievementManager.AchievementStatus.COMPLETED)
                menu.AddOption(label, () => ClaimAchievement(achievement, back));
            else menu.AddOption(label, () => Say(label));
        }

        static string Description(AchievementDataModel a)
        {
            var d = Str.Clean(a.Description);
            if (d.Length > 0) return d;
            var record = GameDbf.Achievement.GetRecord(a.ID);
            if (record == null || record.Description == null) return null;
            try { return Str.Clean(ProgressUtils.FormatDescription(record.Description.GetString(), record.Quota)); } catch { return Str.Clean(record.Description.GetString()); }
        }

        static string AchievementLabel(AchievementDataModel a)
        {
            string state;
            switch (a.Status)
            {
                case AchievementManager.AchievementStatus.COMPLETED: state = Str.Word("GLUE_PROGRESSION_REWARD_TRACK_POPUP_SKIN_CHOICE_CTA"); break;
                case AchievementManager.AchievementStatus.REWARD_GRANTED:
                case AchievementManager.AchievementStatus.REWARD_ACKED:
                    // (the game's date text already says "Completed {date}")
                    state = string.IsNullOrEmpty(a.CompletionDate) ? Str.Completed : Str.Clean(a.CompletionDate); break;
                default: state = a.IsLocked ? Str.Join(Str.Locked, Str.Clean(a.LockedMessage)) : Str.Clean(a.ProgressMessage); break;
            }
            return Str.Join(Str.Clean(a.Name), Description(a), state,
                a.MaxTier > 1 ? Str.Clean(a.TierMessage) : null,
                a.Points > 0 ? Str.Game("GLOBAL_PROGRESSION_POINTS", a.Points) : null,
                Str.Clean(a.RewardSummary), a.RewardTrackXp > 0 ? Str.Game("GLOBAL_PROGRESSION_REWARD_TRACK_XP", a.RewardTrackXp) : null);
        }

        void ShowAchievementList(string title, List<AchievementDataModel> list, Action back, int at)
        {
            var menu = new Core.Menu(this, title, back);
            foreach (var a in list) AddAchievement(menu, a, () => ShowAchievementList(title, list, back, 0));
            menu.AddOption(Str.Back, back);
            Show(menu, at, () => ShowAchievementList(title, list, back, Index));
        }

        // the reward of a completed achievement (a choose one: the item chosen), once, as its cell claims it
        void ClaimAchievement(AchievementDataModel a, Action back)
        {
            if (!Journal.Online(this)) return;
            var record = GameDbf.Achievement.GetRecord(a.ID);
            bool chooseOne = record != null && record.RewardListRecord != null && record.RewardListRecord.ChooseOne;
            if (chooseOne && a.RewardList != null && a.RewardList.Items != null)
            {
                ChooseOne(a.RewardList, Str.Clean(a.Name), back, item =>
                {
                    Log.Info("journal: claim achievement " + a.ID + " item " + item.AssetId);
                    AchievementManager.Get().ClaimAchievementReward(a.ID, item.AssetId);
                    m_rebuild = back;
                    RebuildSoon();
                });
                return;
            }
            // as its cell's Claim button does (its animation and sound): the game's journal goes to its
            // achievements, whose section ours has selected; with no cell, straight away
            GameTab("ACHIEVEMENT_SELECTED");
            Core.Jobs.Run(ClaimWhenCell(a.ID));
            RebuildSoon();
        }

        static IEnumerator ClaimWhenCell(int id)
        {
            var until = Time.unscaledTime + 3f;
            while (Time.unscaledTime < until)
            {
                foreach (var cell in UnityEngine.Object.FindObjectsOfType<AchievementCell>())
                {
                    var widget = cell == null ? null : Ref.Get<Hearthstone.UI.Widget>(cell, "m_widget");
                    AchievementDataModel model = null;
                    try { model = widget == null ? null : widget.GetDataModel<AchievementDataModel>(); } catch { }
                    if (model == null || model.ID != id) continue;
                    Log.Info("journal: claim achievement " + id + " by its cell");
                    widget.TriggerEvent("CODE_CLAIM_ACHIEVEMENT");
                    yield break;
                }
                yield return null;
            }
            Log.Info("journal: claim achievement " + id + " (no cell)");
            AchievementManager.Get().ClaimAchievementReward(id);
        }
        #endregion

        #region Tavern Guide
        void ShowTavernGuide(int at)
        {
            var menu = new Core.Menu(this, Journal.Word("GLUE_PROGRESSION_TAVERN_GUIDE_TITLE"), () => ShowMain(0));
            TavernGuideDataModel guide = null;
            try { guide = TavernGuideManager.Get().GetTavernGuideDataModel(); } catch { }
            if (guide != null && guide.TavernGuideQuestSetCategories != null)
                foreach (var category in guide.TavernGuideQuestSetCategories)
                {
                    if (category == null || category.TavernGuideQuestSets == null) continue;
                    foreach (var s in category.TavernGuideQuestSets)
                    {
                        if (s == null) continue;
                        var set = s;
                        menu.AddOption(Str.Join(Str.Clean(category.Title), Str.Clean(set.Title), Str.Clean(set.Description)), () => ShowQuestSet(set, 0));
                    }
                }
            menu.AddOption(Str.Back, () => ShowMain(0));
            Show(menu, at, () => ShowTavernGuide(Index));
        }

        void ShowQuestSet(TavernGuideQuestSetDataModel set, int at)
        {
            var menu = new Core.Menu(this, Str.Clean(set.Title), () => ShowTavernGuide(0));
            Info(menu, set.Description);
            if (set.Quests != null)
                foreach (var q in set.Quests)
                {
                    if (q == null) continue;
                    var state = q.Status.ToString() == "LOCKED" ? Str.Join(Str.Locked, Str.Clean(q.UnlockRequirementsDescription))
                              : q.Status.ToString() == "COMPLETED" ? Str.Completed : (q.Quest == null ? null : Str.Clean(q.Quest.ProgressMessage));
                    Info(menu, Str.Join(Str.Clean(q.Title), state, Str.Clean(q.SelectedDescription), q.Quest == null ? null : Journal.Rewards(q.Quest.Rewards)));
                }
            var done = set.CompletionAchievement;
            if (done != null)
            {
                var label = Str.Join(Journal.Word("GLUE_PROGRESSION_APPRENTICE_TAVERN_GUIDE_COMPLETION_REWARD"), AchievementLabel(done));
                if (done.Status == AchievementManager.AchievementStatus.COMPLETED) menu.AddOption(label, () => ClaimAchievement(done, () => ShowQuestSet(set, 0)));
                else Info(menu, label);
            }
            menu.AddOption(Str.Back, () => ShowTavernGuide(0));
            Show(menu, at, () => ShowQuestSet(set, Index));
        }
        #endregion

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
