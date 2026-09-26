using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HSAEnhanced
{
    // Adventure missions as the game sees them: the wing a mission coin belongs to (AdventureWing
    // keeps a list of its bosses: mission id + coin) and whether it can be played now.
    static class Missions
    {
        internal static AdventureWing WingOf(AdventureBossCoin coin, out int mission)
        {
            mission = 0;
            if (coin == null) return null;
            foreach (var wing in UnityEngine.Object.FindObjectsByType<AdventureWing>(FindObjectsSortMode.None))
            {
                var bosses = Ref.Get<IList>(wing, "m_BossCoins");
                if (bosses == null) continue;
                foreach (var boss in bosses)
                    if (ReferenceEquals(Ref.Get(boss, "m_Coin"), coin))
                    {
                        var id = Ref.Get(boss, "m_MissionId");
                        if (id != null) mission = Convert.ToInt32(id);
                        return wing;
                    }
            }
            return null;
        }

        // "not owned" / "locked" / "unavailable", or null when the mission can be started
        internal static string State(AdventureBossCoin coin)
        {
            int mission;
            var wing = WingOf(coin, out mission);
            if (wing == null) return null;
            if (!Ref.Get<bool>(wing, "m_Owned")) return Str.NotOwned;
            if (Ref.Get<bool>(wing, "m_Locked") || !Ref.Get<bool>(wing, "m_Playable")) return Str.Locked;
            bool available = true;
            try { available = mission == 0 || AdventureConfig.IsMissionAvailable(mission); } catch { }
            if (!available) return Str.Locked;
            if (Ref.Field(coin.GetType(), "m_Enabled") != null && !Ref.Get<bool>(coin, "m_Enabled")) return Str.Unavailable;
            return null;
        }

        // What to say when it cannot be started: the state, and what the wing shows about it
        // (its buy button, release date, lock plate)
        internal static string WhyNot(AdventureBossCoin coin)
        {
            var state = State(coin);
            if (state == null) return null;
            int mission;
            var wing = WingOf(coin, out mission);
            var texts = new List<string> { state };
            foreach (var field in new[] { "m_BuyButtonText", "m_ReleaseLabelText" })
            {
                var ut = Ref.Get<UberText>(wing, field);
                if (ut != null && ut.isActiveAndEnabled) Add(texts, Ui.ShownText(ut.Text));
            }
            var plate = Ref.Get<GameObject>(wing, "m_LockPlate");
            if (plate != null && plate.activeInHierarchy)
                foreach (var ut in plate.GetComponentsInChildren<UberText>(false)) Add(texts, Ui.ShownText(ut.Text));
            return Str.Join(texts.ToArray());
        }

        static void Add(List<string> texts, string s) { if (s.Length > 0 && !texts.Contains(s)) texts.Add(s); }
    }
}
