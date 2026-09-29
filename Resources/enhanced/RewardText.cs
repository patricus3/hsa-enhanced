using System;
using System.Collections.Generic;
using Accessibility;
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
                        if (ability != null)
                        {
                            var model = new LettuceAbilityDataModel();
                            CollectionUtils.PopulateAbilityDataModel(model, ability, merc);
                            var parts = new List<string>();
                            Mail.Collect(model, parts, 2);
                            lines.Add(Str.Join(parts.ToArray()));
                        }
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
                // the rest: the game's own text for it, and the item its reward screen shows
                lines.Add(Str.Clean(RewardUtils.GetRewardText(data)));
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
            }
            catch (Exception e) { Log.Error(e); }
            return Clean(lines);
        }

        static List<string> Clean(List<string> lines)
        {
            var result = new List<string>();
            foreach (var l in lines) if (!string.IsNullOrEmpty(l) && !result.Contains(l)) result.Add(l);
            return result;
        }
    }
}
