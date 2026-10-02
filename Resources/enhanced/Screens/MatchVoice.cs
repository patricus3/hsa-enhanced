namespace HSAEnhanced
{
    // who speaks for a match: our match screen, or Hearthstone Access's gameplay screen
    static class MatchVoice
    {
        internal static object Speaker
        {
            get
            {
#if WITHOUT_HSA
                return Match.Screen;
#else
                return Accessibility.AccessibleGameplay.Get();
#endif
            }
        }

        internal static void Say(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
#if WITHOUT_HSA
            var s = Match.Screen;
            if (s != null) s.Say(text);
#else
            Accessibility.AccessibilityMgr.Output(Accessibility.AccessibleGameplay.Get(), text);
#endif
        }
    }
}
