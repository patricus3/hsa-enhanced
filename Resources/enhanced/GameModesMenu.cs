using System;
using Accessibility;
using Hearthstone.DataModels;

namespace HSAEnhanced
{
    // The game modes screen: one option per mode button the game shows, in the game's order
    // (HSA lists only Solo Adventures and Tavern Brawl).
    static class GameModesMenu
    {
        internal static AccessibleMenu Build(AccessibleComponent scene, GameModeSceneDataModel model, AccessibleMenu hsaMenu)
        {
            if (!Engine.Enabled || model == null || model.GameModeButtons == null || model.GameModeButtons.Count == 0) return hsaMenu;
            var menu = new AccessibleMenu(scene, LocalizationUtils.Get(LocalizationKey.GLOBAL_CHOOSE_MODE), ClickBack);
            int last = -1;
            foreach (var button in model.GameModeButtons)
            {
                var b = button;
                if (b == null) continue;
                var name = Str.Clean(b.Name);
                if (name.Length == 0) continue;
                menu.AddOption(Str.Join(name, Flags(b)), () => Choose(scene, b));
                if (b.GameModeRecordId == model.LastSelectedGameModeRecordId) last = menu.GetNumItems() - 1;
            }
            menu.AddOption(LocalizedText.SCREEN_GO_BACK, ClickBack);
            if (last >= 0) menu.SetIndex(last);
            return menu;
        }

        static string Flags(GameModeButtonDataModel b)
        {
            return Str.Join(
                b.IsNew ? Str.Word("GLUE_COLLECTION_CARD_NEW") : null,
                b.IsEarlyAccess ? Str.Word("GLUE_GAME_MODES_POPUP_EARLY_ACCESS") : null,
                b.IsBeta ? Str.Word("GLUE_GAME_MODES_POPUP_BETA") : null,
                b.IsDownloading ? Str.Word("GLUE_TOOLTIP_DOWNLOAD_HEADER") :
                    b.IsDownloadRequired ? Str.Word("GLUE_GAME_MODE_TOOLTIP_DOWNLOAD_REQUIRED_TITLE") : null);
        }

        static void Choose(AccessibleComponent scene, GameModeButtonDataModel b)
        {
            try
            {
                var display = GameModeDisplay.Get();
                display.SelectMode(b);
                string reason;
                if (!display.CanEnterMode(out reason, out _))
                {
                    AccessibilityMgr.Output(scene, string.IsNullOrEmpty(reason) ? Str.Clean(b.Description) : Str.Clean(reason));
                    return;
                }
                var play = Ui.FieldOfType<PlayButton>(display);
                if (play != null) play.TriggerRelease();
            }
            catch (Exception e) { Log.Error(e); }
        }

        // the game's own back (Escape), else the screen's back button
        static void ClickBack()
        {
            var display = GameModeDisplay.Get();
            Back.Go(null, display == null ? null : display.gameObject);
        }
    }
}
