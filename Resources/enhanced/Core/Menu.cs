using System;
using System.Collections.Generic;

namespace HSAEnhanced.Core
{
    // A vertical menu of options (docs/core-spec.md 5.1): Up/Down (Tab/Shift+Tab the same), no wrap
    // (decision 1), Shift+Up again, Home/End, Enter (Space when allowed) selects, Backspace goes back.
    // Each option is read as "name n of count".
    class Menu
    {
        class Option { internal string Text; internal Func<string> GetText; internal Action Click; internal object Key; internal Func<List<string>> Lines; }

        readonly Screen m_owner;
        readonly string m_name;
        readonly Action m_back;
        readonly List<Option> m_options = new List<Option>();
        int m_index;

        internal bool SpaceSelects = false;

        // a row of items: Left/Right (and Tab) move between them, Up/Down read the item's lines
        // (line 0 is its name), Home/End, Enter selects
        internal bool Horizontal;
        int m_line;

        // the line of the item the player is on (kept when the menu is rebuilt on the same item)
        internal int Line { get { return m_line; } set { m_line = Math.Max(0, value); } }

        internal void AddOption(string text, Action click, object key, Func<List<string>> lines) { m_options.Add(new Option { Text = text, Click = click, Key = key, Lines = lines }); }

        internal Menu(Screen owner, string name, Action back = null) { m_owner = owner; m_name = name; m_back = back; }

        internal void AddOption(string text, Action click) { m_options.Add(new Option { Text = text, Click = click }); }

        // key: what the option stands for (a player), so a rebuilt menu keeps the place on it
        internal void AddOption(string text, Action click, object key) { m_options.Add(new Option { Text = text, Click = click, Key = key }); }

        internal string TextAt(int i) { return i >= 0 && i < m_options.Count ? TextOf(m_options[i]) : ""; }

        internal object KeyAt(int i) { return i >= 0 && i < m_options.Count ? m_options[i].Key : null; }

        internal int IndexOfKey(object key)
        {
            if (key == null) return -1;
            for (int i = 0; i < m_options.Count; i++) if (Equals(m_options[i].Key, key)) return i;
            return -1;
        }

        // text read again each time (a live value, such as a checkbox)
        internal void AddOption(Func<string> text, Action click) { m_options.Add(new Option { GetText = text, Click = click }); }

        internal int Count { get { return m_options.Count; } }

        internal void Clear() { m_options.Clear(); m_index = 0; }

        internal int Index
        {
            get { return m_index; }
            set { m_index = Math.Max(0, Math.Min(value, m_options.Count - 1)); }
        }

        string TextOf(Option o)
        {
            if (o.GetText == null) return o.Text ?? "";
            try { return o.GetText() ?? ""; } catch (Exception e) { Log.Error(e); return ""; }
        }

        internal void StartReading(bool readName = true)
        {
            if (readName && !string.IsNullOrEmpty(m_name)) m_owner.Say(m_name);
            ReadCurrent();
        }

        internal void ReadCurrent()
        {
            if (m_options.Count == 0) { m_owner.Say(Speech.S("ACCESSIBILITY_LIST_NO_ITEMS")); return; }
            Index = m_index;
            m_owner.Say(Speech.S("ACCESSIBILITY_MENU_OPTION_FORMAT", TextOf(m_options[m_index]), m_index + 1, m_options.Count));
        }

        void MoveTo(int i)
        {
            if (i < 0 || i >= m_options.Count) return;
            m_index = i;
            m_line = 0;
            ReadCurrent();
        }

        // the current item's lines: its name, then what its option adds
        List<string> LinesOf(Option o)
        {
            var lines = new List<string> { TextOf(o) };
            if (o.Lines == null) return lines;
            try
            {
                var more = o.Lines();
                if (more != null) foreach (var l in more) if (!string.IsNullOrEmpty(l)) lines.Add(l);
            }
            catch (Exception e) { Log.Error(e); }
            return lines;
        }

        bool HorizontalKeys()
        {
            int n = m_options.Count;
            if (Keys.Right.Pressed || Keys.Tab.Pressed) { MoveTo(m_index + 1); return true; }
            if (Keys.Left.Pressed || Keys.ShiftTab.Pressed) { MoveTo(m_index - 1); return true; }
            if (n == 0) return false;
            Index = m_index;
            var lines = LinesOf(m_options[m_index]);
            if (m_line >= lines.Count) m_line = lines.Count - 1;
            if (Keys.Down.Pressed) { if (m_line + 1 < lines.Count) m_owner.Say(lines[++m_line]); return true; }
            if (Keys.Up.Pressed) { if (m_line > 0) m_owner.Say(lines[--m_line]); return true; }
            if (Keys.ShiftUp.Pressed) { m_owner.Say(m_line == 0 ? Speech.S("ACCESSIBILITY_MENU_OPTION_FORMAT", lines[0], m_index + 1, n) : lines[m_line]); return true; }
            if (Keys.ShiftDown.Pressed) { for (int i = m_line; i < lines.Count; i++) m_owner.Say(lines[i]); m_line = lines.Count - 1; return true; }
            return false;
        }

        internal bool HandleKey()
        {
            if (Horizontal)
            {
                if (HorizontalKeys()) return true;
            }
            else
            {
            if (Keys.Down.Pressed || Keys.Tab.Pressed) { MoveTo(m_index + 1); return true; }
            if (Keys.Up.Pressed || Keys.ShiftTab.Pressed) { MoveTo(m_index - 1); return true; }
            if (Keys.ShiftUp.Pressed) { ReadCurrent(); return true; }
            }
            if (Keys.Home.Pressed) { MoveTo(0); return true; }
            if (Keys.End.Pressed) { MoveTo(m_options.Count - 1); return true; }
            if (Keys.Enter.Pressed || SpaceSelects && Keys.Space.Pressed)
            {
                if (m_options.Count == 0) return true;
                Index = m_index;
                var o = m_options[m_index];
                Log.Info("option: " + TextOf(o));
                try { if (o.Click != null) o.Click(); } catch (Exception e) { Log.Error(e); }
                return true;
            }
            if (Keys.Back.Pressed && m_back != null)
            {
                try { m_back(); } catch (Exception e) { Log.Error(e); }
                return true;
            }
            return false;
        }

        // the names older code of ours uses
        internal void SetIndex(int i) { Index = i; }
        internal int GetNumItems() { return Count; }
        internal bool HandleAccessibleInput() { return HandleKey(); }
        internal string GetHelp() { return Help(); }
        internal string Name { get { return m_name; } }

        internal string Help()
        {
            if (m_options.Count == 0)
                return Speech.S("ACCESSIBILITY_LIST_NO_ITEMS") + Speech.S("ACCESSIBILITY_FORMATTING_PERIOD") + " " + Speech.S("ACCESSIBILITY_PRESS_KEY_TO_GO_BACK", Keys.Back.Name);
            if (Horizontal)
                return m_back != null
                    ? Speech.S("ACCESSIBILITY_MENU_HORIZONTAL_HELP_WITH_BACK_BUTTON", Keys.Enter.Name, Keys.Back.Name)
                    : Speech.S("ACCESSIBILITY_MENU_HORIZONTAL_HELP_NO_BACK_BUTTON", Keys.Enter.Name);
            return m_back != null
                ? Speech.S("ACCESSIBILITY_MENU_HELP_WITH_BACK_BUTTON", Keys.Enter.Name, Keys.Back.Name)
                : Speech.S("ACCESSIBILITY_MENU_HELP_NO_BACK_BUTTON", Keys.Enter.Name);
        }
    }
}
