using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // The end of a match: the game's end screen takes a click only once its flow is ready (network
    // data, achievements, rewards, rank, reward track XP) and nothing blocks it (animations, reward
    // overlays). When Enter does nothing there, the log says what it is waiting for.
    static class EndScreenLog
    {
        static string s_last;

        // every frame, from FallbackWatcher
        internal static void Tick()
        {
            try
            {
                var gs = GameState.Get();
                if (gs == null || !gs.IsGameOver()) { s_last = null; return; }
                var screen = UnityEngine.Object.FindObjectOfType<EndGameScreen>();
                if (screen == null) return;
                var state = State(screen);
                bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
                if (state == s_last && !enter) return;
                s_last = state;
                Log.Info("end screen" + (enter ? " (Enter)" : "") + ": " + state);
            }
            catch (Exception e) { Log.Error(e); }
        }

        static string State(EndGameScreen screen)
        {
            var parts = new List<string> { screen.GetType().Name };
            foreach (var f in new[] { "m_shown", "m_netCacheReady", "m_achievesReady", "m_rewardsLoaded", "m_shouldShowRankChange", "m_rankChangeReady",
                                      "m_medalInfoUpdated", "m_shouldShowRankedCardBackProgress", "m_shouldShowRewardXpGains",
                                      "m_isShowingMercenariesExperienceRewards", "m_finishedShowingMercenariesExperienceRewards", "m_isShowingPetRewards" })
            {
                var field = Ref.Field(typeof(EndGameScreen), f);
                if (field != null && field.FieldType == typeof(bool)) parts.Add(f.Substring(2) + "=" + field.GetValue(screen));
            }
            try { parts.Add("blocking anim=" + screen.IsPlayingBlockingAnim()); } catch { }
            try { var xp = Hearthstone.Progression.RewardXpNotificationManager.Get(); if (xp != null) parts.Add("reward xp ready=" + xp.IsReady); } catch { }
            var hitbox = screen.m_hitbox;
            parts.Add("hitbox " + (hitbox == null ? "missing" : (hitbox.gameObject.activeInHierarchy ? "shown" : "hidden") + ", listened " + Ui.Listened(hitbox)));
            var ctx = Hearthstone.UI.UIContext.GetRoot();
            if (ctx != null && ctx.ShowingPopups()) { var p = ctx.GetLatestPopup(); parts.Add("popup " + (p == null || p.PopupInstance == null ? "?" : p.PopupInstance.name)); }
            var dialog = Ref.Get<DialogBase>(DialogManager.Get(), "m_currentDialog");
            if (dialog != null && dialog.gameObject.activeInHierarchy) parts.Add("dialog " + dialog.name);
            return string.Join(", ", parts.ToArray());
        }
    }
}
