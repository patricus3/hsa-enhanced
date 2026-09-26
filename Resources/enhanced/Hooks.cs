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
                Log.Info("format picker: read from its buttons");
                FallbackWatcher.ShowPopup(widget.gameObject, hsaPopup);
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
