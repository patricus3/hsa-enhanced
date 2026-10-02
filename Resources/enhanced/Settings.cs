using System;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Our own settings, kept by the game's PlayerPrefs (they stay between sessions); set in the Options
    // screen (OptionsScreen)
    static class Settings
    {
        const string ConfirmEndTurnKey = "HSAEnhanced.ConfirmEndTurn";

        // E asks first while plays are left (HSA's question); off: E ends the turn at once, as Shift+E
        internal static bool ConfirmEndTurn
        {
            get { try { return PlayerPrefs.GetInt(ConfirmEndTurnKey, 1) != 0; } catch { return true; } }
            set { try { PlayerPrefs.SetInt(ConfirmEndTurnKey, value ? 1 : 0); PlayerPrefs.Save(); } catch (Exception e) { Log.Error(e); } }
        }

        // our own navigation and card reading in matches (Combat): HSA's is no longer used (2026-10-01)
        internal static bool OwnCombat { get { return true; } }

        // start of HSA's AccessibleGameplay.HandleEndTurnInput(); true: E ended the turn here
        internal static bool EndTurnWithoutAsking(object gameplay)
        {
            if (ConfirmEndTurn || !GameState.Get().IsInMainOptionMode() || !Bind.END_TURN.Pressed) return false;
            Log.Info("end turn without asking (setting)");
            Ref.Invoke(gameplay, "EndTurn");
            return true;
        }
    }
}
