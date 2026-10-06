using System;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace HSAEnhanced.Core
{
    // Our speech: straight to the screen reader (Prism on Windows, the VoiceOver bridge on the Mac),
    // not through Hearthstone Access's queue. Texts are tidied the way the spec describes
    // (docs/core-spec.md 3.7), without the English-only uppercase rule (decision 7).
    static class Speech
    {
        static MethodInfo s_output, s_silence, s_detect;
        static bool s_bound;

        // HSAPrism.Speech on Windows; the Mac's stand-in DavyKager.Tolk; both have the same calls
        static void Bind()
        {
            if (s_bound) return;
            s_bound = true;
            foreach (var name in new[] { "HSAPrism", "TolkDotNet" })
            {
                try { Assembly.Load(name); } catch { }
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("HSAPrism.Speech") ?? asm.GetType("DavyKager.Tolk");
                if (t == null) continue;
                s_output = t.GetMethod("Output", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(bool) }, null);
                s_silence = t.GetMethod("Silence", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                s_detect = t.GetMethod("DetectScreenReader", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                // loaded by Hearthstone Access when it is there; otherwise by us
                try
                {
                    var isLoaded = t.GetMethod("IsLoaded", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    var load = t.GetMethod("Load", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (load != null && (isLoaded == null || !(bool)isLoaded.Invoke(null, null))) load.Invoke(null, null);
                }
                catch (Exception e) { Log.Error(e); }
                Log.Info("speech: " + t.FullName);
                return;
            }
            Log.Info("speech: no screen reader bridge found");
        }

        internal static void Say(string text, bool interrupt = false)
        {
            text = Curate(text);
            if (text.Length == 0) return;
            if (!Allowed()) return;
            Bind();
            try { if (s_output != null) s_output.Invoke(null, new object[] { text, interrupt }); }
            catch (Exception e) { Log.Error(e); }
        }

        internal static void Silence()
        {
            Bind();
            try { if (s_silence != null) s_silence.Invoke(null, null); } catch (Exception e) { Log.Error(e); }
        }

        // SAPI and OneCore don't stop on a key press by themselves; screen readers do (decision 3)
        internal static bool UsingSapi
        {
            get
            {
                Bind();
                try { return s_detect != null && (s_detect.Invoke(null, null) as string) == "SAPI"; } catch { return false; }
            }
        }

        // the game window has focus, or the player lets it talk in the background (on by default)
        static bool Allowed()
        {
            try
            {
                var app = Hearthstone.HearthstoneApplication.Get();
                if (app == null || app.HasFocus()) return true;
                return true;
            }
            catch { return true; }
        }

        // a string from the game's tables (Hearthstone Access's are loaded into them too);
        // a missing one is spoken as its tag (decision 6)
        internal static string S(string key, params object[] args)
        {
            try
            {
                if (!GameStrings.HasKey(key)) return OwnStrings.Get(key, args) ?? key;
                return args.Length == 0 ? GameStrings.Get(key) : GameStrings.Format(key, args);
            }
            catch (Exception e) { Log.Error(e); return key; }
        }

        // "A, B and C" in the game's language
        internal static string HumanizeList(System.Collections.Generic.IList<string> items)
        {
            if (items == null || items.Count == 0) return "";
            if (items.Count == 1) return items[0];
            var sep = S("ACCESSIBILITY_FORMATTING_LIST_SEPARATOR");
            var last = S("ACCESSIBILITY_FORMATTING_LIST_FINAL_SEPARATOR");
            var sb = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(i == items.Count - 1 ? " " + last + " " : sep + " ");
                sb.Append(items[i]);
            }
            return sb.ToString();
        }

        static readonly Regex Tag = new Regex("<[^>]*>");
        static readonly Regex StatsBonus = new Regex(@"\(\+\d Attack/\+\d Health\)");
        static readonly Regex Spaces = new Regex(" {2,}");

        internal static string Curate(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            try
            {
                var period = S("ACCESSIBILITY_FORMATTING_PERIOD");
                var enders = S("ACCESSIBILITY_FORMATTING_SENTENCE_ENDING_CHARACTERS");
                text = text.Trim();
                // each line its own sentence when it is a bold keyword line ("Battlecry")
                var lines = text.Split('\n');
                var sb = new StringBuilder();
                for (int i = 0; i < lines.Length; i++)
                {
                    sb.Append(lines[i]);
                    if (i == lines.Length - 1) break;
                    var l = lines[i].Trim();
                    sb.Append(l.StartsWith("<b>") && l.EndsWith("</b>") ? period + " " : " ");
                }
                text = sb.ToString().Replace("</b> <b>", "</b>" + period + " <b>");
                text = Tag.Replace(text, "");
                text = text.Replace("[X]", "").Replace("[x]", "").Replace("*", "");
                var bonus = StatsBonus.Match(text);
                if (bonus.Success) text = text.Remove(bonus.Index, bonus.Length);
                text = text.Replace('/', ' ');
                text = Capitalize(text, enders);
                text = text.Replace('_', ' ');
                if (period.Length > 0) text = text.Replace(period + period + period, period).Replace(period + period, period);
                text = Spaces.Replace(text, " ").Trim();
                if (text.Length == 0) return "";
                var last = text[text.Length - 1];
                if (enders.IndexOf(last) < 0 && !text.EndsWith("." + "\"") && last != ':') text += period;
                return text;
            }
            catch (Exception e) { Log.Error(e); return ""; }
        }

        // the first letter or digit of the text and of each sentence upper-cased
        static string Capitalize(string text, string enders)
        {
            var chars = text.ToCharArray();
            bool start = true;
            for (int i = 0; i < chars.Length; i++)
            {
                if (start && char.IsLetterOrDigit(chars[i])) { chars[i] = char.ToUpper(chars[i]); start = false; }
                else if (enders.IndexOf(chars[i]) >= 0) start = true;
            }
            return new string(chars);
        }
    }
}
