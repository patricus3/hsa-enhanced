using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Accessibility;
using UnityEngine;

namespace HSAEnhanced
{
    // The game's Options screen as it is shown: HSA's menu has graphics, sound options and its own
    // settings; what else the screen shows is added (Language and Signature Card Appearance, chosen
    // through HSA's own dropdown reader like Resolution; Miscellaneous, Privacy, Switch Account).
    // Checkboxes, sliders and dropdowns are not taken as buttons: HSA reads those itself.
    static class OptionsScreen
    {
        // HSA's dropdown reader for each of the screen's dropdowns it has none for
        static readonly ConditionalWeakTable<DropdownControl, object> s_readers = new ConditionalWeakTable<DropdownControl, object>();

        internal static List<GameButton> Buttons(OptionsMenu options)
        {
            var found = new List<GameButton>();
            AddDropdown(found, options, options.m_languageDropdown, "GLOBAL_LANGUAGE_DROPDOWN");
            AddDropdown(found, options, options.m_signatureTextDisplayDropdown, "GLOBAL_OPTIONS_ADVANCED_SIGNATURE");
            foreach (var b in Ui.ClickablesUnder(options.gameObject, go =>
                         go.GetComponent<DropdownControl>() != null || go.GetComponent<ScrollbarControl>() != null || go.GetComponent<CheckBox>() != null))
                found.Add(b);
            return found;
        }

        static void AddDropdown(List<GameButton> found, OptionsMenu options, DropdownControl dropdown, string labelKey)
        {
            if (dropdown == null || !dropdown || !dropdown.gameObject.activeInHierarchy || dropdown.m_selectedItem == null) return;
            var label = Str.Word(labelKey);
            if (label.Length == 0) return;
            object reader;
            if (!s_readers.TryGetValue(dropdown, out reader))
            {
                // done choosing: back to the options, the way HSA's own dropdowns return
                var back = Ref.Method(typeof(OptionsMenu), "BackToMainMenu", 0);
                if (back == null) return;
                var onDone = (Action)Delegate.CreateDelegate(typeof(Action), options, back);
                reader = new AccessibleDropdownControl(options, label, dropdown, onDone);
                s_readers.Add(dropdown, reader);
            }
            var control = (AccessibleDropdownControl)reader;
            string text;
            try { text = control.GetText(); } catch { text = label; }
            found.Add(new GameButton { Target = dropdown, Label = text, Click = () => Ref.Call(options, "OnClickDropdown", control) });
        }
    }
}
