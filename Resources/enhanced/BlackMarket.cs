using System;
using System.Collections.Generic;
using System.Text;
using Accessibility;
using Blizzard.T5.Services;
using Hearthstone.BlackMarket;
using Hearthstone.DataModels;
using Hearthstone.UI;
using PegasusUtil;
using UnityEngine;

namespace HSAEnhanced
{
    // Lives on the BlackMarketDisplay object: creates the accessible screen once the main
    // page is up, follows the item list and the item popup, and goes away with the scene.
    class BlackMarketWatcher : MonoBehaviour
    {
        internal static int Active;     // the fallback menu leaves the Black Market scene to us
        internal BlackMarketDisplay Display;

        void OnEnable() { Active++; }
        void OnDisable() { Active--; }

        AccessibleBlackMarket m_screen;
        float m_next;

        void Update()
        {
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 0.25f;
            try
            {
                if (m_screen == null)
                {
                    var page = Ref.Get<BlackMarketMainPage>(Display, "m_mainPage");
                    if (page == null || !Ref.Get<bool>(Display, "m_mainPageInitialized")) return;
                    m_screen = new AccessibleBlackMarket(page);
                    AccessibilityMgr.SetScreen(m_screen);
                }
                m_screen.Poll();
            }
            catch (Exception e) { Log.Error(e); enabled = false; }
        }

        void OnDestroy()
        {
            try { if (m_screen != null) m_screen.Close(); }
            catch (Exception e) { Log.Error(e); }
        }
    }

    class AccessibleBlackMarket : AccessibleScreen
    {
        readonly BlackMarketMainPage m_page;
        AccessibleMenu m_menu;
        string m_signature;
        int m_itemCount;
        AccessibleBlackMarketItem m_item;          // popup UI while open
        float m_expectPopupUntil;                   // popup detection only right after we opened one
        Widget.EventListenerDelegate m_pageListener;
        int m_polls;

        internal AccessibleBlackMarket(BlackMarketMainPage page)
        {
            m_page = page;
            var widget = Ref.Get<Widget>(page, "m_widget");
            if (widget != null)
            {
                m_pageListener = OnPageEvent;
                widget.RegisterEventListener(m_pageListener);
            }
            Rebuild();
        }

        internal BlackMarketMainPage Page { get { return m_page; } }

        internal BlackMarketDataModel Model { get { return Ref.Get<BlackMarketDataModel>(m_page, "m_blackMarketDataModel"); } }

        void OnPageEvent(string ev)
        {
            if (ev == "HIDE_ITEM_POPUP") CloseItem();
        }

        #region Menu
        void Rebuild()
        {
            var keep = m_menu == null ? 0 : MenuEdit.GetIndex(m_menu);
            var model = Model;
            var title = model != null && !string.IsNullOrEmpty(model.Name) ? Str.Clean(model.Name) : Title();
            m_menu = MenuEdit.Carry(m_menu, new AccessibleMenu(this, title, GoBack));
            m_itemCount = 0;

            if (model != null && model.Items != null)
                foreach (var it in model.Items)
                {
                    var item = it;
                    if (item == null) continue;
                    m_menu.AddOption(() => ItemLabel(item), () => OpenItem(item));
                    m_itemCount++;
                }

            if (model != null)
            {
                AddInfo(() => model.TimeRemainingText, "GLUE_BLACK_MARKET_EVENT_END_TITLE");
                AddInfo(() => model.GraceTimeRemainingText, "GLUE_BLACK_MARKET_STORE_CLOSE_TITLE");
                if (model.HasUpcomingPriceRefresh) AddInfo(() => model.NextPriceRefreshText, "GLUE_BLACK_MARKET_PRICE_REFRESH_TITLE");
                if (model.HasUpcomingItemRefresh) AddInfo(() => model.NextItemRefreshText, "GLUE_BLACK_MARKET_ITEM_REFRESH_TITLE");
                AddInfo(() => model.DailyEarnCapText, "GLUE_BLACK_MARKET_DAILY_EARN_CAP_TITLE");
                AddInfo(() => model.TotalPurchaseText, "GLUE_BLACK_MARKET_ITEM_NUM_PURCHASED");
                AddInfo(() => model.WalletStealingCurrencyPercentText, null);
                AddInfo(() => model.WarModeRewardCurrencyMultiplierText, null);
            }
            m_menu.AddOption(() => Balance().ToString(), () => Output(Balance().ToString()));

            // anything else the page shows (info, war mode, play, close, ...), found on screen
            foreach (var b in Ui.ClickablesUnder(m_page.gameObject, IsItemOrPopup))
            {
                var button = b;
                m_menu.AddOption(button.Label, () => { try { button.Click(); } catch (Exception e) { Log.Error(e); } });
            }
            m_menu.AddOption(Str.Back, GoBack);

            m_menu.SetIndex(Math.Min(keep, m_menu.GetNumItems() - 1));
            m_signature = Signature();
        }

        void AddInfo(Func<string> value, string titleKey)
        {
            if (string.IsNullOrEmpty(Str.Clean(value()))) return;
            AccessibleMenu.GetTextDelegate text = () =>
            {
                var v = Str.Clean(value());
                if (titleKey == null) return v;
                return Str.Join(Str.Game(titleKey), v);
            };
            m_menu.AddOption(text, () => Output(text()));
        }

        static string Title()
        {
            return Str.Word("GLUE_BLACK_MARKET_SUB_TITLE", "GLUE_TOOLTIP_BUTTON_BLACK_MARKET_HEADLINE");
        }

        internal static long Balance()
        {
            var cm = ServiceManager.Get<CurrencyManager>();
            return cm == null ? 0 : cm.GetBalance(CurrencyType.BMEC);
        }

        internal static string Price(float amount)
        {
            return Mathf.RoundToInt(amount).ToString();
        }

        internal static string ItemLabel(BlackMarketItemDataModel item)
        {
            var parts = new List<string> { Str.Clean(item.DisplayName) };
            if (item.IsLocked) parts.Add(Str.Locked);
            if (item.SinglePrice != null) parts.Add(Price(item.SinglePrice.Amount));
            if (item.HasNewPrice && !string.IsNullOrEmpty(item.PriceAdjustmentText)) parts.Add(Str.Clean(item.PriceAdjustmentText));
            parts.Add(Stock(item));
            if (item.HaggleStatus == BlackMarketItemEntry.HaggleStatus.HS_DECLINED) parts.Add(Str.Word("GLUE_BLACK_MARKET_ITEM_OFFER_DECLINED"));
            return Str.Join(parts.ToArray());
        }

        internal static string Stock(BlackMarketItemDataModel item)
        {
            if (item.ItemStock == BlackMarketEventManager.UnlimitedItemStock) return null;
            if (item.ItemStock <= 0) return Str.Word("GLUE_BLACK_MARKET_ITEM_SOLD_OUT");
            if (!string.IsNullOrEmpty(item.ItemStockText)) return Str.Clean(item.ItemStockText);
            return Str.Game("GLUE_BLACK_MARKET_ITEM_NUM_AVAILABLE", item.ItemStock) ?? item.ItemStock.ToString();
        }

        // What makes the menu different: items and their state, and the page's buttons
        string Signature()
        {
            var sb = new StringBuilder();
            var model = Model;
            if (model != null && model.Items != null)
                foreach (var i in model.Items)
                    if (i != null) sb.Append(i.ItemAssetId).Append(i.DisplayName).Append(i.IsLocked).Append(i.VisualState).Append(i.ItemStock).Append(i.HaggleStatus).Append('|');
            if (model != null) sb.Append(model.HasUpcomingPriceRefresh).Append(model.HasUpcomingItemRefresh).Append(string.IsNullOrEmpty(model.GraceTimeRemainingText));
            foreach (var b in Ui.ClickablesUnder(m_page.gameObject, IsItemOrPopup)) sb.Append(b.Label).Append('|');
            return sb.ToString();
        }
        #endregion

        #region Items and the item popup
        bool IsItemOrPopup(GameObject go)
        {
            if (go.GetComponent<BlackMarketItemPopup>() != null) return true;
            var w = go.GetComponent<WidgetTemplate>();
            return w != null && w.GetDataModel<BlackMarketItemDataModel>() != null;
        }

        void OpenItem(BlackMarketItemDataModel item)
        {
            if (item.IsLocked) { Output(Str.Locked); return; }
            var tile = FindTile(item);
            if (tile == null) { Output(Str.Unavailable); return; }
            m_expectPopupUntil = Time.unscaledTime + 5f;
            Ui.Press(tile);
        }

        // The tile showing this item: the smallest widget bound to its data model, and the
        // clickable on it
        Component FindTile(BlackMarketItemDataModel item)
        {
            foreach (var w in m_page.GetComponentsInChildren<WidgetTemplate>(false))
            {
                if (w.GetComponentInParent<BlackMarketItemPopup>() != null) continue;
                if (!ReferenceEquals(w.GetDataModel<BlackMarketItemDataModel>(), item)) continue;
                var click = w.GetComponentInChildren<Clickable>(false);
                if (click != null && Ui.IsShown(click)) return click;
                var peg = w.GetComponentInChildren<PegUIElement>(false);
                if (peg != null && Ui.IsShown(peg)) return peg;
            }
            return null;
        }

        BlackMarketItemPopup FindOpenPopup()
        {
            foreach (var p in UnityEngine.Object.FindObjectsByType<BlackMarketItemPopup>(FindObjectsSortMode.None))
            {
                if (p == null || !p.isActiveAndEnabled || Ref.Get<bool>(p, "m_isWaiting")) continue;
                var w = Ref.Get<Widget>(p, "m_widget");
                if (w == null || !w.IsActive || w.GetDataModel<BlackMarketItemDataModel>() == null) continue;
                return p;
            }
            return null;
        }

        internal void CloseItem()
        {
            if (m_item == null) return;
            var item = m_item;
            m_item = null;
            item.Detach();
            AccessibilityMgr.HideUI(item);
        }
        #endregion

        internal void Poll()
        {
            // item popup opened by our click
            if (m_item == null && Time.unscaledTime < m_expectPopupUntil)
            {
                var popup = FindOpenPopup();
                if (popup != null)
                {
                    m_expectPopupUntil = 0;
                    m_item = new AccessibleBlackMarketItem(this, popup);
                    AccessibilityMgr.ShowUI(m_item);
                    m_item.Start();
                }
            }
            else if (m_item != null && !m_item.StillOpen()) CloseItem();

            // item list or page buttons changed (items arrive after the page opens, prices refresh, ...);
            // checked once a second, walking the page is not free
            if (++m_polls % 4 != 0) return;
            var sig = Signature();
            if (sig == m_signature) return;
            var hadItems = m_itemCount > 0;
            Rebuild();
            if (!hadItems && m_itemCount > 0 && AccessibilityMgr.IsCurrentlyFocused(this)) m_menu.StartReading(false);
        }

        void GoBack()
        {
            if (!Ref.Call(m_page, "OnNavigateBack")) Ref.Call(m_page, "HandleEvent", "CODE_CLOSE");
        }

        internal void Close()
        {
            CloseItem();
            var widget = Ref.Get<Widget>(m_page, "m_widget");
            if (widget != null && m_pageListener != null) widget.RemoveEventListener(m_pageListener);
            if (AccessibilityMgr.IsCurrentlyFocused(this)) AccessibilityMgr.TransitioningScreens();
        }

        void Output(string text) { AccessibilityMgr.Output(this, text); }

        public void HandleInput() { if (m_menu != null) m_menu.HandleAccessibleInput(); }

        public string GetHelp() { return m_menu == null ? "" : m_menu.GetHelp(); }

        public void OnGainedFocus()
        {
            Rebuild();
            m_menu.StartReading();
        }
    }

    // The popup of one Black Market item: details, quantity, buy, haggle
    class AccessibleBlackMarketItem : AccessibleUI
    {
        readonly AccessibleBlackMarket m_market;
        readonly BlackMarketItemPopup m_popup;
        readonly Widget m_widget;
        readonly Widget.EventListenerDelegate m_listener;
        AccessibleMenu m_menu;
        bool m_dismissed;

        internal AccessibleBlackMarketItem(AccessibleBlackMarket market, BlackMarketItemPopup popup)
        {
            m_market = market;
            m_popup = popup;
            m_widget = Ref.Get<Widget>(popup, "m_widget");
            m_listener = ev => { if (ev == "CODE_DISMISS_FROM_FOREGROUND") m_dismissed = true; };
            if (m_widget != null) m_widget.RegisterEventListener(m_listener);
        }

        BlackMarketItemDataModel Item { get { return m_widget == null ? null : m_widget.GetDataModel<BlackMarketItemDataModel>(); } }

        internal bool StillOpen()
        {
            return !m_dismissed && m_popup != null && m_popup.isActiveAndEnabled && m_widget != null && m_widget.IsActive && Item != null;
        }

        internal void Detach()
        {
            if (m_widget != null) m_widget.RemoveEventListener(m_listener);
        }

        internal void Start()
        {
            Build();
            m_menu.StartReading();
        }

        void Build()
        {
            var item = Item;
            m_menu = new AccessibleMenu(this, item == null ? "" : Str.Clean(item.DisplayName), Close);
            if (item == null) return;

            var description = Str.Join(Str.Clean(item.Description), item.RewardList == null ? null : Str.Clean(item.RewardList.Description));
            if (description.Length > 0) m_menu.AddOption(description, () => Output(description));
            AccessibleMenu.GetTextDelegate price = () =>
            {
                var i = Item;
                return i == null || i.TotalPrice == null ? "" : Str.Join(Str.Word("GLUE_STORE_SUMMARY_PRICE_HEADLINE"), AccessibleBlackMarket.Price(i.TotalPrice.Amount));
            };
            m_menu.AddOption(price, () => Output(price()));
            var stock = AccessibleBlackMarket.Stock(item);
            if (!string.IsNullOrEmpty(stock)) m_menu.AddOption(stock, () => Output(stock));

            // quantity, when more than one can be bought
            if (item.ItemStock != 1)
            {
                AccessibleMenu.GetTextDelegate qty = () => Str.Join(Str.Word("GLUE_STORE_QUANTITY_HEADLINE"), (Item == null ? 0 : Item.PurchaseQuantity).ToString());
                m_menu.AddOption(qty, () => Output(qty()));
                // "+1" is the game's text for such a button; a bare "-" is silent in screen readers
                var plus = Str.Word("GLUE_BATTLEBASH_PURCHASE_PLUS1");
                m_menu.AddOption(plus.Length > 0 ? plus : "+1", () => Adjust("INCREASE_PURCHASE_QUANTITY", qty, price));
                m_menu.AddOption("-1", () => Adjust("DECREASE_PURCHASE_QUANTITY", qty, price));
            }

            if (item.ItemStock != 0 && !item.IsLocked)
                m_menu.AddOption(Str.Join(Str.Word("GLOBAL_BUTTON_BUY", "GLUE_STORE_BUY_TEXT"), AccessibleBlackMarket.Price(item.TotalPrice == null ? 0 : item.TotalPrice.Amount)),
                    () => Send("PRODUCTBUYBUTTON_CLICKED"));

            // haggling is offered during the grace period, once per item
            var mgr = BlackMarketEventManager.Get();
            // (the game's popup follows the server's InGracePeriod in its global data model; the local timing
            // may lag behind it)
            var global = mgr == null ? null : Ref.Get<Hearthstone.DataModels.BlackMarketGlobalDataModel>(mgr, "m_blackMarketGlobalDataModel");
            bool grace = mgr != null && (mgr.IsCurrentEventInGracePeriod || (global != null && global.InGracePeriod));
            if (grace && item.HaggleStatus == BlackMarketItemEntry.HaggleStatus.HS_NONE && item.ItemStock != 0)
                m_menu.AddOption(Str.Word("GLUE_BLACK_MARKET_HAGGLE_OFFER"), () => Send("BLACK_MARKET_HAGGLE_BUTTON_CLICKED"));

            m_menu.AddOption(Str.Back, Close);
            var labels = new List<string>();
            var list = MenuEdit.List(m_menu);
            if (list != null) foreach (var o in list) labels.Add(MenuEdit.TextOf(o));
            Log.Info("Black Market item: " + string.Join(" | ", labels.ToArray()) + (mgr == null ? "" : " (grace period " + grace + ", haggle " + item.HaggleStatus + ")"));
        }

        void Adjust(string ev, AccessibleMenu.GetTextDelegate qty, AccessibleMenu.GetTextDelegate price)
        {
            Send(ev);
            Output(Str.Join(qty(), price()));
        }

        // The popup's own handler, as if its button had been clicked
        void Send(string ev)
        {
            try { Ref.Call(m_popup, "HandleEvent", ev); }
            catch (Exception e) { Log.Error(e); }
        }

        void Close()
        {
            m_dismissed = true;
            Ref.Call(m_market.Page, "HideItemPopup");
            m_market.CloseItem();
        }

        void Output(string text) { AccessibilityMgr.Output(this, text); }

        public void HandleAccessibleInput() { if (m_menu != null) m_menu.HandleAccessibleInput(); }

        public string GetAccessibleHelp() { return m_menu == null ? "" : m_menu.GetHelp(); }
    }
}
