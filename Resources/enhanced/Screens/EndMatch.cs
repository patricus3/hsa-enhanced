using System;
using System.Collections.Generic;
using Hearthstone.DataModels;
using Hearthstone.Progression;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // The end of a match and what the game tells you afterwards, our own way:
    //   the victory / defeat screen: a list that grows as the game shows its steps: the result (the
    //     banner, your hero, its level), your rank (the new or current rank, stars won or lost, the
    //     bonuses, what you can't lose), the season card back progress, a ranked reward, reward track
    //     XP (the XP of the game, each quest finished with its XP, level ups, the level and its XP).
    //     Each new item is spoken as it comes; Left / Right go through them, Up / Down their lines;
    //     Enter goes on through the game's steps.
    //   anywhere: quest progress and finished quests, achievements, reward track XP gains.
    static class EndMatch
    {
        static EndMatchUI s_ui;

        internal static EndGameScreen Shown()
        {
            var es = EndGameScreen.Get();
            if (es == null || !es || !es.gameObject.activeInHierarchy) return null;
            var gs = GameState.Get();
            if (gs == null || !gs.IsGameOver()) return null;
            return es;
        }

        // every frame
        internal static void Tick()
        {
            var es = Shown();
            if (s_ui != null && (es == null || s_ui.Screen != es)) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && es != null) { s_ui = new EndMatchUI(es); Focus.Push(s_ui); }
            if (s_ui != null) s_ui.Gather();
            Toasts.Tick();
            Xp.Tick();
        }

        // something to say: into the end screen's list while it is up, else spoken
        internal static void Tell(string key, List<string> lines, string log)
        {
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            if (lines.Count == 0) return;
            Log.Info(log + ": " + string.Join(" | ", lines.ToArray()));
            if (s_ui != null) { s_ui.Add(key, lines); return; }
            Speech.Say(Str.Join(lines.ToArray()));
        }

        internal static string A(string key, params object[] args) { return Speech.S("ACCESSIBILITY_" + key, args); }
    }

    class EndMatchUI : Core.Screen
    {
        internal readonly EndGameScreen Screen;

        class Item { internal string Key; internal List<string> Lines; }
        readonly List<Item> m_items = new List<Item>();
        readonly HashSet<string> m_keys = new HashSet<string>();
        int m_at, m_line;
        float m_next;

        internal EndMatchUI(EndGameScreen screen) { Screen = screen; }

        internal override bool Alive { get { return Screen != null && Screen && EndMatch.Shown() == Screen; } }

        static string A(string key, params object[] args) { return EndMatch.A(key, args); }

        // a new item: kept for browsing, and spoken now
        internal void Add(string key, List<string> lines)
        {
            if (key != null && !m_keys.Add(key))
            {
                // the same item with more to say (XP of the same track): its lines grow
                var same = m_items.Find(i => i.Key == key);
                if (same != null) foreach (var l in lines) if (!same.Lines.Contains(l)) { same.Lines.Add(l); if (Focused) Say(l); }
                return;
            }
            m_items.Add(new Item { Key = key, Lines = lines });
            if (Focused) Say(Str.Join(lines.ToArray()));
        }

        // what the game shows now, each part once
        internal void Gather()
        {
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 0.3f;
            try { Result(); } catch (Exception e) { Log.Error(e); }
            try { Rank(); } catch (Exception e) { Log.Error(e); }
            try { CardBack(); } catch (Exception e) { Log.Error(e); }
            try { RankedReward(); } catch (Exception e) { Log.Error(e); }
        }

        // the banner (Victory, Defeat...), your hero, its level
        void Result()
        {
            if (m_keys.Contains("result")) return;
            var scoop = Screen.m_twoScoop;
            if (scoop == null || !scoop.gameObject.activeInHierarchy || !scoop.IsShown()) return;
            var lines = new List<string>();
            if (scoop.m_bannerLabel != null && scoop.m_bannerLabel.gameObject.activeInHierarchy) lines.Add(Str.Clean(Ui.ShownText(scoop.m_bannerLabel.Text)));
            try
            {
                var me = GameState.Get().GetFriendlySidePlayer();
                var hero = me == null ? null : me.GetHero();
                if (hero != null) lines.Add(Str.Clean(hero.GetName()));
            }
            catch { }
            var bar = Ref.Get<HeroXPBar>(scoop, "m_xpBar");
            if (bar != null && bar.gameObject.activeInHierarchy)
            {
                if (bar.m_heroLevelText != null) lines.Add(Str.Game("GLOBAL_PROGRESSION_TOOLTIP_CLASS_DEFAULT_DESC", Str.Clean(Ui.ShownText(bar.m_heroLevelText.Text))));
                if (bar.m_barText != null) lines.Add(Str.Clean(Ui.ShownText(bar.m_barText.Text)));
            }
            EndMatch.Tell("result", lines, "endscreen");
        }

        static string RankText(TranslatedMedalInfo info)
        {
            var name = Str.Clean(info.GetRankName());
            return info.IsLegendRank() ? Str.Join(name, info.legendIndex.ToString()) : name;
        }

        // the rank: where you are now, then what changed
        void Rank()
        {
            if (m_keys.Contains("rank")) return;
            var scoop = UnityEngine.Object.FindObjectOfType<RankChangeTwoScoop_NEW>();
            if (scoop == null || !scoop.gameObject.activeInHierarchy) return;
            var prev = Ref.Get<TranslatedMedalInfo>(scoop, "m_prevMedalInfo");
            var cur = Ref.Get<TranslatedMedalInfo>(scoop, "m_currMedalInfo");
            var prevModel = Ref.Get<RankedPlayDataModel>(scoop, "m_prevMedalDataModel");
            var curModel = Ref.Get<RankedPlayDataModel>(scoop, "m_currMedalDataModel");
            if (prev == null || cur == null || prevModel == null || curModel == null) return;
            bool streak = Ref.Get<bool>(scoop, "m_isOnWinStreak");
            var lines = new List<string>();
            var now = RankText(cur);
            lines.Add(A(now != RankText(prev) ? "UI_RANK_CHANGE_UI_NEW_RANK" : "UI_RANK_CHANGE_UI_CURRENT_RANK", now));
            bool same = curModel.StarLevel == prevModel.StarLevel && curModel.Stars == prevModel.Stars;
            bool up = curModel.StarLevel > prevModel.StarLevel || (curModel.StarLevel == prevModel.StarLevel && curModel.Stars > prevModel.Stars);
            if (cur.IsLegendRank() && !prev.IsLegendRank()) up = true;
            if (!same && up)
            {
                if (prevModel.StarMultiplier > 1) lines.Add(A("UI_RANK_CHANGE_UI_STAR_BONUS_MULT", prevModel.StarMultiplier));
                if (streak) lines.Add(A("UI_RANK_CHANGE_UI_WIN_STREAK_MULT", 2));
                lines.Add(A("UI_RANK_CHANGE_UI_EARNED_STARS", prev.starsPerWin * (streak ? 2 : 1)));
            }
            else if (!same) lines.Add(A("UI_RANK_CHANGE_UI_LOST_STARS", 1));
            else if (!curModel.IsLegend && !curModel.IsNewPlayer)
            {
                if (!cur.CanLoseStars()) lines.Add(Str.Word("GLOBAL_RANK_SCRUB_RANK_DESC"));
                else if (!cur.CanLoseLevel()) lines.Add(Str.Word("GLOBAL_RANK_CANT_LOSE_LEVEL"));
            }
            if (!cur.IsLegendRank()) lines.Add(A("UI_RANK_CHANGE_UI_CURRENT_STARS", cur.earnedStars, cur.RankConfig.Stars));
            EndMatch.Tell("rank", lines, "endscreen");
        }

        void CardBack()
        {
            if (m_keys.Contains("cardback")) return;
            var d = UnityEngine.Object.FindObjectOfType<RankedCardBackProgressDisplay>();
            if (d == null || !d.gameObject.activeInHierarchy) return;
            var info = Ref.Get<MedalInfoTranslator>(d, "m_medalInfo");
            if (info == null) return;
            var lines = new List<string>();
            if (info.GetSeasonCardBackWinsRemaining() == 0) lines.Add(Str.Word("GLOBAL_REWARD_CARD_BACK_HEADLINE"));
            else if (d.m_footerText != null) lines.Add(Str.Clean(Ui.ShownText(d.m_footerText.Text)));
            EndMatch.Tell("cardback", lines, "endscreen");
        }

        void RankedReward()
        {
            if (m_keys.Contains("rankedreward")) return;
            var d = UnityEngine.Object.FindObjectOfType<RankedRewardDisplay>();
            if (d == null || !d.gameObject.activeInHierarchy) return;
            EndMatch.Tell("rankedreward", new List<string> { Str.Word("GLUE_RANKED_REWARD_IN_GAME_TITLE"), Str.Word("GLUE_RANKED_REWARD_IN_GAME_FOOTER") }, "endscreen");
        }

        // ---- reading and keys ---------------------------------------------------------------------

        string First()
        {
            if (m_items.Count == 0) return "";
            m_at = Math.Max(0, Math.Min(m_at, m_items.Count - 1));
            var lines = m_items[m_at].Lines;
            return Speech.S(K.MENU_OPTION_FORMAT, lines.Count > 0 ? lines[0] : "", m_at + 1, m_items.Count);
        }

        void Move(int to)
        {
            if (m_items.Count == 0) return;
            m_at = Math.Max(0, Math.Min(m_items.Count - 1, to)); m_line = 0;
            Say(First(), true);
        }

        internal override bool HandleKey()
        {
            int n = m_items.Count;
            if (Keys.Enter.Pressed || Keys.Space.Pressed)
            {
                var hit = Screen.m_hitbox;
                if (hit != null && hit.gameObject.activeInHierarchy) { Log.Info("endscreen: continue"); hit.TriggerRelease(); }
                return true;
            }
            if (Keys.Right.Pressed || Keys.Tab.Pressed) { if (m_at + 1 < n) Move(m_at + 1); return true; }
            if (Keys.Left.Pressed || Keys.ShiftTab.Pressed) { if (m_at > 0) Move(m_at - 1); return true; }
            if (Keys.Home.Pressed) { Move(0); return true; }
            if (Keys.End.Pressed) { Move(n - 1); return true; }
            if (n == 0) return Keys.Back.Pressed;
            var lines = m_items[Math.Max(0, Math.Min(m_at, n - 1))].Lines;
            if (Keys.ShiftUp.Pressed) { if (lines.Count > 0) Say(lines[Math.Min(m_line, lines.Count - 1)], true); return true; }
            if (Keys.ShiftDown.Pressed) { for (int i = m_line; i < lines.Count; i++) Say(lines[i]); m_line = Math.Max(0, lines.Count - 1); return true; }
            if (Keys.Down.Pressed) { if (m_line + 1 < lines.Count) Say(lines[++m_line], true); return true; }
            if (Keys.Up.Pressed) { if (m_line > 0) Say(lines[--m_line], true); return true; }
            return Keys.Back.Pressed;
        }

        internal override string Help()
        {
            return Str.Join(Speech.S(K.MENU_HORIZONTAL_HELP_WITH_BACK_BUTTON, Keys.Enter.Name, Keys.Back.Name), A("PRESS_KEY_TO_CONTINUE", Keys.Enter.Name));
        }

        internal override void Read() { if (m_items.Count > 0) Say(First(), true); }
    }

    // reward track XP as the game shows it (the end of a match, a quest finished): the XP of the game,
    // each quest with its XP, level ups, then the level reached and its XP
    static class Xp
    {
        class State { internal bool Intro; internal int Level = -1; internal int QuestId = -1; }
        static readonly Dictionary<int, State> s_states = new Dictionary<int, State>();

        internal static void Tick()
        {
            var shown = UnityEngine.Object.FindObjectsByType<QuestXpReward>(FindObjectsSortMode.None);
            if (shown.Length == 0) { if (s_states.Count > 0) s_states.Clear(); return; }
            foreach (var x in shown)
            {
                if (x == null || !x.gameObject.activeInHierarchy) continue;
                State st;
                if (!s_states.TryGetValue(x.GetInstanceID(), out st)) s_states[x.GetInstanceID()] = st = new State();
                try { Watch(x, st); } catch (Exception e) { Log.Error(e); }
            }
        }

        static void Watch(QuestXpReward x, State st)
        {
            var model = Ref.Get<RewardTrackDataModel>(x, "m_dataModel");
            if (model == null) return;
            var key = "xp:" + x.GetTrackType();
            var lines = new List<string>();
            if (st.Level < 0) st.Level = model.Level;
            // the game's own XP (the intro of the bar)
            if (!st.Intro && Ref.Get<bool>(x, "m_introShown"))
            {
                st.Intro = true;
                var gained = Ref.Invoke(x, "CalculateIntroXpGained");
                if (gained is int && (int)gained > 0) lines.Add(EndMatch.A("UI_TRACK_REWARD_XP_GAIN", (int)gained));
            }
            // a quest finished (its tile shown)
            var tile = Ref.Get<Hearthstone.UI.Widget>(x, "m_questTileWidget");
            QuestDataModel quest = null;
            try { quest = tile == null ? null : tile.GetDataModel<QuestDataModel>(); } catch { }
            if (quest != null && quest.QuestId != st.QuestId && quest.RewardTrackXp > 0 && tile.gameObject.activeInHierarchy)
            {
                st.QuestId = quest.QuestId;
                lines.Add(EndMatch.A("UI_TRACK_REWARD_XP_GAIN_FROM_QUEST", quest.RewardTrackXp));
                lines.Add(Str.Clean(string.IsNullOrEmpty(quest.Name) ? quest.Description : Str.Join(quest.Name, quest.Description)));
            }
            // a level reached
            if (model.Level > st.Level)
            {
                st.Level = model.Level;
                lines.Add(EndMatch.A("UI_TRACK_REWARD_LEVEL_UP", model.Level));
            }
            if (lines.Count == 0) return;
            // where the track stands now
            lines.Add(Str.Join(Str.Game("GLUE_PROGRESSION_REWARD_TRACK_POPUP_COMPLETE_LEVEL", model.Level), model.XpNeeded > 0 ? model.Xp + "/" + model.XpNeeded : null));
            EndMatch.Tell(key, lines, "xp");
        }
    }

    // toasts: quest progress, a quest finished, an achievement
    static class Toasts
    {
        static readonly HashSet<int> s_seen = new HashSet<int>();

        static bool First(MonoBehaviour b)
        {
            if (b == null || !b || !b.gameObject.activeInHierarchy) return false;
            return s_seen.Add(b.GetInstanceID());
        }

        internal static void Tick()
        {
            if (s_seen.Count > 200) s_seen.Clear();
            try
            {
                foreach (var t in UnityEngine.Object.FindObjectsByType<Hearthstone.Progression.QuestProgressToast>(FindObjectsSortMode.None))
                {
                    var w = t == null ? null : t.GetComponent<Hearthstone.UI.WidgetTemplate>();
                    QuestDataModel q = null;
                    try { q = w == null ? null : w.GetDataModel<QuestDataModel>(); } catch { }
                    if (q == null || !First(t)) continue;
                    var name = Str.Clean(string.IsNullOrEmpty(q.Name) ? q.Description : q.Name);
                    var lines = q.Quota > 0 && q.Progress >= q.Quota
                        ? new List<string> { EndMatch.A("TOAST_QUEST_TOAST_TITLE"), name }
                        : new List<string> { name, q.Quota > 0 ? EndMatch.A("TOAST_QUEST_PROGRESS_TOAST_PROGRESS", q.Progress, q.Quota) : null };
                    EndMatch.Tell(null, lines, "toast");
                }
                foreach (var t in UnityEngine.Object.FindObjectsByType<global::QuestProgressToast>(FindObjectsSortMode.None))
                {
                    if (!First(t)) continue;
                    TellIf(new List<string> { Text(t.m_questTitle), Text(t.m_questDescription), Text(t.m_questProgressCount) });
                }
                foreach (var t in UnityEngine.Object.FindObjectsByType<QuestToast>(FindObjectsSortMode.None))
                {
                    if (!First(t)) continue;
                    TellIf(new List<string> { EndMatch.A("TOAST_QUEST_TOAST_TITLE"), Text(t.m_questName), Text(t.m_requirement) });
                }
                foreach (var t in UnityEngine.Object.FindObjectsByType<AchievementToast>(FindObjectsSortMode.None))
                {
                    if (!First(t)) continue;
                    TellIf(new List<string> { Text(Ref.Get<UberText>(t, "m_text")) });
                }
                // a Mercenaries task done (or progressed): the task as its card shows it
                foreach (var t in UnityEngine.Object.FindObjectsByType<Hearthstone.Progression.LettuceVillageTaskToast>(FindObjectsSortMode.None))
                {
                    var w = t == null ? null : t.GetComponent<Hearthstone.UI.WidgetTemplate>();
                    MercenaryVillageTaskItemDataModel task = null;
                    try { task = w == null ? null : w.GetDataModel<MercenaryVillageTaskItemDataModel>(); } catch { }
                    if (task == null || string.IsNullOrEmpty(task.Title) || !First(t)) continue;
                    bool done = (int)task.TaskStatus == 3 || (task.ProgressNeeded > 0 && task.Progress >= task.ProgressNeeded);
                    TellIf(new List<string> { done ? Str.Word("GLUE_LETTUCE_VILLAGE_TASK_COMPLETE") : null, Campfire.Describe(task) });
                }
            }
            catch (Exception e) { Log.Error(e); }
        }

        // a text the toast shows (not one it hides, nor the prefab's "Uber Text" placeholder)
        static string Text(UberText t)
        {
            if (t == null || !t.gameObject.activeInHierarchy || t.isHidden()) return null;
            var text = Str.Clean(Ui.ShownText(t.Text));
            return string.IsNullOrEmpty(text) || text.StartsWith("Uber Text", StringComparison.OrdinalIgnoreCase) ? null : text;
        }

        // a toast with nothing real to say stays quiet
        static void TellIf(List<string> lines)
        {
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            if (lines.Count > 1 || (lines.Count == 1 && lines[0] != EndMatch.A("TOAST_QUEST_TOAST_TITLE"))) EndMatch.Tell(null, lines, "toast");
        }
    }
}
