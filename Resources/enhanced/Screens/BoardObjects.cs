#if WITHOUT_HSA
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Z in a match: what a sighted player can poke at besides the cards. Your pet (Enter pokes it, as
    // a click does; its toy and treat when it has them), then the board's own clickable decorations,
    // in screen order (Enter is the click their animations listen for). The game names none of the
    // decorations: their names are made from the board's object names.
    static class BoardObjects
    {
        static readonly Type FsmType = Type.GetType("PlayMakerFSM, PlayMaker");
        static readonly PropertyInfo FsmEvents = FsmType == null ? null : FsmType.GetProperty("FsmEvents");
        static readonly MethodInfo SendEvent = FsmType == null ? null : FsmType.GetMethod("SendEvent", new[] { typeof(string) });

        internal static void Open()
        {
            var menu = new BoardObjectsUI();
            if (!menu.Build()) { Speech.Say(Speech.S("ACCESSIBILITY_LIST_NO_ITEMS"), true); return; }
            Focus.Push(menu);
            menu.Read();
        }

        // the friendly pet of the match, if there is one
        internal static PetControllerGame Pet()
        {
            try
            {
                var gs = GameState.Get();
                var me = gs == null ? null : gs.GetFriendlySidePlayer();
                var mgr = PetGameplayManager.Get();
                PetControllerGame pet;
                if (me != null && mgr != null && mgr.TryGetPet(me.GetPlayerId(), out pet) && pet != null && pet.gameObject.activeInHierarchy) return pet;
            }
            catch (Exception e) { Log.Error(e); }
            return null;
        }

        internal static string PetName(PetControllerGame pet)
        {
            try { var dm = PetUtility.CreatePetDataModel(pet.PetId); if (dm != null && !string.IsNullOrEmpty(dm.DisplayName)) return Str.Clean(dm.DisplayName); } catch { }
            return Pets.Title;
        }

        // the board's decorations that answer a click: animation state machines listening for the mouse
        internal static List<KeyValuePair<Component, string>> Decorations()
        {
            var found = new List<KeyValuePair<Component, string>>();
            if (FsmType == null || FsmEvents == null || SendEvent == null) return found;
            var board = Board.Get();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(FsmType))
            {
                var fsm = o as Component;
                if (fsm == null || !fsm.gameObject.activeInHierarchy) continue;
                if (fsm.GetComponentInParent<Card>() != null || fsm.GetComponentInParent<Actor>() != null) continue;
                if (board != null && !fsm.transform.IsChildOf(board.transform)) continue;
                if (fsm.GetComponent<Collider>() == null && fsm.GetComponentInChildren<Collider>() == null) continue;
                bool mouse = false;
                try
                {
                    var events = FsmEvents.GetValue(fsm, null) as Array;
                    if (events != null)
                        foreach (var ev in events)
                        {
                            var name = ev == null ? null : ev.GetType().GetProperty("Name").GetValue(ev, null) as string;
                            if (name == "MOUSE DOWN" || name == "MOUSE UP" || name == "MOUSE UP AS BUTTON") { mouse = true; break; }
                        }
                }
                catch { }
                if (!mouse) continue;
                found.Add(new KeyValuePair<Component, string>(fsm, Humanize(fsm.transform)));
            }
            var sorted = found.ConvertAll(p => new GameButton { Target = p.Key, Label = p.Value });
            Ui.SortByScreen(sorted);
            return sorted.ConvertAll(b => new KeyValuePair<Component, string>(b.Target, b.Label));
        }

        // the click the decoration's animation listens for
        internal static void Click(Component fsm)
        {
            try
            {
                var events = FsmEvents.GetValue(fsm, null) as Array;
                var names = new List<string>();
                if (events != null) foreach (var ev in events) { var n = ev == null ? null : ev.GetType().GetProperty("Name").GetValue(ev, null) as string; if (n != null) names.Add(n); }
                foreach (var n in new[] { "MOUSE DOWN", "MOUSE UP", "MOUSE UP AS BUTTON" })
                    if (names.Contains(n)) SendEvent.Invoke(fsm, new object[] { n });
                Log.Info("board: clicked " + fsm.name);
            }
            catch (Exception e) { Log.Error(e); }
        }

        static readonly string[] Noise = { "collider", "collision", "click", "clickable", "trigger", "mesh", "fx", "hitbox", "geo", "obj", "object", "group", "root", "bone", "lod" };

        // "Catapult_Collider01" -> "Catapult"; a name with nothing left: the parent's
        static string Humanize(Transform t)
        {
            for (var at = t; at != null; at = at.parent)
            {
                var words = Words(at.name);
                if (words.Length > 0) return words;
            }
            return t.name;
        }

        static string Words(string name)
        {
            var sb = new StringBuilder();
            var parts = new List<string>();
            Action flush = () => { if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Length = 0; } };
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetter(c)) { flush(); continue; }
                if (char.IsUpper(c) && sb.Length > 0 && !char.IsUpper(sb[sb.Length - 1])) flush();
                sb.Append(c);
            }
            flush();
            parts.RemoveAll(p => p.Length < 2 || Array.IndexOf(Noise, p.ToLowerInvariant()) >= 0);
            if (parts.Count == 0) return "";
            var text = string.Join(" ", parts.ToArray()).ToLowerInvariant();
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }
    }

    class BoardObjectsUI : Core.Screen
    {
        Menu m_menu;

        internal override bool Alive { get { return GameState.Get() != null && !GameState.Get().IsGameOver(); } }

        internal bool Build()
        {
            var menu = new Menu(this, "", () => Focus.Pop(this));
            var pet = BoardObjects.Pet();
            if (pet != null)
            {
                var p = pet;
                menu.AddOption(BoardObjects.PetName(pet), () => { Log.Info("board: poke the pet"); Core.Click.Mouse(p.gameObject); });
                foreach (var item in new[] { pet.Toy, pet.Treat })
                {
                    if (item == null || !item.gameObject.activeInHierarchy) continue;
                    var it = item;
                    menu.AddOption(Str.Join(BoardObjects.PetName(pet), BoardObjectsName(item)), () => { Log.Info("board: pet item " + it.name); Core.Click.Mouse(it.gameObject); });
                }
            }
            foreach (var d in BoardObjects.Decorations())
            {
                var target = d.Key;
                menu.AddOption(d.Value, () => BoardObjects.Click(target));
            }
            if (menu.Count == 0) return false;
            m_menu = menu;
            Log.Info("board: " + menu.Count + " things to poke");
            return true;
        }

        static string BoardObjectsName(Component item)
        {
            var n = item.name.ToLowerInvariant();
            return n.Contains("toy") ? "toy" : n.Contains("treat") ? "treat" : item.name;
        }

        internal override bool HandleKey() { return m_menu != null && m_menu.HandleKey(); }

        internal override string Help() { return m_menu == null ? "" : m_menu.Help(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }
}
#endif
