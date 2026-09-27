using System.Collections.Generic;
using Accessibility;
using PegasusShared;
using UnityEngine;

namespace HSAEnhanced
{
    // A Death Knight deck's three rune slots (the rune indicator on the deck tray), as options of
    // HSA's edit-deck menu. Enter on a slot does what a click on it does: the next rune
    // (empty, Blood, Frost, Unholy, empty), and the game updates the deck's rune pattern.
    static class Runes
    {
        // HSA's edit-deck menu of the collection manager
        internal static bool IsEditDeckMenu(object parent, AccessibleMenu menu)
        {
            return parent is AccessibleCollectionManager && menu != null && ReferenceEquals(Ref.Get(parent, "m_editDeckMenu"), menu);
        }

        static RuneIndicatorVisual Indicator()
        {
            var tray = CollectionDeckTray.Get();
            var visual = tray == null ? null : Ref.Get<RuneIndicatorVisual>(tray, "m_runeIndicatorVisual");
            return visual != null && visual && visual.gameObject.activeInHierarchy ? visual : null;
        }

        internal static string Name(RuneType rune)
        {
            switch ((int)rune)
            {
                case 1: return Str.Word("ACCESSIBILITY_READ_CARD_RUNE_BLOOD");
                case 2: return Str.Word("ACCESSIBILITY_READ_CARD_RUNE_FROST");
                case 3: return Str.Word("ACCESSIBILITY_READ_CARD_RUNE_UNHOLY");
                default: return Str.Word("GLUE_TRAINING_HALL_BUTTON_EMPTY_TITLE");
            }
        }

        static string Label(int slot, RuneButton button)
        {
            return Str.Join(Str.Word("GLUE_COLLECTION_RUNES_TOOLTIP_HEADER") + " " + (slot + 1), Name(button.RuneType));
        }

        // one option per slot, named with its rune; empty when the deck has no rune slots
        internal static List<GameButton> Buttons(AccessibleMenu menu)
        {
            var found = new List<GameButton>();
            var visual = Indicator();
            if (visual == null || visual.runeButtons == null) return found;
            for (int i = 0; i < visual.runeButtons.Length; i++)
            {
                var button = visual.runeButtons[i];
                if (button == null || !button || !Ui.IsShown(button)) continue;
                int slot = i;
                found.Add(new GameButton
                {
                    Target = button,
                    Label = Label(slot, button),
                    Click = () =>
                    {
                        button.TriggerRelease();
                        var now = Label(slot, button);
                        Log.Info("rune slot " + (slot + 1) + ": " + button.RuneType);
                        MenuAugment.Refresh(menu);
                        AccessibilityMgr.Output(Ref.Get(menu, "m_parent") as AccessibleComponent, now);
                    }
                });
            }
            return found;
        }
    }
}
