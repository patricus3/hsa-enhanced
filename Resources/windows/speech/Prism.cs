// Speech for Hearthstone Access on Windows, straight through Prism (prism.dll, MPL-2.0).
// Replaces Tolk: the Windows build rewrites HSA's calls to DavyKager.Tolk into calls to
// this class (`port speech`), so no Tolk library is loaded.
//
// Prism picks the screen reader that is running (NVDA, JAWS, ZoomText, System Access,
// ...) and falls back to OneCore / SAPI voices when there is none. The choice is
// re-checked every few seconds, so starting or quitting a screen reader while playing
// moves speech over by itself.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HSAPrism
{
    public sealed class Speech
    {
        const string Lib = "prism.dll";
        const int RecheckMs = 2000;

        [StructLayout(LayoutKind.Sequential)]
        struct PrismConfig
        {
            public byte version;
            public IntPtr registry;
            public IntPtr availability_callback;
            public IntPtr availability_userdata;
            public uint availability_poll_interval_ms;
            public uint availability_debounce_samples;
            public uint availability_backoff_max_ms;
            public byte availability_auto_power_manage;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern PrismConfig prism_config_init();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr prism_init(ref PrismConfig cfg);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void prism_shutdown(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr prism_registry_acquire_best(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int prism_backend_initialize(IntPtr backend);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr prism_backend_name(IntPtr backend);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int prism_backend_output(IntPtr backend, byte[] utf8, [MarshalAs(UnmanagedType.U1)] bool interrupt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int prism_backend_stop(IntPtr backend);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;

        static readonly object s_lock = new object();
        static IntPtr s_ctx, s_backend;
        static string s_name = "";
        static int s_lastCheck;
        static bool s_loaded;

        Speech() { }

        // prism.dll sits in Hearthstone_Data\Managed\Accessibility; load it from there so the
        // DllImports above find it without touching PATH
        static void Preload()
        {
            string dir = null;
            try { dir = Path.GetDirectoryName(typeof(Speech).Assembly.Location); } catch { }
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "Hearthstone_Data"), "Managed");
            var dll = Path.Combine(Path.Combine(dir, "Accessibility"), Lib);
            if (File.Exists(dll)) LoadLibraryExW(dll, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
        }

        public static void Load()
        {
            lock (s_lock)
            {
                if (s_loaded) return;
                try
                {
                    Preload();
                    var cfg = prism_config_init();
                    s_ctx = prism_init(ref cfg);
                    s_loaded = s_ctx != IntPtr.Zero;
                    if (s_loaded) PickBest(true);
                }
                catch (Exception) { s_loaded = false; }
            }
        }

        // Switches to the best backend Prism sees now (a screen reader when one runs)
        static void PickBest(bool force)
        {
            if (s_ctx == IntPtr.Zero) return;
            int now = Environment.TickCount;
            if (!force && unchecked(now - s_lastCheck) < RecheckMs) return;
            s_lastCheck = now;
            var best = prism_registry_acquire_best(s_ctx);
            if (best == IntPtr.Zero || best == s_backend) return;
            prism_backend_initialize(best); // already-initialized is fine
            s_backend = best;
            var n = prism_backend_name(best);
            s_name = n == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(n);
        }

        public static bool IsLoaded() { return s_loaded; }

        public static void Unload()
        {
            lock (s_lock)
            {
                if (s_ctx != IntPtr.Zero) prism_shutdown(s_ctx);
                s_ctx = s_backend = IntPtr.Zero;
                s_loaded = false;
            }
        }

        // Prism chooses between screen readers and voices by itself
        public static void TrySAPI(bool trySAPI) { }
        public static void PreferSAPI(bool preferSAPI) { }

        // HSA stops speech on every key press when this is "SAPI"; do the same for any
        // self-voiced backend (SAPI, OneCore), since there is no screen reader to do it
        public static string DetectScreenReader()
        {
            lock (s_lock)
            {
                PickBest(false);
                var n = s_name ?? "";
                if (n.IndexOf("SAPI", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("OneCore", StringComparison.OrdinalIgnoreCase) >= 0) return "SAPI";
                return n;
            }
        }

        public static bool HasSpeech() { return s_backend != IntPtr.Zero; }
        public static bool HasBraille() { return false; }

        public static bool Output(string str, bool interrupt = false)
        {
            lock (s_lock)
            {
                if (!s_loaded) return false;
                try
                {
                    PickBest(false);
                    if (s_backend == IntPtr.Zero) return false;
                    // HSA interrupts with Output("", true)
                    if (string.IsNullOrEmpty(str)) return interrupt && prism_backend_stop(s_backend) == 0;
                    var text = Utf8(str);
                    if (prism_backend_output(s_backend, text, interrupt) == 0) return true;
                    // the screen reader may just have quit: pick again and retry once
                    PickBest(true);
                    return s_backend != IntPtr.Zero && prism_backend_output(s_backend, text, interrupt) == 0;
                }
                catch (Exception) { return false; }
            }
        }

        public static bool Speak(string str, bool interrupt = false) { return Output(str, interrupt); }
        public static bool Braille(string str) { return false; }
        public static bool IsSpeaking() { return false; }

        public static bool Silence()
        {
            lock (s_lock)
            {
                try { return s_backend != IntPtr.Zero && prism_backend_stop(s_backend) == 0; }
                catch (Exception) { return false; }
            }
        }

        static byte[] Utf8(string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            var z = new byte[b.Length + 1];
            Buffer.BlockCopy(b, 0, z, 0, b.Length);
            return z;
        }
    }
}
