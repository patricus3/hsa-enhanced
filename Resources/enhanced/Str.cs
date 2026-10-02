using System;
using System.Text;
using System.Text.RegularExpressions;

namespace HSAEnhanced
{
    static class Str
    {
        // Every word the mod says comes from the game's own string tables (the game's and
        // Hearthstone Access's), so it is in the player's language. Without Hearthstone Access the
        // few of its texts the core uses come from Core/OwnStrings.cs (English).

        // A game string, or null when the table lacks it
        internal static string Game(string key, params object[] args)
        {
            try
            {
                if (!GameStrings.HasKey(key))
                {
                    // ours, where Hearthstone Access is not installed (its table is missing)
                    var own = Core.OwnStrings.Get(key, args);
                    return own == null ? null : Clean(own);
                }
                return Clean(args.Length == 0 ? GameStrings.Get(key) : GameStrings.Format(key, args));
            }
            catch (Exception e) { Log.Error(e); return null; }
        }

        // The first of these game strings the tables have, or ""
        internal static string Word(params string[] keys)
        {
            foreach (var k in keys) { var s = Game(k); if (!string.IsNullOrEmpty(s)) return s; }
            return "";
        }

        // words for states, from the game's tables
        internal static string Locked { get { return Word("GLUE_ADVENTURE_LOCKED", "ACCESSIBILITY_SCREEN_MISSION_LOCKED"); } }
        internal static string Unavailable { get { return Word("GLOBAL_NOT_AVAILABLE"); } }
        internal static string NotOwned { get { return Word("GLUE_COLLECTION_DECK_HELPER_REPLACE_UNOWNED_CARD"); } }
        internal static string Completed { get { return Word("ACCESSIBILITY_SCREEN_MISSION_COMPLETED"); } }
        internal static string Back { get { return Word("GLOBAL_BACK"); } }

        static readonly Regex Placeholder = new Regex(@"<PH>\s*");

        // Spoken form of on-screen text: markup and the "<PH>" placeholder marker removed
        static readonly System.Text.RegularExpressions.Regex GameKey = new System.Text.RegularExpressions.Regex("^[A-Z][A-Z0-9]*(_[A-Z0-9]+)+$");

        internal static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            // a text that is still the game's string key (the game looks it up when it draws it): its
            // text, and nothing when the game has none (then it shows nothing either)
            var key = text.Trim();
            if (GameKey.IsMatch(key))
            {
                if (!GameStrings.HasKey(key)) return "";
                text = GameStrings.Get(key);
            }
            text = Placeholder.Replace(text, "");
            text = Core.Speech.Curate(text);
            return text.TrimEnd('.', ':', ' ');
        }

        internal static string Join(params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(p);
            }
            return sb.ToString();
        }
    }
}
