using System.Collections;
using Accessibility;
using UnityEngine;

namespace HSAEnhanced
{
    // Picking up a card from the hand. HSA presses the virtual mouse where the card is; while the
    // hand is still moving (a card drawn or discovered this turn slides in and the others make
    // room) the pointer can sit on a neighbour, and HSA then says "try again" but clicks anyway,
    // so the neighbour is played. The game takes the card whose stand-in is under the pointer:
    // the click goes ahead only when that is the card being read, waiting for the hand to settle.
    static class HandClick
    {
        static bool s_bypass;       // our own call of HSA's click, once the pointer is on the card
        static int s_waiting;       // a click waiting for the hand (a new one replaces it)
        const float Patience = 1.5f;

        // HSA's ClickCard(bool) (there is a ClickCard(Card) too)
        static readonly System.Reflection.MethodInfo ClickMethod = typeof(AccessibleGameplay).GetMethod("ClickCard",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, new[] { typeof(bool) }, null);

        // start of HSA's AccessibleGameplay.ClickCard(bool); true: HSA's click is not made now
        internal static bool Before(object gameplay, bool performingDeckAction)
        {
            if (s_bypass) return false;
            var card = CardBeingRead(gameplay);
            if (card == null || !InFriendlyHand(card) || OnCard(card)) return false;
            Log.Info("hand: pointer not on " + card.GetEntity().GetName() + " yet, waiting for the hand to settle");
            Core.Jobs.Run(ClickWhenOnCard(gameplay, card, performingDeckAction, ++s_waiting));
            return true;
        }

        static Card CardBeingRead(object gameplay)
        {
            var read = Ref.Get(gameplay, "m_cardBeingRead") as AccessibleCard;
            return read == null ? null : read.GetCard();
        }

        static bool InFriendlyHand(Card card)
        {
            var hand = card.GetZone() as ZoneHand;
            return hand != null && card.GetController() != null && card.GetController().IsFriendlySide();
        }

        // what a click now would pick: the card whose stand-in (or actor) is under the pointer
        static bool OnCard(Card card)
        {
            var input = InputManager.Get();
            if (input == null || input.GetMousedOverCard() != card) return false;
            RaycastHit hit;
            if (!UniversalInputManager.Get().GetInputHitInfo((GameLayer)8, out hit)) return false;
            var standIn = hit.transform.GetComponentInParent<CardStandIn>();
            if (standIn != null) return standIn.linkedCard == card;
            var actor = hit.transform.GetComponentInParent<Actor>();
            return actor != null && actor.GetCard() == card;
        }

        static IEnumerator ClickWhenOnCard(object gameplay, Card card, bool performingDeckAction, int id)
        {
            float until = Time.unscaledTime + Patience;
            while (Time.unscaledTime < until)
            {
                yield return null;
                // another click, another card read, or the card left the hand: this one is off
                if (id != s_waiting || card == null || !InFriendlyHand(card) || CardBeingRead(gameplay) != card) yield break;
                // HSA keeps moving the pointer onto the card being read every frame
                InputManager.Get().SetMousedOverCard(card);
                if (!OnCard(card)) continue;
                Log.Info("hand: pointer on " + card.GetEntity().GetName() + ", clicking");
                s_bypass = true;
                try { if (ClickMethod != null) ClickMethod.Invoke(gameplay, new object[] { performingDeckAction }); }
                finally { s_bypass = false; }
                yield break;
            }
            if (id != s_waiting) yield break;
            // never on it: nothing is played (rather than the card next to it)
            Log.Info("hand: pointer never reached " + card.GetEntity().GetName() + ", not clicked");
            AccessibilityMgr.Output(gameplay as AccessibleComponent, LocalizationUtils.Get(LocalizationKey.GAMEPLAY_TRY_AGAIN));
        }
    }
}
