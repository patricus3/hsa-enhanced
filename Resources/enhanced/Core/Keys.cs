using UnityEngine;

namespace HSAEnhanced.Core
{
    // A key with its modifiers. Pressed: down this frame with exactly these modifiers held
    // (Up and Shift+Up are different keys); Enter also takes the keypad's Enter.
    struct Key
    {
        readonly KeyCode m_code;
        readonly bool m_shift, m_ctrl;

        Key(KeyCode code, bool shift, bool ctrl) { m_code = code; m_shift = shift; m_ctrl = ctrl; }

        internal static Key Of(KeyCode code) { return new Key(code, false, false); }
        internal static Key WithShift(KeyCode code) { return new Key(code, true, false); }
        internal static Key WithCtrl(KeyCode code) { return new Key(code, false, true); }

        static bool Held(KeyCode a, KeyCode b) { return Input.GetKey(a) || Input.GetKey(b); }

        internal bool Pressed
        {
            get
            {
                bool down = Input.GetKeyDown(m_code) || m_code == KeyCode.Return && Input.GetKeyDown(KeyCode.KeypadEnter);
                return down
                    && Held(KeyCode.LeftShift, KeyCode.RightShift) == m_shift
                    && Held(KeyCode.LeftControl, KeyCode.RightControl) == m_ctrl
                    && !Held(KeyCode.LeftAlt, KeyCode.RightAlt);
            }
        }

        // as Hearthstone Access names keys: its override for the key ("Enter"), else the key's name
        internal string Name
        {
            get
            {
                var tag = "ACCESSIBILITY_INPUT_KEY_OVERRIDE_" + m_code;
                var name = GameStrings.HasKey(tag) ? GameStrings.Get(tag) : m_code.ToString();
                if (m_ctrl) return Speech.S("ACCESSIBILITY_INPUT_COMMAND_WITH_CTRL_FORMAT", name);
                if (m_shift) return Speech.S("ACCESSIBILITY_INPUT_COMMAND_WITH_MODIFIER_FORMAT", name);
                return name;
            }
        }
    }

    static class Keys
    {
        internal static readonly Key Up = Key.Of(KeyCode.UpArrow), Down = Key.Of(KeyCode.DownArrow);
        internal static readonly Key Left = Key.Of(KeyCode.LeftArrow), Right = Key.Of(KeyCode.RightArrow);
        internal static readonly Key Tab = Key.Of(KeyCode.Tab), ShiftTab = Key.WithShift(KeyCode.Tab);
        internal static readonly Key Home = Key.Of(KeyCode.Home), End = Key.Of(KeyCode.End);
        internal static readonly Key PageUp = Key.Of(KeyCode.PageUp), PageDown = Key.Of(KeyCode.PageDown);
        internal static readonly Key ShiftUp = Key.WithShift(KeyCode.UpArrow), ShiftDown = Key.WithShift(KeyCode.DownArrow);
        internal static readonly Key Enter = Key.Of(KeyCode.Return), Space = Key.Of(KeyCode.Space);
        internal static readonly Key Back = Key.Of(KeyCode.Backspace), Escape = Key.Of(KeyCode.Escape);
        internal static readonly Key Help = Key.Of(KeyCode.F1);

        // the number key down this frame (top row or keypad): 1-9, 0 is 10; null when none
        internal static int? Number()
        {
            for (int i = 0; i <= 9; i++)
                if (Input.GetKeyDown(KeyCode.Alpha0 + i) || Input.GetKeyDown(KeyCode.Keypad0 + i)) return i == 0 ? 10 : i;
            return null;
        }
    }
}
