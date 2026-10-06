using System;
using System.Collections;
using System.Collections.Generic;
using HSAEnhanced.Core;
using PegasusShared;
using UnityEngine;

namespace HSAEnhanced
{
    // The Arena (normal and the Underground), from the game's draft data:
    //   the landing page: the mode, wins and losses of the run against its limits, tickets, the run's
    //     hero; Start (it spends tickets: Enter twice), Switch Modes, Play, Retire (the game asks), the
    //     deck and its mana curve, the rewards of a finished run
    //   drafting: the phase and the pick number, the three choices with their text (Enter takes one:
    //     a hero is chosen and confirmed as the game's two steps do; a Legendary group says what comes
    //     with it); the deck so far
    //   the deck after a redraft: the count against the deck size, every card of the deck (Enter
    //     moves it out to the redraft cards), the redraft cards (Enter puts one in the deck), Revert,
    //     Done (the game says when the count is not right)
    static class Arena
    {
        static ArenaUI s_ui;

        internal static bool InArena
        {
            get
            {
                var scenes = SceneMgr.Get();
                if (scenes == null || scenes.GetMode() != SceneMgr.Mode.DRAFT || scenes.IsTransitioning() || !scenes.IsSceneLoaded() || GameState.Get() != null) return false;
                var landing = ArenaLandingPageManager.Get();
                var display = DraftDisplay.Get();
                return landing != null && landing.IsLoadingComplete() && display != null && display.IsLoadingComplete() && DraftManager.Get() != null;
            }
        }

        // every frame
        internal static void Tick()
        {
            if (s_ui != null && !s_ui.Alive) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && InArena)
            {
                var ui = new ArenaUI();
                if (!ui.Refresh(true)) return;
                s_ui = ui;
                Generic.Yield();
                Focus.PushBase(ui);
                if (ui.Focused) ui.Read();
            }
            if (s_ui != null) s_ui.Refresh(false);
        }
    }

    class ArenaUI : Core.Screen
    {
        Menu m_menu;
        string m_key, m_signature;
        float m_next, m_armedUntil;

        internal override bool Alive { get { return Arena.InArena; } }

        static DraftManager DM { get { return DraftManager.Get(); } }

        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_menu != null;
            m_next = Time.unscaledTime + 0.4f;
            string key, title; var items = new List<GameButton>();
            if (!Build(out key, out title, items)) return m_menu != null;
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
                Log.Info("arena: " + key + " '" + title + "': " + GameButton.Describe(items));
                if (Focused) m_menu.StartReading();
            }
            return true;
        }

        bool Build(out string key, out string title, List<GameButton> items)
        {
            key = null; title = "";
            var display = DraftDisplay.Get();
            var state = DM.GetArenaState();
            bool drafting = state == ArenaSessionState.DRAFTING || state == ArenaSessionState.REDRAFTING;
            if (drafting && DM.IsClientStateInAnyDrafting())
            {
                // the first-time banner holds the choices back until it is closed: it is said with them
                if (Ref.Get<bool>(display, "m_phaseMessageShowing")) display.HandleArenaDraftScreenEvent("PHASE_POPUP_CLOSED");
                if (display.GetCardVisuals() == null || !display.DraftAnimationIsComplete() || display.IsHeroAnimating()) return false;
                return Draft(display, ref key, ref title, items);
            }
            if (state == ArenaSessionState.EDITING_DECK) return EditDeck(display, ref key, ref title, items);
            return Landing(display, state, ref key, ref title, items);
        }

        static string ModeName() { return Str.Word(DM.IsUnderground() ? "GLUE_ARENA_LANDING_TITLE_UG" : "GLOBAL_ARENA"); }

        // ---- the landing page -----------------------------------------------------------------

        bool Landing(DraftDisplay display, ArenaSessionState state, ref string key, ref string title, List<GameButton> items)
        {
            key = "landing:" + DM.IsUnderground() + ":" + state;
            var tickets = DM.GetNumTicketsOwned();
            title = Str.Join(ModeName(), Str.Game("GLUE_ARENA_TICKET_COUNT", tickets) ?? tickets.ToString());
            var anchor = (Component)display;
            bool run = DM.HasActiveRun;
            if (run && DM.CanShowWinsLosses)
            {
                bool ug = DM.IsUnderground();
                var line = Str.Join(Str.Word("GLUE_WINS"), DM.GetCurrentWins(ug) + "/" + DM.GetMaxWins(ug), Str.Word("GLUE_LOSSES"), DM.GetCurrentLosses(ug) + "/" + DM.GetMaxLosses(ug));
                Add(items, anchor, line, null);
                var deck = DM.GetDraftDeck();
                if (deck != null && !string.IsNullOrEmpty(deck.HeroCardID)) Add(items, anchor, Card(deck.HeroCardID), null);
            }
            // the rewards of a finished run
            if (DM.IsRewardChestReady())
            {
                var rewards = new List<string>();
                var list = DM.GetRewards();
                if (list != null) foreach (var r in list) foreach (var s in RewardText.Describe(r)) rewards.Add(s);
                Add(items, anchor, Str.Join(rewards.ToArray()), () => { Log.Info("arena: claim"); display.HandleArenaDraftScreenEvent("REWARDCHEST_CLAIMED"); });
                return true;
            }
            if (!run)
            {
                // start: spends tickets (1, the Underground 2), so Enter twice
                int cost = DM.IsUnderground() ? 2 : 1;
                var label = Str.Join(Str.Word("GLOBAL_PLAY"), Str.Game("GLUE_ARENA_TICKET_COUNT", cost) ?? cost.ToString());
                Add(items, anchor, label, () =>
                {
                    if (Time.unscaledTime > m_armedUntil)
                    {
                        m_armedUntil = Time.unscaledTime + 6f;
                        Speech.Say(Str.Join(label, Str.Word("GLOBAL_CONFIRM"), Keys.Enter.Name));
                        return;
                    }
                    m_armedUntil = 0;
                    Log.Info("arena: start a run");
                    ArenaLandingPageManager.Get().HandleArenaLandingPageEvent("SPEND_TICKET");
                });
                Add(items, anchor, Str.Game("GLUE_ARENA_NORUN_BODY", DM.GetMaxWins(), DM.GetMaxLosses()), null);
            }
            else
            {
                var play = display.m_playButtonWidget == null ? null : display.m_playButtonWidget.GetComponentInChildren<PlayButton>();
                if (play != null && play.IsEnabled())
                {
                    var label = Ui.LabelOf(play);
                    Add(items, play, label.Length > 0 ? label : Str.Word("GLOBAL_PLAY"), () => { Log.Info("arena: play"); play.TriggerRelease(); });
                }
                Add(items, anchor, Str.Word("GLUE_ARENA_RETIRE_POPUP_YES"), () => { Log.Info("arena: retire"); display.HandleArenaDraftScreenEvent("RETIRE_PRESSED"); });
                DeckItems(items, anchor);
            }
            if (GameModeUtils.HasUnlockedMode(Assets.Global.UnlockableGameMode.UNDERGROUND_ARENA))
                Add(items, anchor, Str.Word("GLUE_ARENA_SWITCHMODES_BTN"), () => { Log.Info("arena: switch modes"); ArenaLandingPageManager.Get().HandleArenaLandingPageEvent("CODE_ARENA_TOGGLE_CLICKED"); });
            return true;
        }

        // ---- drafting ---------------------------------------------------------------------------

        bool Draft(DraftDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            var choices = Ref.Get(display, "m_choices") as IList;
            if (choices == null || choices.Count == 0) return false;
            var slot = DM.GetSlotType();
            bool redraft = DM.GetArenaState() == ArenaSessionState.REDRAFTING;
            string phase = slot == DraftSlotType.DRAFT_SLOT_HERO ? Str.Word("GLUE_ARENA_PHASE_HERO")
                         : slot == DraftSlotType.DRAFT_SLOT_HERO_POWER ? Str.Word("GLUE_ARENA_PHASE_HERO_POWER")
                         : redraft ? Str.Game("GLUE_ARENA_DRAFT_REDRAFT_DETAIL", DM.GetRedraftSlot() + 1, DM.GetMaxRedraftSlot() + 1)
                         : Str.Join(Str.Word("GLUE_ARENA_PHASE_DRAFT"), (DM.GetSlot()) + "/" + (DM.GetMaxSlot()));
            key = "draft:" + slot + ":" + (redraft ? DM.GetRedraftSlot() : DM.GetSlot());
            title = Str.Join(ModeName(), phase);
            var anchor = (Component)display;
            for (int i = 0; i < choices.Count; i++)
            {
                var c = choices[i];
                var id = Ref.Get<string>(c, "m_cardID");
                if (string.IsNullOrEmpty(id)) continue;
                var premium = Ref.Get<TAG_PREMIUM>(c, "m_premium");
                var package = Ref.Get<List<string>>(c, "m_packageCardIds");
                var label = Card(id);
                if (package != null && package.Count > 0)
                {
                    var names = new List<string>();
                    foreach (var p in package) names.Add(Name(p));
                    label = Str.Join(label, string.Join(", ", names.ToArray()));
                }
                int n = i + 1;
                bool valid = display.IsChoiceValid(n);
                if (!valid) label = Str.Join(label, Str.Unavailable);
                Add(items, anchor, label, () => Pick(display, slot, n, premium, package != null && package.Count > 0, valid));
            }
            DeckItems(items, anchor);
            return true;
        }

        void Pick(DraftDisplay display, DraftSlotType slot, int n, TAG_PREMIUM premium, bool package, bool valid)
        {
            if (!valid) { Speech.Say(Str.Unavailable); return; }
            Log.Info("arena: pick " + n + " (" + slot + ")");
            if (slot == DraftSlotType.DRAFT_SLOT_HERO || slot == DraftSlotType.DRAFT_SLOT_HERO_POWER)
            {
                // as the game's two steps: the hero is looked at, then confirmed
                display.OnHeroClicked(n);
                Jobs.Run(After(0.6f, () => display.ClickConfirmButton()));
                return;
            }
            DM.MakeChoice(n, premium);
            // a Legendary group opens its tray first: confirmed as the tray's button does
            if (package) Jobs.Run(After(0.6f, () => Ref.Call(display, "ConfirmPackageChoice")));
        }

        static IEnumerator After(float seconds, Action action)
        {
            yield return new WaitForSecondsRealtime(seconds);
            try { action(); } catch (Exception e) { Log.Error(e); }
        }

        // ---- the deck after a redraft -----------------------------------------------------------

        bool EditDeck(DraftDisplay display, ref string key, ref string title, List<GameButton> items)
        {
            var deck = DM.GetDraftDeck();
            var slots = Ref.Get(display, "m_redraftCardSlots") as IList;
            var widgets = display.m_redraftCardSlotWidgets;
            if (deck == null || slots == null || widgets == null) return false;
            int count = Ref.Get<int>(display, "m_deckCount"), max = Ref.Get<int>(display, "m_maxDeckCount");
            if (max <= 0) { count = deck.GetTotalCardCount(); max = deck.GetMaxCardCount(); }
            key = "edit:" + DM.IsUnderground();
            title = Str.Join(ModeName(), Str.Word("GLUE_EDIT_DECK"), Str.Game("GLUE_DECK_TRAY_COUNT", count, max) ?? count + "/" + max);
            var anchor = (Component)display;
            foreach (var s in deck.GetSlots())
            {
                var def = s == null ? null : s.GetEntityDef();
                if (def == null) continue;
                var id = s.CardID;
                var premium = s.PreferredPremium;
                bool fixedCard = def.HasTag(GAME_TAG.CAN_NOT_BE_REDRAFTED);
                var label = Str.Join(Str.Clean(def.GetName()), s.Count > 1 ? "x" + s.Count : null, def.GetCost().ToString(),
                    DM.IsRedraftCard(id, premium) ? Str.Word("GLUE_COLLECTION_CARD_NEW") : null,
                    GameUtils.IsBannedByArenaDenylist(id) ? Str.Unavailable : null, fixedCard ? Str.Locked : null);
                Add(items, anchor, label, () =>
                {
                    if (fixedCard) { Speech.Say(Str.Locked); return; }
                    Log.Info("arena: out of the deck " + id);
                    if (!display.RemoveCardFromArenaDeck(id, premium)) Speech.Say(Str.Unavailable);
                });
            }
            // the redraft cards waiting outside the deck
            foreach (var o in slots)
            {
                var slot = o as RedraftCardSlot;
                if (slot == null || !slot.HasCard || slot.Index < 0 || slot.Index >= widgets.Count || widgets[slot.Index] == null) continue;
                var model = widgets[slot.Index].GetDataModel<Hearthstone.DataModels.EventDataModel>();
                var card = model == null ? null : model.Payload as Hearthstone.DataModels.CardDataModel;
                if (card == null) continue;
                var label = Str.Join(Card(card.CardId), slot.IsNew ? Str.Word("GLUE_COLLECTION_CARD_NEW") : null, GameUtils.IsBannedByArenaDenylist(card.CardId) ? Str.Unavailable : null);
                var sl = slot;
                Add(items, anchor, Str.Join(Str.Word("GLUE_ARENA_DRAFT_REDRAFT_HEADER"), label), () =>
                {
                    Log.Info("arena: into the deck " + card.CardId);
                    if (!display.AddCardToArenaDeck(sl)) Speech.Say(Str.Unavailable);
                });
            }
            Add(items, anchor, Str.Word("GLUE_ARENA_REVERT_BUTTON"), () => { Log.Info("arena: revert"); display.HandleArenaDraftScreenEvent("REVERT_CHANGES_PRESSED"); });
            Add(items, anchor, Str.Word("GLOBAL_DONE"), () => { Log.Info("arena: deck edit done"); display.HandleArenaDraftScreenEvent("EDITDECK_PRESSED"); });
            return true;
        }

        // ---- the deck ---------------------------------------------------------------------------

        static void DeckItems(List<GameButton> items, Component anchor)
        {
            var deck = DM.GetDraftDeck();
            if (deck == null) return;
            var slots = deck.GetSlots();
            if (slots == null || slots.Count == 0) return;
            var curve = new int[8];
            var lines = new List<string>();
            foreach (var s in slots)
            {
                if (s == null) continue;
                var def = DefLoader.Get().GetEntityDef(s.CardID);
                if (def == null) continue;
                curve[Math.Max(0, Math.Min(7, def.GetCost()))] += s.Count;
                lines.Add(Str.Join(Str.Clean(def.GetName()), s.Count > 1 ? "x" + s.Count : null, def.GetCost().ToString()));
            }
            Add(items, anchor, Str.Join(Str.Word("GLUE_ARENA_DECK_TITLE"), deck.GetTotalCardCount().ToString(), string.Join("; ", lines.ToArray())), null);
            var bars = new List<string>();
            for (int i = 0; i < curve.Length; i++) if (curve[i] > 0) bars.Add((i == 7 ? "7+" : i.ToString()) + ": " + curve[i]);
            Add(items, anchor, Str.Join(Str.Word("GLUE_FORGE_MANATIP_HEADER"), string.Join(", ", bars.ToArray())), null);
        }

        static string Name(string cardId)
        {
            var def = DefLoader.Get().GetEntityDef(cardId);
            return def == null ? cardId : Str.Clean(def.GetName());
        }

        // a card as the choice shows it: name, cost, attack / health, its text
        static string Card(string cardId)
        {
            var def = DefLoader.Get().GetEntityDef(cardId);
            if (def == null) return cardId;
            string text = null;
            try { text = def.GetCardTextInHand(); } catch { }
            string stats = def.IsMinion() ? def.GetATK() + "/" + def.GetHealth() : def.IsWeapon() ? def.GetATK() + "/" + def.GetHealth() : null;
            return Str.Join(Str.Clean(def.GetName()), def.IsHero() ? null : def.GetCost().ToString(), stats, Str.Clean(text));
        }

        static void Add(List<GameButton> items, Component anchor, string label, Action click)
        {
            if (string.IsNullOrEmpty(label)) return;
            var said = label;
            items.Add(new GameButton { Target = anchor, Label = label, Click = click ?? (() => Speech.Say(said)) });
        }

        // the game's own back (a looked-at hero first, then the main menu)
        void Back()
        {
            Log.Info("arena: back");
            Navigation.GoBack();
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
