using System;
using System.Collections.Generic;
using System.Reflection;
using HSAEnhanced.Core;

namespace HSAEnhanced
{
    // What a card says in a match, line by line (docs/combat-spec-1-navigation-reading.md, section 3, with
    // our decisions): line 0 is the name, each status its own line; then cost, stats with the status
    // words of a card in play, text, tribe, type, rarity, in the order HSA reads them. Ours: why a
    // friendly character can't attack (the game's own message), weapons get status words, a hidden cost
    // is not read. Words from the game's and HSA's string tables.
    static class CombatCards
    {
        static string L(string key) { return Speech.S(key); }
        static string F(string key, params object[] args) { return Speech.S(key, args); }

        internal static List<string> Lines(Card card)
        {
            var lines = new List<string>();
            var e = card == null ? null : card.GetEntity();
            if (e == null) return lines;
            try
            {
                if (e.IsHero()) Hero(card, e, lines);
                else if (e.IsHeroPower()) HeroPower(card, e, lines);
                else if (e.IsWeapon()) Weapon(card, e, lines);
                else if (e.IsSecret() && !e.IsControlledByFriendlySidePlayer() && e.GetZone() == TAG_ZONE.SECRET && !e.IsQuest() && !e.IsSideQuest() && !e.IsQuestline()) Header(card, e, Speech.S(K.GLOBAL_SECRET, GameStrings.GetClassName(e.GetClass())), lines);
                else if (e.IsQuest() || e.IsSideQuest() || e.IsQuestline()) Quest(card, e, lines);
                else Normal(card, e, lines);
            }
            catch (Exception ex) { Log.Error(ex); if (lines.Count == 0) lines.Add(Str.Clean(e.GetName())); }
            // ours: the flavor text last (the game shows it when a card is inspected in the collection)
            try { var def = e.GetEntityDef(); if (def != null && lines.Count > 0) lines.Add(Str.Clean(def.GetFlavorText())); } catch { }
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            return lines;
        }

        // a card outside a match (a popup, the collection): read as a card in the hand is
        internal static List<string> Lines(EntityDef d)
        {
            var lines = new List<string>();
            if (d == null) return lines;
            try
            {
                lines.Add(Str.Clean(d.GetName()));
                if (!d.IsHero() || d.GetCost() > 0) lines.Add(Speech.S(K.READ_CARD_COST, d.GetCost()));
                if (d.GetTag(GAME_TAG.HIDE_STATS) != 1)
                {
                    if (d.IsMinion()) lines.Add(Speech.S(K.READ_CARD_ATK_HEALTH, d.GetATK(), d.GetHealth()));
                    else if (d.IsWeapon()) lines.Add(Speech.S(K.READ_CARD_ATK_DURABILITY, d.GetATK(), d.GetHealth()));
                    else if (d.IsLocation()) lines.Add(Speech.S(K.READ_CARD_DURABILITY, d.GetHealth()));
                }
                if (d.IsHero()) lines.Add(Speech.S(K.READ_HERO_CARD_ARMOR, d.GetTag(GAME_TAG.ARMOR)));
                string text = null;
                try { text = d.GetCardTextInHand(); } catch { }
                lines.Add(Str.Clean(text));
                try { lines.Add(Str.Clean(d.GetRaceText())); } catch { }
                try { lines.Add(Str.Clean(GameStrings.GetCardTypeName(d.GetCardType()))); } catch { }
                var rarity = d.GetRarity();
                if (rarity != TAG_RARITY.FREE && rarity != TAG_RARITY.INVALID)
                    lines.Add(Str.Clean(GameStrings.GetRarityText(d.IsElite() ? TAG_RARITY.LEGENDARY : rarity)));
                try { lines.Add(Str.Clean(d.GetFlavorText())); } catch { }
            }
            catch (Exception ex) { Log.Error(ex); }
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            return lines;
        }

        // a hidden card (the opponent's hand, a face-down card): the game shows it as a card back
        internal static string HiddenName() { return Speech.S(K.GLOBAL_CARD); }

        #region Building blocks
        static void Header(Card card, Entity e, string name, List<string> lines)
        {
            lines.Add(Str.Clean(name));
            if (e.HasTag(GAME_TAG.EVIL_GLOW)) lines.Add(Speech.S(K.GLOBAL_CURSED));
            if (e.HasTag(GAME_TAG.VALEERASHADOW)) lines.Add(Speech.S(K.GLOBAL_HAUNTED));
            if (Ready(card, e)) lines.Add(Speech.S(K.GLOBAL_READY));
            else
            {
                var why = WhyNot(card, e);
                if (why != null) lines.Add(why);
            }
            if (e.GetZone() == TAG_ZONE.SECRET)
            {
                string banner = null;
                try { banner = e.GetCustomObjectiveBannerText(); } catch { }
                if (!string.IsNullOrEmpty(banner)) lines.Add(Str.Clean(banner));
            }
        }

        // the game's green / yellow glow (playable, can attack, usable); a location: in play and not exhausted
        static bool Ready(Card card, Entity e)
        {
            if (e.IsLocation()) return e.GetZone() == TAG_ZONE.PLAY && !e.IsExhausted() && e.IsControlledByFriendlySidePlayer();
            var actor = card.GetActor();
            if (actor == null) return false;
            var state = actor.GetActorStateType();
            return state == ActorStateType.CARD_POWERED_UP || state == ActorStateType.CARD_COMBO;
        }

        static readonly MethodInfo ErrorDescription = typeof(PlayErrors).GetMethod("GetErrorDescription", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        // ours: a friendly minion or hero on the board, on your turn, that can't attack: why, as the game
        // tells a player who tries (frozen, no attack, can't attack...)
        static string WhyNot(Card card, Entity e)
        {
            var gs = GameState.Get();
            if (gs == null || !gs.IsFriendlySidePlayerTurn() || !gs.IsInMainOptionMode()) return null;
            if (!e.IsControlledByFriendlySidePlayer() || e.GetZone() != TAG_ZONE.PLAY || !(e.IsMinion() || e.IsHero())) return null;
            if (e.IsHero() && e.GetATK() <= 0) return null;     // a hero without attack is not expected to attack
            if (gs.HasResponse(e)) return null;
            try
            {
                var type = gs.GetErrorType(e);
                if (type == PlayErrors.ErrorType.NONE || ErrorDescription == null) return null;
                var text = ErrorDescription.Invoke(null, new object[] { type, gs.GetErrorParam(e), e, null }) as string;
                return Str.Clean(text);
            }
            catch { return null; }
        }

        static bool CostHidden(Entity e) { return e.HasTag(GAME_TAG.HIDE_STATS) || e.HasTag(GAME_TAG.HIDE_COST); }

        // ours: no cost line when the game hides the cost (HSA said "0 mana")
        static string Cost(Entity e)
        {
            if (CostHidden(e)) return null;
            if (e.GetRealTimeCardCostsHealth()) return Speech.S(K.READ_HERO_CARD_HEALTH, e.GetCost());
            return Speech.S(K.READ_CARD_COST, e.GetCost());
        }

        static bool StatsHidden(Entity e)
        {
            if ((e.IsDormant() || e.HasTag(GAME_TAG.DORMANT_VISUAL)) && !e.IsLaunchpad()) return true;
            return e.GetTag(GAME_TAG.HIDE_STATS) == 1 && !e.IsStarship();
        }

        static string Resources(Entity e)
        {
            if (StatsHidden(e)) return null;
            if (e.IsMinion()) return Speech.S(K.READ_CARD_ATK_HEALTH, e.GetATK(), e.GetCurrentHealth());
            if (e.IsWeapon()) return Speech.S(K.READ_CARD_ATK_DURABILITY, e.GetATK(), e.GetCurrentHealth());
            if (e.IsLocation()) return Speech.S(K.READ_CARD_DURABILITY, e.GetCurrentHealth());
            if (e.IsHero())
            {
                var parts = new List<string>();
                if (e.GetATK() > 0 && !e.HasTag(GAME_TAG.HIDE_ATTACK)) parts.Add(Speech.S(K.READ_HERO_CARD_ATK, e.GetATK()));
                if (e.GetArmor() > 0) parts.Add(Speech.S(K.READ_HERO_CARD_ARMOR, e.GetArmor()));
                if (!e.HasTag(GAME_TAG.HIDE_HEALTH)) parts.Add(Speech.S(K.READ_HERO_CARD_HEALTH, e.GetCurrentHealth()));
                return parts.Count == 0 ? null : Speech.HumanizeList(parts);
            }
            return null;
        }

        // status words of a card in play, in HSA's order
        static string Effects(Entity e)
        {
            if (e.GetZone() != TAG_ZONE.PLAY) return null;
            var words = new List<string>();
            Action<bool, string> add = (on, key) => { if (on) { var w = Str.Word(key); if (w.Length > 0) words.Add(w); } };
            add(e.IsSilenced(), "GLOBAL_KEYWORD_SILENCE");
            add(e.HasDivineShield(), "GLOBAL_KEYWORD_DIVINE_SHIELD");
            add(e.IsFrozen(), "GLOBAL_KEYWORD_FROZEN");
            add(e.HasLifesteal(), "GLOBAL_KEYWORD_LIFESTEAL");
            add(e.HasDeathrattle(), "GLOBAL_KEYWORD_DEATHRATTLE");
            add(e.IsPoisonous(), "GLOBAL_KEYWORD_POISONOUS");
            add(e.IsStealthed(), "GLOBAL_KEYWORD_STEALTH");
            add(e.HasTaunt(), "GLOBAL_KEYWORD_TAUNT");
            add(e.HasTag(GAME_TAG.ELUSIVE), "GLOBAL_KEYWORD_ELUSIVE");
            add(e.IsImmune(), "GLOBAL_KEYWORD_IMMUNE");
            add(e.IsDormant(), "GLOBAL_KEYWORD_DORMANT");
            add(e.HasReborn(), "GLOBAL_KEYWORD_REBORN");
            add(e.HasWindfury(), "GLOBAL_KEYWORD_WINDFURY");
            add(e.IsVenomous(), "GLOBAL_KEYWORD_VENOMOUS");
            add(e.HasTag(GAME_TAG.HAS_DARK_GIFT), "GLOBAL_KEYWORD_DARKGIFT");
            return words.Count == 0 ? null : Speech.HumanizeList(words);
        }

        // stats with the status words after them (a card in play); the words alone when there are no stats
        static string ResourcesAndEffects(Entity e)
        {
            var r = Resources(e);
            var fx = Effects(e);
            if (string.IsNullOrEmpty(r)) return fx;
            return string.IsNullOrEmpty(fx) ? r : r + " " + fx;
        }

        static string Description(Entity e)
        {
            if (e.GetZone() == TAG_ZONE.PLAY && e.IsMinion() && e.IsSilenced()) return Str.Word("GLOBAL_KEYWORD_SILENCE");
            string text = null;
            try { text = e.GetCardTextBuilder().BuildCardTextInHand(e); } catch { }
            if (string.IsNullOrEmpty(text)) try { text = e.GetCardTextInHand(); } catch { }
            return Str.Clean(text);
        }

        static string Race(Entity e)
        {
            try { var def = e.GetEntityDef(); return def == null ? null : Str.Clean(def.GetRaceText()); } catch { return null; }
        }

        static string Type(Entity e) { try { return Str.Clean(GameStrings.GetCardTypeName(e.GetCardType())); } catch { return null; } }

        static string Rarity(Entity e)
        {
            var rarity = e.GetRarity();
            if (rarity == TAG_RARITY.FREE || rarity == TAG_RARITY.INVALID) return null;
            if (e.IsElite()) return GameStrings.GetRarityText(TAG_RARITY.LEGENDARY);
            return Str.Clean(GameStrings.GetRarityText(rarity));
        }
        #endregion

        #region Card kinds
        static void Normal(Card card, Entity e, List<string> lines)
        {
            Header(card, e, e.GetName(), lines);
            var zone = e.GetZone();
            if (zone == TAG_ZONE.PLAY)
            {
                lines.Add(ResourcesAndEffects(e));
                lines.Add(Description(e));
                lines.Add(Race(e));
                if (e.IsMinion()) lines.Add(Cost(e));
                lines.Add(Rarity(e));
                return;
            }
            if (zone == TAG_ZONE.SECRET)
            {
                lines.Add(Resources(e));
                lines.Add(Description(e));
                lines.Add(Race(e));
                lines.Add(Type(e));
                lines.Add(Rarity(e));
                return;
            }
            lines.Add(Cost(e));
            lines.Add(Resources(e));
            lines.Add(Description(e));
            lines.Add(Race(e));
            lines.Add(Type(e));
            lines.Add(Rarity(e));
        }

        static void Hero(Card card, Entity e, List<string> lines)
        {
            if (e.GetZone() == TAG_ZONE.PLAY)
            {
                bool friendly = e.IsControlledByFriendlySidePlayer();
                Header(card, e, L(friendly ? K.GAMEPLAY_ZONE_PLAYER_HERO : K.GAMEPLAY_ZONE_OPPONENT_HERO), lines);
                lines.Add(ResourcesAndEffects(e));
                try { lines.Add(Str.Clean(e.GetEntityDef().GetName())); } catch { }
                var gs = GameState.Get();
                var player = e.GetController();
                if (player != null && GameMgr.Get() != null && GameMgr.Get().IsPlay())
                {
                    try { lines.Add(Str.Clean(player.GetName())); } catch { }
                    try { lines.Add(Str.Clean(GameStrings.GetClassName(e.GetClass()))); } catch { }
                }
                return;
            }
            Header(card, e, e.GetName(), lines);
            lines.Add(Cost(e));
            lines.Add(Type(e));
            lines.Add(Speech.S(K.READ_HERO_CARD_ARMOR, e.GetArmor()));
            lines.Add(Description(e));
            try
            {
                var powerId = GameUtils.GetHeroPowerCardIdFromHero(e.GetCardId());
                var power = string.IsNullOrEmpty(powerId) ? null : DefLoader.Get().GetEntityDef(powerId);
                if (power != null)
                {
                    lines.Add(Str.Join(Speech.S(K.GAMEPLAY_ZONE_PLAYER_HERO_POWER), Str.Clean(power.GetName())));
                    lines.Add(Speech.S(K.READ_CARD_COST, power.GetCost()));
                    lines.Add(Str.Clean(power.GetCardTextInHand()));
                }
            }
            catch { }
            lines.Add(Rarity(e));
        }

        static void HeroPower(Card card, Entity e, List<string> lines)
        {
            Header(card, e, e.GetName(), lines);
            lines.Add(Cost(e));
            lines.Add(Description(e));
        }

        // ours: a weapon gets its status words like minions and heroes
        static void Weapon(Card card, Entity e, List<string> lines)
        {
            bool wielded = card.GetZone() is ZoneWeapon;
            Header(card, e, e.GetName(), lines);
            if (!wielded) lines.Add(Cost(e));
            lines.Add(ResourcesAndEffects(e));
            lines.Add(Description(e));
            if (!wielded) lines.Add(Type(e));
            lines.Add(Rarity(e));
        }

        static void Quest(Card card, Entity e, List<string> lines)
        {
            Header(card, e, e.GetName(), lines);
            lines.Add(Description(e));
            int total = e.GetTag(GAME_TAG.QUEST_PROGRESS_TOTAL);
            if (total > 0) lines.Add(Speech.S(K.TOAST_QUEST_PROGRESS_TOAST_PROGRESS, e.GetTag(GAME_TAG.QUEST_PROGRESS), total));
            if (e.IsSideQuest()) return;
            try
            {
                var rewardId = QuestController.GetRewardCardIDFromQuestCardID(e);
                var reward = string.IsNullOrEmpty(rewardId) ? null : DefLoader.Get().GetEntityDef(rewardId);
                if (reward != null)
                {
                    lines.Add(Speech.S(K.UI_QUEST_REWARD_DESCRIPTION, Str.Clean(reward.GetName())));
                    lines.Add(Str.Clean(reward.GetCardTextInHand()));
                }
            }
            catch { }
        }
        #endregion

        #region K: printed stats and enchantments (ours: from the game's data, no hovering needed)
        internal static List<string> OriginalStats(Card card)
        {
            var lines = new List<string>();
            var e = card == null ? null : card.GetEntity();
            if (e == null || !e.IsMinion()) return lines;
            var def = e.GetEntityDef();
            if (def != null && !e.HasTag(GAME_TAG.HIDE_STATS)) lines.Add(Speech.S(K.READ_CARD_ATK_HEALTH, def.GetATK(), def.GetHealth()));
            var seen = new Dictionary<string, int>();
            var order = new List<string>();
            var texts = new Dictionary<string, string>();
            try
            {
                foreach (var en in e.GetDisplayedEnchantments())
                {
                    if (en == null) continue;
                    var name = Str.Clean(en.GetName());
                    string text = null;
                    try { text = Str.Clean(en.GetCardTextInHand()); } catch { }
                    var key = name + "|" + text;
                    int n = Math.Max(en.GetTag(GAME_TAG.SPAWN_TIME_COUNT), 1);
                    if (seen.ContainsKey(key)) seen[key] += n; else { seen[key] = n; order.Add(key); texts[key] = text; }
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            foreach (var key in order)
            {
                var name = key.Substring(0, key.IndexOf('|'));
                lines.Add(Str.Word("GLOBAL_CARDTYPE_ENCHANTMENT"));
                lines.Add(seen[key] > 1 ? Str.Game("GAMEPLAY_ENCHANTMENT_MULTIPLIER_HEADER", seen[key], name) : name);
                lines.Add(texts[key]);
            }
            lines.RemoveAll(l => string.IsNullOrEmpty(l));
            return lines;
        }
        #endregion

        #region I: the card's keywords (ours: from the game's keyword table, no hovering needed)
        internal static List<string> Keywords(Card card)
        {
            var lines = new List<string>();
            var e = card == null ? null : card.GetEntity();
            if (e == null) return lines;
            try
            {
                foreach (GAME_TAG tag in Enum.GetValues(typeof(GAME_TAG)))
                {
                    if (!e.HasTag(tag) || !GameStrings.HasKeywordName(tag)) continue;
                    var name = Str.Clean(GameStrings.GetKeywordName(tag));
                    var text = Str.Clean(GameStrings.GetKeywordText(tag));
                    if (name.Length == 0 || lines.Contains(name)) continue;
                    lines.Add(name);
                    if (text.Length > 0) lines.Add(text);
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            return lines;
        }
        #endregion
    }
}
