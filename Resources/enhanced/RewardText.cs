using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
#if !WITHOUT_HSA
using Accessibility;
#endif
using Hearthstone.DataModels;

namespace HSAEnhanced
{
    // Rewards HSA's reward reader does not know ("1 unknown reward"): the Mercenaries rewards (a new
    // ability, experience, coins, equipment, a mercenary), hero skins, Battlegrounds cosmetics, pets
    // and the rest. Named as the game names them for its reward screens (RewardUtils, the item the
    // reward shows), with the amount and the description it has; a new ability with the mercenary,
    // the ability and its text as its reward card shows them.
    static class RewardText
    {
        // the types HSA reads itself
        static readonly HashSet<Reward.Type> HsaKnows = new HashSet<Reward.Type>
        {
            Reward.Type.ARCANE_DUST, Reward.Type.BOOSTER_PACK, Reward.Type.GOLD, Reward.Type.CARD, Reward.Type.CARD_BACK,
            Reward.Type.MOUNT, Reward.Type.ARCANE_ORBS, Reward.Type.MINI_SET, Reward.Type.FORGE_TICKET, Reward.Type.DECK,
            Reward.Type.BATTLEGROUNDS_TOKEN,
        };

        // start of HSA's AccessibleRewardData.GetLines(); non-null: these lines instead of HSA's
        internal static List<string> Lines(object accessibleReward)
        {
            var data = Ref.Get(accessibleReward, "RewardData") as RewardData ?? Ref.Get(accessibleReward, "<RewardData>k__BackingField") as RewardData;
            if (data == null || HsaKnows.Contains(data.RewardType)) return null;
            var lines = Describe(data);
            Log.Info("reward " + data.RewardType + ": " + (lines.Count == 0 ? "nothing to read" : string.Join(" | ", lines.ToArray())));
            return lines.Count == 0 ? null : lines;
        }

        internal static List<string> Describe(RewardData data)
        {
            var lines = new List<string>();
            try
            {
                // a new ability: the mercenary and the ability, as the reward's cards show them
                var unlock = data as MercenariesAbilityUnlockRewardData;
                if (unlock != null)
                {
                    lines.Add(Str.Word("GLUE_LETTUCE_ABILITY_UNLOCK_HEADER"));
                    var merc = CollectionManager.Get().GetMercenary(unlock.MercenaryId);
                    if (merc != null)
                    {
                        lines.Add(Str.Clean(merc.m_mercName));
                        var ability = merc.GetLettuceAbility(unlock.AbilityId);
                        if (ability != null) lines.Add(Ability(ability));
                    }
                    lines.Add(Str.Word("GLUE_LETTUCE_ABILITY_UNLOCK_FOOTER"));
                    return Clean(lines);
                }
                // experience: the mercenary and what it got
                var exp = data as MercenaryExpRewardData;
                if (exp != null)
                {
                    var merc = CollectionManager.Get().GetMercenary(exp.MercenaryId);
                    lines.Add(Str.Join(merc == null ? null : Str.Clean(merc.m_mercName), Str.Game("GLUE_LETTUCE_MERCENARY_EXP_GAIN", exp.Amount),
                        exp.NumberOfLevelUps > 0 ? Str.Word("GLUE_LETTUCE_MERCENARY_LEVEL_UP") : null));
                    return Clean(lines);
                }
                // Mercenary coins: the game's own words for them ("5 Chromie Coins")
                var coin = data as MercenaryCoinRewardData;
                if (coin != null)
                {
                    var merc = CollectionManager.Get().GetMercenary(coin.MercenaryId);
                    var name = merc == null ? null : Str.Clean(merc.m_mercShortName ?? merc.m_mercName);
                    lines.Add(Str.Game("GLUE_LETTUCE_REWARD_MERCENARY_TASK_COINS_REWARD", coin.Quantity, name) ?? Str.Join(coin.Quantity.ToString(), name));
                    return Clean(lines);
                }
                // the rest: the game's own text for it (it says UNKNOWN for types it has none for),
                // the item its reward screen shows, and what the reward's own data holds
                var text = Str.Clean(RewardUtils.GetRewardText(data));
                if (!string.Equals(text, "UNKNOWN", StringComparison.OrdinalIgnoreCase)) lines.Add(text);
                RewardItemDataModel item = null;
                try { item = data.CreateRewardItem(); } catch { }
                if (item != null)
                {
                    string name = null;
                    try { name = RewardUtils.GetName(item, false); } catch { }
                    lines.Add(Str.Join(Str.Clean(name), item.Quantity > 1 ? item.Quantity.ToString() : null));
                    lines.Add(Str.Clean(item.StandaloneDescription));
                    if (lines.TrueForAll(l => string.IsNullOrEmpty(l)))
                    {
                        var parts = new List<string>();
                        Mail.Collect(item, parts, 1);
                        lines.AddRange(parts);
                    }
                }
                if (lines.TrueForAll(l => string.IsNullOrEmpty(l))) lines.AddRange(FromData(data, 0));
            }
            catch (Exception e) { Log.Error(e); }
            return Clean(lines);
        }

        // what a reward's own data holds, read by what it is: a mercenary by its name, an ability or
        // equipment of that mercenary by its name, a card by its name, amounts, a headline, the
        // rewards of a list one by one
        static List<string> FromData(object data, int depth)
        {
            var lines = new List<string>();
            if (data == null || depth > 2) return lines;
            LettuceMercenary merc = null;
            var t = data.GetType();
            var mercId = t.GetProperty("MercenaryId");
            if (mercId != null && mercId.PropertyType == typeof(int)) merc = CollectionManager.Get().GetMercenary((int)mercId.GetValue(data, null));
            if (merc != null) lines.Add(Str.Clean(merc.m_mercName));
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (p.GetIndexParameters().Length > 0 || p.DeclaringType == typeof(RewardData) || p.Name == "MercenaryId") continue;
                object v;
                try { v = p.GetValue(data, null); } catch { continue; }
                if (v == null) continue;
                var n = p.Name;
                if (v is int && merc != null && (n.EndsWith("AbilityId") || n.EndsWith("EquipmentId")))
                {
                    var ability = merc.GetLettuceAbility((int)v);
                    if (ability != null) lines.Add(Ability(ability));
                }
                else if (v is string && (n == "CardID" || n == "CardId"))
                {
                    var def = DefLoader.Get().GetEntityDef((string)v);
                    if (def != null) lines.Add(Str.Clean(def.GetName()));
                }
                else if (v is int && (n == "Quantity" || n == "Amount" || n == "Count") && (int)v > 0) lines.Add(v.ToString());
                else if (v is string && n.EndsWith("Text")) lines.Add(Str.Clean((string)v));
                else if (v is System.Collections.IEnumerable && !(v is string))
                    foreach (var item in (System.Collections.IEnumerable)v)
                        if (item is RewardData) lines.AddRange(Describe((RewardData)item));
            }
            return lines;
        }

        // an ability as its card shows it: its name and its current tier's text (not every field of its
        // data: card ids, every tier's cost, an unfilled name template)
        static string Ability(LettuceAbility a)
        {
            var name = Str.Clean(a.GetCardName());
            if (name.Length == 0) name = Str.Clean(a.m_abilityName);
            string text = null;
            try
            {
                var id = a.GetCardId();
                var def = string.IsNullOrEmpty(id) ? null : DefLoader.Get().GetEntityDef(id);
                text = def == null ? null : def.GetCardTextInHand();
            }
            catch { }
            return Str.Join(name, Str.Clean(text));
        }

        static List<string> Clean(List<string> lines)
        {
            var result = new List<string>();
            foreach (var l in lines) if (!string.IsNullOrEmpty(l) && !result.Contains(l)) result.Add(l);
            return result;
        }
    }
}
