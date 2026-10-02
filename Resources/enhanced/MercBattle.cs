using System;
using System.Collections.Generic;
using HSAEnhanced.Core;
using UnityEngine;

namespace HSAEnhanced
{
    // Mercenaries battles (HSA has no Mercenaries support; it reads mercenaries as minions).
    // - Placing mercenaries (the start, or replacing one that died): Enter on a mercenary on the bench
    //   puts it into play at the right end, with the option the game sends when one is dropped there.
    // - Commanding: Enter on a friendly mercenary opens its ability tray, and while the game shows the
    //   tray (it also opens by itself for each mercenary still to command) a menu of the abilities the
    //   tray shows is up: each with its speed, cooldown and text, the one already queued first.
    //   Choosing one does what a click on it does (the game's own option); a Choose One goes to HSA's
    //   choice mode. Also: cancel the queued ability, the mercenary's equipment, the Ready / Fight button.
    // - Targets: while an ability waits for its target, a menu of the targets the game allows (enemies
    //   first); Enter sends the target with the game's option (no mouse), Back cancels the targeting.
    // - Enter on an enemy mercenary says what it has prepared.
    static class MercBattle
    {
        static MercMenuUI s_ui;
        static int s_dismissedFor;     // the mercenary whose menu was closed with Back

        internal static bool InBattle
        {
            get { var gs = GameState.Get(); return gs != null && gs.GetGameEntity() is LettuceMissionEntity; }
        }

        static bool Nominating
        {
            get { var gs = GameState.Get(); return gs.IsActionStep() && gs.GetActionStepType() == ACTION_STEP_TYPE.LETTUCE_MERCENARY_SELECTION; }
        }

        static bool Commanding
        {
            get { var gs = GameState.Get(); return gs.IsActionStep() && gs.GetActionStepType() == ACTION_STEP_TYPE.DEFAULT; }
        }

        // the mercenaries on the board last frame (entity id -> name), to say who died
        static readonly Dictionary<int, string> s_onBoard = new Dictionary<int, string>();

        // HSA's snapshots of the game follow the turn steps of normal games and say nothing of deaths in
        // Mercenaries: a mercenary that leaves the board for the graveyard is said here ("X died")
        static void TickDeaths()
        {
            var gs = GameState.Get();
            var now = new Dictionary<int, string>();
            foreach (var side in new[] { Player.Side.FRIENDLY, Player.Side.OPPOSING })
            {
                var zone = ZoneMgr.Get() == null ? null : ZoneMgr.Get().FindZoneOfType<ZonePlay>(side);
                if (zone == null) continue;
                foreach (var c in zone.GetCards())
                {
                    var e = c == null ? null : c.GetEntity();
                    if (e != null) now[e.GetEntityId()] = Str.Clean(e.GetName());
                }
            }
            var died = new List<string>();
            foreach (var kv in s_onBoard)
            {
                if (now.ContainsKey(kv.Key)) continue;
                var e = gs.GetEntity(kv.Key);
                if (e == null) continue;
                var z = e.GetZone();
                if (z == TAG_ZONE.GRAVEYARD || z == TAG_ZONE.REMOVEDFROMGAME || (z != TAG_ZONE.HAND && e.GetCurrentHealth() <= 0)) died.Add(kv.Value);
            }
            s_onBoard.Clear();
            foreach (var kv in now) s_onBoard[kv.Key] = kv.Value;
            if (died.Count == 0) return;
            var text = Str.Join(Str.Join(died.ToArray()), Speech.S(died.Count > 1 ? K.GAMEPLAY_DIFF_MULTIPLE_ENTITIES_DIED : K.GAMEPLAY_DIFF_ENTITY_DIED));
            Log.Info("mercenaries: " + text);
            MatchVoice.Say(text);
        }

        // every frame, from FallbackWatcher
        internal static void Tick()
        {
            try
            {
                if (!InBattle || !true) { s_onBoard.Clear(); Hide(); return; }
                TickDeaths();
                if (GameState.Get().IsGameOver()) { Hide(); return; }
                var gs = GameState.Get();
                // an ability waiting for its target: the targets the game allows
                if (gs.IsInTargetMode())
                {
                    var option = gs.IsSelectedOptionMercenariesAbility() ? gs.GetSelectedNetworkOption() : null;
                    var ability = option == null || option.Main == null ? null : gs.GetEntity(option.Main.ID);
                    if (ability == null) { Hide(); return; }
                    var targetKey = "target:" + ability.GetEntityId();
                    if (s_ui != null && s_ui.Key == targetKey) return;
                    Hide();
                    var targets = new MercTargetUI(ability, targetKey);
                    Log.Info("mercenaries: targets for " + ability.GetName() + ": " + targets.Describe());
                    Show(targets);
                    return;
                }
                var zones = ZoneMgr.Get();
                var source = zones == null ? null : zones.GetLettuceAbilitiesSourceEntity();
                if (source == null) { s_dismissedFor = 0; Hide(); return; }
                if (!source.IsControlledByFriendlySidePlayer() || gs.IsInSubOptionMode() || ChoiceCardMgr.Get().IsShown()) { Hide(); return; }
                if (source.GetEntityId() == s_dismissedFor) return;
                var key = source.GetEntityId() + ":" + source.GetSelectedLettuceAbilityID() + ":" + gs.GetResponseMode();
                if (s_ui != null && s_ui.Key == key) return;
                Hide();
                var menu = new MercAbilityUI(source, key);
                Log.Info("mercenaries: abilities of " + source.GetName() + ": " + menu.Describe());
                Show(menu);
            }
            catch (Exception e) { Log.Error(e); Hide(); }
        }

        static void Show(MercMenuUI ui)
        {
            s_ui = ui;
            Core.Focus.Push(ui);
            ui.Start();
        }

        static void Hide()
        {
            if (s_ui == null) return;
            var ui = s_ui;
            s_ui = null;
            Core.Focus.Pop(ui);
        }

        internal static void Dismissed(Entity source)
        {
            s_dismissedFor = source.GetEntityId();
            Hide();
            try { ZoneMgr.Get().DismissMercenariesAbilityTray(); } catch (Exception e) { Log.Error(e); }
        }

        // Enter on the card HSA reads (its ClickCard); true: done here, HSA does not click
        internal static bool OnEnter(Card card)
        {
            if (!InBattle || card == null) return false;
            var gs = GameState.Get();
            if (gs.IsInTargetMode() || gs.IsInSubOptionMode()) return false;
            var merc = card.GetEntity();
            if (merc == null || !merc.IsMercenary()) return false;
            var gameplay = MatchVoice.Speaker;
            if (merc.IsControlledByFriendlySidePlayer() && card.GetZone() is ZoneHand) return Nominate(merc, gameplay);
            if (card.GetZone() is ZonePlay)
            {
                if (merc.IsControlledByFriendlySidePlayer() && Commanding)
                {
                    s_dismissedFor = 0;
                    Log.Info("mercenaries: command " + merc.GetName());
                    ZoneMgr.Get().DisplayLettuceAbilitiesForEntity(merc);
                    return true;
                }
                if (!merc.IsControlledByFriendlySidePlayer())
                {
                    MatchVoice.Say(Prepared(merc));
                    return true;
                }
            }
            return false;
        }

        // a mercenary from the bench into play, at the right end (as dropped there)
        static bool Nominate(Entity merc, object speaker)
        {
            var gs = GameState.Get();
            if (!Nominating || !gs.IsValidOption(merc))
            {
                MatchVoice.Say(Str.Join(Str.Clean(merc.GetName()), Str.Unavailable));
                return true;
            }
            var play = ZoneMgr.Get().FindZoneOfType<ZonePlay>(Player.Side.FRIENDLY);
            int pos = play == null ? 1 : ZoneMgr.Get().PredictZonePosition(merc, play, play.GetCards().Count + 1);
            Combat.WhenReady(speaker, merc, () =>
            {
                gs.SetSelectedOptionPosition(pos);
                Log.Info("mercenaries: " + merc.GetName() + " into play at " + pos);
                if (!InputManager.Get().DoNetworkResponse(merc))
                    MatchVoice.Say(Str.Join(Str.Clean(merc.GetName()), Str.Unavailable));
            });
            return true;
        }

        // the mercenary as its menu names it: name, role, attack / health
        internal static string Describe(Entity merc)
        {
            string role = null;
            try { role = GameStrings.GetRoleName(merc.GetMercenaryRole()); } catch { }
            return Str.Join(Str.Clean(merc.GetName()), Str.Clean(role), merc.GetATK() + "/" + merc.GetCurrentHealth());
        }

        // what a mercenary has prepared, as its bubble shows it: the ability, its speed, its turn
        internal static string Prepared(Entity merc)
        {
            var card = merc.GetCard();
            var ability = card == null ? null : card.GetPreparedLettuceAbilityEntity();
            if (ability == null) return Describe(merc);
            int order = card.GetLettuceAbilityActionOrder();
            return Str.Join(Describe(merc), Str.Clean(ability.GetName()),
                Str.Word("GAMEPLAY_LETTUCE_SPEED_LABEL_TUTORIAL") + " " + card.GetPreparedLettuceAbilitySpeedValue(),
                order > 0 ? Str.Word("GLUE_ORDINAL_" + order) : null);
        }

        // an ability as the tray shows it: name, speed, cooldown left, passive, text
        internal static string Describe(Entity ability, bool queued)
        {
            var parts = new List<string> { Str.Clean(ability.GetName()) };
            if (queued) parts.Add(Core.Speech.S(Core.K.OPTIONS_MENU_CHECKBOX_CHECKED));
            if (ability.HasTag(GAME_TAG.LETTUCE_PASSIVE_ABILITY)) parts.Add(Str.Word("GLOBAL_KEYWORD_PASSIVE"));
            else parts.Add(Str.Word("GAMEPLAY_LETTUCE_SPEED_LABEL_TUTORIAL") + " " + ability.GetCost());
            int cooldown = ability.GetTag(GAME_TAG.LETTUCE_CURRENT_COOLDOWN);
            if (cooldown > 0) parts.Add(Str.Game("GAMEPLAY_PlayErrors_REQ_NOT_IN_COOLDOWN", cooldown));
            string text = null;
            try { text = ability.GetCardTextInHand(); } catch { }
            parts.Add(Str.Clean(text));
            return Str.Join(parts.ToArray());
        }
    }

    // a menu of ours over the battle
    abstract class MercMenuUI : Core.Screen
    {
        internal readonly string Key;
        protected Core.Menu m_menu;
        readonly List<string> m_labels = new List<string>();

        protected MercMenuUI(string key) { Key = key; }

        protected void Add(string label, Action action)
        {
            m_labels.Add(label);
            m_menu.AddOption(label, action);
        }

        internal string Describe() { return string.Join(" | ", m_labels.ToArray()); }

        internal void Start() { m_menu.StartReading(); }

        internal override bool HandleKey() { return m_menu.HandleKey(); }

        internal override string Help() { return m_menu.GetHelp(); }

        internal override void Read() { if (m_menu != null) m_menu.StartReading(); }
    }

    // the targets the chosen ability may go to (the game's list for its option), enemies first; each as
    // the board shows it (role, attack / health, what an enemy prepared, 2x when strong against it)
    class MercTargetUI : MercMenuUI
    {
        internal MercTargetUI(Entity ability, string key) : base(key)
        {
            var gs = GameState.Get();
            var owner = ability.GetLettuceAbilityOwner();
            var title = Str.Join(Str.Clean(ability.GetName()), Core.Speech.S(Core.K.GAMEPLAY_CHOOSE_TARGET));
            m_menu = new Core.Menu(this, title, () => { Log.Info("mercenaries: target choice cancelled"); InputManager.Get().CancelTargetMode(); });
            foreach (var side in new[] { Player.Side.OPPOSING, Player.Side.FRIENDLY })
            {
                var zone = ZoneMgr.Get().FindZoneOfType<ZonePlay>(side);
                if (zone == null) continue;
                foreach (var c in zone.GetCards())
                {
                    var target = c == null ? null : c.GetEntity();
                    if (target == null || !gs.IsValidOptionTarget(target, false)) continue;
                    var t = target;
                    bool strong = owner != null && side == Player.Side.OPPOSING && owner.IsMyLettuceRoleStrongAgainst(t);
                    var label = Str.Join(side == Player.Side.OPPOSING ? MercBattle.Prepared(t) : MercBattle.Describe(t),
                        strong ? Str.Word("GAMEPLAY_LETTUCE_WEAKNESS_LABEL") : null);
                    Add(label, () => Combat.WhenReady(this, t, () =>
                    {
                        Log.Info("mercenaries: " + ability.GetName() + " at " + t.GetName());
                        if (!InputManager.Get().DoNetworkResponse(t)) Say(Str.Join(Str.Clean(t.GetName()), Str.Unavailable));
                    }));
                }
            }
        }
    }

    class MercAbilityUI : MercMenuUI
    {
        readonly Entity m_merc;

        internal MercAbilityUI(Entity merc, string key) : base(key)
        {
            m_merc = merc;
            m_menu = new Core.Menu(this, MercBattle.Describe(merc), () => MercBattle.Dismissed(m_merc));
            var gs = GameState.Get();
            int queued = merc.GetSelectedLettuceAbilityID();
            // the abilities the tray shows (the game's own choice of cards), else the mercenary's own list
            var ids = new List<int>();
            var shown = ZoneMgr.Get().GetLettuceAbilitiesSourceEntity() == merc ? ZoneMgr.Get().GetDisplayedLettuceAbilityCards() : null;
            if (shown != null) foreach (var c in shown) { var e = c == null ? null : c.GetEntity(); if (e != null) ids.Add(e.GetEntityId()); }
            if (ids.Count == 0) ids.AddRange(merc.GetLettuceAbilityEntityIDs());
            var abilities = new List<Entity>();
            foreach (var id in ids)
            {
                var a = gs.GetEntity(id);
                if (a == null || a.IsLettuceEquipment()) continue;     // the equipment is read on its own
                if (id == queued) abilities.Insert(0, a); else abilities.Add(a);
            }
            foreach (var a in abilities)
            {
                var ability = a;
                Add(MercBattle.Describe(ability, ability.GetEntityId() == queued), () => Choose(ability));
            }
            if (queued != 0)
            {
                var q = gs.GetEntity(queued);
                var label = Str.Join(Str.Word("GLOBAL_CANCEL"), q == null ? null : Str.Clean(q.GetName()));
                Add(label, () => { Log.Info("mercenaries: cancel " + label); InputManager.Get().CancelSelectedLettuceAbilityForEntity(m_merc); });
            }
            var equipment = merc.GetEquipmentEntity();
            if (equipment != null)
            {
                string text = null;
                try { text = equipment.GetCardTextInHand(); } catch { }
                var said = Str.Join(Str.Clean(equipment.GetName()), Str.Clean(text));
                Add(said, () => Say(said));
            }
            var ready = ReadyText();
            if (ready.Length > 0) Add(ready, () => { Log.Info("mercenaries: " + ready); InputManager.Get().DoEndTurnButton(); });
        }

        // what the game's button says now (Ready!, Fight!, "2/3 Played" ...)
        static string ReadyText()
        {
            var button = EndTurnButton.Get();
            if (button == null || button.m_MyTurnText == null) return "";
            return Ui.ShownText(button.m_MyTurnText.Text);
        }

        void Choose(Entity ability)
        {
            var gs = GameState.Get();
            // the game takes an ability only while the tray shows its mercenary's abilities
            if (ZoneMgr.Get().GetLettuceAbilitiesSourceEntity() != m_merc) ZoneMgr.Get().DisplayLettuceAbilitiesForEntity(m_merc);
            if (!gs.IsValidOption(ability))
            {
                // why not, as the game says it (cooldown, passive ...)
                int cooldown = ability.GetTag(GAME_TAG.LETTUCE_CURRENT_COOLDOWN);
                var why = cooldown > 0 ? Str.Game("GAMEPLAY_PlayErrors_REQ_NOT_IN_COOLDOWN", cooldown) : Str.Join(Str.Clean(ability.GetName()), Str.Unavailable);
                Say(why);
                return;
            }
            Combat.WhenReady(this, ability, () =>
            {
                Log.Info("mercenaries: " + m_merc.GetName() + " uses " + ability.GetName());
                InputManager.Get().DoNetworkResponse(ability);
                // a target to choose: the target menu takes over (next frame)
                if (gs.IsInTargetMode() || gs.IsInSubOptionMode()) return;
                Say(Str.Join(Str.Clean(ability.GetName()), Core.Speech.S(Core.K.OPTIONS_MENU_CHECKBOX_CHECKED)));
            });
        }
    }
}
