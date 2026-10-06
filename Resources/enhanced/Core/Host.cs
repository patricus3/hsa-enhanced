using System;
using Hearthstone.Progression;
using UnityEngine;

namespace HSAEnhanced.Core
{
    // Without Hearthstone Access: our per-frame work (made on the first key check)
    class Host : MonoBehaviour
    {
        static Host s_host;

        internal static void Ensure()
        {
            if (s_host != null) return;
            var go = new GameObject("HSAEnhancedHost");
            DontDestroyOnLoad(go);
            s_host = go.AddComponent<Host>();
            Claims();
            Log.Info("host: started without Hearthstone Access");
        }

        internal static void Run(System.Collections.IEnumerator routine)
        {
            Ensure();
            s_host.StartCoroutine(routine);
        }

        float m_next;

        void Update()
        {
            try { Launch.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Focus.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Generic.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Hub.Tick(); } catch (Exception e) { Log.Error(e); }
            try { GameModes.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Adventure.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Packs.Tick(); } catch (Exception e) { Log.Error(e); }
            try { ShopScreen.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Arena.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Battlegrounds.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Collection.Tick(); } catch (Exception e) { Log.Error(e); }
            try { TavernBrawl.Tick(); } catch (Exception e) { Log.Error(e); }
            try { BgCollection.Tick(); } catch (Exception e) { Log.Error(e); }
            try { QuestPopup.Tick(); } catch (Exception e) { Log.Error(e); }
            try { RewardChest.Tick(); } catch (Exception e) { Log.Error(e); }
            try { EndMatch.Tick(); } catch (Exception e) { Log.Error(e); }
            try { GameStart.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Announce.Tick(); } catch (Exception e) { Log.Error(e); }
            try { DeckTray.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Friends.Tick(); } catch (Exception e) { Log.Error(e); }
            try { Match.Tick(); } catch (Exception e) { Log.Error(e); }
            try { MercBattle.Tick(); } catch (Exception e) { Log.Error(e); }      // the ability tray opens and closes quickly
            try { EndScreenLog.Tick(); } catch (Exception e) { Log.Error(e); }
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 0.5f;
            try
            {
                Screens();
                CreditsWatcher.Ensure();
                Mercenaries.Tick();
                MercRewards.Tick();
                SetRotation.Tick();
            }
            catch (Exception e) { Log.Error(e); }
        }

        // the game's screens our own readers attach to (with Hearthstone Access its hooks do this)
        static void Screens()
        {
            var lucky = UnityEngine.Object.FindObjectOfType<LuckyDrawDisplay>();
            if (lucky != null && lucky.GetComponent<LuckyDrawWatcher>() == null) lucky.gameObject.AddComponent<LuckyDrawWatcher>().Display = lucky;
            var market = UnityEngine.Object.FindObjectOfType<BlackMarketDisplay>();
            if (market != null && market.GetComponent<BlackMarketWatcher>() == null) market.gameObject.AddComponent<BlackMarketWatcher>().Display = market;
            // the journal: ours while the game shows it
            var journal = UnityEngine.Object.FindObjectOfType<JournalPopup>();
            bool shown = journal != null && journal.gameObject.activeInHierarchy;
            if (shown && !Journal.Active) Journal.Open();
            else if (!shown && Journal.Active) Journal.Close();
        }

        // popups our own screens read themselves
        static void Claims()
        {
            Generic.Claims.Add(go => Mercenaries.Handles(go));
            // the shop window and its purchase box are the shop screen's (its "Are you sure?" dialogs are not)
            Generic.Claims.Add(go => (go.GetComponentInChildren<Shop>(true) != null || go.GetComponentInParent<Shop>() != null
                                                           || go.GetComponentInChildren<StorePurchaseAuth>(true) != null));
            Generic.Claims.Add(go => BlackMarketWatcher.Active > 0 && (go.GetComponentInChildren<BlackMarketMainPage>(true) != null || go.GetComponentInChildren<BlackMarketItemPopup>(true) != null));
            Generic.Claims.Add(go => LuckyDrawWatcher.Active > 0 && go.GetComponentInChildren<LuckyDrawWidget>(true) != null);
            Generic.Claims.Add(go => Journal.Active && go.GetComponentInChildren<JournalPopup>(true) != null || go.GetComponentInParent<JournalPopup>() != null);
        }

        void LateUpdate() { VirtualMouse.Step(); }
    }
}
