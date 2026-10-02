using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Accessibility;

namespace HSAEnhanced
{
    // Reaches into AccessibleMenu's option list (private in HSA) to insert and remove options
    static class MenuEdit
    {
        static readonly FieldInfo Options = Ref.Field(typeof(AccessibleMenu), "m_options");
        static readonly FieldInfo Index = Ref.Field(typeof(AccessibleMenu), "m_curOptionIdx");

        internal static IList List(AccessibleMenu menu) { return Options == null ? null : Options.GetValue(menu) as IList; }

        internal static string TextOf(object option)
        {
            var text = Ref.Get(option, "m_text") as string;
            if (text != null) return text;
            var get = Ref.Get(option, "m_getText") as AccessibleMenu.GetTextDelegate;
            try { return get == null ? "" : get(); } catch { return ""; }
        }

        // Adds options at `position` (end when negative); returns the option objects added
        internal static List<object> Insert(AccessibleMenu menu, int position, IList<GameButton> buttons)
        {
            var list = List(menu);
            var added = new List<object>();
            if (list == null) return added;
            foreach (var b in buttons)
            {
                var button = b;
                menu.AddOption(button.Label, () => { try { Log.Info("option: " + button.Label + " (" + Ui.StateText(button.Target) + ")"); button.Click(); } catch (Exception e) { Log.Error(e); } });
                added.Add(list[list.Count - 1]);
            }
            if (position >= 0 && position < list.Count - added.Count)
            {
                foreach (var o in added) list.Remove(o);
                for (int i = 0; i < added.Count; i++) list.Insert(position + i, added[i]);
            }
            return added;
        }

        internal static void Remove(AccessibleMenu menu, IEnumerable<object> options)
        {
            var list = List(menu);
            if (list == null) return;
            foreach (var o in options) list.Remove(o);
            ClampIndex(menu);
        }

        // names of the methods an option runs (its delegate fields)
        internal static HashSet<string> ActionNames(object option)
        {
            var names = new HashSet<string>();
            for (var t = option.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    Delegate d;
                    try { d = f.GetValue(option) as Delegate; } catch { continue; }
                    if (d == null) continue;
                    foreach (var one in d.GetInvocationList()) if (one.Method != null) names.Add(one.Method.Name);
                }
            return names;
        }

        // A menu built again in place of `old`: it keeps taking Enter. HSA's menu acts on Enter only
        // once it has started reading, and a rebuilt menu is not read again
        internal static AccessibleMenu Carry(AccessibleMenu old, AccessibleMenu fresh)
        {
            if (old != null && Ref.Get<bool>(old, "m_isReading")) Ref.Set(fresh, "m_isReading", true);
            return fresh;
        }

        internal static void ClampIndex(AccessibleMenu menu)
        {
            var list = List(menu);
            if (list == null || Index == null) return;
            var i = (int)Index.GetValue(menu);
            if (i >= list.Count) Index.SetValue(menu, Math.Max(0, list.Count - 1));
        }

        internal static int GetIndex(AccessibleMenu menu) { return Index == null ? 0 : (int)Index.GetValue(menu); }

        internal static void SetIndex(AccessibleMenu menu, int index) { if (Index != null) Index.SetValue(menu, index); }

        static readonly FieldInfo BackAction = Ref.Field(typeof(AccessibleMenu), "m_goBackAction");

        internal static Action GetBack(AccessibleMenu menu) { return BackAction == null ? null : BackAction.GetValue(menu) as Action; }

        internal static void SetBack(AccessibleMenu menu, Action back) { if (BackAction != null) BackAction.SetValue(menu, back); }

        internal static int IndexOfText(AccessibleMenu menu, string text)
        {
            var list = List(menu);
            if (list == null || string.IsNullOrEmpty(text)) return -1;
            for (int i = 0; i < list.Count; i++) if (TextOf(list[i]) == text) return i;
            return -1;
        }
    }
}
