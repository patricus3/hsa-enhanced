using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // The way into a match, said as it happens (our own design, from the game's state):
    //   "Searching for an Opponent" while matchmaking; "Loading" when the match loads
    //   as Hearthstone Access: who you play against (games against players) and how many cards you start
    //     with when they are dealt;
    //     who goes first when the game shows its coin toss text (Match reads the starting hand once the game
    //     waits for the choice)
    //   after your mulligan: "Opponent is still choosing" while they do; the cards you drew instead
    //   "You get the coin" when it comes; and in any turn "10 seconds remaining" as the rope burns
    static class GameStart
    {
        static bool s_finding, s_loading;
        static GameState s_gs;
        static bool s_saidVs, s_saidFirst, s_saidWaiting, s_mulliganDone, s_saidCoin;
        static HashSet<int> s_startHand;
        static float s_mulliganEndedAt;
        static float s_coinAt = -1;
        static int s_lastRopeSecond = -1;

        static string A(string key, params object[] args) { return Speech.S("ACCESSIBILITY_" + key, args); }

        static void Say(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Log.Info("start: " + text);
            Speech.Say(text);
        }

        internal static void Tick()
        {
            // matchmaking and loading
            var mgr = GameMgr.Get();
            bool finding = mgr != null && mgr.IsFindingGame();
            if (finding && !s_finding) Say(Str.Word("PRESENCE_STATUS_PLAY_QUEUE"));
            s_finding = finding;
            var scenes = SceneMgr.Get();
            bool loading = scenes != null && scenes.GetNextMode() == SceneMgr.Mode.GAMEPLAY && scenes.GetMode() != SceneMgr.Mode.GAMEPLAY;
            if (loading && !s_loading) Say(A("GLOBAL_LOADING"));
            s_loading = loading;

            var gs = GameState.Get();
            if (gs != s_gs)
            {
                s_gs = gs;
                s_saidVs = s_saidFirst = s_saidWaiting = s_mulliganDone = s_saidCoin = false;
                s_coinAt = -1;
                Confirmed = false;
                s_startHand = null;
                s_lastRopeSecond = -1;
            }
            if (gs == null || !gs.IsGameCreated() || mgr == null || mgr.IsBattlegrounds() || mgr.IsMercenaries()) return;
            var me = gs.GetFriendlySidePlayer();
            var them = gs.GetOpposingSidePlayer();
            if (me == null || them == null) return;
            var mulligan = MulliganManager.Get();
            bool inMulligan = mulligan != null && (mulligan.IsMulliganActive() || mulligan.IsMulliganIntroActive());

            // who against, the starting hand, who goes first: once the starting cards are dealt
            var start = mulligan == null ? null : Ref.Get<List<Card>>(mulligan, "m_startingCards");
            if (!s_saidVs && start != null && start.Count > 0 && them.GetHero() != null)
            {
                s_saidVs = true;
                var hero = them.GetHero();
                string cls = null;
                try { cls = Str.Clean(GameStrings.GetClassName(hero.GetClass())); } catch { }
                string name = null;
                try { name = Str.Clean(them.GetName()); } catch { }
                if (string.IsNullOrEmpty(name) || mgr.IsAI()) name = Str.Clean(hero.GetName());
                // who you play against: in games against players only, as Hearthstone Access
                bool players = false;
                try { players = !mgr.IsAI(); } catch { }
                if (players) { var vs = A("GAMEPLAY_VS_PLAYER_ANNOUNCEMENT", name, cls); Say(vs); HistoryLog.Add(vs); }
                Say(A("GAMEPLAY_YOU_START_WITH_N_CARDS", start.Count));
                s_startHand = new HashSet<int>();
                foreach (var c in start) if (c != null && c.GetEntity() != null) s_startHand.Add(c.GetEntity().GetEntityId());
            }

            // who goes first: when the game shows its coin toss text (it fades in 1.8 s after it is made), as
            // Hearthstone Access said it
            if (!s_saidFirst && s_saidVs && mulligan != null)
            {
                var coin = Ref.Get<GameObject>(mulligan, "coinTossText");
                if (coin != null && s_coinAt < 0) s_coinAt = Time.unscaledTime;
                if (s_coinAt >= 0 && Time.unscaledTime - s_coinAt >= 1.8f)
                {
                    s_saidFirst = true;
                    Say(A(me.HasTag(GAME_TAG.FIRST_PLAYER) ? "GAMEPLAY_YOU_GO_FIRST" : "GAMEPLAY_OPPONENT_GOES_FIRST"));
                }
            }

            // your choice made and your new cards in, the opponent still choosing: only then does the game
            // show its "Opponent is still choosing..." (against the computer it rarely does)
            if (inMulligan && !s_saidWaiting && mulligan != null && s_saidVs && MulliganConfirmed()
                && Ref.Get(mulligan, "m_WaitForOpponentToFinishMulligan") != null && !mulligan.opponentPlayerHasReplacementCards)
            {
                s_saidWaiting = true;
                Say(A("GAMEPLAY_WAITING_FOR_OPPONENT"));
            }

            // the mulligan over: the cards drawn instead of the ones sent back (the coin is its own line)
            if (!inMulligan && s_saidVs && !s_mulliganDone)
            {
                s_mulliganDone = true;
                s_mulliganEndedAt = Time.unscaledTime;
                // who goes first, if the coin toss text never showed (a mode without it)
                if (!s_saidFirst)
                {
                    s_saidFirst = true;
                    Say(A(me.HasTag(GAME_TAG.FIRST_PLAYER) ? "GAMEPLAY_YOU_GO_FIRST" : "GAMEPLAY_OPPONENT_GOES_FIRST"));
                }
                if (s_startHand != null)
                {
                    var drawn = new List<string>();
                    foreach (var c in me.GetHandZone().GetCards())
                    {
                        var e = c == null ? null : c.GetEntity();
                        if (e == null || s_startHand.Contains(e.GetEntityId()) || IsCoin(e)) continue;
                        drawn.Add(Str.Clean(e.GetName()));
                    }
                    if (drawn.Count > 0) Say(A("GAMEPLAY_PLAYER_DREW_CARDS", Speech.HumanizeList(drawn)));
                }
            }
            // the coin for going second
            if (s_mulliganDone && !s_saidCoin && !me.HasTag(GAME_TAG.FIRST_PLAYER) && Time.unscaledTime - s_mulliganEndedAt < 20f)
                foreach (var c in me.GetHandZone().GetCards())
                    if (c != null && c.GetEntity() != null && IsCoin(c.GetEntity())) { s_saidCoin = true; Say(A("GAMEPLAY_YOU_GET_THE_COIN")); break; }

            // the rope: 10 seconds left, either player's turn
            var timer = TurnTimer.Get();
            if (timer != null && timer.IsRopeActive())
            {
                int left = Mathf.RoundToInt(timer.ComputeCountdownRemainingSec());
                if (left == 10 && s_lastRopeSecond != 10) Say(A("GAMEPLAY_N_SECONDS_REMAINING", 10));
                s_lastRopeSecond = left;
            }
            else s_lastRopeSecond = -1;
        }

        // the mulligan confirmed (the game's button gone, or Match said so)
        internal static bool Confirmed;
        static bool MulliganConfirmed() { return Confirmed; }

        static bool IsCoin(Entity e)
        {
            try
            {
                var id = e.GetCardId();
                if (id == "GAME_005") return true;
                var coins = CosmeticCoinManager.Get();
                return coins != null && coins.GetCoinIdFromCoinCard(id) > 0;
            }
            catch { return false; }
        }
    }
}
