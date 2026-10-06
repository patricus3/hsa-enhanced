using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // A reward chest (Mercenaries, Tavern Brawl, a league promotion, quest rewards) and the reward
    // boxes it opens into, wherever the game shows them:
    //   the chest: its banner, Open (as a click on the chest does)
    //   the boxes: each box still closed (Enter opens it and says what was in it), what the opened
    //     ones gave, Next (bonus rewards) and Done once the game shows them
    static class RewardChest
    {
        static RewardChestUI s_ui;
        static bool s_claimed;

        internal static ChestRewardDisplay Chest()
        {
            foreach (var c in UnityEngine.Object.FindObjectsByType<ChestRewardDisplay>(FindObjectsSortMode.None))
                if (c != null && c.gameObject.activeInHierarchy && c.m_rewardChest != null && c.m_rewardChest.gameObject.activeInHierarchy) return c;
            return null;
        }

        internal static RewardBoxesDisplay Boxes()
        {
            foreach (var b in UnityEngine.Object.FindObjectsByType<RewardBoxesDisplay>(FindObjectsSortMode.None))
                if (b != null && b.gameObject.activeInHierarchy && !b.IsClosing) return b;
            return null;
        }

        // a single reward on show (a pack, gold, a card, a card back...): it waits for a click
        internal static Reward Single()
        {
            foreach (var r in UnityEngine.Object.FindObjectsByType<Reward>(FindObjectsSortMode.None))
                if (r != null && r.gameObject.activeInHierarchy && r.IsShown) return r;
            return null;
        }

        internal static bool Shown { get { return Boxes() != null || Single() != null || Chest() != null; } }

        internal static void Tick()
        {
            if (!s_claimed)
            {
                s_claimed = true;
                Generic.Claims.Add(go => Shown && (go.GetComponentInChildren<ChestRewardDisplay>(true) != null || go.GetComponentInParent<ChestRewardDisplay>() != null
                    || go.GetComponentInChildren<RewardBoxesDisplay>(true) != null || go.GetComponentInParent<RewardBoxesDisplay>() != null
                    || go.GetComponentInChildren<Reward>(true) != null || go.GetComponentInParent<Reward>() != null));
            }
            bool shown = Shown;
            if (s_ui != null && !shown) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && shown)
            {
                var ui = new RewardChestUI();
                if (ui.Refresh(true)) { s_ui = ui; Focus.Push(ui); ui.Read(); }
            }
            if (s_ui != null) s_ui.Refresh(false);
        }
    }

    class RewardChestUI : Core.Screen
    {
        Menu m_menu;
        string m_key, m_signature;
        float m_next;
        bool m_opening;

        internal override bool Alive { get { return RewardChest.Shown; } }

        static string A(string key, params object[] args) { return Speech.S("ACCESSIBILITY_" + key, args); }

        static string Describe(RewardData r)
        {
            try { return Str.Join(RewardText.Describe(r).ToArray()); } catch { return ""; }
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
            var menu = new Menu(this, title, null);
            foreach (var b in items) { var click = b.Click; menu.AddOption(b.Label, () => click()); }
            menu.Index = Math.Max(0, Math.Min(at, items.Count - 1));
            m_menu = menu;
            m_signature = sig;
            m_key = key;
            if (newStep)
            {
                Log.Info("rewards: " + key + " '" + title + "': " + GameButton.Describe(items));
                if (Focused) m_menu.StartReading();
            }
            return true;
        }

        bool Build(out string key, out string title, List<GameButton> items)
        {
            key = null; title = "";
            var boxes = RewardChest.Boxes();
            if (boxes != null) return BoxItems(boxes, ref key, ref title, items);
            var single = RewardChest.Single();
            if (single != null) return SingleItems(single, ref key, ref title, items);
            var chest = RewardChest.Chest();
            if (chest == null) return false;
            key = "chest";
            title = chest.m_bannerUberText != null && chest.m_bannerUberText.gameObject.activeInHierarchy ? Str.Clean(Ui.ShownText(chest.m_bannerUberText.Text)) : "";
            if (string.IsNullOrEmpty(title)) title = A("UI_REWARD_TYPE_REWARD_PACKAGE");
            var desc = chest.m_descText == null || !chest.m_descText.activeInHierarchy ? null : chest.m_descText.GetComponent<UberText>();
            if (desc != null) { var t = Str.Clean(Ui.ShownText(desc.Text)); if (!string.IsNullOrEmpty(t)) items.Add(new GameButton { Target = chest, Label = t, Click = () => Say(t) }); }
            var c = chest;
            items.Add(new GameButton { Target = chest.m_rewardChest, Label = Str.Word("GLUE_LOADINGSCREEN_OPEN_APP_STORE"), Click = () =>
            {
                if (m_opening) return;
                m_opening = true;
                Log.Info("rewards: open the chest");
                c.m_rewardChest.TriggerRelease();
            } });
            return true;
        }

        bool BoxItems(RewardBoxesDisplay boxes, ref string key, ref string title, List<GameButton> items)
        {
            m_opening = false;
            var rewards = Ref.Get<List<RewardData>>(boxes, "m_rewards") ?? new List<RewardData>();
            var packages = new List<RewardPackage>();
            foreach (var p in boxes.GetComponentsInChildren<RewardPackage>())
                if (p != null && p.gameObject.activeInHierarchy) packages.Add(p);
            int page = Ref.Get<int>(boxes, "m_currentPageNum");
            key = "boxes:" + page;
            title = A("UI_REWARD_TYPE_REWARD_PACKAGE");
            var closed = new HashSet<int>();
            for (int i = 0; i < packages.Count; i++)
            {
                var p = packages[i];
                closed.Add(p.m_RewardIndex);
                int n = i + 1;
                items.Add(new GameButton { Target = p, Label = Str.Join(A("UI_REWARD_TYPE_REWARD_PACKAGE"), n.ToString()), Click = () =>
                {
                    Log.Info("rewards: open box " + p.m_RewardIndex);
                    p.TriggerRelease();
                    var r = p.m_RewardIndex >= 0 && p.m_RewardIndex < rewards.Count ? rewards[p.m_RewardIndex] : null;
                    if (r != null) Say(Describe(r));
                } });
            }
            // what the opened boxes gave
            for (int i = 0; i < rewards.Count; i++)
            {
                if (closed.Contains(i) || rewards[i] == null) continue;
                var text = Describe(rewards[i]);
                if (!string.IsNullOrEmpty(text)) items.Add(new GameButton { Target = boxes, Label = text, Click = () => Say(text) });
            }
            if (Ref.Get<bool>(boxes, "m_doneButtonFinishedShown"))
            {
                foreach (var b in new[] { boxes.m_BonusLootNextButton, boxes.m_DoneButton })
                {
                    if (b == null || !b.gameObject.activeInHierarchy) continue;
                    var button = b;
                    var label = Str.Clean(Ui.LabelOf(button));
                    if (string.IsNullOrEmpty(label)) label = Str.Word(button == boxes.m_DoneButton ? "GLOBAL_DONE" : "GLOBAL_BUTTON_NEXT");
                    items.Add(new GameButton { Target = button, Label = label, Click = () => { Log.Info("rewards: " + label); button.TriggerRelease(); } });
                }
            }
            return true;
        }

        // one reward: its banner (headline, details, where it came from), what it is, Continue
        bool SingleItems(Reward reward, ref string key, ref string title, List<GameButton> items)
        {
            key = "reward:" + reward.GetInstanceID();
            var banner = reward.m_rewardBanner;
            string head = null, details = null, source = null;
            if (banner != null && banner.gameObject.activeInHierarchy)
            {
                head = Str.Clean(Ui.ShownText(banner.HeadlineText));
                details = Str.Clean(Ui.ShownText(banner.DetailsText));
                if (banner.m_sourceText != null && banner.m_sourceText.gameObject.activeInHierarchy) source = Str.Clean(Ui.ShownText(banner.SourceText));
            }
            var what = reward.Data == null ? null : Describe(reward.Data);
            title = !string.IsNullOrEmpty(head) ? head : what ?? "";
            foreach (var t in new[] { what != title ? what : null, details, source })
            {
                if (string.IsNullOrEmpty(t)) continue;
                var text = t;
                items.Add(new GameButton { Target = reward, Label = text, Click = () => Say(text) });
            }
            var r = reward;
            items.Add(new GameButton { Target = reward, Label = Str.Word("GLOBAL_CONTINUE"), Click = () =>
            {
                Log.Info("rewards: continue past " + r.name);
                if (r.m_clickCatcher != null && r.m_clickCatcher.gameObject.activeInHierarchy) r.m_clickCatcher.TriggerRelease();
                else Core.Click.Mouse(r.gameObject);
            } });
            return true;
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
