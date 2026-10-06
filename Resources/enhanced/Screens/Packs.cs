using System;
using System.Collections;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Opening packs, as Hearthstone Access players know it (its behaviour, our code). Everything is a
    // list: Left / Right (Tab, Home / End) go through it, Up / Down through an item's lines.
    //   the packs: "3 card packs, 2 of 5", then (Down) why it is locked, its name; Enter opens one,
    //     Space asks how many to open at once (2 to as many as the game allows); Backspace leaves
    //   an opened pack: "1 card, 1 of 5" until turned; Enter turns the card, Space a random one; each
    //     card's name is said as it turns; its lines: name (edition), class, cost, runes, stats, text,
    //     tribe, type, rarity, new. When all are turned: "Press Enter to continue", Enter is Done
    //   several packs: the packs-opened count and Highlights, then each page of cards as above (Space
    //     turns the whole page; Enter continues once all are turned); the summary by rarity (Down: the
    //     classes or the legendaries), Enter is Done
    static class Packs
    {
        static PacksUI s_ui;

        internal static PackOpening Screen()
        {
            var scenes = SceneMgr.Get();
            if (scenes == null || scenes.GetMode() != SceneMgr.Mode.PACKOPENING || scenes.IsTransitioning() || !scenes.IsSceneLoaded()) return null;
            var p = PackOpening.Get();
            if (p == null || !p.IsReady() || !Ref.Get<bool>(p, "m_shown")) return null;
            return p;
        }

        // every frame
        internal static void Tick()
        {
            var screen = Screen();
            if (s_ui != null && (screen == null || s_ui.Screen != screen)) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && screen != null)
            {
                var ui = new PacksUI(screen);
                if (!ui.Refresh(true)) return;
                s_ui = ui;
                Generic.Yield();
                Focus.PushBase(ui);
                if (ui.Focused) ui.Read();
            }
            if (s_ui != null) s_ui.Refresh(false);
        }
    }

    class PacksUI : Core.Screen
    {
        internal readonly PackOpening Screen;

        // one item of a list: its lines, and the card behind it (for turning it)
        class Item
        {
            internal Func<List<string>> Lines;
            internal PackOpeningCard Card;
            internal UnopenedPack Pack;
        }

        string m_view;              // list, pack, highlights, summary
        string m_key;
        List<Item> m_items = new List<Item>();
        int m_at, m_line;
        float m_next;
        int m_listTypes = -1;
        bool m_saidContinue, m_firstHighlights = true;
        readonly HashSet<PackOpeningCard> m_turned = new HashSet<PackOpeningCard>();

        internal PacksUI(PackOpening screen) { Screen = screen; }

        internal override bool Alive { get { return Screen != null && Screen && Packs.Screen() == Screen; } }

        PackOpeningDirector Director { get { return Ref.Get<PackOpeningDirector>(Screen, "m_director"); } }

        bool Busy { get { return Screen.m_InputBlocker != null && Screen.m_InputBlocker.activeSelf; } }

        static string A(string key, params object[] args) { return Speech.S("ACCESSIBILITY_" + key, args); }

        // ---- what is on screen ------------------------------------------------------------------

        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_view != null;
            m_next = Time.unscaledTime + 0.25f;
            var director = Director;
            string view, key, intro = null;
            List<Item> items;
            if (director != null && director.IsMassPackOpening()) { if (!Mass(director, out view, out key, out intro, out items)) return m_view != null; }
            else
            {
                var cards = HiddenCards(director);
                if (director != null && (director.IsPlaying() || director.IsDoneButtonShown) && cards != null && cards.Count > 0) { view = "pack"; items = CardItems(cards); key = "pack|" + cards[0].GetInstanceID(); }
                else if (Busy) return m_view != null;     // an opening under way: the cards come next
                else { view = "list"; items = PackItems(); key = "list|" + items.Count; }
            }
            bool newKey = key != m_key;
            var before = m_view;
            m_view = view; m_items = items;
            if (m_at >= m_items.Count) { m_at = Math.Max(0, m_items.Count - 1); m_line = 0; }

            if (view == "list")
            {
                if (m_items.Count == 0) { if (newKey) { m_key = key; Log.Info("packs: none left"); Navigation.GoBack(); } return true; }
                if (newKey)
                {
                    m_key = key;
                    // back from opening a pack, the same packs: the one in focus again, with its count
                    bool same = before != null && before != "list" && m_listTypes == m_items.Count;
                    m_listTypes = m_items.Count;
                    if (!same) { m_at = 0; m_line = 0; }
                    Log.Info("packs: list, " + m_items.Count + " kinds");
                    if (Focused) Say(same ? First() : Str.Join(Str.Word("GLUE_OPEN_PACKS"), First()), true);
                }
                return true;
            }
            if (newKey)
            {
                m_key = key; m_at = 0; m_line = 0; m_saidContinue = false;
                m_turned.Clear();
                foreach (var it in m_items) if (it.Card != null && it.Card && it.Card.IsRevealed()) m_turned.Add(it.Card);
                Log.Info("packs: " + key);
                if (Focused) Say(Str.Join(intro, First()), true);
            }
            // a card turned (any way): its name; all turned: the way on
            foreach (var it in m_items)
            {
                var c = it.Card;
                if (c == null || !c || !c.IsRevealed() || m_turned.Contains(c)) continue;
                m_turned.Add(c);
                var lines = it.Lines();
                if (lines.Count > 0 && Focused) Say(lines[0]);
            }
            if (!m_saidContinue && ContinueShown(director))
            {
                m_saidContinue = true;
                if (Focused) Say(A("PRESS_KEY_TO_CONTINUE", Keys.Enter.Name));
            }
            return true;
        }

        bool ContinueShown(PackOpeningDirector director)
        {
            if (director == null) return false;
            if (m_view == "pack") return director.IsDoneButtonShown;
            if (m_view == "highlights") return director.IsMassPackOpeningHighlightsContinueButtonShowing();
            return false;
        }

        // ---- the packs ---------------------------------------------------------------------------

        List<Item> PackItems()
        {
            var items = new List<Item>();
            var packs = new List<UnopenedPack>();
            foreach (var p in UnityEngine.Object.FindObjectsByType<UnopenedPack>(FindObjectsSortMode.None))
                if (p != null && p.gameObject.activeInHierarchy && p.GetCreatorPack() == null && p.GetCount() > 0) packs.Add(p);
            // as the tray shows them, left to right
            var shown = packs.ConvertAll(p => new GameButton { Target = p });
            Ui.SortByScreen(shown);
            var seen = new HashSet<int>();
            foreach (var b in shown)
            {
                var pack = (UnopenedPack)b.Target;
                if (!seen.Add(pack.GetBoosterId())) continue;
                var p = pack;
                items.Add(new Item
                {
                    Pack = pack,
                    Lines = () =>
                    {
                        string why;
                        var lines = new List<string> { A("UI_REWARD_N_CARD_PACKS", p.GetCount()) };
                        if (!p.CanOpenPack(out why)) lines.Add(Str.Clean(why));
                        lines.Add(PackName(p.GetBoosterId()));
                        lines.RemoveAll(l => string.IsNullOrEmpty(l));
                        return lines;
                    },
                });
            }
            return items;
        }

        static string PackName(int id)
        {
            var record = GameDbf.Booster.GetRecord(id);
            if (record == null) return null;
            var name = record.Name == null ? null : Str.Clean(record.Name.GetString());
            // a pack named only by its set's code (a new set's pack): the set's name
            if (string.IsNullOrEmpty(name) || name.Length <= 4 && name.ToUpperInvariant() == name)
            {
                string set = null;
                try { if (record.CardSetId > 0) set = Str.Clean(GameStrings.GetCardSetName((TAG_CARD_SET)record.CardSetId)); } catch { }
                if (string.IsNullOrEmpty(set) && record.ShortName != null) set = Str.Clean(record.ShortName.GetString());
                if (!string.IsNullOrEmpty(set) && set != name) name = set;
            }
            return name;
        }

        void OpenOne(UnopenedPack pack)
        {
            string why;
            if (!pack.CanOpenPack(out why)) { Say(Str.Clean(why), true); return; }
            Log.Info("packs: open one " + pack.GetBoosterId());
            Ref.Set(Screen, "m_lastOpenedBoosterId", pack.GetBoosterId());
            Ref.Call(Screen, "AutomaticallyOpenPack");
        }

        // several at once: how many is asked (2 to what the game allows and you have)
        void OpenSeveral(UnopenedPack pack)
        {
            string why;
            if (!pack.CanOpenPack(out why)) { Say(Str.Clean(why), true); return; }
            if (pack.GetCount() <= 1) return;
            int max = 0;
            try { max = Screen.MassPackOpeningEnabled() ? Math.Min(Screen.MassPackOpeningPackLimit(pack.GetBoosterId()), pack.GetCount()) : 0; } catch { }
            if (max < 2) return;
            var p = pack;
            TextInput.Ask(A("MASS_PACK_OPENING_QUANTITY_PROMPT", 2, max), text =>
            {
                int n;
                if (!int.TryParse((text ?? "").Trim(), out n) || n < 2 || n > max)
                {
                    Say(A("MASS_PACK_OPENING_QUANTITY_PROMPT_ERROR", 2, max), true);
                    Say(First());
                    return;
                }
                Log.Info("packs: open " + n + " of " + p.GetBoosterId());
                Ref.Invoke(Screen, "OpenBooster", p, n);
            }, () => Say(First(), true));
        }

        // ---- the cards of a pack (or of a highlights page) ----------------------------------------

        static List<PackOpeningCard> HiddenCards(PackOpeningDirector director)
        {
            var hidden = director == null ? null : Ref.Get(director, "m_hiddenCards");
            return hidden == null ? null : Ref.Get<List<PackOpeningCard>>(hidden, "m_cards");
        }

        static List<Item> CardItems(List<PackOpeningCard> cards)
        {
            var items = new List<Item>();
            foreach (var card in cards)
            {
                if (card == null || !card) continue;
                var c = card;
                items.Add(new Item { Card = c, Lines = () => CardLines(c) });
            }
            return items;
        }

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

        // the class as the pack's card shows it: its class, its classes, or all classes
        static string ClassLine(EntityDef def)
        {
            try
            {
                if (def.IsMultiClass())
                {
                    var classes = new List<TAG_CLASS>();
                    def.GetClasses(classes);
                    if (classes.Count >= 10) return Str.Word("GLUE_PACK_OPENING_ALL_CLASSES");
                    if (classes.Count > 1) return Speech.HumanizeList(classes.ConvertAll(c => Str.Clean(GameStrings.GetClassName(c))));
                }
                return Str.Clean(GameStrings.GetClassName(def.GetClass()));
            }
            catch { return null; }
        }

        // turned: name (edition), class, cost, runes, stats, text, tribe, type, rarity, new; else "1 card"
        static List<string> CardLines(PackOpeningCard card)
        {
            if (!card.IsRevealed()) return new List<string> { A("UI_REWARD_TYPE_ONE_CARD") };
            var merc = PackCards.MercLines(card);
            if (merc != null && merc.Count > 0) return new List<string>(merc);
            var def = card.GetEntityDef();
            if (def == null) return new List<string> { A("UI_REWARD_TYPE_ONE_CARD") };
            var lines = new List<string> { Edition(Str.Clean(def.GetName()), card.GetPremium()), ClassLine(def) };
            var rest = CombatCards.Lines(def);
            string flavor = null;
            try { flavor = Str.Clean(def.GetFlavorText()); } catch { }
            for (int i = 1; i < rest.Count; i++)
            {
                if (!string.IsNullOrEmpty(flavor) && rest[i] == flavor) continue;
                lines.Add(rest[i]);
            }
            int runes = 0;
            foreach (var t in new[] { GAME_TAG.COST_BLOOD, GAME_TAG.COST_FROST, GAME_TAG.COST_UNHOLY }) runes += def.GetTag(t);
            if (runes > 0)
            {
                var parts = new List<string>();
                if (def.GetTag(GAME_TAG.COST_BLOOD) > 0) parts.Add(def.GetTag(GAME_TAG.COST_BLOOD) + " " + A("READ_CARD_RUNE_BLOOD"));
                if (def.GetTag(GAME_TAG.COST_FROST) > 0) parts.Add(def.GetTag(GAME_TAG.COST_FROST) + " " + A("READ_CARD_RUNE_FROST"));
                if (def.GetTag(GAME_TAG.COST_UNHOLY) > 0) parts.Add(def.GetTag(GAME_TAG.COST_UNHOLY) + " " + A("READ_CARD_RUNE_UNHOLY"));
                lines.Insert(Math.Min(3, lines.Count), Speech.HumanizeList(parts));
            }
            if (Ref.Get<bool>(card, "m_isNew")) lines.Add(Str.Word("GLUE_COLLECTION_CARD_NEW").ToLowerInvariant().Trim('!'));
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            return lines;
        }

        static void Turn(PackOpeningCard card)
        {
            if (card == null || !card || card.IsRevealed() || !card.IsReady() || !card.IsRevealEnabled()) return;
            card.ForceReveal();
        }

        // ---- several packs at once ----------------------------------------------------------------

        bool Mass(PackOpeningDirector director, out string view, out string key, out string intro, out List<Item> items)
        {
            view = null; key = null; intro = null; items = null;
            if (director.IsMassPackOpeningSummaryDoneButtonShowing())
            {
                view = "summary";
                key = "summary";
                intro = Str.Word("GLUE_MASS_PACK_OPEN_SUMMARY");
                items = SummaryItems(director);
                return true;
            }
            var highlights = Ref.Get<MassPackOpeningHighlights>(director, "m_massPackOpeningHighlights");
            var cards = highlights == null ? null : highlights.GetPackOpeningCards();
            if (cards == null || cards.Count == 0 || !cards.Exists(c => c != null && c && c.gameObject.activeInHierarchy)) return false;
            if (!director.IsMassPackOpeningHighlightsContinueButtonShowing() && !director.IsMassPackOpeningHighlightsRevealButtonShowing() && !cards.Exists(c => c != null && c && c.IsReady())) return false;
            view = "highlights";
            key = "hl|" + cards[0].GetInstanceID();
            items = CardItems(cards);
            if (m_firstHighlights)
            {
                // the first page: the packs-opened count the game shows, and Highlights
                m_firstHighlights = false;
                var count = Ref.Get<UberText>(highlights, "m_numPacksOpened");
                intro = Str.Join(count == null ? null : Str.Clean(Ui.ShownText(count.Text)), Str.Word("GLUE_MASS_PACK_OPEN_HIGHLIGHTS"));
            }
            return true;
        }

        static List<Item> SummaryItems(PackOpeningDirector director)
        {
            var items = new List<Item>();
            var summary = Ref.Get<MassPackOpeningSummary>(director, "m_massPackOpeningSummary");
            var model = summary == null ? null : summary.GetDataModel();
            if (model == null) return items;
            // the legendaries by name, or (many of them) by class as the game's fallback shows
            int legendaries = model.LegendariesOpened == null ? 0 : model.LegendariesOpened.Count;
            if (legendaries > 0)
            {
                var names = new List<string>();
                foreach (var c in model.LegendariesOpened)
                {
                    var def = c == null ? null : DefLoader.Get().GetEntityDef(c.CardId);
                    if (def != null) names.Add(Edition(Str.Clean(def.GetName()), c.Premium));
                }
                AddSummary(items, A("MASS_PACK_OPENING_SUMMARY_LEGENDARY_COUNT", legendaries), Speech.HumanizeList(names));
            }
            else AddByClass(items, "MASS_PACK_OPENING_SUMMARY_LEGENDARY_COUNT", model.LegendariesOpenedFallback);
            AddByClass(items, "MASS_PACK_OPENING_SUMMARY_EPIC_COUNT", model.EpicsOpened);
            AddByClass(items, "MASS_PACK_OPENING_SUMMARY_RARE_COUNT", model.RaresOpened);
            AddByClass(items, "MASS_PACK_OPENING_SUMMARY_COMMON_COUNT", model.CommonsOpened);
            return items;
        }

        static void AddByClass(List<Item> items, string key, IEnumerable entries)
        {
            if (entries == null) return;
            int total = 0;
            var parts = new List<string>();
            foreach (var e in entries)
            {
                var c = e as Hearthstone.DataModels.ClassCardCountDataModel;
                if (c == null || c.CardCount <= 0) continue;
                total += c.CardCount;
                string cls = null;
                cls = Str.Clean(c.ClassName);
                parts.Add(A("MASS_PACK_OPENING_SUMMARY_CLASS_COUNT", c.CardCount, cls));
            }
            if (total > 0) AddSummary(items, A(key, total), Speech.HumanizeList(parts));
        }

        static void AddSummary(List<Item> items, string header, string body)
        {
            var lines = new List<string> { header, body };
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            items.Add(new Item { Lines = () => lines });
        }

        // ---- reading and keys ---------------------------------------------------------------------

        List<string> Lines()
        {
            if (m_at < 0 || m_at >= m_items.Count) return new List<string>();
            return m_items[m_at].Lines();
        }

        string First()
        {
            if (m_items.Count == 0) return Speech.S("ACCESSIBILITY_LIST_NO_ITEMS");
            m_at = Math.Max(0, Math.Min(m_at, m_items.Count - 1));
            var lines = Lines();
            return Speech.S(K.MENU_OPTION_FORMAT, lines.Count > 0 ? lines[0] : "", m_at + 1, m_items.Count);
        }

        void Move(int to)
        {
            if (m_items.Count == 0) return;
            m_at = to; m_line = 0;
            Say(First(), true);
        }

        internal override bool HandleKey()
        {
            Refresh(true);
            if (m_view == null) return false;
            int n = m_items.Count;
            var director = Director;
            if (Keys.Right.Pressed) { if (m_at + 1 < n) Move(m_at + 1); return true; }
            if (Keys.Left.Pressed) { if (m_at > 0) Move(m_at - 1); return true; }
            if (Keys.Tab.Pressed) { if (n > 0) Move((m_at + 1) % n); return true; }
            if (Keys.ShiftTab.Pressed) { if (n > 0) Move((m_at + n - 1) % n); return true; }
            if (Keys.Home.Pressed) { Move(0); return true; }
            if (Keys.End.Pressed) { Move(n - 1); return true; }
            var lines = Lines();
            if (Keys.ShiftUp.Pressed) { if (lines.Count > 0) Say(lines[Math.Min(m_line, lines.Count - 1)], true); return true; }
            if (Keys.ShiftDown.Pressed) { for (int i = m_line; i < lines.Count; i++) Say(lines[i]); m_line = Math.Max(0, lines.Count - 1); return true; }
            if (Keys.Down.Pressed) { if (m_line + 1 < lines.Count) Say(lines[++m_line], true); return true; }
            if (Keys.Up.Pressed) { if (m_line > 0) Say(lines[--m_line], true); return true; }
            var item = n > 0 && m_at < n ? m_items[m_at] : null;
            switch (m_view)
            {
                case "list":
                    if (Keys.Enter.Pressed) { if (item != null && item.Pack != null) OpenOne(item.Pack); return true; }
                    if (Keys.Space.Pressed) { if (item != null && item.Pack != null) OpenSeveral(item.Pack); return true; }
                    if (Keys.Back.Pressed) { Log.Info("packs: back"); Navigation.GoBack(); return true; }
                    return false;
                case "pack":
                    if (Keys.Enter.Pressed)
                    {
                        if (director != null && director.IsDoneButtonShown) { Log.Info("packs: done"); director.FinishPackOpen(); }
                        else if (item != null) Turn(item.Card);
                        return true;
                    }
                    if (Keys.Space.Pressed) { if (director != null && !director.IsDoneButtonShown) director.ForceRevealRandomCard(); return true; }
                    return Keys.Back.Pressed;
                case "highlights":
                    if (Keys.Enter.Pressed)
                    {
                        if (director.IsMassPackOpeningHighlightsContinueButtonShowing()) { Log.Info("packs: next page"); director.MassPackOpeningContinuePressed(); }
                        else if (item != null) Turn(item.Card);
                        return true;
                    }
                    if (Keys.Space.Pressed) { if (director.IsMassPackOpeningHighlightsRevealButtonShowing()) director.MassPackOpeningRevealAllPressed(); return true; }
                    return Keys.Back.Pressed;
                case "summary":
                    if (Keys.Enter.Pressed) { Log.Info("packs: done (several)"); director.MassPackOpeningDonePressed(); return true; }
                    return Keys.Back.Pressed;
            }
            return false;
        }

        internal override string Help()
        {
            var director = Director;
            switch (m_view)
            {
                case "list":
                    return m_items.Count == 0 ? Speech.S("ACCESSIBILITY_LIST_NO_ITEMS")
                        : Speech.S(K.MENU_HORIZONTAL_HELP_WITH_BACK_BUTTON, Keys.Enter.Name, Keys.Back.Name);
                case "pack":
                    return director != null && director.IsDoneButtonShown ? A("PRESS_KEY_TO_CONTINUE", Keys.Enter.Name) : A("SCREEN_PACK_OPENING_OPEN_CARDS_HELP", Keys.Enter.Name);
                case "highlights":
                    return director != null && director.IsMassPackOpeningHighlightsContinueButtonShowing() ? A("PRESS_KEY_TO_CONTINUE", Keys.Enter.Name)
                        : A("SCREEN_PACK_OPENING_MASS_PACK_OPENING_HELP", Keys.Enter.Name, Keys.Space.Name);
                case "summary":
                    return A("MASS_PACK_OPENING_SUMMARY_HELP", Keys.Enter.Name);
            }
            return A("GLOBAL_LOADING");
        }

        internal override void Read() { if (m_view != null) Say(First(), true); }
    }
}
