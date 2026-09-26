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
            var result = new List<string>();
            foreach (var kv in Ui.TextsUnder(root)) result.Add(kv.Value);
            return result;
        }
    }
}
