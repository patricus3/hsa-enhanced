#if WITHOUT_HSA
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Without Hearthstone Access: the match screen (docs/combat-spec-2-actions.md, our own way).
    // Navigation and card reading are Combat's (part 1). Actions call the game directly, no mouse:
    // - Enter on a card: what the game allows for it now (play, attack, use), or why not;
    //   a minion or location from hand first asks where (Left/Right, Home/End, numbers; Ctrl+Up/Down
    //   for a card that can go to the opponent's side), Enter places it;
    // - target mode: "Choose a target", Tab / zone keys, Enter, Backspace cancels;
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
        enum Mode { Browse, Place, Choices, Mulligan }

        Mode m_mode;
        Entity m_placing;
        bool m_opposingSide;
        int m_slot;
        List<Card> m_list = new List<Card>();
        int m_at;
        int m_line;
        bool m_confirmEndTurn;
        GameState.ResponseMode? m_lastResponse;
        bool m_mulliganSeen, m_gameOverSaid, m_choicesShown;
        object m_choiceKey;

        internal override bool Alive { get { return Match.InMatch; } }

        static GameState GS { get { return GameState.Get(); } }

        #region Every frame: what the game is doing
        internal void Tick()
        {
            var gs = GS;
            if (gs == null) return;
            if (gs.IsGameOver()) { GameOver(); return; }

            // the mulligan
            if (gs.IsMulliganPhase() && MulliganCards() != null && !ChoiceCardMgr.Get().IsFriendlyShown())
            {
                if (!m_mulliganSeen && MulliganReady())
                {
                    m_mulliganSeen = true;
                    m_mode = Mode.Mulligan;
                    m_list = new List<Card>(MulliganCards());
                    m_at = 0; m_line = 0;
                    Say(Str.Join(Str.Word("GAMEPLAY_MULLIGAN_STARTING_HAND"), Str.Word("GAMEPLAY_MULLIGAN_SUBTITLE")));
                    ReadListItem();
                }
                return;
            }
            if (m_mode == Mode.Mulligan) { m_mode = Mode.Browse; m_list.Clear(); }

            // Choose One / Discover: a list of the cards the game shows
            var cards = ChoiceCardMgr.Get() == null ? null : ChoiceCardMgr.Get().GetFriendlyCards();
            // a Rewind choice is two buttons (Rewind / Keep) instead of cards on show: a choice all the same
            bool shown = cards != null && cards.Count > 0 && (ChoiceCardMgr.Get().IsFriendlyShown() || RewindUIManager.IsShowingRewindUI);
            if (shown && (!m_choicesShown || !Equals(m_choiceKey, cards[0])))
            {
                m_choicesShown = true;
                m_choiceKey = cards[0];
                m_mode = Mode.Choices;
                m_list = new List<Card>(cards);
                m_at = 0; m_line = 0;
                Say(RewindUIManager.IsShowingRewindUI ? Speech.S("ACCESSIBILITY_GAMEPLAY_QUERY_REWIND") : gs.IsInSubOptionMode() ? Str.Word("GAMEPLAY_CHOOSE_ONE") : ChoiceTitle());
                ReadListItem();
            }
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
                if ((response == GameState.ResponseMode.OPTION_TARGET || response == GameState.ResponseMode.OPTION_REVERSE_TARGET) && m_mode != Mode.Choices)
                    Say(Speech.S(K.GAMEPLAY_CHOOSE_TARGET));
                m_lastResponse = response;
            }
            if (m_mode == Mode.Place && (m_placing == null || !gs.IsInMainOptionMode() || m_placing.GetZone() != TAG_ZONE.HAND)) m_mode = Mode.Browse;
            if (m_confirmEndTurn && !gs.IsInMainOptionMode()) m_confirmEndTurn = false;
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

            // the end screen: Enter goes on
            var end = EndGameScreen.Get();
            if (gs.IsGameOver() || (end != null && end.m_hitbox != null && end.m_hitbox.gameObject.activeInHierarchy))
            {
                if (Keys.Enter.Pressed && end != null && end.m_hitbox != null && end.m_hitbox.gameObject.activeInHierarchy) { Log.Info("match: continue"); end.m_hitbox.TriggerRelease(); return true; }
                return Combat.StatusKeys(this);
            }
            if (m_mode == Mode.Mulligan) return MulliganKeys();
            if (m_mode == Mode.Choices && ChoiceKeys()) return true;
            if (m_mode == Mode.Place) return PlaceKeys();

            // the end-turn question: Enter or E ends the turn, any other key cancels it (and does nothing else)
            if (m_confirmEndTurn)
            {
                m_confirmEndTurn = false;
                if (Keys.Enter.Pressed || Bind.END_TURN.Pressed) { EndTurn(); return true; }
                return true;
            }

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
                if (Keys.Enter.Pressed) { Combat.ConfirmTarget(this, true); return true; }
                if (Keys.Back.Pressed) { Cancel(); return true; }
                return false;
            }
            if (Keys.Back.Pressed && gs.IsInChoiceMode()) return true;      // a choice cannot be backed out of

            if (!gs.IsInMainOptionMode()) return false;
            var card = Combat.FocusedCard;
            if (Keys.Enter.Pressed) { if (card != null) Act(card); return true; }
            // Space on your hero: the emotes, as clicking the hero opens them
            if (Keys.Space.Pressed && card != null && card.GetEntity() != null && card.GetEntity().IsHero() && card.GetEntity().IsControlledByFriendlySidePlayer())
            {
                EmoteUI.Open();
                return true;
            }
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

        static bool MoveKey()
        {
            return Bind.READ_NEXT_ITEM.Pressed || Bind.READ_PREV_ITEM.Pressed || Bind.READ_FIRST_ITEM.Pressed || Bind.READ_LAST_ITEM.Pressed || Keys.Number().HasValue;
        }

        static bool LineKey()
        {
            return Bind.READ_NEXT_LINE.Pressed || Bind.READ_PREV_LINE.Pressed || Bind.READ_CUR_LINE.Pressed || Bind.READ_TO_END.Pressed || Bind.READ_ORIGINAL_CARD_STATS.Pressed;
        }

        internal override string Help()
        {
            var gs = GS;
            if (gs == null) return "";
            if (m_mode == Mode.Choices && RewindUIManager.IsShowingRewindUI) return Speech.S("ACCESSIBILITY_GAMEPLAY_REWIND_MODE_HELP", Bind.REWIND.Name, Bind.REWIND_KEEP.Name);
            if (m_mode == Mode.Mulligan) return Speech.S(K.GAMEPLAY_MULLIGAN_HELP, Keys.Space.Name, Keys.Enter.Name);
            if (m_confirmEndTurn) return Speech.S(K.GAMEPLAY_CONFIRM_END_TURN_HELP, Keys.Enter.Name, Bind.END_TURN.Name);
            if (gs.IsInTargetMode()) return Speech.S(K.GAMEPLAY_CHOOSE_TARGET_HELP, Keys.Tab.Name, Keys.Enter.Name, Keys.Back.Name);
            if (gs.IsGameOver()) return Speech.S(K.PRESS_KEY_TO_CONTINUE, Keys.Enter.Name);
            return "";
        }

        internal override void Read() { }
        #endregion

        #region Acting on a card
        // what the game allows for the card now; why not, in the game's words, when nothing
        void Act(Card card)
        {
            var gs = GS;
            var e = card.GetEntity();
            if (e == null) return;
            if (MercBattle.OnEnter(card)) return;
            if (!gs.IsValidOption(e))
            {
                // as a click does: the game's error (the hero says it, the message is spoken by our hook)
                var type = PlayErrors.ErrorType.NONE;
                try { type = gs.GetErrorType(e); } catch { }
                if (type != PlayErrors.ErrorType.NONE)
                {
                    try { PlayErrors.DisplayPlayError(type, gs.GetErrorParam(e), e); return; }
                    catch (Exception ex) { Log.Error(ex); }
                }
                Say(WhyNot(e) ?? Str.Join(Str.Clean(e.GetName()), Str.Unavailable));
                return;
            }
            // the sounds a click makes: picking a card up from the hand, an attacker getting ready
            try
            {
                if (e.GetZone() == TAG_ZONE.HAND)
                    SoundManager.Get().LoadAndPlay((AssetReference)"FX_MinionSummon01_DrawFromHand_01.prefab:c8adc026a7f5d0a4cb0706627a980c58", card.gameObject);
                else if (e.GetZone() == TAG_ZONE.PLAY && e.IsCharacter() && !e.IsInteractableObject())
                    card.ActivateCharacterAttackEffects();
            }
            catch (Exception ex) { Log.Error(ex); }
            // from hand, a minion or location goes somewhere on the board: ask where first
            if (e.GetZone() == TAG_ZONE.HAND && (e.IsMinion() || e.IsLocation()))
            {
                m_placing = e;
                m_opposingSide = false;
                m_slot = Zone(false) == null ? 1 : Zone(false).GetCards().Count + 1;
                m_mode = Mode.Place;
                SayPlace();
                return;
            }
            Respond(e, GAME_TAG.TAG_NOT_SET);
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
            else if (Keys.Enter.Pressed) { Place(); return true; }
            else if (Keys.Back.Pressed) { m_mode = Mode.Browse; Say(Str.Clean(m_placing.GetName())); return true; }
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
        }

        void Place()
        {
            var e = m_placing;
            var zone = Zone(m_opposingSide);
            m_mode = Mode.Browse;
            if (e == null || zone == null) return;
            if (!m_opposingSide && zone.GetCards().Count >= 7) { Say(Str.Word("GAMEPLAY_PlayErrors_REQ_MINION_CAP")); return; }
            int pos = ZoneMgr.Get().PredictZonePosition(e, zone, m_slot);
            Log.Info("match: place " + e.GetName() + " at " + m_slot + (m_opposingSide ? " (opponent's side)" : ""));
            Combat.WhenReady(this, e, () =>
            {
                GS.SetSelectedOptionPosition(pos);
                if (!InputManager.Get().DoNetworkResponse(e, true, m_opposingSide ? GAME_TAG.DISGUISED : GAME_TAG.TAG_NOT_SET)) Log.Info("match: the game refused the placement");
            });
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
                if (button != null && button.gameObject.activeInHierarchy) button.TriggerRelease();
                return true;
            }
            if (ListKeys(Keys.Tab.Pressed && GS.IsInSubOptionMode())) return true;
            if (Keys.Enter.Pressed)
            {
                var e = m_list[m_at].GetEntity();
                Log.Info("match: chose " + e.GetName());
                // Rewind / Keep: the game's own button (its animation goes with it)
                if (RewindUIManager.IsShowingRewindUI)
                {
                    var hud = RewindUIManager.Get();
                    var button = hud == null ? null : Ref.Get<UIBButton>(hud, e.GetCardId() == RewindUIManager.REWIND_CHOICE_CARDID ? "m_rewindButton" : "m_keepButton");
                    if (button != null && button.gameObject.activeInHierarchy) { button.TriggerRelease(); return true; }
                }
                if (GS.IsInSubOptionMode()) InputManager.Get().HandleClickOnSubOption(e);
                else InputManager.Get().DoNetworkResponse(e);
                return true;
            }
            if (Keys.Back.Pressed && GS.IsInSubOptionMode()) { Cancel(); return true; }
            if (Keys.Tab.Pressed && !GS.IsInSubOptionMode())
            {
                var toggle = ChoiceCardMgr.Get().GetToggleButton();
                if (toggle != null && toggle.gameObject.activeInHierarchy) { toggle.TriggerRelease(); return true; }
            }
            if (Bind.REROLL_CHOICE.Pressed)
            {
                var reroll = UnityEngine.Object.FindObjectOfType<RerollUIManager>();
                var button = reroll == null ? null : Ref.Get(reroll, "m_rerollButton") as PegUIElement;
                if (button != null && button.gameObject.activeInHierarchy) { button.TriggerRelease(); return true; }
            }
            return Combat.StatusKeys(this) || Keys.Back.Pressed;
        }

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
            return button != null && button.gameObject.activeInHierarchy && cards != null && cards.Count > 0;
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
                var card = m_list[m_at];
                MulliganManager.Get().ToggleHoldState(card);
                int i = m_at;
                Jobs.Run(SayMarkLater(i));
                return true;
            }
            if (Keys.Enter.Pressed)
            {
                var button = Ref.Get(MulliganManager.Get(), "mulliganButton") as NormalButton;
                if (button != null && button.IsEnabled()) { Log.Info("match: mulligan confirmed"); button.TriggerRelease(); m_mode = Mode.Browse; m_list.Clear(); }
                return true;
            }
            if (Keys.Tab.Pressed) { m_at = (m_at + 1) % m_list.Count; ReadListItem(); return true; }
            if (Keys.ShiftTab.Pressed) { m_at = (m_at + m_list.Count - 1) % m_list.Count; ReadListItem(); return true; }
            if (ListKeys(false)) return true;
            if (Bind.READ_ANOMALIES.Pressed) return Combat.StatusKeys(this);
            return true;
        }

        IEnumerator SayMarkLater(int i)
        {
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
                if (e != null && e.IsMinion() && e.IsControlledByFriendlySidePlayer() && e.GetZone() == TAG_ZONE.PLAY && !e.HasUsableTitanAbilities()) list.Add(e);
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

        // each attacker (null: every one the game allows, asked again after each attack) at the enemy hero
        IEnumerator Attack(List<Entity> only)
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
                if (!InputManager.Get().DoNetworkResponse(attacker)) yield break;
                yield return null;
                if (GS.IsInTargetMode()) InputManager.Get().DoNetworkResponse(hero);
                if (only != null) only.Remove(attacker);
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        void DeckAction(Card card)
        {
            var e = card == null ? null : card.GetEntity();
            if (e == null || e.GetZone() != TAG_ZONE.HAND || !e.HasDeckAction() || e.IsPassable()) return;
            GAME_TAG keyword = e.IsForgeable() ? GAME_TAG.FORGE : e.IsPrepareable() ? GAME_TAG.PREPARE : GAME_TAG.TRADEABLE;
            Respond(e, keyword);
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

        internal override bool Alive { get { var h = EmoteHandler.Get(); return h != null && h.AreEmotesActive(); } }

        internal static void Open()
        {
            var handler = EmoteHandler.Get();
            if (handler == null || GameState.Get() == null || GameState.Get().IsBusy()) { Core.Speech.Say(Str.Unavailable); return; }
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
}

#endif
