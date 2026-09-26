using System;
using System.Collections.Generic;
using System.Text;
using Accessibility;
using Hearthstone.DataModels;
using Hearthstone.UI;
using UnityEngine;

namespace HSAEnhanced
{
    // Lives on the LuckyDrawDisplay object (the Darkmoon Faire Treasures and other lucky draws):
    // sets the accessible screen once the draw's widget is up and goes away with the scene.
    class LuckyDrawWatcher : MonoBehaviour
    {
        internal static int Active;     // the fallback menu leaves the lucky draw scene to us
        internal LuckyDrawDisplay Display;
        AccessibleLuckyDraw m_screen;
        float m_next;

        void OnEnable() { Active++; }
        void OnDisable() { Active--; }

        void Update()
        {
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 0.5f;
            try
            {
                if (m_screen == null)
                {
                    var widget = Ref.Get<LuckyDrawWidget>(Display, "m_luckyDrawWidget");
                    if (widget == null || !widget.isActiveAndEnabled || AccessibleLuckyDraw.Model(widget) == null) return;
                    m_screen = new AccessibleLuckyDraw(widget);
                    AccessibilityMgr.SetScreen(m_screen);
                }
                m_screen.Poll();
            }
            catch (Exception e) { Log.Error(e); enabled = false; }
        }

        void OnDestroy()
        {
            if (m_screen != null && AccessibilityMgr.IsCurrentlyFocused(m_screen)) AccessibilityMgr.TransitioningScreens();
        }
    }

    // The draw from its data model: what it is, time left, the draw with its price, the rewards
    class AccessibleLuckyDraw : AccessibleScreen
    {
        readonly LuckyDrawWidget m_widget;
        AccessibleMenu m_menu;
        string m_signature;

        internal AccessibleLuckyDraw(LuckyDrawWidget widget) { m_widget = widget; Build(); }

        internal static LuckyDrawDataModel Model(LuckyDrawWidget w)
        {
            var widget = Ref.Get<Widget>(w, "m_widget");
            return widget == null ? null : widget.GetDataModel<LuckyDrawDataModel>();
        }

        void Build()
        {
            var keep = m_menu == null ? 0 : MenuEdit.GetIndex(m_menu);
            var dm = Model(m_widget);
            m_menu = new AccessibleMenu(this, dm == null ? "" : Str.Clean(dm.Name), Close);
            if (dm != null)
            {
                if (dm.IsClosed) m_menu.AddOption(Str.Clean(dm.ClosedReason), () => Output(Str.Clean(dm.ClosedReason)));
                else if (!dm.IsAllRewardsOwned)
                    m_menu.AddOption(() => Str.T("ACCESSIBILITY_ENH_LD_DRAW", "Draw for {0}", PriceText(dm)), Draw);
                AccessibleMenu.GetTextDelegate owned = () =>
                {
                    var m = Model(m_widget);
                    return m == null ? "" : Str.T("ACCESSIBILITY_ENH_LD_OWNED_COUNT", "{0} of {1} rewards owned", m.OwnedRewardCount, m.Rewards == null ? 0 : m.Rewards.Count);
                };
                m_menu.AddOption(owned, () => Output(owned()));
                if (!string.IsNullOrEmpty(Str.Clean(dm.TimeLeft)))
                {
                    AccessibleMenu.GetTextDelegate time = () => { var m = Model(m_widget); return m == null ? "" : Str.Clean(m.TimeLeft); };
                    m_menu.AddOption(time, () => Output(time()));
                }
                if (dm.Rewards != null)
                    foreach (var r in dm.Rewards)
                    {
                        var reward = r;
                        if (reward == null) continue;
                        var label = Str.Join(Str.Clean(reward.Name), reward.IsPurchased ? Str.T("ACCESSIBILITY_ENH_OWNED", "owned") : null);
                        var details = Str.Join(Str.Clean(reward.Name), Str.Clean(reward.Description),
                                               reward.RewardList == null ? null : Str.Clean(reward.RewardList.Description));
                        m_menu.AddOption(label, () => Output(details));
                    }
            }
            m_menu.AddOption(Str.Game("GLOBAL_BACK") ?? "Back", Close);
            m_menu.SetIndex(Math.Min(keep, m_menu.GetNumItems() - 1));
            m_signature = Signature();
        }

        static string PriceText(LuckyDrawDataModel dm)
        {
            var product = dm.NextPullProduct;
            if (product != null && product.Prices != null)
                foreach (var price in product.Prices)
                {
                    if (price == null) continue;
                    var s = "";
                    try { s = AccessibleShopUtils.GetBuyText(price); } catch { }
                    if (!string.IsNullOrEmpty(s)) return s;
                    if (!string.IsNullOrEmpty(price.DisplayText)) return Str.Clean(price.DisplayText) + " " + price.Currency;
                }
            return dm.DrawPrice.ToString();
        }

        // what the draw's own button does: the game's purchase flow (with its confirmation)
        void Draw()
        {
            Log.Info("lucky draw: draw");
            Ref.Call(m_widget, "HandleEvent", "LUCKY_DRAW_BUY_PULL");
        }

        void Close()
        {
            if (!Navigation.GoBack()) Ref.Call(m_widget, "Close");
        }

        string Signature()
        {
            var dm = Model(m_widget);
            if (dm == null) return "";
            var sb = new StringBuilder();
            sb.Append(dm.IsClosed).Append(dm.IsAllRewardsOwned).Append(dm.OwnedRewardCount).Append('|');
            if (dm.Rewards != null) foreach (var r in dm.Rewards) if (r != null) sb.Append(r.Name).Append(r.IsPurchased).Append('|');
            return sb.ToString();
        }

        internal void Poll()
        {
            if (Signature() == m_signature) return;
            Build();
        }

        void Output(string text) { AccessibilityMgr.Output(this, text); }

        public void HandleInput() { if (m_menu != null) m_menu.HandleAccessibleInput(); }

        public string GetHelp() { return m_menu == null ? "" : m_menu.GetHelp(); }

        public void OnGainedFocus()
        {
            Build();
            m_menu.StartReading();
        }
    }
}
