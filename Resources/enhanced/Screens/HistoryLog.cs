using System.Collections.Generic;
using HSAEnhanced.Core;

namespace HSAEnhanced
{
    // The match announcements kept for review with Y.
    static class HistoryLog
    {
        static readonly List<string> s_entries = new List<string>();
        static HistoryLogUI s_open;

        internal static void Reset()
        {
            if (s_open != null) { var old = s_open; s_open = null; Focus.Pop(old); }
            s_entries.Clear();
        }

        internal static void Add(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            s_entries.Add(text.Replace('\n', ' ').Trim());
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
