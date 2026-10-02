using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
#if !WITHOUT_HSA
using Accessibility;
#endif
using UnityEngine;

namespace HSAEnhanced.Core
{
    // One of our screens or popups: it gets the keys while it has focus.
    abstract class Screen
    {
        // true: the key press was ours (it goes nowhere else, decision 5)
        internal abstract bool HandleKey();
        internal abstract string Help();
        // read when it gets focus back (a popup over it closed, decision 4)
        internal abstract void Read();
        // false: the game's screen behind it is gone, and it is closed
        internal virtual bool Alive { get { return true; } }

        internal bool Focused { get { return Focus.Top == this; } }

        // spoken only while it has focus
        internal void Say(string text, bool interrupt = false) { if (Focused) Speech.Say(text, interrupt); }
    }

    // Our stack of screens, beside Hearthstone Access's. Ours has focus while it is on top and no
    // Hearthstone Access popup (a game dialog) came up after it was shown.
    static class Focus
    {
        class Entry { internal Screen Screen; internal HashSet<object> Under; }

        static readonly List<Entry> s_stack = new List<Entry>();
        static Screen s_lastFocused;

#if WITHOUT_HSA
        static IList HsaUIs { get { return null; } }
        static object ForcedKeyValue { get { return null; } }
#else
        static readonly FieldInfo UIs = Ref.Field(typeof(AccessibilityMgr), "s_curUIs");
        static readonly FieldInfo ForcedKey = Ref.Field(typeof(AccessibilityMgr), "s_forcedKey");
        static readonly FieldInfo CurScreen = Ref.Field(typeof(AccessibilityMgr), "s_curScreen");
        static readonly MethodInfo GlobalInput = Ref.Method(typeof(AccessibilityMgr), "HandleGlobalInput", 0);

        static object Static(FieldInfo f) { return f == null ? null : f.GetValue(null); }

        static IList HsaUIs { get { return Static(UIs) as IList; } }
        static object ForcedKeyValue { get { return Static(ForcedKey); } }
#endif

        internal static void Push(Screen screen)
        {
            Remove(screen);
            var under = new HashSet<object>();
            var uis = HsaUIs;
            if (uis != null) foreach (var u in uis) under.Add(u);
            s_stack.Add(new Entry { Screen = screen, Under = under });
            s_lastFocused = Top;
            Log.Info("focus: " + screen.GetType().Name + " shown");
        }

        // a screen of the game (the main menu, a screen read as a whole): under any popup of ours
        internal static void PushBase(Screen screen)
        {
            Remove(screen);
            var under = new HashSet<object>();
            var uis = HsaUIs;
            if (uis != null) foreach (var u in uis) under.Add(u);
            s_stack.Insert(0, new Entry { Screen = screen, Under = under });
            s_bases.Add(screen);
            if (s_stack.Count == 1) s_lastFocused = Top;
            Log.Info("focus: " + screen.GetType().Name + " shown (screen)");
        }

        // the game screen of ours (in place of the one before it), read when it has focus
        internal static void SetScreen(Screen screen)
        {
            foreach (var e in s_stack.ToArray()) if (s_bases.Contains(e.Screen) && e.Screen != screen) Remove(e.Screen);
            PushBase(screen);
            if (screen.Focused) { try { screen.Read(); } catch (Exception ex) { Log.Error(ex); } }
        }

        // a game screen of ours is open
        internal static bool HasBase { get { return s_bases.Count > 0 && s_stack.Exists(e => s_bases.Contains(e.Screen)); } }

        static readonly HashSet<Screen> s_bases = new HashSet<Screen>();

        internal static void Pop(Screen screen)
        {
            bool wasTop = s_stack.Count > 0 && s_stack[s_stack.Count - 1].Screen == screen;
            if (!Remove(screen)) return;
            Log.Info("focus: " + screen.GetType().Name + " closed");
            if (!wasTop) return;
            var top = Top;
            s_lastFocused = top;
            if (top != null) { Read(top); return; }
            if (s_stack.Count == 0) RefocusHsa();
        }

        static bool Remove(Screen screen)
        {
            s_bases.Remove(screen);
            int n = s_stack.RemoveAll(e => e.Screen == screen);
            return n > 0;
        }

        // ours on top, when nothing of Hearthstone Access's came up over it
        internal static Screen Top
        {
            get
            {
                if (s_stack.Count == 0) return null;
                var top = s_stack[s_stack.Count - 1];
                if (ForcedKeyValue != null) return null;
                var uis = HsaUIs;
                if (uis != null) foreach (var u in uis) if (!top.Under.Contains(u)) return null;
                return top.Screen;
            }
        }

        internal static bool Any { get { return s_stack.Count > 0; } }

        internal static bool Has(Screen screen) { return s_stack.Exists(e => e.Screen == screen); }

        static void Read(Screen s)
        {
            try { s.Read(); } catch (Exception e) { Log.Error(e); }
        }

        // our last screen closed: Hearthstone Access's screen is read again, as when its own popup closes
        static void RefocusHsa()
        {
#if !WITHOUT_HSA
            try
            {
                var uis = HsaUIs;
                if (uis != null && uis.Count > 0) return;
                var screen = Static(CurScreen);
                if (screen != null) Ref.Call(screen, "OnGainedFocus");
            }
            catch (Exception e) { Log.Error(e); }
#endif
        }

        // every frame: closed when their game screen is gone; read again when focus comes back
        internal static void Tick()
        {
            for (int i = s_stack.Count - 1; i >= 0; i--)
            {
                if (i >= s_stack.Count) continue;
                bool alive;
                try { alive = s_stack[i].Screen.Alive; } catch { alive = false; }
                if (!alive) Pop(s_stack[i].Screen);
            }
            var top = Top;
            if (top != null && top != s_lastFocused) Read(top);
            s_lastFocused = top;
        }

        // With Hearthstone Access: start of AccessibilityMgr.HandleKeyboardInput; true: ours had focus
        // (Hearthstone Access's routing is skipped). Without it: start of the game's keyboard
        // handling; true: ours took the key press (the game's handling is skipped)
        internal static bool HandleKeys()
        {
            var top = Top;
            if (top == null) return false;
#if WITHOUT_HSA
            var input = UniversalInputManager.Get();
            if (input != null && input.IsTextInputActive()) return false;
#else
            if (AccessibilityMgr.IsTextInputAllowed()) return false;
#endif
            if (Input.anyKeyDown && Speech.UsingSapi) Speech.Silence();
            if (Keys.Help.Pressed) { Speech.Say(top.Help()); return true; }
            bool mine = false;
            try { mine = top.HandleKey(); } catch (Exception e) { Log.Error(e); }
#if WITHOUT_HSA
            return mine;
#else
            // Escape (the game menu), F4 (friends), F11/F12 (game speed): Hearthstone Access's for now
            if (!mine && GlobalInput != null) GlobalInput.Invoke(null, null);
            return true;
#endif
        }

        // start of AccessibilityMgr.Output: Hearthstone Access's screens are quiet behind ours
        internal static bool Mutes(object speaker) { return speaker != null && Top != null; }
    }
}
