using System;
using HSAEnhanced.Core;

namespace HSAEnhanced
{
    // The hooks the port tool puts into the game when Hearthstone Access is not installed
    // (Enhance.StandaloneSites): only game methods.
    public static class Hooks
    {
        // start of UniversalInputManager.UpdateInput(); true: the key press was ours (the game's
        // keyboard handling is skipped this frame). F4 opens and closes the friends list.
        // the frame the game's input update last ran in (Host takes the keys when it does not run)
        internal static int KeysFrame = -1;

        public static bool StandaloneKeys()
        {
            KeysFrame = UnityEngine.Time.frameCount;
            try
            {
                Host.Ensure();
                if (Focus.Typing) return false;
                if (Focus.HandleKeys()) return true;
                if (Core.Key.Of(UnityEngine.KeyCode.F4).Pressed && !(Focus.Top is PanelUI && Dialogs.Shown != null))
                {
                    var button = BnetBarFriendButton.Get();
                    if (button != null) { Core.Click.Peg(button); return true; }
                }
                return false;
            }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of GameplayErrorManager.DisplayMessage(message): the game's error line, spoken
        public static void StandaloneGameError(string message)
        {
            try { Match.GameError(message); } catch (Exception e) { Log.Error(e); }
        }
    }
}
