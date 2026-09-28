using System;
using System.Collections;
using System.Collections.Generic;
using Accessibility;
using Hearthstone.DataModels;
using UnityEngine;

namespace HSAEnhanced
{
    // Pets (Collection > Pets), which HSA leaves out (its CollectiblePet is not accessible). Read from
    // the game's own pet data (PetUtility's data models) and changed through the calls its buttons
    // make: Favorite Pet / Favorite Skin (Network.SetFavoritePet / SetFavoritePetVariant), and, while
    // a deck is edited, the pet the deck uses (the deck tray's UpdatePet / UpdatePetVariant, what
    // dropping a pet on the deck does; Random and Favorites Only as the tray's pet slot).
    // UNTESTED: written without an account that owns a pet.
    static class Pets
    {
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AccessibleMenu, object> s_added = new System.Runtime.CompilerServices.ConditionalWeakTable<AccessibleMenu, object>();

        internal static string Title { get { return Str.Word("GLUE_COLLECTION_MANAGER_PET_TITLE"); } }

        // the game shows pets in the Collection (its pets tab is there when this feature is on)
        internal static bool Enabled()
        {
            try
            {
                var features = NetCache.Get().GetNetObject<NetCache.NetCacheFeatures>();
                return features != null && features.PetSkinsHSEnabled && PetsManager.Get() != null;
            }
            catch { return false; }
        }

        // HSA's Browse Collection menu (Cards, Hero Skins, Card Backs, Coins) and its menu of the deck
        // being edited get Pets
        internal static void AddTo(AccessibleMenu menu, object collection)
        {
            object mark;
            if (s_added.TryGetValue(menu, out mark) || Title.Length == 0) return;
            bool browse = ReferenceEquals(Ref.Get(collection, "m_browseCollectionMenu"), menu);
            bool editDeck = ReferenceEquals(Ref.Get(collection, "m_editDeckMenu"), menu) && EditedDeck() != null;
            if (!browse && !editDeck) return;
            if (!Enabled()) return;
            // while a deck is edited the game shows the tab only when a pet is owned
            if (editDeck && PetsManager.Get().GetTotalPetsOwned() <= 0) return;
            s_added.Add(menu, true);
            var from = menu;
            menu.AddOption(Title, () => AccessiblePets.Open(from));
        }

        internal static CollectionDeck EditedDeck()
        {
            try
            {
                var cm = CollectionManager.Get();
                return cm != null && cm.IsInEditMode() ? cm.GetEditedDeck() : null;
            }
            catch { return null; }
        }

        internal static string Checked(bool on)
        {
            return LocalizationUtils.Get(on ? LocalizationKey.OPTIONS_MENU_CHECKBOX_CHECKED : LocalizationKey.OPTIONS_MENU_CHECKBOX_NOT_CHECKED);
        }

        internal static string Level(int level) { return Str.Game("GLOBAL_PROGRESSION_TOOLTIP_CLASS_DEFAULT_DESC", level); }

        // every pet the game lists (its enabled records), owned first, then by name, as its pages
        internal static List<PetDataModel> All()
        {
            var pets = new List<PetDataModel>();
            try
            {
                foreach (var record in GameDbf.Pet.GetRecords())
                    if (record != null && record.Enabled) pets.Add(PetUtility.CreatePetDataModel(record.ID));
            }
            catch (Exception e) { Log.Error(e); }
            pets.Sort((a, b) =>
            {
                bool oa = Owned(a), ob = Owned(b);
                if (oa != ob) return oa ? -1 : 1;
                int byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
                return byName != 0 ? byName : a.PetDbiId.CompareTo(b.PetDbiId);
            });
            return pets;
        }

        internal static bool Owned(PetDataModel pet) { return PetsManager.Get().IsPetOwned(pet.PetDbiId); }

        // the deck tray's pet slot: the deck's pet as dropping one on it sets it (with its visuals),
        // else straight on the deck (saved with the deck either way)
        internal static void UseInDeck(int petId, int? variantId)
        {
            var deck = EditedDeck();
            if (deck == null) return;
            try
            {
                var tray = CollectionDeckTray.Get();
                var content = tray == null ? null : tray.GetPetContent();
                if (content != null)
                {
                    if (variantId.HasValue) content.UpdatePetVariant(variantId.Value, true);
                    else content.UpdatePet(petId, true);
                }
            }
            catch (Exception e) { Log.Error(e); }
            if (deck.PetID != petId || deck.PetVariantID != variantId) { deck.PetID = petId; deck.PetVariantID = variantId; }
            Log.Info("pets: deck " + deck.Name + " uses pet " + petId + (variantId.HasValue ? " skin " + variantId.Value : ""));
        }

        // back to a random pet, as clicking the tray's pet slot does
        internal static void RemoveFromDeck()
        {
            var deck = EditedDeck();
            if (deck == null) return;
            try
            {
                var tray = CollectionDeckTray.Get();
                var content = tray == null ? null : tray.GetPetContent();
                if (content != null) content.RemovePet();
            }
            catch (Exception e) { Log.Error(e); }
            deck.PetID = null; deck.PetVariantID = null;
            Log.Info("pets: deck " + deck.Name + " uses a random pet");
        }

        // the tray's Favorites Only checkbox (the random pet among favorites only)
        internal static void SetFavoritesOnly(bool on)
        {
            var deck = EditedDeck();
            if (deck == null) return;
            try
            {
                var tray = CollectionDeckTray.Get();
                var content = tray == null ? null : tray.GetPetContent();
                if (content != null) Ref.Call(content, "SetRandomPetUseFavorite", on);
            }
            catch (Exception e) { Log.Error(e); }
            deck.RandomPetUseFavorite = on;
        }
    }

    // The pets menu over the Collection: the pets, one pet (its details, Favorite Pet, its skins) and
    // one skin (Favorite Skin); Choose sets the pet of the deck being edited. Back goes up a level and
    // from the list back to the HSA menu it was opened from.
    class AccessiblePets : AccessibleUI
    {
        static AccessiblePets s_open;

        readonly AccessibleMenu m_from;
        AccessibleMenu m_menu;
        Action m_rebuild;       // builds the menu shown now again (after a change the game confirms later)

        AccessiblePets(AccessibleMenu from) { m_from = from; }

        internal static void Open(AccessibleMenu from)
        {
            if (s_open != null) AccessibilityMgr.HideUI(s_open);
            s_open = new AccessiblePets(from);
            AccessibilityMgr.ShowUI(s_open);
            s_open.ShowList(0);
        }

        void Show(AccessibleMenu menu, Action rebuild)
        {
            m_menu = menu;
            m_rebuild = rebuild;
            m_menu.StartReading();
        }

        void Close()
        {
            AccessibilityMgr.HideUI(this);
            if (s_open == this) s_open = null;
            if (m_from != null) m_from.StartReading();
        }

        // the change reaches the server; the game's data says it a moment later: the menu is read again
        void RebuildSoon()
        {
            var at = m_menu == null ? 0 : MenuEdit.GetIndex(m_menu);
            FallbackWatcher.Run(RebuildAfter(at));
        }

        IEnumerator RebuildAfter(int at)
        {
            yield return new WaitForSecondsRealtime(1f);
            if (s_open != this || m_rebuild == null) yield break;
            m_rebuild();
            if (m_menu != null) MenuEdit.SetIndex(m_menu, Math.Max(0, Math.Min(at, m_menu.GetNumItems() - 1)));
        }

        #region The pets
        void ShowList(int at)
        {
            var deck = Pets.EditedDeck();
            var menu = new AccessibleMenu(this, Pets.Title, Close);
            // the deck's pet slot: Random (no pet of its own) and Favorites Only
            if (deck != null)
            {
                var random = Str.Word("GLUE_COLLECTION_MANAGER_DECK_CARD_BACK_RANDOM_TITLE");
                menu.AddOption(Str.Join(random, deck.PetID.HasValue ? null : Str.Word("GLUE_BACON_COLLECTION_EQUIPPED_EMOTE")), () =>
                {
                    Pets.RemoveFromDeck();
                    ShowList(MenuEdit.GetIndex(m_menu));
                });
                var only = Str.Word("GLUE_COLLECTION_MANAGER_DECK_CARD_BACK_RANDOM_TOGGLE");
                menu.AddOption(Str.Join(only, Pets.Checked(deck.RandomPetUseFavorite)), () =>
                {
                    Pets.SetFavoritesOnly(!deck.RandomPetUseFavorite);
                    ShowList(MenuEdit.GetIndex(m_menu));
                });
            }
            foreach (var p in Pets.All())
            {
                var pet = p;
                menu.AddOption(PetLabel(pet, deck), () => ShowPet(pet.PetDbiId, 0));
            }
            menu.SetIndex(Math.Max(0, Math.Min(at, menu.GetNumItems() - 1)));
            Show(menu, () => ShowList(MenuEdit.GetIndex(m_menu)));
        }

        static string PetLabel(PetDataModel pet, CollectionDeck deck)
        {
            if (!Pets.Owned(pet)) return Str.Join(Str.Clean(pet.DisplayName), Str.NotOwned);
            return Str.Join(Str.Clean(pet.DisplayName), Pets.Level(pet.CurrentLevel),
                pet.IsFavorite ? Str.Word("GLUE_PETPREVIEW_FAVORITE_PET") : null,
                deck != null && deck.PetID == pet.PetDbiId ? Str.Word("GLUE_BACON_COLLECTION_EQUIPPED_EMOTE") : null);
        }
        #endregion

        #region One pet
        void ShowPet(int petId, int at)
        {
            var pet = PetUtility.CreatePetDataModel(petId);
            var deck = Pets.EditedDeck();
            bool owned = Pets.Owned(pet);
            var menu = new AccessibleMenu(this, Str.Clean(pet.DisplayName), () => ShowList(0));
            var ui = this;
            // what the pet's page says
            foreach (var text in new[] { pet.Description, owned ? null : pet.HowToGet })
            {
                var t = Str.Clean(text);
                if (t.Length > 0) menu.AddOption(t, () => AccessibilityMgr.Output(ui, t));
            }
            if (owned)
            {
                var level = Pets.Level(pet.CurrentLevel);
                if (!string.IsNullOrEmpty(level)) menu.AddOption(level, () => AccessibilityMgr.Output(ui, level));
                // the Favorite Pet button
                menu.AddOption(Str.Join(Str.Word("GLUE_PETPREVIEW_FAVORITE_PET"), Pets.Checked(pet.IsFavorite)), () =>
                {
                    Log.Info("pets: favorite pet " + petId + " " + !pet.IsFavorite);
                    Network.Get().SetFavoritePet(petId, !pet.IsFavorite, null);
                    RebuildSoon();
                });
                // the deck being edited uses this pet (any of its skins)
                if (deck != null)
                    menu.AddOption(Str.Join(Str.Word("GLUE_CHOOSE"), deck.PetID == petId && !deck.PetVariantID.HasValue ? Str.Word("GLUE_BACON_COLLECTION_EQUIPPED_EMOTE") : null), () =>
                    {
                        Pets.UseInDeck(petId, null);
                        ShowPet(petId, MenuEdit.GetIndex(m_menu));
                    });
            }
            // its skins, one per level
            if (pet.PetLevels != null)
                foreach (var l in pet.PetLevels)
                {
                    var level = l;
                    if (level == null || level.PetSkin == null) continue;
                    menu.AddOption(SkinLabel(level, deck), () => ShowSkin(petId, level.PetSkin.PetVariantDbiId, 0));
                }
            menu.SetIndex(Math.Max(0, Math.Min(at, menu.GetNumItems() - 1)));
            Show(menu, () => ShowPet(petId, MenuEdit.GetIndex(m_menu)));
        }

        static string SkinLabel(PetLevelDataModel level, CollectionDeck deck)
        {
            var skin = level.PetSkin;
            string state;
            if (!skin.Owned) state = Str.Word("GLUE_PETPREVIEW_UNLOCKMETHOD_LEVEL");
            else if (level.Level <= 1) state = Str.Word("GLUE_PETPREVIEW_UNLOCK_DEFAULT");
            else state = null;
            return Str.Join(Str.Clean(skin.DisplayName), Pets.Level(level.Level), state,
                skin.Owned && skin.IsFavorite ? Str.Word("GLUE_PETPREVIEW_FAVORITE_SKIN") : null,
                deck != null && deck.PetVariantID == skin.PetVariantDbiId ? Str.Word("GLUE_BACON_COLLECTION_EQUIPPED_EMOTE") : null);
        }
        #endregion

        #region One skin
        void ShowSkin(int petId, int variantId, int at)
        {
            var skin = PetUtility.CreatePetSkinDataModel(variantId);
            var deck = Pets.EditedDeck();
            var menu = new AccessibleMenu(this, Str.Clean(skin.DisplayName), () => ShowPet(petId, 0));
            var ui = this;
            foreach (var text in new[] { skin.CollectionDescription, skin.Owned ? null : skin.PreviewDescription })
            {
                var t = Str.Clean(text);
                if (t.Length > 0) menu.AddOption(t, () => AccessibilityMgr.Output(ui, t));
            }
            if (skin.Owned)
            {
                // the Favorite Skin button (the game keeps at least one skin of a pet a favorite)
                menu.AddOption(Str.Join(Str.Word("GLUE_PETPREVIEW_FAVORITE_SKIN"), Pets.Checked(skin.IsFavorite)), () =>
                {
                    if (skin.IsFavorite && !PetsManager.Get().CanUnfavoritePetVariant(variantId, false))
                    {
                        AccessibilityMgr.Output(ui, Str.Unavailable);
                        return;
                    }
                    Log.Info("pets: favorite skin " + variantId + " " + !skin.IsFavorite);
                    Network.Get().SetFavoritePetVariant(variantId, !skin.IsFavorite, null);
                    RebuildSoon();
                });
                if (deck != null)
                    menu.AddOption(Str.Join(Str.Word("GLUE_CHOOSE"), deck.PetVariantID == variantId ? Str.Word("GLUE_BACON_COLLECTION_EQUIPPED_EMOTE") : null), () =>
                    {
                        Pets.UseInDeck(petId, variantId);
                        ShowSkin(petId, variantId, MenuEdit.GetIndex(m_menu));
                    });
            }
            else menu.AddOption(Str.Word("GLUE_PETPREVIEW_UNLOCKMETHOD_LEVEL"), () => AccessibilityMgr.Output(ui, Str.Word("GLUE_PETPREVIEW_UNLOCKMETHOD_LEVEL")));
            menu.SetIndex(Math.Max(0, Math.Min(at, menu.GetNumItems() - 1)));
            Show(menu, () => ShowSkin(petId, variantId, MenuEdit.GetIndex(m_menu)));
        }
        #endregion

        public void HandleAccessibleInput() { if (m_menu != null) m_menu.HandleAccessibleInput(); }

        public string GetAccessibleHelp() { return m_menu == null ? "" : m_menu.GetHelp(); }
    }
}
