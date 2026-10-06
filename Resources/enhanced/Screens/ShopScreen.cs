using System;
using System.Collections.Generic;
using Hearthstone.DataModels;
using Hearthstone.UI;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // The shop, from the game's shop data (not its tiles on screen):
    //   your gold and runestones, then the tabs (locked ones say so); a tab: its sub tabs, then its
    //     products with their prices and whether you have them; Enter opens a product's page
    //   a product's page: name, what it says, what is in it, its versions (Enter chooses one), the
    //     quantity where the game offers one, and one option per price. Gold / runestones: not
    //     enough is said before anything is sent; else the game's own "Are you sure?" follows.
    //     Real money (the game hands it to Blizzard's payment window): Enter twice
    //   the purchase box (working on it / complete / failed): read, OK closes it
    static class ShopScreen
    {
        static ShopUI s_ui;

        internal static Shop Open()
        {
            var shop = Shop.Get();
            return shop != null && shop && shop.IsOpen() ? shop : null;
        }

        // every frame
        internal static void Tick()
        {
            var shop = Open();
            if (s_ui != null && shop == null) { var old = s_ui; s_ui = null; Focus.Pop(old); }
            if (s_ui == null && shop != null && shop.IsBrowserReady)
            {
                var ui = new ShopUI();
                if (!ui.Refresh(true)) return;
                s_ui = ui;
                Focus.Push(ui);
                ui.Read();
            }
            if (s_ui != null) s_ui.Refresh(false);
        }

        internal static bool Active { get { return s_ui != null; } }
    }

    class ShopUI : Core.Screen
    {
        Menu m_menu;
        string m_key, m_signature;
        float m_next;
        int m_tab = -1, m_sub = -1;          // the tab / sub tab being looked at; -1: the list of them
        int m_quantity = 1;
        object m_armed; float m_armedUntil;  // a real-money price pressed once

        internal override bool Alive { get { return ShopScreen.Open() != null; } }

        static ShopDataModel Data()
        {
            IDataModel model;
            var ctx = GlobalDataContext.Get();
            return ctx != null && ctx.GetDataModel(24, out model) ? model as ShopDataModel : null;
        }

        static ProductPage Page()
        {
            var shop = ShopScreen.Open();
            var pages = shop == null ? null : shop.ProductPageController;
            if (pages == null || !pages.IsOpen || pages.IsOpening) return null;
            var page = pages.CurrentProductPage;
            return page != null && page && page.IsOpen ? page : null;
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
                Log.Info("shop: " + key + " '" + title + "': " + GameButton.Describe(items));
                if (Focused) m_menu.StartReading();
            }
            return true;
        }

        bool Build(out string key, out string title, List<GameButton> items)
        {
            key = null; title = "";
            var shop = ShopScreen.Open();
            if (shop == null) return false;
            var auth = UnityEngine.Object.FindObjectOfType<StorePurchaseAuth>();
            if (auth != null && auth.gameObject.activeInHierarchy && auth.IsShown()) return Auth(auth, ref key, ref title, items);
            bool ok = Level(ref key, ref title, items);
            // what you have, first on every page of the shop (the game shows it in the shop's corner)
            if (ok)
            {
                var have = Balances();
                items.Insert(0, new GameButton { Target = shop, Label = have, Click = () => Speech.Say(have) });
            }
            return ok;
        }

        internal static string Balances()
        {
            return Str.Join(Str.Word("GLUE_SHOP_GOLD"), Balance(CurrencyType.GOLD).ToString(), Str.Word("GLUE_SHOP_RUNESTONES"), Balance(Runestones).ToString());
        }

        bool Level(ref string key, ref string title, List<GameButton> items)
        {
            var page = Page();
            if (page != null) return ProductItems(page, ref key, ref title, items);
            var data = Data();
            if (data == null || data.Pages == null) return false;
            if (m_tab < 0 || m_tab >= data.Pages.Count) return Tabs(data, ref key, ref title, items);
            var tab = data.Pages[m_tab];
            if (tab.ShopSubPages == null || tab.ShopSubPages.Count == 0) { m_tab = -1; return Tabs(data, ref key, ref title, items); }
            if (m_sub < 0 && tab.ShopSubPages.Count > 1) return SubTabs(tab, ref key, ref title, items);
            var sub = tab.ShopSubPages[Math.Max(0, Math.Min(m_sub, tab.ShopSubPages.Count - 1))];
            return Products(tab, sub, ref key, ref title, items);
        }

        static string TabName(ShopTabDataModel t) { return t == null ? "" : Str.Clean(GameStrings.Get(t.Name)); }

        static long Balance(CurrencyType c)
        {
            try { return Blizzard.T5.Services.ServiceManager.Get<CurrencyManager>().GetBalance(c); } catch { return 0; }
        }

        static CurrencyType Runestones
        {
            get { CurrencyType t; return ShopUtils.TryGetMainVirtualCurrencyType(out t) ? t : CurrencyType.ROW_RUNESTONES; }
        }

        // ---- the tabs -------------------------------------------------------------------------

        bool Tabs(ShopDataModel data, ref string key, ref string title, List<GameButton> items)
        {
            key = "tabs";
            title = Str.Word("GLUE_STORE_HEADLINE");
            for (int i = 0; i < data.Pages.Count; i++)
            {
                var t = data.Pages[i] == null ? null : data.Pages[i].Tab;
                if (t == null) continue;
                int index = i;
                var label = Str.Join(TabName(t), t.Locked ? Str.Word("GLUE_STORE_UNAVAILABLE_REASON_TITLE_LOCKED") : null);
                items.Add(new GameButton { Target = ShopScreen.Open(), Label = label, Click = () =>
                {
                    if (t.Locked) { Speech.Say(label); return; }
                    m_tab = index; m_sub = -1; m_next = 0;
                } });
            }
            return true;
        }

        bool SubTabs(ShopPageDataModel tab, ref string key, ref string title, List<GameButton> items)
        {
            key = "subtabs:" + m_tab;
            title = TabName(tab.Tab);
            for (int i = 0; i < tab.ShopSubPages.Count; i++)
            {
                var t = tab.ShopSubPages[i] == null ? null : tab.ShopSubPages[i].Tab;
                if (t == null) continue;
                int index = i;
                var label = Str.Join(TabName(t), t.Locked ? Str.Word("GLUE_STORE_UNAVAILABLE_REASON_TITLE_LOCKED") : null);
                items.Add(new GameButton { Target = ShopScreen.Open(), Label = label, Click = () =>
                {
                    if (t.Locked) { Speech.Say(label); return; }
                    m_sub = index; m_next = 0;
                } });
            }
            return true;
        }

        // ---- the products of a tab --------------------------------------------------------------

        bool Products(ShopPageDataModel tab, ShopSubPageDataModel sub, ref string key, ref string title, List<GameButton> items)
        {
            key = "products:" + m_tab + ":" + m_sub;
            title = Str.Join(TabName(tab.Tab), tab.ShopSubPages.Count > 1 ? TabName(sub.Tab) : null);
            if (sub.Sections == null) return true;
            foreach (var section in sub.Sections)
            {
                if (section == null || section.BrowserButtons == null || section.IsChainOfferSection) continue;
                foreach (var button in section.BrowserButtons)
                {
                    if (button == null || !ShopUtils.ShouldDisplayButton(button)) continue;
                    var product = button.DisplayProduct;
                    if (product == null || product.IsEmpty()) continue;
                    IGamemodeAvailabilityService.Status status;
                    if (!ShopUtils.IsProductGamemodesAvailable(product, out status)) continue;
                    var p = product;
                    var label = Str.Join(Str.Clean(product.Name), Prices(product), Owned(product));
                    items.Add(new GameButton { Target = ShopScreen.Open(), Label = label, Click = () =>
                    {
                        Log.Info("shop: open " + p.Name);
                        m_quantity = 1;
                        ShopScreen.Open().ProductPageController.OpenProductPage(p);
                    } });
                }
            }
            return true;
        }

        static string Owned(ProductDataModel p)
        {
            return p.Availability == ProductAvailability.ALREADY_OWNED ? Str.Word("GLUE_STORE_HEROES_PURCHASED") : null;
        }

        static string PriceText(PriceDataModel price)
        {
            if (price == null) return "";
            switch (price.Currency)
            {
                case CurrencyType.GOLD: return Str.Join(price.DisplayText, Str.Word("GLUE_SHOP_GOLD"));
                case CurrencyType.ROW_RUNESTONES:
                case CurrencyType.CN_RUNESTONES: return Str.Join(price.DisplayText, Str.Word("GLUE_SHOP_RUNESTONES"));
                default: return Str.Clean(price.DisplayText);
            }
        }

        static string Prices(ProductDataModel p)
        {
            if (p.Prices == null) return null;
            var parts = new List<string>();
            foreach (var price in p.Prices) if (price != null) parts.Add(PriceText(price));
            return Str.Join(parts.ToArray());
        }

        // ---- a product's page -------------------------------------------------------------------

        bool ProductItems(ProductPage page, ref string key, ref string title, List<GameButton> items)
        {
            var product = page.Product;
            if (product == null) return false;
            var variant = page.GetSelectedVariant() ?? product;
            key = "page:" + product.PmtId;
            title = Str.Clean(product.Name);
            var shop = ShopScreen.Open();
            // what it says and what is in it
            foreach (var text in new[] { product.DescriptionHeader, variant.Description ?? product.Description, product.FlavorText })
            {
                if (string.IsNullOrEmpty(text)) continue;
                foreach (var line in text.Split('•'))
                {
                    var said = Str.Clean(line);
                    if (said.Length > 0) items.Add(new GameButton { Target = shop, Label = said, Click = () => Speech.Say(said) });
                }
            }
            // its versions (pack counts, deck classes ...)
            if (product.Variants != null && product.Variants.Count > 1)
            {
                int selected = page.GetSelectedVariantIndex();
                for (int i = 0; i < product.Variants.Count; i++)
                {
                    var v = product.Variants[i];
                    if (v == null) continue;
                    int index = i;
                    var label = Str.Join(Str.Clean(string.IsNullOrEmpty(v.VariantName) ? v.Name : v.VariantName), Prices(v), Owned(v), i == selected ? Pets.Checked(true) : null);
                    items.Add(new GameButton { Target = shop, Label = label, Click = () => { page.SelectVariantByIndex(index); m_quantity = 1; } });
                }
            }
            // the quantity, where the game offers one
            if (variant.ProductSupportsQuantitySelect())
            {
                int max = Math.Max(1, page.Selection == null ? 1 : page.Selection.MaxQuantity);
                m_quantity = Math.Max(1, Math.Min(m_quantity, max));
                var head = Str.Word("GLUE_STORE_QUANTITY_HEADLINE");
                foreach (var step in new[] { 1, 10, -1, -10 })
                {
                    int d = step;
                    var label = Str.Join(head, (d > 0 ? "+" : "") + d, m_quantity + "/" + max);
                    items.Add(new GameButton { Target = shop, Label = label, Click = () =>
                    {
                        m_quantity = Math.Max(1, Math.Min(m_quantity + d, max));
                        page.SetVariantQuantityAndUpdateDataModel(page.GetSelectedVariant(), m_quantity);
                        Speech.Say(m_quantity.ToString());
                    } });
                }
            }
            // one option per price
            if (variant.Availability != ProductAvailability.ALREADY_OWNED && variant.Prices != null)
                for (int i = 0; i < variant.Prices.Count; i++)
                {
                    var price = variant.Prices[i];
                    if (price == null) continue;
                    int index = i;
                    var label = Str.Join(PriceText(price), Short(price));
                    items.Add(new GameButton { Target = shop, Label = label, Click = () => Buy(page, price, index) });
                }
            else if (variant.Availability == ProductAvailability.ALREADY_OWNED)
                items.Add(new GameButton { Target = shop, Label = Str.Word("GLUE_STORE_HEROES_PURCHASED"), Click = () => Speech.Say(Str.Word("GLUE_STORE_HEROES_PURCHASED")) });
            return true;
        }

        // not enough of a virtual currency, in the game's words
        static string Short(PriceDataModel price)
        {
            if (price.Currency == CurrencyType.GOLD && Balance(CurrencyType.GOLD) < price.Amount) return Str.Word("GLUE_STORE_FAIL_NOT_ENOUGH_GOLD");
            if ((price.Currency == CurrencyType.ROW_RUNESTONES || price.Currency == CurrencyType.CN_RUNESTONES) && Balance(price.Currency) < price.Amount)
                return Str.Word("GLUE_STORE_FAIL_NOT_ENOUGH_RUNESTONES");
            return null;
        }

        void Buy(ProductPage page, PriceDataModel price, int index)
        {
            var shortOf = Short(price);
            if (price.Currency == CurrencyType.GOLD && shortOf != null) { Speech.Say(shortOf); return; }   // the game would do nothing
            if (price.Currency == CurrencyType.REAL_MONEY)
            {
                // real money: a second Enter within a few seconds
                if (m_armed != (object)price || Time.unscaledTime > m_armedUntil)
                {
                    m_armed = price; m_armedUntil = Time.unscaledTime + 6f;
                    Speech.Say(Str.Join(Str.Clean(price.DisplayText), Str.Word("GLOBAL_CONFIRM"), Keys.Enter.Name));
                    return;
                }
                m_armed = null;
            }
            Log.Info("shop: buy " + (page.Product == null ? "?" : page.Product.Name) + " for " + price.DisplayText + " " + price.Currency + " x" + m_quantity);
            Ref.Call(page, "TryBuy", index);
        }

        // ---- the purchase box -------------------------------------------------------------------

        bool Auth(StorePurchaseAuth auth, ref string key, ref string title, List<GameButton> items)
        {
            var texts = new List<string>();
            foreach (var ut in new[] { auth.m_waitingForAuthText, auth.m_successHeadlineText, auth.m_failHeadlineText, auth.m_failDetailsText })
                if (ut != null && ut.gameObject.activeInHierarchy) { var s = Ui.ShownText(ut.Text); if (s.Length > 0) texts.Add(s); }
            key = "auth:" + string.Join("|", texts.ToArray());
            title = Str.Join(texts.ToArray());
            var ok = auth.m_okButton;
            if (ok != null && ok.gameObject.activeInHierarchy && ok.IsEnabled())
                items.Add(new GameButton { Target = ok, Label = Str.Word("GLOBAL_OK"), Click = () => Core.Click.Peg(ok) });
            else
            {
                var said = title;
                items.Add(new GameButton { Target = auth, Label = said, Click = () => Speech.Say(said) });
            }
            return true;
        }

        // ---- back -------------------------------------------------------------------------------

        void Back()
        {
            if (Page() != null) { Log.Info("shop: close the page"); Navigation.GoBack(); return; }
            var data = Data();
            if (m_tab >= 0)
            {
                bool hasSubs = data != null && m_tab < data.Pages.Count && data.Pages[m_tab].ShopSubPages != null && data.Pages[m_tab].ShopSubPages.Count > 1;
                if (m_sub >= 0 && hasSubs) m_sub = -1; else { m_tab = -1; m_sub = -1; }
                m_next = 0;
                return;
            }
            Log.Info("shop: close");
            Navigation.GoBack();
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
