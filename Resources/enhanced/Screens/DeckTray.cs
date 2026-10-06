using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // The game's choice trays on our core: the deck tray (ranked / casual, friendly challenges,
    // adventures, practice) and the practice opponent tray. Every button of the tray in screen
    // order with the game's own texts, and its choices (decks, opponents). Choosing one selects it
    // like a click; Play says what is selected and starts. The format button says the chosen format
    // and opens the game's picker. Parts are found by their game types, not by names.
    static class DeckTray
    {
        static DeckTrayUI s_ui;

        // the tray on screen: the opponent tray over the deck tray when it is up
        internal static MonoBehaviour Shown()
        {
            var practice = PracticePickerTrayDisplay.Get();
            if (practice != null && practice && practice.gameObject.activeInHierarchy && practice.IsShown()) return practice;
            var decks = DeckPickerTrayDisplay.Get();
            if (decks != null && decks && decks.gameObject.activeInHierarchy && GameState.Get() == null) return decks;
            return null;
        }

        // every frame
        internal static void Tick()
        {
            var tray = Shown();
            if (s_ui != null && s_ui.Tray != tray) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (tray != null && s_ui == null)
            {
                var ui = new DeckTrayUI(tray);
                if (!ui.Build()) return;
                s_ui = ui;
                Generic.Yield();
                Focus.PushBase(ui);
                if (ui.Focused) ui.Read();
            }
            if (s_ui != null) s_ui.Poll();
        }

        internal static bool Active { get { return s_ui != null; } }
    }

    class DeckTrayUI : Core.Screen
    {
        internal readonly MonoBehaviour Tray;
        Menu m_menu;
        string m_signature;
        float m_next;

        internal DeckTrayUI(MonoBehaviour tray) { Tray = tray; }

        internal override bool Alive { get { return Tray != null && Tray && DeckTray.Shown() == Tray; } }

        class Entry { internal float Key; internal int Order; internal string Label; internal Action Press; internal Component Target; }
        class Choice { internal int Page; internal PegUIElement Button; }

        internal bool Build()
        {
            var entries = new List<Entry>();
            var choices = Choices();
            var choiceObjects = new HashSet<GameObject>();
            foreach (var c in choices) choiceObjects.Add(c.Button.gameObject);

            foreach (var b in Ui.ButtonsIn(Tray, new string[0], v => false))
            {
                if (choiceObjects.Contains(b.Object)) continue;
                var button = b;
                var e = new Entry { Key = Ui.ScreenOrderOf(b.Target), Label = b.Label, Target = b.Target, Press = () => { Log.Info("deck tray: " + button.Label); button.Click(); } };
                if (b.Target is SwitchFormatButton) { var f = (SwitchFormatButton)b.Target; e.Label = Str.Join(b.Label, FormatName()); e.Press = () => PressFormat(f); }
                else if (b.Target is PlayButton) { e.Label = PlayLabel(); e.Press = Play; }
                entries.Add(e);
            }

            var format = FormatButton();
            if (format != null && format.IsEnabled() && !entries.Exists(x => x.Target == format))
                entries.Add(new Entry { Key = Ui.ScreenOrderOf(format), Target = format, Press = () => PressFormat(format),
                    Label = Str.Join(Speech.S("ACCESSIBILITY_GLOBAL_SWITCH_FORMAT"), FormatName()) });

            var play = PlayButtonOf();
            if (play != null && play.gameObject.activeInHierarchy && Enabled(play) && !entries.Exists(x => x.Target == play))
                entries.Add(new Entry { Key = Ui.ScreenOrderOf(play), Target = play, Label = PlayLabel(), Press = Play });

            var rank = RankLine();
            if (rank != null)
            {
                var medal = Ui.FieldOfType<RankedPlayDisplay>(Tray);
                entries.Add(new Entry { Key = medal != null && Ui.ScreenPoint(medal) != null ? Ui.ScreenOrderOf(medal) : -3e38f, Label = rank, Press = () => Say(rank) });
            }

            float choiceKey = 0; bool placed = false;
            foreach (var c in choices)
                if (c.Button.gameObject.activeInHierarchy && Ui.ScreenPoint(c.Button) != null) { choiceKey = Ui.ScreenOrderOf(c.Button); placed = true; break; }
            if (!placed && entries.Count > 0) choiceKey = entries[0].Key;
            foreach (var c in choices)
            {
                var choice = c;
                entries.Add(new Entry { Key = choiceKey, Label = ChoiceLabel(choice.Button), Target = choice.Button, Press = () => Select(choice) });
            }
            if (entries.Count == 0) return m_menu != null;

            for (int i = 0; i < entries.Count; i++) entries[i].Order = i;
            entries.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Order.CompareTo(b.Order));

            object keep = m_menu == null ? null : m_menu.KeyAt(m_menu.Index);
            int at = m_menu == null ? 0 : m_menu.Index;
            var menu = new Menu(this, Title(), GoBack);
            var labels = new List<string>();
            foreach (var e in entries)
            {
                var x = e;
                menu.AddOption(x.Label, () => x.Press(), (object)x.Target ?? x.Label);
                labels.Add(x.Label);
            }
            var k = menu.IndexOfKey(keep);
            menu.Index = k >= 0 ? k : at;
            m_menu = menu;
            m_signature = Signature();
            Log.Once(Tray.GetType().Name + ": " + string.Join(" | ", labels.ToArray()));
            return true;
        }

        string Title()
        {
            if (FormatButton() != null) return FormatName();
            if (!(Tray is DeckPickerTrayDisplay))
            {
                var choose = Str.Word("GLUE_CHOOSE_OPPONENT");
                if (choose.Length > 0) return choose;
            }
            foreach (var ut in Ui.FieldsOfType<UberText>(Tray))
            {
                if (!ut.gameObject.activeInHierarchy || ut.GetComponentInParent<PegUIElement>() != null) continue;
                var s = Ui.ShownText(ut.Text);
                if (s.Length > 0) return s;
            }
            return SceneNames.Of(SceneMgr.Get().GetMode());
        }

        #region Format and rank
        SwitchFormatButton FormatButton()
        {
            var f = Ui.FieldOfType<SwitchFormatButton>(Tray);
            return f != null && f.gameObject.activeInHierarchy && !Covered(f) ? f : null;
        }

        void PressFormat(SwitchFormatButton f)
        {
            Log.Info("deck tray: format button (enabled " + f.IsEnabled() + ")");
            Core.Click.Peg(f);
            if (!f.IsEnabled() && f.gameObject.activeInHierarchy) Say(Str.Unavailable);
        }

        static bool Covered(PegUIElement button)
        {
            var covered = Ref.Method(button.GetType(), "IsCovered", 0);
            try { return covered != null && (bool)covered.Invoke(button, null); } catch { return false; }
        }

        static object CurrentFormat()
        {
            var ext = typeof(VisualsFormatType).Assembly.GetType("VisualsFormatTypeExtensions");
            var get = ext == null ? null : Ref.Method(ext, "GetCurrentVisualsFormatType", 0);
            try { return get == null ? null : get.Invoke(null, null); } catch { return null; }
        }

        static string FormatName()
        {
            var current = CurrentFormat();
            var name = current == null ? "" : current.ToString();
            if (name.StartsWith("VFT_")) name = name.Substring(4);
            var mode = Options.GetInRankedPlayMode() ? Str.Word("GLOBAL_RANKED", "GLUE_TOURNAMENT_RANKED") : Str.Word("GLUE_TOURNAMENT_CASUAL");
            return Str.Join(mode, Str.Word("GLOBAL_" + name));
        }

        // the rank medal (ranked play only): the rank's name, the legend place
        static string RankLine()
        {
            try
            {
                if (SceneMgr.Get().GetMode() != SceneMgr.Mode.TOURNAMENT || !Options.GetInRankedPlayMode()) return null;
                var medal = RankMgr.Get().GetLocalPlayerMedalInfo().GetCurrentMedal(Options.GetFormatType());
                if (medal == null || !medal.IsValid()) return null;
                var name = Str.Clean(medal.GetRankName());
                if (medal.IsLegendRank() && medal.legendIndex > 0) name = Str.Join(name, medal.legendIndex.ToString());
                return name.Length == 0 ? null : name;
            }
            catch (Exception e) { Log.Error(e); return null; }
        }
        #endregion

        #region Choices: decks and opponents
        List<Choice> Choices()
        {
            var choices = new List<Choice>();
            var pages = Ui.FieldOfType<List<CustomDeckPage>>(Tray);
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
                        if (deck.GetDeckID() == -1L && !IsLoaner(deck)) break;
                        choices.Add(new Choice { Page = i, Button = deck });
                    }
                }
            }
            var opponents = Ui.FieldOfType<List<PracticeAIButton>>(Tray);
            if (opponents != null)
                foreach (var o in opponents)
                    if (o != null && o && o.gameObject.activeInHierarchy) choices.Add(new Choice { Page = -1, Button = o });
            return choices;
        }

        static bool IsLoaner(CollectionDeckBoxVisual box)
        {
            try { var d = box.GetCollectionDeck(); return d != null && d.IsLoanerDeck; } catch { return false; }
        }

        IEnumerable EnabledPageTypes()
        {
            try
            {
                var current = CurrentFormat();
                if (current == null) return null;
                foreach (var dict in Ui.FieldsOfType<IDictionary>(Tray))
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

        // a deck: its name, its class, and what the box warns about; an opponent: what it shows
        static string ChoiceLabel(PegUIElement button)
        {
            var deck = button as CollectionDeckBoxVisual;
            if (deck == null) return Ui.LabelOf(button);
            string name = null, cls = null;
            try
            {
                var text = deck.GetDeckNameText();
                name = text == null ? null : Ui.ShownText(text.Text);
                var hero = deck.GetHeroCardID();
                var def = string.IsNullOrEmpty(hero) ? null : DefLoader.Get().GetEntityDef(hero);
                if (def != null) cls = Str.Clean(GameStrings.GetClassName(def.GetClass()));
            }
            catch (Exception e) { Log.Error(e); }
            var said = Str.Join(name, cls);
            return Str.Join(said.Length > 0 ? said : Ui.LabelOf(button), DeckBoxWarning(deck, said));
        }

        static string DeckBoxWarning(CollectionDeckBoxVisual box, string said)
        {
            bool count = Ref.Get<bool>(box, "m_isShowingInvalidCardCount");
            bool sideboard = Ref.Get<int>(box, "m_invalidSideboardCardCount") > 0 || Ref.Get<int>(box, "m_missingSideboardCardCount") > 0;
            if (!count && !sideboard) return null;
            var desc = Ref.Get<UberText>(box, "m_deckDesc");
            var badge = count ? Ref.Get<UberText>(box, "m_invalidCardCountIndicatorText") : null;
            var line = desc == null ? null : Ui.ShownText(desc.Text);
            if (line != null && Labels.Norm(said).Contains(Labels.Norm(line))) line = null;
            return Str.Join(line, badge == null ? null : Ui.ShownText(badge.Text));
        }

        PegUIElement Selected()
        {
            var choices = new HashSet<PegUIElement>();
            foreach (var c in Choices()) choices.Add(c.Button);
            foreach (var d in Ui.FieldsOfType<PegUIElement>(Tray)) if (choices.Contains(d)) return d;
            return null;
        }

        void Select(Choice c)
        {
            try
            {
                if (c.Page >= 0 && !c.Button.gameObject.activeInHierarchy) Ref.Call(Tray, "ShowPage", c.Page, false);
                Log.Info("deck tray: chose " + ChoiceLabel(c.Button));
                Core.Click.Peg(c.Button);
                Say(ChoiceLabel(c.Button));
            }
            catch (Exception e) { Log.Error(e); }
        }
        #endregion

        PlayButton PlayButtonOf() { return Ui.FieldOfType<PlayButton>(Tray); }

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
            if (!Enabled(play)) { Say(PlayLabel()); return; }
            Log.Info("deck tray: play");
            Core.Click.Peg(play);
        }

        // the tray's back button, else the game's own way back
        void GoBack()
        {
            foreach (var b in Ui.ButtonsIn(Tray, new string[0], v => false))
                if (Labels.IsBack(b.Label)) { Log.Info("deck tray: back"); b.Click(); return; }
            try { Navigation.GoBack(); } catch (Exception e) { Log.Error(e); }
        }

        string Signature()
        {
            var sb = new StringBuilder();
            sb.Append(Title()).Append('|');
            foreach (var c in Choices()) sb.Append(c.Button.GetInstanceID()).Append(',');
            var selected = Selected();
            sb.Append('|').Append(selected == null ? 0 : selected.GetInstanceID()).Append(Enabled(PlayButtonOf())).Append(Enabled(FormatButton()));
            foreach (var b in Ui.ButtonsIn(Tray, new string[0], v => false)) sb.Append('|').Append(b.Label);
            return sb.ToString();
        }

        internal void Poll()
        {
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 0.5f;
            try { if (Signature() != m_signature) Build(); } catch (Exception e) { Log.Error(e); }
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
