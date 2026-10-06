using System;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Launching the game: the splash screen's own loading texts as they change (each startup stage,
    // and its progress at every quarter), the login queue's texts, and the loading popup's title with
    // "Please wait" when a match or mode loads.
    static class Launch
    {
        static string s_text, s_queue;
        static int s_quarter = -1;
        static Component s_popup;

        static void Say(string text)
        {
            text = Str.Clean(text);
            if (string.IsNullOrEmpty(text)) return;
            Log.Info("launch: " + text);
            Speech.Say(text);
        }

        static string Shown(UberText t)
        {
            return t != null && t.gameObject.activeInHierarchy && !t.isHidden() ? Ui.ShownText(t.Text) : null;
        }

        internal static void Tick()
        {
            var splash = SplashScreen.Get();
            if (splash != null && splash.gameObject.activeInHierarchy)
            {
                // the stage text ("Loading cards"...)
                var text = Shown(splash.m_loadingText);
                if (!string.IsNullOrEmpty(text) && text != s_text) { s_text = text; s_quarter = -1; Say(text); }
                // its progress, every quarter
                if (splash.m_loadingBar != null && splash.m_loadingBar.activeInHierarchy && splash.m_loadingProgress != null)
                {
                    float p = 0;
                    try { p = Ref.Get<float>(splash.m_loadingProgress, "m_progress"); } catch { }
                    int quarter = Mathf.FloorToInt(Mathf.Clamp01(p) * 4f);
                    if (quarter > s_quarter && quarter > 0) { s_quarter = quarter; Say((quarter * 25) + "%"); }
                }
                // the login queue
                var queue = Str.Join(Shown(splash.m_queueTitle), Shown(splash.m_queueText));
                if (!string.IsNullOrEmpty(queue) && queue != s_queue) { s_queue = queue; Say(Str.Join(queue, Shown(splash.m_queueTime))); }
            }

            // the loading popup (a match or mode on its way)
            var popup = UnityEngine.Object.FindObjectOfType<LoadingPopupDisplay>();
            bool shown = popup != null && popup.gameObject.activeInHierarchy && popup.m_title != null && popup.m_title.gameObject.activeInHierarchy;
            if (shown && s_popup != popup)
            {
                s_popup = popup;
                Say(Str.Join(Ui.ShownText(popup.m_title.Text), Speech.S("ACCESSIBILITY_GLOBAL_PLEASE_WAIT")));
            }
            else if (!shown) s_popup = null;
        }
    }
}
