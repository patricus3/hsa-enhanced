using System;
using System.Collections.Generic;
using Accessibility;
using UnityEngine;

namespace HSAEnhanced
{
    // Our own navigation and card reading in a match (docs/combat-spec-1-navigation-reading.md, with our
    // decisions); HSA's navigation and reading are no longer used. It takes over HSA's handlers for the zone keys, the
    // arrows, Home / End and numbers, Tab, the card lines and the information keys. Playing, attacking and
    // targeting stay HSA's for now (part 2): HSA's idea of the focused card is kept on ours, so its Enter
    // acts on the card we read and its pointer follows it (the game's hover: big card, tooltips).
    // Ours: Tab keeps the focus when nothing is valid; Shift+C browses the opponent's hand; I and K read
    // the game's data. Battlegrounds and Mercenaries keep HSA's (and ours) as before.
    static class Combat
    {
        enum Kind { Hand, Minions, Secrets, Hero, HeroPower, Weapon }

        static Card s_card;
        static Kind s_kind;
        static bool s_friendly;
        static bool s_hasZone;
        static List<string> s_lines = new List<string>();
        static int s_line;

        internal static bool Enabled
        {
            get
            {
                if (!Settings.OwnCombat) return false;
                var gs = GameState.Get();
                if (gs == null || !gs.IsGameCreated()) return false;
                var mgr = GameMgr.Get();
                if (mgr != null && (mgr.IsBattlegrounds() || mgr.IsMercenaries())) return false;
                return true;
            }
        }

        static void Say(object gameplay, string text)
        {
            if (!string.IsNullOrEmpty(text)) AccessibilityMgr.Output(gameplay as AccessibleComponent, text);
        }

        static string L(LocalizationKey key) { return LocalizationUtils.Get(key); }
        static string F(LocalizationKey key, params object[] args) { return LocalizationUtils.Format(key, args); }

        static Player Side(bool friendly)
        {
            var gs = GameState.Get();
            return friendly ? gs.GetFriendlySidePlayer() : gs.GetOpposingSidePlayer();
        }

        #region Zones
        static List<Card> Cards(Kind kind, bool friendly)
        {
            var list = new List<Card>();
            var p = Side(friendly);
            if (p == null) return list;
            switch (kind)
            {
                case Kind.Hand: list.AddRange(p.GetHandZone().GetCards()); break;
                case Kind.Minions: list.AddRange(p.GetBattlefieldZone().GetCards()); break;
                case Kind.Secrets: list.AddRange(p.GetSecretZone().GetCards()); break;
                case Kind.Hero: { var h = p.GetHeroCard(); if (h != null) list.Add(h); break; }
                case Kind.Weapon: { var w = p.GetWeaponCard(); if (w != null) list.Add(w); break; }
                case Kind.HeroPower:
                    var powers = new List<Card>();
                    foreach (var z in ZoneMgr.Get().FindZonesOfType<ZoneHeroPower>(friendly ? Player.Side.FRIENDLY : Player.Side.OPPOSING))
                    {
                        var c = z == null || z.GetCardCount() == 0 ? null : z.GetCardAtSlot(1);
                        if (c != null) powers.Add(c);
                    }
                    powers.Sort((a, b) => a.GetEntity().GetTag(GAME_TAG.ADDITIONAL_HERO_POWER_INDEX) - b.GetEntity().GetTag(GAME_TAG.ADDITIONAL_HERO_POWER_INDEX));
                    list.AddRange(powers);
                    break;
            }
            list.RemoveAll(c => c == null || c.GetEntity() == null);
            return list;
        }

        // which zone a card is in, as navigation sees it
        static bool KindOf(Card card, out Kind kind, out bool friendly)
        {
            kind = Kind.Hand; friendly = true;
            var e = card == null ? null : card.GetEntity();
            if (e == null) return false;
            friendly = e.IsControlledByFriendlySidePlayer();
            var z = card.GetZone();
            if (z is ZoneHand) kind = Kind.Hand;
            else if (z is ZonePlay) kind = Kind.Minions;
            else if (z is ZoneSecret) kind = Kind.Secrets;
            else if (z is ZoneHero) kind = Kind.Hero;
            else if (z is ZoneHeroPower) kind = Kind.HeroPower;
            else if (z is ZoneWeapon) kind = Kind.Weapon;
            else return false;
            return true;
        }

        static bool IsList(Kind kind, bool friendly)
        {
            if (kind == Kind.Hand || kind == Kind.Minions || kind == Kind.Secrets) return true;
            return kind == Kind.HeroPower && Cards(kind, friendly).Count > 1;
        }

        static string ZoneWord(Kind kind, bool friendly)
        {
            switch (kind)
            {
                case Kind.HeroPower: return L(friendly ? LocalizationKey.GAMEPLAY_ZONE_PLAYER_HERO_POWER : LocalizationKey.GAMEPLAY_ZONE_OPPONENT_HERO_POWER);
                case Kind.Weapon: return L(friendly ? LocalizationKey.GAMEPLAY_ZONE_PLAYER_WEAPON : LocalizationKey.GAMEPLAY_ZONE_OPPONENT_WEAPON);
                case Kind.Hand: return L(friendly ? LocalizationKey.GAMEPLAY_ZONE_PLAYER_HAND : LocalizationKey.GAMEPLAY_ZONE_OPPONENT_HAND);
                case Kind.Minions: return L(friendly ? LocalizationKey.GAMEPLAY_ZONE_PLAYER_MINIONS : LocalizationKey.GAMEPLAY_ZONE_OPPONENT_MINIONS);
                case Kind.Secrets: return L(friendly ? LocalizationKey.GAMEPLAY_ZONE_PLAYER_SECRETS : LocalizationKey.GAMEPLAY_ZONE_OPPONENT_SECRETS);
                default: return null;      // a hero's first line names it
            }
        }
        #endregion

        #region Focus
        // a card that is not the player's to see: the opponent's hidden hand cards
        static bool Hidden(Card card)
        {
            var e = card.GetEntity();
            return e == null || e.IsHidden() || string.IsNullOrEmpty(e.GetName());
        }

        static void Focus(object gameplay, Card card, bool forceZone)
        {
            Kind kind; bool friendly;
            if (!KindOf(card, out kind, out friendly)) return;
            bool zoneChanged = !s_hasZone || kind != s_kind || friendly != s_friendly;
            s_card = card; s_kind = kind; s_friendly = friendly; s_hasZone = true;
            s_lines = Hidden(card) ? new List<string> { CombatCards.HiddenName() } : CombatCards.Lines(card);
            s_line = 0;
            if (zoneChanged || forceZone) Say(gameplay, ZoneWord(kind, friendly));
            var first = s_lines.Count > 0 ? s_lines[0] : "";
            if (IsList(kind, friendly))
            {
                var cards = Cards(kind, friendly);
                first = F(LocalizationKey.MENU_OPTION_FORMAT, first, cards.IndexOf(card) + 1, cards.Count);
            }
            Say(gameplay, first);
            SyncHsa(gameplay, card);
        }

        // HSA's focused card follows ours: its Enter, targeting and pointer act on this card
        static void SyncHsa(object gameplay, Card card)
        {
            try
            {
                Ref.Set(gameplay, "m_cardBeingRead", AccessibleCard.CreateCard(gameplay as AccessibleComponent, card));
                Ref.Set(gameplay, "m_curZone", Ref.Invoke(card, "GetAccessibleZone"));
            }
            catch (Exception e) { Log.Error(e); }
        }

        static void Clear(object gameplay)
        {
            s_card = null; s_hasZone = false; s_lines.Clear();
            Ref.Invoke(gameplay, "StopReadingCard", true);
        }

        // the focused card left its zone (played, died, bounced, stolen): the focus goes, silently
        static void CheckFocus(object gameplay)
        {
            if (s_card == null) return;
            Kind kind; bool friendly;
            if (!KindOf(s_card, out kind, out friendly) || kind != s_kind || friendly != s_friendly) { s_card = null; s_hasZone = false; s_lines.Clear(); }
        }

        static void FocusZone(object gameplay, Kind kind, bool friendly, LocalizationKey empty, bool force)
        {
            var cards = Cards(kind, friendly);
            if (cards.Count == 0) { Say(gameplay, L(empty)); return; }
            Focus(gameplay, cards[0], force);
        }
        #endregion

        #region Hooks (start of HSA's handlers; true: ours handled it, HSA's is skipped)
        // zone keys (HSA's HandleZoneSelection); in target mode the hand and secrets only when the game lets
        // cards in hand be targeted
        internal static bool ZoneKeys(object gameplay, bool minionsAndHeroesOnly)
        {
            if (!Enabled) return false;
            CheckFocus(gameplay);
            if (!minionsAndHeroesOnly && AccessibleKey.SEE_PLAYER_HAND.IsPressed()) FocusZone(gameplay, Kind.Hand, true, LocalizationKey.GAMEPLAY_SEE_ZONE_PLAYER_HAND_EMPTY, true);
            else if (!minionsAndHeroesOnly && AccessibleKey.SEE_PLAYER_SECRETS.IsPressed()) FocusZone(gameplay, Kind.Secrets, true, LocalizationKey.GAMEPLAY_SEE_ZONE_PLAYER_SECRETS_EMPTY, true);
            else if (!minionsAndHeroesOnly && AccessibleKey.SEE_OPPONENT_SECRETS.IsPressed()) FocusZone(gameplay, Kind.Secrets, false, LocalizationKey.GAMEPLAY_SEE_ZONE_OPPONENT_SECRETS_EMPTY, true);
            else if (AccessibleKey.SEE_PLAYER_MINIONS.IsPressed()) FocusZone(gameplay, Kind.Minions, true, LocalizationKey.GAMEPLAY_SEE_ZONE_PLAYER_MINIONS_EMPTY, true);
            else if (AccessibleKey.SEE_OPPONENT_MINIONS.IsPressed()) FocusZone(gameplay, Kind.Minions, false, LocalizationKey.GAMEPLAY_SEE_ZONE_OPPONENT_MINIONS_EMPTY, true);
            else if (AccessibleKey.SEE_OPPONENT_HERO.IsPressed()) FocusZone(gameplay, Kind.Hero, false, LocalizationKey.GAMEPLAY_SEE_ZONE_OPPONENT_MINIONS_EMPTY, false);
            else if (AccessibleKey.SEE_PLAYER_HERO.IsPressed()) FocusZone(gameplay, Kind.Hero, true, LocalizationKey.GAMEPLAY_SEE_ZONE_PLAYER_MINIONS_EMPTY, false);
            else if (AccessibleKey.SEE_PLAYER_HERO_POWER.IsPressed()) FocusZone(gameplay, Kind.HeroPower, true, LocalizationKey.GAMEPLAY_SEE_ZONE_PLAYER_HERO_POWER_EMPTY, true);
            else if (AccessibleKey.SEE_OPPONENT_HERO_POWER.IsPressed()) FocusZone(gameplay, Kind.HeroPower, false, LocalizationKey.GAMEPLAY_SEE_ZONE_OPPONENT_HERO_POWER_EMPTY, true);
            else if (AccessibleKey.SEE_PLAYER_WEAPON.IsPressed()) FocusZone(gameplay, Kind.Weapon, true, LocalizationKey.GAMEPLAY_SEE_ZONE_PLAYER_WEAPON_EMPTY, false);
            else if (AccessibleKey.SEE_OPPONENT_WEAPON.IsPressed()) FocusZone(gameplay, Kind.Weapon, false, LocalizationKey.GAMEPLAY_SEE_ZONE_OPPONENT_WEAPON_EMPTY, false);
            return true;
        }

        // arrows (no wrapping), Home / End, number keys within the current zone (HSA's HandleZoneInput)
        internal static bool ZoneMove(object gameplay)
        {
            if (!Enabled) return false;
            CheckFocus(gameplay);
            if (!s_hasZone) return true;
            var cards = Cards(s_kind, s_friendly);
            if (cards.Count == 0) return true;
            int at = s_card == null ? -1 : cards.IndexOf(s_card);
            int to = -1;
            if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) to = at + 1;
            else if (AccessibleKey.READ_PREV_ITEM.IsPressed()) to = at < 0 ? -1 : at - 1;
            else if (AccessibleKey.READ_FIRST_ITEM.IsPressed()) to = 0;
            else if (AccessibleKey.READ_LAST_ITEM.IsPressed()) to = cards.Count - 1;
            else
            {
                var n = AccessibleInputMgr.TryGetPressedNumKey();
                if (n.HasValue) to = (n.Value == 0 ? 10 : n.Value) - 1;
            }
            if (to >= 0 && to < cards.Count && to != at) Focus(gameplay, cards[to], false);
            return true;
        }

        // Tab / Shift+Tab: the valid items, wrapping (HSA's HandleValidOptionsSelectionInput); ours: with
        // nothing valid the focus stays
        internal static bool ValidItems(object gameplay)
        {
            if (!Enabled) return false;
            bool next = AccessibleKey.READ_NEXT_VALID_ITEM.IsPressed(), prev = !next && AccessibleKey.READ_PREV_VALID_ITEM.IsPressed();
            if (!next && !prev) return true;
            CheckFocus(gameplay);
            var candidates = new List<Card>();
            var gs = GameState.Get();
            Action<bool> side = friendly =>
            {
                if (friendly) candidates.AddRange(Cards(Kind.Hand, true));
                if (friendly) { candidates.AddRange(Cards(Kind.Weapon, true)); candidates.AddRange(Cards(Kind.Hero, true)); candidates.AddRange(Cards(Kind.HeroPower, true)); candidates.AddRange(Cards(Kind.Minions, true)); }
                else { candidates.AddRange(Cards(Kind.Minions, false)); candidates.AddRange(Cards(Kind.Hero, false)); candidates.AddRange(Cards(Kind.Weapon, false)); candidates.AddRange(Cards(Kind.HeroPower, false)); }
            };
            side(true); side(false);
            var valid = candidates.FindAll(c => IsValid(gs, c.GetEntity()));
            if (valid.Count == 0) { Say(gameplay, L(LocalizationKey.GAMEPLAY_NO_VALID_PLAYS)); return true; }
            if (valid.Count == 1) { Focus(gameplay, valid[0], true); return true; }
            int at = s_card == null ? -1 : candidates.IndexOf(s_card);
            if (at < 0) { Focus(gameplay, next ? valid[0] : valid[valid.Count - 1], false); return true; }
            for (int i = 1; i <= candidates.Count; i++)
            {
                int k = ((at + (next ? i : -i)) % candidates.Count + candidates.Count) % candidates.Count;
                if (valid.Contains(candidates[k])) { Focus(gameplay, candidates[k], false); return true; }
            }
            return true;
        }

        static bool IsValid(GameState gs, Entity e)
        {
            if (e == null) return false;
            try
            {
                if (gs.IsInTargetMode() || gs.GetResponseMode() == GameState.ResponseMode.OPTION_TARGET || gs.GetResponseMode() == GameState.ResponseMode.OPTION_REVERSE_TARGET) return gs.IsValidOptionTarget(e, true);
                if (gs.IsInSubOptionMode()) return gs.IsValidSubOption(e);
                if (gs.IsInChoiceMode()) return gs.IsChoice(e);
                if (gs.IsInMainOptionMode()) return gs.IsValidOption(e);
            }
            catch { }
            return false;
        }

        // the focused card's lines (HSA's HandleCardReadingInput): Down / Up, Shift+Up again, Shift+Down
        // to the end; K the printed stats and enchantments (ours: from the game's data)
        internal static bool CardLines(object gameplay)
        {
            if (!Enabled) return false;
            CheckFocus(gameplay);
            if (s_card == null) return true;
            if (AccessibleKey.READ_ORIGINAL_CARD_STATS.IsPressed())
            {
                foreach (var l in CombatCards.OriginalStats(s_card)) Say(gameplay, l);
                return true;
            }
            // the lines follow the card as it changes (damage, buffs)
            if (!Hidden(s_card)) s_lines = CombatCards.Lines(s_card);
            if (s_lines.Count == 0) return true;
            s_line = Math.Min(s_line, s_lines.Count - 1);
            if (AccessibleKey.READ_TO_END.IsPressed()) { for (int i = s_line; i < s_lines.Count; i++) Say(gameplay, s_lines[i]); s_line = s_lines.Count - 1; }
            else if (AccessibleKey.READ_CUR_LINE.IsPressed()) Say(gameplay, s_lines[s_line]);
            else if (AccessibleKey.READ_NEXT_LINE.IsPressed()) { if (s_line + 1 < s_lines.Count) Say(gameplay, s_lines[++s_line]); }
            else if (AccessibleKey.READ_PREV_LINE.IsPressed()) { if (s_line > 0) Say(gameplay, s_lines[--s_line]); }
            return true;
        }

        // A / Shift+A, D / Shift+D, Shift+C (ours: the opponent's hand, browsable), O (HSA's
        // HandleCheckStatusKeys). I stays HSA's (it runs before these handlers; its pointer follows our focus)
        internal static bool StatusKeys(object gameplay)
        {
            if (!Enabled) return false;
            var gs = GameState.Get();
            if (AccessibleKey.SEE_OPPONENT_MANA.IsPressed()) Mana(gameplay, false);
            else if (AccessibleKey.SEE_PLAYER_MANA.IsPressed()) Mana(gameplay, true);
            else if (AccessibleKey.SEE_OPPONENT_DECK.IsPressed()) Say(gameplay, F(LocalizationKey.GAMEPLAY_READ_OPPONENT_DECK, Side(false).GetDeckZone().GetCardCount()));
            else if (AccessibleKey.SEE_PLAYER_DECK.IsPressed()) Say(gameplay, F(LocalizationKey.GAMEPLAY_READ_PLAYER_DECK, Side(true).GetDeckZone().GetCardCount()));
            else if (AccessibleKey.SEE_OPPONENT_HAND.IsPressed())
            {
                var hand = Cards(Kind.Hand, false);
                Say(gameplay, F(LocalizationKey.GAMEPLAY_READ_OPPONENT_HAND, hand.Count));
                if (hand.Count > 0) { s_hasZone = false; Focus(gameplay, hand[0], false); }
            }
            else if (AccessibleKey.READ_ANOMALIES.IsPressed()) Anomalies(gameplay);
            else return false;     // other status keys stay HSA's
            return true;
        }
        #endregion

        // Enter on a target (HSA's HandleConfirmOrCancel with a target required): the target goes with the
        // game's option, as a click on it ends up (HSA clicked where its pointer was, which could still be
        // on the card before); Backspace stays HSA's cancel
        internal static bool ConfirmTarget(object gameplay, bool targetRequired)
        {
            if (!Enabled || !targetRequired || !AccessibleKey.CONFIRM.IsPressed()) return false;
            CheckFocus(gameplay);
            if (s_card == null) return true;
            var e = s_card.GetEntity();
            var gs = GameState.Get();
            if (e == null || !gs.IsValidOptionTarget(e, false)) { Say(gameplay, Str.Join(e == null ? null : Str.Clean(e.GetName()), Str.Unavailable)); return true; }
            WhenReady(gameplay, e, () =>
            {
                Log.Info("combat: target " + e.GetName());
                if (!InputManager.Get().DoNetworkResponse(e)) Say(gameplay, Str.Join(Str.Clean(e.GetName()), Str.Unavailable));
            });
            return true;
        }

        // An action of ours on the game (a target, a mercenary, an ability) waits, as a click would have to,
        // until the game takes input again (animations, cards still moving, HSA still speaking: the game's
        // own IsEntityInputEnabled); after a few seconds of waiting: "try again"
        internal static void WhenReady(object speaker, Entity e, Action act)
        {
            var gs = GameState.Get();
            if (gs != null && e != null && gs.IsEntityInputEnabled(e) && InputManager.Get().PermitDecisionMakingInput()) { act(); return; }
            FallbackWatcher.Run(WaitThen(speaker, e, act, ++s_waiting));
        }

        static int s_waiting;

        static System.Collections.IEnumerator WaitThen(object speaker, Entity e, Action act, int id)
        {
            float until = Time.unscaledTime + 4f;
            while (Time.unscaledTime < until)
            {
                yield return null;
                if (id != s_waiting) yield break;       // another action replaced this one
                var gs = GameState.Get();
                if (gs == null || e == null) yield break;
                if (gs.IsEntityInputEnabled(e) && InputManager.Get().PermitDecisionMakingInput()) { act(); yield break; }
            }
            if (id == s_waiting) { Log.Info("combat: the game did not take input in time"); Say(speaker, LocalizationUtils.Get(LocalizationKey.GAMEPLAY_TRY_AGAIN)); }
        }

        static void Mana(object gameplay, bool friendly)
        {
            var p = Side(friendly);
            int available = p.GetNumAvailableResources(), total = p.GetTag(GAME_TAG.RESOURCES);
            if (friendly) Say(gameplay, available != total ? F(LocalizationKey.GAMEPLAY_READ_PLAYER_MANA_CURRENT_AND_TOTAL, available, total) : F(LocalizationKey.GAMEPLAY_READ_PLAYER_MANA, available));
            else Say(gameplay, available != total ? F(LocalizationKey.GAMEPLAY_READ_OPPONENT_MANA_CURRENT_AND_TOTAL, available, total) : F(LocalizationKey.GAMEPLAY_READ_OPPONENT_MANA, available));
            int owed = p.GetTag(GAME_TAG.OVERLOAD_OWED);
            if (owed > 0) Say(gameplay, F(friendly ? LocalizationKey.GAMEPLAY_READ_PLAYER_OVERLOADED_MANA : LocalizationKey.GAMEPLAY_READ_OPPONENT_OVERLOADED_MANA, owed));
            int locked = p.GetTag(GAME_TAG.OVERLOAD_LOCKED);
            if (friendly && locked > 0) Say(gameplay, F(LocalizationKey.GAMEPLAY_READ_PLAYER_LOCKED_MANA, locked));
            try
            {
                if (CorpseCounter.ShouldShowCorpseCounter(p))
                {
                    int corpses = p.GetNumAvailableCorpses();
                    Say(gameplay, corpses > 0 ? F(LocalizationKey.GAMEPLAY_READ_CORPSES, corpses) : L(LocalizationKey.GAMEPLAY_READ_CORPSES_EMPTY));
                }
            }
            catch { }
        }

        static void Anomalies(object gameplay)
        {
            var said = false;
            try
            {
                // (HSA's addition to MulliganManager: the anomalies' entity ids)
                var get = typeof(MulliganManager).GetMethod("GetAnomalies", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var ids = get == null ? null : get.Invoke(null, null) as System.Collections.IEnumerable;
                if (ids != null)
                    foreach (var id in ids)
                    {
                        if (!(id is int)) continue;
                        var e = GameState.Get().GetEntity((int)id);
                        if (e == null) continue;
                        Say(gameplay, F(LocalizationKey.BATTLEGROUNDS_GAMEPLAY_READ_ANOMALY, Str.Clean(e.GetName()), Str.Clean(e.GetCardTextInHand())));
                        said = true;
                    }
            }
            catch (Exception ex) { Log.Error(ex); }
            if (!said) Say(gameplay, L(LocalizationKey.GAMEPLAY_NO_ANOMELIES));
        }
    }
}
