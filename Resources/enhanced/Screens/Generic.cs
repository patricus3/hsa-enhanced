using System;
using System.Collections.Generic;
using Hearthstone.UI;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Without Hearthstone Access: what nothing of ours reads yet, read from the game's objects.
    // - the game's popup or dialog on top: its texts, then its buttons;
    // - a screen with no reader of its own: every button it shows, in reading order.
    // Both stay current while shown (keeping the place), and step aside for screens of ours.
    static class Generic
    {
        static PopupBase s_popup;
        static PanelUI s_screen;
        static float s_next;

        // popups our own screens read themselves (the friends list's own popups, ...)
        internal static readonly List<Func<GameObject, bool>> Claims = new List<Func<GameObject, bool>>();

        internal static void Tick()
        {
            if (Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + 0.3f;

            var popup = TopPopup();
            if (s_popup != null && (popup == null || s_popup.Root != popup)) { var old = s_popup; s_popup = null; Focus.Pop(old); }
            if (popup != null && s_popup == null)
            {
                if (CardsPopupUI.HasCards(popup))
                {
                    var cards = new CardsPopupUI(popup);
                    if (cards.Refresh()) { s_popup = cards; Log.Info("popup with cards: " + popup.name); Focus.Push(cards); cards.Read(); }
                }
                else
                {
                    var ui = new PanelUI(popup, true);
                    if (ui.Refresh()) { s_popup = ui; Log.Info("popup: " + popup.name); Focus.Push(ui); ui.Read(); }
                }
            }
            if (s_popup != null) s_popup.Refresh();

            // a screen: only when nothing of ours is open
            var scenes = SceneMgr.Get();
            bool ready = scenes != null && !scenes.IsTransitioning() && scenes.IsSceneLoaded() && GameState.Get() == null
                         && scenes.GetMode() != SceneMgr.Mode.STARTUP && scenes.GetMode() != SceneMgr.Mode.FATAL_ERROR;
            if (s_screen != null && !Focus.Has(s_screen)) s_screen = null;     // a screen of ours took its place
            if (s_screen != null && (!ready || s_screen.Mode != scenes.GetMode())) { var old = s_screen; s_screen = null; Focus.Pop(old); }
            if (ready && s_screen == null && !Focus.HasBase && !Specific(scenes.GetMode()) && BlackMarketWatcher.Active == 0 && LuckyDrawWatcher.Active == 0
                && CreditsWatcher.Active == 0 && Mercenaries.Active == 0)
            {
                var ui = new PanelUI(null, false) { Mode = scenes.GetMode() };
                if (ui.Refresh()) { s_screen = ui; Log.Info("screen: " + ui.Mode); Focus.PushBase(ui); if (ui.Focused) ui.Read(); }
            }
            if (s_screen != null) s_screen.Refresh();
        }

        // a screen of ours takes over from the generic one
        internal static void Yield()
        {
            if (s_screen == null) return;
            var old = s_screen; s_screen = null; Focus.Pop(old);
        }

        // modes with a screen of ours
        static bool Specific(SceneMgr.Mode mode) { return mode == SceneMgr.Mode.HUB; }

        static GameObject TopPopup()
        {
            // OptionsMenu is an overlay rather than a Dialogs popup. The Game Menu button list
            // stays shown underneath it, so prefer the actual options overlay while it is open.
            var options = OptionsMenu.Get();
            if (options != null && options.IsShown()) return options.gameObject;
            var dialog = Dialogs.Shown;
            if (dialog != null && !Claimed(dialog.gameObject)) return dialog.gameObject;
            // the game's button-list menus: the Escape menu and those it opens
            foreach (var m in UnityEngine.Object.FindObjectsByType<ButtonListMenu>(FindObjectsSortMode.None))
            {
                if (m == null || !m.IsShown()) continue;
                var def = Ref.Get<Component>(m, "m_menu");
                var root = def != null ? def.gameObject : m.gameObject;
                if (root.activeInHierarchy && !Claimed(root)) return root;
            }
            var ctx = UIContext.GetRoot();
            if (ctx != null && ctx.ShowingPopups())
            {
                var latest = ctx.GetLatestPopup();
                var go = latest == null ? null : latest.PopupInstance;
                if (go != null && go.activeInHierarchy && !Claimed(go)) return go;
            }
            return null;
        }

        static bool Claimed(GameObject go)
        {
            foreach (var c in Claims) { try { if (c(go)) return true; } catch { } }
            return false;
        }
    }

    // a popup (Root) or the whole screen (no Root): texts and buttons as a menu
    // a popup reader: its root, kept current
    abstract class PopupBase : Core.Screen
    {
        internal abstract GameObject Root { get; }
        internal abstract bool Refresh();
    }

    // a popup that shows cards (cards that changed, rewards...): the cards read as in a match.
    // Left / Right go through the cards (Home / End), Up / Down through the card's lines, Shift+Down
    // reads the rest; Tab goes through the popup's buttons, Enter presses the one chosen (the first);
    // its other texts are read first.
    class CardsPopupUI : PopupBase
    {
        readonly GameObject m_root;
        List<EntityDef> m_cards = new List<EntityDef>();
        List<GameButton> m_buttons = new List<GameButton>();
        readonly List<string> m_texts = new List<string>();
        int m_at, m_line, m_button;
        float m_next;

        internal CardsPopupUI(GameObject root) { m_root = root; }

        internal override GameObject Root { get { return m_root; } }

        internal override bool Alive { get { return m_root != null && m_root.activeInHierarchy; } }

        // part of a card's picture (its name, cost, text...)
        internal static bool InCard(Component c)
        {
            return c != null && (c.GetComponentInParent<Actor>() != null || c.GetComponentInParent<Hearthstone.UI.Card>() != null);
        }

        // the cards shown, in reading order: card pictures, and the game's card widgets (whose card
        // comes from their data)
        static List<EntityDef> Cards(GameObject root)
        {
            var found = new List<GameButton>();
            var defs = new Dictionary<Component, EntityDef>();
            foreach (var w in root.GetComponentsInChildren<Hearthstone.UI.Card>())
            {
                if (w == null || !w.gameObject.activeInHierarchy) continue;
                EntityDef def = null;
                try { var dm = w.GetCardDataModel(); if (dm != null && !string.IsNullOrEmpty(dm.CardId)) def = DefLoader.Get().GetEntityDef(dm.CardId); } catch { }
                if (def == null && w.CardActor != null) def = w.CardActor.GetEntityDef();
                if (def == null) continue;
                defs[w] = def; found.Add(new GameButton { Target = w });
            }
            foreach (var a in root.GetComponentsInChildren<Actor>())
            {
                if (a == null || !a.gameObject.activeInHierarchy || a.GetEntityDef() == null) continue;
                if (a.GetComponentInParent<Hearthstone.UI.Card>() != null) continue;
                defs[a] = a.GetEntityDef(); found.Add(new GameButton { Target = a });
            }
            Ui.SortByScreen(found);
            return found.ConvertAll(b => defs[b.Target]);
        }

        internal static bool HasCards(GameObject root) { return Cards(root).Count > 0; }

        internal override bool Refresh()
        {
            if (Time.unscaledTime < m_next) return m_cards.Count > 0;
            m_next = Time.unscaledTime + 0.5f;
            var cards = Cards(m_root);
            if (cards.Count == 0) return false;
            m_cards = cards;
            if (m_at >= m_cards.Count) { m_at = m_cards.Count - 1; m_line = 0; }
            m_buttons = Ui.ClickablesUnder(m_root, null);
            m_buttons.RemoveAll(b => InCard(b.Target));
            if (m_button >= m_buttons.Count) m_button = 0;
            m_texts.Clear();
            var title = PanelUI.PopupTitle(m_root);
            if (!string.IsNullOrEmpty(title)) m_texts.Add(title);
            foreach (var t in Ui.TextsUnder(m_root))
            {
                if (InCard(t.Key)) continue;
                if (m_buttons.Exists(b => b.Label == t.Value) || m_texts.Contains(t.Value)) continue;
                m_texts.Add(t.Value);
            }
            return true;
        }

        List<string> Lines() { return m_at >= 0 && m_at < m_cards.Count ? CombatCards.Lines(m_cards[m_at]) : new List<string>(); }

        string First()
        {
            var lines = Lines();
            return lines.Count == 0 ? "" : Speech.S(K.MENU_OPTION_FORMAT, lines[0], m_at + 1, m_cards.Count);
        }

        void ReadCard() { m_line = 0; Say(First(), true); }

        internal override void Read()
        {
            Say(Str.Join(string.Join(". ", m_texts.ToArray()), First()), true);
            Log.Info("popup cards: " + string.Join(" | ", m_texts.ToArray()) + " | " + m_cards.Count + " cards | buttons: " + GameButton.Describe(m_buttons));
        }

        internal override bool HandleKey()
        {
            Refresh();
            int n = m_cards.Count;
            if (n == 0) return false;
            if (Keys.Right.Pressed) { if (m_at + 1 < n) { m_at++; ReadCard(); } return true; }
            if (Keys.Left.Pressed) { if (m_at > 0) { m_at--; ReadCard(); } return true; }
            if (Keys.Home.Pressed) { m_at = 0; ReadCard(); return true; }
            if (Keys.End.Pressed) { m_at = n - 1; ReadCard(); return true; }
            var lines = Lines();
            if (Keys.ShiftUp.Pressed) { if (lines.Count > 0) Say(lines[Math.Min(m_line, lines.Count - 1)], true); return true; }
            if (Keys.ShiftDown.Pressed) { for (int i = m_line; i < lines.Count; i++) Say(lines[i]); m_line = Math.Max(0, lines.Count - 1); return true; }
            if (Keys.Down.Pressed) { if (m_line + 1 < lines.Count) Say(lines[++m_line], true); return true; }
            if (Keys.Up.Pressed) { if (m_line > 0) Say(lines[--m_line], true); return true; }
            if (Keys.ShiftTab.Pressed || Keys.Tab.Pressed)
            {
                if (m_buttons.Count == 0) { Say(string.Join(". ", m_texts.ToArray()), true); return true; }
                m_button = (m_button + (Keys.ShiftTab.Pressed ? m_buttons.Count - 1 : 1)) % m_buttons.Count;
                Say(m_buttons[m_button].Label, true);
                return true;
            }
            if (Keys.Enter.Pressed || Keys.Space.Pressed)
            {
                if (m_buttons.Count == 0) { Core.Click.Mouse(m_root); return true; }
                var b = m_buttons[Math.Min(m_button, m_buttons.Count - 1)];
                Log.Info("press: " + b.Label);
                b.Click();
                return true;
            }
            if (Keys.Back.Pressed || Keys.Escape.Pressed)
            {
                foreach (var b in m_buttons) if (Labels.IsBack(b.Label)) { b.Click(); return true; }
                if (m_buttons.Count == 1) { m_buttons[0].Click(); return true; }
                return Keys.Back.Pressed;
            }
            return false;
        }

        internal override string Help() { return Str.Join(Speech.S(K.MENU_HORIZONTAL_HELP_WITH_BACK_BUTTON), Speech.S(K.GAMEPLAY_READ_CARDS_HELP)); }
    }

    class PanelUI : PopupBase
    {
        readonly GameObject m_rootObject;
        internal override GameObject Root { get { return m_rootObject; } }
        internal SceneMgr.Mode Mode;
        readonly bool m_popup;
        Menu m_menu;
        float m_next;
        string m_signature;

        internal PanelUI(GameObject root, bool popup) { m_rootObject = root; m_popup = popup; }

        internal override bool Alive { get { return !m_popup || (Root != null && Root.activeInHierarchy); } }

        string Title()
        {
            if (!m_popup) return SceneNames.Of(Mode);
            return PopupTitle(Root);
        }

        // a popup's title: the text the game names a title or header, else the topmost text
        internal static string PopupTitle(GameObject root)
        {
            var texts = Ui.TextsUnder(root);
            texts.RemoveAll(t => CardsPopupUI.InCard(t.Key));
            foreach (var t in texts)
            {
                var name = t.Key == null ? "" : t.Key.name.ToLowerInvariant();
                if (name.Contains("title") || name.Contains("header") || name.Contains("headline")) return t.Value;
            }
            var sorted = texts.ConvertAll(t => new GameButton { Target = t.Key, Label = t.Value });
            Ui.SortByScreen(sorted);
            return sorted.Count == 0 ? "" : sorted[0].Label;
        }

        List<GameButton> Items()
        {
            var found = Root != null ? Ui.ClickablesUnder(Root, null) : Ui.ScreenButtons();
            foreach (var b in found)
            {
                var box = b.Target as CheckBox;
                if (box != null) b.Label = Str.Join(b.Label, Friends.Checked(box.IsChecked()));
            }
            if (m_popup)
            {
                var options = OptionsMenu.Get();
                if (options != null && options.IsShown() && Root == options.gameObject)
                {
                    var label = OptionsBattleLogsLabel();
                    found.Add(new GameButton
                    {
                        Target = options,
                        Label = label,
                        Click = () =>
                        {
                            Settings.SaveBattleLogs = !Settings.SaveBattleLogs;
                            Say(OptionsBattleLogsLabel(), true);
                        }
                    });
                }
                var labels = new List<string>();
                foreach (var b in found) labels.Add(b.Label);
                var title = Title();
                bool titleSkipped = false;
                foreach (var t in Ui.TextsUnder(Root))
                {
                    if (!titleSkipped && t.Value == title) { titleSkipped = true; continue; }
                    if (labels.Contains(t.Value)) continue;
                    var text = t.Value;
                    labels.Add(text);
                    found.Add(new GameButton { Target = t.Key, Label = text, Click = () => Say(text) });
                }
                Ui.SortByScreen(found);
                // nothing to press: a click on the popup usually goes on
                if (!found.Exists(b => !(b.Target is UberText)))
                {
                    var root = Root;
                    // Some popups use a graphic-only Clickable with no text. The normal
                    // scanner omits it, so route Continue through its PegUIElement handler.
                    var click = root == null ? null : root.GetComponentInChildren<Clickable>(false);
                    var peg = click == null ? null : click.GetComponent<PegUIElement>();
                    if (peg != null)
                        found.Add(new GameButton { Target = peg, Label = Str.Word("GLOBAL_CONTINUE"), Click = () => Core.Click.Peg(peg) });
                    else
                        found.Add(new GameButton { Target = root == null ? null : root.transform, Label = Str.Word("GLOBAL_CONTINUE"), Click = () => Core.Click.Mouse(root) });
                }
            }
            return found;
        }

        static string OptionsBattleLogsLabel()
        {
            var name = Speech.S(K.OPTIONS_MENU_SAVE_BATTLE_LOGS);
            var checkbox = Speech.S(K.OPTIONS_MENU_CHECKBOX_LABEL, name);
            var state = Speech.S(Settings.SaveBattleLogs ? K.OPTIONS_MENU_CHECKBOX_CHECKED : K.OPTIONS_MENU_CHECKBOX_NOT_CHECKED);
            return Str.Join(checkbox, state);
        }

        // true when there is something to read; rebuilt at most twice a second, on the same option
        internal override bool Refresh()
        {
            if (m_menu != null && Time.unscaledTime < m_next) return true;
            m_next = Time.unscaledTime + 0.5f;
            List<GameButton> items;
            try { items = Items(); } catch (Exception e) { Log.Error(e); return m_menu != null; }
            if (items.Count == 0 && m_menu == null) return false;
            var sig = new System.Text.StringBuilder(Title());
            foreach (var b in items) sig.Append('\n').Append(b.Label);
            if (m_menu != null && sig.ToString() == m_signature) return true;
            m_signature = sig.ToString();
            var key = m_menu == null ? null : m_menu.KeyAt(m_menu.Index);
            var at = m_menu == null ? 0 : m_menu.Index;
            var menu = new Menu(this, Title(), Back);
            foreach (var b in items)
            {
                var button = b;
                menu.AddOption(button.Label, () => { Log.Info("press: " + button.Label); button.Click(); }, button.Label);
            }
            var k = menu.IndexOfKey(key);
            menu.Index = k >= 0 ? k : at;
            m_menu = menu;
            if (m_menu.Count > 0) Log.Once((m_popup ? "popup" : "screen") + " '" + Title() + "': " + GameButton.Describe(items));
            return true;
        }

        // the game's own way back (Escape), else a Back / Cancel / Close button
        void Back()
        {
            foreach (var b in Items())
                if (Labels.IsBack(b.Label)) { Log.Info("back: " + b.Label); b.Click(); return; }
            if (!m_popup) { try { Navigation.GoBack(); } catch (Exception e) { Log.Error(e); } }
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
