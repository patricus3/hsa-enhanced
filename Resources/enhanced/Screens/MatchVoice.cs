namespace HSAEnhanced
{
    // who speaks for a match: our match screen, or Hearthstone Access's gameplay screen
    static class MatchVoice
    {
        internal static object Speaker
        {
            get
            {
                return Match.Screen;
            }
        }

        internal static void Say(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var s = Match.Screen;
            if (s != null) s.Say(text);
        }
    }
}
