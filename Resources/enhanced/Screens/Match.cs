using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Without Hearthstone Access: the match screen, acting as Hearthstone Access did
    // (docs/combat-spec-2-actions.md): with the game's own mouse handling. Our virtual mouse rests every
    // frame on the card looked at (a hand card on its mana gem), Enter is a left click there and
    // Backspace a right click, so the game does exactly what a sighted player's clicks do (picking a card
    // up, the minions making room, the arrows, the sounds, its errors). Navigation and card reading are
    // Combat's (part 1).
    // - Enter on a card: a click on it. A hand card is picked up: a minion or location then asks where
    //   (Left/Right, Home/End, numbers; Ctrl+Up/Down for a card that can go to the opponent's side), the
    //   mouse goes over that place and Enter drops it there; another card asks "Play card?" (unless it
    //   needs a target), Enter plays it. A card in play: attack, hero power, location as clicked;
    // - target mode: "Choose a target", Tab / zone keys, Enter clicks the target, Backspace cancels;
    // - Choose One and Discover-type choices: a list (Left/Right, Home/End, Tab), Enter chooses;
    //   Choose One can be cancelled with Backspace; Tab hides / shows a Discover;
    // - mulligan: the starting cards, Space marks (the game's own state is read back), Enter confirms;
    // - E ends the turn (asking first while plays are left, as the setting says), Shift+E at once;
    //   Ctrl+F: the focused minion attacks the enemy hero; Shift+F: every minion that can;
    // - T trades / forges / prepares the focused hand card;
    // - game over: the result; Enter continues on the end screen.
    static class Match
    {
        static MatchUI s_ui;

        internal static Core.Screen Screen { get { return s_ui; } }

        internal static bool InMatch
        {
            get
            {
                var gs = GameState.Get();
                return gs != null && SceneMgr.Get() != null && SceneMgr.Get().GetMode() == SceneMgr.Mode.GAMEPLAY;
            }
        }

        // every frame
        internal static void Tick()
        {
            if (s_ui != null && !s_ui.Alive) s_ui = null;
            if (s_ui == null && InMatch)
            {
                s_ui = new MatchUI();
                Generic.Yield();
                Focus.PushBase(s_ui);
                Log.Info("match: ours");
            }
            if (s_ui != null) s_ui.Tick();
        }

        // the game's error line (not enough mana, ...): spoken
        internal static void GameError(string message)
        {
            if (s_ui != null && !string.IsNullOrEmpty(message)) s_ui.Say(message);
        }
    }

    class MatchUI : Core.Screen
    {
        enum Mode { Browse, Place, Playing, DeckAction, Reverse, Choices, Mulligan, Assemble }

        Mode m_mode;
        Entity m_placing;
        bool m_opposingSide;
        int m_slot;
        List<Card> m_list = new List<Card>();
        int m_at;
        int m_line;
        bool m_confirmEndTurn;
        GameState.ResponseMode? m_lastResponse;
        bool m_mulliganSeen, m_gameOverSaid, m_choicesShown, m_loggedChoiceMode, m_loggedShown;
        bool m_mulliganListening;     // the starting hand is being chosen (after Enter the mouse is hidden)
        object m_choiceKey;
        AssembleUIManager m_assembleManager;
        AssembleChoiceParams[] m_assembleChoiceData;
        readonly List<AssemblePartChoice> m_assembleChoices = new List<AssemblePartChoice>();
        int m_assembleAt, m_assembleLine, m_assembleTargetLine;
        bool m_assembleViewingTarget;
        string m_assembleTop, m_assembleBottom, m_assembleTargetName;

        sealed class AssemblePartChoice
        {
            internal string Name, Top, Bottom;
            internal UIBButton TopButton, BottomButton;
        }

        internal override bool Alive { get { return Match.InMatch; } }

        static GameState GS { get { return GameState.Get(); } }

        #region Every frame: what the game is doing
        internal void Tick()
        {
            var gs = GS;
            if (gs == null) return;
            if (gs.IsGameOver()) { GameOver(); return; }

            var assemble = AssembleUIManager.Get();
            if (assemble != null && Ref.Get<bool>(assemble, "m_isShowingAssembleUI"))
            {
                var choices = Ref.Get<AssembleChoiceParams[]>(assemble, "m_assembleCardProperties");
                if (m_mode != Mode.Assemble || !ReferenceEquals(m_assembleManager, assemble) || !ReferenceEquals(m_assembleChoiceData, choices))
                    BeginAssemble(assemble, choices);
                return;
            }
            if (m_mode == Mode.Assemble)
            {
                m_mode = Mode.Browse;
                m_assembleManager = null;
                m_assembleChoiceData = null;
                m_assembleChoices.Clear();
                m_assembleViewingTarget = false;
            }

            // the mulligan
            if (gs.IsMulliganPhase() && MulliganCards() != null && !ChoiceCardMgr.Get().IsFriendlyShown())
            {
                if (!m_mulliganSeen && MulliganReady())
                {
                    m_mulliganSeen = true;
                    m_mulliganListening = true;
                    m_mode = Mode.Mulligan;
                    m_list = new List<Card>(MulliganCards());
                    m_at = 0; m_line = 0;
                    Say(Str.Join(Str.Word("GAMEPLAY_MULLIGAN_STARTING_HAND"), Str.Word("GAMEPLAY_MULLIGAN_SUBTITLE")));
                    ReadListItem();
                }
                UpdateMouse(gs);
                return;
            }
            if (m_mode == Mode.Mulligan) { m_mode = Mode.Browse; m_list.Clear(); }

            // Choose One / Discover: a list of the cards the game shows
            var cards = ChoiceCardMgr.Get() == null ? null : ChoiceCardMgr.Get().GetFriendlyCards();
            // a Rewind choice is two buttons (Rewind / Keep) instead of cards on show: a choice all the same
            bool shown = cards != null && cards.Count > 0 && (ChoiceCardMgr.Get().IsFriendlyShown() || RewindUIManager.IsShowingRewindUI);
            // a Choose One option that needs a target (Wrath...): the game keeps its choice until the
            // target is chosen, but the cards are gone and the target is what is asked
            var targeting = gs.GetResponseMode() == GameState.ResponseMode.OPTION_TARGET || gs.GetResponseMode() == GameState.ResponseMode.OPTION_REVERSE_TARGET;
            if (shown && targeting && ChoiceCardMgr.Get().HasSubOption()) shown = false;
            // for bug reports: every choice the game asks for, and whether its cards are on show
            bool choiceMode = gs.IsInChoiceMode();
            if (choiceMode != m_loggedChoiceMode || shown != m_loggedShown)
            {
                m_loggedChoiceMode = choiceMode; m_loggedShown = shown;
                Log.Info("match: choice mode " + choiceMode + ", cards " + (cards == null ? 0 : cards.Count) + ", shown " + shown);
            }
            if (shown && (!m_choicesShown || !Equals(m_choiceKey, cards[0])))
            {
                m_choicesShown = true;
                m_choiceKey = cards[0];
                m_mode = Mode.Choices;
                m_list = new List<Card>(cards);
                m_at = 0; m_line = 0;
                Say(RewindUIManager.IsShowingRewindUI ? Speech.S("ACCESSIBILITY_GAMEPLAY_QUERY_REWIND") : gs.IsInSubOptionMode() ? Str.Word("GAMEPLAY_CHOOSE_ONE") : ChoiceTitle());
                bool reroll = AddReroll();
                ReadListItem();
                if (reroll) SayRerollHelp();
            }
            else if (shown && m_mode == Mode.Choices && AddReroll()) SayRerollHelp();
            else if (!shown && m_choicesShown)
            {
                m_choicesShown = false;
                m_choiceKey = null;
                if (m_mode == Mode.Choices) m_mode = Mode.Browse;
                if (gs.IsInChoiceMode() && !gs.IsInSubOptionMode()) Say(Speech.S(K.GAMEPLAY_CHOICES_HIDDEN));
            }

            // a target to choose (an attack, a battlecry, a spell)
            var response = gs.GetResponseMode();
            if (response != m_lastResponse)
            {
                // not while Shift+F / Ctrl+F attack the hero: the mod picks the target itself
                if ((response == GameState.ResponseMode.OPTION_TARGET || response == GameState.ResponseMode.OPTION_REVERSE_TARGET) && m_mode != Mode.Choices && !m_attackingFace)
                {
                    Say(Speech.S(K.GAMEPLAY_CHOOSE_TARGET));
                    // the hand card just played does not stay looked at (the mouse stays where it is)
                    var looked = Combat.FocusedCard;
                    if (looked != null && looked.GetEntity() != null && looked.GetEntity().GetZone() == TAG_ZONE.HAND) Combat.DropFocus();
                }
                m_lastResponse = response;
            }
            HeldCard(gs);
            m_prevFrameResponse = gs.GetResponseMode();
            if (m_confirmEndTurn && !gs.IsInMainOptionMode()) m_confirmEndTurn = false;
            UpdateMouse(gs);
        }

        // A card picked up by the click (the game's held card) decides what follows, as in Hearthstone
        // Access: a minion or location asks where ("Summon at the right?"), anything else "Play card?" unless
        // it needs a target (then the game asks for it while the card is held over the board)
        void HeldCard(GameState gs)
        {
            var im = InputManager.Get();
            var held = im == null ? null : im.GetHeldCard();
            var e = held == null ? null : held.GetEntity();
            if (e == null)
            {
                m_deckPending = false;
                if (Holding) { m_mode = Mode.Browse; m_placing = null; }
                return;
            }
            if (m_placing == e && Holding) return;
            m_placing = e;
            m_opposingSide = false;
            // a hand card picked up while a board object waits for one: dropped onto that object
            if (m_prevFrameResponse == GameState.ResponseMode.OPTION_REVERSE_TARGET)
            {
                m_mode = Mode.Reverse;
                Log.Info("match: holding " + e.GetName() + " for the board object");
                Say(Speech.S(K.TUTORIAL_HOGGER_2_5, Keys.Enter.Name, Keys.Back.Name));
                return;
            }
            // T picked it up: the deck action (its question was said already)
            if (m_deckPending && DeckAllowed(e))
            {
                m_deckPending = false;
                m_mode = Mode.DeckAction;
                Log.Info("match: holding " + e.GetName() + " for the deck action");
                return;
            }
            m_deckPending = false;
            if (e.IsMinion() || e.IsLocation())
            {
                m_mode = Mode.Place;
                var zone = Zone(false);
                m_slot = zone == null ? 1 : zone.GetCards().Count + 1;
                Log.Info("match: holding " + e.GetName() + ", where?");
                SayPlace();
            }
            else
            {
                m_mode = Mode.Playing;
                Log.Info("match: holding " + e.GetName());
                if (!gs.EntityHasTargets(e)) Say(Speech.S(K.GAMEPLAY_QUERY_PLAY_CARD));
            }
        }

        bool m_mouseOwned;        // Shift+F / Ctrl+F move the mouse themselves
        bool m_deckPending;       // T clicked a hand card: once held it goes to the deck action area
        GameState.ResponseMode? m_prevFrameResponse;

        bool Holding { get { return m_mode == Mode.Place || m_mode == Mode.Playing || m_mode == Mode.DeckAction || m_mode == Mode.Reverse; } }

        // Every frame, as Hearthstone Access: a held card's mouse is the placement's (or over the board);
        // otherwise the mouse rests on the card looked at (in a list, the list's card); with nothing looked
        // at it is hidden. While a target is chosen it is never hidden or put on a hand card the option
        // cannot target: off the board the game would cancel the targeting.
        void UpdateMouse(GameState gs)
        {
            if (!VirtualMouse.Pointing || VirtualMouse.Clicking || m_mouseOwned) return;
            try
            {
                var im = InputManager.Get();
                var held = im == null ? null : im.GetHeldCard();
                bool targeting = gs.IsInTargetMode();
                if (held != null && m_mode == Mode.Reverse) { ReverseMouse(gs); return; }
                if (held != null && m_mode == Mode.DeckAction) { DeckMouse(); return; }
                if (held != null && !targeting)
                {
                    if (m_mode == Mode.Place) PlaceMouse();
                    else BoardMouse();
                    return;
                }
                if ((m_mode == Mode.Mulligan || m_mode == Mode.Choices) && m_at >= 0 && m_at < m_list.Count)
                {
                    if (m_mode == Mode.Mulligan && !m_mulliganListening) { VirtualMouse.Hide(); return; }
                    PointCard(m_list[m_at]);
                    return;
                }
                var card = Combat.FocusedCard;
                if (card != null && CanPoint(card, gs)) { PointCard(card); return; }
                if (!targeting && held == null) VirtualMouse.Hide();
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        static bool CanPoint(Card card, GameState gs)
        {
            var e = card.GetEntity();
            if (e == null) return false;
            if (gs.IsInTargetMode() && e.GetZone() == TAG_ZONE.HAND && !gs.DoesSelectedOptionHaveHandTarget()) return false;
            return true;
        }

        // On the card: a hand card on its mana gem (attack, then health gem if it has none), and only when it
        // is not already under the mouse (hovering raises it, re-aiming would make it flicker); else the card
        static void PointCard(Card card)
        {
            if (card == null || card.gameObject == null) return;
            var e = card.GetEntity();
            if (e != null && e.GetZone() == TAG_ZONE.HAND && card.GetZone() is ZoneHand)
            {
                if (card.IsMousedOver()) return;
                var actor = card.GetActor();
                var gem = actor == null ? null : (actor.m_manaObject ?? actor.m_attackObject ?? actor.m_healthObject);
                VirtualMouse.PointAt(gem != null ? gem : card.gameObject);
                return;
            }
            VirtualMouse.PointAt(card.gameObject);
        }

        // the deck action: the held card over the board's deck action area (the game trades, forges or
        // prepares a card dropped there)
        void DeckMouse()
        {
            var board = Board.Get();
            var area = board == null ? null : board.FindCollider("DeckActionArea");
            if (area != null) VirtualMouse.PointAtWorld(area.bounds.center);
        }

        // a hand card for a board object: held over the object
        void ReverseMouse(GameState gs)
        {
            var target = gs.GetInteractableObject();
            if (target != null) VirtualMouse.PointAt(target.gameObject);
        }

        static bool DeckAllowed(Entity e) { return e != null && e.HasDeckAction() && !e.IsPassable(); }

        static string DeckQuestion(Entity e)
        {
            if (e.IsForgeable()) return Speech.S(K.GAMEPLAY_QUERY_FORGE_CARD);
            if (e.IsPrepareable()) return Speech.S(K.GAMEPLAY_QUERY_PREPARE_CARD);
            return Speech.S(K.GAMEPLAY_QUERY_TRADE_CARD);
        }

        // Enter confirms (a click: the drop), Backspace cancels; nothing else in these states
        bool ConfirmKeys()
        {
            if (Keys.Enter.Pressed) { Log.Info("match: confirm"); VirtualMouse.ClickHere(0); return true; }
            if (Keys.Back.Pressed) { RightClick(); return true; }
            return true;
        }

        // a held card that is not placed: over the friendly side of the board once the mouse is below the
        // friendly hero (on the first frame it is still on the card in the hand), as Hearthstone Access did
        void BoardMouse()
        {
            var hero = GS.GetFriendlySidePlayer() == null ? null : GS.GetFriendlySidePlayer().GetHeroCard();
            var cam = Camera.main;
            var zone = Zone(false);
            if (hero == null || cam == null || zone == null) return;
            if (VirtualMouse.Position.y >= cam.WorldToScreenPoint(hero.transform.position).y) return;
            var col = zone.GetComponent<Collider>();
            VirtualMouse.PointAtWorld(col != null ? col.bounds.center : zone.transform.position);
        }

        // the held minion over the chosen place (Hearthstone Access's points): an empty side its middle; the
        // right end the zone's right edge, the left end its left edge; between two minions halfway between
        // them. The game turns that into the slot (InputManager.PlayZoneSlotMousedOver).
        void PlaceMouse()
        {
            var zone = Zone(m_opposingSide);
            if (zone == null) return;
            var cards = zone.GetCards().FindAll(c => c != null && !c.IsBeingDragged);
            var col = zone.GetComponent<Collider>();
            var center = col != null ? col.bounds.center : zone.transform.position;
            var extent = col != null ? col.bounds.extents.x : 0f;
            int last = cards.Count + 1;
            Vector3 world = center;
            if (cards.Count == 0) world = center;
            else if (m_slot >= last) world.x = center.x + extent;
            else if (m_slot <= 1) world.x = center.x - extent;
            else world = (cards[m_slot - 2].transform.position + cards[m_slot - 1].transform.position) / 2f;
            VirtualMouse.PointAtWorld(world);
        }

        void GameOver()
        {
            if (m_gameOverSaid) { return; }
            m_gameOverSaid = true;
            var me = GS.GetFriendlySidePlayer();
            var state = me == null ? TAG_PLAYSTATE.INVALID : (TAG_PLAYSTATE)me.GetTag(GAME_TAG.PLAYSTATE);
            string key = state == TAG_PLAYSTATE.WON ? K.GAMEPLAY_GAME_OVER_WON
                       : state == TAG_PLAYSTATE.LOST || state == TAG_PLAYSTATE.CONCEDED ? K.GAMEPLAY_GAME_OVER_LOST
                       : state == TAG_PLAYSTATE.TIED ? K.GAMEPLAY_GAME_OVER_TIED : K.GAMEPLAY_GAME_OVER_GENERIC;
            Log.Info("match: game over " + state);
            Say(Speech.S(key));
        }
        #endregion

        #region Keys
        internal override bool HandleKey()
        {
            if (!Input.anyKeyDown) return false;
            var gs = GS;
            if (gs == null) return false;
            VirtualMouse.TakeOver();
            if (Bind.SEE_HISTORY.Pressed) { HistoryLog.Open(); return true; }
            // I: the keywords of the card looked at (a choice or mulligan card in those lists)
            if (Bind.READ_TOOLTIP.Pressed && m_mode != Mode.Assemble)
            {
                var looked = (m_mode == Mode.Choices || m_mode == Mode.Mulligan) && m_at >= 0 && m_at < m_list.Count ? m_list[m_at] : Combat.FocusedCard;
                var e = looked == null ? null : looked.GetEntity();
                if (e != null) CombatCards.SayKeywords(e);
                return true;
            }

            // the end screen: Enter goes on
            var end = EndGameScreen.Get();
            if (gs.IsGameOver() || (end != null && end.m_hitbox != null && end.m_hitbox.gameObject.activeInHierarchy))
            {
                if (Keys.Enter.Pressed && end != null && end.m_hitbox != null && end.m_hitbox.gameObject.activeInHierarchy) { Log.Info("match: continue"); Core.Click.Peg(end.m_hitbox); return true; }
                return Combat.StatusKeys(this);
            }
            if (m_mode == Mode.Assemble) return AssembleKeys();
            if (m_mode == Mode.Mulligan) return MulliganKeys();
            // Z: the pet and the board's clickable decorations
            if (Core.Key.Of(KeyCode.Z).Pressed) { BoardObjects.Open(); return true; }
            if (m_mode == Mode.Choices && ChoiceKeys()) return true;
            if (m_mode == Mode.DeckAction || m_mode == Mode.Reverse) return ConfirmKeys();
            if (m_mode == Mode.Place) return PlaceKeys();
            if (m_mode == Mode.Playing && !gs.IsInTargetMode()) return PlayKeys();

            // E in the opponent's turn (also right after yours ended, while the game still finishes it):
            // "Opponent's turn", never the end-turn question
            if ((Bind.END_TURN.Pressed || Bind.FORCE_END_TURN.Pressed) && OpponentsTurn(gs))
            {
                m_confirmEndTurn = false;
                Say(Speech.S(K.GAMEPLAY_OPPONENT_TURN));
                return true;
            }

            // the end-turn question: Enter or E ends the turn, any other key cancels it (and does nothing else)
            if (m_confirmEndTurn)
            {
                m_confirmEndTurn = false;
                if (Keys.Enter.Pressed || Bind.END_TURN.Pressed) { EndTurn(); return true; }
                return true;
            }

            // before the mulligan is over (the versus intro, the starting hand, waiting for the opponent) the
            // board is not in play yet: the zone keys (V, F, ...) and moving through cards wait for it
            if ((!gs.IsPastBeginPhase() || gs.IsMulliganManagerActive()) && (ZoneKey() || MoveKey() || LineKey() || StatusKey())) return true;

            bool target = gs.IsInTargetMode() || gs.GetResponseMode() == GameState.ResponseMode.OPTION_TARGET;
            // the shared combat handlers were written as Hearthstone Access hooks ("true": skip HSA's
            // handler), so they claim every key: each gets only the keys it handles
            if (ZoneKey() && Combat.ZoneKeys(this, target && !gs.CanTargetCardsInHand())) return true;
            if (MoveKey() && Combat.ZoneMove(this)) return true;
            if ((Bind.READ_NEXT_VALID_ITEM.Pressed || Bind.READ_PREV_VALID_ITEM.Pressed) && Combat.ValidItems(this)) return true;
            if (LineKey() && Combat.CardLines(this)) return true;
            if (Combat.StatusKeys(this)) return true;

            if (target || gs.IsInSubOptionMode())
            {
                // a click on the target looked at (the mouse is on it); nothing looked at: silent
                if (Keys.Enter.Pressed)
                {
                    var on = Combat.FocusedCard;
                    if (on != null && on.GetEntity() != null)
                    {
                        var te = on.GetEntity();
                        Log.Info("match: target " + te.GetName());
                        Jobs.Run(ClickCard(on, () =>
                        {
                            if (te.GetZone() == TAG_ZONE.HAND) InputManager.Get().DoNetworkResponse(te);
                            else Ref.Call(InputManager.Get(), "HandleClickOnCardInBattlefield", te, true);
                        }));
                    }
                    return true;
                }
                if (Keys.Back.Pressed) { RightClick(); return true; }
                return false;
            }
            if (Keys.Back.Pressed && gs.IsInChoiceMode()) return true;      // a choice cannot be backed out of

            // Space on a hero in play, in your main option mode or the opponent's turn (as Hearthstone Access):
            // your hero gives your emotes, the enemy hero its squelch option
            var card = Combat.FocusedCard;
            var entity = card == null ? null : card.GetEntity();
            if (Keys.Space.Pressed && entity != null && entity.IsHero() && entity.GetZone() == TAG_ZONE.PLAY
                && (gs.IsInMainOptionMode() || !gs.IsFriendlySidePlayerTurn()) && !(GameMgr.Get() != null && GameMgr.Get().IsSpectator()))
            {
                if (entity.IsControlledByFriendlySidePlayer()) EmoteUI.Open(); else EnemyEmoteUI.Open();
                return true;
            }

            if (!gs.IsInMainOptionMode()) return false;
            if (Keys.Enter.Pressed) { if (card != null) Act(card); return true; }
            if (Bind.FORCE_END_TURN.Pressed) { EndTurn(); return true; }
            if (Bind.END_TURN.Pressed) { AskEndTurn(); return true; }
            if (Bind.SEND_MINION_TO_FACE.Pressed) { MinionToFace(card); return true; }
            if (Bind.SEND_ALL_MINIONS_TO_FACE.Pressed) { AllToFace(); return true; }
            if (Bind.PERFORM_DECK_ACTION.Pressed) { DeckAction(card); return true; }
            return false;
        }

        static bool ZoneKey()
        {
            return Bind.SEE_PLAYER_HAND.Pressed || Bind.SEE_PLAYER_SECRETS.Pressed || Bind.SEE_OPPONENT_SECRETS.Pressed || Bind.SEE_PLAYER_MINIONS.Pressed
                || Bind.SEE_OPPONENT_MINIONS.Pressed || Bind.SEE_OPPONENT_HERO.Pressed || Bind.SEE_PLAYER_HERO.Pressed || Bind.SEE_PLAYER_HERO_POWER.Pressed
                || Bind.SEE_OPPONENT_HERO_POWER.Pressed || Bind.SEE_PLAYER_WEAPON.Pressed || Bind.SEE_OPPONENT_WEAPON.Pressed;
        }

        // A / Shift+A (mana), D / Shift+D (decks), Shift+C (the opponent's hand)
        static bool StatusKey()
        {
            return Bind.SEE_OPPONENT_MANA.Pressed || Bind.SEE_PLAYER_MANA.Pressed || Bind.SEE_OPPONENT_DECK.Pressed || Bind.SEE_PLAYER_DECK.Pressed
                || Bind.SEE_OPPONENT_HAND.Pressed;
        }

        static bool MoveKey()
        {
            return Bind.READ_NEXT_ITEM.Pressed || Bind.READ_PREV_ITEM.Pressed || Bind.READ_FIRST_ITEM.Pressed || Bind.READ_LAST_ITEM.Pressed || Keys.Number().HasValue;
        }

        static bool LineKey()
        {
            return Bind.READ_NEXT_LINE.Pressed || Bind.READ_PREV_LINE.Pressed || Bind.READ_CUR_LINE.Pressed || Bind.READ_TO_END.Pressed || Bind.READ_ORIGINAL_CARD_STATS.Pressed;
        }

        // F1, as Hearthstone Access (docs/combat-spec-1-navigation-reading.md section 5)
        internal override string Help()
        {
            var gs = GS;
            if (gs == null) return "";
            if (m_mode == Mode.Assemble)
            {
                if (m_assembleViewingTarget)
                    return Speech.S("HSA_ENHANCED_ASSEMBLE_TARGET_HELP", Keys.Up.Name, Keys.Down.Name, Keys.Back.Name, Keys.Enter.Name);
                return Speech.S("HSA_ENHANCED_ASSEMBLE_PARTS_HELP", Bind.READ_PREV_ITEM.Name, Bind.READ_NEXT_ITEM.Name,
                    Keys.Up.Name, Keys.Down.Name, Bind.ASSEMBLE_SELECT_TOP.Name, Bind.ASSEMBLE_SELECT_BOTTOM.Name,
                    Bind.ASSEMBLE_VIEW_TARGET.Name, Keys.Enter.Name);
            }
            var end = EndGameScreen.Get();
            if (end != null && end.m_hitbox != null && end.m_hitbox.gameObject.activeInHierarchy) return Speech.S(K.PRESS_KEY_TO_CONTINUE, Keys.Enter.Name);
            if (gs.IsGameOver()) return Speech.S(K.GAMEPLAY_GAME_OVER_GENERIC);
            if (!gs.IsGameCreated() || gs.IsMulliganPhasePending()) return Speech.S(K.GAMEPLAY_MULLIGAN_HELP_WAITING_FOR_GAME_TO_START);
            if (m_mode == Mode.Mulligan) return Speech.S(K.GAMEPLAY_MULLIGAN_HELP, Keys.Space.Name, Keys.Enter.Name);
            if (gs.IsMulliganManagerActive()) return Speech.S(K.GAMEPLAY_WAITING_FOR_OPPONENT);
            if (m_mode == Mode.DeckAction || m_mode == Mode.Reverse) return Speech.S(K.TUTORIAL_HOGGER_2_5, Keys.Enter.Name, Keys.Back.Name);
            if (m_confirmEndTurn) return Speech.S(K.GAMEPLAY_CONFIRM_END_TURN_HELP, Keys.Enter.Name, Bind.END_TURN.Name);
            if (m_mode == Mode.Place)
                return m_placing != null && m_placing.IsLocation() ? Speech.S(K.GAMEPLAY_POSITION_LOCATION_HELP, Keys.Enter.Name) : Speech.S(K.GAMEPLAY_SUMMON_MINION_HELP, Keys.Enter.Name);
            bool target = gs.IsInTargetMode() || gs.GetResponseMode() == GameState.ResponseMode.OPTION_TARGET || gs.GetResponseMode() == GameState.ResponseMode.OPTION_REVERSE_TARGET;
            if (m_mode == Mode.Playing && !target) return Speech.S(K.GAMEPLAY_PLAY_CARD_HELP, Keys.Enter.Name);
            if (!gs.IsFriendlySidePlayerTurn()) return Speech.S(K.GAMEPLAY_OPPONENT_TURN);
            if (m_mode == Mode.Choices && RewindUIManager.IsShowingRewindUI) return Speech.S("ACCESSIBILITY_GAMEPLAY_REWIND_MODE_HELP", Bind.REWIND.Name, Bind.REWIND_KEEP.Name);
            if (m_mode == Mode.Choices) return Speech.S(K.GAMEPLAY_CHOICE_MODE_HELP, Keys.Enter.Name, Keys.Tab.Name);
            if (gs.IsInChoiceMode()) return Speech.S(K.GAMEPLAY_HIDDEN_CHOICE_HELP, Keys.Tab.Name);
            if (target) return Speech.S(K.GAMEPLAY_CHOOSE_TARGET_HELP, Keys.Tab.Name, Keys.Enter.Name, Keys.Back.Name);
            if (gs.IsInMainOptionMode()) return MainHelp(gs);
            return "";
        }

        // your turn, nothing held: what the focused card allows, then every key to look around
        string MainHelp(GameState gs)
        {
            var lines = new List<string>();
            var button = EndTurnButton.Get();
            bool noPlays = button != null && button.HasNoMorePlays();
            var me = gs.GetFriendlySidePlayer();
            var them = gs.GetOpposingSidePlayer();
            bool tutorial = gs.GetGameEntity() is TutorialEntity;
            string mana = Speech.S(K.GAMEPLAY_READ_PLAYER_MANA_HELP, Bind.SEE_PLAYER_MANA.Name);
            bool manaFirst = false;
            if (noPlays) lines.Add(Speech.S(K.GAMEPLAY_END_TURN_HELP, Bind.END_TURN.Name));
            var card = Combat.FocusedCard;
            var e = card == null ? null : card.GetEntity();
            if (e != null)
            {
                lines.Add(Speech.S(K.GAMEPLAY_READ_CARD_HELP));
                if (TooltipsShown()) lines.Add(Speech.S(K.GAMEPLAY_READ_CARD_TOOLTIP_HELP, Bind.READ_TOOLTIP.Name));
                bool valid = false;
                try { valid = gs.IsValidOption(e); } catch { }
                bool mine = e.IsControlledByFriendlySidePlayer();
                if (valid && e.GetZone() == TAG_ZONE.HAND)
                {
                    lines.Add(Speech.S(K.GAMEPLAY_PLAY_CARD_HELP, Keys.Enter.Name));
                    if (e.IsForgeable()) lines.Add(Speech.S(K.GAMEPLAY_FORGE_CARD_HELP, Bind.PERFORM_DECK_ACTION.Name));
                    else if (e.IsPrepareable()) lines.Add(Speech.S(K.GAMEPLAY_PREPARE_CARD_HELP, Bind.PERFORM_DECK_ACTION.Name));
                    else if (e.IsTradeable()) lines.Add(Speech.S(K.GAMEPLAY_TRADE_CARD_HELP, Bind.PERFORM_DECK_ACTION.Name));
                }
                else if (valid && mine && e.IsMinion() && e.GetZone() == TAG_ZONE.PLAY) lines.Add(Speech.S(K.GAMEPLAY_ATTACK_WITH_MINION_HELP, Keys.Enter.Name));
                else if (valid && mine && e.IsHero()) lines.Add(Speech.S(K.GAMEPLAY_ATTACK_WITH_HERO_HELP, Keys.Enter.Name));
                else if (valid && mine && e.IsHeroPower()) lines.Add(Speech.S(K.GAMEPLAY_USE_HERO_POWER_HELP, Keys.Enter.Name));
                else if (!valid && mine && e.GetZone() == TAG_ZONE.HAND) { lines.Add(mana); manaFirst = true; }
            }
            if (!noPlays && e == null) lines.Add(Speech.S(K.GAMEPLAY_SEE_VALID_OPTIONS, Bind.READ_NEXT_VALID_ITEM.Name));
            if (me != null && me.GetHandZone() != null && me.GetHandZone().GetCardCount() > 0) lines.Add(Speech.S(K.GAMEPLAY_SEE_PLAYER_HAND_HELP, Bind.SEE_PLAYER_HAND.Name));
            if (me != null && me.GetBattlefieldZone() != null && me.GetBattlefieldZone().GetCardCount() > 0) lines.Add(Speech.S(K.GAMEPLAY_SEE_PLAYER_MINIONS_HELP, Bind.SEE_PLAYER_MINIONS.Name));
            if (me != null && me.GetSecretZone() != null && me.GetSecretZone().GetCardCount() > 0) lines.Add(Speech.S(K.GAMEPLAY_SEE_PLAYER_SECRETS_HELP, Bind.SEE_PLAYER_SECRETS.Name));
            lines.Add(Speech.S(K.GAMEPLAY_SEE_PLAYER_HERO_HELP, Bind.SEE_PLAYER_HERO.Name));
            if (me != null && me.GetHeroPower() != null) lines.Add(Speech.S(K.GAMEPLAY_READ_PLAYER_HERO_POWER_HELP, Bind.SEE_PLAYER_HERO_POWER.Name));
            if (them != null && them.GetHeroPower() != null) lines.Add(Speech.S(K.GAMEPLAY_READ_OPPONENT_HERO_POWER_HELP, Bind.SEE_OPPONENT_HERO_POWER.Name));
            lines.Add(Speech.S(K.GAMEPLAY_SEE_OPPONENT_HERO_HELP, Bind.SEE_OPPONENT_HERO.Name));
            if (them != null && them.GetBattlefieldZone() != null && them.GetBattlefieldZone().GetCardCount() > 0) lines.Add(Speech.S(K.GAMEPLAY_SEE_OPPONENT_MINIONS_HELP, Bind.SEE_OPPONENT_MINIONS.Name));
            if (!tutorial)
            {
                lines.Add(Speech.S(K.GAMEPLAY_COUNT_PLAYER_DECK_HELP, Bind.SEE_PLAYER_DECK.Name));
                lines.Add(Speech.S(K.GAMEPLAY_COUNT_OPPONENT_DECK_HELP, Bind.SEE_OPPONENT_DECK.Name));
                lines.Add(Speech.S(K.GAMEPLAY_COUNT_OPPONENT_HAND_HELP, Bind.SEE_OPPONENT_HAND.Name));
                lines.Add(Speech.S(K.GAMEPLAY_COUNT_OPPONENT_SECRETS_HELP, Bind.SEE_OPPONENT_SECRETS.Name));
            }
            if (!manaFirst) lines.Add(mana);
            if (!tutorial)
            {
                lines.Add(Speech.S(K.GAMEPLAY_READ_OPPONENT_MANA_HELP, Bind.SEE_OPPONENT_MANA.Name));
                lines.Add(Speech.S(K.GAMEPLAY_OPEN_HISTORY_LOG_HELP, Bind.SEE_HISTORY.Name));
            }
            return string.Join(" ", lines);
        }

        // the game's keyword panels are up (the mouse is on a card that has some)
        static bool TooltipsShown()
        {
            try
            {
                var panels = Ref.Get(TooltipPanelManager.Get(), "m_tooltipPanels") as System.Collections.ICollection;
                return panels != null && panels.Count > 0;
            }
            catch { return false; }
        }

        internal override void Read() { }
        #endregion

        #region Assemble
        void BeginAssemble(AssembleUIManager manager, AssembleChoiceParams[] choices)
        {
            m_mode = Mode.Assemble;
            m_assembleManager = manager;
            m_assembleChoiceData = choices;
            m_assembleChoices.Clear();
            m_assembleAt = 0;
            m_assembleLine = 0;
            m_assembleTargetLine = 0;
            m_assembleViewingTarget = false;

            var target = Ref.Get<Entity>(manager, "m_assembleTarget");
            m_assembleTargetName = target == null ? "" : Str.Clean(target.GetName());
            m_assembleTop = ReadAssembleText(Ref.Get<UberText>(manager, "m_assembleTargetTopHalfText"));
            m_assembleBottom = ReadAssembleText(Ref.Get<UberText>(manager, "m_assembleTargetBottomHalfText"));

            var cards = ChoiceCardMgr.Get() == null ? null : ChoiceCardMgr.Get().GetFriendlyCards();
            if (choices != null && cards != null)
            {
                for (int i = 0; i < choices.Length && i * 2 < cards.Count; i++)
                {
                    var choice = choices[i];
                    if (choice == null) continue;
                    var topButton = Ref.Get<UIBButton>(choice, "m_choiceFirstHalfButton");
                    var bottomButton = Ref.Get<UIBButton>(choice, "m_choiceSecondHalfButton");
                    var firstHalf = cards[i * 2].GetEntity();
                    m_assembleChoices.Add(new AssemblePartChoice
                    {
                        Name = firstHalf == null ? "" : Str.Clean(firstHalf.GetName()),
                        Top = ReadAssembleText(Ref.Get<UberText>(choice, "m_choiceFirstHalfText")),
                        Bottom = ReadAssembleText(Ref.Get<UberText>(choice, "m_choiceSecondHalfText")),
                        TopButton = topButton,
                        BottomButton = bottomButton
                    });
                }
            }
            Log.Info("match: Assemble shown with " + m_assembleChoices.Count + " part choices");
            Say(Speech.S("ACCESSIBILITY_GAMEPLAY_QUERY_ASSEMBLE", AssembleTargetText()));
            ReadAssembleChoice();
        }

        static string ReadAssembleText(UberText text)
        {
            return text == null ? "" : Str.Clean(Ui.ShownText(text.Text));
        }

        string AssembleTargetText()
        {
            return Str.Join(m_assembleTop, m_assembleBottom);
        }

        void ReadAssembleChoice()
        {
            if (m_assembleChoices.Count == 0)
            {
                Say(Speech.S("ACCESSIBILITY_LIST_NO_ITEMS"));
                return;
            }
            m_assembleAt = Math.Max(0, Math.Min(m_assembleAt, m_assembleChoices.Count - 1));
            m_assembleLine = 0;
            var item = m_assembleChoices[m_assembleAt];
            Say(Speech.S(K.MENU_OPTION_FORMAT, item.Name, m_assembleAt + 1, m_assembleChoices.Count));
        }

        bool AssembleKeys()
        {
            if (m_assembleViewingTarget)
            {
                if (Keys.Back.Pressed)
                {
                    m_assembleViewingTarget = false;
                    ReadAssembleChoice();
                    return true;
                }
                if (Keys.Down.Pressed) { m_assembleTargetLine = Math.Min(2, m_assembleTargetLine + 1); Say(AssembleTargetLine(m_assembleTargetLine)); return true; }
                if (Keys.Up.Pressed) { m_assembleTargetLine = Math.Max(0, m_assembleTargetLine - 1); Say(AssembleTargetLine(m_assembleTargetLine)); return true; }
                if (Keys.ShiftUp.Pressed) { Say(AssembleTargetLine(m_assembleTargetLine)); return true; }
            }
            else
            {
                if (Bind.ASSEMBLE_VIEW_TARGET.Pressed)
                {
                    m_assembleViewingTarget = true;
                    m_assembleTargetLine = 0;
                    Say(AssembleTargetLine(m_assembleTargetLine));
                    return true;
                }
                if (m_assembleChoices.Count > 0 && Bind.ASSEMBLE_SELECT_TOP.Pressed) { SelectAssemblePart(true); return true; }
                if (m_assembleChoices.Count > 0 && Bind.ASSEMBLE_SELECT_BOTTOM.Pressed) { SelectAssemblePart(false); return true; }
                if (Keys.Right.Pressed) { if (m_assembleAt + 1 < m_assembleChoices.Count) { m_assembleAt++; ReadAssembleChoice(); } return true; }
                if (Keys.Left.Pressed) { if (m_assembleAt > 0) { m_assembleAt--; ReadAssembleChoice(); } return true; }
                if (Keys.Home.Pressed) { m_assembleAt = 0; ReadAssembleChoice(); return true; }
                if (Keys.End.Pressed) { m_assembleAt = m_assembleChoices.Count - 1; ReadAssembleChoice(); return true; }
                if (m_assembleChoices.Count > 0 && Keys.Down.Pressed)
                {
                    m_assembleLine = Math.Min(2, m_assembleLine + 1);
                    Say(AssembleChoiceLine(m_assembleChoices[m_assembleAt], m_assembleLine));
                    return true;
                }
                if (m_assembleChoices.Count > 0 && Keys.Up.Pressed)
                {
                    m_assembleLine = Math.Max(0, m_assembleLine - 1);
                    Say(AssembleChoiceLine(m_assembleChoices[m_assembleAt], m_assembleLine));
                    return true;
                }
                if (m_assembleChoices.Count > 0 && Keys.ShiftUp.Pressed)
                {
                    Say(AssembleChoiceLine(m_assembleChoices[m_assembleAt], m_assembleLine));
                    return true;
                }
            }

            if (Keys.Enter.Pressed)
            {
                var manager = AssembleUIManager.Get();
                var selected = manager == null ? null : Ref.Get<Entity>(manager, "m_currentAssembleChoice");
                if (selected == null) { Say(Speech.S("ACCESSIBILITY_GAMEPLAY_ASSEMBLE_NO_SELECTION")); return true; }
                var confirm = manager == null ? null : Ref.Get<UIBButton>(manager, "m_confirmAssembleChoiceButton");
                if (confirm != null && confirm.isActiveAndEnabled) Core.Click.Peg(confirm);
                else Say(Speech.S("HSA_ENHANCED_ASSEMBLE_CANNOT_CONFIRM"));
                return true;
            }
            return true;
        }

        string AssembleChoiceLine(AssemblePartChoice item, int line)
        {
            if (line == 1) return item.Top;
            if (line == 2) return item.Bottom;
            return Speech.S(K.MENU_OPTION_FORMAT, item.Name, m_assembleAt + 1, m_assembleChoices.Count);
        }

        string AssembleTargetLine(int line)
        {
            if (line == 1) return "Top half: " + m_assembleTop;
            if (line == 2) return "Bottom half: " + m_assembleBottom;
            return Str.Join(m_assembleTargetName, AssembleTargetText());
        }

        void SelectAssemblePart(bool top)
        {
            if (m_assembleAt < 0 || m_assembleAt >= m_assembleChoices.Count) return;
            var item = m_assembleChoices[m_assembleAt];
            var button = top ? item.TopButton : item.BottomButton;
            if (button == null || !button.isActiveAndEnabled) { Say(Speech.S("HSA_ENHANCED_ASSEMBLE_PART_UNAVAILABLE")); return; }
            Core.Click.Peg(button);
            if (top) m_assembleTop = item.Top;
            else m_assembleBottom = item.Bottom;
            Log.Info("match: Assemble selected " + (top ? "top" : "bottom") + " half from " + item.Name);
            Say(AssembleTargetText());
        }
        #endregion

        #region Acting on a card
        // Enter in main option mode, as Hearthstone Access: a left click on the card looked at (the mouse is
        // on it). Not checked first: the game does what the click does there, or says why not (its play
        // error, spoken by our hook). If the game's mouse-over is not this card, "Try again", the card is
        // made the moused-over one and the click goes all the same.
        void Act(Card card)
        {
            var e = card.GetEntity();
            if (e == null) return;
            if (MercBattle.OnEnter(card)) return;
            var im = InputManager.Get();
            if (im == null) return;
            PointCard(card);
            if (im.GetMousedOverCard() != card && !card.IsMousedOver())
            {
                Say(Speech.S(K.GAMEPLAY_TRY_AGAIN));
                try { im.SetMousedOverCard(card); } catch (Exception ex) { Log.Error(ex); }
            }
            Log.Info("match: click " + e.GetName());
            bool inHand = e.GetZone() == TAG_ZONE.HAND;
            Jobs.Run(ClickCard(card, () =>
            {
                if (inHand) Ref.Call(im, "GrabCard", card.gameObject);
                else Ref.Call(im, "HandleClickOnCardInBattlefield", e, true);
            }));
        }

        // A click on a card, made sure of: the mouse is put on the card (its mana gem in the hand, its middle,
        // its position) until the game's own raycast finds that card under it, then the click. If no point
        // reaches it, the game's function that the click would have called does it instead (the same action,
        // without the mouse), so nothing is ever stuck.
        IEnumerator ClickCard(Card card, Action direct)
        {
            bool own = !m_mouseOwned;
            m_mouseOwned = true;
            try
            {
                if (card == null || card.gameObject == null) yield break;
                foreach (var point in CardPoints(card))
                {
                    VirtualMouse.PointAtWorld(point);
                    yield return null;
                    yield return null;
                    if (!UnderMouse(card)) continue;
                    VirtualMouse.ClickHere(0);
                    while (VirtualMouse.Clicking) yield return null;
                    yield break;
                }
                Log.Info("match: the mouse does not reach " + (card.GetEntity() == null ? "?" : card.GetEntity().GetName()) + "; the game's function instead");
                try { if (direct != null) direct(); } catch (Exception ex) { Log.Error(ex); }
            }
            finally { if (own) m_mouseOwned = false; }
        }

        static List<Vector3> CardPoints(Card card)
        {
            var points = new List<Vector3>();
            var actor = card.GetActor();
            var gem = actor == null ? null : (actor.m_manaObject ?? actor.m_attackObject ?? actor.m_healthObject);
            if (gem != null && card.GetZone() is ZoneHand) points.Add(gem.transform.position);
            var col = card.GetComponent<Collider>() ?? card.GetComponentInChildren<Collider>();
            if (col != null) points.Add(col.bounds.center);
            points.Add(card.transform.position);
            if (gem != null && !(card.GetZone() is ZoneHand)) points.Add(gem.transform.position);
            return points;
        }

        // the card the game's mouse raycast finds (the card itself or its stand-in in the hand)
        static bool UnderMouse(Card card)
        {
            var input = UniversalInputManager.Get();
            RaycastHit hit;
            if (input == null || !input.GetInputHitInfo(GameLayer.CardRaycast, out hit)) return false;
            var actor = hit.transform.GetComponentInParent<Actor>();
            if (actor != null && actor.GetCard() == card) return true;
            var standIn = hit.transform.GetComponentInParent<CardStandIn>();
            return standIn != null && standIn.linkedCard == card;
        }

        // Backspace: a right click (the game's cancel: a target, a Choose One, a held card back to the hand),
        // then the mouse out of the way
        void RightClick()
        {
            Log.Info("match: cancel");
            VirtualMouse.ClickHere(1, true);
        }

        void Respond(Entity e, GAME_TAG keyword)
        {
            Log.Info("match: " + e.GetName() + (keyword == GAME_TAG.TAG_NOT_SET ? "" : " (" + keyword + ")"));
            Combat.WhenReady(this, e, () =>
            {
                if (!InputManager.Get().DoNetworkResponse(e, true, keyword)) Log.Info("match: the game refused " + e.GetName());
            });
        }

        static readonly MethodInfo ErrorDescription = typeof(PlayErrors).GetMethod("GetErrorDescription", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        static string WhyNot(Entity e)
        {
            try
            {
                var gs = GS;
                var type = gs.GetErrorType(e);
                if (type == PlayErrors.ErrorType.NONE || ErrorDescription == null) return null;
                var text = Str.Clean(ErrorDescription.Invoke(null, new object[] { type, gs.GetErrorParam(e), e, null }) as string);
                return text.Length == 0 ? null : text;
            }
            catch { return null; }
        }

        // cancels what is under way (a target, a Choose One)
        void Cancel()
        {
            Log.Info("match: cancel");
            var cancel = Ref.Method(typeof(InputManager), "CancelOption", 1);
            try
            {
                if (cancel != null) cancel.Invoke(InputManager.Get(), new object[] { false });
                else { InputManager.Get().CancelTargetMode(); InputManager.Get().CancelSubOptionMode(false); }
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        #endregion

        #region Placing a minion or location
        ZonePlay Zone(bool opposing) { return ZoneMgr.Get().FindZoneOfType<ZonePlay>(opposing ? Player.Side.OPPOSING : Player.Side.FRIENDLY); }

        bool PlaceKeys()
        {
            var zone = Zone(m_opposingSide);
            int last = zone == null ? 1 : zone.GetCards().Count + 1;
            int was = m_slot;
            if (Keys.Left.Pressed) m_slot = Math.Max(1, m_slot - 1);
            else if (Keys.Right.Pressed) m_slot = Math.Min(last, m_slot + 1);
            else if (Keys.Home.Pressed) { m_slot = 1; SayPlace(); return true; }
            else if (Keys.End.Pressed) { m_slot = last; SayPlace(); return true; }
            else if (Keys.Number().HasValue) { m_slot = Math.Min(last, Keys.Number().Value); SayPlace(); return true; }
            else if (Bind.SWITCH_TO_OPPOSING_SUMMONING_SIDE.Pressed && m_placing.HasDisguisedAction()) { m_opposingSide = true; m_slot = Zone(true) == null ? 1 : Zone(true).GetCards().Count + 1; SayPlace(); return true; }
            else if (Bind.SWITCH_TO_FRIENDLY_SUMMONING_SIDE.Pressed && m_opposingSide) { m_opposingSide = false; m_slot = Zone(false).GetCards().Count + 1; SayPlace(); return true; }
            else if (Keys.Enter.Pressed) { Drop(); return true; }
            else if (Keys.Back.Pressed) { RightClick(); return true; }
            else if (Bind.PERFORM_DECK_ACTION.Pressed) { HeldDeckAction(); return true; }
            else if (Combat.CardLines(this) || Combat.StatusKeys(this)) return true;
            else return true;
            if (m_slot != was) SayPlace();
            return true;
        }

        void SayPlace()
        {
            var zone = Zone(m_opposingSide);
            var cards = zone == null ? new List<Card>() : zone.GetCards();
            int last = cards.Count + 1;
            string key;
            object[] args = new object[0];
            if (cards.Count == 0) key = m_opposingSide ? K.GAMEPLAY_QUERY_SUMMON_MINION_OPPOSING_SIDE : K.GAMEPLAY_QUERY_SUMMON_MINION;
            else if (m_slot >= last) key = m_opposingSide ? K.GAMEPLAY_QUERY_SUMMON_MINION_AT_THE_RIGHT_OPPOSING_SIDE : K.GAMEPLAY_QUERY_SUMMON_MINION_AT_THE_RIGHT;
            else if (m_slot <= 1) key = m_opposingSide ? K.GAMEPLAY_QUERY_SUMMON_MINION_AT_THE_LEFT_OPPOSING_SIDE : K.GAMEPLAY_QUERY_SUMMON_MINION_AT_THE_LEFT;
            else
            {
                key = m_opposingSide ? K.GAMEPLAY_QUERY_SUMMON_MINION_BETWEEN_OPPOSING_SIDE : K.GAMEPLAY_QUERY_SUMMON_MINION_BETWEEN;
                args = new object[] { Str.Clean(cards[m_slot - 2].GetEntity().GetName()), Str.Clean(cards[m_slot - 1].GetEntity().GetName()) };
            }
            Say(Speech.S(key, args));
            PlaceMouse();
        }

        // "Play card?": Enter plays it (a click over the board drops it), Backspace puts it back
        bool PlayKeys()
        {
            if (Bind.PERFORM_DECK_ACTION.Pressed) { HeldDeckAction(); return true; }
            if (Keys.Enter.Pressed) { Drop(); return true; }
            if (Keys.Back.Pressed) { RightClick(); return true; }
            if (Combat.StatusKeys(this)) return true;
            return true;
        }

        // T while holding a card that allows it (not a location): its question, then to the deck action area
        void HeldDeckAction()
        {
            if (m_placing == null || m_placing.IsLocation() || !DeckAllowed(m_placing)) return;
            Say(DeckQuestion(m_placing));
            m_mode = Mode.DeckAction;
        }

        // the held card let go where the mouse is (the game's DropHeldCard on the mouse-up)
        void Drop()
        {
            var zone = Zone(m_opposingSide);
            if (m_mode == Mode.Place && zone != null && zone.GetSlotMousedOver() > 0 && zone.GetSlotMousedOver() != m_slot)
                Log.Info("match: the game's slot is " + zone.GetSlotMousedOver() + ", ours " + m_slot);
            Log.Info("match: drop " + (m_placing == null ? "?" : m_placing.GetName()));
            if (m_mode == Mode.Place) PlaceMouse();
            VirtualMouse.ClickHere(0);
        }

        #endregion

        #region Choices (Discover, Choose One)
        string ChoiceTitle()
        {
            try
            {
                var banner = Ref.Get(ChoiceCardMgr.Get(), "m_choiceBanner");
                var headline = banner == null ? null : Ref.Get(banner, "m_headline") as UberText;
                var text = headline == null ? null : Ui.ShownText(headline.Text);
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch { }
            return Str.Word("GAMEPLAY_CHOOSE_ONE");
        }

        bool ChoiceKeys()
        {
            if (m_list.Count == 0) return false;
            // Rewind: U rewinds, J keeps (the zone keys still look at the board)
            if (RewindUIManager.IsShowingRewindUI && (Bind.REWIND.Pressed || Bind.REWIND_KEEP.Pressed))
            {
                var hud = RewindUIManager.Get();
                var button = hud == null ? null : Ref.Get<UIBButton>(hud, Bind.REWIND.Pressed ? "m_rewindButton" : "m_keepButton");
                Log.Info("match: " + (Bind.REWIND.Pressed ? "rewind" : "keep"));
                if (button != null && button.gameObject.activeInHierarchy) Core.Click.Peg(button);
                return true;
            }
            if (ListKeys(Keys.Tab.Pressed && GS.IsInSubOptionMode())) return true;
            if (Keys.Enter.Pressed)
            {
                var e = m_list[m_at].GetEntity();
                if (RerollEntity() == e) { Reroll(); return true; }
                Log.Info("match: chose " + e.GetName());
                // Rewind / Keep: the game's own button (its animation goes with it)
                if (RewindUIManager.IsShowingRewindUI)
                {
                    var hud = RewindUIManager.Get();
                    var button = hud == null ? null : Ref.Get<UIBButton>(hud, e.GetCardId() == RewindUIManager.REWIND_CHOICE_CARDID ? "m_rewindButton" : "m_keepButton");
                    if (button != null && button.gameObject.activeInHierarchy) { Core.Click.Peg(button); return true; }
                }
                // a click on the card (the game's choice or Choose One click: its sound, its errors)
                bool sub = GS.IsInSubOptionMode();
                Jobs.Run(ClickCard(m_list[m_at], () =>
                {
                    if (sub) InputManager.Get().HandleClickOnSubOption(e);
                    else Ref.Call(InputManager.Get(), "HandleClickOnChoice", e);
                }));
                return true;
            }
            if (Keys.Back.Pressed && GS.IsInSubOptionMode()) { RightClick(); return true; }
            if (Keys.Tab.Pressed && !GS.IsInSubOptionMode())
            {
                var toggle = ChoiceCardMgr.Get().GetToggleButton();
                if (toggle != null && toggle.gameObject.activeInHierarchy) { Core.Click.Peg(toggle); return true; }
            }
            if (Bind.REROLL_CHOICE.Pressed && RerollEntity() != null) { Reroll(); return true; }
            return Combat.StatusKeys(this) || Keys.Back.Pressed;
        }

        // The Skeleton Key's refresh: the game takes its card out of the choices and shows a button
        static Entity RerollEntity()
        {
            var reroll = UnityEngine.Object.FindObjectOfType<RerollUIManager>();
            return reroll == null || !reroll.gameObject.activeInHierarchy ? null : Ref.Get<Entity>(reroll, "m_rerollEntity");
        }

        static void Reroll()
        {
            var reroll = UnityEngine.Object.FindObjectOfType<RerollUIManager>();
            var button = reroll == null ? null : Ref.Get(reroll, "m_rerollButton") as PegUIElement;
            Log.Info("match: reroll");
            if (button != null && button.gameObject.activeInHierarchy) Core.Click.Peg(button);
        }

        // the refresh as the last of the choices (once the game shows its button)
        bool AddReroll()
        {
            var e = RerollEntity();
            var card = e == null ? null : e.GetCard();
            if (card == null || m_list.Contains(card)) return false;
            m_list.Add(card);
            return true;
        }

        void SayRerollHelp() { Say(Speech.S(K.GAMEPLAY_REROLL_CHOICE_HELP, Bind.REROLL_CHOICE.Name)); }

        // Left/Right, Home/End (and Tab, wrapping, when asked), and the focused card's lines
        bool ListKeys(bool tabMoves)
        {
            int n = m_list.Count;
            if (Keys.Right.Pressed) { if (m_at + 1 < n) { m_at++; ReadListItem(); } return true; }
            if (Keys.Left.Pressed) { if (m_at > 0) { m_at--; ReadListItem(); } return true; }
            if (Keys.Home.Pressed) { m_at = 0; ReadListItem(); return true; }
            if (Keys.End.Pressed) { m_at = n - 1; ReadListItem(); return true; }
            if (tabMoves) { m_at = (m_at + 1) % n; ReadListItem(); return true; }
            if (Keys.ShiftTab.Pressed && GS.IsInSubOptionMode()) { m_at = (m_at + n - 1) % n; ReadListItem(); return true; }
            var lines = CombatCards.Lines(m_list[m_at]);
            if (lines.Count == 0) return false;
            if (Keys.Down.Pressed) { if (m_line + 1 < lines.Count) Say(lines[++m_line]); return true; }
            if (Keys.Up.Pressed) { if (m_line > 0) Say(lines[--m_line]); return true; }
            if (Keys.ShiftUp.Pressed) { Say(lines[Math.Min(m_line, lines.Count - 1)]); return true; }
            if (Keys.ShiftDown.Pressed) { for (int i = m_line; i < lines.Count; i++) Say(lines[i]); m_line = lines.Count - 1; return true; }
            return false;
        }

        void ReadListItem()
        {
            m_line = 0;
            if (m_at < 0 || m_at >= m_list.Count) return;
            var lines = CombatCards.Lines(m_list[m_at]);
            var first = lines.Count > 0 ? lines[0] : Str.Clean(m_list[m_at].GetEntity().GetName());
            var extra = m_mode == Mode.Mulligan ? Marked(m_at) : null;
            Say(Str.Join(Speech.S(K.MENU_OPTION_FORMAT, first, m_at + 1, m_list.Count), extra));
        }
        #endregion

        #region Mulligan
        static List<Card> MulliganCards()
        {
            var m = MulliganManager.Get();
            return m == null ? null : Ref.Get(m, "m_startingCards") as List<Card>;
        }

        // the cards are dealt and the confirm button is up
        static bool MulliganReady()
        {
            var m = MulliganManager.Get();
            var button = m == null ? null : Ref.Get(m, "mulliganButton") as NormalButton;
            var cards = MulliganCards();
            // the cards spread and the game waiting for the player's choice (as Hearthstone Access said them)
            return button != null && button.gameObject.activeInHierarchy && cards != null && cards.Count > 0 && Ref.Get<bool>(m, "m_waitingForUserInput");
        }

        // the game's own mark: replaced or kept
        string Marked(int i)
        {
            var marks = Ref.Get(MulliganManager.Get(), "m_handCardsMarkedForReplace") as bool[];
            bool marked = marks != null && i < marks.Length && marks[i];
            return Speech.S(marked ? K.GAMEPLAY_MULLIGAN_WILL_BE_REPLACED : K.GAMEPLAY_MULLIGAN_WILL_NOT_BE_REPLACED);
        }

        bool MulliganKeys()
        {
            if (m_list.Count == 0) return false;
            if (Bind.MULLIGAN_MARK_CARD.Pressed)
            {
                // a click on the card: the game marks it (or not)
                var marking = m_list[m_at];
                Jobs.Run(ClickCard(marking, () => MulliganManager.Get().ToggleHoldState(marking)));
                int i = m_at;
                Jobs.Run(SayMarkLater(i));
                return true;
            }
            if (Keys.Enter.Pressed)
            {
                var button = Ref.Get(MulliganManager.Get(), "mulliganButton") as NormalButton;
                if (button != null && button.IsEnabled()) { Log.Info("match: mulligan confirmed"); GameStart.Confirmed = true; Core.Click.Peg(button); m_mulliganListening = false; VirtualMouse.Hide(); m_mode = Mode.Browse; m_list.Clear(); }
                return true;
            }
            if (Keys.Tab.Pressed) { m_at = (m_at + 1) % m_list.Count; ReadListItem(); return true; }
            if (Keys.ShiftTab.Pressed) { m_at = (m_at + m_list.Count - 1) % m_list.Count; ReadListItem(); return true; }
            if (ListKeys(false)) return true;
            if (Bind.READ_ANOMALIES.Pressed) return Combat.StatusKeys(this);
            return true;
        }

        // once the click is through (a few frames)
        IEnumerator SayMarkLater(int i)
        {
            yield return null;
            float until = Time.unscaledTime + 2f;
            while (Time.unscaledTime < until && (m_mouseOwned || VirtualMouse.Clicking)) yield return null;
            yield return null;
            Say(Marked(i));
        }
        #endregion

        #region Turn, attacks on the hero, deck actions
        void AskEndTurn()
        {
            var button = EndTurnButton.Get();
            if (!Settings.ConfirmEndTurn || (button != null && button.HasNoMorePlays())) { EndTurn(); return; }
            var gs = GS;
            var me = gs.GetFriendlySidePlayer();
            string key = K.GAMEPLAY_QUERY_END_TURN_WHEN_VALID_PLAYS;
            bool attack = me.GetHero() != null && gs.HasResponse(me.GetHero());
            bool location = false;
            foreach (var c in me.GetBattlefieldZone().GetCards())
            {
                var e = c.GetEntity();
                if (e == null || !gs.HasResponse(e)) continue;
                if (e.IsLocation()) location = true; else attack = true;
            }
            var power = me.GetHeroPower();
            if (attack) key = K.GAMEPLAY_QUERY_END_TURN_WHEN_CAN_ATK;
            else if (location) key = K.GAMEPLAY_QUERY_END_TURN_WHEN_CAN_USE_LOCATION;
            else if (power != null && gs.HasResponse(power)) key = K.GAMEPLAY_QUERY_END_TURN_WHEN_CAN_USE_HERO_POWER;
            m_confirmEndTurn = true;
            Say(Speech.S(key));
        }

        // the opponent's turn, or yours already ended (the button waits for the opponent)
        static bool OpponentsTurn(GameState gs)
        {
            if (!gs.IsFriendlySidePlayerTurn()) return true;
            var button = EndTurnButton.Get();
            try { return button != null && button.IsInWaitingState(); } catch { return false; }
        }

        void EndTurn()
        {
            m_confirmEndTurn = false;
            Log.Info("match: end turn");
            InputManager.Get().DoEndTurnButton();
        }

        Entity EnemyHero { get { var p = GS.GetOpposingSidePlayer(); return p == null ? null : p.GetHero(); } }

        // minions of yours the game lets attack the enemy hero now (in its options' order)
        List<Entity> FaceAttackers()
        {
            var list = new List<Entity>();
            var gs = GS;
            var hero = EnemyHero;
            var packet = gs.GetOptionsPacket();
            if (hero == null || packet == null || packet.List == null) return list;
            foreach (var o in packet.List)
            {
                if (o == null || o.Type != Network.Options.Option.OptionType.POWER || o.Main == null || !o.Main.IsValidTarget(hero.GetEntityId())) continue;
                var e = gs.GetEntity(o.Main.ID);
                if (e != null && e.IsMinion() && e.IsControlledByFriendlySidePlayer() && e.GetZone() == TAG_ZONE.PLAY && !e.GetRealTimeTitanAbilityUsable()) list.Add(e);
            }
            return list;
        }

        string FaceProblem()
        {
            var hero = EnemyHero;
            if (hero == null || !CanBeAttacked(hero)) return Speech.S(K.GAMEPLAY_SEND_TO_FACE_HERO_CANT_BE_ATTACKED);
            if (!CanBeTargetedByOpponents(hero)) return Speech.S(K.GAMEPLAY_SEND_TO_FACE_HERO_CANT_BE_TARGETED);
            return null;
        }

        // from the game's own tags (the plain game has no such helpers)
        static bool CanBeAttacked(Entity e) { return !e.HasTag(GAME_TAG.CANT_BE_ATTACKED); }

        static bool CanBeTargetedByOpponents(Entity e) { return !e.HasTag(GAME_TAG.CANT_BE_TARGETED_BY_OPPONENTS) && !e.IsStealthed(); }

        void MinionToFace(Card card)
        {
            if (card == null) return;
            var e = card.GetEntity();
            if (e == null || !e.IsControlledByFriendlySidePlayer() || e.GetZone() != TAG_ZONE.PLAY) { Say(Speech.S(K.GAMEPLAY_SEND_TO_FACE_NOT_FRIENDLY_MINION)); return; }
            if (!e.IsMinion()) { Say(Speech.S(K.GAMEPLAY_SEND_TO_FACE_NOT_MINION)); return; }
            var problem = FaceProblem();
            if (problem != null) { Say(problem); return; }
            if (!FaceAttackers().Contains(e)) { Say(Speech.S(K.GAMEPLAY_SEND_TO_FACE_NOT_VALID_ATTACKER)); return; }
            Jobs.Run(Attack(new List<Entity> { e }));
        }

        void AllToFace()
        {
            var me = GS.GetFriendlySidePlayer();
            if (me.GetBattlefieldZone().GetCards().Count == 0) { Say(Speech.S(K.GAMEPLAY_SEND_TO_FACE_NO_MINIONS)); return; }
            var problem = FaceProblem();
            if (problem != null) { Say(problem); return; }
            if (FaceAttackers().Count == 0) { Say(Speech.S(K.GAMEPLAY_SEND_TO_FACE_HERO_NO_VALID_ATTACKERS)); return; }
            Jobs.Run(Attack(null));
        }

        bool m_attackingFace;      // Shift+F / Ctrl+F at work: the game's target mode is ours to answer

        // each attacker (null: every one the game allows, asked again after each attack) at the enemy hero
        IEnumerator Attack(List<Entity> only)
        {
            m_attackingFace = true;
            m_mouseOwned = true;
            try
            {
                var e = AttackEach(only);
                while (e.MoveNext()) yield return e.Current;
            }
            finally { m_attackingFace = false; m_mouseOwned = false; }
        }

        IEnumerator AttackEach(List<Entity> only)
        {
            for (int guard = 0; guard < 10; guard++)
            {
                var gs = GS;
                if (gs == null || !gs.IsFriendlySidePlayerTurn() || gs.IsGameOver()) yield break;
                // wait until the game takes input again (the last attack is done)
                float until = Time.unscaledTime + 6f;
                while (Time.unscaledTime < until && !(gs.IsInMainOptionMode() && InputManager.Get().PermitDecisionMakingInput())) yield return null;
                var list = FaceAttackers();
                if (only != null) list = list.FindAll(only.Contains);
                if (list.Count == 0) yield break;
                var attacker = list[0];
                var hero = EnemyHero;
                Log.Info("match: " + attacker.GetName() + " attacks the hero");
                // as Hearthstone Access: a click on the minion, a moment, a click on the enemy hero
                var attackerCard = attacker.GetCard();
                var job = ClickCard(attackerCard, () => Ref.Call(InputManager.Get(), "HandleClickOnCardInBattlefield", attacker, true));
                while (job.MoveNext()) yield return job.Current;
                float wait = Time.unscaledTime + 1.5f;
                while (Time.unscaledTime < wait && (VirtualMouse.Clicking || !GS.IsInTargetMode())) yield return null;
                if (!GS.IsInTargetMode()) yield break;
                yield return new WaitForSecondsRealtime(0.1f);
                var heroCard = hero == null ? null : hero.GetCard();
                job = ClickCard(heroCard, () => Ref.Call(InputManager.Get(), "HandleClickOnCardInBattlefield", hero, true));
                while (job.MoveNext()) yield return job.Current;
                if (only != null) only.Remove(attacker);
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        // T on a hand card that allows it (Hearthstone Access): its question, then the card is clicked (picked
        // up); once held it goes over the deck action area, Enter confirms, Backspace cancels. Otherwise silent.
        void DeckAction(Card card)
        {
            var e = card == null ? null : card.GetEntity();
            if (e == null || e.GetZone() != TAG_ZONE.HAND || !DeckAllowed(e)) return;
            Say(DeckQuestion(e));
            m_deckPending = true;
            var im = InputManager.Get();
            if (im != null && im.GetMousedOverCard() != card && !card.IsMousedOver())
            {
                Say(Speech.S(K.GAMEPLAY_TRY_AGAIN));
                try { im.SetMousedOverCard(card); } catch (Exception ex) { Log.Error(ex); }
            }
            Log.Info("match: deck action " + e.GetName());
            Jobs.Run(ClickCard(card, () => Ref.Call(InputManager.Get(), "GrabCard", card.gameObject)));
        }
        #endregion
    }
}

namespace HSAEnhanced
{
    // the emotes (Space on your hero): the game's emote options as a menu; Enter says it, as clicking does
    class EmoteUI : Core.Screen
    {
        static EmoteUI s_open;
        Core.Menu m_menu;

        internal static bool IsOpen { get { return s_open != null; } }

        internal override bool Alive { get { var h = EmoteHandler.Get(); return h != null && h.AreEmotesActive(); } }

        internal static void Open()
        {
            var handler = EmoteHandler.Get();
            if (handler == null || GameState.Get() == null) { Core.Speech.Say(Str.Unavailable); return; }
            handler.ShowEmotes();
            if (!handler.AreEmotesActive()) { Core.Speech.Say(Str.Unavailable); return; }
            if (s_open != null) Core.Focus.Pop(s_open);
            s_open = new EmoteUI();
            s_open.Build(handler);
            Core.Focus.Push(s_open);
            s_open.Read();
        }

        void Build(EmoteHandler handler)
        {
            var menu = new Core.Menu(this, "", Close);
            menu.SpaceSelects = true;
            var options = Ref.Get<System.Collections.Generic.List<EmoteOption>>(handler, "m_availableEmotes");
            if (options != null)
                foreach (var o in options)
                {
                    if (o == null || !o.gameObject.activeInHierarchy) continue;
                    var option = o;
                    string label = o.m_Text == null ? null : Str.Clean(Ui.ShownText(o.m_Text.Text));
                    if (string.IsNullOrEmpty(label)) label = o.m_EmoteType.ToString();
                    menu.AddOption(label, () =>
                    {
                        if (handler.EmoteSpamBlocked()) { Core.Speech.Say(Str.Unavailable); return; }
                        Log.Info("match: emote " + option.m_EmoteType);
                        option.DoClick();
                        Close();
                    });
                }
            m_menu = menu;
        }

        void Close()
        {
            var handler = EmoteHandler.Get();
            if (handler != null && handler.AreEmotesActive()) handler.HideEmotes();
            Core.Focus.Pop(this);
            if (s_open == this) s_open = null;
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }

    // Space on the enemy hero: the game's squelch option (its current text: Squelch / Unsquelch) as a
    // one-option menu, as Hearthstone Access; Enter (or Space) chooses, Backspace closes
    class EnemyEmoteUI : Core.Screen
    {
        static EnemyEmoteUI s_open;
        Core.Menu m_menu;

        internal static bool IsOpen { get { return s_open != null; } }

        internal override bool Alive { get { var h = EnemyEmoteHandler.Get(); return h != null && h.AreEmotesActive(); } }

        internal static void Open()
        {
            var handler = EnemyEmoteHandler.Get();
            if (handler == null || GameState.Get() == null) { Core.Speech.Say(Str.Unavailable); return; }
            handler.ShowEmotes();
            if (!handler.AreEmotesActive()) { Core.Speech.Say(Str.Unavailable); return; }
            if (s_open != null) Core.Focus.Pop(s_open);
            s_open = new EnemyEmoteUI();
            s_open.Build(handler);
            Core.Focus.Push(s_open);
            s_open.Read();
        }

        void Build(EnemyEmoteHandler handler)
        {
            var menu = new Core.Menu(this, "", Close);
            menu.SpaceSelects = true;
            var emote = Ref.Get(handler, "m_emote1");
            var text = emote == null ? null : Ref.Get(emote, "m_SquelchEmoteText") as UberText;
            var label = text == null ? null : Str.Clean(Ui.ShownText(text.Text));
            if (string.IsNullOrEmpty(label)) label = Str.Word("GAMEPLAY_EMOTE_SQUELCH");
            menu.AddOption(label, () =>
            {
                Log.Info("match: squelch");
                if (emote != null) Ref.Invoke(handler, "DoSquelchClick", emote);
                Close();
            });
            m_menu = menu;
        }

        void Close()
        {
            var handler = EnemyEmoteHandler.Get();
            if (handler != null && handler.AreEmotesActive()) handler.HideEmotes();
            Core.Focus.Pop(this);
            if (s_open == this) s_open = null;
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
