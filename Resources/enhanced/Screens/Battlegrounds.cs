#if WITHOUT_HSA
using System;
using System.Collections;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Battlegrounds, from the game's own data and options:
    //   the lobby: rating, first places, solo / duos, Play, Switch to duos or solo, the collection,
    //     the shop
    //   in a match, over the match screen while there is something to decide:
    //     the hero choice: each hero with its armor and hero power (Enter picks and confirms it),
    //       the minion types of the game, rerolls (those that spend tokens: Enter twice)
    //     the recruit phase: gold and tavern tier, Bob's minions (Enter buys), the hand (Enter
    //       plays to the end of the board), the board (Enter sells), Freeze / Refresh / Upgrade with
    //       their costs, the hero power, End Turn
    //   Targets, discovers and the combat are the match screen's.
    static class Battlegrounds
    {
        static BgLobbyUI s_lobby;
        static BgMatchUI s_match;

        internal static bool InLobby
        {
            get
            {
                var scenes = SceneMgr.Get();
                if (scenes == null || scenes.GetMode() != SceneMgr.Mode.BACON || scenes.IsTransitioning() || !scenes.IsSceneLoaded() || GameState.Get() != null) return false;
                var display = BaconDisplay.Get();
                string why;
                return display != null && display.IsFinishedLoading(out why);
            }
        }

        // a step of a Battlegrounds match that is ours (the hero choice, the recruit phase)
        internal static string MatchStep
        {
            get
            {
                var gs = GameState.Get();
                var mgr = GameMgr.Get();
                if (gs == null || mgr == null || !mgr.IsBattlegrounds()) return null;
                if (gs.IsInTargetMode() || gs.IsInChoiceMode()) return null;
                var mulligan = MulliganManager.Get();
                if (mulligan != null && mulligan.IsMulliganActive() && gs.GetBooleanGameOption(GameEntityOption.MULLIGAN_IS_CHOOSE_ONE)) return "heroes";
                var shop = gs.GetGameEntity() as TB_BaconShop;
                if (shop != null && shop.IsShopPhase() && gs.IsFriendlySidePlayerTurn()) return "shop";
                return null;
            }
        }

        // every frame
        internal static void Tick()
        {
            if (s_lobby != null && !s_lobby.Alive) { var old = s_lobby; s_lobby = null; Focus.Pop(old); }
            if (s_lobby == null && InLobby)
            {
                var ui = new BgLobbyUI();
                if (ui.Refresh(true)) { s_lobby = ui; Generic.Yield(); Focus.PushBase(ui); if (ui.Focused) ui.Read(); }
            }
            if (s_lobby != null) s_lobby.Refresh(false);

            var step = MatchStep;
            if (s_match != null && step == null) { var old = s_match; s_match = null; Focus.Pop(old); }
            if (s_match == null && step != null)
            {
                var ui = new BgMatchUI();
                if (ui.Refresh(true)) { s_match = ui; Focus.Push(ui); ui.Read(); }
            }
            if (s_match != null) s_match.Refresh(false);
        }
    }

    // a menu rebuilt from the game's data when it changes, read out when the step changes
    abstract class BgUI : Core.Screen
    {
        protected Menu m_menu;
        string m_key, m_signature;
        float m_next;

        protected abstract bool Build(out string key, out string title, List<GameButton> items);
        protected abstract void Back();

        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_menu != null;
            m_next = Time.unscaledTime + 0.3f;
            string key, title; var items = new List<GameButton>();
            if (!Build(out key, out title, items)) return m_menu != null;
            var sig = key + "\n" + title + "\n" + string.Join("\n", items.ConvertAll(b => b.Label).ToArray());
            bool newStep = key != m_key;
            if (!newStep && sig == m_signature) return true;
            var at = m_menu == null || newStep ? 0 : m_menu.Index;
            var menu = new Menu(this, title, Back);
            foreach (var b in items) { var click = b.Click; menu.AddOption(b.Label, () => click()); }
            menu.Index = at;
            m_menu = menu;
            m_signature = sig;
            m_key = key;
            if (newStep)
            {
                Log.Info("battlegrounds: " + key + " '" + title + "': " + GameButton.Describe(items));
                if (Focused) m_menu.StartReading();
            }
            return true;
        }

        protected static void Add(List<GameButton> items, Component anchor, string label, Action click)
        {
            if (string.IsNullOrEmpty(label)) return;
            var said = label;
            items.Add(new GameButton { Target = anchor, Label = label, Click = click ?? (() => Speech.Say(said)) });
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }

    // ---- the lobby ----------------------------------------------------------------------------

    class BgLobbyUI : BgUI
    {
        internal override bool Alive { get { return Battlegrounds.InLobby; } }

        protected override bool Build(out string key, out string title, List<GameButton> items)
        {
            key = null; title = "";
            var display = BaconDisplay.Get();
            var model = display == null ? null : display.GetBaconLobbyDataModel();
            if (model == null) return false;
            bool duos = model.BattlegroundsInDuosMode;
            key = "lobby:" + duos;
            title = Str.Join(SceneNames.Of(SceneMgr.Mode.BACON), duos ? Str.Word("GLUE_BACON_INFO_POPUP_DUOS_RULES") : null);
            Add(items, display, Str.Join(Str.Word("GLUE_BACON_RATING_LABEL"), model.Rating.ToString(), Str.Word("GLUE_BACON_FIRST_PLACE_LABEL"), model.FirstPlaceFinishes.ToString()), null);
            var finding = GameMgr.Get() != null && GameMgr.Get().IsFindingGame();
            if (!finding)
                Add(items, display, Str.Word("GLOBAL_PLAY"), () => { Log.Info("battlegrounds: play"); display.PlayButtonRelease(null); });
            else
                Add(items, display, Str.Word("GLOBAL_CANCEL"), () => { Log.Info("battlegrounds: cancel the queue"); GameMgr.Get().CancelFindGame(); });
            Add(items, display, Str.Clean(duos ? model.FooterTextDuos : model.FooterText), null);
            Add(items, display, Str.Word("GLUE_BACON_LOBBY_TOGGLE_TUTORIAL"), () => { Log.Info("battlegrounds: solo / duos"); display.ToggleDuosRelease(null); });
            if (model.BattlegroundsSkinsEnabled)
                Add(items, display, Str.Word("GLUE_BACON_COLLECTION"), () => { Log.Info("battlegrounds: collection"); Ref.Call(display, "OpenBattlegroundsCollection"); });
            if (model.ShopOpen)
                Add(items, display, Str.Word("GLUE_STORE_HEADLINE"), () => { Log.Info("battlegrounds: shop"); display.OpenBattlegroundsShop("battlegrounds"); });
            return true;
        }

        protected override void Back()
        {
            Log.Info("battlegrounds: back");
            var display = BaconDisplay.Get();
            if (display != null) display.BackButtonRelease(null); else Navigation.GoBack();
        }
    }

    // ---- in a match ---------------------------------------------------------------------------

    class BgMatchUI : BgUI
    {
        float m_armedUntil;
        int m_armedHero;

        internal override bool Alive { get { return Battlegrounds.MatchStep != null; } }

        static GameState GS { get { return GameState.Get(); } }

        protected override bool Build(out string key, out string title, List<GameButton> items)
        {
            key = null; title = "";
            var step = Battlegrounds.MatchStep;
            if (step == "heroes") return Heroes(ref key, ref title, items);
            if (step == "shop") return Shop(ref key, ref title, items);
            return false;
        }

        // ---- the hero choice ----------------------------------------------------------------------

        bool Heroes(ref string key, ref string title, List<GameButton> items)
        {
            var mulligan = MulliganManager.Get();
            var cards = mulligan.GetStartingCards();
            if (cards == null || cards.Count == 0) return false;
            key = "heroes";
            title = Str.Word("GAMEPLAY_BACON_MULLIGAN_CHOOSE_HERO_BANNER");
            var anchor = (Component)mulligan;
            foreach (var card in cards)
            {
                var e = card == null ? null : card.GetEntity();
                if (e == null) continue;
                var c = card;
                bool locked = e.HasTag(GAME_TAG.BACON_LOCKED_MULLIGAN_HERO);
                Add(items, anchor, Str.Join(Hero(e), locked ? Str.Locked : null), () =>
                {
                    if (locked) { Speech.Say(Str.Locked); return; }
                    Log.Info("battlegrounds: hero " + c.GetEntity().GetName());
                    mulligan.ToggleHoldState(c);
                    Jobs.Run(After(0.4f, () => Ref.Call(mulligan, "OnMulliganButtonReleased", new object[] { null })));
                });
            }
            // the minion types of this game
            var races = new List<string>();
            try { foreach (var r in GS.GetAvailableRacesInBattlegroundsExcludingAmalgam()) races.Add(Str.Clean(GameStrings.GetRaceName(r))); } catch { }
            if (races.Count > 0) Add(items, anchor, Str.Join(Str.Word("GAMEPLAY_BACON_MULLIGAN_AVAILABLE_RACES"), string.Join(", ", races.ToArray())), null);
            // rerolls (a free one at once; one that spends a token: Enter twice)
            foreach (var card in cards)
            {
                var e = card == null ? null : card.GetEntity();
                if (e == null || !e.ShouldShowHeroRerollButton()) continue;
                var result = e.ShouldEnableRerollButton();
                if ((int)result > 3) continue;
                bool paid = result == Entity.RerollButtonEnableResult.REROLL || result == Entity.RerollButtonEnableResult.UNLOCK;
                var label = Str.Join(Str.Word("GLUE_BACON_REROLL"), Str.Clean(e.GetName()));
                var hero = e;
                Add(items, anchor, label, () =>
                {
                    if (paid && (m_armedHero != hero.GetEntityId() || Time.unscaledTime > m_armedUntil))
                    {
                        m_armedHero = hero.GetEntityId(); m_armedUntil = Time.unscaledTime + 6f;
                        Speech.Say(Str.Join(label, Str.Word("GLOBAL_CONFIRM"), Keys.Enter.Name));
                        return;
                    }
                    m_armedHero = 0;
                    Log.Info("battlegrounds: reroll " + hero.GetName());
                    mulligan.RequestHeroReroll(hero);
                });
            }
            return true;
        }

        static string Hero(Entity e)
        {
            string power = null;
            try
            {
                var id = GameUtils.GetHeroPowerCardIdFromHero(e.GetCardId());
                var def = string.IsNullOrEmpty(id) ? null : DefLoader.Get().GetEntityDef(id);
                if (def != null) power = Str.Join(Str.Clean(def.GetName()), def.GetCost().ToString(), Str.Clean(def.GetCardTextInHand()));
            }
            catch { }
            var armor = e.GetArmor();
            return Str.Join(Str.Clean(e.GetName()), armor > 0 ? armor.ToString() : null, power);
        }

        // ---- the recruit phase --------------------------------------------------------------------

        bool Shop(ref string key, ref string title, List<GameButton> items)
        {
            var gs = GS;
            var me = gs.GetFriendlySidePlayer();
            var bob = gs.GetOpposingSidePlayer();
            var shop = gs.GetGameEntity() as TB_BaconShop;
            if (me == null || bob == null || shop == null) return false;
            key = "shop";
            var gold = me.GetNumAvailableResources();
            title = Str.Join(Str.Word("GLUE_SHOP_GOLD"), gold + "/" + me.GetTag(GAME_TAG.RESOURCES), Str.Word("GAMEPLAY_BACON_TAVERN_TIER"), me.GetTag(GAME_TAG.PLAYER_TECH_LEVEL).ToString());
            var anchor = (Component)me.GetHeroCard();
            // Bob's minions: buy
            foreach (var card in bob.GetBattlefieldZone().GetCards())
            {
                var e = card == null ? null : card.GetEntity();
                if (e == null) continue;
                var target = e;
                Add(items, card, Str.Join(Str.Word("GAMEPLAY_BACON_DRAGBUY_BUTTON_TUTORIAL"), Minion(e), e.GetCost().ToString()), () => Act(target, GAME_TAG.TAG_NOT_SET, 0));
            }
            // the hand: play to the end of the board
            foreach (var card in me.GetHandZone().GetCards())
            {
                var e = card == null ? null : card.GetEntity();
                if (e == null) continue;
                var target = e;
                Add(items, card, Str.Join(Speech.S(K.GAMEPLAY_ZONE_PLAYER_HAND), Minion(e)), () => Act(target, GAME_TAG.TAG_NOT_SET, me.GetBattlefieldZone().GetCardCount() + 1));
            }
            // the board: sell
            foreach (var card in me.GetBattlefieldZone().GetCards())
            {
                var e = card == null ? null : card.GetEntity();
                if (e == null) continue;
                var target = e;
                Add(items, card, Str.Join(Str.Word("GAMEPLAY_BACON_DRAGSELL_BUTTON_TUTORIAL"), Minion(e)), () => Act(target, GAME_TAG.DISGUISED, 0));
            }
            // the tavern's buttons
            foreach (var button in new[] { shop.GetFreezeButtonCard(), shop.GetRefreshButtonCard(), shop.GetTavernUpgradeButtonCard() })
            {
                var e = button == null ? null : button.GetEntity();
                if (e == null) continue;
                var target = e;
                int free = e.GetTag(GAME_TAG.BACON_FREE_REFRESH_COUNT);
                Add(items, button, Str.Join(Str.Clean(e.GetName()), free > 0 ? "0" : e.GetCost().ToString()), () => Act(target, GAME_TAG.TAG_NOT_SET, 0));
            }
            var power = me.GetHeroPower();
            if (power != null)
            {
                var target = power;
                Add(items, anchor, Str.Join(Str.Word("GLOBAL_TUTORIAL_HERO_POWER"), Str.Clean(power.GetName()), power.GetCost().ToString(), Str.Clean(power.GetCardTextInHand())), () => Act(target, GAME_TAG.TAG_NOT_SET, 0));
            }
            Add(items, anchor, Str.Word("GAMEPLAY_END_TURN"), () => { Log.Info("battlegrounds: end turn"); InputManager.Get().DoEndTurnButton(); });
            return true;
        }

        // a minion (or tavern spell) as its card shows it
        static string Minion(Entity e)
        {
            string tier = e.HasTag(GAME_TAG.TECH_LEVEL) ? e.GetTag(GAME_TAG.TECH_LEVEL).ToString() : null;
            string stats = e.IsMinion() ? e.GetATK() + "/" + e.GetCurrentHealth() : null;
            string golden = e.GetPremiumType() == TAG_PREMIUM.GOLDEN ? Str.Word("GLOBAL_COLLECTION_GOLDEN") : null;
            string races = null;
            try { races = Str.Clean(e.GetRaceText()); } catch { }
            string text = null;
            try { text = e.GetCardTextInHand(); } catch { }
            return Str.Join(Str.Clean(e.GetName()), golden, stats, tier, races, Str.Clean(text));
        }

        // the game's own option for it, as a click / drag sends it (position: where on the board)
        static void Act(Entity e, GAME_TAG keyword, int position)
        {
            var gs = GS;
            if (gs == null || !gs.IsFriendlySidePlayerTurn() || !gs.IsInMainOptionMode()) { Speech.Say(Str.Unavailable); return; }
            Log.Info("battlegrounds: " + e.GetName() + (keyword != GAME_TAG.TAG_NOT_SET ? " " + keyword : "") + (position > 0 ? " at " + position : ""));
            if (position > 0) gs.SetSelectedOptionPosition(position);
            if (!InputManager.Get().DoNetworkResponse(e, true, keyword)) Speech.Say(Str.Unavailable);
        }

        static IEnumerator After(float seconds, Action action)
        {
            yield return new WaitForSecondsRealtime(seconds);
            try { action(); } catch (Exception e) { Log.Error(e); }
        }

        protected override void Back() { }
    }
}
#endif
