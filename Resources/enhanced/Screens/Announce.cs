using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // What a match announces (docs/combat-spec-3-announcements.md: Hearthstone Access's behaviour, our
    // own code). Without hooks, the game's power processing is watched every frame:
    // - a task list (PTL) that starts: an attack is said at once ("Your Raid Leader attacked your
    //   opponent's Hero"), an opponent's play and a secret or hand trigger as soon as the card is known
    //   (with its text, as the big card shows it);
    // - task lists that ended: your plays, triggers, fatigue, burned cards, turn lines, then what changed
    //   on the board since the last look (the "diff": damage, healing, deaths, draws, summons, buffs,
    //   keywords, moves, quest progress, mana gains, spell damage), grouped and merged as HSA does;
    // - your turn ("Your Turn. You have N mana." and what was held back for it), when the game's own
    //   friendly-turn-started event fires.
    // Not done (no hooks): holding the game back while an announcement is spoken; the attack at the
    // exact lunge (it is said when the attack's task list starts); the speech optimizer's "Both minions
    // died"; history (Y). Battlegrounds and Mercenaries are left out as by the match screen.
    static class Announce
    {
        // ---- per game state ----------------------------------------------------------------------
        static GameState s_gs;
        static Snap s_last;
        static bool s_canSnapshot, s_started, s_turnStarted, s_waitingBanner, s_turnStartPending;
        static TAG_STEP s_prevStep;
        static int s_lastTurn;
        static readonly List<string> s_deferred = new List<string>();
        static readonly List<string> s_held = new List<string>();     // said during the mulligan: after it
        static readonly HashSet<PowerTaskList> s_described = new HashSet<PowerTaskList>();
        static readonly HashSet<PowerTaskList> s_ended = new HashSet<PowerTaskList>();
        static readonly List<PowerTaskList> s_pending = new List<PowerTaskList>();
        static PowerTaskList s_lastCurrent;
        static float s_waitingSince;
        static readonly HashSet<int> s_sheathed = new HashSet<int>(), s_unsheathed = new HashSet<int>(), s_broke = new HashSet<int>();

        static bool Enabled
        {
            get
            {
                var gs = GameState.Get();
                if (gs == null || !gs.IsGameCreated()) return false;
                var mgr = GameMgr.Get();
                if (mgr != null && (mgr.IsBattlegrounds() || mgr.IsMercenaries())) return false;
                return true;
            }
        }

        static void Reset(GameState gs)
        {
            s_gs = gs;
            HistoryLog.Reset();
            s_last = null;
            s_canSnapshot = s_started = s_turnStarted = s_waitingBanner = s_turnStartPending = false;
            s_prevStep = TAG_STEP.INVALID;
            s_lastTurn = 0;
            s_deferred.Clear(); s_held.Clear(); s_described.Clear(); s_ended.Clear(); s_pending.Clear();
            s_lastCurrent = null;
            s_sheathed.Clear(); s_unsheathed.Clear(); s_broke.Clear();
            if (gs != null)
                try { gs.RegisterFriendlyTurnStartedListener(OnFriendlyTurnStarted); } catch (Exception e) { Log.Error(e); }
        }

        static void OnFriendlyTurnStarted(object userData) { s_turnStartPending = true; }

        // every frame
        internal static void Tick()
        {
            var gs = GameState.Get();
            if (gs != s_gs) Reset(gs);
            if (gs == null || !Enabled) return;
            var pp = gs.GetPowerProcessor();
            if (pp == null) return;

            // the task lists waiting: remembered, so the ones that ran between two frames are not lost
            var queue = pp.GetPowerQueue();
            if (queue != null)
                foreach (var t in queue.GetList()) if (t != null && !s_ended.Contains(t) && !s_pending.Contains(t)) s_pending.Add(t);
            var current = pp.GetCurrentTaskList();
            if (current != null && !s_ended.Contains(current) && !s_pending.Contains(current)) s_pending.Add(current);
            var previous = Ref.Get<PowerTaskList>(pp, "m_previousTaskList");
            if (previous != null && previous != current && !s_ended.Contains(previous) && !s_pending.Contains(previous)) s_pending.Add(previous);

            // early lines of the running one (an attack, an opponent's play, a secret)
            if (current != null)
            {
                if (current != s_lastCurrent) { s_lastCurrent = current; }
                try { Early(current); } catch (Exception e) { Log.Error(e); }
            }

            // the ones that ended
            var ended = new List<PowerTaskList>();
            foreach (var t in s_pending)
            {
                if (t == current) continue;
                if (queue != null && queue.Contains(t)) continue;
                ended.Add(t);
            }
            if (ended.Count > 0)
            {
                foreach (var t in ended) { s_pending.Remove(t); s_ended.Add(t); }
                ended.RemoveAll(t => t.GetTaskList() == null || t.GetTaskList().Count == 0);
                ended.Sort((a, b) => a.GetId().CompareTo(b.GetId()));
                if (ended.Count > 0) try { Ended(gs, ended); } catch (Exception e) { Log.Error(e); }
            }
            if (s_ended.Count > 400) s_ended.Clear();

            // what came during the mulligan (its cards still moving): once it is over
            bool mulligan = Mulligan();
            if (!mulligan && s_held.Count > 0)
            {
                var held = new List<string>(s_held);
                s_held.Clear();
                foreach (var h in held) { if (s_waitingBanner) s_deferred.Add(h); else Say(h); }
            }
            // your turn: when the game shows its "Your Turn" banner (the mulligan over, the mana given)
            if (s_turnStartPending && current == null && !mulligan && BannerShown()) { s_turnStartPending = false; YourTurn(gs); }
            // no turn event (some missions): what was held back is not kept forever
            else if (s_waitingBanner && Time.unscaledTime - s_waitingSince > 10f && gs.IsFriendlySidePlayerTurn() && gs.IsInMainOptionMode()) YourTurn(gs);
        }

        static bool Mulligan()
        {
            var m = MulliganManager.Get();
            try { return m != null && (m.IsMulliganActive() || m.IsMulliganIntroActive()); } catch { return false; }
        }

        // the game's banner for your turn has been shown (or there is none to wait for)
        static bool BannerShown()
        {
            var t = TurnStartManager.Get();
            if (t == null) return true;
            try { return Ref.Get<bool>(t, "m_twoScoopsDisplayed") || t.IsTurnStartIndicatorShowing() || !t.IsListeningForTurnEvents(); } catch { return true; }
        }

        static void Say(string text)
        {
            text = text == null ? "" : text.Trim();
            if (text.Length == 0) return;
            Log.Info("announce: " + text);
            HistoryLog.Add(text);
            Speech.Say(text);
        }

        static string A(string key, params object[] args) { return Speech.S("ACCESSIBILITY_" + key, args); }

        // sentences joined: a period before the next unless one ends it already
        static string Lines(List<string> lines)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var l in lines)
            {
                var t = l == null ? "" : l.Trim();
                if (t.Length == 0) continue;
                if (sb.Length > 0)
                {
                    char c = sb[sb.Length - 1];
                    if (".!?;:".IndexOf(c) < 0) sb.Append('.');
                    sb.Append(' ');
                }
                sb.Append(t);
            }
            return sb.ToString();
        }

        // ---- the start of a task list: early lines --------------------------------------------------

        static bool Known(Entity e)
        {
            if (e == null) return false;
            try { return e.GetCardType() != TAG_CARDTYPE.INVALID && !string.IsNullOrEmpty(e.GetName()) && !e.IsHidden(); } catch { return false; }
        }

        static void Early(PowerTaskList t)
        {
            if (s_described.Contains(t) || !t.IsOrigin()) return;
            if (t.IsBlockType(PegasusGame.HistoryBlock.Type.ATTACK))
            {
                var line = AttackLine(t);
                if (!string.IsNullOrEmpty(line)) { s_described.Add(t); Say(line); }
                return;
            }
            var src = t.GetSourceEntity(false);
            if (src == null || src.IsGame() || !Known(src)) return;
            if (t.IsPlayBlock() && ShowsBigCard(src, t))
            {
                var line = PlayLine(src, true);
                if (!string.IsNullOrEmpty(line)) { s_described.Add(t); Say(line); }
                return;
            }
            if (t.IsTriggerBlock() && BigTrigger(src, t))
            {
                s_described.Add(t);
                Say(BigTriggerLine(src));
            }
        }

        static readonly System.Reflection.MethodInfo PlayedBigCard = Ref.Method(typeof(PowerProcessor), "ShouldShowPlayedBigCard", 2);

        // would the game show the played card big (an opponent's play, a forced one): the long text
        static bool ShowsBigCard(Entity src, PowerTaskList t)
        {
            try
            {
                var pp = GameState.Get().GetPowerProcessor();
                if (PlayedBigCard != null) return (bool)PlayedBigCard.Invoke(pp, new object[] { src, t.GetBlockStart() });
            }
            catch (Exception e) { Log.Error(e); }
            return src.IsControlledByOpposingSidePlayer();
        }

        // a secret's reveal, a card triggering from the hand, an opponent's start-of-game card: the
        // game shows them big
        static bool BigTrigger(Entity src, PowerTaskList t)
        {
            try
            {
                if (src.IsSecret()) return true;
                if (src.GetZone() == TAG_ZONE.HAND && !src.IsHidden() && src.HasTriggerVisual()) return true;
                if (src.IsControlledByOpposingSidePlayer())
                    foreach (var task in t.GetTaskList())
                    {
                        var show = task.GetPower() as Network.HistShowEntity;
                        if (show != null && show.Entity != null && show.Entity.Tags != null)
                            foreach (var tag in show.Entity.Tags) if (tag.Name == (int)GAME_TAG.START_OF_GAME_KEYWORD && tag.Value == 1) return true;
                    }
            }
            catch { }
            return false;
        }

        static string Text(Entity e)
        {
            string text = null;
            try { text = e.GetCardTextBuilder().BuildCardTextInHand(e); } catch { }
            if (string.IsNullOrEmpty(text)) try { text = e.GetCardTextInHand(); } catch { }
            return Str.Clean(text);
        }

        static string BigTriggerLine(Entity src)
        {
            var name = Str.Clean(src.GetName());
            return src.IsControlledByFriendlySidePlayer() ? A("GAMEPLAY_PLAYER_CARD_TRIGGERED", name) : A("GAMEPLAY_OPPONENT_CARD_TRIGGERED_BIG_CARD", name, Text(src));
        }

        // ---- action lines -------------------------------------------------------------------------

        static string PlayLine(Entity e, bool big)
        {
            bool mine = e.IsControlledByFriendlySidePlayer();
            var name = Str.Clean(e.GetName());
            string cls = null;
            try { cls = Str.Clean(GameStrings.GetClassName(e.GetClass())); } catch { }
            if (big)
            {
                bool hide = e.GetTag(GAME_TAG.HIDE_STATS) == 1;
                if (e.IsMinion()) return A(mine ? "GAMEPLAY_PLAYER_SUMMONED_MINION_BIG_CARD" : "GAMEPLAY_OPPONENT_SUMMONED_MINION_BIG_CARD", name, hide ? "" : e.GetATK().ToString(), hide ? "" : e.GetHealth().ToString(), Text(e));
                if (e.IsSecret()) return mine ? A("GAMEPLAY_PLAYER_CAST_SECRET_BIG_CARD", name, Text(e)) : A("GAMEPLAY_OPPONENT_CAST_SECRET", cls);
                if (e.IsHeroPower()) return A(mine ? "GAMEPLAY_PLAYER_USED_HERO_POWER_BIG_CARD" : "GAMEPLAY_OPPONENT_USED_HERO_POWER_BIG_CARD", name, Text(e));
                if (e.IsWeapon()) return A(mine ? "GAMEPLAY_PLAYER_EQUIPPED_WEAPON_BIG_CARD" : "GAMEPLAY_OPPONENT_EQUIPPED_WEAPON_BIG_CARD", name, e.GetATK(), e.GetHealth(), Text(e));
                if (e.IsLocation()) return A(mine ? "GAMEPLAY_PLAYER_PLAYED_LOCATION_BIG_CARD" : "GAMEPLAY_OPPONENT_PLAYED_LOCATION_BIG_CARD", name, e.GetHealth(), Text(e));
                if (e.IsSpell() || e.IsEnchantment()) return A(mine ? "GAMEPLAY_PLAYER_PLAYED_CARD_BIG_CARD" : "GAMEPLAY_OPPONENT_PLAYED_CARD_BIG_CARD", name, Text(e));
                return PlayLine(e, false);
            }
            if (e.IsMinion()) return A(mine ? "GAMEPLAY_PLAYER_SUMMONED_MINION" : "GAMEPLAY_OPPONENT_SUMMONED_MINION", name);
            if (e.IsWeapon()) return A(mine ? "GAMEPLAY_PLAYER_EQUIPPED_WEAPON" : "GAMEPLAY_OPPONENT_EQUIPPED_WEAPON", name);
            if (e.IsHeroPower()) return A(mine ? "GAMEPLAY_PLAYER_USED_HERO_POWER" : "GAMEPLAY_OPPONENT_USED_HERO_POWER", name);
            if (e.IsSecret()) return mine ? A("GAMEPLAY_PLAYER_CAST_SECRET", name) : A("GAMEPLAY_OPPONENT_CAST_SECRET", cls);
            return A(mine ? "GAMEPLAY_PLAYER_PLAYED_CARD" : "GAMEPLAY_OPPONENT_PLAYED_CARD", name);
        }

        static string AttackLine(PowerTaskList t)
        {
            var attacker = t.GetAttacker();
            var defender = t.GetDefender();
            if (attacker == null || defender == null) return null;
            var proposed = t.GetProposedDefender();
            var all = LiveSnapshot();
            var a = NameInZone(S.Of(attacker), all);
            var d = NameInZone(S.Of(defender), all);
            if (proposed != null && proposed != defender) return A("GAMEPLAY_ENTITY_ATTACKED_OTHER", a, NameInZone(S.Of(proposed), all), d);
            return A("GAMEPLAY_ENTITY_ATTACKED", a, d);
        }

        static string TriggerLine(Entity src)
        {
            if (src == null || src.IsGame()) return null;
            bool callout = (src.IsCharacter() && !src.IsHero()) || src.IsWeapon() || src.IsSecret() || src.IsQuest() || src.IsSideQuest() || src.IsQuestline() || src.IsHeroPower();
            if (!callout) return null;
            return A("GAMEPLAY_CARD_IN_ZONE_TRIGGERED", NameInZone(S.Of(src), LiveSnapshot()));
        }

        static bool IsFriendly(Entity e)
        {
            var p = e as Player;
            if (p != null) return p.IsFriendlySide();
            return e.IsControlledByFriendlySidePlayer();
        }

        // ---- task lists that ended ------------------------------------------------------------------

        static void Ended(GameState gs, List<PowerTaskList> ended)
        {
            var lines = new List<string>();
            bool canDescribe = s_started;
            bool quietDamage = false;       // minion against minion: the damage is not said (deaths are)
            Entity source = null;
            foreach (var t in ended)
            {
                var src = t.GetSourceEntity(false);
                if (t.IsOrigin()) source = src ?? source;
                if (t.IsOrigin() && !s_described.Contains(t))
                {
                    s_described.Add(t);
                    if (t.IsPlayBlock() && src != null && Known(src)) lines.Add(PlayLine(src, ShowsBigCard(src, t)));
                    else if (t.IsBlockType(PegasusGame.HistoryBlock.Type.FATIGUE) && src != null)
                        lines.Add(A(IsFriendly(src) ? "GAMEPLAY_PLAYER_DREW_CARD_FROM_EMPTY_DECK" : "GAMEPLAY_OPPONENT_DREW_CARD_FROM_EMPTY_DECK"));
                    else if (t.IsBlockType(PegasusGame.HistoryBlock.Type.ATTACK)) lines.Add(AttackLine(t));
                    else if (t.IsTriggerBlock() && src != null) lines.Add(BigTrigger(src, t) ? BigTriggerLine(src) : TriggerLine(src));
                }
                // a minion-against-minion attack: the damage is not said (the deaths are)
                if (t.IsOrigin() && t.IsBlockType(PegasusGame.HistoryBlock.Type.ATTACK))
                {
                    var att = t.GetAttacker();
                    var def = t.GetDefender();
                    if (!(att != null && att.IsHero()) && !(def != null && def.IsHero())) quietDamage = true;
                }
                // burned cards (a full hand)
                foreach (var task in t.GetTaskList())
                {
                    var meta = task.GetPower() as Network.HistMetaData;
                    if (meta == null || meta.MetaType.ToString() != "BURNED_CARD" || meta.Info == null) continue;
                    foreach (var id in meta.Info)
                    {
                        var e = gs.GetEntity(id);
                        if (e != null) lines.Add(A(e.IsControlledByFriendlySidePlayer() ? "GAMEPLAY_PLAYER_DREW_CARD_BUT_BURNED" : "GAMEPLAY_OPPONENT_DREW_CARD_BUT_BURNED", Str.Clean(e.GetName())));
                    }
                }
            }

            var step = (TAG_STEP)gs.GetGameEntity().GetTag(GAME_TAG.STEP);
            bool changed = step != s_prevStep;
            // checked once a frame, a short step (MAIN_READY) is easily missed: the game is under way once
            // it is past the mulligan, and a new turn is a new turn number
            bool pastMulligan = step != TAG_STEP.INVALID && step != TAG_STEP.BEGIN_FIRST && step != TAG_STEP.BEGIN_SHUFFLE
                && step != TAG_STEP.BEGIN_DRAW && step != TAG_STEP.BEGIN_MULLIGAN;
            if (pastMulligan && !s_started) { s_started = true; s_canSnapshot = true; }
            int turn = gs.GetTurn();
            if (pastMulligan && turn != s_lastTurn)
            {
                if (s_lastTurn > 0 && s_turnStarted && step != TAG_STEP.MAIN_END)
                {
                    lines.Add(A("GAMEPLAY_TURN_ENDED"));
                    TurnEnded(gs);
                }
                s_lastTurn = turn;
                s_turnStarted = false;
                TurnChange(gs, lines);
                s_canSnapshot = true;
            }
            if (step == TAG_STEP.MAIN_END)
            {
                if (changed && s_turnStarted)
                {
                    lines.Add(A("GAMEPLAY_TURN_ENDED"));
                    TurnEnded(gs);
                    s_turnStarted = false;
                    s_canSnapshot = true;
                }
            }
            else if (step == TAG_STEP.MAIN_READY) s_canSnapshot = true;
            else if (step == TAG_STEP.MAIN_START_TRIGGERS || step == TAG_STEP.MAIN_START) s_canSnapshot = true;
            else if (step == TAG_STEP.BEGIN_MULLIGAN)
            {
                s_canSnapshot = true;
                if (gs.IsFriendlySidePlayerTurn() && !s_turnStarted) Wait();
            }
            else if (s_prevStep == TAG_STEP.BEGIN_MULLIGAN && changed) s_canSnapshot = false;
            s_prevStep = step;
            if (s_last == null) s_canSnapshot = true;

            if (!s_canSnapshot) { Out(lines); return; }
            var now = SnapshotFull(gs);
            if (canDescribe && s_last != null) lines.Add(Diff(gs, s_last, now, source == null ? 0 : source.GetEntityId(), step, quietDamage));
            s_last = now;
            Out(lines);
        }

        static void Wait()
        {
            if (!s_waitingBanner) s_waitingSince = Time.unscaledTime;
            s_waitingBanner = true;
        }

        static void Out(List<string> lines)
        {
            var text = Lines(lines);
            if (text.Length == 0) return;
            if (s_waitingBanner) { s_deferred.Add(text); return; }
            if (Mulligan()) { s_held.Add(text); return; }
            Say(text);
        }

        // ---- turns --------------------------------------------------------------------------------

        static void TurnChange(GameState gs, List<string> lines)
        {
            if (s_turnStarted) return;
            s_turnStarted = true;
            if (gs.IsFriendlySidePlayerTurn()) { Wait(); return; }
            lines.Add(A("GAMEPLAY_OPPONENT_TURN"));
        }

        static void YourTurn(GameState gs)
        {
            s_waitingBanner = false;
            s_turnStarted = true;
            // Changes collected while waiting for the turn banner belong to the previous
            // turn boundary (for example, "Turn ended"). Speak them before announcing
            // the new turn so the narration follows the game's event order.
            var lines = new List<string>(s_deferred);
            s_deferred.Clear();
            lines.Add(Str.Word("GAMEPLAY_YOUR_TURN"));
            try
            {
                var me = gs.GetFriendlySidePlayer();
                if (me != null) lines.Add(A("GAMEPLAY_PLAYER_TURN_START_READ_MANA", me.GetNumAvailableResources()));
            }
            catch { }
            Say(Lines(lines));
        }

        // weapons sheathed / drawn at the turn change: their heroes' attack swing is not said
        static void TurnEnded(GameState gs)
        {
            try
            {
                var current = gs.GetCurrentPlayer();
                foreach (var p in new[] { gs.GetFriendlySidePlayer(), gs.GetOpposingSidePlayer() })
                {
                    if (p == null || !p.HasWeapon() || p.GetHero() == null) continue;
                    if (p == current) s_sheathed.Add(p.GetHero().GetEntityId()); else s_unsheathed.Add(p.GetHero().GetEntityId());
                }
            }
            catch { }
        }

        // ---- snapshots ----------------------------------------------------------------------------

        // what we keep of an entity
        class S
        {
            internal int Id, Pos, Damage, Atk, Health, Armor, QuestProgress, QuestTotal;
            internal string Name, CardId;
            internal bool Friendly, Known;
            internal TAG_ZONE Zone;
            internal TAG_CARDTYPE Type;
            internal TAG_CLASS Class;
            internal bool Minion, Hero, HeroPower, Weapon, Location, Secret, Quest, Character, Objective;
            internal bool Invulnerable, Cursed, Haunted, Exhausted, Silenced, Dormant, Frozen, Immune, Magnetic, Poisonous, Stealthed, Revealed;
            internal HashSet<GAME_TAG> Keywords = new HashSet<GAME_TAG>();

            internal static readonly GAME_TAG[] KeywordTags = { GAME_TAG.TAUNT, GAME_TAG.ELUSIVE, GAME_TAG.DEATHRATTLE, GAME_TAG.BATTLECRY, GAME_TAG.CHARGE,
                GAME_TAG.LIFESTEAL, GAME_TAG.RUSH, GAME_TAG.WINDFURY, GAME_TAG.DIVINE_SHIELD, GAME_TAG.FREEZE, GAME_TAG.REBORN };

            internal static S Of(Entity e)
            {
                var s = new S();
                s.Id = e.GetEntityId();
                try
                {
                    s.CardId = e.GetCardId();
                    s.Type = e.GetCardType();
                    s.Name = Str.Clean(e.GetName());
                    s.Known = s.Type != TAG_CARDTYPE.INVALID && !string.IsNullOrEmpty(s.Name);
                    s.Friendly = e.IsControlledByFriendlySidePlayer();
                    s.Zone = e.GetZone();
                    s.Pos = e.GetZonePosition();
                    s.Class = e.GetClass();
                    s.Minion = e.IsMinion(); s.Hero = e.IsHero(); s.HeroPower = e.IsHeroPower(); s.Weapon = e.IsWeapon(); s.Location = e.IsLocation();
                    s.Secret = e.IsSecret(); s.Quest = e.IsQuest() || e.IsSideQuest() || e.IsQuestline(); s.Character = e.IsCharacter(); s.Objective = e.IsObjective();
                    s.Damage = e.GetDamage(); s.Atk = e.GetATK(); s.Health = e.GetHealth(); s.Armor = e.GetArmor();
                    s.QuestProgress = e.GetTag(GAME_TAG.QUEST_PROGRESS); s.QuestTotal = e.GetTag(GAME_TAG.QUEST_PROGRESS_TOTAL);
                    s.Invulnerable = e.HasTag(GAME_TAG.CANT_BE_ATTACKED) || e.HasTag(GAME_TAG.UNTOUCHABLE);
                    s.Cursed = e.HasTag(GAME_TAG.EVIL_GLOW); s.Haunted = e.HasTag(GAME_TAG.VALEERASHADOW);
                    s.Exhausted = e.IsExhausted(); s.Silenced = e.IsSilenced(); s.Dormant = e.IsDormant(); s.Frozen = e.IsFrozen(); s.Immune = e.IsImmune();
                    s.Magnetic = e.HasTag(GAME_TAG.MAGNETIC); s.Poisonous = e.IsPoisonous(); s.Stealthed = e.IsStealthed(); s.Revealed = e.HasTag(GAME_TAG.REVEALED);
                    foreach (var k in KeywordTags) if (e.GetTag(k) > 0) s.Keywords.Add(k);
                }
                catch { }
                return s;
            }

            internal bool CanLive { get { return Character || Weapon || Objective || Location; } }
            internal bool Alive { get { return CanLive && (Zone == TAG_ZONE.PLAY || Zone == TAG_ZONE.SECRET); } }
            internal bool Dead { get { return CanLive && (Zone == TAG_ZONE.GRAVEYARD || Zone == TAG_ZONE.REMOVEDFROMGAME || Zone == TAG_ZONE.SETASIDE); } }
        }

        class Snap
        {
            internal Dictionary<int, S> All = new Dictionary<int, S>();
            internal List<S> Order = new List<S>();
            internal int[] Avail = new int[2], Crystals = new int[2], SpellPower = new int[2], HeroId = new int[2], HeroPowerId = new int[2];
            internal bool[] HasWeapon = new bool[2];
        }

        static Dictionary<int, S> s_liveCache;
        static int s_liveFrame = -1;

        static Dictionary<int, S> LiveSnapshot()
        {
            if (s_liveFrame == Time.frameCount && s_liveCache != null) return s_liveCache;
            s_liveFrame = Time.frameCount;
            s_liveCache = SnapshotFull(GameState.Get()).All;
            return s_liveCache;
        }

        // the board as it is: heroes, hero powers, weapons, hands, decks, graveyards, battlefields,
        // secrets, and cards set aside (hand destruction sends cards there)
        static Snap SnapshotFull(GameState gs)
        {
            var snap = new Snap();
            foreach (var e in gs.GetEntityMap().Values)
            {
                if (e == null || e is Player || e.IsGame()) continue;
                TAG_CARDTYPE type;
                try { type = e.GetCardType(); } catch { continue; }
                if (type == TAG_CARDTYPE.ENCHANTMENT) continue;
                var zone = e.GetZone();
                if (zone == TAG_ZONE.INVALID) continue;
                if (zone == TAG_ZONE.SETASIDE && !(e.IsHero() || e.IsSpell() || e.IsMinion() || e.IsWeapon() || e.IsLocation())) continue;
                var s = S.Of(e);
                snap.All[s.Id] = s;
                snap.Order.Add(s);
            }
            snap.Order.Sort((a, b) =>
            {
                int za = ZoneRank(a.Zone), zb = ZoneRank(b.Zone);
                if (za != zb) return za.CompareTo(zb);
                if (a.Friendly != b.Friendly) return a.Friendly ? -1 : 1;
                return a.Pos.CompareTo(b.Pos);
            });
            for (int i = 0; i < 2; i++)
            {
                var p = i == 0 ? gs.GetFriendlySidePlayer() : gs.GetOpposingSidePlayer();
                if (p == null) continue;
                try
                {
                    snap.Avail[i] = p.GetNumAvailableResources();
                    snap.Crystals[i] = p.GetTag(GAME_TAG.RESOURCES);
                    snap.SpellPower[i] = p.TotalSpellpower(null);
                    snap.HeroId[i] = p.GetHero() == null ? 0 : p.GetHero().GetEntityId();
                    snap.HeroPowerId[i] = p.GetHeroPower() == null ? 0 : p.GetHeroPower().GetEntityId();
                    snap.HasWeapon[i] = p.HasWeapon();
                }
                catch { }
            }
            return snap;
        }

        static int ZoneRank(TAG_ZONE z)
        {
            switch (z)
            {
                case TAG_ZONE.HAND: return 1;
                case TAG_ZONE.DECK: return 2;
                case TAG_ZONE.GRAVEYARD: return 3;
                case TAG_ZONE.PLAY: return 4;
                case TAG_ZONE.SECRET: return 5;
                default: return 6;
            }
        }

        // ---- naming -------------------------------------------------------------------------------

        static string BaseName(S s)
        {
            if (s.Hero) return Str.Word("GLOBAL_CARDTYPE_HERO");
            if (s.HeroPower) return Str.Word("GLOBAL_CARDTYPE_HEROPOWER");
            if (!s.Known) return Speech.S(K.GLOBAL_CARD);
            return s.Name;
        }

        static string FullName(S s) { return A(s.Friendly ? "GAMEPLAY_DIFF_PLAYER_ENTITY_FULL_NAME" : "GAMEPLAY_DIFF_OPPONENT_ENTITY_FULL_NAME", BaseName(s)); }

        static string Ordinal(int n)
        {
            if (n >= 1 && n <= 10) { var t = A("FORMATTING_ORDINAL_NUMBER_" + n); if (!string.IsNullOrEmpty(t) && !t.StartsWith("ACCESSIBILITY_")) return t; }
            return n.ToString();
        }

        // "Your Raid Leader", or "Your second Raid Leader" when the list has more of them
        static string NameInList(S s, List<S> list)
        {
            var full = FullName(s);
            int count = 0, at = 0;
            foreach (var o in list)
            {
                if (FullName(o) != full) continue;
                count++;
                if (o.Id == s.Id) at = count;
            }
            if (count <= 1 || at == 0) return full;
            return A(s.Friendly ? "GAMEPLAY_DIFF_PLAYER_ENTITY_FULL_NAME_IN_LIST" : "GAMEPLAY_DIFF_OPPONENT_ENTITY_FULL_NAME_IN_LIST", Ordinal(at), BaseName(s));
        }

        // the ordinal by its place in its zone now (left to right)
        static string NameInZone(S s, Dictionary<int, S> live)
        {
            S now;
            var zone = live != null && live.TryGetValue(s.Id, out now) ? now.Zone : s.Zone;
            bool friendly = live != null && live.TryGetValue(s.Id, out now) ? now.Friendly : s.Friendly;
            if (zone == TAG_ZONE.GRAVEYARD || zone == TAG_ZONE.INVALID || zone == TAG_ZONE.REMOVEDFROMGAME || zone == TAG_ZONE.SETASIDE || live == null) return FullName(s);
            var list = new List<S>();
            foreach (var o in live.Values) if (o.Zone == zone && o.Friendly == friendly) list.Add(o.Id == s.Id ? s : o);
            list.Sort((a, b) => a.Pos.CompareTo(b.Pos));
            return NameInList(s, list);
        }

        // "2 Raid Leader and Coin": names with their counts, first-seen order
        static string Names(List<string> names)
        {
            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (var n in names)
            {
                if (!counts.ContainsKey(n)) { counts[n] = 0; order.Add(n); }
                counts[n]++;
            }
            var card = Speech.S(K.GLOBAL_CARD);
            var parts = new List<string>();
            foreach (var n in order)
            {
                int c = counts[n];
                if (n == card) parts.Add(c == 1 ? "1 " + card : c + " " + A("GLOBAL_CARD_PLURAL"));
                else parts.Add(c > 1 ? A("GAMEPLAY_DIFF_MULTIPLE_ENTITIES", c, n) : n);
            }
            return Speech.HumanizeList(parts);
        }

        // one name: the singular line; several: the plural one with the names and counts
        static string ZoneText(List<string> names, string singular, string plural)
        {
            if (names.Count == 0) return null;
            if (names.Count == 1) return A(singular, names[0]);
            return A(plural, Names(names));
        }

        // ---- the diff -----------------------------------------------------------------------------

        // a phrase in its two forms: one ("Your Yeti took 2 damage"), several ("... took 2 damage")
        struct P
        {
            internal string One, Many;
            internal P(string one, string many) { One = one; Many = many; }
            internal bool Empty { get { return string.IsNullOrEmpty(One); } }
        }

        static P Pair(string key, string multipleKey, params object[] args)
        {
            return new P(A("GAMEPLAY_DIFF_" + key, args), A("GAMEPLAY_DIFF_" + (multipleKey ?? key), args));
        }

        static string Diff(GameState gs, Snap prevFull, Snap full, int sourceId, TAG_STEP step, bool quietDamage = false)
        {
            var before = prevFull.All;
            var after = full.All;
            var parts = new List<string>();

            // quest progress
            foreach (var a in after.Values)
            {
                S b;
                if (!a.Quest || !before.TryGetValue(a.Id, out b)) continue;
                if (a.QuestProgress != b.QuestProgress && a.QuestTotal > 0) parts.Add(A("TOAST_QUEST_PROGRESS_TOAST_PROGRESS", a.QuestProgress, a.QuestTotal));
            }
            // a hero replaced by a hero card
            if (true)
                for (int i = 0; i < 2; i++)
                    if (prevFull.HeroId[i] != 0 && full.HeroId[i] != 0 && prevFull.HeroId[i] != full.HeroId[i])
                    {
                        S h;
                        if (after.TryGetValue(full.HeroId[i], out h)) parts.Add(A(i == 0 ? "GAMEPLAY_DIFF_MOVEMENT_PLAYER_HERO_CHANGED" : "GAMEPLAY_DIFF_MOVEMENT_OPPONENT_HERO_CHANGED", h.Name));
                    }

            // the per-entity facts
            var died = new List<S>();
            var transformed = new List<KeyValuePair<S, S>>();
            var changed = new Dictionary<int, P>();
            var moved = new List<KeyValuePair<S, S>>();
            foreach (var a in full.Order)
            {
                S b;
                if (!before.TryGetValue(a.Id, out b)) continue;
                bool dies = b.Alive && a.Dead && !(b.Hero && b.Zone == TAG_ZONE.PLAY && a.Zone == TAG_ZONE.SETASIDE);
                bool revealed = b.Zone == TAG_ZONE.SECRET && a.Zone == b.Zone && b.Name != a.Name;
                bool transforms = !dies && a.Zone == b.Zone && !revealed && b.Known && a.Known && b.Name != a.Name;
                bool moves = (!dies && a.Zone != b.Zone) || a.Friendly != b.Friendly;
                if (dies) { died.Add(b); if (b.Weapon) { int side = b.Friendly ? 0 : 1; if (!full.HasWeapon[side] && full.HeroId[side] != 0) s_broke.Add(full.HeroId[side]); } }
                else if (transforms) transformed.Add(new KeyValuePair<S, S>(b, a));
                if (moves) moved.Add(new KeyValuePair<S, S>(b, a));
                if (!dies && !transforms && !revealed)
                {
                    var d = Describe(b, a);
                    if (!d.Empty) changed[a.Id] = d;
                }
            }

            parts.Add(NewEntities(before, full));
            parts.Add(Movements(moved, sourceId, full, before));
            // transforms (not in the deck: Dredge reveals are not transforms)
            {
                var entries = new Dictionary<int, P>();
                var list = new List<S>();
                foreach (var kv in transformed)
                {
                    var z = kv.Value.Zone;
                    if (z == TAG_ZONE.DECK || z == TAG_ZONE.SETASIDE || z == TAG_ZONE.GRAVEYARD || z == TAG_ZONE.REMOVEDFROMGAME) continue;
                    entries[kv.Key.Id] = Pair("ENTITY_TRANSFORMED", "MULTIPLE_ENTITIES_TRANSFORMED", kv.Value.Name);
                    list.Add(kv.Key);
                }
                parts.Add(Grouped(entries, before, sourceId, s => NameInZone(s, after)));
            }
            // deaths: named among the dying
            {
                var entries = new Dictionary<int, P>();
                foreach (var b in died)
                {
                    P p;
                    if (b.Weapon) { if (full.HasWeapon[b.Friendly ? 0 : 1]) continue; p = Pair("WEAPON_BROKE", null); }
                    else if (b.Objective) p = Pair("AURA_FADED", "MULTIPLE_AURAS_FADED");
                    else if (b.Location) p = Pair("LOCATION_WAS_DESTROYED", "MULTIPLE_LOCATIONS_WERE_DESTROYED");
                    else p = Pair("ENTITY_DIED", "MULTIPLE_ENTITIES_DIED");
                    entries[b.Id] = p;
                }
                parts.Add(Grouped(entries, before, sourceId, s => NameInList(s, died)));
            }
            // the other changes (not after a minion fought a minion: its deaths say enough)
            if (quietDamage) changed.Clear();
            parts.Add(Grouped(changed, before, sourceId, s => { S b2; return NameInZone(before.TryGetValue(s.Id, out b2) ? b2 : s, after); }));

            // mana gained mid-turn (spending is not said)
            if (prevFull != null && (step == TAG_STEP.MAIN_ACTION || step == TAG_STEP.MAIN_COMBAT))
                for (int i = 0; i < 2; i++)
                {
                    int dAvail = full.Avail[i] - prevFull.Avail[i], dCrystals = full.Crystals[i] - prevFull.Crystals[i];
                    string who = i == 0 ? "PLAYER" : "OPPONENT";
                    if (dCrystals > 0 && dAvail >= dCrystals) parts.Add(A("GAMEPLAY_DIFF_" + who + "_GAINED_MANA", dAvail));
                    else if (dCrystals > 0 && dAvail > 0) parts.Add(A("GAMEPLAY_DIFF_" + who + "_GAINED_MANA_AND_EMPTY_MANA_CRYSTALS", dAvail, dCrystals - dAvail));
                    else if (dCrystals > 0) parts.Add(A("GAMEPLAY_DIFF_" + who + "_GAINED_EMPTY_MANA_CRYSTALS", dCrystals));
                    else if (dAvail > 0) parts.Add(A("GAMEPLAY_DIFF_" + who + "_GAINED_MANA", dAvail));
                }
            // spell damage
            if (true)
                for (int i = 0; i < 2; i++)
                {
                    int d = full.SpellPower[i] - prevFull.SpellPower[i];
                    string who = i == 0 ? "PLAYER" : "OPPONENT";
                    if (d > 0) parts.Add(A("GAMEPLAY_DIFF_" + who + "_GAINED_SPELL_DAMAGE", d));
                    else if (d < 0) parts.Add(A("GAMEPLAY_DIFF_" + who + "_LOST_SPELL_DAMAGE", -d));
                }
            return Lines(parts);
        }

        // the changes of one entity, in HSA's order (armor lost first, then damage...)
        static P Describe(S b, S a)
        {
            var none = new P(null, null);
            if (b.Hero && b.Zone == TAG_ZONE.HAND && a.Zone == TAG_ZONE.PLAY) return none;
            if (b.Type == TAG_CARDTYPE.INVALID || a.Type == TAG_CARDTYPE.INVALID) return none;
            if (b.Dead && a.Dead) return none;
            if (a.Zone != b.Zone && a.Zone != TAG_ZONE.PLAY) return none;
            var one = new List<string>();
            var many = new List<string>();
            Action<P> add = p => { if (!p.Empty) { one.Add(p.One); many.Add(p.Many); } };
            var gained = new List<string>();
            var lost = new List<string>();

            if (b.Armor > a.Armor) add(Pair("ENTITY_LOST_STATS", "MULTIPLE_ENTITIES_LOST_STATS", A("GAMEPLAY_DIFF_ENTITY_N_ARMOR", b.Armor - a.Armor)));
            if (a.Character && a.Damage > b.Damage) add(Pair("ENTITY_TOOK_N_DAMAGE", "MULTIPLE_ENTITIES_TOOK_N_DAMAGE", a.Damage - b.Damage));
            if (a.Damage < b.Damage)
            {
                if (a.Character) add(Pair("ENTITY_RECOVERED_N_HEALTH", "MULTIPLE_ENTITIES_RECOVERED_N_HEALTH", b.Damage - a.Damage));
                else if (a.Weapon) gained.Add(A("GAMEPLAY_DIFF_ENTITY_N_DURABILITY", b.Damage - a.Damage));
            }
            if (!b.Invulnerable && a.Invulnerable && !(GameMgr.Get() != null && GameUtils.IsTutorialMission(GameMgr.Get().GetMissionId())) && !(!b.Immune && a.Immune))
                add(Pair("ENTITY_BECAME_INVULNERABLE", "MULTIPLE_ENTITIES_BECAME_INVULNERABLE"));
            if (!b.Cursed && a.Cursed) add(Pair("ENTITY_WAS_CURSED", "MULTIPLE_ENTITIES_WERE_CURSED"));
            if (!b.Haunted && a.Haunted) add(Pair("ENTITY_WAS_HAUNTED", "MULTIPLE_ENTITIES_WERE_HAUNTED"));
            if (a.Location && b.Exhausted && !a.Exhausted) add(Pair("LOCATION_REOPENED", "MULTIPLE_LOCATIONS_REOPENED"));
            if (!b.Silenced && a.Silenced) add(Pair("ENTITY_BECAME_SILENCED", "MULTIPLE_ENTITIES_BECAME_SILENCED"));
            else
            {
                foreach (var k in S.KeywordTags)
                {
                    bool had = b.Keywords.Contains(k), has = a.Keywords.Contains(k);
                    if (had == has) continue;
                    string name = null;
                    try { name = Str.Clean(GameStrings.GetKeywordName(k)); } catch { }
                    if (string.IsNullOrEmpty(name)) continue;
                    (has ? gained : lost).Add(name);
                }
                if (!b.Dormant && a.Dormant) add(Pair("ENTITY_BECAME_DORMANT", "MULTIPLE_ENTITIES_BECAME_DORMANT"));
                if (b.Dormant && !a.Dormant) add(Pair("ENTITY_NO_LONGER_DORMANT", "MULTIPLE_ENTITIES_NO_LONGER_DORMANT"));
                if (!b.Frozen && a.Frozen) add(Pair("ENTITY_BECAME_FROZEN", "MULTIPLE_ENTITIES_BECAME_FROZEN"));
                if (b.Frozen && !a.Frozen) add(Pair("ENTITY_NO_LONGER_FROZEN", "MULTIPLE_ENTITIES_NO_LONGER_FROZEN"));
                if (!a.Weapon && !b.Immune && a.Immune) add(Pair("ENTITY_BECAME_IMMUNE", "MULTIPLE_ENTITIES_BECAME_IMMUNE"));
                if (!a.Weapon && b.Immune && !a.Immune) add(Pair("ENTITY_NO_LONGER_IMMUNE", "MULTIPLE_ENTITIES_NO_LONGER_IMMUNE"));
                if (!b.Magnetic && a.Magnetic) add(Pair("ENTITY_BECAME_MAGNETIC", "MULTIPLE_ENTITIES_BECAME_MAGNETIC"));
                if (b.Magnetic && !a.Magnetic) add(Pair("ENTITY_NO_LONGER_MAGNETIC", "MULTIPLE_ENTITIES_NO_LONGER_MAGNETIC"));
                if (!b.Poisonous && a.Poisonous) add(Pair("ENTITY_BECAME_POISONOUS", "MULTIPLE_ENTITIES_BECAME_POISONOUS"));
                if (b.Poisonous && !a.Poisonous) add(Pair("ENTITY_NO_LONGER_POISONOUS", "MULTIPLE_ENTITIES_NO_LONGER_POISONOUS"));
                if (!b.Stealthed && a.Stealthed) add(Pair("ENTITY_BECAME_STEALTHED", "MULTIPLE_ENTITIES_BECAME_STEALTHED"));
            }
            // stats (a hero's weapon swing at the turn change is not said)
            int dAtk = a.Atk - b.Atk;
            if (dAtk < 0 && a.Hero && (s_sheathed.Remove(a.Id) || s_broke.Remove(a.Id))) dAtk = 0;
            if (dAtk > 0 && a.Hero && s_unsheathed.Remove(a.Id)) dAtk = 0;
            if (dAtk > 0) gained.Add(A("GAMEPLAY_DIFF_ENTITY_N_ATTACK", dAtk));
            else if (dAtk < 0) lost.Add(A("GAMEPLAY_DIFF_ENTITY_N_ATTACK", -dAtk));
            int dHealth = a.Health - b.Health;
            if (!a.Weapon)
            {
                if (dHealth > 0) gained.Add(A("GAMEPLAY_DIFF_ENTITY_N_HEALTH", dHealth));
                else if (dHealth < 0) lost.Add(A("GAMEPLAY_DIFF_ENTITY_N_HEALTH", -dHealth));
            }
            if (a.Armor > b.Armor) gained.Add(A("GAMEPLAY_DIFF_ENTITY_N_ARMOR", a.Armor - b.Armor));
            if (gained.Count > 0) add(Pair("ENTITY_GAINED_STATS", "MULTIPLE_ENTITIES_GAINED_STATS", Speech.HumanizeList(gained)));
            if (lost.Count > 0) add(Pair("ENTITY_LOST_STATS", "MULTIPLE_ENTITIES_LOST_STATS", Speech.HumanizeList(lost)));
            if (one.Count == 0) return none;
            return new P(Speech.HumanizeList(one), Speech.HumanizeList(many));
        }

        // groups ("Your enemies", "All minions", "Your 2 Wisp") take a phrase all their members share;
        // the rest keep theirs; entries with the same phrase are merged into one sentence
        static string Grouped(Dictionary<int, P> affected, Dictionary<int, S> before, int sourceId, Func<S, string> nameOf)
        {
            if (affected.Count == 0) return null;
            var fMinions = new List<int>(); var oMinions = new List<int>();
            int fHero = 0, oHero = 0;
            var ordered = new List<S>(before.Values);
            ordered.Sort((a, b) => a.Pos.CompareTo(b.Pos));
            foreach (var s in ordered)
            {
                if (s.Zone != TAG_ZONE.PLAY) continue;
                if (s.Minion) (s.Friendly ? fMinions : oMinions).Add(s.Id);
                else if (s.Hero) { if (s.Friendly) fHero = s.Id; else oHero = s.Id; }
            }
            var heroes = new List<int>(); if (fHero != 0) heroes.Add(fHero); if (oHero != 0) heroes.Add(oHero);
            var everyone = new List<int>(fMinions); everyone.AddRange(oMinions); everyone.AddRange(heroes);
            var allMinions = new List<int>(fMinions); allMinions.AddRange(oMinions);
            var enemies = new List<int>(oMinions); if (oHero != 0) enemies.Add(oHero);
            var friends = new List<int>(fMinions); if (fHero != 0) friends.Add(fHero);

            var groups = new List<KeyValuePair<string, List<int>>>();
            Action<string, List<int>, int> group = (key, members, min) => { if (members.Count > min) groups.Add(new KeyValuePair<string, List<int>>(A("GAMEPLAY_DIFF_GROUP_" + key), members)); };
            group("EVERYONE", everyone, 2);
            group("BOTH_HEROES", heroes, 1);
            if (fMinions.Count > 0 && oMinions.Count > 0) group("ALL_MINIONS", allMinions, 1);
            group("ENEMIES", enemies, 1);
            group("FRIENDLY_CHARACTERS", friends, 1);
            group("OPPONENT_MINIONS", oMinions, 1);
            group("FRIENDLY_MINIONS", fMinions, 1);
            if (sourceId != 0)
            {
                Func<List<int>, List<int>> without = l => l.FindAll(id => id != sourceId);
                group("EVERYONE_ELSE", without(everyone), 1);
                group("ALL_OTHER_MINIONS", without(allMinions), 1);
                group("OTHER_ENEMIES", without(enemies), 1);
                group("OTHER_FRIENDLY_CHARACTERS", without(friends), 1);
                group("OTHER_OPPONENT_MINIONS", without(oMinions), 1);
                group("OTHER_FRIENDLY_MINIONS", without(fMinions), 1);
            }
            foreach (var side in new[] { true, false })
            {
                var byName = new Dictionary<string, List<int>>();
                var names = new List<string>();
                foreach (var id in side ? fMinions : oMinions)
                {
                    var n = before[id].Name ?? "";
                    if (!byName.ContainsKey(n)) { byName[n] = new List<int>(); names.Add(n); }
                    byName[n].Add(id);
                }
                foreach (var n in names)
                    if (byName[n].Count >= 2)
                        groups.Add(new KeyValuePair<string, List<int>>(A(side ? "GAMEPLAY_DIFF_GROUP_FRIENDLY_SAME_NAME_ENTITIES" : "GAMEPLAY_DIFF_GROUP_OPPONENT_SAME_NAME_ENTITIES", byName[n].Count, n), byName[n]));
            }

            var handled = new HashSet<int>();
            var entries = new List<KeyValuePair<string, P>>();
            var isGroup = new List<bool>();
            foreach (var g in groups)
            {
                if (g.Value.Count < 2) continue;
                bool all = true;
                P? phrase = null;
                foreach (var id in g.Value)
                {
                    P p;
                    if (handled.Contains(id) || !affected.TryGetValue(id, out p) || p.Empty) { all = false; break; }
                    if (phrase == null) phrase = p;
                    else if (phrase.Value.One != p.One || phrase.Value.Many != p.Many) { all = false; break; }
                }
                if (!all || phrase == null) continue;
                foreach (var id in g.Value) handled.Add(id);
                entries.Add(new KeyValuePair<string, P>(g.Key, phrase.Value));
                isGroup.Add(true);
            }
            foreach (var kv in affected)
            {
                if (handled.Contains(kv.Key) || kv.Value.Empty) continue;
                S s;
                if (!before.TryGetValue(kv.Key, out s)) continue;
                entries.Add(new KeyValuePair<string, P>(nameOf(s), kv.Value));
                isGroup.Add(false);
            }
            // merge the entries that share a phrase, in first-seen order
            var order = new List<string>();
            var names2 = new Dictionary<string, List<string>>();
            var phrases = new Dictionary<string, P>();
            var anyGroup = new Dictionary<string, bool>();
            for (int i = 0; i < entries.Count; i++)
            {
                var key = entries[i].Value.One + "\n" + entries[i].Value.Many;
                if (!names2.ContainsKey(key)) { names2[key] = new List<string>(); order.Add(key); phrases[key] = entries[i].Value; anyGroup[key] = false; }
                names2[key].Add(entries[i].Key);
                if (isGroup[i]) anyGroup[key] = true;
            }
            var sentences = new List<string>();
            foreach (var key in order)
            {
                var n = names2[key];
                var p = phrases[key];
                sentences.Add(A("GAMEPLAY_DIFF_ENTITY_SPEECHES_FORMAT", Speech.HumanizeList(n), n.Count > 1 || anyGroup[key] ? p.Many : p.One));
            }
            return Lines(sentences);
        }

        // cards that appeared: added to a hand or deck, summoned, a weapon equipped, a secret, a new hero power
        static string NewEntities(Dictionary<int, S> before, Snap full)
        {
            var now = new List<string>();
            var hand = new[] { new List<string>(), new List<string>() };
            var deck = new[] { new List<string>(), new List<string>() };
            var board = new[] { new List<string>(), new List<string>() };
            var secrets = new List<string>();
            foreach (var a in full.Order)
            {
                S b;
                bool isNew = !before.TryGetValue(a.Id, out b) || (b.Zone == TAG_ZONE.SETASIDE && a.Zone != TAG_ZONE.SETASIDE);
                if (!isNew) continue;
                int side = a.Friendly ? 0 : 1;
                var name = a.Known ? a.Name : Speech.S(K.GLOBAL_CARD);
                if (a.HeroPower && a.Zone == TAG_ZONE.PLAY) { now.Add(A(a.Friendly ? "GAMEPLAY_DIFF_MOVEMENT_PLAYER_HERO_POWER_CHANGED" : "GAMEPLAY_DIFF_MOVEMENT_OPPONENT_HERO_POWER_CHANGED", name)); continue; }
                if (a.Hero) continue;
                // A spell can briefly appear in PLAY while its effect resolves. Only a
                // minion entering PLAY is a summon; reporting every card type here made
                // spells sound as if they had been summoned onto the battlefield.
                if (a.Zone == TAG_ZONE.PLAY && !a.Minion && !a.Weapon) continue;
                switch (a.Zone)
                {
                    case TAG_ZONE.HAND: hand[side].Add(name); break;
                    case TAG_ZONE.DECK: deck[side].Add(name); break;
                    case TAG_ZONE.PLAY:
                        if (a.Weapon) now.Add(a.Friendly ? A("GAMEPLAY_PLAYER_EQUIPPED_WEAPON", name) : A("GAMEPLAY_DIFF_MOVEMENT_OPPONENT_EQUIPPED_WEAPON", name));
                        else board[side].Add(name);
                        break;
                    case TAG_ZONE.SECRET:
                        if (!a.Friendly && a.Secret) { try { secrets.Add(Str.Clean(GameStrings.GetClassName(a.Class))); } catch { } }
                        break;
                }
            }
            var lines = new List<string>(now);
            lines.Add(ZoneText(hand[0], "GAMEPLAY_DIFF_MOVEMENT_CARD_ADDED_TO_PLAYER_HAND", "GAMEPLAY_DIFF_MOVEMENT_CARDS_ADDED_TO_PLAYER_HAND"));
            lines.Add(ZoneText(hand[1], "GAMEPLAY_DIFF_MOVEMENT_CARD_ADDED_TO_OPPONENT_HAND", "GAMEPLAY_DIFF_MOVEMENT_CARDS_ADDED_TO_OPPONENT_HAND"));
            lines.Add(ZoneText(deck[0], "GAMEPLAY_DIFF_MOVEMENT_CARD_ADDED_TO_PLAYER_DECK", "GAMEPLAY_DIFF_MOVEMENT_CARDS_ADDED_TO_PLAYER_DECK"));
            lines.Add(ZoneText(deck[1], "GAMEPLAY_DIFF_MOVEMENT_CARD_ADDED_TO_OPPONENT_DECK", "GAMEPLAY_DIFF_MOVEMENT_CARDS_ADDED_TO_OPPONENT_DECK"));
            lines.Add(ZoneText(board[0], "GAMEPLAY_DIFF_MOVEMENT_CARD_ADDED_TO_PLAYER_BATTLEFIELD", "GAMEPLAY_DIFF_MOVEMENT_CARDS_ADDED_TO_PLAYER_BATTLEFIELD"));
            lines.Add(ZoneText(board[1], "GAMEPLAY_DIFF_MOVEMENT_CARD_ADDED_TO_OPPONENT_BATTLEFIELD", "GAMEPLAY_DIFF_MOVEMENT_CARDS_ADDED_TO_OPPONENT_BATTLEFIELD"));
            if (secrets.Count == 1) lines.Add(A("GAMEPLAY_OPPONENT_CAST_SECRET", secrets[0]));
            else if (secrets.Count > 1) lines.Add(A("GAMEPLAY_OPPONENT_CAST_N_SECRETS", secrets.Count));
            return Lines(lines);
        }

        static string ZoneName(TAG_ZONE z, bool friendly)
        {
            string who = friendly ? "PLAYER" : "OPPONENT";
            switch (z)
            {
                case TAG_ZONE.HAND: return A("GAMEPLAY_DIFF_ZONE_" + who + "_HAND");
                case TAG_ZONE.PLAY: return A("GAMEPLAY_DIFF_ZONE_" + who + "_BATTLEFIELD");
                case TAG_ZONE.SECRET: return A("GAMEPLAY_DIFF_ZONE_" + who + "_SECRETS");
                case TAG_ZONE.DECK: return A("GAMEPLAY_DIFF_ZONE_" + who + "_DECK");
                case TAG_ZONE.GRAVEYARD: return A("GAMEPLAY_DIFF_ZONE_" + who + "_GRAVEYARD");
            }
            return null;
        }

        // cards that moved: draws, discards, returns to hand, control taken, secrets, other moves
        static string Movements(List<KeyValuePair<S, S>> moved, int sourceId, Snap full, Dictionary<int, S> before)
        {
            var draws = new List<string>(); var oppDraws = new List<string>();
            var discards = new List<string>(); var oppDiscards = new List<string>();
            var tookByMe = new List<string>(); var tookByOpp = new List<string>();
            var mySecrets = new List<string>(); var oppSecrets = new List<string>();
            var now = new List<string>();
            var phrases = new Dictionary<int, P>();
            var group = new List<S>();
            var card = Speech.S(K.GLOBAL_CARD);
            foreach (var kv in moved)
            {
                S b = kv.Key, a = kv.Value;
                if (a.Id == sourceId) continue;
                if (b.Zone == TAG_ZONE.SETASIDE) continue;
                var name = a.Known ? a.Name : (b.Known ? b.Name : card);
                if (a.Zone == TAG_ZONE.GRAVEYARD)
                {
                    if (b.Zone != TAG_ZONE.PLAY && b.Zone != TAG_ZONE.SECRET) (a.Friendly ? discards : oppDiscards).Add(name);
                    continue;
                }
                if (b.Zone == TAG_ZONE.HAND && a.Zone == TAG_ZONE.SETASIDE) { (a.Friendly ? discards : oppDiscards).Add(name); continue; }
                if (a.Zone == TAG_ZONE.SETASIDE || a.Zone == TAG_ZONE.REMOVEDFROMGAME)
                {
                    if (b.HeroPower)
                    {
                        int side = b.Friendly ? 0 : 1;
                        if (full.HeroPowerId[side] != 0 && full.HeroPowerId[side] != b.Id)
                        {
                            S hp;
                            if (full.All.TryGetValue(full.HeroPowerId[side], out hp)) now.Add(A(b.Friendly ? "GAMEPLAY_DIFF_MOVEMENT_PLAYER_HERO_POWER_CHANGED" : "GAMEPLAY_DIFF_MOVEMENT_OPPONENT_HERO_POWER_CHANGED", hp.Name));
                        }
                        continue;
                    }
                    if (b.Hero && b.Zone == TAG_ZONE.PLAY) continue;
                    if (a.Zone == TAG_ZONE.SETASIDE) { (a.Friendly ? discards : oppDiscards).Add(name); continue; }
                }
                if (b.Zone == TAG_ZONE.DECK && a.Zone == TAG_ZONE.HAND && b.Friendly == a.Friendly)
                {
                    if (a.Friendly) draws.Add(name); else oppDraws.Add(a.Known && a.Revealed ? a.Name : card);
                    continue;
                }
                if ((b.Zone == TAG_ZONE.PLAY || b.Zone == TAG_ZONE.SECRET) && a.Zone == TAG_ZONE.HAND && b.Friendly == a.Friendly)
                {
                    phrases[a.Id] = a.Friendly ? Pair("MOVEMENT_ENTITY_RETURNED_TO_PLAYER_HAND", "MOVEMENT_MULTIPLE_ENTITIES_RETURNED_TO_PLAYER_HAND")
                                               : Pair("MOVEMENT_ENTITY_RETURNED_TO_OPPONENT_HAND", "MOVEMENT_MULTIPLE_ENTITIES_RETURNED_TO_OPPONENT_HAND");
                    group.Add(b);
                    continue;
                }
                if (b.Zone == TAG_ZONE.PLAY && a.Zone == TAG_ZONE.PLAY && b.Friendly != a.Friendly) { (a.Friendly ? tookByMe : tookByOpp).Add(name); continue; }
                if (a.Zone == TAG_ZONE.SECRET && a.Secret)
                {
                    if (a.Friendly) mySecrets.Add(name);
                    else try { oppSecrets.Add(Str.Clean(GameStrings.GetClassName(a.Class))); } catch { }
                    continue;
                }
                var from = ZoneName(b.Zone, b.Friendly);
                var to = ZoneName(a.Zone, a.Friendly);
                if (from != null && to != null) phrases[a.Id] = Pair("MOVEMENT_ENTITY_MOVED_FROM_ZONE_TO_ZONE", "MOVEMENT_MULTIPLE_ENTITIES_MOVED_FROM_ZONE_TO_ZONE", from, to);
                else if (to != null) phrases[a.Id] = Pair("MOVEMENT_ENTITY_MOVED_TO_ZONE", "MOVEMENT_MULTIPLE_ENTITIES_MOVED_TO_ZONE", to);
                else phrases[a.Id] = Pair("MOVEMENT_ENTITY_MOVED_ZONES_GENERIC", "MOVEMENT_MULTIPLE_ENTITIES_MOVED_ZONES_GENERIC");
                group.Add(b.Known ? b : a);
            }
            var lines = new List<string>(now);
            if (draws.Count > 0) lines.Add(A("GAMEPLAY_PLAYER_DREW_CARDS", Names(draws)));
            if (oppDraws.Count > 0) lines.Add(A("GAMEPLAY_OPPONENT_DREW_CARDS", Names(oppDraws)));
            if (discards.Count > 0) lines.Add(A("GAMEPLAY_PLAYER_DISCARDED_CARDS", Names(discards)));
            if (oppDiscards.Count > 0) lines.Add(A("GAMEPLAY_OPPONENT_DISCARDED_CARDS", Names(oppDiscards)));
            if (tookByMe.Count > 0) lines.Add(A("GAMEPLAY_DIFF_MOVEMENT_CARDS_TAKEN_CONTROL_BY_PLAYER", Names(tookByMe)));
            if (tookByOpp.Count > 0) lines.Add(A("GAMEPLAY_DIFF_MOVEMENT_CARDS_TAKEN_CONTROL_BY_OPPONENT", Names(tookByOpp)));
            if (mySecrets.Count > 0) lines.Add(A("GAMEPLAY_PLAYER_CAST_SECRET", Names(mySecrets)));
            if (oppSecrets.Count == 1) lines.Add(A("GAMEPLAY_OPPONENT_CAST_SECRET", oppSecrets[0]));
            else if (oppSecrets.Count > 1) lines.Add(A("GAMEPLAY_OPPONENT_CAST_N_SECRETS", oppSecrets.Count));
            if (phrases.Count > 0)
            {
                lines.Add(Grouped(phrases, before, sourceId, s => NameInList(s, group)));
            }
            return Lines(lines);
        }
    }
}
