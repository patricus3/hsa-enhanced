using System;
using System.Collections;
using System.Collections.Generic;
using Accessibility;
using Hearthstone.UI;
using UnityEngine;

namespace HSAEnhanced
{
    // Any screen or popup Hearthstone Access does not know: a menu of every button the game
    // shows on it, read from the game objects, with a way back. It steps aside as soon as HSA
    // (or the Black Market screen) takes over.
    class FallbackWatcher : MonoBehaviour
    {
        static FallbackWatcher s_instance;
        static object s_focusedScreen;      // the screen HSA last gave focus to
        static float s_focusedAt;
        static bool s_spokeSinceFocus;

        internal static void ScreenFocused()
        {
            s_focusedScreen = Ref.Field(typeof(AccessibilityMgr), "s_curScreen")?.GetValue(null);
            s_focusedAt = Time.unscaledTime;
            s_spokeSinceFocus = false;
        }

        internal static void Spoke() { s_spokeSinceFocus = true; }

        // Got focus and said nothing (HSA's OnGainedFocus did not read anything)
        static bool Silent(object screen)
        {
            return screen != null && screen == s_focusedScreen && !s_spokeSinceFocus && Time.unscaledTime - s_focusedAt > 1.5f;
        }
        FallbackUI m_ui;
        string m_candidate;     // target seen on the previous check
        float m_candidateSince;
        bool m_claimed;         // the popup found has an HSA reader of its own
        string m_emptyKey;      // target that had no buttons, and until when not to look again
        float m_emptyUntil;
        float m_next;

        // coroutines of ours (FallbackWatcher is our MonoBehaviour that lives as long as HSA)
        internal static void Run(System.Collections.IEnumerator routine)
        {
            if (s_instance != null) s_instance.StartCoroutine(routine);
        }

        internal static void Ensure(GameObject host)
        {
            if (s_instance != null || host == null) return;
            s_instance = host.AddComponent<FallbackWatcher>();
        }

        // popups an HSA reader stood for, read here from their own buttons until they close
        class Popup { internal FallbackUI Ui; internal GameObject Root; internal object Replaced; }
        static readonly List<Popup> s_popups = new List<Popup>();

        // a menu of what `root` shows (its buttons, and with `texts` its texts too, in reading
        // order), in place of the HSA UI `replaced`
        internal static void ShowPopup(GameObject root, object replaced, bool texts = false)
        {
            var hsa = replaced as AccessibleUI;
            if (hsa != null) AccessibilityMgr.HideUI(hsa);
            var ui = new FallbackUI("popup:" + root.GetInstanceID(), root, true, texts);
            s_popups.Add(new Popup { Ui = ui, Root = root, Replaced = replaced });
            AccessibilityMgr.ShowUI(ui);
            ui.Start();
        }

        // The game's small button-list menus nobody made accessible (Options > Miscellaneous,
        // Privacy, ...): read from their own buttons and header text while shown
        static void TickButtonListMenus()
        {
            foreach (var m in UnityEngine.Object.FindObjectsByType<ButtonListMenu>(FindObjectsSortMode.None))
            {
                if (m == null || m is AccessibleUI || !m.IsShown() || !m.gameObject.activeInHierarchy) continue;
                if (IsOpen(m.gameObject)) continue;
                Log.Info("button list menu: " + m.GetType().Name + " read from its buttons");
                ShowPopup(m.gameObject, null);     // its header is the popup's title
            }
            // Options > Privacy > Privacy Settings: a panel of switches (chat, nearby friends, ...),
            // Done and the shop offers rules (its section headings are not options: nothing to press)
            var privacy = PrivacySettingsMenu.Get();
            if (privacy != null && privacy && privacy.IsShown() && privacy.gameObject.activeInHierarchy && !IsOpen(privacy.gameObject))
            {
                Log.Info("privacy settings: read from its switches and buttons");
                ShowPopup(privacy.gameObject, null);
            }
        }

        static bool IsOpen(GameObject root)
        {
            foreach (var p in s_popups) if (p.Root == root) return true;
            return false;
        }

        static void TickPopups()
        {
            for (int i = s_popups.Count - 1; i >= 0; i--)
            {
                var p = s_popups[i];
                var w = p.Root == null ? null : p.Root.GetComponent<Widget>();
                bool open = p.Root != null && p.Root.activeInHierarchy && (w == null || w.IsActive) && StillOpen(p.Replaced);
                if (open) { p.Ui.Refresh(false); continue; }
                s_popups.RemoveAt(i);
                AccessibilityMgr.HideUI(p.Ui);
            }
        }

        // HSA keeps its open popup reader in a static field of its class and clears it on close
        static bool StillOpen(object replaced)
        {
            if (replaced == null) return true;
            bool held = false;
            foreach (var f in replaced.GetType().GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (!f.FieldType.IsInstanceOfType(replaced)) continue;
                held = true;
                if (ReferenceEquals(f.GetValue(null), replaced)) return true;
            }
            return !held;
        }

        void Update()
        {
            MercBattle.Tick();      // every frame: the ability tray opens and closes quickly
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + 0.5f;
            try
            {
                TickPopups();
                TickButtonListMenus();
                CreditsWatcher.Ensure();
                Mercenaries.Tick();
                MercRewards.Tick();
                SetRotation.Tick();
                MenuAugment.Tick();
                Back.Tick();
                Check();
            }
            catch (Exception e) { Log.Error(e); }
        }

        void Check()
        {
            GameObject root; string key; bool popup;
            FindTarget(out root, out key, out popup);
            if (key == null) { m_candidate = null; Hide(); return; }

            // the same target on two checks in a row: HSA had its chance to take it
            if (key != m_candidate) { m_candidate = key; m_candidateSince = Time.unscaledTime; return; }
            // an adventure book page: HSA's page reader takes it about a second after the adventure
            // screen goes quiet; ours only if it has not after a few seconds (no menu swapped under the player)
            if (m_ui == null && !popup && Book.IsShown() && Time.unscaledTime - m_candidateSince < 3f) return;
            // a popup with an HSA reader that is not reading it: ours after a moment (its reader may
            // still start; then it takes the popup and ours goes away)
            if (m_ui == null && popup && m_claimed && Time.unscaledTime - m_candidateSince < 2f) return;

            if (m_ui != null && m_ui.Key == key) { m_ui.Refresh(false); return; }
            // this target had nothing to offer a moment ago: look again only every 2 seconds
            if (key == m_emptyKey && Time.unscaledTime < m_emptyUntil) return;
            Hide();
            m_ui = new FallbackUI(key, root, popup, popup);     // a popup's texts are read too
            if (popup) Log.Info("fallback: popup " + root.name + (m_claimed ? " (its HSA reader is not reading it)" : ""));
            if (!m_ui.HasButtons) { m_ui = null; m_emptyKey = key; m_emptyUntil = Time.unscaledTime + 2f; return; }   // nothing on screen to offer (loading)
            AccessibilityMgr.ShowUI(m_ui);
            m_ui.Start();
        }

        void Hide()
        {
            if (m_ui == null) return;
            var ui = m_ui;
            m_ui = null;
            AccessibilityMgr.HideUI(ui);
        }

        static readonly Type Mgr = typeof(AccessibilityMgr);

        // What needs a fallback menu now: an unhandled popup or dialog, or a screen HSA has
        // no screen for. null when HSA handles everything.
        void FindTarget(out GameObject root, out string key, out bool popup)
        {
            root = null; key = null; popup = false;
            if (!AccessibilityMgr.IsAccessibilityEnabled() || GameState.Get() != null) return;
            var scenes = SceneMgr.Get();
            if (scenes == null || scenes.IsTransitioning() || !scenes.IsSceneLoaded()) return;
            var mode = scenes.GetMode();
            if (mode == SceneMgr.Mode.STARTUP || mode == SceneMgr.Mode.GAMEPLAY || mode == SceneMgr.Mode.FATAL_ERROR) return;

            // HSA busy with something of its own: a UI (other than ours), a forced key, a notification
            var uis = Ref.Field(Mgr, "s_curUIs")?.GetValue(null) as IList;
            if (uis != null) foreach (var u in uis) if (u != m_ui) return;
            if (SetRotation.Active) return;
            if (Ref.Field(Mgr, "s_forcedKey")?.GetValue(null) != null) return;
            if (Ref.Field(Mgr, "s_curNotificationDismissButton")?.GetValue(null) as UnityEngine.Object) return;

            // topmost popup / dialog nobody reads: none made accessible, or its HSA reader is not
            // reading it (no HSA UI is up at this point: e.g. a reader that closed at once)
            var dialog = Ref.Get<DialogBase>(DialogManager.Get(), "m_currentDialog");
            if (dialog != null && dialog.gameObject.activeInHierarchy && !Handled(dialog.gameObject)) { root = dialog.gameObject; popup = true; }
            var ctx = UIContext.GetRoot();
            if (root == null && ctx != null && ctx.ShowingPopups())
            {
                var latest = ctx.GetLatestPopup();
                var go = latest == null ? null : latest.PopupInstance;
                if (go != null && go.activeInHierarchy && !Handled(go)) { root = go; popup = true; }
            }
            if (root != null) { key = "popup:" + root.GetInstanceID(); m_claimed = HasHsaReader(root); return; }

            // a screen with no HSA screen (or HSA's hub screen left over after leaving the hub)
            var screen = Ref.Field(Mgr, "s_curScreen")?.GetValue(null);
            if (BlackMarketWatcher.Active > 0 || LuckyDrawWatcher.Active > 0 || CreditsWatcher.Active > 0 || Mercenaries.Active > 0) return;
            // (the pack opening screen is quiet when a pack's reveal closes, and still handles itself)
            bool quiet = Silent(screen) && mode != SceneMgr.Mode.PACKOPENING;
            if (screen == null || (screen is AccessibleHub && mode != SceneMgr.Mode.HUB) || Inert(screen as AccessibleScreen) || quiet)
                key = "screen:" + mode + ":" + (screen == null ? "" : screen.GetType().Name);
        }

        // an HSA reader belongs to this popup (it is not reading it now; it may still start)
        static bool HasHsaReader(GameObject go)
        {
            foreach (var c in go.GetComponentsInParent<Component>(true)) if (c is AccessibleUI || c is AccessibleScreen) return true;
            foreach (var c in go.GetComponentsInChildren<Component>(true)) if (c is AccessibleUI || c is AccessibleScreen) return true;
            return false;
        }

        // An HSA screen that is set but handles nothing here: it has no help to give (e.g. the
        // adventure screen on sub-screens it does not know stays "loading" and ignores all keys)
        static bool Inert(AccessibleScreen screen)
        {
            if (screen == null) return false;
            try { return string.IsNullOrEmpty(screen.GetHelp()); }
            catch { return false; }
        }

        // Read already: the current HSA screen in or above it (its HSA UI readers are not up, or
        // FindTarget would have stopped before), our own readers, or our Black Market popup
        static bool Handled(GameObject go)
        {
            var screen = Ref.Field(Mgr, "s_curScreen")?.GetValue(null);
            foreach (var c in go.GetComponentsInParent<Component>(true))
                if (c is AccessibleScreen && ReferenceEquals(c, screen)) return true;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
                if ((c is AccessibleScreen && ReferenceEquals(c, screen)) || c is BlackMarketItemPopup) return true;
            if (Mercenaries.Handles(go)) return true;
            // pages our own screens read (the Black Market page, the lucky draw)
            if (BlackMarketWatcher.Active > 0 && go.GetComponentInChildren<BlackMarketMainPage>(true) != null) return true;
            if (LuckyDrawWatcher.Active > 0 && go.GetComponentInChildren<LuckyDrawWidget>(true) != null) return true;
            return false;
        }
    }

    class FallbackUI : AccessibleUI
    {
        internal readonly string Key;
        readonly GameObject m_root;     // popup/dialog, or null for the whole screen
        readonly bool m_popup;
        readonly bool m_texts;      // its texts are read as well (a panel of information)
        AccessibleMenu m_menu;

        internal FallbackUI(string key, GameObject root, bool popup, bool texts = false)
        {
            Key = key; m_root = root; m_popup = popup; m_texts = texts;
            var title = popup ? PopupTitle(root) : (Book.Title() ?? "");
            m_menu = new AccessibleMenu(this, title, GoBack);
            Refresh(true);
        }

        internal bool HasButtons { get { return m_menu.GetNumItems() > 1; } }   // more than Go back

        internal void Start() { m_menu.StartReading(); }

        internal void Refresh(bool immediate)
        {
            var buttons = Buttons();
            // nothing to click on a popup: a click on it usually continues
            if (!buttons.Exists(b => !(b.Target is UberText)) && m_popup && m_root != null)     // (its texts are no buttons)
            {
                var root = m_root;
                buttons.Add(new GameButton { Target = root.transform, Label = Str.Word("GLOBAL_CONTINUE"), Click = () => AccessibleInputMgr.Click(root) });   // no widget button on it: the virtual mouse
            }
            ExtraOptions.Of(m_menu).Update(m_menu, buttons, () => -1, immediate);
            if (MenuEdit.IndexOfText(m_menu, LocalizedText.SCREEN_GO_BACK) < 0)
                m_menu.AddOption(LocalizedText.SCREEN_GO_BACK, GoBack);
            else
            {
                // keep Back last
                var list = MenuEdit.List(m_menu);
                var i = MenuEdit.IndexOfText(m_menu, LocalizedText.SCREEN_GO_BACK);
                if (list != null && i != list.Count - 1) { var o = list[i]; list.RemoveAt(i); list.Add(o); }
            }
        }

        // what is shown, without Back / Cancel / Close buttons: Go back uses them if the game's
        // own back navigation does nothing
        List<GameButton> Buttons()
        {
            var found = m_root != null ? Ui.ClickablesUnder(m_root, null) : Ui.ScreenButtons();
            found.RemoveAll(b => Labels.IsBack(b.Label));
            // a switch says whether it is on, in HSA's words for its own options' checkboxes
            foreach (var b in found)
            {
                var box = b.Target as CheckBox;
                if (box == null) continue;
                var state = LocalizationUtils.Get(box.IsChecked() ? LocalizationKey.OPTIONS_MENU_CHECKBOX_CHECKED : LocalizationKey.OPTIONS_MENU_CHECKBOX_NOT_CHECKED);
                b.Label = LocalizationUtils.Format(LocalizationKey.OPTIONS_MENU_CHECKBOX_LABEL, b.Label) + " " + state;
            }
            if (m_texts && m_root != null)
            {
                var ui = this;
                var labels = new List<string>();
                foreach (var b in found) labels.Add(b.Label);
                var title = Ref.Get(m_menu, "m_menuName") as string;
                foreach (var t in Ui.TextsUnder(m_root))
                {
                    if (labels.Contains(t.Value) || (title != null && title.EndsWith(t.Value))) continue;
                    var text = t.Value;
                    labels.Add(text);
                    found.Add(new GameButton { Target = t.Key, Label = text, Click = () => AccessibilityMgr.Output(ui, text) });
                }
                Ui.SortByScreen(found);
            }
            // an adventure book page: its chapters or missions from the game's data come first
            var book = m_root == null ? Book.Buttons() : null;
            if (book != null && book.Count > 0)
            {
                var labels = new List<string>();
                foreach (var b in book) labels.Add(b.Label);
                foreach (var b in found) if (!Labels.SimilarToAny(labels, b.Label)) { labels.Add(b.Label); book.Add(b); }
                return book;
            }
            return found;
        }

        static bool Under(Transform t, GameObject root)
        {
            if (root == null) return false;
            for (; t != null; t = t.parent) if (t.gameObject == root) return true;
            return false;
        }

        static string PopupTitle(GameObject root)
        {
            // the first text on the popup that is not on a button, usually its header
            foreach (var ut in root.GetComponentsInChildren<UberText>(false))
            {
                if (ut.GetComponentInParent<PegUIElement>() != null || ut.GetComponentInParent<Clickable>() != null) continue;
                var s = Ui.ShownText(ut.Text);     // (a text holding its string key is looked up)
                if (s.Length > 0) return Str.Join(LocalizedText.UI_POPUP, s);
            }
            return LocalizedText.UI_POPUP;
        }

        void GoBack() { Back.Go(m_menu, m_root); }

        public void HandleAccessibleInput() { m_menu.HandleAccessibleInput(); }

        public string GetAccessibleHelp() { return m_menu.GetHelp(); }
    }
}
