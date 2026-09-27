using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Accessibility;
using UnityEngine;

namespace HSAEnhanced
{
    // Lives on the CreditsDisplay object (Options > Miscellaneous > Credits): sets the accessible
    // screen once the credits text is loaded and goes away with the scene.
    class CreditsWatcher : MonoBehaviour
    {
        internal static int Active;     // the fallback menu leaves the credits scene to us
        internal CreditsDisplay Display;
        AccessibleCredits m_screen;
        float m_next;

        // from FallbackWatcher: the credits scene got its display
        internal static void Ensure()
        {
            var d = CreditsDisplay.Get();
            if (d != null && d && d.GetComponent<CreditsWatcher>() == null) d.gameObject.AddComponent<CreditsWatcher>().Display = d;
        }

        void OnEnable() { Active++; }
        void OnDisable() { Active--; }

        void Update()
        {
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 0.5f;
            try
            {
                if (m_screen == null)
                {
                    if (AccessibleCredits.Lines(Display) == null) return;
                    m_screen = new AccessibleCredits(Display);
                    AccessibilityMgr.SetScreen(m_screen);
                }
                m_screen.Poll();
            }
            catch (Exception e) { Log.Error(e); enabled = false; }
        }

        void OnDestroy()
        {
            if (m_screen != null && AccessibilityMgr.IsCurrentlyFocused(m_screen)) AccessibilityMgr.TransitioningScreens();
        }
    }

    // The credits of the year on show, as the screen lays them out: its section headings (the
    // orange ones) make the menu, each opens its roles (the yellow ones) with the names under them;
    // then the year buttons and Back. Each credits card is said as it flies in. All text is the game's.
    class AccessibleCredits : AccessibleScreen
    {
        readonly CreditsDisplay m_display;
        AccessibleMenu m_main;
        AccessibleMenu m_section;       // open section, or null
        string m_signature;
        Actor m_card;                   // the credits card last said

        static readonly Regex Heading = new Regex("^<color=#ffb400>(.*)</color>$", RegexOptions.IgnoreCase);
        static readonly Regex Role = new Regex("^<color=#[0-9a-f]{6}>(.*)</color>$", RegexOptions.IgnoreCase);

        internal AccessibleCredits(CreditsDisplay display) { m_display = display; Build(); }

        internal static string[] Lines(CreditsDisplay d) { return Ref.Get<string[]>(d, "m_creditLines"); }

        string YearLabel()
        {
            var years = Ref.Get(m_display, "m_creditsYearsAvailable") as Array;
            var index = Ref.Get<int>(m_display, "m_creditsYearIndex");
            if (years == null || index < 0 || index >= years.Length) return "";
            var record = years.GetValue(index);
            // (a DbfLocValue: its text in the game's language)
            var loc = Ref.Get(record, "m_buttonLabel");
            var label = loc == null ? null : CallString(loc, "GetString") ?? loc.ToString();
            if (string.IsNullOrEmpty(label)) label = CallString(record, "get_ID");
            return Str.Clean(label);
        }

        static string CallString(object o, string method)
        {
            var m = o == null ? null : Ref.Method(o.GetType(), method, 0);
            try { var v = m == null ? null : m.Invoke(o, null); return v == null ? null : v.ToString(); } catch { return null; }
        }

        class Section { internal string Title; internal List<string> Entries = new List<string>(); }

        // sections of the file: "Role: name, name" entries under each heading; lines under no
        // heading make entries of their own
        List<Section> Sections()
        {
            var sections = new List<Section>();
            Section cur = null;
            string role = null;
            var names = new List<string>();
            Action flush = () =>
            {
                if (cur == null) { cur = new Section(); sections.Add(cur); }
                if (role != null) cur.Entries.Add(names.Count == 0 ? role : role + ": " + string.Join(", ", names.ToArray()));
                else foreach (var n in names) cur.Entries.Add(n);
                role = null; names.Clear();
            };
            foreach (var raw in Lines(m_display) ?? new string[0])
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("<!--")) continue;
                var h = Heading.Match(line);
                if (h.Success)
                {
                    flush();
                    cur = new Section { Title = Str.Clean(h.Groups[1].Value) };
                    sections.Add(cur);
                    continue;
                }
                var r = Role.Match(line);
                if (r.Success) { flush(); role = Str.Clean(r.Groups[1].Value); continue; }
                var name = Str.Clean(line);
                if (name.Length > 0) names.Add(name);
            }
            flush();
            sections.RemoveAll(s => s.Title == null && s.Entries.Count == 0);
            return sections;
        }

        void Build()
        {
            var keep = m_main == null ? 0 : MenuEdit.GetIndex(m_main);
            m_section = null;
            m_main = new AccessibleMenu(this, YearLabel(), GoBack);
            foreach (var s in Sections())
            {
                var section = s;
                if (section.Title == null) { foreach (var e in section.Entries) { var text = e; m_main.AddOption(text, () => Output(text)); } continue; }
                m_main.AddOption(section.Title, () => OpenSection(section));
            }
            foreach (var field in new[] { "m_yearButton1", "m_yearButton2" })
            {
                var button = Ref.Get<Component>(m_display, field);
                if (button == null || !button || !Ui.IsShown(button)) continue;
                var label = Ui.LabelOf(button);
                if (label.Length == 0) continue;
                var press = Ui.ClickOf(button);
                m_main.AddOption(label, () => { Log.Info("credits: year " + label); press(); });
            }
            m_main.AddOption(Str.Back, GoBack);
            m_main.SetIndex(Math.Min(keep, m_main.GetNumItems() - 1));
            m_signature = Signature();
        }

        // the credits card flying in right now (the people credited as cards)
        string CardText()
        {
            var actor = Ref.Get<Actor>(m_display, "m_shownCreditsCard");
            if (actor == null || !actor) return "";
            var def = actor.GetEntityDef();
            if (def == null) return "";
            string text = null;
            try { text = def.GetCardTextInHand(); } catch { }
            return Str.Join(Str.Clean(def.GetName()), Str.Clean(text));
        }

        void OpenSection(Section section)
        {
            m_section = new AccessibleMenu(this, section.Title, () => { m_section = null; m_main.StartReading(false); });
            foreach (var e in section.Entries) { var text = e; m_section.AddOption(text, () => Output(text)); }
            m_section.StartReading();
        }

        void GoBack()
        {
            Log.Info("credits: back");
            if (!Navigation.GoBack()) SceneMgr.Get().SetNextMode(SceneMgr.Mode.HUB);
        }

        string Signature()
        {
            var lines = Lines(m_display);
            return Ref.Get<int>(m_display, "m_creditsYearIndex") + "|" + (lines == null ? -1 : lines.Length) + "|" + YearLabel();
        }

        internal void Poll()
        {
            var card = Ref.Get<Actor>(m_display, "m_shownCreditsCard");
            if (card != m_card)
            {
                m_card = card;
                var said = CardText();
                if (said.Length > 0 && AccessibilityMgr.IsCurrentlyFocused(this)) Output(said);
            }
            if (Lines(m_display) == null || Signature() == m_signature) return;
            Build();
            if (AccessibilityMgr.IsCurrentlyFocused(this)) m_main.StartReading();
        }

        void Output(string text) { AccessibilityMgr.Output(this, text); }

        public void HandleInput() { if (m_section != null) m_section.HandleAccessibleInput(); else if (m_main != null) m_main.HandleAccessibleInput(); }

        public string GetHelp() { return m_section != null ? m_section.GetHelp() : m_main == null ? "" : m_main.GetHelp(); }

        public void OnGainedFocus()
        {
            if (m_section != null) m_section.StartReading();
            else m_main.StartReading();
        }
    }
}
