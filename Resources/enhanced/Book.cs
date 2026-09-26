using System;
using System.Collections;
using System.Collections.Generic;
using Accessibility;
using Hearthstone.DataModels;
using Hearthstone.UI;
using PegasusShared;
using UnityEngine;

namespace HSAEnhanced
{
    // Adventure books (Descent of Dragons, Galakrond's Awakening, Book of Heroes, ...), read from the
    // page the game shows: its data model says whether it is the map of chapters or one chapter
    // with its missions, and in which state each is.
    static class Book
    {
        static AdventureBookPageDisplay Shown()
        {
            foreach (var d in UnityEngine.Object.FindObjectsByType<AdventureBookPageDisplay>(FindObjectsSortMode.None))
                if (d != null && d.isActiveAndEnabled && Ref.Get<AdventureBookPageDataModel>(d, "m_pageDataModel") != null) return d;
            return null;
        }

        static AdventureBookPageDataModel Page(AdventureBookPageDisplay d) { return Ref.Get<AdventureBookPageDataModel>(d, "m_pageDataModel"); }

        internal static string Title()
        {
            var d = Shown();
            var page = d == null ? null : Page(d);
            if (page == null) return null;
            if (page.PageType == AdventureBookPageType.CHAPTER && page.ChapterData != null)
                return Str.Join(Str.Clean(page.ChapterData.Name), LocalizationUtils.Get(LocalizationKey.SCREEN_ADVENTURE_SCREEN_CHOOSE_MISSION_TITLE));
            return LocalizationUtils.Get(LocalizationKey.SCREEN_ADVENTURE_SCREEN_CHOOSE_CHAPTER_TITLE);
        }

        // The chapters (map page) or missions (chapter page) shown; null when no book page is up
        internal static List<GameButton> Buttons()
        {
            var d = Shown();
            var page = d == null ? null : Page(d);
            if (page == null) return null;
            if (page.PageType == AdventureBookPageType.MAP) return Chapters(d, page);
            if (page.PageType == AdventureBookPageType.CHAPTER) return Missions(d, page);
            return null;
        }

        static List<GameButton> Chapters(AdventureBookPageDisplay d, AdventureBookPageDataModel page)
        {
            var found = new List<GameButton>();
            var map = Ref.Get(d, "m_chapterButtonClickablesNameMap") as IEnumerable;
            if (map == null) return found;
            foreach (var entry in map)
            {
                var t = entry.GetType();
                var name = t.GetProperty("Key") == null ? null : t.GetProperty("Key").GetValue(entry, null) as string;
                var button = t.GetProperty("Value") == null ? null : t.GetProperty("Value").GetValue(entry, null) as Clickable;
                if (string.IsNullOrEmpty(name) || button == null || !button) continue;
                var chapter = ChapterNamed(page, name);
                var label = Str.Join(Str.Clean(name), chapter == null ? null : ChapterState(chapter));
                var b = button;
                found.Add(new GameButton { Target = b, Label = label, Click = () => Ui.Press(b) });
            }
            return found;
        }

        static AdventureChapterDataModel ChapterNamed(AdventureBookPageDataModel page, string name)
        {
            if (page.AllChaptersData == null) return null;
            foreach (var c in page.AllChaptersData)
                if (c != null && Labels.Norm(c.Name) == Labels.Norm(name)) return c;
            return null;
        }

        static string ChapterState(AdventureChapterDataModel c)
        {
            if (c.TimeLocked) return Str.Join(Str.Locked, Str.Clean(c.TimeLockInfoMessage));
            if (!c.PlayerOwnsChapter) return Str.NotOwned;
            switch (c.ChapterState.ToString())
            {
                case "LOCKED": return Str.Join(Str.Locked, Str.Clean(c.UnlockChapterText));
                case "COMPLETED": return Str.Completed;
            }
            return null;
        }

        static List<GameButton> Missions(AdventureBookPageDisplay d, AdventureBookPageDataModel page)
        {
            var found = new List<GameButton>();
            if (page.ChapterData == null || page.ChapterData.Missions == null) return found;
            int n = 1;
            foreach (var m in page.ChapterData.Missions)
            {
                if (m == null) continue;
                var mission = m;
                var state = mission.MissionState.ToString();
                var label = Str.Join(n + ". " + ScenarioName(mission.ScenarioId),
                    state == "LOCKED" ? Str.Locked : state == "COMPLETED" ? Str.Completed : null);
                found.Add(new GameButton { Target = d, Label = label, Click = () => Play(d, mission) });
                n++;
            }
            return found;
        }

        static string ScenarioName(ScenarioDbId id)
        {
            try
            {
                var record = GameDbf.Scenario.GetRecord((int)id);
                if (record != null)
                {
                    var name = record.Name == null ? null : record.Name.GetString();
                    if (string.IsNullOrEmpty(name) && record.OpponentName != null) name = record.OpponentName.GetString();
                    if (!string.IsNullOrEmpty(name)) return Str.Clean(name);
                }
            }
            catch (Exception e) { Log.Error(e); }
            return id.ToString();
        }

        // Starts a mission the way the book's play button does: the deck (or hero) picker when the
        // mission needs a deck, else the game against the AI
        static void Play(AdventureBookPageDisplay d, AdventureMissionDataModel mission)
        {
            var id = mission.ScenarioId;
            if (mission.MissionState.ToString() == "LOCKED")
            {
                AccessibilityMgr.OutputNotification(Str.Locked);
                return;
            }
            var config = AdventureConfig.Get();
            Log.Info("book: mission " + id);
            if (AdventureConfig.DoesMissionRequireDeck(id))
            {
                config.SetMission(id, true);
                var subscene = !GameUtils.DoesAdventureModeUseDungeonCrawlFormat(config.GetSelectedMode()) || config.IsHeroSelectedBeforeDungeonCrawlScreenForSelectedAdventure()
                    ? config.SubSceneForPickingHeroForCurrentAdventure() : Assets.AdventureData.Adventuresubscene.DUNGEON_CRAWL;
                config.ChangeSubScene(subscene, true);
            }
            else
            {
                config.SetMission(id, true);
                GameMgr.Get().FindGame(GameType.GT_VS_AI, FormatType.FT_WILD, (int)id, 0, 0L, null, null, false, null, null, 0L, GameType.GT_UNKNOWN, 0);
            }
        }
    }
}
