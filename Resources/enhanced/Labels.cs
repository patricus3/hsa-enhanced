using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HSAEnhanced
{
    static class Labels
    {
        internal static string Norm(string s)
        {
            return Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\w]+", " ").Trim();
        }

        // Same text, or one is the other with words added ("Play" / "Play ranked")
        internal static bool Similar(string a, string b)
        {
            a = Norm(a); b = Norm(b);
            if (a.Length == 0 || b.Length == 0) return a == b;
            if (a == b) return true;
            return (" " + a + " ").Contains(" " + b + " ") || (" " + b + " ").Contains(" " + a + " ");
        }

        internal static bool SimilarToAny(IEnumerable<string> labels, string label)
        {
            foreach (var l in labels) if (Similar(l, label)) return true;
            return false;
        }

        static string[] s_backWords;

        // "Back", "Cancel", "Close", ... in the game's language (and English)
        internal static bool IsBack(string label)
        {
            if (s_backWords == null)
            {
                var words = new List<string> { "back", "go back", "cancel", "close", "done", "exit", "leave" };
                foreach (var key in new[] { "GLOBAL_BACK", "GLOBAL_CANCEL", "GLOBAL_CLOSE", "GLOBAL_DONE", "GLOBAL_EXIT", "GLOBAL_LEAVE" })
                {
                    var s = Str.Game(key);
                    if (!string.IsNullOrEmpty(s)) words.Add(s);
                }
                var goBack = Str.Game("ACCESSIBILITY_SCREEN_GO_BACK");
                if (!string.IsNullOrEmpty(goBack)) words.Add(goBack);
                s_backWords = words.ToArray();
            }
            var n = Norm(label);
            foreach (var w in s_backWords) if (n == Norm(w)) return true;
            return false;
        }
    }
}
