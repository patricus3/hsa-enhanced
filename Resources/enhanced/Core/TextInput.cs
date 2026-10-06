using System;
using UnityEngine;

namespace HSAEnhanced.Core
{
    // Typing into the game's own text field (the one chat uses): Enter finishes, Escape cancels.
    // While it is open the game gives it every key, so nothing else reacts to typing.
    static class TextInput
    {
        static GameObject s_owner;

        // Hearthstone Access's keys on (true) or off while a game text box is open
        internal static void HsaKeys(bool on)
        {
        }

        internal static bool Ask(string prompt, Action<string> done, Action cancelled = null)
        {
            var input = UniversalInputManager.Get();
            if (input == null) return false;
            if (s_owner == null)
            {
                s_owner = new GameObject("HSAEnhancedTextInput");
                UnityEngine.Object.DontDestroyOnLoad(s_owner);
            }
            Speech.Say(prompt, true);
            var parms = new UniversalInputManager.TextInputParams
            {
                m_owner = s_owner,
                m_rect = new Rect(0.3f, 0.45f, 0.4f, 0.06f),
                m_maxCharacters = 512,
                m_showVirtualKeyboard = false,
                m_completedCallback = text =>
                {
                    Log.Info("text input: done");
                    try { done(text ?? ""); } catch (Exception e) { Log.Error(e); }
                },
                m_canceledCallback = (byUser, requester) =>
                {
                    Log.Info("text input: cancelled");
                    try { if (cancelled != null) cancelled(); } catch (Exception e) { Log.Error(e); }
                },
            };
            input.UseTextInput(parms, true);
            return true;
        }
    }
}
