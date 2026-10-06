using System;
using System.Collections.Generic;
using Hearthstone;
using Hearthstone.DataModels;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // The Battlegrounds collection, from the game's collection data:
    //   the tabs: Heroes, Bartenders, Boards, Strikes, Emotes (those the game has turned on)
    //   heroes and their skins (owned only, or all), bartenders, boards, strikes: each with its rarity,
    //     owned or not, favorite; Enter makes an owned one a favorite or not (the game keeps the last one)
    //   emotes: owned or not, in the six emote slots or not; Enter puts it in a free slot or takes it
    //     out (the game saves the slots when the tab is left)
    static class BgCollection
    {
        static BgCollectionUI s_ui;

        internal static BaconCollectionDisplay Display()
        {
            var scenes = SceneMgr.Get();
            if (scenes == null || scenes.GetMode() != SceneMgr.Mode.BACON_COLLECTION || scenes.IsTransitioning() || !scenes.IsSceneLoaded() || GameState.Get() != null) return null;
            var cm = CollectionManager.Get();
            var display = cm == null ? null : cm.GetCollectibleDisplay() as BaconCollectionDisplay;
            if (display == null || !display.IsReady()) return null;
            return display;
        }

        // every frame
        internal static void Tick()
        {
            var display = Display();
            if (s_ui != null && display == null) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && display != null)
            {
                var ui = new BgCollectionUI();
                if (!ui.Refresh(true)) return;
                s_ui = ui;
                Generic.Yield();
                Focus.PushBase(ui);
                if (ui.Focused) ui.Read();
            }
            if (s_ui != null) s_ui.Refresh(false);
        }
    }

    class BgCollectionUI : Core.Screen
    {
        Menu m_menu;
        string m_key, m_signature;
        float m_next;
        bool m_inTab;      // a tab's list is open (else the tabs)

        internal override bool Alive { get { return BgCollection.Display() != null; } }

        static CollectionManager CM { get { return CollectionManager.Get(); } }

        static readonly CollectionUtils.ViewMode[] Tabs = {
            CollectionUtils.ViewMode.BATTLEGROUNDS_HERO_SKINS, CollectionUtils.ViewMode.BATTLEGROUNDS_GUIDE_SKINS, CollectionUtils.ViewMode.BATTLEGROUNDS_BOARD_SKINS,
            CollectionUtils.ViewMode.BATTLEGROUNDS_FINISHERS, CollectionUtils.ViewMode.BATTLEGROUNDS_EMOTES };

        static string TabName(CollectionUtils.ViewMode mode)
        {
            switch (mode)
            {
                case CollectionUtils.ViewMode.BATTLEGROUNDS_HERO_SKINS: return Str.Word("GLUE_COLLECTION_MANAGER_HERO_SKINS_TITLE");
                case CollectionUtils.ViewMode.BATTLEGROUNDS_GUIDE_SKINS: return Str.Word("GLUE_BACON_COLLECTION_MANAGER_GUIDE_SKINS_TITLE");
                case CollectionUtils.ViewMode.BATTLEGROUNDS_BOARD_SKINS: return Str.Word("GLUE_BACON_COLLECTION_MANAGER_BOARD_SKINS_TITLE");
                case CollectionUtils.ViewMode.BATTLEGROUNDS_FINISHERS: return Str.Word("GLUE_BACON_COLLECTION_MANAGER_FINISHERS_TITLE");
                case CollectionUtils.ViewMode.BATTLEGROUNDS_EMOTES: return Str.Word("GLUE_BACON_COLLECTION_MANAGER_EMOTES_TITLE");
            }
            return mode.ToString();
        }

        static bool TabOn(CollectionUtils.ViewMode mode)
        {
            var f = NetCache.Get().GetNetObject<NetCache.NetCacheFeatures>();
            if (f == null) return mode == CollectionUtils.ViewMode.BATTLEGROUNDS_HERO_SKINS;
            switch (mode)
            {
                case CollectionUtils.ViewMode.BATTLEGROUNDS_GUIDE_SKINS: return f.BattlegroundsGuideSkinsEnabled;
                case CollectionUtils.ViewMode.BATTLEGROUNDS_BOARD_SKINS: return f.BattlegroundsBoardSkinsEnabled;
                case CollectionUtils.ViewMode.BATTLEGROUNDS_FINISHERS: return f.BattlegroundsFinishersEnabled;
                case CollectionUtils.ViewMode.BATTLEGROUNDS_EMOTES: return f.BattlegroundsEmotesEnabled;
            }
            return true;
        }

        internal bool Refresh(bool now)
        {
            if (!now && Time.unscaledTime < m_next) return m_menu != null;
            m_next = Time.unscaledTime + 0.4f;
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
                Log.Info("bg collection: " + key + " '" + title + "': " + (items.Count > 40 ? items.Count + " options" : GameButton.Describe(items)));
                if (Focused) m_menu.StartReading();
            }
            return true;
        }

        bool Build(out string key, out string title, List<GameButton> items)
        {
            key = null; title = "";
            var display = BgCollection.Display();
            if (display == null) return false;
            var pm = display.m_pageManager as BaconCollectionPageManager;
            if (pm == null || !pm.IsFullyLoaded() || pm.ArePagesTurning()) return false;
            var view = display.GetViewMode();
            if (!m_inTab)
            {
                key = "tabs";
                title = Str.Join(Str.Word("GLUE_BACON"), Str.Word("GLUE_MY_COLLECTION"));
                foreach (var t in Tabs)
                {
                    if (!TabOn(t)) continue;
                    var tab = t;
                    Add(items, display, Str.Join(TabName(t), t == view ? Friends.Checked(true) : null), () =>
                    {
                        Log.Info("bg collection: tab " + tab);
                        if (display.GetViewMode() != tab) display.SetViewMode(tab);
                        m_inTab = true;
                        m_next = 0;
                    });
                }
                return true;
            }
            key = "tab:" + view;
            title = TabName(view);
            switch (view)
            {
                case CollectionUtils.ViewMode.BATTLEGROUNDS_HERO_SKINS: Heroes(display, pm, items); break;
                case CollectionUtils.ViewMode.BATTLEGROUNDS_GUIDE_SKINS: Guides(display, pm, items); break;
                case CollectionUtils.ViewMode.BATTLEGROUNDS_BOARD_SKINS: Boards(display, pm, items); break;
                case CollectionUtils.ViewMode.BATTLEGROUNDS_FINISHERS: Finishers(display, pm, items); break;
                case CollectionUtils.ViewMode.BATTLEGROUNDS_EMOTES: Emotes(display, pm, items); break;
                default: m_inTab = false; return false;
            }
            return true;
        }

        static string Fav { get { return Str.Word("GLUE_BACON_COLLECTION_FAVORITE"); } }

        static string Rarity(TAG_RARITY r)
        {
            try { return Str.Clean(GameStrings.GetRarityText(r)); } catch { return null; }
        }

        // every card of a paged filter, in its order
        static List<CollectibleCard> AllPages(CollectibleCardFilter filter)
        {
            var all = new List<CollectibleCard>();
            if (filter == null) return all;
            int pages = filter.GetTotalNumPages();
            for (int p = 1; p <= pages; p++)
            {
                var page = filter.GetPageContents(p);
                if (page != null) foreach (var c in page) if (c != null) all.Add(c);
            }
            return all;
        }

        void Later() { m_next = Time.unscaledTime + 1f; }

        void Heroes(BaconCollectionDisplay display, BaconCollectionPageManager pm, List<GameButton> items)
        {
            bool all = display.GetHeroSkinFilterMode() == CollectionUtils.BattlegroundsHeroSkinFilterMode.ALL;
            Add(items, display, Str.Join(Str.Word("GLUE_BACON_COLLECTION_FILTER_ALL"), Friends.Checked(all)), () => { display.ToggleHeroSkinFilterMode(); m_next = 0; });
            foreach (var c in AllPages(Ref.Get<CollectibleCardFilter>(pm, "m_baconHeroesCollection")))
            {
                var def = c.GetEntityDef();
                if (def == null) continue;
                bool owned = c.OwnedCount > 0;
                bool fav = BaconHeroSkinUtils.IsBattlegroundsHeroSkinFavorited(def);
                string rotation = null;
                try
                {
                    var r = BaconHeroSkinUtils.GetBattleGroundsHeroRotationType(GameUtils.GetCardRecord(c.CardId), def).ToString();
                    rotation = r == "Resting" ? Str.Word("GLUE_BACON_COLLECTION_RESTING") : r == "Preview" ? Str.Word("GLUE_BACON_COLLECTION_PREVIEWING") : null;
                }
                catch { }
                var card = c;
                Add(items, display, Str.Join(Str.Clean(c.Name), Rarity(def.GetRarity()), rotation, owned ? null : Str.NotOwned, fav ? Fav : null), () =>
                {
                    if (!owned) { Speech.Say(Str.NotOwned); return; }
                    if (!BaconHeroSkinUtils.CanToggleFavoriteBattlegroundsHeroSkin(def)) { Speech.Say(Str.Unavailable); return; }
                    // as the hero's preview does it: the base hero is skin 0
                    int baseId = GameUtils.TranslateCardIdToDbId(CM.GetBattlegroundsBaseHeroCardId(card.CardId));
                    int dbId = GameUtils.TranslateCardIdToDbId(card.CardId);
                    int skin = BaconHeroSkinUtils.SKIN_ID_FOR_FAVORITED_BASE_HERO;
                    if (CM.IsBattlegroundsHeroSkinCard(dbId))
                    {
                        var rec = GameDbf.BattlegroundsHeroSkin.GetRecord(h => h.SkinCardId == dbId);
                        if (rec == null) { Speech.Say(Str.Unavailable); return; }
                        skin = rec.ID;
                    }
                    Log.Info("bg collection: favorite hero " + card.CardId + " " + !BaconHeroSkinUtils.IsBattlegroundsHeroSkinFavorited(def));
                    Network.Get().UpdateFavoriteBattlegroundsHeroSkin(baseId, skin, !BaconHeroSkinUtils.IsBattlegroundsHeroSkinFavorited(def));
                    Later();
                });
            }
        }

        void Guides(BaconCollectionDisplay display, BaconCollectionPageManager pm, List<GameButton> items)
        {
            foreach (var c in AllPages(Ref.Get<CollectibleCardFilter>(pm, "m_baconGuidesCollection")))
            {
                var def = c.GetEntityDef();
                if (def == null) continue;
                bool owned = CM.OwnsBattlegroundsGuideSkin(c.CardId);
                bool fav = BaconHeroSkinUtils.IsBattlegroundsGuideSkinFavorited(def);
                var card = c;
                Add(items, display, Str.Join(Str.Clean(c.Name), Rarity(def.GetRarity()), owned ? null : Str.NotOwned, fav ? Fav : null), () =>
                {
                    if (!owned) { Speech.Say(Str.NotOwned); return; }
                    BattlegroundsGuideSkinId id;
                    if (!BaconHeroSkinUtils.CanToggleFavoriteBattlegroundsGuideSkin(def) || !CM.GetBattlegroundsGuideSkinIdForCardId(GameUtils.TranslateCardIdToDbId(card.CardId), out id)) { Speech.Say(Str.Unavailable); return; }
                    Log.Info("bg collection: favorite bartender " + card.CardId + " " + !fav);
                    if (fav) Network.Get().ClearBattlegroundsFavoriteGuideSkin(id); else Network.Get().SetBattlegroundsFavoriteGuideSkin(id);
                    Later();
                });
            }
        }

        void Boards(BaconCollectionDisplay display, BaconCollectionPageManager pm, List<GameButton> items)
        {
            var set = Ref.Get<CollectibleBattlegroundsBoardSet>(pm, "m_baconBoardsCollection");
            if (set == null) return;
            foreach (var b in set.ItemsRemaining)
            {
                if (b == null) continue;
                var dm = b.CreateBoardDataModel();
                int id = dm.BoardDbiId;
                bool owned = dm.IsOwned, fav = dm.IsFavorite;
                Add(items, display, Str.Join(Str.Clean(dm.DetailsDisplayName ?? dm.DisplayName), Str.Clean(dm.Rarity), owned ? null : Str.NotOwned, fav ? Fav : null, Str.Clean(dm.Description)), () =>
                {
                    if (!owned) { Speech.Say(Str.NotOwned); return; }
                    Log.Info("bg collection: favorite board " + id + " " + !fav);
                    if (fav) Network.Get().ClearBattlegroundsFavoriteBoardSkin(BattlegroundsBoardSkinId.FromTrustedValue(id));
                    else Network.Get().SetBattlegroundsFavoriteBoardSkin(BattlegroundsBoardSkinId.FromTrustedValue(id));
                    Later();
                });
            }
        }

        void Finishers(BaconCollectionDisplay display, BaconCollectionPageManager pm, List<GameButton> items)
        {
            var set = Ref.Get<CollectibleBattlegroundsFinisherSet>(pm, "m_baconFinishersCollection");
            if (set == null) return;
            foreach (var f in set.ItemsRemaining)
            {
                if (f == null) continue;
                var dm = f.CreateFinisherDataModel();
                int id = dm.FinisherDbiId;
                bool owned = dm.IsOwned, fav = dm.IsFavorite;
                Add(items, display, Str.Join(Str.Clean(dm.DetailsDisplayName ?? dm.DisplayName), Str.Clean(dm.Rarity), owned ? null : Str.NotOwned, fav ? Fav : null, Str.Clean(dm.Description)), () =>
                {
                    if (!owned) { Speech.Say(Str.NotOwned); return; }
                    Log.Info("bg collection: favorite strike " + id + " " + !fav);
                    if (fav) Network.Get().ClearBattlegroundsFavoriteFinisher(BattlegroundsFinisherId.FromTrustedValue(id));
                    else Network.Get().SetBattlegroundsFavoriteFinisher(BattlegroundsFinisherId.FromTrustedValue(id));
                    Later();
                });
            }
        }

        void Emotes(BaconCollectionDisplay display, BaconCollectionPageManager pm, List<GameButton> items)
        {
            var set = Ref.Get<CollectibleBattlegroundsEmoteSet>(pm, "m_baconEmotesCollection");
            var tray = Ref.Get<BaconEmoteTray>(display, "m_emoteTray");
            if (set == null) return;
            foreach (var e in set.ItemsRemaining)
            {
                if (e == null) continue;
                var dm = e.CreateEmoteDataModel();
                int id = dm.EmoteDbiId;
                bool owned = dm.IsOwned;
                bool on = tray != null ? tray.IsEmoteInLoadout(id) : dm.IsEquipped;
                Add(items, display, Str.Join(Str.Clean(dm.DisplayName), Str.Clean(dm.Rarity), owned ? null : Str.NotOwned, Friends.Checked(on), Str.Clean(dm.Description)), () =>
                {
                    if (!owned) { Speech.Say(Str.NotOwned); return; }
                    if (tray == null) { Speech.Say(Str.Unavailable); return; }
                    Log.Info("bg collection: emote " + id + " " + !on);
                    if (on)
                    {
                        var loadout = tray.GetLoadoutDataModel();
                        if (loadout != null) foreach (var slot in loadout.EmoteList) if (slot != null && slot.EmoteDbiId == id) { tray.RemoveEmote(slot); break; }
                    }
                    else tray.DropOverEmoteTray(dm);
                    pm.SetEmoteEquippedState(BattlegroundsEmoteId.FromTrustedValue(id), tray.IsEmoteInLoadout(id));
                    Speech.Say(Str.Join(Str.Clean(dm.DisplayName), Friends.Checked(tray.IsEmoteInLoadout(id))));
                    m_next = 0;
                });
            }
        }

        static void Add(List<GameButton> items, Component anchor, string label, Action click)
        {
            if (string.IsNullOrEmpty(label)) return;
            var said = label;
            items.Add(new GameButton { Target = anchor, Label = label, Click = click ?? (() => Speech.Say(said)) });
        }

        void Back()
        {
            if (m_inTab) { m_inTab = false; m_next = 0; return; }
            Log.Info("bg collection: back");
            Navigation.GoBack();
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
