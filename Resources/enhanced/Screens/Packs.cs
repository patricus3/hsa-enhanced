#if WITHOUT_HSA
using System;
using System.Collections;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Opening packs (Open Packs on the main menu), from the game's own pack objects:
    //   the packs you have, each with its count (locked ones say why); Enter opens one the way the
    //     game's own quick-open does (Space); "open several" opens as many as the game allows at once
    //   an opened pack: its five cards in order, each said once it is turned (name, rarity, golden /
    //     diamond / signature, new); Enter turns it as a click does; Reveal all; Done
    //   several packs at once: the game's Reveal all, Continue and Done steps, and the summary counts
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
        Menu m_menu;
        string m_key, m_signature;
        float m_next;
        // an opened pack, read as cards are in a match (no menu then)
        List<PackOpeningCard> m_cards;
        PackOpeningDirector m_director;
        int m_at, m_line;

        internal PacksUI(PackOpening screen) { Screen = screen; }

        internal override bool Alive { get { return Screen != null && Screen && Packs.Screen() == Screen; } }

        PackOpeningDirector Director { get { return Ref.Get<PackOpeningDirector>(Screen, "m_director"); } }

        bool Busy { get { return Screen.m_InputBlocker != null && Screen.m_InputBlocker.activeSelf; } }

        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_menu != null;
            m_next = Time.unscaledTime + 0.4f;
            string key, title; var items = new List<GameButton>();
            var before = m_cards;
            m_cards = null;
            if (!Build(out key, out title, items)) { m_cards = before; return m_menu != null || m_cards != null; }
            if (m_cards != null)
            {
                m_menu = null;
                if (m_at >= m_cards.Count) { m_at = Math.Max(0, m_cards.Count - 1); m_line = 0; }
                if (key != m_key)
                {
                    m_key = key; m_signature = null; m_at = 0; m_line = 0;
                    Log.Info("packs: " + key + " " + m_cards.Count + " cards");
                    if (Focused) Say(Str.Join(title, First()), true);
                }
                return true;
            }
            var sig = key + "\n" + title + "\n" + string.Join("\n", items.ConvertAll(b => b.Label).ToArray());
            bool newStep = key != m_key;
            if (!newStep && sig == m_signature) return true;
            var at = m_menu == null || newStep ? 0 : m_menu.Index;
            var menu = new Menu(this, title, Back);
            foreach (var b in items) { var click = b.Click; menu.AddOption(b.Label, () => click()); }
            menu.Index = at;
            m_menu = menu;
            m_signature = sig;
            m_key = key;
            if (newStep)
            {
                Log.Info("packs: " + key + " '" + title + "': " + GameButton.Describe(items));
                if (Focused) m_menu.StartReading();
            }
            return true;
        }

        bool Build(out string key, out string title, List<GameButton> items)
        {
            key = null; title = "";
            var director = Director;
            if (director != null && director.IsMassPackOpening()) return Mass(director, ref key, ref title, items);
            var cards = Cards(director);
            if (director != null && (director.IsPlaying() || director.IsDoneButtonShown) && cards != null && cards.Count > 0)
                return Opened(director, cards, ref key, ref title, items);
            if (Busy) return false;     // an opening under way: the cards come next
            return List(ref key, ref title, items);
        }

        // ---- the packs you have -----------------------------------------------------------------

        bool List(ref string key, ref string title, List<GameButton> items)
        {
            key = "list";
            title = Str.Word("GLUE_PACK_OPENING_HEADER");
            var packs = new List<UnopenedPack>();
            foreach (var p in UnityEngine.Object.FindObjectsByType<UnopenedPack>(FindObjectsSortMode.None))
                if (p != null && p.GetCreatorPack() == null && p.GetCount() > 0) packs.Add(p);
            var order = GameUtils.GetSortedPackIds(false);
            packs.Sort((a, b) => Order(order, a.GetBoosterId()).CompareTo(Order(order, b.GetBoosterId())));
            var seen = new HashSet<int>();
            foreach (var pack in packs)
            {
                var id = pack.GetBoosterId();
                if (!seen.Add(id)) continue;
                var name = PackName(id);
                string why;
                bool can = pack.CanOpenPack(out why);
                var label = Str.Join(name, pack.GetCount().ToString(), can ? null : Str.Clean(why));
                var p = pack;
                items.Add(new GameButton { Target = pack, Label = label, Click = () =>
                {
                    string reason;
                    if (!p.CanOpenPack(out reason)) { Speech.Say(Str.Clean(reason)); return; }
                    Log.Info("packs: open " + name);
                    Ref.Set(Screen, "m_lastOpenedBoosterId", id);
                    Ref.Call(Screen, "AutomaticallyOpenPack");
                } });
                // several at once, as many as the game allows for this pack
                int limit = 0;
                try { limit = Screen.MassPackOpeningEnabled() ? Math.Min(Screen.MassPackOpeningPackLimit(id), pack.GetCount()) : 0; } catch { }
                if (can && limit >= 2)
                {
                    int n = limit;
                    var many = Str.Join(Str.Word("GLUE_PACK_OPENING_HEADER"), name, n.ToString());
                    items.Add(new GameButton { Target = pack, Label = many, Click = () =>
                    {
                        Log.Info("packs: open " + n + " of " + name);
                        Ref.Invoke(Screen, "OpenBooster", p, n);
                    } });
                }
            }
            if (items.Count == 0) return true;
            return true;
        }

        static int Order(List<int> order, int id) { var i = order == null ? -1 : order.IndexOf(id); return i < 0 ? 1 << 30 : i; }

        static string PackName(int id)
        {
            var record = GameDbf.Booster.GetRecord(id);
            if (record == null) return id.ToString();
            var name = record.Name == null ? null : Str.Clean(record.Name.GetString());
            // a pack named only by its set's code (a new set's pack): the set's name
            if (string.IsNullOrEmpty(name) || name.Length <= 4 && name.ToUpperInvariant() == name)
            {
                string set = null;
                try { if (record.CardSetId > 0) set = Str.Clean(GameStrings.GetCardSetName((TAG_CARD_SET)record.CardSetId)); } catch { }
                if (string.IsNullOrEmpty(set) && record.ShortName != null) set = Str.Clean(record.ShortName.GetString());
                if (!string.IsNullOrEmpty(set) && set != name) name = set;
            }
            return string.IsNullOrEmpty(name) ? id.ToString() : name;
        }

        // ---- one opened pack --------------------------------------------------------------------

        static List<PackOpeningCard> Cards(PackOpeningDirector director)
        {
            var hidden = director == null ? null : Ref.Get(director, "m_hiddenCards");
            return hidden == null ? null : Ref.Get<List<PackOpeningCard>>(hidden, "m_cards");
        }

        bool Opened(PackOpeningDirector director, List<PackOpeningCard> cards, ref string key, ref string title, List<GameButton> items)
        {
            key = "pack";
            title = Str.Word("GLUE_PACK_OPENING_HEADER");
            m_director = director;
            m_cards = new List<PackOpeningCard>();
            foreach (var c in cards) if (c != null && c) m_cards.Add(c);
            return true;
        }

        // a card not turned yet: a card (and the glow of its rarity the game shows); a turned one: its name
        static string Name(PackOpeningCard card)
        {
            if (!card.IsRevealed())
            {
                var hidden = card.GetEntityDef();
                return Str.Join(CombatCards.HiddenName(), hidden == null ? null : Rarity(hidden));
            }
            var lines = Lines(card);
            return lines.Count > 0 ? lines[0] : "";
        }

        static string Rarity(EntityDef def)
        {
            switch (def.GetRarity())
            {
                case TAG_RARITY.COMMON: return Str.Word("GLOBAL_RARITY_COMMON");
                case TAG_RARITY.RARE: return Str.Word("GLOBAL_RARITY_RARE");
                case TAG_RARITY.EPIC: return Str.Word("GLOBAL_RARITY_EPIC");
                case TAG_RARITY.LEGENDARY: return Str.Word("GLOBAL_RARITY_LEGENDARY");
            }
            return null;
        }

        static string Edition(string name, TAG_PREMIUM premium)
        {
            switch (premium)
            {
                case TAG_PREMIUM.GOLDEN: return Speech.S("ACCESSIBILITY_READ_COLLECTION_CARD_NAME_GOLDEN", name);
                case TAG_PREMIUM.DIAMOND: return Speech.S("ACCESSIBILITY_READ_COLLECTION_CARD_NAME_DIAMOND", name);
                case TAG_PREMIUM.SIGNATURE: return Speech.S("ACCESSIBILITY_READ_COLLECTION_CARD_NAME_SIGNATURE", name);
            }
            return name;
        }

        // a turned card's lines, as in a match: its name (edition, rarity, new) first, then cost,
        // stats, text, tribe, type, flavor
        static List<string> Lines(PackOpeningCard card)
        {
            var lines = new List<string>();
            if (!card.IsRevealed()) { lines.Add(Name(card)); return lines; }
            var merc = PackCards.MercLines(card);
            if (merc != null && merc.Count > 0) return new List<string>(merc);
            var def = card.GetEntityDef();
            if (def == null) return lines;
            lines = CombatCards.Lines(def);
            if (lines.Count == 0) lines.Add(Str.Clean(def.GetName()));
            bool isNew = Ref.Get<bool>(card, "m_isNew");
            lines[0] = Str.Join(Edition(Str.Clean(def.GetName()), card.GetPremium()), Rarity(def), isNew ? Str.Word("GLUE_COLLECTION_CARD_NEW") : null);
            return lines;
        }

        string First()
        {
            if (m_cards == null || m_cards.Count == 0) return "";
            return Speech.S(K.MENU_OPTION_FORMAT, Name(m_cards[m_at]), m_at + 1, m_cards.Count);
        }

        void Move(int to)
        {
            if (m_cards == null || m_cards.Count == 0) return;
            m_at = Math.Max(0, Math.Min(m_cards.Count - 1, to)); m_line = 0;
            Say(First(), true);
        }

        bool CardKeys()
        {
            int n = m_cards.Count;
            if (Keys.Right.Pressed || Keys.Tab.Pressed) { if (m_at + 1 < n) Move(m_at + 1); return true; }
            if (Keys.Left.Pressed || Keys.ShiftTab.Pressed) { if (m_at > 0) Move(m_at - 1); return true; }
            if (Keys.Home.Pressed) { Move(0); return true; }
            if (Keys.End.Pressed) { Move(n - 1); return true; }
            var card = n > 0 && m_at < n ? m_cards[m_at] : null;
            var lines = card == null ? new List<string>() : Lines(card);
            if (Keys.ShiftUp.Pressed) { if (lines.Count > 0) Say(lines[Math.Min(m_line, lines.Count - 1)], true); return true; }
            if (Keys.ShiftDown.Pressed) { for (int i = m_line; i < lines.Count; i++) Say(lines[i]); m_line = Math.Max(0, lines.Count - 1); return true; }
            if (Keys.Down.Pressed) { if (m_line + 1 < lines.Count) Say(lines[++m_line], true); return true; }
            if (Keys.Up.Pressed) { if (m_line > 0) Say(lines[--m_line], true); return true; }
            // Enter turns the card over (it is read then); Space turns over the rest; once all are
            // turned, Enter or Space is Done
            if (Keys.Enter.Pressed && card != null)
            {
                bool allTurned = !m_cards.Exists(c => c != null && c && !c.IsRevealed());
                if (allTurned && m_director != null && m_director.IsDoneButtonShown) { Log.Info("packs: done"); m_director.FinishPackOpen(); }
                else if (card.IsRevealed()) Say(Name(card), true);
                else { Turn(card, quiet: true); m_line = 0; Say(Name(card), true); }
                return true;
            }
            if (Keys.Space.Pressed)
            {
                bool hidden = m_cards.Exists(c => c != null && c && !c.IsRevealed());
                if (hidden)
                {
                    Log.Info("packs: reveal all");
                    foreach (var c in m_cards) if (c != null && c && !c.IsRevealed()) Turn(c, quiet: true);
                    m_line = 0;
                    Say(First(), true);
                }
                else if (m_director != null && m_director.IsDoneButtonShown) { Log.Info("packs: done"); m_director.FinishPackOpen(); }
                return true;
            }
            if (Keys.Back.Pressed) { Back(); return true; }
            return false;
        }

        static void Turn(PackOpeningCard card, bool quiet = false)
        {
            if (card.IsRevealed()) { if (!quiet) Speech.Say(Name(card)); return; }
            if (!card.IsReady() || !card.IsRevealEnabled()) return;
            card.ForceReveal();
            if (!quiet) Speech.Say(Name(card));
        }

        // ---- several packs at once --------------------------------------------------------------

        bool Mass(PackOpeningDirector director, ref string key, ref string title, List<GameButton> items)
        {
            title = Str.Word("GLUE_PACK_OPENING_HEADER");
            if (director.IsMassPackOpeningSummaryDoneButtonShowing())
            {
                key = "mass:summary";
                var summary = Ref.Get<MassPackOpeningSummary>(director, "m_massPackOpeningSummary");
                var model = summary == null ? null : summary.GetDataModel();
                if (model != null)
                {
                    AddCount(items, director, "GLOBAL_RARITY_COMMON", model.CommonsOpened);
                    AddCount(items, director, "GLOBAL_RARITY_RARE", model.RaresOpened);
                    AddCount(items, director, "GLOBAL_RARITY_EPIC", model.EpicsOpened);
                    AddCount(items, director, "GLOBAL_RARITY_LEGENDARY", model.LegendariesOpened);
                }
                items.Add(new GameButton { Target = director, Label = Str.Word("GLOBAL_DONE"), Click = () => { Log.Info("packs: done (several)"); director.MassPackOpeningDonePressed(); } });
                return true;
            }
            if (director.IsMassPackOpeningHighlightsContinueButtonShowing())
            {
                key = "mass:highlights";
                items.Add(new GameButton { Target = director, Label = Str.Word("GLOBAL_CONTINUE"), Click = () => director.MassPackOpeningContinuePressed() });
                return true;
            }
            if (director.IsMassPackOpeningHighlightsRevealButtonShowing())
            {
                key = "mass:reveal";
                items.Add(new GameButton { Target = director, Label = Str.Word("GLUE_MASS_PACK_OPEN_REVEAL_ALL"), Click = () => director.MassPackOpeningRevealAllPressed() });
                return true;
            }
            return false;
        }

        // a rarity's cards, all classes together
        // (per-class counts, or the cards themselves for legendaries)
        static void AddCount(List<GameButton> items, PackOpeningDirector director, string rarityKey, IEnumerable entries)
        {
            int count = 0;
            if (entries != null)
                foreach (var e in entries)
                {
                    var perClass = e as Hearthstone.DataModels.ClassCardCountDataModel;
                    if (perClass != null) count += perClass.CardCount; else if (e != null) count++;
                }
            if (count <= 0) return;
            var label = Str.Join(Str.Word(rarityKey), count.ToString());
            items.Add(new GameButton { Target = director, Label = label, Click = () => Speech.Say(label) });
        }

        // the game's back (it refuses while a pack is being opened)
        void Back()
        {
            Log.Info("packs: back");
            Navigation.GoBack();
        }

        internal override bool HandleKey()
        {
            if (m_cards != null && m_menu == null) return CardKeys();
            return m_menu != null && m_menu.HandleKey();
        }

        internal override string Help()
        {
            if (m_cards != null && m_menu == null)
                return Str.Join(Speech.S("ACCESSIBILITY_SCREEN_PACK_OPENING_MASS_PACK_OPENING_HELP", Keys.Enter.Name, Keys.Space.Name), Keys.Space.Name, Str.Word("GLOBAL_DONE"));
            return m_menu == null ? "" : m_menu.Help();
        }

        internal override void Read()
        {
            if (m_cards != null && m_menu == null) { Say(First(), true); return; }
            if (m_menu != null) m_menu.StartReading();
        }
    }
}
#endif
