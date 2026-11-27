using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // A Battlegrounds match on the match screen, laid out the way Hearthstone Access lays it out
    // (its keys, its questions, its announcements; the code is ours):
    //   the hero choice: Left / Right through the heroes, Up / Down their lines, Enter picks and then
    //     Enter confirms (Backspace goes back), R rerolls the hero (R or Enter again; Shift+R at
    //     once), A the reroll tokens, O the minion types and anomaly, E the time left;
    //   the recruit phase: the board keys as in any match (B your minions, G the minions for sale,
    //     C your hand, arrows, Up / Down lines, Tab), and:
    //     Enter on a minion for sale: "Buy ...?" (Enter); on your minion: "Sell ...?" (Enter);
    //       on a card in hand or the hero power: played as in any match;
    //     Space on your minion: move it (Left / Right, Home / End, numbers; Enter or Space places it);
    //     F freeze / unfreeze, R refresh, U upgrade, J a dark gift: asked first (the key again or
    //       Enter does it, any other key cancels), with Shift at once;
    //     Ctrl+Space / Ctrl+Enter: the focused minion's activated ability;
    //     T the tavern (its tier and Bob), P your hero power (Shift+P the opponent's), A your gold,
    //     E the time left, D your hero buddy, Q your trinkets (Shift+Q the opponent's);
    //   any time: N / M your next opponent's / your own stats (Shift: read in full), L the
    //     leaderboard (Left / Right the players, Up / Down their lines, Backspace leaves it),
    //     O the minion types and anomaly;
    //   announced: the recruit phase with your tier and gold, the minions in Bob's tavern after a
    //     refresh, the cards you get, gold spent and gained, the combat with your opponent and
    //     their board, who won it, players who died.
    static class BgPlay
    {
        enum Ask { None, Freeze, Unfreeze, Refresh, Upgrade, DarkGift, Buy, Sell, Reroll }

        static Ask s_ask;
        static HSAEnhanced.Core.Key? s_askKey;
        static Entity s_askEntity;

        static Entity s_moving;
        static int s_movePos;

        static bool s_board;
        static List<PlayerLeaderboardCard> s_tiles = new List<PlayerLeaderboardCard>();
        static int s_tileAt, s_tileLine;

        static GameState s_game;
        static List<Card> s_heroes = new List<Card>();
        static int s_heroAt, s_heroLine;
        static bool s_heroesSaid, s_confirming, s_chosen;
        static string s_readySaid;
        static int s_rerolling;
        static string s_rerollingId;

        static int s_phase;               // 0: none yet, 1: recruit, 2: combat
        static int s_gold = -1;
        static HashSet<int> s_tavern = new HashSet<int>(), s_hand = new HashSet<int>();
        static int s_bob, s_opponent;
        static float s_opponentSince;
        static bool s_combatSaid, s_combatWon;
        static int s_alive;

        internal static bool Active
        {
            get
            {
                var mgr = GameMgr.Get();
                return GameState.Get() != null && mgr != null && mgr.IsBattlegroundsMatchOrTutorial();
            }
        }

        static GameState GS { get { return GameState.Get(); } }
        static TB_BaconShop Shop { get { var gs = GS; return gs == null ? null : gs.GetGameEntity() as TB_BaconShop; } }
        static Player Me { get { return GS.GetFriendlySidePlayer(); } }
        static Player Them { get { return GS.GetOpposingSidePlayer(); } }

        internal static bool Combat { get { var s = Shop; return s != null && s.IsInBattlegroundsCombatPhase(); } }

        // the recruit phase, the player's to act in
        internal static bool Recruit
        {
            get
            {
                var s = Shop; var gs = GS;
                return s != null && !s.IsInBattlegroundsCombatPhase() && gs.IsFriendlySidePlayerTurn() && !gs.IsMulliganPhase();
            }
        }

        static string Name(Entity e) { return e == null ? "" : Str.Clean(e.GetName()); }

        #region Every frame
        internal static void Tick(MatchUI ui)
        {
            var gs = GS;
            if (gs == null) return;
            if (!ReferenceEquals(gs, s_game)) Reset(gs);
            try
            {
                Tutorial(ui);
                if (gs.IsMulliganPhase()) { HeroChoice(ui); return; }
                if (s_heroes.Count > 0) { s_heroes.Clear(); s_confirming = false; }
                Phases(ui);
                if (s_moving != null && (s_moving.GetZone() != TAG_ZONE.PLAY || !Recruit)) s_moving = null;
                if (s_ask != Ask.None && !Recruit && s_ask != Ask.Reroll) s_ask = Ask.None;
            }
            catch (Exception e) { Log.Error(e); }
        }

        static void Reset(GameState gs)
        {
            s_game = gs;
            s_ask = Ask.None; s_moving = null; s_board = false;
            s_heroes.Clear(); s_heroesSaid = false; s_confirming = false; s_chosen = false; s_readySaid = null; s_rerolling = 0;
            s_tutorialSaid.Clear(); s_tutorialText = null;
            s_phase = 0; s_gold = -1; s_tavern.Clear(); s_hand.Clear(); s_bob = 0; s_opponent = 0; s_combatSaid = false; s_alive = 0;
        }

        // the recruit phase and the combat, as they begin and go on
        static void Phases(MatchUI ui)
        {
            var shop = Shop;
            if (shop == null || GS.IsGameOver()) return;
            int phase = shop.IsInBattlegroundsCombatPhase() ? 2 : shop.IsShopPhase() ? 1 : 0;
            if (phase == 1 && !GS.IsFriendlySidePlayerTurn()) phase = 0;
            if (phase != s_phase && phase != 0)
            {
                if (phase == 1) RecruitStart(ui);
                else CombatStart(ui);
                s_phase = phase;
            }
            if (s_phase == 1 && phase == 1) RecruitGoesOn(ui);
            if (s_phase == 2 && phase == 2) CombatGoesOn(ui);
        }

        static void RecruitStart(MatchUI ui)
        {
            if (s_phase == 2 && !s_combatWon) ui.Say(Speech.S(K.GAMEPLAY_GAME_OVER_TIED));
            ui.Say(Str.Word(K.BATTLEGROUNDS_GAMEPLAY_RECRUIT_PHASE));
            int alive = Alive();
            if (s_alive > 0 && alive > 0 && alive < s_alive)
            {
                ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_COMBAT_PHASE_ENDED_N_PLAYERS_DIED, s_alive - alive));
                ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_COMBAT_PHASE_ENDED_N_PLAYERS_REMAINING, alive));
            }
            if (alive > 0) s_alive = alive;
            s_gold = Me.GetNumAvailableResources();
            ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_RECRUIT_PHASE_START_DESCRIBE, Me.GetTag(GAME_TAG.PLAYER_TECH_LEVEL), s_gold));
            s_tavern.Clear(); s_hand.Clear();
            foreach (var c in Me.GetHandZone().GetCards()) if (c != null && c.GetEntity() != null) s_hand.Add(c.GetEntity().GetEntityId());
            var hero = Them.GetHero();
            s_bob = hero == null ? 0 : hero.GetEntityId();
            NewInTavern(ui);
            Log.Info("battlegrounds: recruit, tier " + Me.GetTag(GAME_TAG.PLAYER_TECH_LEVEL) + ", gold " + s_gold);
        }

        static void RecruitGoesOn(MatchUI ui)
        {
            int gold = Me.GetNumAvailableResources();
            if (s_gold >= 0 && gold != s_gold)
                ui.Say(gold > s_gold ? Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_PLAYER_GOLD, gold) : Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_REMAINING_GOLD, gold));
            s_gold = gold;
            NewInTavern(ui);
            NewInHand(ui);
        }

        // "... appeared in Bob's Tavern": the minions that were not there before
        static void NewInTavern(MatchUI ui)
        {
            var names = new List<string>();
            var now = new HashSet<int>();
            foreach (var c in Them.GetBattlefieldZone().GetCards())
            {
                var e = c == null ? null : c.GetEntity();
                if (e == null) continue;
                now.Add(e.GetEntityId());
                if (!s_tavern.Contains(e.GetEntityId())) names.Add(Name(e));
            }
            s_tavern = now;
            if (names.Count > 0) ui.Say(Grouped(names, K.BATTLEGROUNDS_GAMEPLAY_DIFF_MOVEMENT_CARD_ADDED_TO_TAVERN, K.BATTLEGROUNDS_GAMEPLAY_DIFF_MOVEMENT_CARDS_ADDED_TO_TAVERN));
        }

        static void NewInHand(MatchUI ui)
        {
            var names = new List<string>();
            var now = new HashSet<int>();
            foreach (var c in Me.GetHandZone().GetCards())
            {
                var e = c == null ? null : c.GetEntity();
                if (e == null) continue;
                now.Add(e.GetEntityId());
                if (!s_hand.Contains(e.GetEntityId())) names.Add(Name(e));
            }
            s_hand = now;
            if (names.Count > 0) ui.Say(Grouped(names, K.BATTLEGROUNDS_GAMEPLAY_DIFF_MOVEMENT_CARD_ADDED_TO_HAND, K.BATTLEGROUNDS_GAMEPLAY_DIFF_MOVEMENT_CARDS_ADDED_TO_HAND));
        }

        // one name: its own text ("1 {0}"); more: "1 X and 2 Y"
        static string Grouped(List<string> names, string one, string many)
        {
            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (var n in names) { if (!counts.ContainsKey(n)) { counts[n] = 0; order.Add(n); } counts[n]++; }
            if (order.Count == 1 && counts[order[0]] == 1) return Speech.S(one, order[0]);
            var parts = new List<string>();
            foreach (var n in order) parts.Add(counts[n] + " " + n);
            return Speech.S(many, Speech.HumanizeList(parts));
        }

        static void CombatStart(MatchUI ui)
        {
            s_ask = Ask.None; s_moving = null;
            s_combatSaid = false; s_combatWon = false;
            s_opponent = 0;
            ui.Say(Str.Word(K.BATTLEGROUNDS_GAMEPLAY_COMBAT_PHASE));
            Log.Info("battlegrounds: combat");
        }

        static void CombatGoesOn(MatchUI ui)
        {
            var hero = Them.GetHero();
            if (hero == null) return;
            // the opponent: once the hero facing us is no longer Bob and has settled
            if (!s_combatSaid)
            {
                if (hero.GetEntityId() == s_bob) return;
                if (hero.GetEntityId() != s_opponent) { s_opponent = hero.GetEntityId(); s_opponentSince = Time.unscaledTime; return; }
                if (Time.unscaledTime - s_opponentSince < 0.6f) return;
                s_combatSaid = true;
                DescribeOpponent(ui, hero);
                return;
            }
            // the end of the fight: the winner's hero gets an attack to strike with
            if (!s_combatWon)
            {
                var mine = Me.GetHero();
                if ((mine != null && mine.GetATK() > 0) || hero.GetATK() > 0)
                {
                    s_combatWon = true;
                    int me = Me.GetBattlefieldZone().GetCardCount(), them = Them.GetBattlefieldZone().GetCardCount();
                    if (me > them) ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_COMBAT_PHASE_ENDED_PLAYER_WON, me));
                    else if (them > me) ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_COMBAT_PHASE_ENDED_OPPONENT_WON, them));
                    else s_combatWon = false;
                }
            }
        }

        static void DescribeOpponent(MatchUI ui, Entity hero)
        {
            var minions = Them.GetBattlefieldZone().GetCards();
            int health = hero.GetCurrentHealth(), armor = hero.GetArmor(), tier = hero.GetRealTimePlayerTechLevel();
            if (health <= 0) ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_COMBAT_PHASE_START_DESCRIBE_AGAINST_DEAD_PLAYER, Name(hero)));
            else if (armor > 0) ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_COMBAT_PHASE_START_DESCRIBE_INCLUDE_ARMOR, tier, Name(hero), health, minions.Count, armor));
            else ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_COMBAT_PHASE_START_DESCRIBE, tier, Name(hero), health, minions.Count));
            var summaries = new List<string>();
            foreach (var c in minions)
            {
                var e = c == null ? null : c.GetEntity();
                if (e != null) summaries.Add(Str.Join(Name(e), Speech.S(K.READ_CARD_ATK_HEALTH, e.GetATK(), e.GetCurrentHealth())));
            }
            if (summaries.Count > 0) ui.Say(Speech.HumanizeList(summaries));
            Log.Info("battlegrounds: facing " + hero.GetName() + " with " + minions.Count + " minions");
        }
        #endregion

        #region The tutorial
        // The Battlegrounds tutorial's hints (bubbles) and dialogs (a headline, a text and a button
        // the tutorial waits for). Each hint is said once as it appears; a hint that tells the player
        // to drag a card is said with the keys instead, as Hearthstone Access does. Enter presses a
        // dialog's button.
        static readonly HashSet<int> s_tutorialSaid = new HashSet<int>();
        static string s_tutorialText;

        static readonly string[][] Overrides =
        {
            new[] { "GAMEPLAY_BACON_DRAGBUY_TUTORIAL", K.BATTLEGROUNDS_TUTORIAL_DRAGBUY_TUTORIAL_OVERRIDE, "G" },
            new[] { "GAMEPLAY_BACON_RECRUIT_REMINDER_TUTORIAL", K.BATTLEGROUNDS_TUTORIAL_DRAGBUY_TUTORIAL_OVERRIDE, "G" },
            new[] { "GAMEPLAY_BACON_DRAGBUY_BODY_TUTORIAL", K.BATTLEGROUNDS_TUTORIAL_DRAGBUY_BODY_TUTORIAL_OVERRIDE, "G" },
            new[] { "GAMEPLAY_BACON_DRAGSELL_TUTORIAL", K.BATTLEGROUNDS_TUTORIAL_DRAGSELL_TUTORIAL_OVERRIDE, "B" },
            new[] { "GAMEPLAY_BACON_DRAGSELL_BODY_TUTORIAL", K.BATTLEGROUNDS_TUTORIAL_DRAGSELL_BODY_TUTORIAL_OVERRIDE, "B" },
            new[] { "GAMEPLAY_BACON_REFRESH_TUTORIAL", K.BATTLEGROUNDS_TUTORIAL_REFRESH_TUTORIAL_OVERRIDE, "R" },
            new[] { "GAMEPLAY_BACON_REFRESH_UPGRADE_TUTORIAL", K.BATTLEGROUNDS_TUTORIAL_REFRESH_TUTORIAL_OVERRIDE, "R" },
            new[] { "GAMEPLAY_BACON_MINION_UPGRADE_TAVERN_TUTORIAL", K.BATTLEGROUNDS_TUTORIAL_MINION_UPGRADE_TAVERN_TUTORIAL_OVERRIDE, "U" },
            new[] { "GAMEPLAY_BACON_MINION_MOVE_TUTORIAL", K.BATTLEGROUNDS_TUTORIAL_MINION_MOVE_TUTORIAL_OVERRIDE, "M" },
        };

        static string KeyName(string which)
        {
            switch (which)
            {
                case "G": return Bind.SEE_OPPONENT_MINIONS.Name;
                case "B": return Bind.SEE_PLAYER_MINIONS.Name;
                case "R": return Bind.REFRESH_TAVERN.Name;
                case "U": return Bind.UPGRADE_TAVERN.Name;
                default: return Bind.SEE_PLAYER_MINIONS.Name;
            }
        }

        // the hint's text, or ours with the keys
        static string HintText(UberText text)
        {
            if (text == null) return null;
            var shown = text.Text;
            if (string.IsNullOrEmpty(shown)) return null;
            foreach (var o in Overrides)
            {
                string game = null;
                try { game = GameStrings.Get(o[0]); } catch { }
                if (game == null || game != shown) continue;
                var second = o[2] == "M" ? Keys.Space.Name : Keys.Enter.Name;
                return Speech.S(o[1], KeyName(o[2]), second);
            }
            return Str.Clean(shown);
        }

        static TutorialNotification TutorialDialog()
        {
            if (!(Shop is TB_BaconShop_Tutorial)) return null;
            var d = Ref.Get(Shop, "m_popupTutorialNotification") as TutorialNotification;
            return d != null && d && d.gameObject.activeInHierarchy && d.m_ButtonStart != null ? d : null;
        }

        static void SayDialog(MatchUI ui, TutorialNotification d)
        {
            ui.Say(Str.Clean(d.headlineUberText == null ? null : d.headlineUberText.Text));
            ui.Say(HintText(d.speechUberText));
            ui.Say(Speech.S(K.PRESS_KEY_TO_CONTINUE, Keys.Enter.Name));
        }

        static void Tutorial(MatchUI ui)
        {
            var shop = Shop as TB_BaconShop_Tutorial;
            if (shop == null) return;
            for (var t = shop.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (!typeof(Notification).IsAssignableFrom(f.FieldType)) continue;
                    var n = f.GetValue(shop) as Notification;
                    if (n == null || !n || !n.gameObject.activeInHierarchy) continue;
                    int id = n.GetInstanceID();
                    if (s_tutorialSaid.Contains(id)) continue;
                    var dialog = n as TutorialNotification;
                    if (dialog != null)
                    {
                        if (dialog.speechUberText == null || string.IsNullOrEmpty(dialog.speechUberText.Text)) continue;
                        s_tutorialSaid.Add(id);
                        s_tutorialText = Str.Join(Str.Clean(dialog.headlineUberText == null ? null : dialog.headlineUberText.Text), HintText(dialog.speechUberText));
                        Log.Info("battlegrounds: tutorial dialog " + f.Name);
                        SayDialog(ui, dialog);
                        continue;
                    }
                    var text = HintText(n.speechUberText);
                    if (string.IsNullOrEmpty(text)) continue;
                    s_tutorialSaid.Add(id);
                    s_tutorialText = text;
                    Log.Info("battlegrounds: tutorial hint " + f.Name);
                    ui.Say(text);
                }
        }
        #endregion

        #region The hero choice
        static MulliganManager Mulligan { get { return MulliganManager.Get(); } }

        // HSA reads the heroes where the game is done dealing them and waits for the player
        static bool HeroesReady()
        {
            var m = Mulligan;
            if (m == null || !Ref.Get<bool>(m, "m_waitingForUserInput")) return false;
            var cards = m.GetStartingCards();
            return cards != null && cards.Count > 0;
        }

        static void HeroChoice(MatchUI ui)
        {
            if (!GS.GetBooleanGameOption(GameEntityOption.MULLIGAN_IS_CHOOSE_ONE)) return;
            if (s_chosen) { Ready(ui, false); return; }
            if (!HeroesReady()) return;
            // the heroes: those that are not locked
            var now = new List<Card>();
            foreach (var c in Mulligan.GetStartingCards())
                if (c != null && c.GetEntity() != null && !c.GetEntity().HasTag(GAME_TAG.BACON_LOCKED_MULLIGAN_HERO)) now.Add(c);
            if (!s_heroesSaid)
            {
                s_heroesSaid = true;
                s_heroes = now; s_heroAt = 0; s_heroLine = 0;
                RacesAndAnomaly(ui);
                Tokens(ui, false);
                string banner = null, subtitle = null;
                try { banner = Str.Clean(GS.GetGameEntity().GetMulliganBannerText()); subtitle = Str.Clean(GS.GetGameEntity().GetMulliganBannerSubtitleText()); } catch { }
                ui.Say(string.IsNullOrEmpty(banner) ? Str.Word("GAMEPLAY_BACON_MULLIGAN_CHOOSE_HERO_BANNER") : banner);
                if (!string.IsNullOrEmpty(subtitle)) ui.Say(subtitle);
                SayHero(ui);
                Log.Info("battlegrounds: heroes " + string.Join(", ", s_heroes.ConvertAll(c => c.GetEntity().GetName()).ToArray()));
                return;
            }
            s_heroes = now;
            if (s_heroAt >= s_heroes.Count) s_heroAt = Math.Max(0, s_heroes.Count - 1);
            // a reroll answered: the new hero in its place
            if (s_rerolling != 0)
            {
                var e = GS.GetEntity(s_rerolling);
                if (e == null || e.GetCardId() != s_rerollingId) { s_rerolling = 0; s_heroLine = 0; SayHero(ui); }
            }
        }

        // "5 out of 8 players ready", when it changes
        static void Ready(MatchUI ui, bool force)
        {
            var shop = Shop;
            if (shop == null) return;
            object ready = null, all = null;
            try { ready = Ref.Invoke(shop, "CountPlayersFinishedMulligan"); all = Ref.Invoke(shop, "CountPlayersInGame"); } catch { }
            if (!(ready is int) || !(all is int)) return;
            var text = Speech.S(K.UI_BATTLEGROUNDS_MULLIGAN_PLAYERS_READY, (int)ready, (int)all);
            if (force || text != s_readySaid) ui.Say(text);
            s_readySaid = text;
        }

        static List<string> HeroLines(Card card)
        {
            var e = card.GetEntity();
            var lines = new List<string> { Name(e) };
            int armor = e.GetArmor();
            if (armor > 0) lines.Add(Speech.S(K.READ_HERO_CARD_ARMOR, armor));
            try
            {
                var id = GameUtils.GetHeroPowerCardIdFromHero(e.GetCardId());
                var def = string.IsNullOrEmpty(id) ? null : DefLoader.Get().GetEntityDef(id);
                if (def != null)
                {
                    lines.Add(Str.Join(Str.Word("GLOBAL_TUTORIAL_HERO_POWER"), Str.Clean(def.GetName())));
                    lines.Add(Speech.S(K.READ_CARD_COST, def.GetCost()));
                    lines.Add(Str.Clean(def.GetCardTextInHand()));
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            return lines;
        }

        static void SayHero(MatchUI ui)
        {
            if (s_heroAt < 0 || s_heroAt >= s_heroes.Count) return;
            var lines = HeroLines(s_heroes[s_heroAt]);
            ui.Say(Speech.S(K.MENU_OPTION_FORMAT, lines[0], s_heroAt + 1, s_heroes.Count));
        }

        static bool HeroKeys(MatchUI ui)
        {
            if (Bind.BATTLEGROUNDS_READ_RACES_IN_GAME.Pressed) { RacesAndAnomaly(ui); return true; }
            if (Bind.END_TURN.Pressed) { Seconds(ui, MulliganSeconds()); return true; }
            if (Bind.SEE_PLAYER_MANA.Pressed) { Tokens(ui, true); return true; }
            if (s_chosen) { if (Keys.Enter.Pressed) { Ready(ui, true); return true; } return false; }
            if (s_heroes.Count == 0 || !s_heroesSaid) return false;
            var card = s_heroes[Math.Min(s_heroAt, s_heroes.Count - 1)];
            // a reroll asked: R again or Enter rerolls, any other key cancels
            if (s_ask == Ask.Reroll)
            {
                if (Keys.Enter.Pressed || Bind.REFRESH_TAVERN.Pressed) { s_ask = Ask.None; Reroll(ui, card); return true; }
                if (!ShiftOnly()) s_ask = Ask.None;
            }
            if (Keys.Enter.Pressed)
            {
                if (s_confirming)
                {
                    Log.Info("battlegrounds: hero confirmed");
                    s_confirming = false; s_chosen = true;
                    Ref.Call(Mulligan, "OnMulliganButtonReleased", new object[] { null });
                    return true;
                }
                Log.Info("battlegrounds: hero " + card.GetEntity().GetName());
                Mulligan.ToggleHoldState(card);
                s_confirming = true;
                ui.Say(Speech.S(K.TUTORIAL_HOGGER_2_5, Keys.Enter.Name, Keys.Back.Name));
                return true;
            }
            if (Keys.Back.Pressed) { if (s_confirming) { s_confirming = false; SayHero(ui); } return true; }
            if (s_confirming) return false;
            if (Bind.REFRESH_TAVERN.Pressed) { AskReroll(ui, card); return true; }
            if (Bind.FORCE_REFRESH_TAVERN.Pressed) { Reroll(ui, card); return true; }
            int was = s_heroAt;
            if (Bind.READ_NEXT_ITEM.Pressed) s_heroAt = Math.Min(s_heroes.Count - 1, s_heroAt + 1);
            else if (Bind.READ_PREV_ITEM.Pressed) s_heroAt = Math.Max(0, s_heroAt - 1);
            else if (Bind.READ_FIRST_ITEM.Pressed) s_heroAt = 0;
            else if (Bind.READ_LAST_ITEM.Pressed) s_heroAt = s_heroes.Count - 1;
            else if (Keys.Number().HasValue) s_heroAt = Math.Min(s_heroes.Count - 1, Math.Max(0, Keys.Number().Value - 1));
            else
            {
                var lines = HeroLines(card);
                s_heroLine = Math.Min(s_heroLine, lines.Count - 1);
                if (Bind.READ_TO_END.Pressed) { for (int i = s_heroLine; i < lines.Count; i++) ui.Say(lines[i]); s_heroLine = lines.Count - 1; return true; }
                if (Bind.READ_CUR_LINE.Pressed) { ui.Say(lines[s_heroLine]); return true; }
                if (Bind.READ_NEXT_LINE.Pressed) { if (s_heroLine + 1 < lines.Count) ui.Say(lines[++s_heroLine]); return true; }
                if (Bind.READ_PREV_LINE.Pressed) { if (s_heroLine > 0) ui.Say(lines[--s_heroLine]); return true; }
                if (Bind.READ_TOOLTIP.Pressed) { CombatCards.SayKeywords(card.GetEntity()); return true; }
                return false;
            }
            if (s_heroAt != was) { s_heroLine = 0; SayHero(ui); }
            return true;
        }

        static bool RerollActive { get { var g = GS.GetGameEntity(); return g != null && g.HasTag(GAME_TAG.BACON_MULLIGAN_HERO_REROLL_ACTIVE); } }

        static void AskReroll(MatchUI ui, Card card)
        {
            if (!RerollActive) return;
            int cost = card.GetEntity().BaconFreeRerollLeft() > 0 ? 0 : 1;
            s_ask = Ask.Reroll;
            ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_REROLL_HERO, cost));
        }

        static void Reroll(MatchUI ui, Card card)
        {
            if (!RerollActive) return;
            var e = card.GetEntity();
            var can = e.ShouldEnableRerollButton();
            if (can <= Entity.RerollButtonEnableResult.UNLOCK)
            {
                Log.Info("battlegrounds: reroll " + e.GetName());
                s_rerolling = e.GetEntityId(); s_rerollingId = e.GetCardId();
                Mulligan.RequestHeroReroll(e);
                return;
            }
            string key = can == Entity.RerollButtonEnableResult.OUT_OF_CURRENCY ? "GLUE_BACON_TOOLTIP_REROLL_BUTTON_NOT_ENOUGH_CURRENCY_DESC"
                       : can == Entity.RerollButtonEnableResult.HERO_REROLL_LIMITATION_REACHED ? "GLUE_BACON_TOOLTIP_REROLL_BUTTON_LIMIT_REACHED_DESC"
                       : can == Entity.RerollButtonEnableResult.INSUFFICIENT_MULLIGAN_TIME_LEFT ? "GLUE_BACON_TOOLTIP_REROLL_BUTTON_INSUFFICIENT_MULLIGAN_TIME_DESC" : null;
            ui.Say(key == null ? Str.Unavailable : Str.Word(key));
        }

        static void Tokens(MatchUI ui, bool force)
        {
            if (!RerollActive) return;
            long tokens = 0;
            try { tokens = NetCache.Get().GetBattlegroundsTokenBalance(); } catch { }
            if (force || tokens > 0) ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_PLAYER_TOKENS, tokens));
        }

        static float MulliganSeconds()
        {
            var timer = Ref.Get(Mulligan, "m_mulliganTimer") as MulliganTimer;
            return timer == null ? 0 : timer.ComputeCountdownRemainingSec();
        }
        #endregion

        #region Keys
        // true: handled here (the match screen's own handling is skipped)
        internal static bool HandleKey(MatchUI ui)
        {
            var gs = GS;
            var dialog = TutorialDialog();
            if (dialog != null)
            {
                if (Keys.Enter.Pressed || Keys.Space.Pressed)
                {
                    Log.Info("battlegrounds: tutorial dialog continue");
                    Core.Click.Peg(dialog.m_ButtonStart);
                    return true;
                }
                if (Bind.READ_CUR_LINE.Pressed || Bind.READ_NEXT_LINE.Pressed || Bind.READ_PREV_LINE.Pressed) { SayDialog(ui, dialog); return true; }
            }
            if (gs.IsMulliganPhase()) return HeroKeys(ui);
            if (s_board) return BoardKeys(ui);
            if (s_moving != null) return MoveKeys(ui);

            // a question asked: the key again or Enter does it; Backspace cancels; any other key
            // cancels and does what it does (Shift alone keeps it)
            if (s_ask != Ask.None)
            {
                if (Keys.Enter.Pressed || (s_askKey.HasValue && s_askKey.Value.Pressed)) { var ask = s_ask; s_ask = Ask.None; Do(ui, ask, s_askEntity); return true; }
                if (ShiftOnly()) return true;
                s_ask = Ask.None;
                if (Keys.Back.Pressed) return true;
            }

            // any time
            if (Bind.BATTLEGROUNDS_READ_NEXT_OPPONENT_STATS.Pressed) { OpenBoard(ui, NextOpponent()); return true; }
            if (Bind.BATTLEGROUNDS_READ_MY_STATS.Pressed) { OpenBoard(ui, MyTile()); return true; }
            if (Bind.BATTLEGROUNDS_READ_NEXT_OPPONENT_STATS_TO_END.Pressed) { var t = NextOpponent(); if (t != null) foreach (var l in TileLines(t)) ui.Say(l); return true; }
            if (Bind.BATTLEGROUNDS_READ_MY_STATS_TO_END.Pressed) { var t = MyTile(); if (t != null) foreach (var l in TileLines(t)) ui.Say(l); return true; }
            if (Bind.BATTLEGROUNDS_READ_LEADERBOARD.Pressed) { OpenBoard(ui, null); return true; }
            if (Bind.BATTLEGROUNDS_READ_RACES_IN_GAME.Pressed) { RacesAndAnomaly(ui); return true; }
            if (Bind.BATTLEGROUNDS_READ_HERO_BUDDY.Pressed) { Buddy(ui); return true; }
            if (Bind.BATTLEGROUNDS_READ_TRINKETS.Pressed) { Trinkets(ui, Player.Side.FRIENDLY); return true; }
            if (Bind.BATTLEGROUNDS_READ_OPPONENT_TRINKETS.Pressed) { Trinkets(ui, Player.Side.OPPOSING); return true; }
            if (Bind.SEE_PLAYER_MANA.Pressed) { Gold(ui); return true; }
            if (Bind.END_TURN.Pressed || Bind.FORCE_END_TURN.Pressed) { Seconds(ui, TurnTimer.Get() == null ? 0 : TurnTimer.Get().ComputeCountdownRemainingSec()); return true; }
            if (Bind.BATTLEGROUNDS_SEE_PLAYER_HERO_POWER.Pressed) { HeroPower(ui, true); return true; }
            if (Bind.BATTLEGROUNDS_SEE_OPPONENT_HERO_POWER.Pressed) { if (Combat) HeroPower(ui, false); return true; }
            if (Bind.SEE_TAVERN.Pressed) { var h = Them.GetHeroCard(); if (h != null) HSAEnhanced.Combat.FocusCard(ui, h); return true; }

            if (!Recruit) return Combat && (Bind.REFRESH_TAVERN.Pressed || Bind.UPGRADE_TAVERN.Pressed || Bind.BATTLEGROUNDS_BUY_DARK_GIFT.Pressed);
            if (!gs.IsInMainOptionMode()) return false;

            // the tavern's buttons
            if (Bind.FREEZE_TAVERN.Pressed) { AskButton(ui, Frozen() ? Ask.Unfreeze : Ask.Freeze, Bind.FREEZE_TAVERN); return true; }
            if (Bind.FORCE_FREEZE_TAVERN.Pressed) { Do(ui, Ask.Freeze, null); return true; }
            if (Bind.REFRESH_TAVERN.Pressed) { AskButton(ui, Ask.Refresh, Bind.REFRESH_TAVERN); return true; }
            if (Bind.FORCE_REFRESH_TAVERN.Pressed) { Do(ui, Ask.Refresh, null); return true; }
            if (Bind.UPGRADE_TAVERN.Pressed) { AskButton(ui, Ask.Upgrade, Bind.UPGRADE_TAVERN); return true; }
            if (Bind.FORCE_UPGRADE_TAVERN.Pressed) { Do(ui, Ask.Upgrade, null); return true; }
            if (Bind.BATTLEGROUNDS_BUY_DARK_GIFT.Pressed) { AskButton(ui, Ask.DarkGift, Bind.BATTLEGROUNDS_BUY_DARK_GIFT); return true; }
            if (Bind.BATTLEGROUNDS_FORCE_BUY_DARK_GIFT.Pressed) { Do(ui, Ask.DarkGift, null); return true; }

            var card = HSAEnhanced.Combat.FocusedCard;
            var e = card == null ? null : card.GetEntity();
            if (Bind.BATTLEGROUNDS_ACTIVATE_MINION.Pressed || Bind.BATTLEGROUNDS_ACTIVATE_MINION_ALT.Pressed)
            {
                if (e != null && e.IsControlledByFriendlySidePlayer() && e.GetZone() == TAG_ZONE.PLAY && e.HasTag(GAME_TAG.INTERACTABLE_OBJECT))
                {
                    Log.Info("battlegrounds: activate " + e.GetName());
                    var target = e;
                    HSAEnhanced.Combat.WhenReady(ui, e, () => { if (!InputManager.Get().DoNetworkResponse(target, true, GAME_TAG.INTERACTABLE_OBJECT)) ui.Say(Str.Unavailable); });
                }
                return true;
            }
            if (Keys.Space.Pressed && e != null && e.GetZone() == TAG_ZONE.PLAY && e.IsControlledByFriendlySidePlayer() && e.IsMinion())
            {
                if (Me.GetBattlefieldZone().GetCardCount() > 1) StartMove(ui, e);
                return true;
            }
            if (Keys.Enter.Pressed && e != null && e.GetZone() == TAG_ZONE.PLAY && e.IsControlledByFriendlySidePlayer() && e.IsMinion())
            {
                if (!gs.IsValidOption(e)) { ui.Act(card); return true; }
                s_ask = Ask.Sell; s_askKey = null; s_askEntity = e;
                ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_SELL_MINION, Name(e)));
                return true;
            }
            if (Keys.Enter.Pressed && e != null && e.GetZone() == TAG_ZONE.PLAY && !e.IsControlledByFriendlySidePlayer() && !e.IsHero())
            {
                if (!gs.IsValidOption(e)) { ui.Act(card); return true; }
                s_ask = Ask.Buy; s_askKey = null; s_askEntity = e;
                ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_BUY_MINION, Name(e)));
                return true;
            }
            // the tavern hero (Bob): nothing to do
            if (Keys.Enter.Pressed && e != null && e.IsHero() && !e.IsControlledByFriendlySidePlayer()) return true;
            return false;
        }

        static bool ShiftOnly()
        {
            return Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift);
        }
        #endregion

        #region Tavern actions
        static Card Button(Ask ask)
        {
            var shop = Shop;
            if (shop == null) return null;
            switch (ask)
            {
                case Ask.Freeze: case Ask.Unfreeze: return shop.GetFreezeButtonCard();
                case Ask.Refresh: return shop.GetRefreshButtonCard();
                case Ask.Upgrade: return shop.GetTavernUpgradeButtonCard();
                case Ask.DarkGift: return Ref.Invoke(shop, "GetGameModeButtonBySlot", 5) as Card;
            }
            return null;
        }

        // frozen: Bob's minions are
        static bool Frozen()
        {
            foreach (var c in Them.GetBattlefieldZone().GetCards())
                if (c != null && c.GetEntity() != null && c.GetEntity().IsFrozen()) return true;
            return false;
        }

        static void AskButton(MatchUI ui, Ask ask, HSAEnhanced.Core.Key key)
        {
            var button = Button(ask);
            var e = button == null ? null : button.GetEntity();
            if (e == null || !button.IsInputEnabled()) return;
            int cost = e.GetCost();
            string text;
            switch (ask)
            {
                case Ask.Freeze: text = cost == 0 ? Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_FREEZE_TAVERN) : Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_FREEZE_TAVERN_FOR_N_GOLD, cost); break;
                case Ask.Unfreeze: text = Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_UNFREEZE_TAVERN); break;
                case Ask.Refresh:
                    text = Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_REFRESH_TAVERN_FOR_N_GOLD, cost);
                    int free = e.GetTag(GAME_TAG.BACON_FREE_REFRESH_COUNT);
                    if (cost == 0 && free > 0)
                    {
                        try { text = Str.Join(Str.Clean(GameStrings.Format(GameStrings.GetKeywordTextKey(GAME_TAG.BACON_FREE_REFRESH_COUNT), free)), text); } catch { }
                    }
                    break;
                case Ask.Upgrade: text = Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_UPGRADE_TAVERN_FOR_N_GOLD, cost); break;
                case Ask.DarkGift: text = Str.Join(Str.Clean(e.GetCardTextInHand()), Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_BUY_DARK_GIFT, cost)); break;
                default: return;
            }
            s_ask = ask; s_askKey = key; s_askEntity = e;
            ui.Say(text);
        }

        static void Do(MatchUI ui, Ask ask, Entity entity)
        {
            var gs = GS;
            switch (ask)
            {
                case Ask.Freeze: case Ask.Unfreeze: case Ask.Refresh: case Ask.Upgrade: case Ask.DarkGift:
                {
                    var button = Button(ask);
                    var e = button == null ? null : button.GetEntity();
                    if (e == null) return;
                    if (!gs.IsValidOption(e)) { ui.Act(button); return; }
                    Log.Info("battlegrounds: " + ask);
                    HSAEnhanced.Combat.WhenReady(ui, e, () => { if (!InputManager.Get().DoNetworkResponse(e)) ui.Say(Str.Unavailable); });
                    return;
                }
                case Ask.Buy:
                    if (entity == null || entity.GetZone() != TAG_ZONE.PLAY) return;
                    Log.Info("battlegrounds: buy " + entity.GetName());
                    Drop(ui, entity, false, Me.GetBattlefieldZone().GetCardCount() + 1);
                    return;
                case Ask.Sell:
                    if (entity == null || entity.GetZone() != TAG_ZONE.PLAY) return;
                    Log.Info("battlegrounds: sell " + entity.GetName());
                    Drop(ui, entity, true, Them.GetBattlefieldZone().GetCardCount() + 1);
                    return;
            }
        }

        // as the game's drop of a held minion: onto a side, at a slot (1: leftmost gap); a minion
        // that has a disguised action (the shop's) goes to the side it is dropped on, the opposing
        // side (Bob's) with the DISGUISED keyword: a sale
        static void Drop(MatchUI ui, Entity e, bool ontoBob, int slot)
        {
            var card = e.GetCard();
            var friendly = ZoneMgr.Get().FindZoneOfType<ZonePlay>(Player.Side.FRIENDLY);
            var bob = ZoneMgr.Get().FindZoneOfType<ZonePlay>(Player.Side.OPPOSING);
            Zone zone = e.IsControlledByFriendlySidePlayer() ? friendly : bob;
            bool disguised = e.HasDisguisedAction();
            if (disguised) zone = ontoBob ? bob : friendly;
            int pos;
            bool dragged = card != null && card.IsBeingDragged;
            try
            {
                if (card != null) card.IsBeingDragged = true;
                pos = ZoneMgr.Get().PredictZonePosition(e, zone, slot);
            }
            finally { if (card != null) card.IsBeingDragged = dragged; }
            var keyword = disguised && ontoBob ? GAME_TAG.DISGUISED : GAME_TAG.TAG_NOT_SET;
            HSAEnhanced.Combat.WhenReady(ui, e, () =>
            {
                GS.SetSelectedOptionPosition(pos);
                if (!InputManager.Get().DoNetworkResponse(e, true, keyword)) { GS.SetSelectedOptionPosition(0); ui.Say(Str.Join(Name(e), Str.Unavailable)); }
            });
        }
        #endregion

        #region Moving a minion
        static void StartMove(MatchUI ui, Entity e)
        {
            s_moving = e;
            var cards = Me.GetBattlefieldZone().GetCards();
            s_movePos = Math.Max(1, cards.FindIndex(c => c != null && c.GetEntity() == e) + 1);
            SayMove(ui);
        }

        // the other minions, in order: the moved one goes between them
        static List<Card> Others()
        {
            var list = new List<Card>(Me.GetBattlefieldZone().GetCards());
            list.RemoveAll(c => c == null || c.GetEntity() == null || c.GetEntity() == s_moving);
            return list;
        }

        static void SayMove(MatchUI ui)
        {
            var others = Others();
            int last = others.Count + 1;
            if (s_movePos >= last) ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_MOVE_MINION_TO_LAST_POSITION));
            else if (s_movePos <= 1) ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_MOVE_MINION_TO_FIRST_POSITION));
            else ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_QUERY_MOVE_MINION_BETWEEN, Name(others[s_movePos - 2].GetEntity()), Name(others[s_movePos - 1].GetEntity())));
        }

        static bool MoveKeys(MatchUI ui)
        {
            int last = Others().Count + 1, was = s_movePos;
            if (Bind.READ_NEXT_ITEM.Pressed) s_movePos = Math.Min(last, s_movePos + 1);
            else if (Bind.READ_PREV_ITEM.Pressed) s_movePos = Math.Max(1, s_movePos - 1);
            else if (Bind.READ_FIRST_ITEM.Pressed) { s_movePos = 1; SayMove(ui); return true; }
            else if (Bind.READ_LAST_ITEM.Pressed) { s_movePos = last; SayMove(ui); return true; }
            else if (Keys.Number().HasValue) { s_movePos = Math.Min(last, Math.Max(1, Keys.Number().Value)); SayMove(ui); return true; }
            else if (Keys.Enter.Pressed || Keys.Space.Pressed)
            {
                var e = s_moving; s_moving = null;
                var at = e.GetZonePosition();
                Log.Info("battlegrounds: move " + e.GetName() + " from " + at + " to " + s_movePos);
                if (at != s_movePos) Drop(ui, e, false, s_movePos);
                return true;
            }
            else if (Keys.Back.Pressed) { var e = s_moving; s_moving = null; ui.Say(Name(e)); return true; }
            else return true;
            if (s_movePos != was) SayMove(ui);
            return true;
        }
        #endregion

        #region Reading
        static void Gold(MatchUI ui)
        {
            int available = Me.GetNumAvailableResources(), total = Me.GetTag(GAME_TAG.RESOURCES);
            ui.Say(available != total ? Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_PLAYER_GOLD_CURRENT_AND_TOTAL, available, total) : Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_PLAYER_GOLD, available));
        }

        static void Seconds(MatchUI ui, float seconds)
        {
            if (seconds > 0) ui.Say(Speech.S(K.GAMEPLAY_N_SECONDS_REMAINING, (int)Math.Ceiling(seconds)));
        }

        static void HeroPower(MatchUI ui, bool friendly)
        {
            var powers = new List<Card>();
            foreach (var z in ZoneMgr.Get().FindZonesOfType<ZoneHeroPower>(friendly ? Player.Side.FRIENDLY : Player.Side.OPPOSING))
            {
                var c = z == null || z.GetCardCount() == 0 ? null : z.GetCardAtSlot(1);
                if (c != null && c.GetEntity() != null) powers.Add(c);
            }
            if (powers.Count == 0) { ui.Say(Speech.S(friendly ? K.GAMEPLAY_SEE_ZONE_PLAYER_HERO_POWER_EMPTY : K.GAMEPLAY_SEE_ZONE_OPPONENT_HERO_POWER_EMPTY)); return; }
            powers.Sort((a, b) => a.GetEntity().GetTag(GAME_TAG.ADDITIONAL_HERO_POWER_INDEX) - b.GetEntity().GetTag(GAME_TAG.ADDITIONAL_HERO_POWER_INDEX));
            HSAEnhanced.Combat.FocusCard(ui, powers[0]);
        }

        static void RacesAndAnomaly(MatchUI ui)
        {
            var races = new List<string>();
            try { foreach (var r in GS.GetAvailableRacesInBattlegroundsExcludingAmalgam()) races.Add(Str.Clean(GameStrings.GetRaceName(r))); } catch { }
            if (races.Count > 0) ui.Say(Str.Join(Str.Word("GAMEPLAY_BACON_MULLIGAN_AVAILABLE_RACES"), Speech.HumanizeList(races)));
            try
            {
                var zone = ZoneMgr.Get().FindZoneOfType<ZoneBattlegroundAnomaly>(Player.Side.NEUTRAL);
                var card = zone == null ? null : zone.GetFirstCard();
                if (card != null && card.GetEntity() != null) ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_ANOMALY, Name(card.GetEntity()), Str.Clean(card.GetEntity().GetCardTextInHand())));
            }
            catch (Exception e) { Log.Error(e); }
        }

        static void Buddy(MatchUI ui)
        {
            if (!GS.BattlegroundAllowBuddies()) return;
            var hero = Me.GetHero();
            if (hero == null) return;
            ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_PLAYER_BUDDY_METER, hero.GetTag(GAME_TAG.BACON_HERO_BUDDY_PROGRESS)));
            try
            {
                int buddy = hero.GetHeroBuddyCardId();
                var def = buddy == 0 ? null : DefLoader.Get().GetEntityDef(buddy);
                if (def == null) return;
                ui.Say(Str.Join(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_ZONE_HERO_BUDDY), Str.Clean(def.GetName())));
                ui.Say(Speech.S(K.READ_CARD_ATK_HEALTH, def.GetATK(), def.GetHealth()));
                ui.Say(Str.Clean(def.GetCardTextInHand()));
                if (def.GetTag(GAME_TAG.TECH_LEVEL) > 0) ui.Say(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_CARD_TIER, def.GetTag(GAME_TAG.TECH_LEVEL)));
            }
            catch (Exception e) { Log.Error(e); }
        }

        static void Trinkets(MatchUI ui, Player.Side side)
        {
            if (!GS.BattlegroundsAllowTrinkets()) return;
            var lines = new List<string>();
            foreach (var zone in ZoneMgr.Get().FindZonesOfType<ZoneBattlegroundTrinket>(side))
            {
                var card = zone == null ? null : zone.GetFirstCard();
                var e = card == null ? null : card.GetEntity();
                if (e != null) lines.Add(Str.Join(Name(e), Str.Clean(e.GetCardTextInHand())));
            }
            if (lines.Count == 0) return;
            ui.Say(Speech.S(side == Player.Side.FRIENDLY ? K.BATTLEGROUNDS_GAMEPLAY_ZONE_PLAYER_TRINKETS : K.BATTLEGROUNDS_GAMEPLAY_ZONE_OPPONENT_TRINKETS));
            foreach (var l in lines) ui.Say(l);
        }

        // a card's lines in Battlegrounds: Bob is the tavern (its tier); a hero its tier after its
        // name; a minion or tavern spell its tier
        internal static void AdjustLines(Card card, Entity e, List<string> lines)
        {
            if (!Active || e == null) return;
            if (e.IsHero())
            {
                if (Recruit && !e.IsControlledByFriendlySidePlayer())
                {
                    lines.Clear();
                    lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_CARD_TIER, Me.GetTag(GAME_TAG.PLAYER_TECH_LEVEL)));
                    lines.Add(Name(e));
                    return;
                }
                int tier = e.GetRealTimePlayerTechLevel();
                if (tier > 0) lines.Insert(Math.Min(1, lines.Count), Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_CARD_TIER, tier));
                return;
            }
            int cardTier = e.GetTag(GAME_TAG.TECH_LEVEL);
            if (cardTier > 0) lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_CARD_TIER, cardTier));
        }

        // the zone word for the opponent's minions: in the recruit phase they are for sale
        internal static string OpponentMinionsWord(bool empty)
        {
            if (!Active || !Recruit) return null;
            return Speech.S(empty ? K.BATTLEGROUNDS_GAMEPLAY_SEE_ZONE_MINIONS_FOR_SALE_EMPTY : K.BATTLEGROUNDS_GAMEPLAY_ZONE_MINIONS_FOR_SALE);
        }
        #endregion

        #region The leaderboard
        static List<PlayerLeaderboardCard> Tiles()
        {
            var list = new List<PlayerLeaderboardCard>();
            var mgr = PlayerLeaderboardManager.Get();
            if (mgr == null) return list;
            foreach (var t in mgr.GetAllTiles()) if (t != null && t.Entity != null) list.Add(t);
            list.Sort((a, b) => Place(a) - Place(b));
            return list;
        }

        static int Place(PlayerLeaderboardCard t)
        {
            try { return t.Team == null ? 99 : t.Team.GetBestPlacement(); } catch { return 99; }
        }

        static int Alive()
        {
            int n = 0;
            foreach (var t in Tiles()) if (t.Entity.GetRealTimeRemainingHP() > 0) n++;
            return n;
        }

        static PlayerLeaderboardCard NextOpponent()
        {
            foreach (var t in Tiles()) if (t.GetNextOpponentState()) return t;
            return null;
        }

        static PlayerLeaderboardCard MyTile()
        {
            int me = GS.GetFriendlyPlayerId();
            foreach (var t in Tiles()) if (t.Entity.GetTag(GAME_TAG.PLAYER_ID) == me) return t;
            return null;
        }

        static List<string> TileLines(PlayerLeaderboardCard tile)
        {
            var lines = new List<string>();
            var hero = tile.Entity;
            int playerId = hero.GetTag(GAME_TAG.PLAYER_ID);
            string player = null;
            try { player = Str.Clean(GS.GetGameEntity().GetBestNameForPlayer(playerId)); } catch { }
            lines.Add(Str.Join(player, Name(hero)));
            int place = Place(tile);
            if (place > 0 && place < 99) lines.Add(Str.Clean(Str.Word("GAMEPLAY_END_OF_GAME_PLACE_" + place)).Replace("!", ""));
            lines.Add(Speech.S(K.READ_HERO_CARD_HEALTH, Math.Max(0, hero.GetCurrentHealth())));
            if (hero.GetArmor() > 0) lines.Add(Speech.S(K.READ_HERO_CARD_ARMOR, hero.GetArmor()));
            int tier = hero.GetRealTimePlayerTechLevel();
            if (tier > 0) lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_CARD_TIER, tier));
            lines.Add(Str.Join(Str.Word(K.BATTLEGROUNDS_GAMEPLAY_LEADERBOARD_STATS_PLAYER_TRIPLES_CREATED), hero.GetTag(GAME_TAG.PLAYER_TRIPLES).ToString()));
            try
            {
                var history = PlayerLeaderboardManager.Get().GetRecentCombatHistoryForPlayer(playerId);
                if (history != null && history.Count > 0)
                {
                    lines.Add(Str.Join(Str.Word("GAMEPLAY_BACON_WIN_STREAK_ICON"), history[history.Count - 1].winStreak.ToString()));
                    for (int i = history.Count - 1; i >= Math.Max(0, history.Count - 3); i--)
                    {
                        var c = history[i];
                        var opponent = PlayerLeaderboardManager.Get().GetTileForPlayerId(c.opponentId);
                        if (opponent == null) continue;
                        var name = Str.Clean(opponent.GetHeroName());
                        if (c.damageTarget == c.ownerId) lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_LEADERBOARDS_PLAYER_STATS_TOOK_DAMAGE, c.damage, name));
                        else if (c.damageTarget == c.opponentId) lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_LEADERBOARDS_PLAYER_STATS_DEALT_DAMAGE, c.damage, name));
                        else lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_LEADERBOARDS_PLAYER_STATS_TIED, name));
                    }
                }
            }
            catch (Exception e) { Log.Error(e); }
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            return lines;
        }

        static void OpenBoard(MatchUI ui, PlayerLeaderboardCard at)
        {
            s_tiles = Tiles();
            if (s_tiles.Count == 0) return;
            s_board = true;
            s_tileAt = at == null ? 0 : Math.Max(0, s_tiles.IndexOf(at));
            s_tileLine = 0;
            SayTile(ui);
        }

        static void SayTile(MatchUI ui)
        {
            var lines = TileLines(s_tiles[s_tileAt]);
            ui.Say(Speech.S(K.MENU_OPTION_FORMAT, lines.Count > 0 ? lines[0] : "", s_tileAt + 1, s_tiles.Count));
        }

        static bool BoardKeys(MatchUI ui)
        {
            if (Keys.Back.Pressed || Bind.BATTLEGROUNDS_READ_LEADERBOARD.Pressed) { s_board = false; return true; }
            if (s_tiles.Count == 0) { s_board = false; return false; }
            int was = s_tileAt;
            if (Bind.READ_NEXT_ITEM.Pressed) s_tileAt = Math.Min(s_tiles.Count - 1, s_tileAt + 1);
            else if (Bind.READ_PREV_ITEM.Pressed) s_tileAt = Math.Max(0, s_tileAt - 1);
            else if (Bind.READ_FIRST_ITEM.Pressed) s_tileAt = 0;
            else if (Bind.READ_LAST_ITEM.Pressed) s_tileAt = s_tiles.Count - 1;
            else if (Keys.Number().HasValue) s_tileAt = Math.Min(s_tiles.Count - 1, Math.Max(0, Keys.Number().Value - 1));
            else
            {
                var lines = TileLines(s_tiles[s_tileAt]);
                if (lines.Count == 0) return true;
                s_tileLine = Math.Min(s_tileLine, lines.Count - 1);
                if (Bind.READ_TO_END.Pressed) { for (int i = s_tileLine; i < lines.Count; i++) ui.Say(lines[i]); s_tileLine = lines.Count - 1; return true; }
                if (Bind.READ_CUR_LINE.Pressed) { ui.Say(lines[s_tileLine]); return true; }
                if (Bind.READ_NEXT_LINE.Pressed) { if (s_tileLine + 1 < lines.Count) ui.Say(lines[++s_tileLine]); return true; }
                if (Bind.READ_PREV_LINE.Pressed) { if (s_tileLine > 0) ui.Say(lines[--s_tileLine]); return true; }
                // another key: the leaderboard is left and the key does what it does
                s_board = false;
                return false;
            }
            if (s_tileAt != was) { s_tileLine = 0; SayTile(ui); }
            return true;
        }
        #endregion

        internal static string Help()
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(s_tutorialText)) lines.Add(s_tutorialText);
            if (GS.IsMulliganPhase())
            {
                lines.Add(Speech.S(K.DUELS_CHOOSE_HERO_POWER_HELP, Keys.Enter.Name));
                if (RerollActive) lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_HERO_REROLL_HELP, Bind.REFRESH_TAVERN.Name));
                lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_RACES_IN_GAME_HELP, Bind.BATTLEGROUNDS_READ_RACES_IN_GAME.Name));
                return string.Join(" ", lines.ToArray());
            }
            lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_PLAYER_GOLD_HELP, Bind.SEE_PLAYER_MANA.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_TAVERN_INFORMATION_HELP, Bind.SEE_TAVERN.Name));
            if (GS.BattlegroundAllowBuddies()) lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_HERO_BUDDY_HELP, Bind.BATTLEGROUNDS_READ_HERO_BUDDY.Name));
            if (GS.BattlegroundsAllowTrinkets()) lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_PLAYER_TRINKETS_HELP, Bind.BATTLEGROUNDS_READ_TRINKETS.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_NEXT_OPPONENT_STATS_HELP, Bind.BATTLEGROUNDS_READ_NEXT_OPPONENT_STATS.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_MY_STATS_HELP, Bind.BATTLEGROUNDS_READ_MY_STATS.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_LEADERBOARD_HELP, Bind.BATTLEGROUNDS_READ_LEADERBOARD.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_GAMEPLAY_READ_RACES_IN_GAME_HELP, Bind.BATTLEGROUNDS_READ_RACES_IN_GAME.Name));
            lines.Add(Speech.S(K.GAMEPLAY_READ_PLAYER_HERO_POWER_HELP, Bind.BATTLEGROUNDS_SEE_PLAYER_HERO_POWER.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_TUTORIAL_READ_TIME_REMAINING, Bind.END_TURN.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_TUTORIAL_MINION_UPGRADE_TAVERN_TUTORIAL_OVERRIDE, Bind.UPGRADE_TAVERN.Name, Keys.Enter.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_TUTORIAL_REFRESH_TUTORIAL_OVERRIDE, Bind.REFRESH_TAVERN.Name, Keys.Enter.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_TUTORIAL_MINION_MOVE_TUTORIAL_OVERRIDE, Bind.SEE_PLAYER_MINIONS.Name, Keys.Space.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_TUTORIAL_DRAGBUY_TUTORIAL_OVERRIDE, Bind.SEE_OPPONENT_MINIONS.Name, Keys.Enter.Name));
            lines.Add(Speech.S(K.BATTLEGROUNDS_TUTORIAL_DRAGSELL_TUTORIAL_OVERRIDE, Bind.SEE_PLAYER_MINIONS.Name, Keys.Enter.Name));
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            return string.Join(" ", lines.ToArray());
        }
    }
}
