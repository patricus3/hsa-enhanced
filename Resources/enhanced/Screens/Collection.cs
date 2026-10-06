using System;
using System.Collections;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // My Collection (normal Hearthstone cards and decks), from the game's collection data. Laid out
    // as Hearthstone Access players know it (its behaviour, our code):
    //   My Collection: Browse collection (cards, heroes, card backs, coins, pets), My Decks (edit,
    //     new, delete, paste), Crafting (mass disenchant, craft cards, the dust, set, filters), Change set
    //   the cards: the game's book, page by page in its class tabs. Left / Right go card by card on
    //     to the next or previous page, Page Up / Down turn the page, Home / End, Tab / Shift+Tab the
    //     next class tab, Up / Down the card's lines; the page and class are said when they change.
    //     The number keys filter the mana cost, Ctrl+F searches; Enter opens the card (read it, its
    //     flavor, craft or disenchant it), or adds it to the deck being edited (Shift+Enter: open it)
    //   decks: read line by line (name, format, class, runes, count); Enter edits or deletes
    //   a deck being edited: Add cards (C), See deck (D: its cards, Enter removes one), rename, copy,
    //     Standard / Wild, complete it, Back; every change says the count
    //   card backs, coins: read as the cards are (Enter: favorite or not); heroes: a list
    //   I reads a card's keywords as its tooltips explain them; a card's view has its related cards
    //   sideboards (E.T.C., Zilliax...): Space on the deck's card opens it, then Add cards, See
    //     sideboard, Zilliax's saved versions, Back to deck; the counts are the sideboard's
    //   a new deck: the format picker (its formats with what they are) and the recipes (name, cards
    //     owned, description; Enter picks, Enter again makes the deck, Backspace back)
    static class Collection
    {
        static CollectionUI s_ui;

        internal static CollectionManagerDisplay Display()
        {
            var scenes = SceneMgr.Get();
            if (scenes == null || scenes.IsTransitioning() || !scenes.IsSceneLoaded() || GameState.Get() != null) return null;
            // the collection, or a Tavern Brawl deck being edited on it
            var mode = scenes.GetMode();
            if (mode != SceneMgr.Mode.COLLECTIONMANAGER && !(mode == SceneMgr.Mode.TAVERN_BRAWL && TavernBrawlDisplay.Get() != null && TavernBrawlDisplay.Get().IsInDeckEditMode())) return null;
            var cm = CollectionManager.Get();
            var display = cm == null ? null : cm.GetCollectibleDisplay() as CollectionManagerDisplay;
            if (display == null || !display.IsReady() || !display.IsBookOpened()) return null;
            if (DeckTray.Shown() != null) return null;      // the hero picker (a new deck): the deck tray's
            return display;
        }

        // every frame
        internal static void Tick()
        {
            try { FormatPicker.Tick(); } catch (Exception e) { Log.Error(e); }
            var display = Display();
            if (s_ui != null && display == null) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && display != null)
            {
                var ui = new CollectionUI();
                if (!ui.Refresh(true)) return;
                s_ui = ui;
                Generic.Yield();
                Focus.PushBase(ui);
                if (ui.Focused) ui.Read();
            }
            if (s_ui != null) s_ui.Refresh(false);
        }
    }

    // the new deck's format picker (the hero picker's Switch Format): its formats, each with what it is
    static class FormatPicker
    {
        static FormatPickerUI s_ui;
        static bool s_claimed;

        internal static GameObject Shown()
        {
            var picker = DeckPickerTrayDisplay.Get();
            if (picker == null || !picker.IsModeSwitchShowing) return null;
            var widget = Ref.Get<Component>(picker, "m_formatTypePickerWidget");
            return widget != null && widget.gameObject.activeInHierarchy ? widget.gameObject : null;
        }

        internal static void Tick()
        {
            if (!s_claimed) { s_claimed = true; Generic.Claims.Add(go => { var shown = Shown(); return shown != null && (go == shown || go.transform.IsChildOf(shown.transform) || shown.transform.IsChildOf(go.transform)); }); }
            var root = Shown();
            if (s_ui != null && root == null) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && root != null)
            {
                var ui = new FormatPickerUI(root);
                if (ui.Refresh()) { s_ui = ui; Focus.Push(ui); ui.Read(); }
            }
            if (s_ui != null) s_ui.Refresh();
        }
    }

    class FormatPickerUI : Core.Screen
    {
        readonly GameObject m_root;
        Menu m_menu;
        string m_signature;
        float m_next;

        internal FormatPickerUI(GameObject root) { m_root = root; }

        internal override bool Alive { get { return m_root != null && m_root.activeInHierarchy; } }

        static readonly string[][] Formats = {
            new[] { "GLOBAL_STANDARD", "GLOBAL_TOOLTIP_MODE_STANDARD" }, new[] { "GLOBAL_WILD", "GLOBAL_TOOLTIP_MODE_WILD" },
            new[] { "GLOBAL_TWIST", "GLOBAL_TOOLTIP_MODE_TWIST" }, new[] { "GLUE_TOURNAMENT_CASUAL", "GLOBAL_TOOLTIP_MODE_CASUAL" } };

        internal bool Refresh()
        {
            if (m_menu != null && Time.unscaledTime < m_next) return true;
            m_next = Time.unscaledTime + 0.4f;
            var buttons = Ui.ClickablesUnder(m_root, null);
            if (buttons.Count == 0) return m_menu != null;
            var sig = string.Join("\n", buttons.ConvertAll(b => b.Label).ToArray());
            if (m_menu != null && sig == m_signature) return true;
            m_signature = sig;
            var menu = new Menu(this, Speech.S("ACCESSIBILITY_GLOBAL_CHOOSE_FORMAT"), Back);
            foreach (var b in buttons)
            {
                var button = b;
                string description = null;
                foreach (var f in Formats)
                    if (f[1] != null && string.Equals(Str.Word(f[0]), button.Label, StringComparison.CurrentCultureIgnoreCase)) description = Str.Word(f[1]);
                menu.AddOption(Str.Join(button.Label, description), () => { Log.Info("format picker: " + button.Label); button.Click(); });
            }
            m_menu = menu;
            Log.Info("format picker: " + GameButton.Describe(buttons));
            return true;
        }

        void Back()
        {
            var picker = DeckPickerTrayDisplay.Get();
            var widget = picker == null ? null : Ref.Get<Hearthstone.UI.Widget>(picker, "m_formatTypePickerWidget");
            if (widget != null) widget.TriggerEvent("HIDE");
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }

    class CollectionUI : Core.Screen
    {
        // a card (deck, card back, coin) of a list: its lines and what its keys do
        class Entry
        {
            internal string Name;
            internal EntityDef Def;         // a card: I reads its keywords
            internal Func<List<string>> Lines;
            internal Action Enter, ShiftEnter, Space;
        }

        static CollectionManager CM { get { return CollectionManager.Get(); } }
        static CollectionDeckTray Tray { get { return CollectionDeckTray.Get(); } }
        static bool Editing { get { return CM.IsInEditMode() && CM.GetEditedDeck() != null; } }

        static readonly Core.Key Delete = Core.Key.Of(KeyCode.Delete), ShiftEnter = Core.Key.WithShift(KeyCode.Return),
            CtrlF = Core.Key.WithCtrl(KeyCode.F), KeyC = Core.Key.Of(KeyCode.C), KeyD = Core.Key.Of(KeyCode.D), KeyI = Core.Key.Of(KeyCode.I);

        // where we are: main, browse, decks, editlist, deletelist, crafting, craftfilters, sets, setgroup,
        // book (the cards), heroes, backs, coins, massdisenchant, readcard, deckmenu, seedeck
        string m_view = "main";
        string m_bookFrom = "main";     // the menu the book was opened from (main, crafting, deckmenu)
        int m_setGroup;
        bool m_wasEditing, m_wasCrafting;
        string m_cardFrom = "book";     // the list a card was opened from, and the place in it
        int m_cardAt;
        string m_flippedFor;

        Menu m_menu;                    // a menu view
        List<Entry> m_list;             // a list view (no menu then)
        int m_at, m_line;
        string m_key, m_signature;
        float m_next, m_armedUntil;
        readonly Dictionary<string, int> m_menuAt = new Dictionary<string, int>();     // the option kept per menu

        // the book: the class tab (of those with cards), the page in it (from 1)
        int m_tab, m_page = 1;
        string m_bookSig;

        internal override bool Alive { get { return Collection.Display() != null; } }

        // ---- refresh ----------------------------------------------------------------------------

        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_menu != null || m_list != null;
            m_next = Time.unscaledTime + 0.4f;
            var display = Collection.Display();
            if (display == null) return false;
            var pm = display.m_pageManager as CollectionPageManager;
            if (pm == null || !pm.IsFullyLoaded()) return m_menu != null || m_list != null;
            var tray = Tray;
            if (tray == null || tray.IsUpdatingTrayMode() || tray.IsAutoAddingCardsWithTiming) return m_menu != null || m_list != null;
            if (display.m_inputBlocker != null && display.m_inputBlocker.gameObject.activeSelf) return m_menu != null || m_list != null;

            // the game moves us: a deck opened (or closed), the card's crafting view opened (or closed)
            bool editing = Editing;
            if (editing != m_wasEditing) { m_wasEditing = editing; Go(editing ? "deckmenu" : "main"); }
            bool crafting = CraftingManager.GetIsInCraftingMode();
            if (crafting && !m_wasCrafting)
            {
                m_wasCrafting = true;
                if (m_view != "cardview" && m_view != "readcard") { m_cardFrom = m_list != null && (m_view == "book" || m_view == "seedeck") ? m_view : "book"; m_cardAt = m_at; }
                m_ownedBefore = -1;
                Go("cardview");
            }
            else if (!crafting && m_wasCrafting)
            {
                m_wasCrafting = false;
                if (m_view == "cardview" || m_view == "readcard")
                {
                    // back where the card was, at its place
                    Go(m_cardFrom);
                    m_at = m_cardAt; m_line = 0;
                    m_key = m_cardFrom + "|";
                }
            }

            string key, title, intro = null;
            var items = new List<GameButton>();
            List<Entry> list = null;
            if (!Build(display, pm, out key, out title, ref intro, items, ref list)) return m_menu != null || m_list != null;

            if (list != null)
            {
                m_menu = null;
                m_list = list;
                if (key != m_key)
                {
                    bool keepPlace = m_key != null && SameList(m_key, key);
                    m_key = key;
                    if (!keepPlace) { m_at = 0; m_line = 0; }
                    if (m_at >= m_list.Count) { m_at = Math.Max(0, m_list.Count - 1); m_line = 0; }
                    Log.Info("collection: " + key + " '" + title + "': " + m_list.Count + " items");
                    if (Focused) Say(Str.Join(intro ?? title, First()), true);
                }
                else if (m_at >= m_list.Count) { m_at = Math.Max(0, m_list.Count - 1); m_line = 0; }
                return true;
            }
            m_list = null;
            var sig = key + "\n" + title + "\n" + string.Join("\n", items.ConvertAll(b => b.Label).ToArray());
            bool newStep = key != m_key;
            if (!newStep && sig == m_signature && m_menu != null) return true;
            int at;
            if (m_menu != null && !newStep) at = m_menu.Index;
            else if (!m_menuAt.TryGetValue(key, out at)) at = 0;
            var menu = new Menu(this, title, Back);
            foreach (var b in items) { var click = b.Click; menu.AddOption(b.Label, () => click()); }
            menu.Index = Math.Max(0, Math.Min(at, Math.Max(0, items.Count - 1)));
            m_menu = menu;
            m_signature = sig;
            m_key = key;
            if (newStep)
            {
                Log.Info("collection: " + key + " '" + title + "': " + GameButton.Describe(items));
                if (Focused) m_menu.StartReading();
            }
            return true;
        }

        // a list read again after a change (a card added, crafted, a favorite) keeps its place
        static bool SameList(string a, string b)
        {
            int i = a.IndexOf('|'), j = b.IndexOf('|');
            return i > 0 && j > 0 && a.Substring(0, i) == b.Substring(0, j);
        }

        void Go(string view)
        {
            if (m_menu != null && m_key != null) m_menuAt[m_key] = m_menu.Index;
            m_view = view;
            m_key = null;
            m_next = 0;
        }

        bool Build(CollectionManagerDisplay display, CollectionPageManager pm, out string key, out string title, ref string intro, List<GameButton> items, ref List<Entry> list)
        {
            key = null; title = "";
            var view = display.GetViewMode();
            // the new deck's recipes take the book while the game shows them
            if (view == CollectionUtils.ViewMode.DECK_TEMPLATE) { if (m_view != "templates") { m_view = "templates"; m_templateArmed = -1; } return Templates(display, pm, ref key, ref title, ref list); }
            if (m_view == "templates") { m_view = Editing ? "deckmenu" : "main"; return false; }
            // the game's page follows us; another page of the book (heroes, card backs, coins) is ours too
            if (m_view == "book" && view != CollectionUtils.ViewMode.CARDS) { display.SetViewMode(CollectionUtils.ViewMode.CARDS); return false; }
            switch (m_view)
            {
                case "cardview": return CardView(display, ref key, ref title, items);
                case "readcard": return ReadCard(ref key, ref title, ref list);
                case "related": return Related(ref key, ref title, ref list);
                case "browse": return BrowseMenu(display, ref key, ref title, items);
                case "decks": return DecksMenu(display, ref key, ref title, items);
                case "editlist": return DeckList(display, false, ref key, ref title, ref list);
                case "deletelist": return DeckList(display, true, ref key, ref title, ref list);
                case "crafting": return CraftingMenu(display, ref key, ref title, items);
                case "craftfilters": return CraftFilters(display, ref key, ref title, items);
                case "sets": return Sets(display, ref key, ref title, items);
                case "setgroup": return SetGroup(display, ref key, ref title, items);
                case "massdisenchant": return MassDisenchantView(display, ref key, ref title, items);
                case "book": return Book(display, pm, ref key, ref title, ref intro, ref list);
                case "heroes": if (view != CollectionUtils.ViewMode.HERO_SKINS) return false; return Heroes(display, pm, ref key, ref title, items);
                case "backs": if (view != CollectionUtils.ViewMode.CARD_BACKS) return false; return Backs(ref key, ref title, ref list);
                case "coins": if (view != CollectionUtils.ViewMode.COINS) return false; return Coins(ref key, ref title, ref list);
                case "deckmenu":
                    if (!Editing) { m_view = "main"; return false; }
                    var sb = Sideboard();
                    return sb != null ? SideboardMenu(display, pm, sb, ref key, ref title, items) : DeckMenu(display, ref key, ref title, items);
                case "seedeck": if (!Editing) { m_view = "main"; return false; } return SeeDeck(display, pm, ref key, ref title, ref intro, ref list);
            }
            if (Editing) { m_view = "deckmenu"; return DeckMenu(display, ref key, ref title, items); }
            m_view = "main";
            return MainMenu(display, ref key, ref title, items);
        }

        static void Add(List<GameButton> items, Component anchor, string label, Action click)
        {
            if (string.IsNullOrEmpty(label)) return;
            var said = label;
            items.Add(new GameButton { Target = anchor, Label = label, Click = click ?? (() => Speech.Say(said)) });
        }

        static string A(string key, params object[] args) { return Speech.S("ACCESSIBILITY_" + key, args); }

        static string DustLine() { return A("UI_REWARD_N_ARCANE_DUST", NetCache.Get().GetArcaneDustBalance()); }

        // ---- the menus --------------------------------------------------------------------------

        bool MainMenu(CollectionManagerDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            key = "main";
            title = Str.Word("GLUE_MY_COLLECTION");
            Add(items, display, A("SCREEN_COLLECTION_MANAGER_BROWSE_COLLECTION"), () => Go("browse"));
            Add(items, display, Str.Word("GLUE_COLLECTION_MY_DECKS"), () => Go("decks"));
            if (display.m_craftingModeButton != null && display.m_craftingModeButton.gameObject.activeInHierarchy)
                Add(items, display, Str.Word("GLUE_COLLECTION_CRAFTING_MODE_SHORT"), () =>
                {
                    if (!display.InCraftingMode()) { Log.Info("collection: crafting on"); Core.Click.Peg(display.m_craftingModeButton); }
                    Go("crafting");
                });
            if (SetTray(display) != null) Add(items, display, A("SCREEN_COLLECTION_MANAGER_CHANGE_SET"), () => OpenSets(display, "main"));
            return true;
        }

        bool BrowseMenu(CollectionManagerDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            key = "browse";
            title = A("SCREEN_COLLECTION_MANAGER_BROWSE_COLLECTION");
            Add(items, display, A("GLOBAL_CARD_PLURAL"), () => OpenBook("main"));
            if (display.CanViewHeroSkins())
                Add(items, display, Str.Word("GLUE_COLLECTION_MANAGER_HERO_SKINS_TITLE"), () => { display.SetHeroSkinClass(null); display.SetViewMode(CollectionUtils.ViewMode.HERO_SKINS); Go("heroes"); });
            if (display.CanViewCardBacks())
                Add(items, display, Str.Word("GLUE_COLLECTION_MANAGER_CARD_BACKS_TITLE"), () => { display.SetViewMode(CollectionUtils.ViewMode.CARD_BACKS); Go("backs"); });
            if (display.CanViewCoins())
                Add(items, display, Str.Word("GLUE_COLLECTION_MANAGER_COIN_TITLE"), () => { display.SetViewMode(CollectionUtils.ViewMode.COINS); Go("coins"); });
            if (Pets.Enabled()) Add(items, display, Pets.Title, () => AccessiblePets.Open(null));
            return true;
        }

        bool DecksMenu(CollectionManagerDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            key = "decks";
            title = Str.Word("GLUE_COLLECTION_MY_DECKS");
            bool any = Boxes().Count > 0;
            if (any) Add(items, display, A("SCREEN_COLLECTION_MANAGER_EDIT_DECK"), () => Go("editlist"));
            var content = Tray.GetDecksContent();
            if (content != null && content.m_newDeckButton != null && content.m_newDeckButton.IsEnabled())
                Add(items, display, Str.Word("GLUE_COLLECTION_NEW_DECK"), () => { Log.Info("collection: new deck"); Core.Click.Peg(content.m_newDeckButton); });
            if (any) Add(items, display, A("SCREEN_COLLECTION_MANAGER_DELETE_DECK"), () => Go("deletelist"));
            Add(items, display, Str.Word("GLUE_COLLECTION_DECK_PASTE_TOOLTIP_HEADLINE"), () => { Log.Info("collection: paste a deck"); display.PasteFromClipboardIfValidOrShowStatusMessage(); });
            return true;
        }

        // the deck boxes the tray shows
        static List<CollectionDeckBoxVisual> Boxes()
        {
            var boxes = new List<CollectionDeckBoxVisual>();
            var content = Tray.GetDecksContent();
            var sections = content == null ? null : Ref.Get(content, "m_traySections") as IList;
            if (sections != null)
                foreach (var s in sections)
                {
                    var box = Ref.Get<CollectionDeckBoxVisual>(s, "m_deckBox");
                    var shown = Ref.Invoke(s, "IsDeckBoxShown");
                    if (box != null && shown is bool && (bool)shown && box.GetCollectionDeck() != null) boxes.Add(box);
                }
            return boxes;
        }

        static string Runes(int blood, int frost, int unholy)
        {
            var parts = new List<string>();
            if (blood > 0) parts.Add(blood + " " + A("READ_CARD_RUNE_BLOOD"));
            if (frost > 0) parts.Add(frost + " " + A("READ_CARD_RUNE_FROST"));
            if (unholy > 0) parts.Add(unholy + " " + A("READ_CARD_RUNE_UNHOLY"));
            return parts.Count == 0 ? null : Speech.HumanizeList(parts);
        }

        // the decks, read line by line: name, format, class, runes, the count when cards are not valid
        bool DeckList(CollectionManagerDisplay display, bool delete, ref string key, ref string title, ref List<Entry> list)
        {
            title = A(delete ? "SCREEN_COLLECTION_MANAGER_DELETE_DECK" : "SCREEN_COLLECTION_MANAGER_EDIT_DECK");
            list = new List<Entry>();
            var boxes = Boxes();
            key = (delete ? "deletelist" : "editlist") + "|" + boxes.Count;
            foreach (var box in boxes)
            {
                var b = box;
                var deck = box.GetCollectionDeck();
                var lines = new List<string> { Str.Clean(deck.Name) };
                try { lines.Add(Str.Clean(GameStrings.GetFormatName(deck.FormatType))); } catch { }
                string cls = null;
                try { cls = Str.Clean(GameStrings.GetClassName(deck.GetClass())); } catch { }
                if (!string.IsNullOrEmpty(cls) && cls != lines[0]) lines.Add(cls);
                lines.Add(Runes(deck.Runes.Blood, deck.Runes.Frost, deck.Runes.Unholy));
                int valid = deck.GetTotalValidCardCount(), max = deck.GetMaxCardCount();
                if (valid != deck.GetTotalCardCount() || valid < max) lines.Add(A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_CARDS_IN_DECK", valid, max));
                if (box.IsLocked()) lines.Add(Str.Locked);
                lines.RemoveAll(l => string.IsNullOrEmpty(l));
                list.Add(new Entry
                {
                    Name = lines[0],
                    Lines = () => lines,
                    Enter = delete
                        ? (Action)(() => { if (b.m_deleteButton == null) { Speech.Say(A("GLOBAL_CANNOT_DO_THAT")); return; } Log.Info("collection: delete deck " + deck.Name); Core.Click.Peg(b.m_deleteButton); })
                        : () => { Log.Info("collection: open deck " + deck.Name); b.TriggerTap(); },
                });
            }
            return true;
        }

        bool CraftingMenu(CollectionManagerDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            key = "crafting";
            title = Str.Word("GLUE_COLLECTION_CRAFTING_MODE_SHORT");
            var tray = CraftingTray.Get();
            if (tray != null && tray.m_massDisenchantButton != null && tray.m_massDisenchantButton.gameObject.activeInHierarchy && CM.GetCardsToMassDisenchantCount() > 0)
                Add(items, display, Str.Word("GLUE_COLLECTION_CRAFTING_DISENCHANT_BUTTON_TEXT"), () =>
                {
                    Log.Info("collection: mass disenchant page");
                    Core.Click.Peg(tray.m_massDisenchantButton);
                    Go("massdisenchant");
                });
            Add(items, display, A("SCREEN_COLLECTION_MANAGER_CRAFTING_CRAFT_CARDS"), () => OpenBook("crafting"));
            Add(items, display, A("SCREEN_COLLECTION_MANAGER_CRAFTING_READ_DUST"), () => Speech.Say(DustLine()));
            if (SetTray(display) != null) Add(items, display, A("SCREEN_COLLECTION_MANAGER_CHANGE_SET"), () => OpenSets(display, "crafting"));
            if (tray != null) Add(items, display, Str.Word("GLUE_COLLECTION_CRAFTING_FILTERS"), () => Go("craftfilters"));
            return true;
        }

        // which cards crafting shows: the game's checkboxes
        bool CraftFilters(CollectionManagerDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            var tray = CraftingTray.Get();
            if (tray == null) { m_view = "crafting"; return false; }
            key = "craftfilters";
            title = Str.Word("GLUE_COLLECTION_CRAFTING_FILTERS");
            // named as the tray shows them in its columns: Normal / Premium, Owned / Missing, Include Uncraftable
            string normal = Str.Word("GLUE_COLLECTION_CRAFTING_FILTERS_NORMAL"), premium = Str.Word("GLUE_COLLECTION_CRAFTING_FILTERS_PREMIUM");
            string owned = Str.Word("GLUE_COLLECTION_CRAFTING_FILTERS_OWNED"), missing = Str.Word("GLUE_COLLECTION_CRAFTING_FILTERS_MISSING");
            var boxes = new[] {
                new KeyValuePair<CheckBox, string>(tray.m_normalOwnedCheckbox, normal + " " + owned),
                new KeyValuePair<CheckBox, string>(tray.m_normalMissingCheckbox, normal + " " + missing),
                new KeyValuePair<CheckBox, string>(tray.m_premiumOwnedCheckbox, premium + " " + owned),
                new KeyValuePair<CheckBox, string>(tray.m_premiumMissingCheckbox, premium + " " + missing),
                new KeyValuePair<CheckBox, string>(tray.m_includeUncraftableCheckbox, Str.Word("GLUE_COLLECTION_CRAFTING_FILTERS_INCLUDE") + " " + Str.Word("GLUE_COLLECTION_CRAFTING_FILTERS_UNCRAFTABLE")) };
            foreach (var pair in boxes)
            {
                var box = pair.Key;
                if (box == null || !box.gameObject.activeInHierarchy) continue;
                var b = box;
                var name = pair.Value;
                Add(items, box, Str.Join(A("OPTIONS_MENU_CHECKBOX_LABEL", name), Friends.Checked(box.IsChecked())), () =>
                {
                    var now = !b.IsChecked();
                    Log.Info("collection: " + name + " " + now);
                    b.SetChecked(now);
                    Ref.Invoke(tray, "CheckboxChanged", now);
                    Speech.Say(Friends.Checked(now));
                });
            }
            return true;
        }

        // ---- the set filter ---------------------------------------------------------------------

        string m_setFrom = "main";

        static SetFilterTray SetTray(CollectionManagerDisplay display) { return Ref.Get<SetFilterTray>(display, "m_setFilterTray"); }

        // the game decides which sets its tray lists (format, owned sets, trials...) when the tray
        // opens on screen; ours opens without it, so the same sorting is asked for first
        void OpenSets(CollectionManagerDisplay display, string from)
        {
            m_setFrom = from;
            var tray = SetTray(display);
            if (tray != null) try { Ref.Invoke(tray, "Arrange"); } catch (Exception e) { Log.Error(e); }
            Go("sets");
        }

        static string SetName(CollectionManagerDisplay display)
        {
            var tray = SetTray(display);
            var selected = tray == null ? null : Ref.Get<SetFilterItem>(tray, "m_selected");
            return selected == null ? null : Str.Clean(selected.Text);
        }

        // the set list in groups as the game's tray shows it: the items before the first header, then
        // one option per header (its sets in a menu of their own)
        List<List<SetFilterItem>> SetGroups(CollectionManagerDisplay display, out List<SetFilterItem> headers)
        {
            headers = new List<SetFilterItem>();
            var groups = new List<List<SetFilterItem>> { new List<SetFilterItem>() };
            var tray = SetTray(display);
            var all = tray == null ? null : Ref.Get<List<SetFilterItem>>(tray, "m_items");
            if (all == null) return groups;
            foreach (var item in all)
            {
                // in the list or not: the game's own mark (items scrolled out of view are switched off)
                var scroll = item == null ? null : item.GetComponent<UIBScrollableItem>();
                if (item == null || string.IsNullOrEmpty(item.Text)) continue;
                if (scroll != null ? scroll.m_active != UIBScrollableItem.ActiveState.Active : !item.gameObject.activeSelf) continue;
                if (item.IsHeader) { headers.Add(item); groups.Add(new List<SetFilterItem>()); continue; }
                groups[groups.Count - 1].Add(item);
            }
            return groups;
        }

        void PickSet(CollectionManagerDisplay display, SetFilterItem item)
        {
            Log.Info("collection: set " + item.Text);
            SetTray(display).Select(item);
            m_menuAt[m_setFrom] = m_setFrom == "crafting" ? (CraftingTray.Get() != null && CM.GetCardsToMassDisenchantCount() > 0 ? 1 : 0) : 0;
            Go(m_setFrom);
        }

        bool Sets(CollectionManagerDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            List<SetFilterItem> headers;
            var groups = SetGroups(display, out headers);
            var selected = Ref.Get<SetFilterItem>(SetTray(display), "m_selected");
            key = "sets";
            title = A("SCREEN_COLLECTION_MANAGER_CHANGE_SET");
            foreach (var item in groups[0])
            {
                var it = item;
                Add(items, item, Str.Join(Str.Clean(item.Text), item == selected ? Friends.Checked(true) : null), () => PickSet(display, it));
            }
            for (int i = 0; i < headers.Count; i++)
            {
                if (groups[i + 1].Count == 0) continue;
                int g = i + 1;
                Add(items, headers[i], Str.Clean(headers[i].Text), () => { m_setGroup = g; Go("setgroup"); });
            }
            return true;
        }

        bool SetGroup(CollectionManagerDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            List<SetFilterItem> headers;
            var groups = SetGroups(display, out headers);
            if (m_setGroup <= 0 || m_setGroup >= groups.Count) { m_view = "sets"; return false; }
            var selected = Ref.Get<SetFilterItem>(SetTray(display), "m_selected");
            key = "setgroup:" + m_setGroup;
            title = Str.Clean(headers[m_setGroup - 1].Text);
            foreach (var item in groups[m_setGroup])
            {
                var it = item;
                Add(items, item, Str.Join(Str.Clean(item.Text), item == selected ? Friends.Checked(true) : null), () => PickSet(display, it));
            }
            return true;
        }

        // ---- the book ---------------------------------------------------------------------------

        void OpenBook(string from)
        {
            m_bookFrom = from;
            m_tab = 0; m_page = 1; m_at = 0; m_line = 0;
            m_bookSig = null;
            Go("book");
        }

        static CollectibleCardClassFilter Filter(CollectionPageManager pm) { return Ref.Get(pm, "m_cardsCollection") as CollectibleCardClassFilter; }

        // the class tabs that have cards now
        static List<CollectionTabInfo> Tabs(CollectibleCardClassFilter filter)
        {
            var tabs = new List<CollectionTabInfo>();
            foreach (var c in CollectionPageManager.CLASS_TAB_ORDER)
            {
                var tab = new CollectionTabInfo { tagClass = c };
                if (filter.GetNumPagesForTab(tab) > 0) tabs.Add(tab);
            }
            return tabs;
        }

        static string ClassName(TAG_CLASS c)
        {
            try { return Str.Clean(GameStrings.GetClassName(c)); } catch { return c.ToString(); }
        }

        static string ManaNow(CollectionManagerDisplay display)
        {
            var mana = display.m_manaTabManager;
            if (mana != null) for (int i = 0; i <= 7; i++) if (mana.IsManaValueActive(i)) return i == 7 ? "7+" : i.ToString();
            return null;
        }

        bool Book(CollectionManagerDisplay display, CollectionPageManager pm, ref string key, ref string title, ref string intro, ref List<Entry> list)
        {
            // Zilliax's modules and saved versions: the pages as shown
            if (Sideboard() is ZilliaxSideboardDeck) return ShownPage(display, pm, ref key, ref title, ref intro, ref list);
            var filter = Filter(pm);
            if (filter == null || filter.FindCardsResult == null) return false;
            var tabs = Tabs(filter);
            // the filters changed (search, mana, set, crafting): back to the first page
            var sig = pm.GetSearchText() + ":" + ManaNow(display) + ":" + SetName(display) + ":" + display.InCraftingMode() + ":" + tabs.Count + ":" + (filter.FindCardsResult.m_cards == null ? 0 : filter.FindCardsResult.m_cards.Count);
            bool entering = m_bookSig == null;
            if (sig != m_bookSig) { if (!entering) { m_tab = 0; m_page = 1; m_at = 0; } m_bookSig = sig; }
            title = A("GLOBAL_CARD_PLURAL");
            list = new List<Entry>();
            if (tabs.Count == 0) { key = "book|empty:" + sig; intro = Str.Join(SetName(display), Speech.S("ACCESSIBILITY_LIST_NO_ITEMS")); return true; }
            m_tab = Math.Max(0, Math.Min(m_tab, tabs.Count - 1));
            var tab = tabs[m_tab];
            int pages = filter.GetNumPagesForTab(tab);
            m_page = Math.Max(1, Math.Min(m_page, pages));
            int bookPage;
            var cards = filter.GetPageContentsForTab(tab, m_page, true, out bookPage) ?? new List<CollectibleCard>();
            key = "book|" + sig + ":" + m_tab + ":" + m_page;
            // the page and class first, the set when one is chosen (as on entering)
            intro = Str.Join(entering ? SetName(display) : null, Str.Game("GLUE_COLLECTION_PAGE_NUM", bookPage), ClassName(tab.tagClass));
            // the game's book follows, once per page (its hover and the card's view need it there)
            if (m_flippedFor != key && !pm.ArePagesTurning() && display.GetViewMode() == CollectionUtils.ViewMode.CARDS)
            {
                m_flippedFor = key;
                if (pm.CurrentPageNum != bookPage) try { pm.FlipToPage(bookPage, null, null); } catch (Exception e) { Log.Error(e); }
            }
            bool editing = Editing;
            bool crafting = display.InCraftingMode();
            foreach (var card in cards)
            {
                if (card == null) continue;
                var c = card;
                list.Add(new Entry
                {
                    Name = CardName(c),
                    Def = c.GetEntityDef(),
                    Lines = () => CardLines(c.GetEntityDef(), c.PremiumType, crafting ? (c.OwnedCount > 0 ? Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_OWNED") : Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_MISSING")) : null, c.OwnedCount, true),
                    Enter = () => { if (editing) AddToDeck(c); else OpenCraft(display, pm, c); },
                    ShiftEnter = () => OpenCraft(display, pm, c),
                });
            }
            return true;
        }

        // Right on the last card: the next page (class); Left on the first: the previous page's last card
        void BookMove(int step)
        {
            var display = Collection.Display();
            var filter = display == null ? null : Filter(display.m_pageManager as CollectionPageManager);
            if (filter == null) return;
            var tabs = Tabs(filter);
            if (tabs.Count == 0) return;
            int n = m_list == null ? 0 : m_list.Count;
            if (step > 0 && m_at + 1 < n) { m_at++; m_line = 0; Say(First(), true); return; }
            if (step < 0 && m_at > 0) { m_at--; m_line = 0; Say(First(), true); return; }
            TurnPage(step, step < 0);
        }

        void TurnPage(int step, bool toLast)
        {
            var display = Collection.Display();
            var filter = display == null ? null : Filter(display.m_pageManager as CollectionPageManager);
            if (filter == null) return;
            var tabs = Tabs(filter);
            if (tabs.Count == 0) return;
            int tab = m_tab, page = m_page + step;
            if (page < 1)
            {
                if (tab == 0) return;
                tab--; page = filter.GetNumPagesForTab(tabs[tab]);
            }
            else if (page > filter.GetNumPagesForTab(tabs[tab]))
            {
                if (tab + 1 >= tabs.Count) return;
                tab++; page = 1;
            }
            m_tab = tab; m_page = page;
            m_at = toLast ? 1 << 30 : 0; m_line = 0;
            ReadPage(toLast);
        }

        void NextTab(int step)
        {
            var display = Collection.Display();
            var filter = display == null ? null : Filter(display.m_pageManager as CollectionPageManager);
            if (filter == null) return;
            var tabs = Tabs(filter);
            if (tabs.Count == 0) return;
            m_tab = (m_tab + step + tabs.Count) % tabs.Count;
            m_page = 1; m_at = 0; m_line = 0;
            ReadPage(false);
        }

        // the new page: its number and class, then its first (or last) card
        void ReadPage(bool toLast)
        {
            m_key = null;
            m_next = 0;
            Refresh(true);
            if (toLast && m_list != null && m_list.Count > 0 && m_at != m_list.Count - 1)
            {
                m_at = m_list.Count - 1;
                Say(First());
            }
        }

        // the number keys: one mana cost, the same again turns it off
        void ManaKey(CollectionManagerDisplay display, int cost)
        {
            var mana = display.m_manaTabManager;
            if (mana == null) return;
            bool on = mana.IsManaValueActive(cost);
            Speech.Say(on ? A("SCREEN_COLLECTION_MANAGER_MANA_FILTER_OFF") : A("SCREEN_COLLECTION_MANAGER_MANA_FILTER_ON", cost == 7 ? "7+" : cost.ToString()), true);
            Ref.Invoke(mana, "UpdateCurrentFilterToSingleValue", on ? -1 : cost, true);
            Log.Info("collection: mana " + (on ? "off" : cost.ToString()));
            m_next = Time.unscaledTime + 0.3f;
        }

        void Search(CollectionManagerDisplay display)
        {
            TextInput.Ask(A("SCREEN_COLLECTION_MANAGER_SEARCH_PROMPT"), text =>
            {
                text = (text ?? "").Trim();
                Log.Info("collection: search '" + text + "'");
                if (text.Length > 0) Speech.Say(text);
                if (text.Length == 0) display.m_search.ClearFilter(true); else display.FilterBySearchText(text);
                m_next = Time.unscaledTime + 0.3f;
            });
        }

        // leaving the book: the search and the mana filter go (as they came for it)
        void LeaveBook(CollectionManagerDisplay display)
        {
            try
            {
                var pm = display.m_pageManager as CollectionPageManager;
                if (pm != null && !string.IsNullOrEmpty(pm.GetSearchText())) display.m_search.ClearFilter(true);
                if (ManaNow(display) != null) Ref.Invoke(display.m_manaTabManager, "UpdateCurrentFilterToSingleValue", -1, true);
            }
            catch (Exception e) { Log.Error(e); }
            Go(m_bookFrom);
        }

        // ---- the card's lines -------------------------------------------------------------------

        static string Edition(string name, TAG_PREMIUM premium)
        {
            switch (premium)
            {
                case TAG_PREMIUM.GOLDEN: return A("READ_COLLECTION_CARD_NAME_GOLDEN", name);
                case TAG_PREMIUM.DIAMOND: return A("READ_COLLECTION_CARD_NAME_DIAMOND", name);
                case TAG_PREMIUM.SIGNATURE: return A("READ_COLLECTION_CARD_NAME_SIGNATURE", name);
            }
            return name;
        }

        static string CardName(CollectibleCard c) { return Edition(Str.Clean(c.Name), c.PremiumType); }

        // name (edition), [state], cost, runes, stats, text, tribe, type, rarity, copies, set
        static List<string> CardLines(EntityDef d, TAG_PREMIUM premium, string state, int copies, bool owned)
        {
            var lines = new List<string>();
            if (d == null) return lines;
            try
            {
                lines.Add(Edition(Str.Clean(d.GetName()), premium));
                lines.Add(state);
                if (d.IsHeroPower()) { lines.Add(A("READ_CARD_COST", d.GetCost())); lines.Add(Str.Clean(d.GetCardTextInHand())); return Clean(lines); }
                lines.Add(A("READ_CARD_COST", d.GetCost()));
                lines.Add(Runes(d.GetTag(GAME_TAG.COST_BLOOD), d.GetTag(GAME_TAG.COST_FROST), d.GetTag(GAME_TAG.COST_UNHOLY)));
                if (d.IsHero())
                {
                    try { lines.Add(Str.Clean(GameStrings.GetCardTypeName(d.GetCardType()))); } catch { }
                    lines.Add(A("READ_HERO_CARD_ARMOR", d.GetTag(GAME_TAG.ARMOR)));
                    lines.Add(Str.Clean(d.GetCardTextInHand()));
                    try
                    {
                        var powerId = GameUtils.GetHeroPowerCardIdFromHero(d.GetCardId());
                        var power = string.IsNullOrEmpty(powerId) ? null : DefLoader.Get().GetEntityDef(powerId);
                        if (power != null)
                        {
                            lines.Add(Str.Join(A("GAMEPLAY_ZONE_PLAYER_HERO_POWER"), Str.Clean(power.GetName())));
                            lines.Add(A("READ_CARD_COST", power.GetCost()));
                            lines.Add(Str.Clean(power.GetCardTextInHand()));
                        }
                    }
                    catch { }
                }
                else
                {
                    if (d.GetTag(GAME_TAG.HIDE_STATS) != 1)
                    {
                        if (d.IsMinion()) lines.Add(A("READ_CARD_ATK_HEALTH", d.GetATK(), d.GetHealth()));
                        else if (d.IsWeapon()) lines.Add(A("READ_CARD_ATK_DURABILITY", d.GetATK(), d.GetHealth()));
                        else if (d.IsLocation()) lines.Add(A("READ_CARD_DURABILITY", d.GetHealth()));
                    }
                    lines.Add(Str.Clean(d.GetCardTextInHand()));
                    try { lines.Add(Str.Clean(d.GetRaceText())); } catch { }
                    try { lines.Add(Str.Clean(GameStrings.GetCardTypeName(d.GetCardType()))); } catch { }
                }
                var rarity = d.GetRarity();
                if (rarity != TAG_RARITY.FREE && rarity != TAG_RARITY.INVALID)
                    lines.Add(Str.Clean(GameStrings.GetRarityText(d.IsElite() ? TAG_RARITY.LEGENDARY : rarity)));
                if (copies > 0) lines.Add(A("READ_CARD_N_COPIES", copies));
                try { lines.Add(Str.Clean(GameStrings.GetCardSetNameShortened(d.GetCardSet()))); } catch { }
            }
            catch (Exception e) { Log.Error(e); }
            return Clean(lines);
        }

        static List<string> Clean(List<string> lines) { lines.RemoveAll(l => string.IsNullOrEmpty(l)); return lines; }

        // ---- a deck being edited ----------------------------------------------------------------

        static string DeckCount(CollectionDeck deck) { return A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_CARDS_IN_DECK", deck.GetTotalCardCount(), CM.GetDeckSize()); }

        bool DeckMenu(CollectionManagerDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            var deck = CM.GetEditedDeck();
            var tray = Tray;
            key = "deckmenu:" + deck.ID;
            title = Str.Clean(deck.Name);
            // what is wrong with it, in the game's words
            IList<DeckRuleViolation> violations;
            var rules = CM.GetDeckRuleset();
            if (rules != null && !rules.IsDeckValid(deck, out violations) && violations != null)
                foreach (var v in violations) if (v != null) Add(items, display, Str.Clean(v.DisplayError), null);
            Add(items, display, A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_MENU_ADD_CARDS_OPTION"), () => OpenBook("deckmenu"));
            Add(items, display, A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_MENU_SEE_DECK_OPTION"), () => OpenDeck(deck));
            var content = tray.GetDecksContent();
            bool realCollection = SceneMgr.Get().GetMode() == SceneMgr.Mode.COLLECTIONMANAGER;
            if (content != null && realCollection)
                Add(items, display, Str.Word("GLUE_COLLECTION_DECK_RENAME"), () =>
                    TextInput.Ask(A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_RENAME_DECK_PROMPT"), text =>
                    {
                        var name = (text ?? "").Trim();
                        if (name.Length > 24) name = name.Substring(0, 24);
                        if (name.Length == 0) return;
                        Log.Info("collection: rename to " + name);
                        content.UpdateDeckName(name, true);
                        Speech.Say(name);
                        m_key = null;
                    }));
            Add(items, display, Str.Word("GLUE_COLLECTION_DECK_COPY_TOOLTIP_HEADLINE"), () =>
            {
                DeckRuleViolation why;
                if (deck.GetTotalCardCount() > 0 && deck.CanCopyAsShareableDeck(out why))
                {
                    ClipboardUtils.CopyToClipboard(deck.GetShareableDeck().Serialize());
                    Speech.Say(Str.Word("GLUE_COLLECTION_DECK_COPIED_TOAST"));
                }
                else Speech.Say(Str.Clean(CollectionDeck.GetUserFriendlyCopyErrorMessageFromDeckRuleViolation(null)));
            });
            // Standard / Wild, as the deck's options menu switches it
            if (realCollection && CM.ShouldAccountSeeStandardWild() && (deck.FormatType == PegasusShared.FormatType.FT_STANDARD || deck.FormatType == PegasusShared.FormatType.FT_WILD))
            {
                bool toWild = deck.FormatType == PegasusShared.FormatType.FT_STANDARD;
                var target = toWild ? PegasusShared.FormatType.FT_WILD : PegasusShared.FormatType.FT_STANDARD;
                Add(items, display, Str.Word(toWild ? "GLUE_COLLECTION_TO_WILD" : "GLUE_COLLECTION_TO_STANDARD"), () =>
                {
                    var menu = Ref.Get<DeckOptionsMenu>(content, "m_deckOptionsMenu");
                    var routine = menu == null ? null : Ref.Invoke(menu, "SwitchFormat", target) as IEnumerator;
                    if (routine == null) return;
                    Log.Info("collection: format " + target);
                    menu.StartCoroutine(routine);
                    Speech.Say(A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_FORMAT_CHANGED"));
                    m_key = null;
                });
            }
            if (deck.GetTotalCardCount() < CM.GetDeckSize())
                Add(items, display, Str.Word("GLUE_COLLECTION_DECK_COMPELTE_BUTTON"), () => { Log.Info("collection: complete the deck"); tray.CompleteMyDeckButtonPress(); });
            if (tray.m_doneButton != null)
                Add(items, display, Str.Word("GLOBAL_BACK"), () => { Log.Info("collection: done"); Core.Click.Peg(tray.m_doneButton); });
            return true;
        }

        void OpenDeck(CollectionDeck deck)
        {
            if (deck.GetTotalCardCount() == 0) { Speech.Say(A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_MENU_SEE_DECK_OPTION_EMPTY_DECK")); return; }
            m_at = 0; m_line = 0;
            Go("seedeck");
        }

        // the deck's cards in the tray's order: Enter removes one, Shift+Enter opens the card
        bool SeeDeck(CollectionManagerDisplay display, CollectionPageManager pm, ref string key, ref string title, ref string intro, ref List<Entry> list)
        {
            var main = CM.GetEditedDeck();
            var side = Sideboard();
            CollectionDeck deck = side != null ? (CollectionDeck)side : main;
            var tray = Tray;
            key = "seedeck|" + main.ID + ":" + (side == null ? "" : side.OwnerCardDbId.ToString()) + ":" + deck.GetTotalCardCount();
            title = side != null ? A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_SIDEBOARD") : Str.Clean(main.Name);
            intro = side != null ? SideCount(side) : DeckCount(main);
            list = new List<Entry>();
            var slots = new List<CollectionDeckSlot>(deck.GetSlots());
            slots.RemoveAll(s => s == null || s.GetEntityDef() == null);
            slots.Sort((a, b) =>
            {
                int byCost = a.GetEntityDef().GetCost().CompareTo(b.GetEntityDef().GetCost());
                return byCost != 0 ? byCost : string.Compare(a.GetEntityDef().GetName(), b.GetEntityDef().GetName(), StringComparison.CurrentCultureIgnoreCase);
            });
            if (slots.Count == 0) { Go("deckmenu"); return false; }
            if (side != null) slots.RemoveAll(s => s.GetEntityDef().HasTag(GAME_TAG.ZILLIAX_CUSTOMIZABLE_COSMETICMODULE));
            foreach (var slot in slots)
            {
                var def = slot.GetEntityDef();
                var sl = slot;
                var status = deck.GetSlotStatus(slot);
                string state = status == CollectionDeck.SlotStatus.MISSING ? Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_MISSING")
                             : status == CollectionDeck.SlotStatus.NOT_VALID ? A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_INVALID_CARD") : null;
                var premium = slot.PreferredPremium;
                bool hasSideboard = side == null && def.HasSideboard;
                list.Add(new Entry
                {
                    Name = Str.Join(Edition(Str.Clean(def.GetName()), premium), hasSideboard && main.HasSideboard(sl.CardID) ? A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_SIDEBOARD") : null),
                    Def = def,
                    Lines = () => CardLines(def, premium, state, sl.Count, true),
                    Enter = () =>
                    {
                        Log.Info("collection: remove " + def.GetName());
                        if (tray.RemoveCard(sl.CardID, sl.UnPreferredPremium, deck.IsValidSlot(sl))) Speech.Say(CountNow(), true);
                        m_next = 0;
                    },
                    Space = hasSideboard ? (Action)(() => OpenSideboard(sl.CardID)) : null,
                    ShiftEnter = () =>
                    {
                        var card = CM.GetCard(sl.CardID, premium) ?? CM.GetCard(sl.CardID, TAG_PREMIUM.NORMAL);
                        if (card != null) OpenCraft(display, pm, card); else Speech.Say(A("GLOBAL_CANNOT_DO_THAT"));
                    },
                });
            }
            return true;
        }

        static void AddToDeck(CollectibleCard c)
        {
            var deck = CM.GetEditedDeck();
            var def = c.GetEntityDef();
            if (deck == null || def == null) return;
            if (Sideboard() != null)
            {
                Log.Info("collection: add to the sideboard " + c.Name);
                if (Tray.AddCard(def, c.PremiumType, false, null)) Speech.Say(CountNow(), true);
                else Speech.Say(A("GLOBAL_CANNOT_DO_THAT"), true);
                return;
            }
            // why the game would refuse it, in its own words
            List<RuleInvalidReason> reasons; List<DeckRule> broken;
            var rules = CM.GetDeckRuleset();
            if (rules != null && !rules.CanAddToDeck(def, c.PremiumType, deck, out reasons, out broken, DeckRule.RuleType.DECK_SIZE, DeckRule.RuleType.DEATHKNIGHT_RUNE_LIMIT, DeckRule.RuleType.TOURIST_LIMIT)
                && reasons != null && reasons.Count > 0)
            {
                Speech.Say(Str.Clean(reasons[0].DisplayError), true);
                return;
            }
            if (c.OwnedCount <= 0) { Speech.Say(Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_MISSING"), true); return; }
            Log.Info("collection: add " + c.Name);
            if (Tray.AddCard(def, c.PremiumType, false, null, DeckRule.RuleType.DEATHKNIGHT_RUNE_LIMIT)) Speech.Say(DeckCount(deck), true);
            else Speech.Say(Str.Word("GLUE_COLLECTION_MANAGER_ON_ADD_FULL_DECK_ERROR_TEXT"), true);
        }

        // ---- related cards, keywords ------------------------------------------------------------

        EntityDef m_relatedOf;

        // the cards the game shows beside this one (related, a Fabled bundle, a hero's power...)
        static List<string> RelatedIds(EntityDef def)
        {
            var ids = new List<string>();
            try
            {
                bool runes;
                var related = GameUtils.GetRelatedCardIds(def, out runes);
                if (related != null) foreach (var id in related) if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
                if (def.IsFabled())
                {
                    var bundled = GameUtils.GetBundledCardsIds(def.GetCardId());
                    if (bundled != null) foreach (var id in bundled) if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
                }
            }
            catch (Exception e) { Log.Error(e); }
            ids.Remove(def.GetCardId());
            return ids;
        }

        bool Related(ref string key, ref string title, ref List<Entry> list)
        {
            if (m_relatedOf == null) { m_view = "cardview"; return false; }
            key = "related:" + m_relatedOf.GetCardId();
            title = A("SCREEN_COLLECTION_MANAGER_CRAFTING_READ_RELATED_CARDS");
            list = new List<Entry>();
            foreach (var id in RelatedIds(m_relatedOf))
            {
                var def = DefLoader.Get().GetEntityDef(id);
                if (def == null) continue;
                var d = def;
                list.Add(new Entry { Name = Str.Clean(def.GetName()), Def = def, Lines = () => CardLines(d, TAG_PREMIUM.NORMAL, null, 0, true) });
            }
            return true;
        }

        // the keyword explanations the game shows beside the card (its tooltip panels), from its data
        static List<string> Keywords(EntityDef d)
        {
            var lines = new List<string>();
            if (d == null) return lines;
            try
            {
                if (d.IsMultiClass())
                {
                    var classes = new List<TAG_CLASS>();
                    d.GetClasses(classes);
                    if (classes.Count > 1)
                        lines.Add(Str.Join(Str.Clean(GameStrings.Format("GLOBAL_KEYWORD_MULTICLASS", classes.Count)), Str.Clean(GameStrings.Format("GLOBAL_KEYWORD_MULTICLASS_TEXT", GameStrings.GetClassesNameComma(classes)))));
                }
                var records = new List<KeywordTextDbfRecord>(GameDbf.KeywordText.GetRecords());
                records.Sort((a, b) => a.TooltipInitOrder.CompareTo(b.TooltipInitOrder));
                int dyn1 = d.GetTag(GAME_TAG.DYNAMIC_KEYWORD1), dyn2 = d.GetTag(GAME_TAG.DYNAMIC_KEYWORD2);
                foreach (var rec in records)
                {
                    if (rec == null) continue;
                    var tag = (GAME_TAG)rec.Tag;
                    int value = d.GetTag(tag);
                    int refValue = d.HasReferencedTag(tag) ? d.GetReferencedTag(tag) : 0;
                    if (rec.Tag != 0 && (rec.Tag == dyn1 || rec.Tag == dyn2)) refValue = 1;
                    string name = null, text = null;
                    if (rec.IsCollectionOnly)
                    {
                        if (value != 0 && GameStrings.HasCollectionKeywordText(tag)) { name = GameStrings.Get(rec.Name); text = GameStrings.Get(rec.CollectionText); }
                    }
                    else if (value == 0 && refValue == 0) continue;
                    else if (value != 0 && GameStrings.HasCollectionKeywordText(tag)) { name = GameStrings.GetKeywordName(tag); text = GameStrings.Format(GameStrings.GetCollectionKeywordTextKey(tag), value); }
                    else if (value != 0 && GameStrings.HasKeywordText(tag)) { if (tag == GAME_TAG.WINDFURY && value > 1) continue; name = GameStrings.GetKeywordName(tag); text = GameStrings.Format(GameStrings.GetKeywordTextKey(tag), value); }
                    else if (refValue != 0 && GameStrings.HasRefKeywordText(tag)) { name = GameStrings.GetKeywordName(tag); text = GameStrings.Get(GameStrings.GetRefKeywordTextKey(tag)); }
                    var line = Str.Join(Str.Clean(name), Str.Clean(text));
                    if (!string.IsNullOrEmpty(line) && !lines.Contains(line)) lines.Add(line);
                }
            }
            catch (Exception e) { Log.Error(e); }
            return lines;
        }

        void SayKeywords(EntityDef d)
        {
            var lines = Keywords(d);
            if (lines.Count == 0) { Speech.Say(Speech.S("ACCESSIBILITY_LIST_NO_ITEMS"), true); return; }
            bool first = true;
            foreach (var l in lines) { Speech.Say(l, first); first = false; }
        }

        // ---- sideboards (E.T.C., Zilliax, ...) ---------------------------------------------------

        static SideboardDeck Sideboard()
        {
            var tray = Tray;
            var deck = CM.GetEditedDeck();
            if (tray == null || deck == null || !tray.IsSideboardOpen) return null;
            return deck.GetCurrentSideboardDeck() as SideboardDeck;
        }

        static string SideCount(SideboardDeck sb)
        {
            var z = sb as ZilliaxSideboardDeck;
            if (z != null && z.ZilliaxDataModel != null)
                return A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_CARDS_IN_SIDEBOARD", z.ZilliaxDataModel.FunctionalModuleCardCount, z.ZilliaxDataModel.FunctionalModuleMaxCount);
            return A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_CARDS_IN_SIDEBOARD", sb.GetTotalCardCount(), sb.DataModel == null ? 0 : sb.DataModel.MaxCards);
        }

        // the count after a change: the open sideboard's, else the deck's
        static string CountNow()
        {
            var sb = Sideboard();
            return sb != null ? SideCount(sb) : DeckCount(CM.GetEditedDeck());
        }

        // Space on a deck card with a sideboard: the game opens it as its tile's button does
        void OpenSideboard(string cardId)
        {
            try
            {
                var content = Tray.GetCardsContent();
                var tile = content == null ? null : content.GetCardTileVisual(cardId);
                var actor = tile == null ? null : tile.GetActor();
                if (actor == null) { Speech.Say(A("GLOBAL_CANNOT_DO_THAT"), true); return; }
                Log.Info("collection: open the sideboard of " + cardId);
                Ref.Invoke(Tray, "OnDeckTileSideboardButtonPressed", actor);
                Go("deckmenu");
            }
            catch (Exception e) { Log.Error(e); }
        }

        bool SideboardMenu(CollectionManagerDisplay display, CollectionPageManager pm, SideboardDeck sb, ref string key, ref string title, List<GameButton> items)
        {
            key = "sidemenu:" + sb.OwnerCardDbId;
            var header = sb.DataModel == null ? null : Str.Clean(sb.DataModel.HeaderLabelText);
            title = string.IsNullOrEmpty(header) ? A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_SIDEBOARD") : header;
            Add(items, display, SideCount(sb), null);
            Add(items, display, A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_MENU_ADD_CARDS_OPTION"), () => OpenBook("deckmenu"));
            Add(items, display, A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_MENU_SEE_SIDEBOARD_OPTION"), () =>
            {
                var now = Sideboard();
                if (now == null || now.GetTotalCardCount() == 0) { Speech.Say(A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_MENU_SEE_DECK_OPTION_EMPTY_SIDEBOARD"), true); return; }
                m_at = 0; m_line = 0; Go("seedeck");
            });
            if (sb is ZilliaxSideboardDeck)
                Add(items, display, A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_MENU_LOAD_SAVED_VERSION_OPTION"), () =>
                {
                    Log.Info("collection: Zilliax saved versions");
                    Ref.Invoke(pm, "OnZilliaxSavedVersionsTabPressed", new object[] { null });
                    OpenBook("deckmenu");
                });
            Add(items, display, A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_MENU_BACK_TO_DECK_OPTION"), () =>
            {
                Log.Info("collection: sideboard done");
                Tray.OnSideboardDoneButtonPressed();
                m_key = null; m_next = Time.unscaledTime + 0.5f;
            });
            return true;
        }

        // the cards the book shows now (the Zilliax modules and saved versions are not class pages)
        bool ShownPage(CollectionManagerDisplay display, CollectionPageManager pm, ref string key, ref string title, ref string intro, ref List<Entry> list)
        {
            title = A("GLOBAL_CARD_PLURAL");
            list = new List<Entry>();
            var visuals = new List<GameButton>();
            foreach (var v in pm.GetComponentsInChildren<CollectionCardVisual>())
                if (v != null && v.gameObject.activeInHierarchy && v.GetActor() != null && v.GetActor().GetEntityDef() != null) visuals.Add(new GameButton { Target = v });
            Ui.SortByScreen(visuals);
            var ids = new System.Text.StringBuilder();
            foreach (var b in visuals)
            {
                var actor = ((CollectionCardVisual)b.Target).GetActor();
                var def = actor.GetEntityDef();
                var premium = actor.GetPremium();
                var card = CM.GetCard(def.GetCardId(), premium);
                ids.Append(def.GetCardId()).Append(',');
                var d = def;
                list.Add(new Entry
                {
                    Name = Edition(Str.Clean(def.GetName()), premium),
                    Def = def,
                    Lines = () => CardLines(d, premium, null, card == null ? 0 : card.OwnedCount, true),
                    Enter = () => { if (card != null) AddToDeck(card); else Speech.Say(A("GLOBAL_CANNOT_DO_THAT"), true); },
                });
            }
            key = "book|shown:" + pm.CurrentPageNum + ":" + ids;
            intro = Str.Game("GLUE_COLLECTION_PAGE_NUM", pm.CurrentPageNum);
            return true;
        }

        // ---- the new deck: format and recipe -----------------------------------------------------

        int m_templateArmed = -1;

        bool Templates(CollectionManagerDisplay display, CollectionPageManager pm, ref string key, ref string title, ref List<Entry> list)
        {
            var picker = pm.GetDeckTemplatePicker();
            if (picker == null || picker.m_root == null || !picker.m_root.activeSelf || !picker.IsShowingPacks()) return false;
            var cls = Ref.Get<TAG_CLASS>(picker, "m_currentSelectedClass");
            var templates = CM.GetNonStarterTemplateDecks(picker.CurrentSelectedFormat, cls) ?? new List<CollectionManager.TemplateDeck>();
            var buttons = Ref.Get<List<DeckTemplatePickerButton>>(picker, "m_pickerButtons") ?? new List<DeckTemplatePickerButton>();
            key = "templates|" + cls + ":" + picker.CurrentSelectedFormat;
            title = Str.Game("GLUE_DECK_TEMPLATE_CHOOSE_DECK", ClassName(cls)) ?? A("GLOBAL_CHOOSE_FORMAT");
            list = new List<Entry>();
            for (int i = 0; i < buttons.Count && i < templates.Count; i++)
            {
                var b = buttons[i];
                if (b == null || !b.gameObject.activeInHierarchy) continue;
                var t = templates[i];
                int index = list.Count;
                var lines = Clean(new List<string> { Str.Clean(t.m_title), A("SCREEN_COLLECTION_MANAGER_RECIPE_OWNED_CARDS_COUNT", b.GetOwnedCardCount(), CM.GetDeckSize()), Str.Clean(t.m_description) });
                list.Add(new Entry { Name = lines.Count > 0 ? lines[0] : "", Lines = () => lines, Enter = () => ChooseTemplate(picker, b, index, false) });
            }
            if (picker.m_customDeckButton != null && picker.m_customDeckButton.gameObject.activeInHierarchy)
            {
                var b = picker.m_customDeckButton;
                int index = list.Count;
                var lines = Clean(new List<string> { Str.Clean(GameStrings.Get("GLUE_DECK_TEMPLATE_CUSTOM_DECK")), Str.Clean(GameStrings.Get("GLUE_DECK_TEMPLATE_CUSTOM_DECK_DESCRIPTION")) });
                list.Add(new Entry { Name = lines.Count > 0 ? lines[0] : "", Lines = () => lines, Enter = () => ChooseTemplate(picker, b, index, true) });
            }
            return true;
        }

        // the first Enter picks it (the game shows it), the second makes the deck from it
        void ChooseTemplate(DeckTemplatePicker picker, DeckTemplatePickerButton button, int index, bool custom)
        {
            if (m_templateArmed != index)
            {
                m_templateArmed = index;
                Log.Info("collection: recipe " + index);
                Core.Click.Peg(button);
                Speech.Say(A(custom ? "SCREEN_COLLECTION_MANAGER_CUSTOM_DECK_CHOOSE_HELP" : "SCREEN_COLLECTION_MANAGER_RECIPE_CHOOSE_HELP", Keys.Enter.Name, Keys.Back.Name), true);
                return;
            }
            m_templateArmed = -1;
            if (picker.m_chooseButton == null || !picker.m_chooseButton.IsEnabled()) { Speech.Say(A("GLOBAL_CANNOT_DO_THAT"), true); return; }
            Log.Info("collection: make the deck from recipe " + index);
            Core.Click.Peg(picker.m_chooseButton);
        }

        // ---- one card: read it, its flavor, craft or disenchant it (the game's crafting view) ------

        static void OpenCraft(CollectionManagerDisplay display, CollectionPageManager pm, CollectibleCard c)
        {
            Log.Info("collection: open " + c.Name);
            display.GoToPageWithCard(c.CardId, c.PremiumType);
            Jobs.Run(CraftWhenOnPage(pm, c));
        }

        static IEnumerator CraftWhenOnPage(CollectionPageManager pm, CollectibleCard c)
        {
            var until = Time.unscaledTime + 4f;
            while (Time.unscaledTime < until)
            {
                yield return null;
                if (pm.ArePagesTurning()) continue;
                var visual = pm.GetCardVisual(c.CardId, c.PremiumType);
                if (visual == null) continue;
                CraftingManager.Get().EnterCraftMode(visual.GetActor());
                yield break;
            }
            Speech.Say(A("GLOBAL_CANNOT_DO_THAT"));
        }

        static EntityDef CraftDef(out TAG_PREMIUM premium)
        {
            premium = TAG_PREMIUM.NORMAL;
            var craft = CraftingManager.Get();
            var actor = craft == null ? null : craft.GetShownActor();
            if (actor == null) return null;
            premium = actor.GetPremium();
            return actor.GetEntityDef();
        }

        int m_ownedBefore = -1;

        bool CardView(CollectionManagerDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            var craft = CraftingManager.Get();
            var ui = craft == null ? null : craft.m_craftingUI;
            TAG_PREMIUM premium;
            var def = CraftDef(out premium);
            if (ui == null || def == null) return false;
            var id = def.GetCardId();
            int owned = 0;
            try { owned = craft.GetNumOwnedIncludePending(id, premium); } catch { }
            // a craft or disenchant went through: said once the game counts it
            if (m_ownedBefore >= 0 && owned != m_ownedBefore)
                Speech.Say(A(owned > m_ownedBefore ? "SCREEN_COLLECTION_MANAGER_CRAFTING_CRAFT_CARD_DONE" : "SCREEN_COLLECTION_MANAGER_CRAFTING_DISENCHANT_DONE"), true);
            m_ownedBefore = owned;
            key = "cardview:" + id + ":" + premium;
            title = Edition(Str.Clean(def.GetName()), premium);
            var anchor = (Component)ui;
            Add(items, anchor, A("SCREEN_COLLECTION_MANAGER_CRAFTING_READ_CARD"), () => Go("readcard"));
            string flavor = null;
            try { flavor = Str.Clean(def.GetFlavorText()); } catch { }
            if (RelatedIds(def).Count > 0) { var d = def; Add(items, anchor, A("SCREEN_COLLECTION_MANAGER_CRAFTING_READ_RELATED_CARDS"), () => { m_relatedOf = d; Go("related"); }); }
            if (!string.IsNullOrEmpty(flavor)) Add(items, anchor, A("SCREEN_COLLECTION_MANAGER_CRAFTING_READ_FLAVOR"), () => Speech.Say(flavor, true));
            if (ui.m_soulboundNotification != null && ui.m_soulboundNotification.activeSelf)
                Add(items, anchor, Str.Join(Ui.ShownText(ui.m_soulboundTitle.Text), Ui.ShownText(ui.m_soulboundDesc.Text)), null);
            else
            {
                var value = CraftingManager.GetCardValue(id, premium);
                if (owned > 0 && value != null && ui.m_buttonDisenchant != null && ui.m_buttonDisenchant.gameObject.activeInHierarchy)
                {
                    var b = ui.m_buttonDisenchant;
                    Add(items, b, A("SCREEN_COLLECTION_MANAGER_CRAFTING_DISENCHANT_CARD_FOR_N_DUST", value.GetSellValue()), () => { Log.Info("collection: disenchant " + id); Core.Click.Peg(b); });
                }
                if (value != null && ui.m_buttonCreate != null && ui.m_buttonCreate.gameObject.activeInHierarchy)
                {
                    var b = ui.m_buttonCreate;
                    Add(items, b, A("SCREEN_COLLECTION_MANAGER_CRAFTING_CRAFT_CARD_FOR_N_DUST", value.GetBuyValue()), () =>
                    {
                        var can = craft.CanCraftCardRightNow(def, premium);
                        if (can == CraftingManager.CanCraftCardResult.NotEnoughDust) { Speech.Say(A("SCREEN_COLLECTION_MANAGER_CRAFTING_CRAFT_CARD_ERROR_NOT_ENOUGH_DUST"), true); return; }
                        if (can == CraftingManager.CanCraftCardResult.TooManyCopies) { Speech.Say(A("SCREEN_COLLECTION_MANAGER_CRAFTING_CRAFT_CARD_ERROR_CARD_LIMIT_REACHED"), true); return; }
                        if (can != CraftingManager.CanCraftCardResult.CanCraft && can != CraftingManager.CanCraftCardResult.CanUpgrade) { Speech.Say(A("GLOBAL_CANNOT_DO_THAT"), true); return; }
                        Log.Info("collection: craft " + id);
                        Core.Click.Peg(b);
                    });
                }
            }
            Add(items, anchor, A("SCREEN_COLLECTION_MANAGER_CRAFTING_READ_DUST"), () => Speech.Say(DustLine(), true));
            return true;
        }

        bool ReadCard(ref string key, ref string title, ref List<Entry> list)
        {
            TAG_PREMIUM premium;
            var def = CraftDef(out premium);
            if (def == null) { m_view = "cardview"; return false; }
            int owned = 0;
            try { owned = CraftingManager.Get().GetNumOwnedIncludePending(def.GetCardId(), premium); } catch { }
            key = "readcard:" + def.GetCardId();
            title = "";
            var lines = CardLines(def, premium, null, owned, true);
            list = new List<Entry> { new Entry { Name = lines.Count > 0 ? lines[0] : "", Def = def, Lines = () => lines } };
            return true;
        }

        // ---- mass disenchant --------------------------------------------------------------------

        bool MassDisenchantView(CollectionManagerDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            var md = MassDisenchant.Get();
            if (md == null || display.GetViewMode() != CollectionUtils.ViewMode.MASS_DISENCHANT) return false;
            int total = md.GetTotalAmount();
            key = "massdisenchant";
            title = Str.Word("GLUE_MASS_DISENCHANT_HEADLINE");
            int cards = 0;
            foreach (var list in new List<DisenchantBar>[] { md.m_singleDisenchantBars, md.m_doubleDisenchantBars })
                if (list != null)
                    foreach (var bar in list)
                    {
                        if (bar == null || bar.GetNumCards() <= 0) continue;
                        cards += bar.GetNumCards();
                        string rarity = null;
                        try { rarity = Str.Clean(GameStrings.GetRarityText(bar.m_rarity)); } catch { }
                        var finish = bar.m_premiumType == TAG_PREMIUM.GOLDEN ? Str.Word("GLOBAL_COLLECTION_GOLDEN") : null;
                        Add(items, md, Str.Join(rarity, finish, bar.GetNumCards().ToString(), A("UI_REWARD_N_ARCANE_DUST", bar.GetAmountDust())), null);
                    }
            if (total > 0 && md.m_disenchantButton != null && md.m_disenchantButton.gameObject.activeInHierarchy)
            {
                var label = A("SCREEN_COLLECTION_MANAGER_CRAFTING_MASS_DISENCHANT_FOR_N_DUST", cards, total);
                items.Insert(0, new GameButton { Target = md.m_disenchantButton, Label = label, Click = () =>
                {
                    if (Time.unscaledTime > m_armedUntil)
                    {
                        m_armedUntil = Time.unscaledTime + 6f;
                        Speech.Say(Str.Join(label, Str.Word("GLOBAL_CONFIRM"), Keys.Enter.Name), true);
                        return;
                    }
                    m_armedUntil = 0;
                    Log.Info("collection: mass disenchant " + total);
                    Core.Click.Peg(md.m_disenchantButton);
                    Jobs.Run(After(2f, () => { Speech.Say(A("SCREEN_COLLECTION_MANAGER_CRAFTING_DISENCHANT_DONE")); display.SetViewMode(CollectionUtils.ViewMode.CARDS); Go("crafting"); }));
                } });
            }
            return true;
        }

        static IEnumerator After(float seconds, Action action)
        {
            yield return new WaitForSecondsRealtime(seconds);
            try { action(); } catch (Exception e) { Log.Error(e); }
        }

        // ---- heroes, card backs, coins ----------------------------------------------------------

        bool Heroes(CollectionManagerDisplay display, CollectionPageManager pm, ref string key, ref string title, List<GameButton> items)
        {
            key = "heroes";
            title = Str.Word("GLUE_COLLECTION_MANAGER_HERO_SKINS_TITLE");
            var heroes = Ref.Get<CollectibleCardHeroesFilter>(pm, "m_heroesCollection");
            var list = heroes == null ? null : heroes.GetAllResults();
            if (list == null) return false;
            foreach (var h in list)
            {
                if (h == null) continue;
                var c = h;
                bool owned = h.OwnedCount > 0, isFav = CM.IsFavoriteHero(h.CardId);
                Add(items, display, Str.Join(Str.Clean(h.Name), ClassName(h.Class), isFav ? Str.Game("GLUE_COLLECTION_MANAGER_FAVORITE_DEFAULT_TEXT", ClassName(h.Class)) : null,
                    owned ? Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_OWNED") : Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_MISSING")), () =>
                {
                    if (!owned) { Speech.Say(Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_MISSING")); return; }
                    if (!HeroSkinUtils.CanToggleFavoriteHeroSkin(c.Class, c.CardId)) { Speech.Say(A("GLOBAL_CANNOT_DO_THAT")); return; }
                    var def = CM.GetFavoriteHero(c.CardId);
                    bool make = def == null;
                    if (make) def = new NetCache.CardDefinition { Name = c.CardId, Premium = c.PremiumType };
                    Log.Info("collection: favorite hero " + c.CardId + " " + make);
                    Network.Get().SetFavoriteHero(c.Class, def, make);
                    if (!Network.IsLoggedIn()) CM.UpdateFavoriteHero(c.Class, c.CardId, c.PremiumType, make);
                    Speech.Say(A("SCREEN_COLLECTION_MANAGER_FAVORITE_HERO_SKIN_SET"));
                    m_next = Time.unscaledTime + 1f;
                });
            }
            return true;
        }

        bool Backs(ref string key, ref string title, ref List<Entry> list)
        {
            title = Str.Word("GLUE_COLLECTION_MANAGER_CARD_BACKS_TITLE");
            list = new List<Entry>();
            var backs = CardBackManager.Get();
            var sig = new System.Text.StringBuilder();
            foreach (var b in backs.GetFilteredCardBacks(false))
            {
                if (b == null) continue;
                var id = b.m_cardBackId; bool owned = b.m_owned;
                sig.Append(id).Append(b.m_favorited ? "*" : "").Append(',');
                var lines = new List<string> { Str.Clean(b.m_name), b.m_favorited ? Str.Word("GLUE_COLLECTION_MANAGER_FAVORITE_CARD_BACK") : null,
                    owned ? Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_OWNED") : Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_MISSING") };
                try { var rec = GameDbf.CardBack.GetRecord(id); if (rec != null) lines.Add(Str.Clean(rec.Description)); } catch { }
                Clean(lines);
                list.Add(new Entry
                {
                    Name = Str.Join(lines[0], b.m_favorited ? Str.Word("GLUE_COLLECTION_MANAGER_FAVORITE_CARD_BACK") : null),
                    Lines = () => lines,
                    Enter = () =>
                    {
                        if (!owned) { Speech.Say(Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_MISSING")); return; }
                        if (!backs.CanToggleFavoriteCardBack(id)) { Speech.Say(A("GLOBAL_CANNOT_DO_THAT")); return; }
                        Log.Info("collection: favorite card back " + id);
                        backs.HandleFavoriteToggle(id);
                        Speech.Say(A("SCREEN_COLLECTION_MANAGER_FAVORITE_CARD_BACK_SET"), true);
                    },
                });
            }
            key = "backs|" + sig;
            return true;
        }

        bool Coins(ref string key, ref string title, ref List<Entry> list)
        {
            title = Str.Word("GLUE_COLLECTION_MANAGER_COIN_TITLE");
            list = new List<Entry>();
            var coins = CosmeticCoinManager.Get();
            var sig = new System.Text.StringBuilder();
            foreach (var coin in coins.GetOrderedCoinCards())
            {
                if (coin == null) continue;
                var c = coin;
                var id = coins.GetCoinIdFromCoinCard(c.CardId);
                bool owned = coins.IsOwnedCoinCard(c.CardId), isFav = coins.IsFavoriteCoin(id);
                sig.Append(id).Append(isFav ? "*" : "").Append(',');
                string cosmetic = null;
                try { var rec = GameDbf.CosmeticCoin.GetRecord(id); if (rec != null) cosmetic = Str.Clean(rec.Name); } catch { }
                var lines = new List<string> { string.IsNullOrEmpty(cosmetic) ? Str.Clean(c.Name) : cosmetic, isFav ? Str.Word("GLUE_COLLECTION_MANAGER_FAVORITE_COIN") : null,
                    owned ? Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_OWNED") : Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_MISSING") };
                lines.AddRange(CardLines(c.GetEntityDef(), c.PremiumType, null, 0, owned));
                Clean(lines);
                list.Add(new Entry
                {
                    Name = Str.Join(lines[0], isFav ? Str.Word("GLUE_COLLECTION_MANAGER_FAVORITE_COIN") : null),
                    Lines = () => lines,
                    Enter = () =>
                    {
                        if (!owned) { Speech.Say(Str.Word("GLUE_COLLECTION_MANAGER_SEARCH_MISSING")); return; }
                        Log.Info("collection: favorite coin " + id + " " + !isFav);
                        coins.RequestSetFavoriteCosmeticCoin(id, !isFav);
                        Speech.Say(A("SCREEN_COLLECTION_MANAGER_FAVORITE_COIN_SET"), true);
                    },
                });
            }
            key = "coins|" + sig;
            return true;
        }

        // ---- reading a list ---------------------------------------------------------------------

        List<string> Lines()
        {
            if (m_list == null || m_at < 0 || m_at >= m_list.Count) return new List<string>();
            var e = m_list[m_at];
            var lines = e.Lines == null ? new List<string>() : e.Lines();
            if (lines.Count == 0) lines.Add(e.Name);
            return lines;
        }

        string First()
        {
            if (m_list == null || m_list.Count == 0) return Speech.S("ACCESSIBILITY_LIST_NO_ITEMS");
            if (m_view == "readcard") return m_list[0].Name;
            return Speech.S(K.MENU_OPTION_FORMAT, m_list[m_at].Name, m_at + 1, m_list.Count);
        }

        void Move(int to)
        {
            if (m_list == null || m_list.Count == 0) { Say(First(), true); return; }
            m_at = Math.Max(0, Math.Min(m_list.Count - 1, to)); m_line = 0;
            Say(First(), true);
        }

        bool ListKeys()
        {
            var display = Collection.Display();
            if (display == null) return false;
            int n = m_list.Count;
            bool book = m_view == "book";
            if (book)
            {
                if (Keys.Right.Pressed) { BookMove(1); return true; }
                if (Keys.Left.Pressed) { BookMove(-1); return true; }
                if (Keys.PageDown.Pressed) { TurnPage(1, false); return true; }
                if (Keys.PageUp.Pressed) { TurnPage(-1, false); return true; }
                if (Keys.ShiftTab.Pressed) { NextTab(-1); return true; }
                if (Keys.Tab.Pressed) { NextTab(1); return true; }
                if (CtrlF.Pressed) { Search(display); return true; }
                for (int i = 0; i <= 7; i++)
                    if (Core.Key.Of((KeyCode)((int)KeyCode.Alpha0 + i)).Pressed || Core.Key.Of((KeyCode)((int)KeyCode.Keypad0 + i)).Pressed) { ManaKey(display, i); return true; }
            }
            else
            {
                if (Keys.Right.Pressed || Keys.Tab.Pressed) { if (m_at + 1 < n) Move(m_at + 1); return true; }
                if (Keys.Left.Pressed || Keys.ShiftTab.Pressed) { if (m_at > 0) Move(m_at - 1); return true; }
            }
            if (Keys.Home.Pressed) { Move(0); return true; }
            if (Keys.End.Pressed) { Move(n - 1); return true; }
            // in a deck: C adds cards, D the deck again
            if (Editing && (book || m_view == "seedeck"))
            {
                if (KeyC.Pressed) { OpenBook("deckmenu"); return true; }
                if (KeyD.Pressed) { var deck = CM.GetEditedDeck(); if (deck != null) { m_key = null; OpenDeck(deck); } return true; }
            }
            if (KeyI.Pressed) { if (n > 0 && m_at < n && m_list[m_at].Def != null) SayKeywords(m_list[m_at].Def); return true; }
            if (Keys.Space.Pressed && n > 0 && m_at < n && m_list[m_at].Space != null) { m_list[m_at].Space(); return true; }
            var lines = Lines();
            if (Keys.ShiftUp.Pressed) { if (lines.Count > 0) Say(lines[Math.Min(m_line, lines.Count - 1)], true); return true; }
            if (Keys.ShiftDown.Pressed) { for (int i = m_line; i < lines.Count; i++) Say(lines[i]); m_line = Math.Max(0, lines.Count - 1); return true; }
            if (Keys.Down.Pressed) { if (m_line + 1 < lines.Count) Say(lines[++m_line], true); return true; }
            if (Keys.Up.Pressed) { if (m_line > 0) Say(lines[--m_line], true); return true; }
            if (n > 0 && m_at < n)
            {
                var e = m_list[m_at];
                if (ShiftEnter.Pressed) { if (e.ShiftEnter != null) e.ShiftEnter(); return true; }
                if (Keys.Enter.Pressed) { if (e.Enter != null) e.Enter(); return true; }
            }
            if (Keys.Back.Pressed) { Back(); return true; }
            return false;
        }

        // ---- back -------------------------------------------------------------------------------

        void Back()
        {
            var display = Collection.Display();
            if (display == null) return;
            switch (m_view)
            {
                case "readcard": case "related": Go("cardview"); return;
                case "templates":
                    if (m_templateArmed >= 0) { m_templateArmed = -1; Say(First(), true); return; }
                    var picker = (display.m_pageManager as CollectionPageManager).GetDeckTemplatePicker();
                    if (picker != null) { Log.Info("collection: back from the recipes"); picker.OnNavigateBack(); }
                    return;
                case "cardview": Log.Info("collection: close the card"); Navigation.GoBack(); return;     // the book again when it closes
                case "book": LeaveBook(display); return;
                case "browse": case "decks": Go("main"); return;
                case "editlist": case "deletelist": Go("decks"); return;
                case "crafting":
                    if (display.InCraftingMode() && display.m_craftingModeButton != null) { Log.Info("collection: crafting off"); Core.Click.Peg(display.m_craftingModeButton); }
                    Go("main"); return;
                case "craftfilters": Go("crafting"); return;
                case "sets": Go(m_setFrom); return;
                case "setgroup": Go("sets"); return;
                case "massdisenchant": display.SetViewMode(CollectionUtils.ViewMode.CARDS); Go("crafting"); return;
                case "heroes": case "backs": case "coins": display.SetViewMode(CollectionUtils.ViewMode.CARDS); Go("browse"); return;
                case "seedeck": Go("deckmenu"); return;
                case "deckmenu": return;        // its Back option finishes the deck
            }
            Log.Info("collection: back");
            Navigation.GoBack();
        }

        internal override bool HandleKey()
        {
            if (m_list != null && m_menu == null) return ListKeys();
            if (m_menu == null) return false;
            if (m_view == "deckmenu" && Keys.Back.Pressed) return true;
            if (m_view == "cardview" && KeyI.Pressed) { TAG_PREMIUM p; SayKeywords(CraftDef(out p)); return true; }
            if (m_view == "deckmenu" && Editing)
            {
                if (KeyC.Pressed) { OpenBook("deckmenu"); return true; }
                if (KeyD.Pressed) { OpenDeck(CM.GetEditedDeck()); return true; }
            }
            return m_menu.HandleKey();
        }

        internal override string Help()
        {
            if (m_list != null && m_menu == null)
            {
                if (m_view == "book")
                    return Str.Join(A("SCREEN_COLLECTION_MANAGER_READ_COLLECTION_HELP", Keys.Tab.Name), A("SCREEN_COLLECTION_MANAGER_READ_COLLECTION_FILTERS_HELP", CtrlF.Name),
                        Editing ? A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_ADD_CARD_HELP", Keys.Enter.Name) : null);
                if (m_view == "seedeck")
                    return Str.Join(A("SCREEN_ADVENTURE_SCREEN_READING_DECK_HELP"), A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_REMOVE_CARD_HELP", Keys.Enter.Name), A("SCREEN_COLLECTION_MANAGER_EDIT_DECK_CRAFT_CARD_HELP", ShiftEnter.Name));
                if (m_view == "backs") return A("SCREEN_COLLECTION_MANAGER_READ_CARD_BACKS_HELP", Keys.Enter.Name);
                if (m_view == "coins") return A("SCREEN_COLLECTION_MANAGER_READ_COINS_HELP", Keys.Enter.Name);
                return Speech.S(K.MENU_HORIZONTAL_HELP_WITH_BACK_BUTTON, Keys.Enter.Name, Keys.Back.Name);
            }
            return m_menu == null ? "" : m_menu.Help();
        }

        internal override void Read()
        {
            if (m_list != null && m_menu == null) { Say(First(), true); return; }
            if (m_menu != null) m_menu.StartReading();
        }
    }
}
