using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using Hearthstone;
using UnityEngine;

namespace HSAEnhanced
{
    // The Mercenaries collection (the village's Tavern), read from the game's collection data and
    // changed through the game's own requests, the same ones its buttons send:
    //   Tavern: the parties (Enter edits one), New Party; the mercenaries you have (level, coins,
    //     an upgrade waiting), each opening its page; the ones you can craft (Enter crafts)
    //   a mercenary: level and coins; every ability and equipment with its tier, text and what
    //     the next upgrade costs (Enter upgrades when the coins are there; equipment: Enter equips)
    //   a party being edited: its mercenaries (Enter removes one), the others (Enter adds one), Done
    //     (saves, as the game's Done does), Delete
    static class MercCollection
    {
        static int s_merc;      // the mercenary whose page is open, 0 for none

        static void Say(string text) { Mercenaries.Say(text); }

        // Back from a mercenary's page: the Tavern (true); else the scene's own back
        internal static bool Back()
        {
            var scenes = SceneMgr.Get();
            if (scenes == null || scenes.GetMode() != SceneMgr.Mode.LETTUCE_COLLECTION) return false;
            if (s_merc != 0) { s_merc = 0; return true; }
            var cm = CollectionManager.Get();
            var tray = CollectionDeckTray.Get();
            if (cm != null && cm.IsInEditTeamMode() && tray != null) { Done(tray); return true; }
            var display = cm == null ? null : cm.GetCollectibleDisplay() as LettuceCollectionDisplay;
            if (display == null) return false;
            Leave(display);
            return true;
        }

        internal static bool Build(ref string key, ref string title, List<GameButton> items)
        {
            var cm = CollectionManager.Get();
            var display = cm == null ? null : cm.GetCollectibleDisplay() as LettuceCollectionDisplay;
            var tray = CollectionDeckTray.Get();
            if (display == null || tray == null) return false;
            ApplyPending(cm);
            // a party being edited: read as it changes (the tray keeps animating while mercenaries
            // are added and removed, and the menu has to follow at once)
            var editing = cm.IsInEditTeamMode() ? cm.GetEditingTeam() : null;
            if (editing != null && s_merc == 0) return Team(cm, tray, editing, ref key, ref title, items);
            if (!display.IsReady()) return false;
            if (Ref.Invoke(tray, "IsUpdatingTrayMode") is bool busy && busy) return false;
            if (s_merc != 0)
            {
                var merc = cm.GetMercenary(s_merc, false, false);
                if (merc != null) return Mercenary(merc, ref key, ref title, items);
                s_merc = 0;
            }
            var team = cm.IsInEditTeamMode() ? cm.GetEditingTeam() : null;
            if (team != null) return Team(cm, tray, team, ref key, ref title, items);
            return Tavern(cm, display, tray, ref key, ref title, items);
        }

        static string Role(TAG_ROLE role)
        {
            try { return Str.Clean(GameStrings.GetRoleName(role)); } catch { return ""; }
        }

        static string Level(LettuceMercenary m) { return Str.Word("GLUE_LETTUCE_MERCENARY_LEVEL_LABEL") + " " + m.m_level; }

        static string Coins(LettuceMercenary m)
        {
            return Str.Game("GLUE_LETTUCE_REWARD_MERCENARY_TASK_COINS_REWARD", m.m_currencyAmount, Str.Clean(m.m_mercShortName ?? m.m_mercName)) ?? m.m_currencyAmount.ToString();
        }

        static string Name(LettuceMercenary m) { return Str.Clean(m.m_mercName); }

        // ---- the Tavern -------------------------------------------------------------------

        static bool Tavern(CollectionManager cm, LettuceCollectionDisplay display, CollectionDeckTray tray, ref string key, ref string title, List<GameButton> items)
        {
            key = "tavern";
            var record = LettuceVillageDataUtil.GetBuildingRecordByType(Assets.MercenaryBuilding.Mercenarybuildingtype.COLLECTION);
            title = record == null || record.Name == null ? "" : Str.Clean(record.Name.GetString());
            // parties
            foreach (var team in cm.GetTeams())
            {
                if (team == null) continue;
                var t = team;
                var mercs = new List<string>();
                foreach (var m in team.GetMercs()) if (m != null) mercs.Add(Name(m));
                var label = Str.Join(Str.Clean(team.Name), mercs.Count + "/" + cm.GetTeamSize(), !team.IsValid() ? Str.Unavailable : null, string.Join(", ", mercs.ToArray()));
                Mercenaries.Item(items, tray, label, () => { Log.Info("mercenaries: edit party " + t.Name); display.RequestContentsToShowTeam(t.ID); });
            }
            var teams = tray.GetTeamsContent();
            if (teams != null)
                Mercenaries.Item(items, tray, Str.Word("GLUE_COLLECTION_NEW_TEAM"), () => { Log.Info("mercenaries: new party"); teams.CreateNewTeam(); });
            // the mercenaries you have
            foreach (var merc in cm.FindMercenaries(null, true).m_mercenaries)
            {
                if (merc == null) continue;
                var m = merc;
                var label = Str.Join(Name(merc), Role(merc.m_role), Level(merc), Coins(merc),
                                     merc.CanAnyCardBeUpgraded() ? Str.Word("GLUE_LETTUCE_ABILITY_UPGRADE_TITLE") : null);
                Mercenaries.Item(items, tray, label, () => { Log.Info("mercenaries: mercenary " + m.m_mercName); s_merc = m.ID; });
            }
            // the ones you can craft now
            foreach (var merc in cm.FindMercenaries(null, false, null, true).m_mercenaries)
            {
                if (merc == null || merc.m_owned) continue;
                var m = merc;
                var cost = Str.Game("GLUE_LETTUCE_REWARD_MERCENARY_TASK_COINS_REWARD", merc.GetCraftingCost(), Str.Clean(merc.m_mercShortName ?? merc.m_mercName));
                var ready = merc.IsReadyForCrafting();
                var label = Str.Join(Str.Word("GLUE_LETTUCE_MERCENARY_CRAFT_TITLE"), Name(merc), Role(merc.m_role), cost, Coins(merc), ready ? null : Str.Word("GLUE_LETTUCE_ABILITY_UPGRADE_NOT_ENOUGH_COINS_HEADER"));
                Mercenaries.Item(items, tray, label, () =>
                {
                    if (!m.IsReadyForCrafting()) { Say(Str.Word("GLUE_LETTUCE_MERCENARY_CRAFTING_NOT_ENOUGH_COIN_BODY")); return; }
                    Log.Info("mercenaries: craft " + m.m_mercName);
                    Network.Get().CraftMercenary(m.ID);
                });
            }
            Mercenaries.Item(items, tray, Str.Back, () => Leave(display));
            return true;
        }

        // out of the Tavern, the way the game's Back does (its detail panel first, if one is open)
        static void Leave(LettuceCollectionDisplay display)
        {
            if (display.IsMercenaryDetailsDisplayActive()) { Log.Info("mercenaries: close the detail panel"); display.HideMercenaryDetailsDisplay(); return; }
            Log.Info("mercenaries: leave the Tavern");
            display.Exit();
        }

        // ---- a mercenary --------------------------------------------------------------------

        static bool Mercenary(LettuceMercenary merc, ref string key, ref string title, List<GameButton> items)
        {
            key = "merc:" + merc.ID;
            var anchor = CollectionDeckTray.Get();
            title = Str.Join(Name(merc), Role(merc.m_role), Level(merc), Coins(merc));
            if (!string.IsNullOrEmpty(merc.m_customAcquireText) && !merc.m_owned) Mercenaries.Info(items, anchor, Str.Clean(merc.m_customAcquireText));
            foreach (var ability in merc.m_abilityList)
                if (ability != null) AbilityItem(items, merc, ability, false);
            var slotted = merc.GetSlottedEquipment();
            foreach (var equipment in merc.m_equipmentList)
                if (equipment != null) AbilityItem(items, merc, equipment, equipment == slotted);
            Mercenaries.Item(items, anchor, Str.Back, () => { s_merc = 0; });
            return true;
        }

        static string Text(LettuceAbility a)
        {
            var id = a.GetCardId();
            var def = string.IsNullOrEmpty(id) ? null : DefLoader.Get().GetEntityDef(id);
            string text = null;
            try { text = def == null ? null : def.GetCardTextInHand(); } catch { }
            return Str.Clean(text);
        }

        // Upgrades sent from here. The game raises the tier when the server answers, but only in its
        // detail page's handler and only for the mercenary that page shows (MercenariesDataUtil
        // .UpdateMercenaryDataModelWithNewData); the server upgraded, the client kept the old tier and
        // every retry was refused. Once the coins show the server took the price, the tier is raised
        // the same way; a refused upgrade leaves the coins, and nothing changes.
        class Pending { internal int Merc; internal LettuceAbility Ability; internal int FromTier; internal long Coins; internal int Cost; internal float Until; }
        static readonly List<Pending> s_pending = new List<Pending>();

        static void ApplyPending(CollectionManager cm)
        {
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                var p = s_pending[i];
                var merc = cm.GetMercenary(p.Merc);
                var a = p.Ability;
                if (merc == null || a == null || a.m_tier != p.FromTier || Time.unscaledTime > p.Until) { s_pending.RemoveAt(i); continue; }
                if (merc.m_currencyAmount > p.Coins - p.Cost) continue;     // no answer yet
                if (a.m_cardType == CollectionUtils.MercenariesModeCardType.Equipment && !a.Owned) { a.Owned = true; a.m_tier = a.GetBaseTier(); }
                else a.m_tier = a.GetNextTier();
                Log.Info("mercenaries: upgraded " + a.GetCardName() + " of " + merc.m_mercName + " to tier " + a.m_tier);
                s_pending.RemoveAt(i);
            }
        }

        static void AbilityItem(List<GameButton> items, LettuceMercenary merc, LettuceAbility a, bool slotted)
        {
            bool equipment = a.m_cardType == CollectionUtils.MercenariesModeCardType.Equipment;
            string name = Str.Clean(a.GetCardName());
            if (name.Length == 0) name = Str.Clean(a.m_abilityName);
            string state;
            bool locked = equipment ? !a.Owned : merc.m_level < a.m_unlockLevel;
            if (locked) state = equipment ? Str.Locked : Str.Game("GLUE_LETTUCE_ABILITY_REACH_LEVEL", a.m_unlockLevel);
            else state = a.m_tier + "/" + a.GetMaxTier();
            bool canUpgrade = !locked && merc.IsCardReadyForUpgrade(a);
            string upgrade = null;
            if (!locked && a.m_tier < a.GetMaxTier())
                upgrade = Str.Join(Str.Word("GLUE_LETTUCE_ABILITY_UPGRADE_TITLE"),
                                   Str.Game("GLUE_LETTUCE_REWARD_MERCENARY_TASK_COINS_REWARD", a.GetNextUpgradeCost(), Str.Clean(merc.m_mercShortName ?? merc.m_mercName)),
                                   canUpgrade ? null : Str.Word("GLUE_LETTUCE_ABILITY_UPGRADE_NOT_ENOUGH_COINS_HEADER"));
            var label = Str.Join(equipment ? Str.Word("GLUE_LETTUCE_EQUIPMENT_TITLE") : null, name, state, slotted ? Mercenaries.Checked : null, upgrade, Text(a));
            var anchor = CollectionDeckTray.Get();
            Mercenaries.Item(items, anchor, label, () =>
            {
                if (s_pending.Exists(p => p.Merc == merc.ID && p.Ability == a)) return;     // one on its way already
                if (canUpgrade)
                {
                    Log.Info("mercenaries: upgrade " + name + " of " + merc.m_mercName + " to tier " + (a.m_tier + 1));
                    s_pending.Add(new Pending { Merc = merc.ID, Ability = a, FromTier = a.m_tier, Coins = merc.m_currencyAmount, Cost = a.GetNextUpgradeCost(), Until = Time.unscaledTime + 30f });
                    if (equipment) Network.Get().UpgradeMercenaryEquipment(merc.ID, a.ID, (uint)(a.m_tier + 1));
                    else Network.Get().UpgradeMercenaryAbility(merc.ID, a.ID, (uint)(a.m_tier + 1));
                    Say(Str.Join(Str.Word("GLUE_LETTUCE_ABILITY_UPGRADE_TITLE"), name));
                    return;
                }
                if (equipment && !locked && !slotted && merc.CanSlotEquipment(a.ID))
                {
                    Log.Info("mercenaries: equip " + name + " on " + merc.m_mercName);
                    merc.SlotEquipment(a.ID);
                    CollectionManager.Get().SendEquippedMercenaryEquipment(merc.ID);
                    Say(Str.Join(name, Mercenaries.Checked));
                    return;
                }
                Say(label);
            });
        }

        // ---- a party ------------------------------------------------------------------------

        static void Done(CollectionDeckTray tray)
        {
            Log.Info("mercenaries: party done");
            // a new name alone does not count as a change to save: it has its own request
            var team = CollectionManager.Get().GetEditingTeam();
            if (team != null) { Log.Info("mercenaries: party name " + team.Name); team.SendTeamRenameChange(); }
            tray.OnBackOutOfMercenariesContents();
        }

        static bool Team(CollectionManager cm, CollectionDeckTray tray, LettuceTeam team, ref string key, ref string title, List<GameButton> items)
        {
            key = "party:" + team.ID;
            var members = team.GetMercs();
            title = Str.Join(Str.Clean(team.Name), members.Count + "/" + cm.GetTeamSize());
            var content = tray.GetMercsContent();
            foreach (var merc in members)
            {
                if (merc == null) continue;
                var m = merc;
                var label = Str.Join(Name(merc), Role(merc.m_role), Level(merc), Str.Word("GLOBAL_FRIENDLIST_REMOVE_FRIEND_BUTTON"));
                Mercenaries.Item(items, tray, label, () =>
                {
                    Log.Info("mercenaries: remove " + m.m_mercName);
                    if (content == null) return;
                    content.RemoveMerc(m.ID, true);
                    Say(Str.Join(Str.Word("GLOBAL_FRIENDLIST_REMOVE_FRIEND_BUTTON"), Name(m)));
                });
            }
            if (members.Count < cm.GetTeamSize())
                foreach (var merc in cm.FindMercenaries(null, true).m_mercenaries)
                {
                    if (merc == null || team.IsMercInTeam(merc.ID)) continue;
                    var m = merc;
                    var label = Str.Join(Str.Word("GLOBAL_FRIENDLIST_ADD_FRIEND_BUTTON"), Name(merc), Role(merc.m_role), Level(merc));
                    Mercenaries.Item(items, tray, label, () =>
                    {
                        Log.Info("mercenaries: add " + m.m_mercName);
                        var def = DefLoader.Get().GetEntityDef(m.GetCardId());
                        if (def == null) return;
                        tray.AddCardToTeam(def, true);
                        Say(Str.Join(Str.Word("GLOBAL_FRIENDLIST_ADD_FRIEND_BUTTON"), Name(m)));
                    });
                }
            var teams = tray.GetTeamsContent();
            // the game's own name box: type the name, Enter keeps it (saved with the party on Done)
            if (teams != null)
                Mercenaries.Item(items, tray, Str.Word("GLUE_COLLECTION_DECK_RENAME"), () =>
                {
                    // the game's own text box (the tray's rename works only from the tray's own
                    // editing view): type, Enter keeps the name (saved with the party on Done),
                    // Escape keeps the old one; HSA's keys are off only while the box is open
                    Log.Info("mercenaries: rename party " + team.Name);
                    var input = UniversalInputManager.Get();
                    var parms = new UniversalInputManager.TextInputParams
                    {
                        m_owner = tray.gameObject,
                        m_rect = new Rect(0.35f, 0.45f, 0.3f, 0.06f),
                        m_maxCharacters = CollectionDeck.DefaultMaxDeckNameCharacters,
                        m_text = "",
                        m_completedCallback = typed =>
                        {
                            TextInput.HsaKeys(true);
                            var name = typed == null ? "" : typed.Trim();
                            var editing = CollectionManager.Get().GetEditingTeam();
                            if (editing == null || name.Length == 0) { Say(Str.Clean(team.Name)); return; }
                            editing.Name = name;
                            Log.Info("mercenaries: party renamed " + name);
                            Say(Str.Join(Str.Word("GLUE_COLLECTION_DECK_RENAME"), name));
                        },
                        m_canceledCallback = (user, requester) => { TextInput.HsaKeys(true); Say(Str.Clean(team.Name)); },
                    };
                    input.UseTextInput(parms, true);
                    if (!input.IsTextInputActive()) { Log.Info("mercenaries: the name box did not open"); Say(Str.Unavailable); return; }
                    TextInput.HsaKeys(false);
                    Say(Str.Join(Str.Clean(team.Name), Speech.S(K.SCREEN_COLLECTION_MANAGER_EDIT_DECK_RENAME_DECK_PROMPT)));
                });
            Mercenaries.Item(items, tray, Str.Word("GLOBAL_DONE"), () => Done(tray));
            if (teams != null)
                Mercenaries.Item(items, tray, Str.Word("GLUE_COLLECTION_DECK_DELETE", "GLOBAL_DELETE"), () => { Log.Info("mercenaries: delete party " + team.Name); teams.DeleteEditingTeam(); });
            return true;
        }
    }
}
