using System;
using System.Collections.Generic;
using System.Reflection;
using Hearthstone.UI;
using UnityEngine;

namespace HSAEnhanced.Core
{
    // Pressing the game's buttons the way the game expects:
    // - a PegUIElement (most buttons): pressed and released;
    // - a widget button: its BUTTON_CLICKED event;
    // - anything else: a click of the mouse over it (our virtual mouse, or Hearthstone Access's).
    static class Click
    {
        internal static void Peg(PegUIElement peg)
        {
            if (peg == null) return;
            peg.TriggerPress();
            peg.TriggerRelease();
        }

        internal static void WidgetClicked(Widget widget) { if (widget != null) widget.TriggerEvent("BUTTON_CLICKED"); }

        internal static void Mouse(Component c) { if (c != null) Mouse(c.gameObject); }

        internal static void Mouse(GameObject go)
        {
            if (go == null) return;
#if WITHOUT_HSA
            VirtualMouse.ClickOn(go);
#else
            Accessibility.AccessibleInputMgr.Click(go);
#endif
        }
    }

#if WITHOUT_HSA
    // Without Hearthstone Access: a mouse the game sees through its own input layer
    // (InputCollection). It answers only while it is clicking, so the real mouse keeps working.
    // A click: the position from the next frame on, the button down for one frame, then up.
    class VirtualMouse : IInput
    {
        static VirtualMouse s_mouse;
        Vector3 m_position;
        int m_step;            // 0 idle; 1 moved; 2 down; 3 up; then idle again after a few frames
        int m_frame;
        bool m_down, m_wasDown;

        static void Ensure()
        {
            if (s_mouse != null) return;
            s_mouse = new VirtualMouse();
            var f = typeof(InputCollection).GetField("m_Inputs", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var list = f == null ? null : f.GetValue(null) as List<IInput>;
            if (list == null) { Log.Info("virtual mouse: the game's input list was not found"); return; }
            list.Insert(0, s_mouse);
        }

        internal static void ClickOn(GameObject go)
        {
            Ensure();
            var cam = Camera.main;
            var at = go.transform.position;
            Vector3 screen;
            // UI on the box camera or the Battle.net bar camera: projected through the camera that draws it
            var drawer = CameraUtils.FindFirstByLayer(go.layer);
            screen = (drawer != null ? drawer : cam) != null ? (drawer != null ? drawer : cam).WorldToScreenPoint(at) : Vector3.zero;
            screen.z = 0;
            s_mouse.m_position = screen;
            s_mouse.m_step = 1;
            s_mouse.m_frame = Time.frameCount;
            Log.Info("virtual mouse: click " + go.name + " at " + screen);
        }

        // Host.LateUpdate: one step per frame
        internal static void Step()
        {
            var m = s_mouse;
            if (m == null || m.m_step == 0 || Time.frameCount == m.m_frame) return;
            m.m_frame = Time.frameCount;
            m.m_wasDown = m.m_down;
            switch (m.m_step)
            {
                case 1: m.m_down = true; m.m_step = 2; break;
                case 2: m.m_down = false; m.m_step = 3; break;
                case 3: m.m_step = 4; break;
                default: m.m_step = 0; break;
            }
        }

        bool Active { get { return m_step != 0; } }

        public bool GetMousePosition(out Vector3 position) { position = m_position; return Active; }
        public bool GetAnyKey(out bool value) { value = false; return false; }
        public bool GetKey(KeyCode keycode, out bool value) { value = false; return false; }
        public bool GetKeyDown(KeyCode keycode, out bool value) { value = false; return false; }
        public bool GetKeyUp(KeyCode keycode, out bool value) { value = false; return false; }
        public bool GetMouseButton(int button, out bool value) { value = button == 0 && m_down; return Active; }
        public bool GetMouseButtonDown(int button, out bool value) { value = button == 0 && m_down && !m_wasDown; return Active; }
        public bool GetMouseButtonUp(int button, out bool value) { value = button == 0 && !m_down && m_wasDown; return Active; }
    }
#endif
}
