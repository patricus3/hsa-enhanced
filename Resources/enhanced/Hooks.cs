using System;
using Accessibility;
using Hearthstone.DataModels;
using UnityEngine;

namespace HSAEnhanced
{
    // Called from code that `port hook` (tools/port/Enhance.cs) inserts at the end of
    // HSA and game methods. Arguments are typed object so the IL stays simple; nothing
    // here may throw into the game.
    public static class Hooks
    {
        // end of AccessibleHub.SetupMainMenu(); hsaFields: "Method=field+field|..." the Box fields
        // each of HSA's hub methods presses (found in its IL when the mod is built)
        public static void AfterHubMenu(object hub, object menu, string hsaFields)
        {
            try { HubMenu.AfterSetup(hub, menu as AccessibleMenu, hsaFields); }
            catch (Exception e) { Log.Error(e); }
        }

        // end of AccessibleGameModeScene.SetupMainMenu(); the returned menu replaces m_mainMenu
        public static object GameModeMenu(object scene, object model, object hsaMenu)
        {
            try { return GameModesMenu.Build(scene as AccessibleComponent, model as GameModeSceneDataModel, hsaMenu as AccessibleMenu); }
            catch (Exception e) { Log.Error(e); return hsaMenu; }
        }

        // end of AccessibilityMgr.Initialize(GameObject root): the fallback for screens HSA does not know.
        // tooltips: "Type=KEY|..." tooltip headlines of button classes, read from the game's code
        // menus: "hsa" when installed with --use-hsa-menus (our menu system stays off)
        public static void OnAccessibilityInitialized(object root, string tooltips, string menus)
        {
            try
            {
                Engine.Enabled = menus != "hsa";
                Log.Info("menus: " + (Engine.Enabled ? "enhanced" : "Hearthstone Access (--use-hsa-menus)"));
                Ui.SetTooltips(tooltips); FallbackWatcher.Ensure(root as GameObject);
            }
            catch (Exception e) { Log.Error(e); }
        }

        // start of AccessibleMenu.StartReading(bool): add what the game shows, and a way back
        public static void BeforeMenuRead(object menu)
        {
            try { var m = menu as AccessibleMenu; if (m != null) MenuAugment.BeforeRead(m); }
            catch (Exception e) { Log.Error(e); }
        }

        // start of AccessibleHorizontalMenu<T>.StartReading(): a way back
        public static void BeforeHorizontalMenuRead(object menu)
        {
            try { MenuAugment.BeforeHorizontalRead(menu); }
            catch (Exception e) { Log.Error(e); }
        }

        // start of AccessibleAdventureScene.SetupAndReadChooseAdventureMenu(); true: HSA's version is skipped
        public static bool ChooseAdventureMenu(object scene)
        {
            try { return AdventureMenu.Build(scene); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's DeckPickerTrayDisplay.RankedOnDeckPickerTrayDisplayReady() and
        // FriendlyOnDeckPickerTrayDisplayReady(); true: HSA's version is skipped
        public static bool PlayScreenReady(object display)
        {
            try { return PlayScreen.Show(display as MonoBehaviour); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's AccessibleAdventureScene.OnDeckPickerTrayDisplayReady(pages) and
        // OnPracticePickerTrayDisplayShown(buttons): the adventure / practice deck tray and the
        // practice opponent tray, read the same way; true: HSA's version is skipped
        public static bool AdventureDeckTrayReady(object scene)
        {
            try { return PlayScreen.Show(DeckPickerTrayDisplay.Get()); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        public static bool OpponentTrayShown(object scene)
        {
            try { return PlayScreen.Show(PracticePickerTrayDisplay.Get()); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's AccessibleFormatTypePickerPopup.ReadPopup(); true: HSA's version is skipped.
        // The game's format picker is read from its own buttons (every format it offers, with its text)
        public static bool FormatPickerOpened(object hsaPopup)
        {
            try
            {
                if (!Engine.Enabled) return false;
                var widget = Ui.FieldOfType<Hearthstone.UI.Widget>(hsaPopup);
                if (widget == null) return false;
                // during the set rotation intro it announces the new year: its texts are read too
                var tray = DeckPickerTrayDisplay.Get();
                var state = tray == null ? null : Ref.Get(tray, "m_setRotationTutorialState");
                bool rotation = state != null && state.ToString() != "INACTIVE";
                Log.Info("format picker: read from its buttons" + (rotation ? " and texts (set rotation)" : ""));
                FallbackWatcher.ShowPopup(widget.gameObject, hsaPopup, rotation);
                return true;
            }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of GameMgr.FindGameInternal(...); true: this call is dropped
        public static bool SkipDuplicateFindGame(object gameMgr)
        {
            try
            {
                var mgr = gameMgr as GameMgr;
                if (mgr == null || !mgr.IsFindingGame()) return false;
                Log.Info("second find-game request while one is under way: dropped");
                return true;
            }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's AccessibleGameplay.ClickCard(bool); true: not clicked now (the pointer is
        // not on the hand card being read yet; HandClick clicks once it is)
        public static bool BeforeClickCard(object gameplay, bool performingDeckAction)
        {
            try
            {
                // a Mercenaries battle: a mercenary is placed / commanded here
                var read = Ref.Get(gameplay, "m_cardBeingRead") as AccessibleCard;
                if (!performingDeckAction && read != null && MercBattle.OnEnter(read.GetCard())) return true;
                return HandClick.Before(gameplay, performingDeckAction);
            }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's AccessiblePackOpeningCard.GetLines(); non-null: these lines instead of HSA's
        // (a Mercenaries pack card: the mercenary, a portrait or coins, with its abilities)
        public static object PackCardLines(object card)
        {
            try { return PackCards.Lines(card); }
            catch (Exception e) { Log.Error(e); return null; }
        }

        // start of AccessibilityMgr.Output(AccessibleComponent speaker, string, bool); our own
        // fallback menu speaking does not count as the screen speaking
        public static void OnSpeech(object speaker) { if (!(speaker is FallbackUI)) FallbackWatcher.Spoke(); }

        // start of AdventureMissionDisplay.selectWing(AdventureBossCoin) (HSA's); true: not started.
        // The game's own state of the mission's wing decides (owned, playable, unlocked, available)
        public static bool BeforeSelectMission(object display, object coin)
        {
            try
            {
                var c = coin as AdventureBossCoin;
                var why = Missions.WhyNot(c);
                if (why == null) return false;
                AccessibilityMgr.Output(display as AccessibleComponent, why);
                Log.Info("mission not started: " + why);
                return true;
            }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's gameplay handlers (zone keys, arrows, Tab, card lines, status keys); true: ours handled it
        public static bool CombatZoneKeys(object gameplay, bool minionsAndHeroesOnly)
        {
            try { return Combat.ZoneKeys(gameplay, minionsAndHeroesOnly); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        public static bool CombatZoneMove(object gameplay)
        {
            try { return Combat.ZoneMove(gameplay); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        public static bool CombatValidItems(object gameplay)
        {
            try { return Combat.ValidItems(gameplay); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        public static bool CombatCardLines(object gameplay)
        {
            try { return Combat.CardLines(gameplay); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        public static bool CombatStatusKeys(object gameplay)
        {
            try { return Combat.StatusKeys(gameplay); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's AccessibleGameplay.HandleConfirmOrCancel(targetRequired); true: the target went here
        public static bool CombatConfirmTarget(object gameplay, bool targetRequired)
        {
            try { return Combat.ConfirmTarget(gameplay, targetRequired); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's AccessibleGameplay.HandleEndTurnInput(); true: E ended the turn without asking
        public static bool EndTurnInput(object gameplay)
        {
            try { return Settings.EndTurnWithoutAsking(gameplay); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's AccessibleJournal.OnJournalOpened(widget, type); true: our journal instead
        public static bool JournalOpened(object journal)
        {
            try { return Journal.Open(); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's AccessibleJournal.OnJournalClosed(): ours goes too (HSA's cleanup runs)
        public static bool JournalClosed(object journal)
        {
            try { Journal.Close(); } catch (Exception e) { Log.Error(e); }
            return false;
        }

        // start of HSA's journal reactions (reading, quest and reward events); true: ours is open, skipped
        public static bool JournalQuiet(object journal)
        {
            try { return Journal.Active; }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's AccessibleJournal.OnTabChanged(data) (a coroutine); non-null: an empty one instead
        public static object JournalTabChanged(object journal)
        {
            try { return Journal.Active ? EmptyRoutine() : null; }
            catch (Exception e) { Log.Error(e); return null; }
        }

        static System.Collections.IEnumerator EmptyRoutine() { yield break; }

        // start of HSA's AccessibleRewardData.GetLines(); non-null: these lines instead of HSA's
        public static object RewardLines(object reward)
        {
            try { return RewardText.Lines(reward); }
            catch (Exception e) { Log.Error(e); return null; }
        }

        // start of HSA's AccessibleInGameState.HasAnyHeroGainedAtk(before, after); true: false is returned
        public static bool SkipHeroAttackCheck(object before, object after)
        {
            try { return MercBattle.InBattle; }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's AccessibleGameplay.MoveMouseToCard(card); true: moved here
        public static bool BeforeMoveMouseToCard(object gameplay, object card)
        {
            try
            {
                var c = card as Card;
                if (c == null || !MercBattle.InBattle || !(c.GetZone() is ZoneHand)) return false;
                if (!c.IsMousedOver()) AccessibleInputMgr.MoveMouseTo(c);
                return true;
            }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of HSA's MessagePopupDisplay.ReadMessage(MessageUIData); true: read here
        public static bool ReadInGameMessage(object popup, object data)
        {
            try { return Mail.Read(popup, data); }
            catch (Exception e) { Log.Error(e); return false; }
        }

        // start of AccessibilityMgr.NotifyScreenFocused() (the screen then speaks, or not)
        public static void OnScreenFocused() { FallbackWatcher.ScreenFocused(); }

        // Black Market exchange with the server, for the log
        public static void OnBlackMarketRequest() { Log.Info("Black Market: asking the server"); }

        public static void OnBlackMarketTimeout() { Log.Info("Black Market: the server did not answer in time"); }

        // end of Network.GetBlackMarketPlayerStateResponse(): the reply the game got
        public static void OnBlackMarketResponse(object response)
        {
            try
            {
                if (response == null) { Log.Info("Black Market: reply read, none there"); return; }
                var parts = new System.Collections.Generic.List<string>();
                foreach (var p in response.GetType().GetProperties())
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    var t = p.PropertyType;
                    if (!(t.IsPrimitive || t.IsEnum || t == typeof(string))) continue;
                    object v; try { v = p.GetValue(response, null); } catch { continue; }
                    parts.Add(p.Name + "=" + v);
                }
                Log.Info("Black Market: server reply " + string.Join(", ", parts.ToArray()));
            }
            catch (Exception e) { Log.Error(e); }
        }

        // end of LuckyDrawDisplay.Start()
        public static void OnLuckyDrawDisplayStart(object display)
        {
            try
            {
                var d = display as LuckyDrawDisplay;
                if (d != null && d.GetComponent<LuckyDrawWatcher>() == null) d.gameObject.AddComponent<LuckyDrawWatcher>().Display = d;
            }
            catch (Exception e) { Log.Error(e); }
        }

        // end of BlackMarketDisplay.Start()
        public static void OnBlackMarketDisplayStart(object display)
        {
            try
            {
                var d = display as BlackMarketDisplay;
                if (d != null && d.GetComponent<BlackMarketWatcher>() == null) d.gameObject.AddComponent<BlackMarketWatcher>().Display = d;
            }
            catch (Exception e) { Log.Error(e); }
        }
    }
}
