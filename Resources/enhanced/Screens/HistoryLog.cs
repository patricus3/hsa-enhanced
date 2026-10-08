using System;
using System.Collections.Generic;
using System.IO;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // The match announcements kept for review with Y.
    static class HistoryLog
    {
        static readonly List<string> s_entries = new List<string>();
        static HistoryLogUI s_open;
        static string s_file;
        static bool s_fileFailed;

        internal static void Reset()
        {
            if (s_open != null) { var old = s_open; s_open = null; Focus.Pop(old); }
            s_entries.Clear();
            s_file = null;
            s_fileFailed = false;
        }

        internal static void Add(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (line.Length == 0) return;
            s_entries.Add(line);
            if (!Settings.SaveBattleLogs || s_fileFailed) return;
            try
            {
                var gs = GameState.Get();
                var mgr = GameMgr.Get();
                if (gs == null || !gs.IsGameCreated() || mgr == null || mgr.IsBattlegrounds() || mgr.IsMercenaries()) return;
                if (s_file == null) s_file = CreateBattleLogPath(gs, mgr);
                if (s_file != null) File.AppendAllText(s_file, line + Environment.NewLine);
            }
            catch (Exception e)
            {
                s_fileFailed = true;
                Log.Error(e);
            }
        }

        static string CreateBattleLogPath(GameState gs, GameMgr mgr)
        {
            var root = Path.GetDirectoryName(Application.dataPath);
            var folder = Path.Combine(root, "battle logs");
            Directory.CreateDirectory(folder);
            var me = gs.GetFriendlySidePlayer();
            var them = gs.GetOpposingSidePlayer();
            string mine = SafeName(me == null ? null : me.GetName());
            string opponent = SafeName(them == null ? null : them.GetName());
            try
            {
                if (string.IsNullOrEmpty(opponent) || mgr.IsAI())
                    opponent = SafeName(them == null || them.GetHero() == null ? null : them.GetHero().GetName());
            }
            catch { }
            if (string.IsNullOrEmpty(mine)) mine = "Player";
            if (string.IsNullOrEmpty(opponent)) opponent = "Opponent";
            var stem = DateTime.Now.ToString("yyyy-M-d H_mm") + " " + mine + " v " + opponent;
            var path = Path.Combine(folder, stem + ".txt");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, stem + " (" + n + ").txt");
            return path;
        }

        static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        internal static void Open()
        {
            if (s_open != null) { s_open.Read(); return; }
            s_open = new HistoryLogUI();
            Focus.Push(s_open);
            s_open.Read();
        }

        internal static void Close(HistoryLogUI ui)
        {
            if (s_open != ui) return;
            s_open = null;
            Focus.Pop(ui);
            Speech.Say(Speech.S(K.GAMEPLAY_HISTORY_LOG_CLOSE));
        }

        internal static int Count { get { return s_entries.Count; } }
        internal static string At(int index) { return index >= 0 && index < s_entries.Count ? s_entries[index] : ""; }
    }

    class HistoryLogUI : Core.Screen
    {
        int m_at;

        internal override bool Alive { get { return Match.InMatch; } }

        string Entry()
        {
            return Speech.S(K.GAMEPLAY_HISTORY_LOG_ENTRY_FORMAT, HistoryLog.At(m_at), m_at + 1, HistoryLog.Count);
        }

        internal override void Read()
        {
            m_at = HistoryLog.Count - 1;
            Say(HistoryLog.Count == 0 ? Speech.S(K.GAMEPLAY_HISTORY_LOG_EMPTY) : Entry(), true);
        }

        internal override bool HandleKey()
        {
            if (!UnityEngine.Input.anyKeyDown) return false;
            if (Keys.Back.Pressed) { HistoryLog.Close(this); return true; }
            if (HistoryLog.Count == 0) return true;
            if (Keys.Up.Pressed) { if (m_at > 0) { m_at--; Say(Entry(), true); } return true; }
            if (Keys.Down.Pressed) { if (m_at + 1 < HistoryLog.Count) { m_at++; Say(Entry(), true); } return true; }
            if (Keys.ShiftUp.Pressed) { Say(Entry(), true); return true; }
            if (Keys.ShiftDown.Pressed)
            {
                for (int i = m_at + 1; i < HistoryLog.Count; i++) { m_at = i; Say(Entry()); }
                return true;
            }
            return true;
        }

        internal override string Help() { return Speech.S(K.GAMEPLAY_READ_HISTORY_HELP); }
    }
}
