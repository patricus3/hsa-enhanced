using System;
using System.Reflection;

namespace HSAEnhanced
{
    // Private members of game and HSA classes, looked up by name so a renamed member
    // costs one feature instead of the whole build.
    static class Ref
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal static FieldInfo Field(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        internal static object Get(object o, string field)
        {
            if (o == null) return null;
            var f = Field(o.GetType(), field);
            return f == null ? null : f.GetValue(o);
        }

        internal static T Get<T>(object o, string field)
        {
            var v = Get(o, field);
            return v is T ? (T)v : default(T);
        }

        internal static void Set(object o, string field, object value)
        {
            var f = o == null ? null : Field(o.GetType(), field);
            if (f != null) f.SetValue(o, value);
        }

        internal static MethodInfo Method(Type t, string name, int argCount)
        {
            for (; t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly))
                    if (m.Name == name && m.GetParameters().Length == argCount) return m;
            return null;
        }

        // a method's result (null when it is missing or returns nothing)
        internal static object Invoke(object o, string method, params object[] args)
        {
            var m = o == null ? null : Method(o.GetType(), method, args.Length);
            if (m == null) { Log.Warn("missing method " + (o == null ? "?" : o.GetType().Name) + "." + method); return null; }
            return m.Invoke(m.IsStatic ? null : o, args);
        }

        internal static bool Call(object o, string method, params object[] args)
        {
            var m = o == null ? null : Method(o.GetType(), method, args.Length);
            if (m == null) { Log.Warn("missing method " + (o == null ? "?" : o.GetType().Name) + "." + method); return false; }
            m.Invoke(m.IsStatic ? null : o, args);
            return true;
        }
    }

    static class Log
    {
        internal static void Warn(string text)
        {
            Accessibility.AccessibilityUtils.LogDebug("[HSAEnhanced] " + text);
        }

        // Always written to Accessibility.log (what the added menus contain, for bug reports)
        internal static void Info(string text)
        {
            try { global::Log.Accessibility.Print("[HSAEnhanced] " + text); } catch { }
        }

        static readonly System.Collections.Generic.HashSet<string> s_said = new System.Collections.Generic.HashSet<string>();

        // Info, each distinct message once (for things checked every second)
        internal static void Once(string text)
        {
            if (s_said.Add(text)) Info(text);
        }

        internal static void Error(Exception e)
        {
            Accessibility.AccessibilityUtils.LogFatalError(e);
        }
    }
}
