using System;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Hearthstone Access's "game speed" (docs/core-spec.md 3.6): after each match announcement the game
    // waits while it is spoken, for (text length / characters per second) whole seconds, so the board does
    // not run ahead of the speech. The characters per second come from the level, 1 to 10 (3 by default),
    // which F11 / F12 change. In games against other players (and Battlegrounds) there is no waiting and
    // the level cannot be changed, as in Hearthstone Access. Any key cuts the wait short.
    // The wait is the game's own: GameState.CanProcessPowerQueue says no while it lasts (a hook).
    static class Pacing
    {
        static readonly int[] Cps = { 10, 12, 15, 17, 20, 22, 25, 27, 30, 1000 };
        static float s_until;

        // a game the pace applies to: one against the computer
        static bool Paced
        {
            get
            {
                var mgr = GameMgr.Get();
                if (mgr == null || GameState.Get() == null) return false;
                try { return mgr.IsAI() && !mgr.IsBattlegrounds() && !mgr.IsSpectator(); } catch { return false; }
            }
        }

        static bool InPlayersGame
        {
            get
            {
                var mgr = GameMgr.Get();
                if (mgr == null || GameState.Get() == null) return false;
                try { return !mgr.IsAI() || mgr.IsBattlegrounds(); } catch { return false; }
            }
        }

        // a match announcement was said: the game waits while it is spoken
        internal static void Wait(string text)
        {
            if (string.IsNullOrEmpty(text) || !Paced) return;
            int level = Math.Max(1, Math.Min(10, Settings.GameSpeed));
            int seconds = text.Length / Cps[level - 1];
            if (seconds <= 0) return;
            s_until = Mathf.Max(s_until, Time.unscaledTime) + seconds;
        }

        // the hook's question: hold the game's power queue now?
        internal static bool Holding
        {
            get
            {
                if (s_until <= 0 || Time.unscaledTime >= s_until) return false;
                if (!Paced) { s_until = 0; return false; }
                return true;
            }
        }

        // any key: the wait ends (Hearthstone Access interrupted the text being spoken)
        internal static void KeyPressed() { s_until = 0; }

        // F12 one level faster, F11 one slower; at either end nothing happens
        internal static bool SpeedKeys()
        {
            bool up = Bind.INCREASE_GAME_SPEED.Pressed, down = Bind.DECREASE_GAME_SPEED.Pressed;
            if (!up && !down) return false;
            if (InPlayersGame) { Speech.Say(Speech.S(K.GLOBAL_GAME_SPEED_CANNOT_ADJUST_VS_PLAYERS)); return true; }
            int level = Math.Max(1, Math.Min(10, Settings.GameSpeed));
            int next = Math.Max(1, Math.Min(10, level + (up ? 1 : -1)));
            if (next == level) return true;
            Settings.GameSpeed = next;
            Log.Info("game speed " + next);
            Speech.Say(Speech.S(K.GLOBAL_GAME_SPEED, next));
            return true;
        }
    }
}
