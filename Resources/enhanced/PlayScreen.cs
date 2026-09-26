using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Accessibility;
using UnityEngine;

namespace HSAEnhanced
{
    // The game's choice trays as the game shows them: the deck tray (ranked / casual, friendly
    // challenges, adventures and practice) and the practice opponent tray. Every button of the tray
    // in screen order with the game's own texts, and its choices (decks, opponents) where the tray
    // shows them. Choosing one selects it like a click; the tray's Play / Choose button says what is
    // selected and goes on. On the Play screen the format button says the chosen format and opens the
    // game's picker. The parts are found by their game types, not by names.
    static class PlayScreen
    {
        // start of HSA's DeckPickerTrayDisplay.RankedOnDeckPickerTrayDisplayReady /
        // FriendlyOnDeckPickerTrayDisplayReady and AccessibleAdventureScene.OnDeckPickerTrayDisplayReady /
        // OnPracticePickerTrayDisplayShown; true: HSA's own menu is not built
        internal static bool Show(MonoBehaviour tray)
        {
            if (!Engine.Enabled || tray == null || !tray) return false;
            var watcher = tray.GetComponent<PlayScreenWatcher>() ?? tray.gameObject.AddComponent<PlayScreenWatcher>();
            watcher.Screen = new AccessiblePlayScreen(tray);
            AccessibilityMgr.SetScreen(watcher.Screen);
            return true;
        }
    }

    class PlayScreenWatcher : MonoBehaviour
    {
        internal AccessiblePlayScreen Screen;
        float m_next;

        void Update()
        {
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 0.5f;
            try
            {
                if (Screen == null) return;
                if (AccessibilityMgr.IsCurrentlyFocused(Screen)) { Screen.Poll(); return; }
                // HSA's adventure screen takes focus again when its sub-screen finishes loading,
                // with nothing to read while the tray is up: the tray stays ours
                var current = Ref.Field(typeof(AccessibilityMgr), "s_curScreen")?.GetValue(null);
                var uis = Ref.Field(typeof(AccessibilityMgr), "s_curUIs")?.GetValue(null) as IList;
                if (current is AccessibleAdventureScene && (uis == null || uis.Count == 0) && isActiveAndEnabled)
                    AccessibilityMgr.SetScreen(Screen);
            }
            catch (Exception e) { Log.Error(e); enabled = false; }
        }

        void OnDestroy()
        {
            if (Screen != null && AccessibilityMgr.IsCurrentlyFocused(Screen)) AccessibilityMgr.TransitioningScreens();
        }
    }

    class AccessiblePlayScreen : AccessibleScreen
    {
        readonly MonoBehaviour m_tray;
        AccessibleMenu m_menu;
        string m_signature;

        internal AccessiblePlayScreen(MonoBehaviour tray)
        {
            m_tray = tray;
            if (tray is DeckPickerTrayDisplay && !GameUtils.HasCompletedApprentice() && Hearthstone.Progression.RewardTrackManager.Get().HasAnyUnclaimedApprenticeRewards())
                AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.APPRENTICE_REWARD_REMINDER));
            Build();
        }

        class Entry { internal float Key; internal int Order; internal string Label; internal Action Press; internal Component Target; }

        // a choice the tray offers: a deck or an opponent, on a page of the tray
        class Choice { internal int Page; internal PegUIElement Button; }

        void Build()
        {
            var keep = m_menu == null ? 0 : MenuEdit.GetIndex(m_menu);
            m_menu = MenuEdit.Carry(m_menu, new AccessibleMenu(this, Title(), GoBack));
            var entries = new List<Entry>();
            var choices = Choices();
            var choiceObjects = new HashSet<GameObject>();
            foreach (var c in choices) choiceObjects.Add(c.Button.gameObject);

            // every button the tray shows, where it shows it
            foreach (var b in Ui.ButtonsIn(m_tray, new string[0], v => false))
            {
                if (choiceObjects.Contains(b.Object)) continue;     // a deck / an opponent: listed with the choices
                var button = b;
                var e = new Entry { Key = Ui.ScreenOrderOf(b.Target), Label = b.Label, Target = b.Target, Press = () => { Log.Info("option: " + GameButton.Describe(new List<GameButton> { button })); WatchForPanel(); button.Click(); } };
                if (b.Target is SwitchFormatButton) { var f = (SwitchFormatButton)b.Target; e.Label = Str.Join(b.Label, FormatName()); e.Press = () => PressFormat(f); }
                else if (b.Target is PlayButton) { e.Label = PlayLabel(); e.Press = Play; }
                entries.Add(e);
            }

            // the format button shows an icon only: named by the game's text for switching format and
            // the chosen format; pressed through its release event, which opens the game's picker
            var format = FormatButton();
            if (format != null && !entries.Exists(x => x.Target == format))
                entries.Add(new Entry { Key = Ui.ScreenOrderOf(format), Target = format, Press = () => PressFormat(format),
                    Label = Str.Join(LocalizationUtils.Get(LocalizationKey.GLOBAL_SWITCH_FORMAT), FormatName(), format.IsEnabled() ? null : Str.Unavailable) });

            // Play / Choose when the tray shows it without a text of its own
            var play = PlayButtonOf();
            if (play != null && play.gameObject.activeInHierarchy && !entries.Exists(x => x.Target == play))
                entries.Add(new Entry { Key = Ui.ScreenOrderOf(play), Target = play, Label = PlayLabel(), Press = Play });

            // the rank medal (ranked play), where the game shows it
            var rank = RankLines();
            if (rank.Count > 0 && !entries.Exists(x => Labels.Norm(x.Label) == Labels.Norm(rank[0])))
            {
                var medal = Ui.FieldOfType<RankedPlayDisplay>(m_tray);
                entries.Add(new Entry { Key = medal != null && Ui.ScreenPoint(medal) != null ? Ui.ScreenOrderOf(medal) : -3e38f, Label = rank[0], Press = () => Output(Str.Join(rank.ToArray())) });
            }

            // the choices, where the tray shows them (those on other pages right after)
            float choiceKey = 0; bool placed = false;
            foreach (var c in choices)
                if (c.Button.gameObject.activeInHierarchy && Ui.ScreenPoint(c.Button) != null) { choiceKey = Ui.ScreenOrderOf(c.Button); placed = true; break; }
            if (!placed && entries.Count > 0) choiceKey = entries[0].Key;
            foreach (var c in choices)
            {
                var choice = c;
                entries.Add(new Entry { Key = choiceKey, Label = ChoiceLabel(choice.Button), Target = choice.Button, Press = () => Select(choice) });
            }

            for (int i = 0; i < entries.Count; i++) entries[i].Order = i;
            entries.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Order.CompareTo(b.Order));
            var labels = new List<string>();
            foreach (var e in entries) { var x = e; m_menu.AddOption(x.Label, () => { Log.Info("play screen: pressed " + x.Label); x.Press(); }); labels.Add(x.Label); }
            Log.Once(m_tray.GetType().Name + ": " + string.Join(" | ", labels.ToArray()));

            m_menu.SetIndex(Math.Max(0, Math.Min(keep, m_menu.GetNumItems() - 1)));
            m_signature = Signature();
        }

        // what the tray is for: the chosen format on the Play screen, the tray's own label (the
        // opponent tray), else the game's "choose a deck"
        string Title()
        {
            if (FormatButton() != null) return FormatName();
            if (!(m_tray is DeckPickerTrayDisplay))
                foreach (var ut in Ui.FieldsOfType<UberText>(m_tray))
                {
                    if (!ut.gameObject.activeInHierarchy || ut.GetComponentInParent<PegUIElement>() != null) continue;
                    var s = Ui.ShownText(ut.Text);
                    if (s.Length > 0) return s;
                }
            return LocalizationUtils.Get(LocalizationKey.SCREEN_CHOOSE_DECK_TITLE);
        }

        #region Format and rank (the Play screen)
        SwitchFormatButton FormatButton()
        {
            var f = Ui.FieldOfType<SwitchFormatButton>(m_tray);
            return f != null && f.gameObject.activeInHierarchy && !Covered(f) ? f : null;
        }

        // the game's release event (what a click sends); a button the game has disabled ignores it
        void PressFormat(SwitchFormatButton f)
        {
            Log.Info("option: format button (enabled " + f.IsEnabled() + ")");
            f.TriggerRelease();
            if (!f.IsEnabled() && f.gameObject.activeInHierarchy) Output(Str.Unavailable);
        }

        static bool Covered(PegUIElement button)
        {
            var covered = Ref.Method(button.GetType(), "IsCovered", 0);
            try { return covered != null && (bool)covered.Invoke(button, null); } catch { return false; }
        }

        // the format the game has chosen (VisualsFormatTypeExtensions is internal to the game)
        static object CurrentFormat()
        {
            var ext = typeof(VisualsFormatType).Assembly.GetType("VisualsFormatTypeExtensions");
            var get = ext == null ? null : Ref.Method(ext, "GetCurrentVisualsFormatType", 0);
            try { return get == null ? null : get.Invoke(null, null); } catch { return null; }
        }

        // the chosen format in the game's words: ranked or casual, and Standard / Wild / ...
        static string FormatName()
        {
            var current = CurrentFormat();
            var name = current == null ? "" : current.ToString();
            if (name.StartsWith("VFT_")) name = name.Substring(4);
            var mode = Options.GetInRankedPlayMode() ? Str.Word("GLOBAL_RANKED", "GLUE_TOURNAMENT_RANKED") : Str.Word("GLUE_TOURNAMENT_CASUAL");
            return Str.Join(mode, Str.Word("GLOBAL_" + name));
        }

        // what the rank medal shows (ranked play only)
        static List<string> RankLines()
        {
            var lines = new List<string>();
            try
            {
                if (SceneMgr.Get().GetMode() != SceneMgr.Mode.TOURNAMENT || !Options.GetInRankedPlayMode()) return lines;
                var medal = RankMgr.Get().GetLocalPlayerMedalInfo().GetCurrentMedal(Options.GetFormatType());
                foreach (var s in new[] { AccessibleRankedUtils.GetRankText(medal), AccessibleRankedUtils.GetRankStarsText(medal), AccessibleRankedUtils.GetStarBonusText(medal) })
                    if (!string.IsNullOrEmpty(s)) lines.Add(s);
            }
            catch (Exception e) { Log.Error(e); }
            return lines;
        }
        #endregion

        #region Choices: decks and opponents
        List<Choice> Choices()
        {
            var choices = new List<Choice>();
            // the deck tray: the decks on its pages that the chosen format shows
            var pages = Ui.FieldOfType<List<CustomDeckPage>>(m_tray);
            if (pages != null)
            {
                var enabled = EnabledPageTypes();
                for (int i = 0; i < pages.Count; i++)
                {
                    var page = pages[i];
                    if (page == null) continue;
                    if (enabled != null && !Contains(enabled, page.DeckPageType)) continue;
                    var list = Ui.FieldOfType<List<CollectionDeckBoxVisual>>(page);
                    if (list == null) continue;
                    foreach (var deck in list)
                    {
                        if (deck == null) continue;
                        if (deck.GetDeckID() == -1L && !deck.IsLoanerDeck()) break;   // the empty slots after the decks
                        choices.Add(new Choice { Page = i, Button = deck });
                    }
                }
            }
            // the opponent tray: its opponents
            var opponents = Ui.FieldOfType<List<PracticeAIButton>>(m_tray);
            if (opponents != null)
                foreach (var o in opponents)
                    if (o != null && o && o.gameObject.activeInHierarchy) choices.Add(new Choice { Page = -1, Button = o });
            return choices;
        }

        // the deck page types the chosen format shows (the tray's config for it); null: all
        IEnumerable EnabledPageTypes()
        {
            try
            {
                var current = CurrentFormat();
                if (current == null) return null;
                foreach (var dict in Ui.FieldsOfType<IDictionary>(m_tray))
                {
                    if (!dict.Contains(current)) continue;
                    var config = dict[current];
                    foreach (var types in Ui.FieldsOfType<IEnumerable>(config))
                        foreach (var t in types) { if (t is CustomDeckPage.PageType) return types; break; }
                    var p = config.GetType().GetProperty("EnabledPageTypes");
                    if (p != null) return p.GetValue(config, null) as IEnumerable;
                }
            }
            catch { }
            return null;
        }

        static bool Contains(IEnumerable items, object value)
        {
            foreach (var x in items) if (Equals(x, value)) return true;
            return false;
        }

        // what the choice shows, as HSA reads it (a deck: name and class; an opponent: its lines)
        string ChoiceLabel(PegUIElement button)
        {
            var lines = new List<string>();
            try
            {
                object item = null;
                if (button is CollectionDeckBoxVisual) item = new AccessibleCollectionDeckBoxVisual(this, (CollectionDeckBoxVisual)button);
                else if (button is PracticeAIButton) item = new AccessiblePracticeAIButton(this, (PracticeAIButton)button);
                var get = item == null ? null : Ref.Method(item.GetType(), "GetLines", 0);
                var got = get == null ? null : get.Invoke(item, null) as List<string>;
                if (got != null) lines = got;
            }
            catch (Exception e) { Log.Error(e); }
            if (lines.Count == 0) return Ui.LabelOf(button);
            if (button is CollectionDeckBoxVisual && lines.Count > 1) return Str.Join(lines[0], lines[1]);
            return Str.Join(lines.ToArray());
        }

        // the choice the tray has selected: the field of its type that holds one of its choices
        PegUIElement Selected()
        {
            var choices = new HashSet<PegUIElement>();
            foreach (var c in Choices()) choices.Add(c.Button);
            foreach (var d in Ui.FieldsOfType<PegUIElement>(m_tray)) if (choices.Contains(d)) return d;
            return null;
        }

        // a click on the deck box / opponent: the game selects it (HSA's own choice also started the game)
        void Select(Choice c)
        {
            try
            {
                if (c.Page >= 0 && !c.Button.gameObject.activeInHierarchy) Ref.Call(m_tray, "ShowPage", c.Page, false);
                Ref.Set(m_tray, "m_chosenDeck", false);
                Ref.Set(AccessibleAdventureScene.Get(), "chosenDeck", false);
                c.Button.TriggerRelease();
                Output(ChoiceLabel(c.Button));
            }
            catch (Exception e) { Log.Error(e); }
        }
        #endregion

        PlayButton PlayButtonOf() { return Ui.FieldOfType<PlayButton>(m_tray); }

        static bool Enabled(PegUIElement b) { return b != null && b.IsEnabled(); }

        string PlayLabel()
        {
            var play = PlayButtonOf();
            var label = play == null ? "" : Ui.LabelOf(play);
            if (label.Length == 0) label = Str.Word("GLOBAL_PLAY");
            var selected = Selected();
            return Str.Join(label, selected == null ? null : ChoiceLabel(selected), Enabled(play) ? null : Str.Unavailable);
        }

        void Play()
        {
            var play = PlayButtonOf();
            if (play == null) return;
            if (!Enabled(play)) { Output(PlayLabel()); return; }
            Log.Info("option: play");
            play.TriggerRelease();
        }

        // the game's own back (Escape, an adventure's sub-screen), else the tray's back button
        // (from the opponent tray: the deck tray under it is read again)
        void GoBack()
        {
            Back.Go(m_menu, m_tray.gameObject);
            if (m_tray is DeckPickerTrayDisplay) return;
            var decks = DeckPickerTrayDisplay.Get();
            var watcher = decks == null ? null : decks.GetComponent<PlayScreenWatcher>();
            if (watcher != null && watcher.Screen != null) AccessibilityMgr.SetScreen(watcher.Screen);
        }

        string Signature()
        {
            var sb = new StringBuilder();
            sb.Append(Title()).Append('|');
            foreach (var c in Choices()) sb.Append(c.Button.GetInstanceID()).Append(',');
            var selected = Selected();
            sb.Append('|').Append(selected == null ? 0 : selected.GetInstanceID()).Append(Enabled(PlayButtonOf()));
            foreach (var b in Ui.ButtonsIn(m_tray, new string[0], v => false)) sb.Append('|').Append(b.Label);
            return sb.ToString();
        }

        internal void Poll()
        {
            if (Time.unscaledTime < m_watchUntil && OpenedPanel()) return;
            if (Signature() == m_signature) return;
            Build();
        }

        #region Panels a button opens (e.g. the ranked rewards list: an overlay, not a popup)
        HashSet<Hearthstone.UI.Widget> m_shownBefore;
        float m_watchUntil;

        // the tray's widgets and those of the parts it holds (its rank display, ...)
        List<Hearthstone.UI.Widget> PanelWidgets()
        {
            var widgets = new List<Hearthstone.UI.Widget>(Ui.FieldsOfType<Hearthstone.UI.Widget>(m_tray));
            foreach (var part in Ui.FieldsOfType<MonoBehaviour>(m_tray))
                if (part != m_tray && !(part is PegUIElement) && !(part is Hearthstone.UI.Widget)) widgets.AddRange(Ui.FieldsOfType<Hearthstone.UI.Widget>(part));
            return widgets;
        }

        static bool IsOpen(Hearthstone.UI.Widget w) { return w != null && w && w.gameObject.activeInHierarchy && w.IsActive; }

        void WatchForPanel()
        {
            m_shownBefore = new HashSet<Hearthstone.UI.Widget>();
            foreach (var w in PanelWidgets()) if (IsOpen(w)) m_shownBefore.Add(w);
            m_watchUntil = Time.unscaledTime + 3f;
        }

        // a widget that opened after the press: read as a panel (its texts and buttons) until it closes
        bool OpenedPanel()
        {
            foreach (var w in PanelWidgets())
            {
                if (!IsOpen(w) || m_shownBefore.Contains(w)) continue;
                if (Ui.TextsUnder(w.gameObject).Count == 0 && Ui.ClickablesUnder(w.gameObject, null).Count == 0) continue;   // still loading
                m_watchUntil = 0;
                Log.Info("play screen: opened " + w.name);
                FallbackWatcher.ShowPopup(w.gameObject, null, true);
                return true;
            }
            return false;
        }
        #endregion

        void Output(string text) { AccessibilityMgr.Output(this, text); }

        public void HandleInput() { if (m_menu != null) m_menu.HandleAccessibleInput(); }

        public string GetHelp() { return m_menu == null ? "" : m_menu.GetHelp(); }

        public void OnGainedFocus()
        {
            Build();
            m_menu.StartReading();
        }
    }
}
