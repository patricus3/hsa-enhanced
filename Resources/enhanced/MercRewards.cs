using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // After a Mercenaries battle the end screen's first click shows what the mercenaries got (their
    // experience, a level up) on its own overlay, which waits for its own click (DISMISS_TWO_SCOOP);
    // HSA does not know it, so its Enter on the end screen did nothing more while it was up. It is
    // read here from the overlay's own data (each mercenary, the experience gained, its level, a level
    // up), in the game's words, and Continue closes it the way its click does.
    static class MercRewards
    {
        static MercRewardsUI s_ui;
        static readonly HashSet<int> s_closed = new HashSet<int>();     // closed ones stay a few seconds before they go

        // from FallbackWatcher, twice a second
        internal static void Tick()
        {
            try
            {
                MercenariesExperienceTwoScoop shown = null;
                foreach (var s in UnityEngine.Object.FindObjectsByType<MercenariesExperienceTwoScoop>(FindObjectsSortMode.None))
                    if (s != null && s.isActiveAndEnabled && !s_closed.Contains(s.GetInstanceID()) && Ref.Get(s, "m_onClosedCallback") != null && Lines(s).Count > 0) { shown = s; break; }
                if (shown == null) { Hide(); return; }
                if (s_ui != null && s_ui.Scoop == shown) return;
                Hide();
                s_ui = new MercRewardsUI(shown);
                Log.Info("mercenaries: rewards " + string.Join(" | ", Lines(shown).ToArray()));
                Core.Focus.Push(s_ui);
                s_ui.Start();
            }
            catch (Exception e) { Log.Error(e); Hide(); }
        }

        static void Hide()
        {
            if (s_ui == null) return;
            var ui = s_ui;
            s_ui = null;
            Core.Focus.Pop(ui);
        }

        // each mercenary as the overlay shows it: name, +N xp, its level, Level up!
        internal static List<string> Lines(MercenariesExperienceTwoScoop scoop)
        {
            var lines = new List<string>();
            var rewards = Ref.Get<List<MercenaryExpRewardData>>(scoop, "m_mercenaryExpRewards");
            if (rewards == null) return lines;
            foreach (var r in rewards)
            {
                if (r == null) continue;
                var merc = CollectionManager.Get().GetMercenary(r.MercenaryId);
                var name = merc == null ? null : Str.Clean(merc.m_mercName);
                int level = GameUtils.GetMercenaryLevelFromExperience(r.FinalExperience);
                lines.Add(Str.Join(name, Str.Game("GLUE_LETTUCE_MERCENARY_EXP_GAIN", r.Amount),
                    Str.Word("GLUE_LETTUCE_MERCENARY_LEVEL_LABEL") + " " + level,
                    r.NumberOfLevelUps > 0 ? Str.Word("GLUE_LETTUCE_MERCENARY_LEVEL_UP") : null));
            }
            return lines;
        }

        internal static void Close(MercenariesExperienceTwoScoop scoop)
        {
            Log.Info("mercenaries: rewards closed");
            s_closed.Add(scoop.GetInstanceID());
            Hide();
            Ref.Call(scoop, "OnClosed");
        }
    }

    class MercRewardsUI : Core.Screen
    {
        internal readonly MercenariesExperienceTwoScoop Scoop;
        readonly Core.Menu m_menu;

        internal MercRewardsUI(MercenariesExperienceTwoScoop scoop)
        {
            Scoop = scoop;
            m_menu = new Core.Menu(this, Core.Speech.S(Core.K.UI_POPUP), () => MercRewards.Close(Scoop));
            var ui = this;
            foreach (var l in MercRewards.Lines(scoop)) { var line = l; m_menu.AddOption(line, () => ui.Say(line)); }
            m_menu.AddOption(Str.Word("GLOBAL_CONTINUE"), () => MercRewards.Close(Scoop));
        }

        internal void Start() { m_menu.StartReading(); }

        internal override bool HandleKey() { return m_menu.HandleKey(); }

        internal override string Help() { return m_menu.GetHelp(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
