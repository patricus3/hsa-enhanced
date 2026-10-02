using System.Collections.Generic;

namespace HSAEnhanced.Core
{
    // Our own wording for the few texts the core needs that the game's tables lack. Used only when
    // Hearthstone Access (whose table normally has them) is not installed. English only.
    static class OwnStrings
    {
        static readonly Dictionary<string, string> Texts = new Dictionary<string, string>
        {
            { "ACCESSIBILITY_FORMATTING_PERIOD", "." },
            { "ACCESSIBILITY_FORMATTING_SENTENCE_ENDING_CHARACTERS", ".!?;" },
            { "ACCESSIBILITY_FORMATTING_LIST_SEPARATOR", "," },
            { "ACCESSIBILITY_FORMATTING_LIST_FINAL_SEPARATOR", "and" },
            { "ACCESSIBILITY_INPUT_COMMAND_WITH_CTRL_FORMAT", "Control {0}" },
            { "ACCESSIBILITY_INPUT_COMMAND_WITH_MODIFIER_FORMAT", "Shift {0}" },
            { "ACCESSIBILITY_INPUT_KEY_OVERRIDE_Return", "Enter" },
            { "ACCESSIBILITY_LIST_NO_ITEMS", "Empty" },
            { "ACCESSIBILITY_MENU_OPTION_FORMAT", "{0}, {1} of {2}" },
            { "ACCESSIBILITY_MENU_HELP_NO_BACK_BUTTON", "Up and down arrows move through the list. {0} chooses" },
            { "ACCESSIBILITY_MENU_HELP_WITH_BACK_BUTTON", "Up and down arrows move through the list. {0} chooses, {1} returns" },
            { "ACCESSIBILITY_PRESS_KEY_TO_GO_BACK", "{0} returns" },
            { "ACCESSIBILITY_OPTIONS_MENU_CHECKBOX_CHECKED", "on" },
            { "ACCESSIBILITY_OPTIONS_MENU_CHECKBOX_NOT_CHECKED", "off" },
            { "ACCESSIBILITY_SCREEN_MISSION_COMPLETED", "Done" },
            { "ACCESSIBILITY_SCREEN_MISSION_LOCKED", "Locked" },
            { "ACCESSIBILITY_UI_SOCIAL_MENU_NAME", "Friends" },
            { "ACCESSIBILITY_HUB_MAIN_MENU_TITLE", "Main menu" },
            { "ACCESSIBILITY_GLOBAL_SWITCH_FORMAT", "Format" },
            { "ACCESSIBILITY_UI_SOCIAL_BATTLETAG_COPIED_TO_CLIPBOARD", "Copied" },
            { "ACCESSIBILITY_UI_SOCIAL_CHAT_MESSAGE_RECEIVED", "{0} said: {1}" },
            { "ACCESSIBILITY_UI_SOCIAL_CHAT_MESSAGE_SENT", "You said to {0}: {1}" },
            { "ACCESSIBILITY_UI_SOCIAL_CHAT_SEND_MESSAGE", "Write a message" },
            { "ACCESSIBILITY_UI_SOCIAL_CHAT_TYPE_MESSAGE_PROMPT", "Message. Enter sends, Escape cancels" },
            { "ACCESSIBILITY_UI_SOCIAL_INVITE_FRIEND_TO_BG", "Invite to your Battlegrounds party" },
        };

        // Hearthstone Access's own table (Strings/<language>/ACCESSIBILITY.txt, the game's file format),
        // which the installer puts in the game folder from its download also when HSA itself is not
        // installed: the game does not load it then, so we read it (English under the player's language)
        static Dictionary<string, string> s_table;

        static Dictionary<string, string> Table()
        {
            if (s_table != null) return s_table;
            s_table = new Dictionary<string, string>();
            Load(Locale.enUS);
            try { var locale = Localization.GetLocale(); if (locale != Locale.enUS) Load(locale); } catch (System.Exception e) { Log.Error(e); }
            Log.Info("strings: " + s_table.Count + " texts of Hearthstone Access's table");
            return s_table;
        }

        static void Load(Locale locale)
        {
            try
            {
                var path = GameStrings.GetAssetPath(locale, "ACCESSIBILITY.txt");
                if (!System.IO.File.Exists(path)) return;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var tab = line.IndexOf('\t');
                    if (tab <= 0 || line.StartsWith("TAG\t") || line.StartsWith("#")) continue;
                    var end = line.IndexOf('\t', tab + 1);
                    var text = end < 0 ? line.Substring(tab + 1) : line.Substring(tab + 1, end - tab - 1);
                    if (text.Length == 0) continue;
                    s_table[line.Substring(0, tab)] = text.Replace("\\n", "\n");
                }
            }
            catch (System.Exception e) { Log.Error(e); }
        }

        // null when we have no text for it either
        internal static string Get(string key, params object[] args)
        {
            string text;
            if (Table().TryGetValue(key, out text))
            {
                try { return args == null || args.Length == 0 ? GameStrings.ParseLanguageRules(text) : GameStrings.FormatLocalizedString(text, args); }
                catch { return text; }
            }
            if (!Texts.TryGetValue(key, out text)) return null;
            if (args == null || args.Length == 0) return text;
            try { return string.Format(text, args); } catch { return text; }
        }
    }
}
