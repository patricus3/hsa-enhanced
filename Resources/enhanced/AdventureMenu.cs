using System;
using System.Collections.Generic;
using Accessibility;

namespace HSAEnhanced
{
    // The adventure chooser: one option per adventure mode the game's chooser holds (HSA lists
    // only a fixed set of adventures and modes). Choosing still goes through HSA, which selects
    // the mode and presses the game's Choose button, or says why it cannot.
    static class AdventureMenu
    {
        // Replaces AccessibleAdventureScene.SetupAndReadChooseAdventureMenu; false leaves it to HSA
        internal static bool Build(object scene)
        {
            if (!Engine.Enabled) return false;
            // every adventure the game has that can be played now (HSA's list only has the chooser's buttons)
            var adventures = new List<AdventureDef>();
            var all = AdventureScene.Get() == null ? null : AdventureScene.Get().GetSortedAdventureDefs();
            if (all != null) foreach (var d in all) if (d != null && d.IsActiveAndPlayable()) adventures.Add(d);
            if (adventures.Count == 0) adventures = Ref.Get<List<AdventureDef>>(scene, "adventures");
            if (adventures == null || adventures.Count == 0) return false;

            var menu = new AccessibleMenu(scene as AccessibleComponent, LocalizationUtils.Get(LocalizationKey.SCREEN_CHOOSE_ADVENTURE_SCREEN_MENU_TITLE), () => Ref.Call(scene, "OnGoBackToHub"));
            var sorted = new List<AdventureDef>();
            foreach (var d in adventures) if (d != null) sorted.Add(d);
            sorted.Sort((a, b) => a.GetSortOrder().CompareTo(b.GetSortOrder()));

            foreach (var def in sorted)
            {
                var adventure = def.GetAdventureId();
                var name = Str.Clean(def.GetAdventureName());
                var modes = def.GetSortedSubDefs();
                if (modes == null) continue;
                foreach (var sub in modes)
                {
                    if (sub == null) continue;
                    var mode = sub.GetAdventureModeId();
                    bool playable = true;
                    try { playable = AdventureConfig.CanPlayMode(adventure, mode, true); } catch { }
                    string label;
                    if (playable)
                        label = Str.Join(name, Str.Clean(First(sub.GetShortName(), sub.GetDescription())));
                    else
                        label = Str.Join(name, Str.Clean(First(sub.GetLockedShortName(), sub.GetShortName())), Str.T("ACCESSIBILITY_ENH_LOCKED", "locked"),
                                         Str.Clean(First(sub.GetRequirementsDescription(), sub.GetLockedDescription())));
                    menu.AddOption(label, () => Choose(scene, adventure, mode));
                }
            }
            menu.AddOption(LocalizedText.SCREEN_GO_BACK, () => Ref.Call(scene, "OnGoBackToHub"));

            Ref.Set(scene, "m_curMenu", menu);
            var state = Ref.Field(scene.GetType(), "m_curState");
            if (state != null) state.SetValue(scene, Enum.Parse(state.FieldType, "CHOOSING_ADVENTURE"));
            menu.StartReading();
            return true;
        }

        // What clicking the mode and then Choose does. The game saves the selection first; pressing
        // Choose while that save is pending makes its next request fail and leaves the chooser stuck,
        // so Choose waits for the save.
        static void Choose(object scene, AdventureDbId adventure, AdventureModeDbId mode)
        {
            var config = AdventureConfig.Get();
            if (config == null) return;
            if (config.GetSelectedAdventure() != adventure || config.GetSelectedMode() != mode) config.SetSelectedAdventureMode(adventure, mode);
            FallbackWatcher.Run(PressChooseWhenSaved(scene));
        }

        static System.Collections.IEnumerator PressChooseWhenSaved(object scene)
        {
            var until = UnityEngine.Time.unscaledTime + 5f;
            while (UnityEngine.Time.unscaledTime < until && SavePending()) yield return null;
            yield return new UnityEngine.WaitForSecondsRealtime(0.2f);
            var trays = UnityEngine.Object.FindObjectsByType<AdventureChooserTray>(UnityEngine.FindObjectsSortMode.None);
            var tray = trays.Length == 0 ? null : trays[0];
            var choose = tray == null ? null : tray.GetComponentInChildren<PlayButton>(false);
            if (choose == null) { Log.Info("adventure: no Choose button"); yield break; }
            if (choose.IsEnabled()) { Log.Info("adventure: Choose"); choose.TriggerRelease(); }
            else AccessibilityMgr.Output(scene as AccessibleComponent, Ui.LabelOf(choose));
        }

        static bool SavePending()
        {
            var mgr = GameSaveDataManager.Get();
            var pending = mgr == null ? null : Ref.Get(mgr, "m_isRequestPendingForKey") as System.Collections.IDictionary;
            if (pending == null) return false;
            foreach (System.Collections.DictionaryEntry e in pending) if (e.Value is bool && (bool)e.Value) return true;
            return false;
        }

        static string First(string a, string b) { return string.IsNullOrEmpty(Str.Clean(a)) ? b : a; }
    }
}
