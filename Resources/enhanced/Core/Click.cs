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
            VirtualMouse.ClickOn(go);
        }

    }

    // Without Hearthstone Access: a mouse the game sees through its own input layer
    // (InputCollection). It answers only while it clicks or points, so the real mouse keeps working.
    // A click: the position from the next frame on, the button down for one frame, then up.
    // Pointing (in a match): it rests where the keyboard is (the card looked at, a summoning slot, a
    // target), so the game does what it does under a sighted player's mouse: the hover, the card held
    // over the board, the target arrow. Moving the real mouse ends it; so does leaving the match.
    class VirtualMouse : IInput
    {
        static VirtualMouse s_mouse;
        Vector3 m_position;
        int m_step;            // 0 idle; 1 moved; 2 down; 3 up; then idle again after a few frames
        int m_frame;
        bool m_down, m_wasDown;
        bool m_pointing;
        Vector3 m_realAt;      // the real mouse when pointing began

        static Vector3 ScreenOf(GameObject go)
        {
            var drawer = CameraUtils.FindFirstByLayer(go.layer);
            var cam = drawer != null ? drawer : Camera.main;
            if (cam == null) return Vector3.zero;
            var screen = cam.WorldToScreenPoint(go.transform.position);
            screen.z = 0;
            return screen;
        }

        // rest over this object (a card)
        internal static void PointAt(GameObject go)
        {
            if (go == null) return;
            Ensure();
            s_mouse.m_position = ScreenOf(go);
            BeginPointing();
        }

        // rest over this point of the board (a summoning slot)
        internal static void PointAtWorld(Vector3 world)
        {
            var cam = Camera.main;
            if (cam == null) return;
            Ensure();
            var screen = cam.WorldToScreenPoint(world);
            screen.z = 0;
            s_mouse.m_position = screen;
            BeginPointing();
        }

        static void BeginPointing()
        {
            if (!s_mouse.m_pointing) s_mouse.m_realAt = Input.mousePosition;
            s_mouse.m_pointing = true;
        }

        internal static void StopPointing() { if (s_mouse != null) s_mouse.m_pointing = false; }

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
            if (m != null && m.m_pointing)
            {
                var scenes = SceneMgr.Get();
                if ((Input.mousePosition - m.m_realAt).sqrMagnitude > 25f) { m.m_pointing = false; Log.Info("virtual mouse: the real mouse moved"); }
                else if (scenes == null || scenes.GetMode() != SceneMgr.Mode.GAMEPLAY) m.m_pointing = false;
            }
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

        bool Active { get { return m_step != 0 || m_pointing; } }

        public bool GetMousePosition(out Vector3 position) { position = m_position; return Active; }
        public bool GetAnyKey(out bool value) { value = false; return false; }
        public bool GetKey(KeyCode keycode, out bool value) { value = false; return false; }
        public bool GetKeyDown(KeyCode keycode, out bool value) { value = false; return false; }
        public bool GetKeyUp(KeyCode keycode, out bool value) { value = false; return false; }
        public bool GetMouseButton(int button, out bool value) { value = button == 0 && m_down; return Active; }
        public bool GetMouseButtonDown(int button, out bool value) { value = button == 0 && m_down && !m_wasDown; return Active; }
        public bool GetMouseButtonUp(int button, out bool value) { value = button == 0 && !m_down && m_wasDown; return Active; }
    }
}
