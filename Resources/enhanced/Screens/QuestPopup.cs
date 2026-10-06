using System;
using System.Collections.Generic;
using Hearthstone.DataModels;
using Hearthstone.Progression;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // The quests popup (new daily / weekly quests), read as Hearthstone Access players know it (its
    // behaviour, our code): "Popup", the popup's title, then the quests: Left / Right go through them,
    // Up / Down through a quest's lines (name, rewards, what to do, progress); Enter goes on.
    static class QuestPopup
    {
        static QuestPopupUI s_ui;
        static bool s_claimed;

        internal static QuestNotificationPopup Shown()
        {
            var popup = UnityEngine.Object.FindObjectOfType<QuestNotificationPopup>();
            return popup != null && popup.gameObject.activeInHierarchy ? popup : null;
        }

        internal static void Tick()
        {
            if (!s_claimed)
            {
                s_claimed = true;
                Generic.Claims.Add(go => { var p = Shown(); return p != null && (go.GetComponentInChildren<QuestNotificationPopup>(true) == p || go.GetComponentInParent<QuestNotificationPopup>() == p); });
            }
            var popup = Shown();
            if (s_ui != null && (popup == null || s_ui.Popup != popup)) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && popup != null)
            {
                var ui = new QuestPopupUI(popup);
                if (ui.Quests().Count > 0) { s_ui = ui; Focus.Push(ui); ui.Read(); }
            }
        }
    }

    class QuestPopupUI : Core.Screen
    {
        internal readonly QuestNotificationPopup Popup;
        int m_at, m_line;

        internal QuestPopupUI(QuestNotificationPopup popup) { Popup = popup; }

        internal override bool Alive { get { return Popup != null && Popup && Popup.gameObject.activeInHierarchy; } }

        Hearthstone.UI.Widget Widget { get { return Ref.Get<Hearthstone.UI.Widget>(Popup, "m_widget"); } }

        internal List<QuestDataModel> Quests()
        {
            var list = new List<QuestDataModel>();
            QuestListDataModel model = null;
            try { var w = Widget; model = w == null ? null : w.GetDataModel<QuestListDataModel>(); } catch { }
            if (model != null && model.Quests != null) foreach (var q in model.Quests) if (q != null) list.Add(q);
            return list;
        }

        static string A(string key, params object[] args) { return Speech.S("ACCESSIBILITY_" + key, args); }

        // a quest's lines: its name (a new quest has none: its XP), when it comes, its rewards, what to
        // do, the progress
        static List<string> Lines(QuestDataModel q)
        {
            var lines = new List<string>();
            if (q.DisplayMode == QuestManager.QuestDisplayMode.NextQuestTime) { lines.Add(Str.Clean(q.TimeUntilNextQuest)); return lines; }
            var name = Str.Clean(q.Name);
            if (name.Length > 0) lines.Add(name);
            else if (q.RewardTrackXp > 0) lines.Add(A("UI_QUEST_NO_NAME_XP_ONLY", q.RewardTrackXp));
            if (q.Status == QuestManager.QuestStatus.UNKNOWN && !string.IsNullOrEmpty(q.TimeUntilNextQuest))
                lines.Add(Str.Join(Str.Word("GLUE_QUEST_UPCOMING_HEADER"), Str.Clean(q.TimeUntilNextQuest)));
            var rewards = Str.Join(q.RewardTrackXp > 0 && name.Length > 0 ? Str.Game("GLOBAL_PROGRESSION_REWARD_TRACK_XP", q.RewardTrackXp) : null, Journal.Rewards(q.Rewards));
            if (!string.IsNullOrEmpty(rewards)) lines.Add(A("UI_QUEST_REWARD_DESCRIPTION", rewards));
            lines.Add(Str.Clean(q.Description));
            lines.Add(Str.Clean(q.ProgressMessage));
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            return lines;
        }

        string First(List<QuestDataModel> quests)
        {
            if (quests.Count == 0) return A("UI_QUEST_LOG_NO_QUESTS");
            m_at = Math.Max(0, Math.Min(m_at, quests.Count - 1));
            var lines = Lines(quests[m_at]);
            return Speech.S(K.MENU_OPTION_FORMAT, lines.Count > 0 ? lines[0] : "", m_at + 1, quests.Count);
        }

        string Title(List<QuestDataModel> quests)
        {
            bool weekly = quests.Count > 0 && quests.TrueForAll(q => q.PoolType == Assets.QuestPool.QuestPoolType.WEEKLY);
            return Str.Word(weekly ? "GLOBAL_PROGRESSION_WEEKLY_QUESTS_POPUP_TITLE" : "GLOBAL_PROGRESSION_DAILY_QUESTS_POPUP_TITLE");
        }

        internal override void Read()
        {
            var quests = Quests();
            Log.Info("quests popup: " + quests.Count + " quests");
            Say(Str.Join(A("UI_POPUP"), Title(quests), First(quests)), true);
        }

        internal override bool HandleKey()
        {
            var quests = Quests();
            int n = quests.Count;
            if (Keys.Enter.Pressed || Keys.Space.Pressed || Keys.Back.Pressed)
            {
                Log.Info("quests popup: continue");
                var w = Widget;
                if (w != null) w.TriggerEvent("CODE_HIDE"); else Popup.Hide();
                return true;
            }
            if (n == 0) return false;
            if (Keys.Right.Pressed || Keys.Tab.Pressed) { if (m_at + 1 < n) { m_at++; m_line = 0; Say(First(quests), true); } return true; }
            if (Keys.Left.Pressed || Keys.ShiftTab.Pressed) { if (m_at > 0) { m_at--; m_line = 0; Say(First(quests), true); } return true; }
            if (Keys.Home.Pressed) { m_at = 0; m_line = 0; Say(First(quests), true); return true; }
            if (Keys.End.Pressed) { m_at = n - 1; m_line = 0; Say(First(quests), true); return true; }
            m_at = Math.Max(0, Math.Min(m_at, n - 1));
            var lines = Lines(quests[m_at]);
            if (Keys.ShiftUp.Pressed) { if (lines.Count > 0) Say(lines[Math.Min(m_line, lines.Count - 1)], true); return true; }
            if (Keys.ShiftDown.Pressed) { for (int i = m_line; i < lines.Count; i++) Say(lines[i]); m_line = Math.Max(0, lines.Count - 1); return true; }
            if (Keys.Down.Pressed) { if (m_line + 1 < lines.Count) Say(lines[++m_line], true); return true; }
            if (Keys.Up.Pressed) { if (m_line > 0) Say(lines[--m_line], true); return true; }
            return false;
        }

        internal override string Help() { return Str.Join(A("UI_QUEST_NOTIFICATION_POPUP_HELP"), A("PRESS_KEY_TO_CONTINUE", Keys.Enter.Name)); }
    }
}
