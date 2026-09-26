using System.Collections.Generic;
using Hearthstone.UI;
using UnityEngine;

namespace HSAEnhanced
{
    // Which menus are used, and texts read off the screen.
    static class Engine
    {
        // Our menus: HSA's screens with every button the game shows added, Game Modes and
        // Adventures built from the game's data, a way back everywhere. Off with the installer's
        // --use-hsa-menus (passed when the mod is built): Hearthstone Access's own menus.
        internal static bool Enabled = true;

        // Texts shown under `root` that are not on buttons, in reading order
        internal static List<string> Texts(GameObject root)
        {
            var found = new List<KeyValuePair<float, string>>();
            var seen = new HashSet<string>();
            if (root == null) return new List<string>();
            foreach (var ut in root.GetComponentsInChildren<UberText>(false))
            {
                if (ut == null || !ut.isActiveAndEnabled) continue;
                if (ut.GetComponentInParent<PegUIElement>() != null || ut.GetComponentInParent<Clickable>() != null) continue;
                var s = Ui.ShownText(ut.Text);
                if (s.Length == 0 || !seen.Add(s)) continue;
                found.Add(new KeyValuePair<float, string>(Ui.ScreenOrderOf(ut), s));
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            var result = new List<string>();
            foreach (var kv in found) result.Add(kv.Value);
            return result;
        }
    }
}
