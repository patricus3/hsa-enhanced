using System;
using System.Collections;
using System.Collections.Generic;
using Accessibility;
using Hearthstone;
using Hearthstone.DataModels;
using Hearthstone.UI;
using UnityEngine;

namespace HSAEnhanced
{
    // Mercenaries outside battles (HSA has no Mercenaries support): one accessible screen that
    // follows the game's Mercenaries scenes and reads each from the game's own data:
    //   village: its buildings (entered the way a click enters them), packs to open, Back
    //   Travel Point: every zone with its difficulties (locked ones say why), Choose
    //   bounty board: every bounty (level, boss, completed / locked / new), the chosen one's boss and
    //     rewards, Play
    //   party choice: every party with its mercenaries, Play, edit parties
    //   bounty map: the encounters you can go to (the game's own description of each), Play, the
    //     party, turns, Retire, and every row of the map; treasure and visitor picks
    // Popups of the village's other buildings (tasks, workshop, training hall) are read by the fallback.
    static class Mercenaries
    {
        static MercScreen s_screen;
        internal static int Active;     // the fallback menu leaves these scenes to us
        static float s_next;

        // from FallbackWatcher, twice a second
        internal static void Tick()
        {
            if (Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + 0.5f;
            try
            {
                var scenes = SceneMgr.Get();
                bool here = AccessibilityMgr.IsAccessibilityEnabled() && GameState.Get() == null && scenes != null && IsMercMode(scenes.GetMode());
                if (!here) { Leave(); return; }
                // HSA's keys off with no text box open (a name box that closed some other way):
                // on again, never stuck
                var typing = UniversalInputManager.Get();
                if (AccessibilityMgr.IsTextInputAllowed() && typing != null && !typing.IsTextInputActive())
                {
                    Log.Info("mercenaries: text box gone, HSA's keys on again");
                    AccessibilityMgr.DisallowTextInput();
                }
                if (scenes.IsTransitioning()) return;
                string key, title; List<GameButton> items;
                if (!Build(scenes.GetMode(), out key, out title, out items)) return;   // still loading: the menu stays as it was
                if (s_screen == null)
                {
                    s_screen = new MercScreen();
                    Active++;
                }
                s_screen.Show(key, title, items);
            }
            catch (Exception e) { Log.Error(e); }
        }

        static bool IsMercMode(SceneMgr.Mode mode)
        {
            return mode == SceneMgr.Mode.LETTUCE_VILLAGE || mode == SceneMgr.Mode.LETTUCE_BOUNTY_BOARD
                || mode == SceneMgr.Mode.LETTUCE_BOUNTY_TEAM_SELECT || mode == SceneMgr.Mode.LETTUCE_MAP
                || mode == SceneMgr.Mode.LETTUCE_COLLECTION;
        }

        static void Leave()
        {
            if (s_screen == null) return;
            if (AccessibilityMgr.IsCurrentlyFocused(s_screen)) AccessibilityMgr.TransitioningScreens();
            s_screen = null;
            Active--;
        }

        // the Travel Point and Workshop popups are ours, not the fallback's
        internal static bool Handles(GameObject go)
        {
            return Active > 0 && (go.GetComponentInChildren<LettuceVillageZonePortal>(true) != null || go.GetComponentInParent<LettuceVillageZonePortal>() != null
                                  || go.GetComponentInChildren<LettuceVillageWorkshop>(true) != null || go.GetComponentInParent<LettuceVillageWorkshop>() != null);
        }

        static bool Blocked(AbsSceneDisplay display)
        {
            return display.m_clickBlocker != null && display.m_clickBlocker.activeInHierarchy;
        }

        static bool Build(SceneMgr.Mode mode, out string key, out string title, out List<GameButton> items)
        {
            key = null; title = ""; items = new List<GameButton>();
            switch (mode)
            {
                case SceneMgr.Mode.LETTUCE_VILLAGE: return Village(ref key, ref title, items);
                case SceneMgr.Mode.LETTUCE_BOUNTY_BOARD: return BountyBoard(ref key, ref title, items);
                case SceneMgr.Mode.LETTUCE_BOUNTY_TEAM_SELECT: return TeamSelect(ref key, ref title, items);
                case SceneMgr.Mode.LETTUCE_MAP: return Map(ref key, ref title, items);
                case SceneMgr.Mode.LETTUCE_COLLECTION: return MercCollection.Build(ref key, ref title, items);
            }
            return false;
        }

        internal static void Say(string text) { if (s_screen != null && text.Length > 0) AccessibilityMgr.Output(s_screen, text); }

        internal static void Item(List<GameButton> items, Component target, string label, Action click)
        {
            if (string.IsNullOrEmpty(label)) return;
            items.Add(new GameButton { Target = target, Label = label, Click = click });
        }

        internal static void Info(List<GameButton> items, Component target, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Item(items, target, text, () => Say(text));
        }

        // the game's own button: pressed only while the game has it enabled
        static void ButtonItem(List<GameButton> items, PegUIElement button, string fallbackLabel)
        {
            if (button == null || !button || !button.gameObject.activeInHierarchy || !button.IsEnabled()) return;
            var label = Ui.LabelOf(button);
            if (label.Length == 0) label = fallbackLabel;
            var b = button;
            Item(items, button, label, () => { Log.Info("mercenaries: " + label); b.TriggerRelease(); });
        }

        static void BackItem(List<GameButton> items, Component target)
        {
            Item(items, target, Str.Back, Back);
        }

        static string CardName(CardDataModel card)
        {
            if (card == null) return "";
            if (!string.IsNullOrEmpty(card.Name)) return Str.Clean(card.Name);
            var def = string.IsNullOrEmpty(card.CardId) ? null : DefLoader.Get().GetEntityDef(card.CardId);
            return def == null ? "" : Str.Clean(def.GetName());
        }

        static string CardText(CardDataModel card)
        {
            if (card == null) return "";
            if (!string.IsNullOrEmpty(card.CardText)) return Str.Clean(card.CardText);
            var def = string.IsNullOrEmpty(card.CardId) ? null : DefLoader.Get().GetEntityDef(card.CardId);
            string text = null;
            try { text = def == null ? null : def.GetCardTextInHand(); } catch { }
            return Str.Clean(text);
        }

        internal static string Merc(LettuceMercenaryDataModel m)
        {
            if (m == null) return "";
            string role = null;
            try { role = GameStrings.GetRoleName(m.MercenaryRole); } catch { }
            return Str.Join(Str.Clean(m.MercenaryName), Str.Clean(role), m.MercenaryLevel > 0 ? Str.Game("GLUE_LETTUCE_BOUNTY_POSTER_TEXT", m.MercenaryLevel) : null);
        }

        internal static string Checked { get { return LocalizationUtils.Get(LocalizationKey.OPTIONS_MENU_CHECKBOX_CHECKED); } }

        // ---- village ----------------------------------------------------------------------

        static bool Village(ref string key, ref string title, List<GameButton> items)
        {
            var display = UnityEngine.Object.FindObjectOfType<LettuceVillageDisplay>();
            if (display == null || Blocked(display)) return false;
            var popups = LettuceVillagePopupManager.Get();
            var open = popups == null ? LettuceVillagePopupManager.PopupType.INVALID : popups.CurrentlyOpenPopup;
            if (open == LettuceVillagePopupManager.PopupType.PVE) return TravelPoint(ref key, ref title, items);
            if (open == LettuceVillagePopupManager.PopupType.WORKSHOP) return Workshop(ref key, ref title, items);
            if (open != LettuceVillagePopupManager.PopupType.INVALID) return false;      // the fallback reads that popup
            var village = display.GetVillage();
            if (village == null || !village.VillageIsReady) return false;
            key = "village";
            title = Str.Word("GLUE_MERCENARIES");
            if (village.Buildings != null)
                foreach (var b in village.Buildings)
                {
                    if (b == null || b.DataModel == null || b.IsGated || !b.DataModel.Enabled) continue;
                    var record = LettuceVillageDataUtil.GetBuildingRecordByType(b.BuildingType);
                    var name = record == null || record.Name == null ? "" : Str.Clean(record.Name.GetString());
                    if (name.Length == 0) continue;
                    var type = b.BuildingType;
                    var target = b.BuildingController as Component;
                    Item(items, target, name, () => { Log.Info("mercenaries: building " + type); Ref.Call(village, "ExecuteBuildingClicked", type); });
                }
            var model = village.GetVillageDataModel();
            if (model != null && model.NumPacksAvailable > 0)
            {
                var widget = village.GetComponent<Widget>();
                Item(items, village, Str.Join(Str.Word("GLUE_OPEN_PACKS"), model.NumPacksAvailable.ToString()),
                     () => { if (widget != null) widget.TriggerEvent("PACK_BUTTON_CLICKED"); });
            }
            BackItem(items, village);
            return true;
        }

        // Back in a village popup closes the popup (the game's back navigation leaves the village)
        internal static bool CloseVillagePopup()
        {
            var scenes = SceneMgr.Get();
            if (scenes == null || scenes.GetMode() != SceneMgr.Mode.LETTUCE_VILLAGE) return false;
            var popups = LettuceVillagePopupManager.Get();
            if (popups == null || popups.CurrentlyOpenPopup == LettuceVillagePopupManager.PopupType.INVALID) return false;
            Log.Info("mercenaries: close " + popups.CurrentlyOpenPopup);
            popups.Hide(popups.CurrentlyOpenPopup);
            return true;
        }

        internal static void GoBack() { Back(); }

        static void Back()
        {
            if (CloseVillagePopup()) return;
            if (MercCollection.Back()) return;
            Log.Info("mercenaries: back");
            Navigation.GoBack();
        }

        // Workshop: every building on all its pages (the game builds each entry the same way):
        // what the next level gives, the task that unlocks it and its progress, or its price; Enter
        // builds / upgrades it as the game's button does when that is possible
        static bool Workshop(ref string key, ref string title, List<GameButton> items)
        {
            var workshop = UnityEngine.Object.FindObjectOfType<LettuceVillageWorkshop>();
            var states = workshop == null ? null : Ref.Get(workshop, "m_buildingStateList") as IList;
            if (states == null) return false;
            key = "workshop";
            title = Str.Word("GLUE_LETTUCE_VILLAGE_WORKSHOP_TITLE");
            long gold = 0;
            CurrencyManager currencies = null;
            if (Blizzard.T5.Services.ServiceManager.TryGet<CurrencyManager>(out currencies) && currencies != null) gold = currencies.GetBalance(CurrencyType.GOLD);
            Info(items, workshop, Str.Join(Str.Word("GLUE_SHOP_GOLD"), gold.ToString()));
            foreach (var state in states)
            {
                var item = Ref.Invoke(workshop, "MakeWorkshopItemDataModel", state, gold) as MercenaryVillageWorkshopItemDataModel;
                if (item == null) continue;
                string status;
                bool canDo = false;
                if (item.IsFullyUpgraded) status = Str.Word("GLUE_LETTUCE_VILLAGE_WORKSHOP_FULLY_UPGRADED");
                else if (!item.IsAchievementCompleted) status = Str.Join(Str.Locked, Str.Clean(item.AchievementDescription), item.Progress + "/" + item.Quota);
                else
                {
                    var action = Str.Word(item.IsNewBuilding ? "GLUE_LETTUCE_VILLAGE_WORKSHOP_BUILD" : "GLUE_LETTUCE_VILLAGE_WORKSHOP_UPGRADE");
                    var price = item.Price == null ? null : Str.Join(item.Price.DisplayText, Str.Word("GLUE_SHOP_GOLD"));
                    canDo = item.CanAffordUpgrade;
                    status = Str.Join(action, price, canDo ? null : Str.Unavailable);
                }
                var label = Str.Join(Str.Clean(item.Title), status, Str.Clean(item.TierDescription), Str.Clean(item.Description));
                var it = item;
                bool upgrade = canDo;
                Item(items, workshop, label, () =>
                {
                    if (!upgrade) { Say(label); return; }
                    Log.Info("mercenaries: workshop upgrade " + it.Title + " to tier " + it.NextTierId);
                    Ref.Invoke(workshop, "UpgradeClicked", it);
                });
            }
            Item(items, workshop, Str.Back, () => CloseVillagePopup());
            return true;
        }

        static bool TravelPoint(ref string key, ref string title, List<GameButton> items)
        {
            var tray = UnityEngine.Object.FindObjectOfType<LettuceLobbyChooserTray>();
            var portal = UnityEngine.Object.FindObjectOfType<LettuceVillageZonePortal>();
            if (tray == null || portal == null) return false;
            var buttons = Ref.Get(tray, "m_ChooserButtons") as IList;
            if (buttons == null || buttons.Count == 0) return false;
            key = "travel";
            title = Str.Word("GLUE_LETTUCE_VILLAGE_ZONES_TITLE");
            var selected = tray.GetSelectedModeInfo();
            foreach (ChooserButton zone in buttons)
            {
                if (zone == null) continue;
                var zoneName = zone.m_ButtonTextObject == null ? "" : Ui.ShownText(zone.m_ButtonTextObject.Text);
                foreach (var sub in zone.GetSubButtons())
                {
                    var lettuce = sub as LettuceLobbyChooserSubButton;
                    if (lettuce == null) continue;
                    var difficulty = sub.m_ButtonTextObject == null ? "" : Ui.ShownText(sub.m_ButtonTextObject.Text);
                    bool locked = lettuce.GetLockedReason() != MercenariesDataUtil.MercenariesBountyLockedReason.UNLOCKED;
                    bool isSelected = selected != null && selected.BountySetRecord == lettuce.GetBountySetRecord() && selected.Difficulty == lettuce.GetDifficulty();
                    var label = Str.Join(zoneName, difficulty, locked ? Str.Locked : null, isSelected ? Checked : null);
                    var button = sub;
                    Item(items, sub, label, () =>
                    {
                        Log.Info("mercenaries: travel point " + label);
                        button.TriggerRelease();
                        var info = Ref.Get(portal, "m_dataModel") as MercenaryVillageZonePortalDataModel;
                        if (info != null) Say(Str.Join(Str.Clean(info.SelectedZoneName), Str.Clean(info.SelectedZoneDescription), info.IsSelectedModeLocked ? Str.Clean(info.SelectedModeLockedReason) : null));
                    });
                }
            }
            var dm = Ref.Get(portal, "m_dataModel") as MercenaryVillageZonePortalDataModel;
            if (dm != null) Info(items, portal, Str.Join(Str.Clean(dm.SelectedZoneName), Str.Clean(dm.SelectedZoneDescription), dm.IsSelectedModeLocked ? Str.Clean(dm.SelectedModeLockedReason) : null));
            ButtonItem(items, Ref.Get<PlayButton>(portal, "m_chooseButton") ?? tray.m_ChooseButton, "");
            Item(items, portal, Str.Back, () => LettuceVillagePopupManager.Get().Hide(LettuceVillagePopupManager.PopupType.PVE));
            return true;
        }

        // ---- bounty board -----------------------------------------------------------------

        static bool BountyBoard(ref string key, ref string title, List<GameButton> items)
        {
            var display = UnityEngine.Object.FindObjectOfType<LettuceBountyBoardDisplay>();
            if (display == null || !display.m_initialized || Blocked(display)) return false;
            var board = Ref.Get(display, "m_bountyBoardDataModel") as LettuceBountyBoardDataModel;
            if (board == null || board.Bounties == null) return false;
            key = "board";
            title = Str.Clean(board.HeaderText);
            var widget = display.m_widgetTemplate;
            foreach (var b in board.Bounties)
            {
                if (b == null) continue;
                var record = GameDbf.LettuceBounty.GetRecord(b.BountyId);
                var name = record == null ? "" : Str.Clean(LettuceVillageDataUtil.GenerateBountyName(record, false, false));
                var state = b.IsDisabled ? Str.Unavailable : b.IsComingSoon || b.IsEventLocked ? Str.Clean(b.ComingSoonText) : b.IsLocked ? Str.Locked : b.Complete ? Str.Completed : null;
                var label = Str.Join(Str.Clean(b.PosterText), name, state, b.BountyId == board.CurrentSelectedBountyRecordId ? Checked : null);
                var bounty = b;
                Item(items, widget, label, () =>
                {
                    Log.Info("mercenaries: bounty " + label);
                    widget.TriggerEvent("BOUNTY_RELEASED", new TriggerEventParameters(null, bounty));
                    Say(Selected(board));
                });
            }
            Info(items, widget, Selected(board));
            ButtonItem(items, Ref.Get<PlayButton>(display, "m_playButton"), "");
            BackItem(items, widget);
            return true;
        }

        // the chosen bounty: its boss, level, why it is locked, its rewards
        static string Selected(LettuceBountyBoardDataModel board)
        {
            if (board.CurrentSelectedBountyRecordId == 0) return "";
            string rewards = board.SelectedBountyRewardList == null ? null : Str.Clean(board.SelectedBountyRewardList.Description);
            return Str.Join(Str.Clean(board.BossName), Str.Clean(board.BossDescription), board.IsSelectedBountyLocked ? Str.Clean(board.BountyLockedText) : null, rewards);
        }

        // ---- party choice -----------------------------------------------------------------

        static bool TeamSelect(ref string key, ref string title, List<GameButton> items)
        {
            var display = UnityEngine.Object.FindObjectOfType<LettuceBountyTeamSelectDisplay>();
            if (display == null || Blocked(display)) return false;
            var listController = Ref.Get<VisualController>(display, "m_teamListVisualController");
            var listWidget = listController == null ? null : listController.GetComponent<Widget>();
            LettuceTeamListDataModel teams = null;
            foreach (var w in display.GetComponentsInChildren<Widget>(true))
            {
                teams = w.GetDataModel<LettuceTeamListDataModel>();
                if (teams != null) break;
            }
            if (listWidget == null || teams == null || teams.TeamList == null) return false;
            key = "teams";
            title = Str.Word("GLUE_LETTUCE_BOUNTY_BOARD_TEAM_SELECT_HEADER");
            var chosen = Ref.Get<LettuceTeam>(display, "m_selectedTeam");
            foreach (var t in teams.TeamList)
            {
                if (t == null) continue;
                var mercs = new List<string>();
                if (t.MercenaryList != null) foreach (var m in t.MercenaryList) mercs.Add(Merc(m));
                var label = Str.Join(Str.Clean(t.TeamName), !t.Valid || t.IsDisabled ? Str.Unavailable : null, chosen != null && chosen.ID == t.TeamId ? Checked : null, string.Join("; ", mercs.ToArray()));
                var team = t;
                Item(items, listWidget, label, () => { Log.Info("mercenaries: party " + t.TeamName); listWidget.TriggerEvent("TEAM_SELECTED", new TriggerEventParameters(null, team)); });
            }
            ButtonItem(items, Ref.Get<PlayButton>(display, "m_playButton"), "");
            ButtonItem(items, Ref.Get<UIBButton>(display, "m_collectionButton"), "");
            BackItem(items, listWidget);
            return true;
        }

        // ---- bounty map -------------------------------------------------------------------

        static bool Map(ref string key, ref string title, List<GameButton> items)
        {
            var display = UnityEngine.Object.FindObjectOfType<LettuceMapDisplay>();
            if (display == null) return false;
            var dm = Ref.Invoke(display, "GetDisplayDataModel") as LettuceMapDisplayDataModel;
            // picks come while the map is still blocked
            if (dm != null && Ref.Get<bool>(display, "m_waitingForTreasureSelection") && dm.TreasureSelectionData != null
                && dm.SelectedTreasureChoices >= 0 && dm.SelectedTreasureChoices < dm.TreasureSelectionData.Count)
                return Treasure(display, dm, ref key, ref title, items);
            if (dm != null && dm.VisitorSelectionData != null && dm.VisitorSelectionData.VisitorOptions != null && dm.VisitorSelectionData.VisitorOptions.Count > 0)
                return Visitor(display, dm, ref key, ref title, items);
            if (Blocked(display) || !Ref.Get<bool>(display, "m_lettuceMapDataInitialized")) return false;
            var map = Ref.Get<LettuceMap>(display, "m_lettuceMap");
            if (map == null) return false;
            key = "map";
            title = dm == null ? "" : Str.Clean(dm.ZoneIdentifier);
            var selected = Ref.Get<LettuceMapCoinDataModel>(display, "m_selectedMapCoin");
            foreach (var coin in map.GetUnlockedCoinDataModels())
            {
                if (coin == null) continue;
                var c = coin;
                var label = Str.Join(Node(coin), selected != null && selected.Id == coin.Id ? Checked : null);
                Item(items, map, label, () => { Log.Info("mercenaries: map node " + c.Id + " " + label); Ref.Invoke(display, "SelectCoinInternal", c); });
            }
            if (dm != null && !string.IsNullOrEmpty(dm.BossName)) Info(items, map, Str.Clean(dm.BossName));
            ButtonItem(items, Ref.Get<PlayButton>(display, "m_playButton"), "");
            // the party: who is in it, who fell in this run, their treasures
            foreach (var w in display.GetComponentsInChildren<Widget>(true))
            {
                var team = w.GetDataModel<LettuceTeamDataModel>();
                if (team == null || team.MercenaryList == null) continue;
                var mercs = new List<string>();
                foreach (var m in team.MercenaryList) if (m != null) mercs.Add(Str.Join(Merc(m), m.DeadInMapRun ? Str.Unavailable : null));
                Info(items, map, Str.Join(Str.Word("GLUE_LETTUCE_MAP_TEAM_TRAY_HEADER"), string.Join("; ", mercs.ToArray())));
                break;
            }
            if (dm != null) Info(items, map, Str.Join(Str.Word("GLUE_LETTUCE_MAP_STATS_CURRENT_TURNS_LABEL"), dm.CurrentTurns.ToString(), Str.Word("GLUE_LETTUCE_MAP_STATS_BEST_TURNS_LABEL"), dm.FewestTurns.ToString()));
            if (dm == null || !dm.RunEnded)
                Item(items, map, Str.Word("GLUE_LETTUCE_MAP_RETIRE_DIALOG_CONFIRM"), () => { Log.Info("mercenaries: retire"); Ref.Invoke(display, "OnRetireButtonRelease", new object[] { null }); });
            // the whole map, the final boss row first (as it is shown, top to bottom)
            var mapWidget = map.GetComponent<Widget>();
            var layout = mapWidget == null ? null : mapWidget.GetDataModel<LettuceMapDataModel>();
            var rows = layout == null ? null : layout.Rows;
            if (rows != null)
                foreach (var row in rows)
                {
                    if (row == null || row.Coins == null) continue;
                    var nodes = new List<string>();
                    foreach (var coin in row.Coins) if (coin != null) nodes.Add(Str.Join(Str.Clean(coin.HoverTooltipHeader), State((int)coin.CoinState)));
                    if (nodes.Count > 0) Info(items, map, string.Join("; ", nodes.ToArray()));
                }
            BackItem(items, map);
            return true;
        }

        static string Node(LettuceMapCoinDataModel coin)
        {
            return Str.Join(Str.Clean(coin.HoverTooltipHeader), Str.Clean(coin.HoverTooltipBody));
        }

        static string State(int state)
        {
            switch (state)
            {
                case 1: return Str.Locked;
                case 3: return Str.Completed;
                case 4: case 5: return Str.Unavailable;
                default: return null;
            }
        }

        static bool Treasure(LettuceMapDisplay display, LettuceMapDisplayDataModel dm, ref string key, ref string title, List<GameButton> items)
        {
            int choice = dm.SelectedTreasureChoices;
            var pick = dm.TreasureSelectionData[choice];
            key = "treasure:" + choice;
            title = Str.Join(Str.Word(pick.ChoiceMercenaryHasTreasure ? "GLUE_LETTUCE_MAP_TREASURE_KEEP" : "GLUE_LETTUCE_MAP_TREASURE_PICK"), Merc(pick.ChoiceMercenary));
            if (pick.ChoiceMercenaryHasTreasure && pick.ChoiceMercenaryTreasure != null)
                Info(items, display, Str.Join(Str.Word("GLUE_LETTUCE_MAP_TREASURE_BANNER"), CardName(pick.ChoiceMercenaryTreasure), CardText(pick.ChoiceMercenaryTreasure)));
            if (pick.TreasureOptions != null)
                for (int i = 0; i < pick.TreasureOptions.Count; i++)
                {
                    var option = pick.TreasureOptions[i];
                    int index = i;
                    var label = Str.Join(CardName(option), CardText(option));
                    Item(items, display, label, () =>
                    {
                        Log.Info("mercenaries: treasure " + index + " " + label + " for " + (pick.ChoiceMercenary == null ? "?" : pick.ChoiceMercenary.MercenaryName));
                        var chosen = Ref.Get(display, "m_selectedTreasureIndex") as List<int>;
                        if (chosen == null || choice >= chosen.Count) return;
                        chosen[choice] = index;
                        Ref.Invoke(display, "OnTreasureChosen");
                    });
                }
            return true;
        }

        static bool Visitor(LettuceMapDisplay display, LettuceMapDisplayDataModel dm, ref string key, ref string title, List<GameButton> items)
        {
            var visitors = dm.VisitorSelectionData;
            key = "visitor";
            title = Str.Word("GLUE_LETTUCE_MAP_VISITOR_PICK");
            for (int i = 0; i < visitors.VisitorOptions.Count; i++)
            {
                var merc = visitors.VisitorOptions[i];
                var task = visitors.TaskOptions != null && i < visitors.TaskOptions.Count ? visitors.TaskOptions[i] : null;
                int index = i;
                var label = Str.Join(Merc(merc), task == null ? null : Str.Clean(task.Title), task == null ? null : Str.Clean(task.Description),
                                     task == null || task.RewardList == null ? null : Str.Clean(task.RewardList.Description));
                Item(items, display, label, () =>
                {
                    Log.Info("mercenaries: visitor " + index + " " + label);
                    Ref.Set(display, "m_selectedVisitorIndex", index);
                    Ref.Invoke(display, "OnVisitorChosen");
                });
            }
            return true;
        }
    }

    // The accessible screen of the Mercenaries scenes: a menu rebuilt from the game's data when
    // it changes (keeping the option under the cursor), read out when the step changes
    class MercScreen : AccessibleScreen
    {
        AccessibleMenu m_menu;
        string m_key, m_signature;

        internal void Show(string key, string title, List<GameButton> items)
        {
            var sig = title + "\n" + string.Join("\n", items.ConvertAll(b => b.Label).ToArray());
            bool newStep = key != m_key;
            if (!newStep && sig == m_signature) return;
            var keep = m_menu == null || newStep ? 0 : MenuEdit.GetIndex(m_menu);
            var keepLabel = m_menu == null || newStep ? null : CurrentLabel();
            m_menu = new AccessibleMenu(this, title, Mercenaries.GoBack);
            foreach (var b in items) { var click = b.Click; m_menu.AddOption(b.Label, () => click()); }
            int to = -1;
            if (keepLabel != null) for (int i = 0; i < items.Count; i++) if (items[i].Label == keepLabel) { to = i; break; }
            MenuEdit.SetIndex(m_menu, to >= 0 ? to : Math.Min(keep, Math.Max(0, items.Count - 1)));
            m_signature = sig;
            if (newStep) Log.Info("mercenaries: " + key + " '" + title + "': " + GameButton.Describe(items));
            m_key = key;
            if (!AccessibilityMgr.IsCurrentlyFocused(this)) { AccessibilityMgr.SetScreen(this); return; }   // it reads itself on focus
            if (newStep) m_menu.StartReading();
            // the same step with new contents (a mercenary added, an upgrade): the menu takes the keys
            // at once without being read again (an HSA menu ignores them until it has been read)
            else Ref.Set(m_menu, "m_isReading", true);
        }

        string CurrentLabel()
        {
            var list = MenuEdit.List(m_menu);
            var i = MenuEdit.GetIndex(m_menu);
            return list != null && i >= 0 && i < list.Count ? MenuEdit.TextOf(list[i]) : null;
        }

        public void HandleInput()
        {
            // the game's text box is open (a party's name): the keys are typing
            var input = UniversalInputManager.Get();
            if (input != null && input.IsTextInputActive()) return;
            if (m_menu != null) m_menu.HandleAccessibleInput();
        }

        public string GetHelp() { return m_menu == null ? "" : m_menu.GetHelp(); }

        public void OnGainedFocus() { if (m_menu != null) m_menu.StartReading(); }
    }
}
