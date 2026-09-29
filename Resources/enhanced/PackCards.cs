using System.Collections.Generic;
using Accessibility;
using PegasusLettuce;

namespace HSAEnhanced
{
    // Mercenaries pack cards (HSA reads every pack card as a normal card). A revealed card is a
    // mercenary (new, or one you have), a portrait of one (golden, diamond ...) or that
    // mercenary's coins; it is read as such, with the mercenary's role and all its abilities.
    static class PackCards
    {
        // start of HSA's AccessiblePackOpeningCard.GetLines(); null: HSA's own lines
        internal static List<string> Lines(object accessibleCard)
        {
            var card = Ref.Get<PackOpeningCard>(accessibleCard, "m_card");
            if (card == null || !card || !card.IsRevealed()) return null;
            var pack = Ref.Get<LettucePackComponent>(card, "m_mercenaryPackComponent");
            if (pack == null || !pack.HasMercenaryId) return null;
            var merc = CollectionManager.Get().GetMercenary(pack.MercenaryId, false, false);
            var def = card.GetEntityDef();
            string name = merc != null ? Str.Clean(merc.m_mercName) : def != null ? Str.Clean(def.GetName()) : "";
            string shortName = merc != null && !string.IsNullOrEmpty(merc.m_mercShortName) ? Str.Clean(merc.m_mercShortName) : name;
            var lines = new List<string>();
            bool portrait = pack.HasMercenaryArtVariationId && pack.MercenaryArtVariationId != 0;
            if (pack.HasCurrencyAmount && pack.CurrencyAmount > 0 && !portrait)
            {
                lines.Add(Str.Game("GLUE_LETTUCE_REWARD_MERCENARY_TASK_COINS_REWARD", pack.CurrencyAmount, shortName) ?? (pack.CurrencyAmount + " " + shortName));
                return lines;
            }
            if (portrait)
                lines.Add(Str.Join(Str.Word("GLUE_MERCENARY_LABEL_PORTRAIT"), Premium(pack.MercenaryArtVariationPremium), name));
            else
                lines.Add(Str.Join(name, pack.MercenaryAlreadyAcquired ? null : Str.Word("GLUE_LETTUCE_MERCENARY_REWARD_TITLE")));
            if (merc != null)
            {
                string role = null;
                try { role = GameStrings.GetRoleName(merc.m_role); } catch { }
                if (!string.IsNullOrEmpty(role)) lines.Add(Str.Clean(role));
                foreach (var ability in merc.m_abilityList)
                {
                    if (ability == null) continue;
                    var id = ability.GetCardId();
                    var adef = string.IsNullOrEmpty(id) ? null : DefLoader.Get().GetEntityDef(id);
                    string text = null;
                    try { text = adef == null ? null : adef.GetCardTextInHand(); } catch { }
                    var abilityName = Str.Clean(ability.GetCardName());
                    if (abilityName.Length == 0 && adef != null) abilityName = Str.Clean(adef.GetName());
                    lines.Add(Str.Join(abilityName, Str.Clean(text)));
                }
            }
            return lines;
        }

        static string Premium(int premium)
        {
            switch (premium)
            {
                case 1: return Str.Word("GLOBAL_COLLECTION_GOLDEN");
                case 2: return Str.Word("GLOBAL_COLLECTION_DIAMOND");
                default: return null;
            }
        }
    }
}
