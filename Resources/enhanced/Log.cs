using System;

namespace HSAEnhanced
{
    // The mod's log lines: in Hearthstone Access's Accessibility.log when it is there, else in the
    // game's Player.log
    static class Log
    {
        const string Prefix = "[HSAEnhanced] ";

        internal static void Warn(string text)
        {
#if WITHOUT_HSA
            UnityEngine.Debug.LogWarning(Prefix + text);
#else
            Accessibility.AccessibilityUtils.LogDebug(Prefix + text);
#endif
        }

        // Always written (what the menus contain, for bug reports)
        internal static void Info(string text)
        {
#if WITHOUT_HSA
            try { UnityEngine.Debug.Log(Prefix + text); } catch { }
#else
            try { global::Log.Accessibility.Print(Prefix + text); } catch { }
#endif
        }

        static readonly System.Collections.Generic.HashSet<string> s_said = new System.Collections.Generic.HashSet<string>();

        // Info, each distinct message once (for things checked every second)
        internal static void Once(string text)
        {
            if (s_said.Add(text)) Info(text);
        }

        internal static void Error(Exception e)
        {
#if WITHOUT_HSA
            UnityEngine.Debug.LogError(Prefix + e);
#else
            Accessibility.AccessibilityUtils.LogFatalError(e);
#endif
        }
    }
}
