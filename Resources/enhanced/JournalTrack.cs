using System;
using System.Collections.Generic;
using Accessibility;
using Hearthstone.Progression;

namespace HSAEnhanced
{
    // The journal's reward track (the season's, or an event's from its Events tab): HSA's menu offers
    // only "claim rewards" and "read level", so what the track gives is never said. Its levels are
    // added: each with its state (claimed, to claim, locked), the XP it needs, and its free, Tavern
    // Pass and premium rewards, in the game's words.
    static class JournalTrack
    {
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AccessibleMenu, object> s_done = new System.Runtime.CompilerServices.ConditionalWeakTable<AccessibleMenu, object>();

        // The journal's main menu: HSA offers Events only when SpecialEventManager has a current event
        // (it keeps the answer it found first). Asked again here; when the game has an event and the menu
        // has no Events, it is added, opening the Events tab as HSA's own option does. The log says what
        // the game has.
        internal static void CheckEvents(AccessibleMenu menu, object journal)
        {
            object mark;
            if (s_done.TryGetValue(menu, out mark) || !ReferenceEquals(Ref.Get(journal, "m_mainMenu"), menu)) return;
            var title = Ref.Get(menu, "m_menuName") as string;
            if (title != LocalizationUtils.Get(LocalizationKey.GLOBAL_JOURNAL)) return;
            s_done.Add(menu, true);
            try
            {
                var events = SpecialEventManager.Get();
                var current = events == null ? null : events.GetCurrentSpecialEvent(true);
                RewardTrack track = null;
                try { track = RewardTrackManager.Get().GetCurrentEventRewardTrack(); } catch { }
                var timing = EventTimingManager.Get();
                var all = new List<string>();
                if (GameDbf.SpecialEvent != null && timing != null)
                    foreach (var r in GameDbf.SpecialEvent.GetRecords())
                    {
                        if (r == null) continue;
                        bool active = timing.IsEventActive(r.EventTiming);
                        var until = timing.GetTimeUntilEventStart(r.EventTiming);
                        var left = timing.GetTimeLeftForEvent(r.EventTiming);
                        if (active || (until > TimeSpan.Zero && until < TimeSpan.FromDays(14)) || (left > TimeSpan.Zero))
                            all.Add(r.ID + " " + (r.DisplayName == null ? "" : r.DisplayName.GetString()) + (active ? " active" : "") + ", starts in " + until + ", left " + left);
                    }
                Log.Info("journal: current event " + (current == null ? "none" : current.ID + " " + (current.DisplayName == null ? "" : current.DisplayName.GetString()))
                         + ", event track " + (track == null ? "none" : track.RewardTrackId.ToString()) + ", apprentice done " + GameUtils.HasCompletedApprentice()
                         + ", events: " + (all.Count == 0 ? "none" : string.Join(" | ", all.ToArray())));
                // every active quest the game holds, by pool and track (what the journal can show)
                var quests = new List<string>();
                foreach (Assets.QuestPool.RewardTrackType trackType in Enum.GetValues(typeof(Assets.QuestPool.RewardTrackType)))
                    foreach (Assets.QuestPool.QuestPoolType pool in Enum.GetValues(typeof(Assets.QuestPool.QuestPoolType)))
                    {
                        Hearthstone.DataModels.QuestListDataModel model = null;
                        try { model = QuestManager.Get().CreateActiveQuestsDataModel(pool, trackType, false); } catch { }
                        if (model == null || model.Quests == null || model.Quests.Count == 0) continue;
                        var names = new List<string>();
                        foreach (var q in model.Quests) if (q != null) names.Add(Str.Clean(q.Name));
                        quests.Add(trackType + "/" + pool + ": " + string.Join(", ", names.ToArray()));
                    }
                Log.Info("journal: quests " + (quests.Count == 0 ? "none" : string.Join(" | ", quests.ToArray())));
                var label = Str.Word("GLUE_PROGRESSION_EVENT_TAB_TITLE");
                if (current == null || label.Length == 0 || MenuEdit.IndexOfText(menu, label) >= 0) return;
                var list = MenuEdit.List(menu);
                var j = journal;
                menu.AddOption(label, () => OpenEvents(j));
                if (list != null && list.Count > 1) { var o = list[list.Count - 1]; list.RemoveAt(list.Count - 1); list.Insert(0, o); }
                Log.Info("journal: Events added (the game has " + current.ID + ")");
            }
            catch (Exception e) { Log.Error(e); }
        }

        // the Events tab, as HSA's own option opens it (its wax seal button)
        static void OpenEvents(object journal)
        {
            var manager = Ref.Invoke(journal, "GetButtonManager") as UnityEngine.GameObject;
            PegUIElement seal = null;
            if (manager != null)
                foreach (var p in manager.GetComponentsInChildren<PegUIElement>(true))
                    if (p != null && p.name.IndexOf("Events_WaxSeal", StringComparison.OrdinalIgnoreCase) >= 0) { seal = p; break; }
            Log.Info("journal: open Events" + (seal == null ? " (no tab button found)" : ""));
            Ref.Call(journal, "SwitchToTab", JournalTrayDisplay.JournalTab.Event, seal);
        }

        internal static void AddLevels(AccessibleMenu menu, object journal)
        {
            object mark;
            if (s_done.TryGetValue(menu, out mark) || !ReferenceEquals(Ref.Get(journal, "m_rewardsTrackMenu"), menu)) return;
            var track = Ref.Invoke(journal, "GetRewardTrack") as RewardTrack;
            var asset = track == null || !track.IsValid ? null : track.RewardTrackAsset;
            if (asset == null || asset.Levels == null) return;
            s_done.Add(menu, true);
            int current = track.TrackDataModel == null ? 0 : track.TrackDataModel.Level;
            var levels = new List<RewardTrackLevelDbfRecord>(asset.Levels);
            levels.Sort((a, b) => a.Level - b.Level);
            var ui = journal as AccessibleComponent;
            var state = Ref.Get(journal, "m_curState");
            bool isEvent = state != null && state.ToString().Contains("EVENT");
            foreach (var level in levels)
            {
                if (level == null) continue;
                var label = Describe(track, level, current, isEvent);
                if (label.Length > 0) menu.AddOption(label, () => AccessibilityMgr.Output(ui, label));
            }
            Log.Info("journal: reward track " + track.RewardTrackId + ", level " + current + ", " + levels.Count + " levels listed");
        }

        static string Describe(RewardTrack track, RewardTrackLevelDbfRecord level, int current, bool isEvent)
        {
            string state;
            if (level.Level > current)
                state = Str.Join(Str.Word("GLUE_ADVENTURE_LOCKED"), level.XpNeeded <= 0 ? null
                    : Str.Game(isEvent ? "GLUE_PROGRESSION_EVENT_TAB_REWARD_POPUP_INCOMPLETE_LEVEL" : "GLOBAL_PROGRESSION_REWARD_TRACK_XP", level.XpNeeded));
            else if (track.HasUnclaimedRewardsForLevel(level)) state = Str.Word("GLUE_PROGRESSION_REWARD_TRACK_POPUP_SKIN_CHOICE_CTA");
            else state = Str.Word("GLUE_PROGRESSION_REWARD_TRACK_POPUP_SKIN_CHOICE_CLAIMED");
            return Str.Join(Str.Game("GLUE_PROGRESSION_REWARD_TRACK_POPUP_COMPLETE_LEVEL", level.Level), state,
                Reward("GLOBAL_RARITY_FREE", level.FreeRewardListRecord),
                Reward("GLUE_PROGRESSION_REWARD_TRACK_PASS", level.PaidRewardListRecord),
                Reward("GLUE_COLLECTION_CRAFTING_FILTERS_PREMIUM", level.PaidPremiumRewardListRecord));
        }

        static string Reward(string kindKey, RewardListDbfRecord list)
        {
            if (list == null || list.Description == null) return null;
            var text = Str.Clean(list.Description.GetString());
            return text.Length == 0 ? null : Str.Join(Str.Word(kindKey), text);
        }
    }
}
