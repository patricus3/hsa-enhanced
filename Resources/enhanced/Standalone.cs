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
                // the game acts on Escape when it is let go: the press was ours, so is the release
                if (s_escapeOurs && UnityEngine.Input.GetKeyUp(UnityEngine.KeyCode.Escape)) { s_escapeOurs = false; return true; }
                if (Focus.HandleKeys()) return true;
                if (Keys.Escape.Pressed && Escape()) { s_escapeOurs = true; return true; }
                if (Core.Key.Of(UnityEngine.KeyCode.F4).Pressed && !(Focus.Top is PanelUI && Dialogs.Shown != null))
                {
                    var button = BnetBarFriendButton.Get();
                    if (button != null) { Core.Click.Peg(button); return true; }
                }
                return false;
            }
            catch (Exception e) { Log.Error(e); return false; }
        }

        static bool s_escapeOurs;

        static readonly System.Reflection.MethodInfo EscapeKey = typeof(BnetBar).GetMethod("HandleEscapeKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        // Escape as the game means it (closes the menu that is open, else opens the game menu), done
        // on the press: in a match the release did not reach the game's menu. While a target or a
        // Choose One is up, Escape stays the game's own cancel.
        static bool Escape()
        {
            var gs = GameState.Get();
            if (gs != null && (gs.IsInTargetMode() || gs.IsInSubOptionMode())) return false;
            var bar = BnetBar.Get();
            if (bar == null || EscapeKey == null) return false;
            Log.Info("keys: Escape (game menu)");
            try { EscapeKey.Invoke(bar, null); } catch (Exception e) { Log.Error(e); }
            return true;
        }

        // start of EmoteHandler.HandleInput / EnemyEmoteHandler.HandleInput; true: skipped. The game
        // closes the emote tray when the mouse is not over it; while our emote menu is open it stays.
        public static bool StandaloneEmoteInput()
        {
            try { return EmoteUI.IsOpen; } catch { return false; }
        }

        // start of GameplayErrorManager.DisplayMessage(message): the game's error line, spoken
        public static void StandaloneGameError(string message)
        {
            try { Match.GameError(message); } catch (Exception e) { Log.Error(e); }
        }
    }
}
