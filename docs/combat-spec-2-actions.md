# Combat spec, part 2: player actions in a match

This spec describes how Hearthstone Access (HSA) lets the player act during a traditional match: playing cards, placing minions, attacking, targeting, choices, the mulligan, ending the turn and the end of the game. It describes behaviour only. HSA has no license, so none of its code is reproduced here.

It follows part 1 (`combat-spec-1-navigation-reading.md`), which covers focus, zone keys, Tab cycling, card lines, the information keys and help. Things already described there are only referenced here as "(part 1, §n)".

Notation is the same as part 1:
- `LK.X` is HSA's localization key; its string tag is `ACCESSIBILITY_X` in `Strings/enUS/ACCESSIBILITY.txt` unless another tag is given.
- Where HSA reuses a game string, the game tag is named (GAMEPLAY.txt / GLOBAL.txt).
- `{Key}` in a string is the spoken key name (part 1, notation).

Scope: constructed, arena, solo and adventure matches. Battlegrounds and Mercenaries use other code paths and are not covered.

---

## 0. How HSA performs actions: the virtual mouse

HSA performs almost every action with a fake mouse, not by calling game APIs. Understanding this is necessary to read the rest of the spec, and it is the main thing a reimplementation should replace.

### 0.1 Mechanism

- HSA inserts its own input source at the front of the game's input list (`InputCollection`), ahead of the real mouse. The game reads the mouse position and the mouse buttons from it.
- Each frame (in `LateUpdate`) HSA pushes the position it wants and the button states. A "click" sets the button to down for exactly one frame. The next frame it is up again. So the game sees mouse-down on frame N and mouse-up on frame N+1, both at the current position.
- Positions are world positions projected to the screen with `Camera.main.WorldToScreenPoint` (z set to 0). "Hiding" the mouse means position (0,0).
- Left click: the game runs `InputManager.HandleLeftMouseDown` then `HandleLeftMouseUp`. Right click: `InputManager.HandleRightMouseDown/Up`, which ends in `HandleRightClick` (cancel).
- The game's own keyboard hotkeys are switched off while accessibility is on: `UniversalInputManager` hands keyboard handling to HSA instead of `InputManager.HandleKeyboardInput`.
- HSA patches `InputManager` so that hovering does not activate "interactable" board objects while accessibility is on (one branch in the hover code).

### 0.2 What a click does in the game (vanilla flow HSA relies on)

| Mouse-up lands on | Game state | Game call chain |
|---|---|---|
| A card in your hand (via its `CardStandIn`) | main option mode, no held card | `HandleClickOnCard` → `GrabCard`: the card becomes the **held card** (`InputManager.GetHeldCard()`), exactly like click-to-pick-up for a sighted player. Grabbing happens even if the card is not playable (zone server tag is HAND). |
| Anything, while a card is held | response mode OPTION, NONE or OPTION_REVERSE_TARGET | `DropHeldCard` (see §3.4) |
| A card in play (minion, hero, hero power, location, weapon) | main option mode | `HandleClickOnCardInBattlefield` → `DoNetworkResponse(entity)` |
| A card in play or in hand | target mode | `HandleClickOnCardInBattlefield` → `DoNetworkResponse(target)` → `DoNetworkOptionTarget`; clicking the source itself cancels |
| A sub-option card | SUB_OPTION | `HandleClickOnSubOption(entity)` |
| A choice card | CHOICE | `HandleClickOnChoice(entity)` → `DoNetworkChoice` |
| A mulligan card | mulligan active | `MulliganManager.ToggleHoldState(actor)` |

`DoNetworkResponse(entity, checkValidInput, desiredKeyword)` dispatches on `GameState.GetResponseMode()`:
- OPTION → `DoNetworkOptions`: finds the POWER option whose main id is the entity and whose play error is valid (and whose `PowerKeyword` matches `desiredKeyword` if given, e.g. TRADEABLE, FORGE, PREPARE, DISGUISED, INTERACTABLE_OBJECT). Then `GameState.SetSelectedOption(i)` and:
  - option has playable sub-options → `GameState.EnterSubOptionMode()` + `ChoiceCardMgr.ShowSubOptions(card)` ("Choose One", titans, starship launch);
  - no targets → `GameState.SendOption()`;
  - targets and the entity has tag INTERACTABLE_OBJECT (not Battlegrounds) → reverse target mode;
  - targets otherwise → `GameState.EnterOptionTargetMode(entity)`.
  - No matching option → `PlayErrors.DisplayPlayError(GameState.GetErrorType(entity), GetErrorParam(entity), entity)`.
- SUB_OPTION → `DoNetworkSubOptions`: `SetSelectedSubOption(i)`, then `SendOption()` or target mode.
- OPTION_TARGET → `DoNetworkOptionTarget`: if `GetSelectedNetworkSubOption().IsValidTarget(id)`, `SetSelectedOptionTarget(id)` + `SendOption()`; otherwise a play error for that target.
- OPTION_REVERSE_TARGET → `DoNetworkOptionReverseTarget` (same, plus `InteractableObjectDropped`).
- CHOICE → `DoNetworkChoice`: toggles the entity in the chosen list; sends at once if the choice is single.

### 0.3 Error speech

HSA patches `GameplayErrorManager.DisplayMessage(message)` to also speak `message` (spoken as the gameplay screen, so it obeys focus gating). Every play error goes through it: `PlayErrors.DisplayPlayError` builds the text from game tag `GAMEPLAY_PlayErrors_<ErrorType>` (for example `GAMEPLAY_PlayErrors_REQ_ENOUGH_MANA` "Not enough Mana", `…REQ_MINION_CAP` "You cannot have any more minions", `…REQ_NOT_EXHAUSTED_ACTIVATE` "That character already attacked") and calls `DisplayMessage`. REQ_YOUR_TURN and NONE give an empty text, so nothing is said. A rejected option says `GAMEPLAY_ERROR_PLAY_REJECTED` "Too late! Your turn is over.".

HSA itself never checks validity before clicking. It lets the game refuse and relies on this error speech.

### 0.4 "Try Again"

Only for Enter in main option mode (§3.1): before clicking, HSA compares `InputManager.GetMousedOverCard()` with the focused card. If they differ, it says `LK.GAMEPLAY_TRY_AGAIN` "Try Again" (spoken with no source, so it bypasses focus gating), forces `InputManager.SetMousedOverCard(focusedCard)`, and **still clicks**. So the player may hear "Try Again" and the action happens anyway, or nothing happens and they press Enter again.

### 0.5 Timing guard

HSA patches `GameState.CanProcessPowerQueue` so the power queue is not processed while HSA's play describer or power-task describer is still speaking (part 4). This slows the game to the speech. It is relevant here because the next options (and so the next valid actions) arrive only after the description of the previous action is spoken.

---

## 1. Key map during a match

Unless noted, a key has no modifier: HSA keys require the exact modifier set (Shift, Ctrl, Alt each either required or forbidden). Keypad Enter counts as Enter everywhere.

### 1.1 Global keys (work over any screen)

| Key | Action |
|---|---|
| F1 | Help for the focused popup or screen (part 1, §5) |
| Escape | Toggle the game menu (`BnetBar.ToggleGameMenu`); closes the friends list or options menu first if open (§10) |
| F4 | Social (friends) menu |
| F8 | Toggle accessibility on/off |
| F11 / F12 | Speech rate down / up |

### 1.2 Gameplay keys

| Key | Name in HSA | States | Action | Where |
|---|---|---|---|---|
| C, B, G, S, Shift+S, V, F, R, Shift+R, W, Shift+W | zone keys | browsing states, target mode, hidden choices, rewind | jump to a zone | part 1, §2.2 |
| Left / Right | prev/next item | browsing; **placement**; lists | move in zone; **move summon slot**; move in choice/mulligan list | part 1, §2.4; §3.3; §5, §7 |
| Home / End | first/last item | same | first/last slot or item | same |
| 1–9, 0 | number keys | browsing; placement | jump to slot N (0 = 10); **choose summon slot** | part 1, §2.4; §3.3 |
| Tab / Shift+Tab | next/prev valid item | browsing, target mode; choice mode | cycle valid items; **in choice mode: hide/show choices** | part 1, §2.5; §5.4 |
| Up / Down, Shift+Up / Shift+Down, PgUp / PgDn | card lines | most states | read lines of the focused card | part 1, §2.6 |
| A, Shift+A, D, Shift+D, Shift+C, O, K, I, Y | info keys | most states | mana, deck, hand count, anomalies, stats, tooltips, history | part 1, §2.7 |
| **Enter** | CONFIRM | see §2 | play / attack / use / confirm placement / choose target / choose / confirm mulligan / confirm end turn / continue after game | §3–§9 |
| **Backspace** | BACK | held card, target mode, deck action, reverse target, choice confirmation, history | cancel (right click) | §3.6, §6, §5.5 |
| **Space** | SPACE / MULLIGAN_MARK_CARD | main option mode and opponent's turn; mulligan | open the emote menu on a focused hero; toggle a mulligan card | §10, §7 |
| **E** | END_TURN | main option mode; end-turn confirmation | end turn, asking first if plays remain; confirms the question | §8 |
| **Shift+E** | FORCE_END_TURN | main option mode | end turn without asking | §8 |
| **T** | PERFORM_DECK_ACTION | main option mode (focused hand card); holding a card (minion or non-location) | trade / forge / prepare | §5.7 |
| **Shift+F** | SEND_ALL_MINIONS_TO_FACE | main option mode | every minion that can attacks the enemy hero, one after another | §4.4 |
| **Ctrl+F** | SEND_MINION_TO_FACE | main option mode | the focused minion attacks the enemy hero | §4.5 |
| **Ctrl+Up / Ctrl+Down** | SWITCH_TO_OPPOSING / FRIENDLY_SUMMONING_SIDE | placement of a card with a disguised action | place on the opponent's / your side | §3.3 |
| **R** | REROLL_CHOICE (same key as REROLL_QUEST) | choice mode, sub-option mode | press the reroll button | §5.4 |
| **U / J** | REWIND / REWIND_KEEP | rewind mode | rewind / keep | §5.6 |

Keys that do nothing in a match: Alt combinations, Shift+Home/End, Delete.

Order of checks per frame (top to bottom, part of the frame loop in §2.3): the I key first (all phases), then the phase switch. Inside PLAYING, the auto states, then mouse update, then the end-turn confirmation, then emotes, then the per-state handlers.

---

## 2. The gameplay state machine

### 2.1 Phases

Recomputed every frame from GameState (part 1, §1.1): MULLIGAN, GAME_OVER, WAITING_FOR_GAME_TO_START, PLAYING. One addition: a flag "in the beginning choose-one" is set whenever a friendly choice is shown (§5.2) and cleared when the mulligan ends. While it is set, the mulligan phase is treated as PLAYING, so a "choose one" mulligan (game option MULLIGAN_IS_CHOOSE_ONE, for example hero or treasure picks) is handled as a choice (§5), not as a mulligan list.

### 2.2 States inside PLAYING

Recomputed every frame, **first match wins**. Before the list, these flags are updated:
- the held card is read from `InputManager.GetHeldCard()`;
- the end-turn confirmation flag is cleared if `!GameState.IsInMainOptionMode()`;
- the deck-action flag is cleared when nothing is held; it is set when a card becomes held after T was pressed (and the held card still allows a deck action);
- if a card is held and the response mode last frame was OPTION_REVERSE_TARGET, the reverse-drag flag is set.

| # | State | Condition | Handled keys (in this order) |
|---|---|---|---|
| 1 | OPPONENT_TURN | `!GameState.IsFriendlySidePlayerTurn()` (also clears both send-to-face flags) | emotes (Space); card lines, info keys, zone arrows/numbers, Tab, zone keys, history |
| 2 | ALL_MINIONS_TO_FACE | Shift+F loop running | none (§4.4) |
| 3 | MINION_TO_FACE | Ctrl+F sequence running | none (§4.5) |
| 4 | PERFORMING_DECK_ACTION | deck-action flag | Enter, Backspace (§5.7) |
| 5 | REVERSE_TARGET_DRAGGING_CARD | reverse-drag flag (reset right after, so it is re-evaluated each frame) | Enter, Backspace (§6.5) |
| 6 | CONFIRMING_END_TURN | confirmation flag | Enter or E confirm; any other key cancels (§8) |
| 7 | BROWSING_HISTORY | history log open | history keys only (part 1) |
| 8 | SUMMONING_MINION | held card is a minion | card lines, info keys, placement, T |
| 8 | POSITIONING_LOCATION | held card is a location | card lines, info keys, placement (no T) |
| 8 | PLAYING_CARD | held card is anything else **and** main option mode | info keys, "play card" confirm, T; target mode if the game entered it |
| – | (unchanged) | held card is neither minion nor location and not main option mode | the state stays what it was last frame (this is how a held targeted spell stays in PLAYING_CARD after target mode starts) |
| 9 | MAIN_OPTION_MODE | `IsInMainOptionMode()` | emotes; card lines, info keys; Shift+F, Ctrl+F; zone arrows/numbers, Tab, zone keys; Enter, T; E, Shift+E; history |
| 10 | SUB_OPTION_MODE | `IsInSubOptionMode()` | info keys; the sub-option list (§5.3) |
| 11 | RewindMode | `IsInChoiceMode()` and the rewind UI is showing | info keys, zone keys, zone arrows, card lines, U, J |
| 11 | CHOICE_MODE | choice mode and (choice type is TARGET or friendly choice cards are shown) | info keys; the choice list (§5); if the list cannot handle it (target choice, no list), target-mode handling (§6) |
| 11 | CHOICE_MODE_CHOICES_HIDDEN | choice mode otherwise | info keys, zone keys, zone arrows, card lines; Tab shows the choices again |
| 12 | TARGET_MODE | `IsInTargetMode()`, or response mode OPTION_TARGET or OPTION_REVERSE_TARGET | info keys; target handling (§6) |
| 13 | UNKNOWN | anything else (network gap between actions/turns) | as OPPONENT_TURN, minus emotes |
| – | WAITING | set once when the mulligan ends, until the next recompute | as UNKNOWN |

`m_prevState` (last frame's state) and the previous response mode are kept; several announcements fire only "on the first frame of a state" by comparing them.

When the state leaves CHOICE_MODE or SUB_OPTION_MODE and the friendly choices are no longer shown (`ChoiceCardMgr.IsFriendlyShown()` false), the choice list is discarded.

### 2.3 Frame loop in PLAYING

1. On the first frame after a reconnect: say `LK.GAMEPLAY_YOUR_TURN` (game tag `GAMEPLAY_YOUR_TURN` "Your Turn") or `LK.GAMEPLAY_OPPONENT_TURN` "Opponent's turn", re-register the game-over listener.
2. Recompute the state (§2.2). If the focused card left its zone, drop focus (part 1, §2.1).
3. If the state is ALL_MINIONS_TO_FACE, MINION_TO_FACE, PERFORMING_DECK_ACTION or REVERSE_TARGET_DRAGGING_CARD: run only that handler and stop.
4. Mouse update (part 1, §4): nothing if a card is held; else onto the focused card; else hidden.
5. CONFIRMING_END_TURN: Enter or E ends the turn and stops. Any other key-down cancels the confirmation silently, the state is recomputed, and **the same key is then processed by the new state** (for example pressing C cancels and also jumps to the hand).
6. In OPPONENT_TURN and MAIN_OPTION_MODE: if the emote menu is open or Space opens it, stop (§10).
7. Run the handlers of the state (table above).

### 2.4 Transition overview

```
MAIN_OPTION_MODE
  Enter on hand minion      -> SUMMONING_MINION -(Enter)-> [server] or TARGET_MODE (battlecry) or SUB_OPTION_MODE (choose one)
  Enter on hand location    -> POSITIONING_LOCATION -(Enter)-> [server]
  Enter on hand spell/weapon/hero
                            -> PLAYING_CARD -(no target: Enter)-> [server] or SUB_OPTION_MODE
                                            -(targeted: game enters target mode while held)-> target handling -(Enter on target)-> [server]
  Enter on own minion/hero  -> TARGET_MODE (attack) -(Enter on target)-> [server]
  Enter on hero power/location in play -> [server] or TARGET_MODE or SUB_OPTION_MODE
  Enter on interactable object -> TARGET_MODE (reverse) -(Enter on hand card)-> REVERSE_TARGET_DRAGGING_CARD -(Enter)-> [server]
  T on tradeable hand card  -> PERFORMING_DECK_ACTION -(Enter)-> [server]
  E                         -> [end turn] or CONFIRMING_END_TURN -(Enter/E)-> [end turn]
  Shift+F / Ctrl+F          -> ALL_MINIONS_TO_FACE / MINION_TO_FACE -> back
Any held/target/sub state  -(Backspace)-> right click cancel -> MAIN_OPTION_MODE
[server] = option sent; state becomes UNKNOWN until the next options packet, then MAIN_OPTION_MODE
Server sends a choice (Discover etc.) -> CHOICE_MODE <-(Tab)-> CHOICE_MODE_CHOICES_HIDDEN
Turn passes                -> OPPONENT_TURN
```

---

## 3. Enter on a hand card: playing cards

### 3.1 Enter in MAIN_OPTION_MODE (any focused card)

Order inside the main-option handler: Shift+F, Ctrl+F (each returns), zone arrows/numbers, Tab, zone keys, then:
- **Enter:** if nothing is focused, nothing happens. Otherwise the "Try Again" check (§0.4) and a left click at the mouse, which is already on the focused card (for a hand card, on its mana gem). There is **no validity check**; an invalid card produces the game's play error.
- **T:** §5.7.

What the click does depends on the card (§0.2). For a hand card it grabs the card; the next frame the held card decides the state:

| Held card | State | First-frame speech |
|---|---|---|
| Minion | SUMMONING_MINION | the placement query (§3.3) |
| Location | POSITIONING_LOCATION | the placement query, with the **minion** strings ("Summon …?") |
| Spell, weapon, hero card, other | PLAYING_CARD | `LK.GAMEPLAY_QUERY_PLAY_CARD` "Play card?", unless the card needs a target (`GameState.EntityHasTargets(entity)`); then nothing, because "Choose a target" follows |

Focus is not changed by grabbing, so Up/Down still read the held card in SUMMONING_MINION and POSITIONING_LOCATION. In PLAYING_CARD card lines are not handled.

### 3.2 Play vs. position vs. target: who decides

HSA does not decide. The type of the held card selects the state as above. Whether the card then needs a target, a sub-option choice or nothing is decided by the game when the card is dropped (minions, locations, weapons) or while it is held over the board (targeted spells), from the options packet (§0.2).

### 3.3 Minion and location placement

**Entering.** On the first frame of SUMMONING_MINION or POSITIONING_LOCATION (previous state was neither), the side is set to friendly and the slot to the zone's last position. The zone is `ZoneMgr.FindZoneOfType<ZonePlay>(side)`. "Last position" is the card count + 1 (`Zone.GetLastPos()`), meaning "to the right of every minion". Then the query is spoken.

**Slots.** Slot n means "insert before the minion now at slot n"; slot count+1 is the far right. Slots are 1-based.

**Keys** (only when the zone is not empty; with an empty zone the keys do nothing):

| Key | Effect |
|---|---|
| Left / Right | slot −1 / +1, clamped to 1…last. If the slot did not change, **nothing is spoken**. |
| Home | slot 1 |
| End | last slot |
| 1–9, 0 | slot min(N, last), with 0 = 10 |
| Ctrl+Up | only if the held card has a disguised action (`Entity.HasDisguisedAction()`): switch to the opposing side; slot reset to that zone's last position and the query spoken |
| Ctrl+Down | same, back to the friendly side |
| Enter | left click (drop), see §3.4 |
| Backspace | cancel (§3.6) |

**Query speech** (spoken on entering, on every slot change, and for Home/End/numbers even if unchanged). Friendly side / opposing side keys:

| Condition (checked in order) | Friendly | Opposing |
|---|---|---|
| zone empty | `LK.GAMEPLAY_QUERY_SUMMON_MINION` "Summon?" | `…_OPPOSING_SIDE` "Summon for opponent?" |
| slot = last | `LK.GAMEPLAY_QUERY_SUMMON_MINION_AT_THE_RIGHT` "Summon at the right?" | "Summon at the right for opponent?" |
| slot = 1 | `…_AT_THE_LEFT` "Summon at the left?" | "Summon at the left for opponent?" |
| otherwise | `…_BETWEEN` "Summon between {0} and {1}?" with the names (`Entity.GetName()`) of the minions at slots n−1 and n | "Summon between {0} and {1} for opponent?" |

With exactly one minion there are only two slots, "left" and "right". The tutorial's "summoning" listeners for the held card fire after each query.

**Mouse position** (set every frame, because the normal mouse update is skipped while a card is held):
- empty zone: centre of the zone's collider bounds;
- slot = last: the right edge of the zone collider (centre x + extent x);
- slot = 1: the left edge (centre x − extent x);
- otherwise: the midpoint between the world positions of the minions at slots n−1 and n.

The game itself turns that mouse x into a slot (`InputManager.PlayZoneSlotMousedOver`: slot width from the zone, counted from the left edge of the occupied area), then `ZoneMgr.PredictZonePosition` and `GameState.SetSelectedOptionPosition`. For disguised cards the side is chosen by the game from the hit point (z < −4 means friendly).

**Magnetic, colossal and similar:** HSA has no special handling. The chosen slot is passed to the game as for any minion; whatever the game does with that slot (for example magnetizing onto the minion to the right) happens without any extra speech. (Inferred from the code: no magnetic or colossal checks exist in the action code.)

### 3.4 Dropping (Enter while holding)

The click's mouse-up calls `InputManager.DropHeldCard()` (response mode OPTION/NONE/OPTION_REVERSE_TARGET). For a card from hand:

| Held card | Game path | Result |
|---|---|---|
| In the deck-action area and tradeable/forgeable/prepareable | `DropHeldTradeable/Forgeable/Prepareable` → `DoNetworkResponse(entity, true, TRADEABLE/FORGE/PREPARE)` | trade etc. (§5.7); on failure REQ_ENOUGH_MANA or REQ_TRADEABLE/REQ_FORGE/REQ_PREPARE error |
| Minion, location, weapon | `DropHeldMinionLikeCard`: needs a raycast hit on the board layer; computes the slot; board full (slot < 0) → REQ_MINION_CAP (or REQ_DISGUISED on the other side); else `DoNetworkResponse(entity[, DISGUISED])` | sends the play, or enters target mode (battlecry), or sub-option mode (choose one). On success the minion is put into play locally at once and mana is predicted as spent. If it then needs a target, the game remembers it as the battlecry source. |
| Spell, hero, other | `DropHeldSpellLikeCard`: if it has targets (and is not reverse targeting) the drop is cancelled (the card returns); needs a board hit; if `!HasResponse(entity)` → play error; else `DoNetworkResponse` | sends the play or enters sub-option mode |

### 3.5 PLAYING_CARD in detail

Each frame:
1. If the virtual mouse is below the friendly hero on screen (mouse y < screen y of `GetFriendlySidePlayer().GetHeroCard()`), move it to the centre of the friendly battlefield zone. On the first frame the mouse is still on the card's mana gem in the hand, so this lifts the card over the board.
2. If `GameState.IsInTargetMode()`: run target handling (§6). This happens for targeted spells: the game's `HandleUpdateWhileHoldingCard`, seeing a held non-minion with targets over the battlefield, calls `DoNetworkResponse` itself and enters target mode while the card is still held.
3. Otherwise: on the first frame, "Play card?" unless the card needs a target; then Enter clicks (drop, §3.4), Backspace cancels.
4. T: deck action for the held card (§5.7).

Targeted spell flow, as heard: Enter on the spell → (nothing) → "Choose a target" (and focus is dropped because it was on a hand card) → Tab or zone keys to a target → Enter. The click goes to the target, the game calls `DoNetworkOptionTarget` and clears the held card.

### 3.6 Cancelling (Backspace)

In SUMMONING_MINION, POSITIONING_LOCATION, PLAYING_CARD (not in target mode), PERFORMING_DECK_ACTION and REVERSE_TARGET_DRAGGING_CARD, Backspace is a right click, then the mouse is hidden. The game's `HandleRightClick` → `CancelOption`: cancels main option mode, then target mode, sub-option mode, reverse target mode (refused while a card is held, unless on timeout), and finally drops the held card as cancelled (it returns to hand). Nothing is spoken by HSA; the game plays a cancel sound in target mode.

### 3.7 Failure messages summary

| Situation | Spoken (game tag) |
|---|---|
| Not enough mana (on drop) | `GAMEPLAY_PlayErrors_REQ_ENOUGH_MANA` "Not enough Mana" |
| Board full | `GAMEPLAY_PlayErrors_REQ_MINION_CAP` "You cannot have any more minions" |
| Any other unmet requirement | `GAMEPLAY_PlayErrors_<ErrorType>` from `GameState.GetErrorType(entity)` |
| Option rejected by the server | `GAMEPLAY_ERROR_PLAY_REJECTED` "Too late! Your turn is over." |
| Card not playable but grabbed | heard as the placement / "Play card?" query first; the error comes only after Enter |

---

## 4. Attacking

### 4.1 Choosing the attacker

In MAIN_OPTION_MODE, focus your minion (B, arrows, Tab) or your hero (V) and press Enter. The click goes to the card in play → `HandleClickOnCardInBattlefield` → `DoNetworkResponse` → `DoNetworkOptions`. If the attacker has targets, the game enters target mode (`GameState.EnterOptionTargetMode`), shows the attack arrow and taunt highlights. HSA then announces "Choose a target" (§6.1).

If the character cannot attack, the option is missing and the game's play error is spoken (for example REQ_NOT_EXHAUSTED_ACTIVATE "That character already attacked", REQ_ATTACK_GREATER_THAN_0, "Minions cannot attack the turn they are played"). Your weapon is not an attacker: attacking with a weapon means attacking with the hero.

### 4.2 Choosing the target and confirming

Target mode (§6): Tab cycles valid targets (enemy first, if the source effect is classified as hostile; for an attack the source is the minion, so usually friendly first), zone keys and arrows browse, Enter clicks the focused target.

### 4.3 Cancelling

- Backspace (right click → `CancelOption` → `CancelTargetMode`).
- Pressing Enter on the attacker itself (game rule: clicking the selected source cancels).
- The turn timer running out (the game cancels with `CancelOption(timeout)`).

### 4.4 Shift+F: all minions attack the enemy hero

Only in MAIN_OPTION_MODE. Checks, in order:
1. No friendly minions: `LK.GAMEPLAY_SEND_TO_FACE_NO_MINIONS` "You don't have any summoned minions".
2. `!opponentHero.CanBeAttacked()`: `…_HERO_CANT_BE_ATTACKED` "Your opponent's Hero can't be attacked".
3. `!opponentHero.CanBeTargetedByOpponents()`: `…_HERO_CANT_BE_TARGETED` "Your opponent's Hero can't be targeted".
4. Valid attackers empty: `…_HERO_NO_VALID_ATTACKERS` "None of your minions can attack your opponent's Hero".

**Valid face attackers:** walk the options packet (`GameState.GetOptionsPacket().List`); for each POWER option whose `Main.IsValidTarget(enemyHeroEntityId)`, take the entity `Main.ID`; keep it if it is a minion, has no usable titan abilities (`HasUsableTitanAbilities()` false), and is in your battlefield zone. Order is the options packet order.

**Loop** (state ALL_MINIONS_TO_FACE; no other keys handled while it runs). The delay is the option `ACCESSIBILITY_AUTO_ATTACK_SPEED` in seconds, 1.0 if it is 0:
- wait until the delay has passed;
- if an attacker is pending: click the enemy hero, clear it, wait;
- else recompute the valid attackers (the options packet is new after each attack); if any, click the first (enters target mode), wait; if none, stop.

The loop also stops when the turn passes. Nothing is spoken by the loop; each attack is described by part 4.

### 4.5 Ctrl+F: the focused minion attacks the enemy hero

Only in MAIN_OPTION_MODE. Checks, in order:
1. Nothing focused: silent.
2. Focused card not in your battlefield zone: `…_NOT_FRIENDLY_MINION` "That is not a friendly minion".
3. Not a minion: `…_NOT_MINION` "That is not a minion".
4. Not a valid face attacker (as above): `…_NOT_VALID_ATTACKER` "That minion can't attack your opponent's hero".
5. Enemy hero can't be attacked / targeted: as in Shift+F.

Then (state MINION_TO_FACE): click the minion, wait 0.1 s, click the enemy hero, and on the next frame return to normal. Note the order of checks differs from Shift+F (the attacker check comes before the hero checks).

---

## 5. Choices: hero power, locations, titans, Choose One, Discover, deck actions

### 5.1 Hero power, locations and other cards in play

Enter on a focused hero power (R) or location in play (B) clicks it → `HandleClickOnCardInBattlefield` → `DoNetworkResponse`:
- no target: the option is sent at once (the game marks a hero power EXHAUSTED and predicts mana);
- targets: target mode, "Choose a target" (§6);
- sub-options: sub-option mode (§5.3).

Several hero powers (part 1, R key) work the same way; the focused one is clicked.

### 5.2 Choice mode (Discover, Adapt, "choose a card" effects, choose-one mulligans)

**Start.** HSA hooks `ChoiceCardMgr.ShowChoiceCards(state, friendly)` and the end of the reveal of friendly choice cards. Each time (so possibly twice for one choice, and again after "show" in §5.4):
1. Throw away any previous list, drop focus (mouse hidden), clear the confirmation flag.
2. Build a list of the choice cards (`state.m_cards`), read with the normal card reader (part 1, §3).
3. Say the title: the choice banner's headline text (`m_choiceBanner.m_headline.Text`, the game's banner, for example the source card's prompt). For a Battlegrounds trinket discover a BG string instead. If there is no banner, an exception is logged and the list is not started (the state then falls through to target handling; quirk).
4. Mark "in the beginning choose-one" (§2.1).
5. Start the list: the first card's line 0 as `LK.MENU_OPTION_FORMAT` "{0} {1} of {2}", for example "Arcane Intellect 1 of 3".
6. If the reroll UI is active (`RerollUIManager` instance enabled): `LK.GAMEPLAY_REROLL_CHOICE_HELP` "Press {R} to reroll".
7. History entry "{title}: {card names}".

**Keys in CHOICE_MODE** (the list handler runs only if the choice is not a TARGET choice, the list exists and is reading; otherwise target handling, §6):

| Key | Effect |
|---|---|
| Left / Right | previous / next card, **no wrap**; silent at the ends |
| Home / End | first / last card |
| Up / Down, Shift+Up/Down, PgUp/PgDn | lines of the focused choice card |
| Enter | move the mouse onto the card and click → `HandleClickOnChoice` → `DoNetworkChoice` (single choice: `GameState.SendChoices()` at once; multi-choice: toggles the card) |
| R | `RerollUIManager.m_rerollButton.TriggerRelease()` (if present) |
| Tab | hide the choices: `ChoiceCardMgr.GetToggleButton().TriggerRelease()` (§5.4) |
| Info keys | as part 1 |

The focused choice card is also the card being read, so the normal mouse update keeps the virtual mouse on it.

There is no Backspace in choice mode (choices cannot be cancelled in the game either).

### 5.3 Sub-option mode ("Choose One" cards, titans, starship launch)

**Start.** HSA hooks the end of `ChoiceCardMgr.ShowSubOptions` (friendly only): throw away the previous list, clear the confirmation flag, drop focus, build a list of `m_subOptionState.m_cards`, say game tag `GAMEPLAY_CHOOSE_ONE` "Choose One" (also for titan abilities), and read the first card ("Option 1 of 2").

**Keys:** the same as choice mode (§5.2), with two differences:
- Tab is **not** intercepted, so Tab and Shift+Tab move through the options **with wrap**.
- Enter clicks the option → `HandleClickOnSubOption(entity)` → `DoNetworkSubOptions` → sent, or target mode (the arrow shows the option's targeting text). The sub-option parent card is dropped.

**No cancel:** Backspace is not handled in SUB_OPTION_MODE, so a started Choose One cannot be backed out of with the keyboard (quirk; the game would cancel on a right click or on a click on the parent card).

How sub-option mode is reached:
- Choose One spell from hand: Enter → PLAYING_CARD "Play card?" → Enter → drop → sub-options.
- Choose One minion: placement → Enter → sub-options.
- Titan in play: Enter on it → sub-options (its abilities). Titans with usable abilities are excluded from Shift+F / Ctrl+F.
- Starship launchpad: `ChoiceCardMgr.ShowSubOptions(card, pieceIds)` opens the starship HUD instead, which HSA makes a popup: says `LK.GAMEPLAY_STARSHIP_HUD_INTRO` "Starship pieces", then a list of the pieces (Left/Right, lines with Up/Down), info keys work, Enter launches (if the launch button is inactive, its text is spoken instead), Backspace aborts. Help: `LK.GAMEPLAY_STARSHIP_HUD_HELP` "Use the arrow keys to read the pieces of the starship, press {Enter} to launch or {Backspace} to abort".

### 5.4 Hiding and showing choices

- Tab in CHOICE_MODE presses the game's Hide button. HSA hooks the hide: says `LK.GAMEPLAY_CHOICES_HIDDEN` "Choices hidden", drops focus, clears the confirmation flag. The state becomes CHOICE_MODE_CHOICES_HIDDEN.
- In CHOICE_MODE_CHOICES_HIDDEN the board can be browsed (zone keys, arrows, card lines, info keys; no Tab cycling) and Tab presses the toggle again. The game's show calls `ShowChoiceCards`, so §5.2 runs again: title and first card are re-announced and the focus goes back to card 1.
- Help: `LK.GAMEPLAY_HIDDEN_CHOICE_HELP` "Use the zone keys to inspect the state of the game board. Press {Tab} to return to the choices".

### 5.5 Multi-step confirmation (Battlegrounds trinkets only)

HSA hooks `ChoiceCardMgr.ChooseBGTrinket`: sets the confirmation flag and says `LK.TUTORIAL_HOGGER_2_5` "Press {Enter} to confirm or {Backspace} to cancel". While waiting: Enter presses `ChoiceCardMgr.GetConfirmButton()` (HSA adds this getter for `m_confirmChoiceButton`); Backspace clears the flag and restarts the list from the current item. Not used in traditional games.

### 5.6 Rewind

Choice mode with the rewind UI showing (`RewindUIManager.IsShowingRewindUI`). On the first frame: `LK.GAMEPLAY_QUERY_REWIND` "Rewind?". U presses `RewindUIManager.m_rewindButton`, J presses `m_keepButton` (HSA makes both fields accessible). Board browsing works. Help: `LK.GAMEPLAY_REWIND_MODE_HELP`.

### 5.7 Deck actions: trade, forge, prepare (T)

**Allowed** when the card is in your hand, `Entity.HasDeckAction()` and not `IsPassable()` (Battlegrounds Duos pass).

**From MAIN_OPTION_MODE** (T with a focused hand card that is allowed; otherwise T is silent):
1. Say the query, chosen in this order: forgeable → `LK.GAMEPLAY_QUERY_FORGE_CARD` "Forge card?"; prepareable → `…PREPARE_CARD` "Prepare card?"; else `…TRADE_CARD` "Trade card?".
2. Click the card (with the Try Again check) and remember "waiting for hold". When the card becomes held and still allows a deck action, the state becomes PERFORMING_DECK_ACTION (the "Summon?"/"Play card?" query is suppressed because the deck-action state wins).

**While holding** (SUMMONING_MINION or PLAYING_CARD; not for locations): T with an allowed held card says the query and switches to PERFORMING_DECK_ACTION.

**PERFORMING_DECK_ACTION**, each frame: move the mouse to the deck-action area: the point of `Board.GetDeckActionArea()`'s collider bounds closest to the held card, shifted right by half the card mesh's width. Enter clicks (drop → the game sees the card in the deck-action area → `DropHeldTradeable/Forgeable/Prepareable`). Backspace cancels (§3.6). Help: `LK.TUTORIAL_HOGGER_2_5` "Press {Enter} to confirm or {Backspace} to cancel".

The help for a focused valid hand card adds "Press {T} to trade / forge / prepare this card" (`LK.GAMEPLAY_TRADE_CARD_HELP` etc.; part 1, §5.2).

---

## 6. Target mode

### 6.1 Announcement

On each frame of target handling, "Choose a target" (`LK.GAMEPLAY_CHOOSE_TARGET`) is said if:
- the "force announce" flag is set (set by HSA's patch at the end of `InputManager.StartPendingChoiceTarget`, so TARGET-type choices and multi-target cards re-announce), or
- the response mode changed since last frame and the new one is not CHOICE.

When it is said, the flag is cleared, and if the focused card is in your hand, focus is dropped **without** hiding the mouse (the hand card that was just played should not stay focused).

The source is never named. Nothing tells the player how many or which targets are valid until they press Tab.

### 6.2 Keys

In this order each frame:

| Key | Effect |
|---|---|
| Zone arrows, Home/End, numbers | move within the current zone (part 1, §2.4) |
| Up/Down etc. | card lines |
| Tab / Shift+Tab | next / previous **valid target**, validity `GameState.IsValidOptionTarget(entity, true)`; candidate order puts the opponent's cards first if the source card's effect is classed as hostile (part 1, §2.5). With no valid target: "You have no valid plays" and focus dropped. |
| Zone keys | as part 1, but C, S and Shift+S do nothing unless `GameState.CanTargetCardsInHand()` |
| Enter | if a card is focused: left click on it (the mouse follows focus); **if nothing is focused, Enter is silent** |
| Backspace | cancel: right click, mouse hidden |

The click on a target runs `HandleClickOnCardInBattlefield` (or `TryHandleClickOnCard` for hand targets when `CanTargetCardsInHand`): if the target is the source itself, cancel; else `DoNetworkResponse(target)` → `DoNetworkOptionTarget`. An invalid target gives the game's targeting play error (for example REQ_TARGET_TAUNTER, REQ_MINION_TARGET), spoken.

There is no "Try Again" check in target mode.

### 6.3 Ways into target mode

| Source | How |
|---|---|
| Attack | Enter on own minion / hero (§4) |
| Hero power / location with a target | Enter on it (§5.1) |
| Targeted spell | held over the board in PLAYING_CARD (§3.5) |
| Battlecry minion / targeted location from hand | after the drop (§3.4); cancelling returns the minion to hand (`ResetBattlecrySourceCard` undoes the local zone change and refunds mana) |
| Choose One option with a target | after choosing the option (§5.3) |
| TARGET-type choice (choose a character on the board as a "choice") | choice mode with choice type TARGET (`GameState.GetFriendlyEntityChoices().ChoiceType == CHOICE_TYPE.TARGET`); the click becomes `DoNetworkChoice` |
| Interactable object | reverse target (§6.5) |

### 6.4 Help

`LK.GAMEPLAY_CHOOSE_TARGET_HELP` "Use {Tab} or the Zone keys to go through your targets. Press {Enter} to choose a target or {Backspace} to cancel".

### 6.5 Reverse target (dragging a hand card onto a board object)

Used by board objects with tag INTERACTABLE_OBJECT (outside Battlegrounds): the object is clicked first, then a card is "dropped" on it.
1. Enter on the object in MAIN_OPTION_MODE → `DoNetworkOptions` → `EnterOptionReverseTargetMode`. Response mode OPTION_REVERSE_TARGET → state TARGET_MODE → "Choose a target".
2. Tab / C to a valid hand card (validity `IsValidOptionTarget`), Enter → the click grabs it.
3. While held and in reverse mode → REVERSE_TARGET_DRAGGING_CARD: each frame the mouse is moved onto `GameState.GetInteractableObject()`; on the first frame `LK.TUTORIAL_HOGGER_2_5` "Press {Enter} to confirm or {Backspace} to cancel".
4. Enter clicks → `DropHeldCard` → `DoNetworkOptionReverseTarget(heldEntity)` → sent. Backspace cancels (the held card returns; reverse mode stays until cancelled again).

If `GetInteractableObject()` is null, this state handles no keys at all.

---

## 7. Mulligan

### 7.1 Announcements before the list

In order of time (hooks in `MulliganManager`):

| When | Spoken |
|---|---|
| Starting cards are dealt (`DealStartingCards`, before the deal animation) | PvP only: `LK.GAMEPLAY_VS_PLAYER_ANNOUNCEMENT` "You're playing against {0}, the {1}" (opponent `Player.GetName()`, `GameStrings.GetClassName(opponentHero.GetClass())`), also added to history. Then always `LK.GAMEPLAY_YOU_START_WITH_N_CARDS` "You start with {0} card(s)". |
| Coin flip animation | `LK.GAMEPLAY_YOU_GO_FIRST` "You go first" or `LK.GAMEPLAY_OPPONENT_GOES_FIRST` "Your opponent goes first" |
| First mulligan ever (option HAS_SEEN_MULLIGAN false, not spectator, not choose-one) | instead of the innkeeper quote, five narrated lines, each awaited: `LK.IN_GAME_TUTORIAL_MULLIGAN_FIRST_TIME` "Every normal game starts with the mulligan phase", `…_B` "In this phase, both players can replace cards in their starting hand with random ones from their deck", `…_C` "Use the arrow keys or {Tab} to go through your starting hand", `…_D` "If you see a card you don't like, press {Space} to mark it for replacement", `…_E` "Once you've marked all cards you'd like to replace, press {Enter} to draw new ones and start the game" |
| Choosing starts (after the cards are spread) | game tag `GAMEPLAY_MULLIGAN_STARTING_HAND` "Starting Hand", game tag `GAMEPLAY_MULLIGAN_SUBTITLE` "Keep or Replace Cards", then the first card "{name} 1 of {n}" |

The mulligan timer speaks `LK.GAMEPLAY_N_SECONDS_REMAINING` "{0} second(s) remaining" once, when its displayed seconds drop to 10 (spoken as a notification, ignoring focus).

HSA suppresses the Battlegrounds reroll tooltip and similar attention grabbers while accessibility is on.

### 7.2 Keys

The list contains the starting cards (`m_startingCards`, including a bonus card if dealt), read with the normal card reader. Each frame of the MULLIGAN phase:

| Key | Effect |
|---|---|
| I, O | tooltips (part 1); anomalies `LK.GAMEPLAY_NO_ANOMELIES` / "Anomaly: {0}; {1}" (part 1, §2.7) |
| Space | move the mouse onto the focused card and click (the game toggles it with `MulliganManager.ToggleHoldState(actor)`); flip HSA's own "marked" flag for that card; say `LK.GAMEPLAY_MULLIGAN_WILL_BE_REPLACED` "Will be replaced" or `…WILL_NOT_BE_REPLACED` "Will not be replaced" |
| Enter | press the mulligan confirm button (`NormalButton.TriggerRelease()` → the game's `OnMulliganButtonReleased` → `BeginDealNewCards`); then stop list navigation and hide the mouse until the mulligan ends |
| Left / Right, Home / End | move through the cards, no wrap; "{name} {i} of {n}" |
| Tab / Shift+Tab | next / previous card **with wrap** |
| Up / Down etc. | lines of the focused card |
| F1 | `LK.GAMEPLAY_MULLIGAN_HELP` "Use the arrow keys to go through your starting cards. Use {Space} to mark the cards you'd like to replace. Press {Enter} once you're done" |

After every non-Space, non-Enter key, the mouse is moved onto the focused card (so hovering shows the BigCard and tooltips).

Quirks:
- The marked state is HSA's own copy. It is never re-read from the game (the game keeps `m_handCardsMarkedForReplace`), so if the game refuses a toggle, HSA's words are wrong.
- Moving onto a card does not say whether it is marked.

### 7.3 After confirming

- When the friendly mulligan is sent and the opponent is still choosing, the game's `WaitForOpponentToFinishMulligan` hook says `LK.GAMEPLAY_WAITING_FOR_OPPONENT` "Waiting for opponent".
- When the mulligan ends (`MulliganManager.EndMulligan` hook): compare the original list with the hand. If any card was replaced: history "You discarded {0}" (`LK.GAMEPLAY_PLAYER_DISCARDED_CARDS`) and speech + history `LK.GAMEPLAY_PLAYER_DREW_CARDS` "You drew {0}" with the names of the new cards (the coin excluded, `CosmeticCoinManager.IsCoinCard`). Names are grouped like "1 Bloodfen Raptor and 1 Murloc Raider". Nothing is said if all cards were kept.
- Then the game starts for HSA: phase PLAYING, state WAITING, the game-over listener registered.
- When the coin is summoned into the hand: `LK.GAMEPLAY_YOU_GET_THE_COIN` "You get the coin".

---

## 8. Turns and ending the turn

### 8.1 E: end turn with confirmation

Only in MAIN_OPTION_MODE and only if `GameState.IsInMainOptionMode()`:
- **Shift+E:** end the turn at once.
- **E:**
  - if `EndTurnButton.Get().HasNoMorePlays()` (the options packet's only valid option is end turn, or the game entity overrides it): end the turn at once;
  - otherwise say one question, chosen in this order:
    1. a friendly minion in play has a response (`GameState.HasResponse(entity)`), or the friendly hero has one: `LK.GAMEPLAY_QUERY_END_TURN_WHEN_CAN_ATK` "You can still attack. Are you sure?";
    2. a friendly location in play has a response: `…WHEN_CAN_USE_LOCATION` "You can still activate locations. Are you sure?";
    3. the hero power (`Player.GetHeroPower()`) has a response: `…WHEN_CAN_USE_HERO_POWER` "You can still use your Hero Power. Are you sure?";
    4. else `…WHEN_VALID_PLAYS` "You still have valid plays. Are you sure?";

    then drop focus (mouse hidden) and enter CONFIRMING_END_TURN.
- **In CONFIRMING_END_TURN:** Enter or E ends the turn. Any other key cancels silently and is then handled normally (§2.3 step 5). The confirmation is also cancelled if main option mode ends. Help: `LK.GAMEPLAY_CONFIRM_END_TURN_HELP` "Press {Enter} or {E} to end your turn. Press any other key to cancel".

"End the turn" = `InputManager.DoEndTurnButton()` (which itself requires `PermitDecisionMakingInput`, a non-blocked response packet and an enabled, unblocked end-turn button; then it finds the END_TURN or PASS option, `SetSelectedOption`, `SendOption`), then drop focus. Nothing is spoken by HSA here; part 4 says "Turn ended".

### 8.2 Turn announcements (summary; details in part 4)

| When | Spoken |
|---|---|
| Your turn banner appears (`TurnStartManager`, where the "your turn" sound plays) | `GAMEPLAY_YOUR_TURN` "Your Turn", then `LK.GAMEPLAY_PLAYER_TURN_START_READ_MANA` "You have {0} mana" (available resources; not if a tutorial disabled the mana counter); on turn 1, the special starting boards (`…SPECIAL_STARTING_PLAYER_BATTLEFIELD` "Your battlefield starts with {0}", `…OPPONENT_BATTLEFIELD`); then descriptions queued while waiting for the banner (for example the card drawn). One utterance, added to history. |
| Opponent's turn reaches the MAIN_READY step | `LK.GAMEPLAY_OPPONENT_TURN` "Opponent's turn" (+ turn-1 boards) |
| A turn reaches MAIN_END | `LK.GAMEPLAY_TURN_ENDED` "Turn ended" |
| After a reconnect | game tag `GLOBAL_RECONNECT_RECONNECTED_HEADER` "Reconnected" (interrupting), then "Your Turn" / "Opponent's turn" |

### 8.3 Turn timer

HSA hooks `TurnTimer.Update`: it rounds `ComputeCountdownRemainingSec()` to whole seconds and, when the value drops to exactly 10 (from a higher value), says `LK.GAMEPLAY_N_SECONDS_REMAINING` "10 seconds remaining" as a notification (ignores focus). Earlier versions warned at 15 and 5; players found that too much. The countdown is the timer of whoever's turn it is, so the warning also comes on the opponent's turn (inferred). There is no key to ask for the remaining time in a traditional game (Battlegrounds has one).

---

## 9. Game end

### 9.1 Game over

HSA registers a game-over listener (`GameState.RegisterGameOverListener`) when the mulligan ends (and again after a reconnect). On game over:
- the phase becomes GAME_OVER (only I and F1 work; F1 says `LK.GAMEPLAY_GAME_OVER_GENERIC` "Game over");
- say the result from `TAG_PLAYSTATE`: WON → `LK.GAMEPLAY_GAME_OVER_WON` "You win"; LOST or CONCEDED → `…LOST` "You lose"; TIED → `…TIED` "You tied"; other → "Game over";
- if the option `ACCESSIBILITY_SAVE_BATTLE_LOGS` is on (not in Battlegrounds), the history log is saved to a text file named "yyyy-M-d H_mm {you} v {opponent}.txt".

The game also cancels any pending action itself (`InputManager.OnGameOver` → `CancelOption`).

### 9.2 End-game screen

- `EndGameScreen.Show` hook: an HSA end-game screen takes focus (so gameplay speech stops being spoken from here on).
- After the screen is set up, the screen's full-screen hitbox (`m_hitbox`) is stored. **Enter presses it** (`TriggerRelease`), which is the game's "click anywhere to continue". F1: `LK.PRESS_KEY_TO_CONTINUE` "Press {Enter} to continue".
- The victory/defeat banner (`EndGameTwoScoop.Show`) says "Press Enter to continue" when it is shown without an XP bar; with an XP bar, after the bar animation, and a level-up says `LK.SCREEN_END_GAME_SCREEN_HERO_LEVEL_UP` "Your Hero reached level {0}" first.
- Later reward screens can give the end-game screen a list of lines to re-read; Up/Down then read them (the last line is current).
- HSA disables the score screen (`GameState.CanShowScoreScreen` returns false) and skips the fixed-rewards step of the end-game flow, because they are not accessible.

---

## 10. Emotes, concede and the game menu

### 10.1 Emotes

In OPPONENT_TURN and MAIN_OPTION_MODE, before anything else:
- If the friendly emote menu is open (`EmoteHandler.AreEmotesActive()`), or the enemy one (`EnemyEmoteHandler.AreEmotesActive()`), all keys go to it.
- Else Space opens one, if a hero in PLAY is focused and the player is not a spectator: your hero → `EmoteHandler.ShowEmotes()`; enemy hero → `EnemyEmoteHandler.ShowEmotes()`.

HSA hooks the show calls and builds a menu with no title:
- friendly: one option per available emote, labelled with the emote's text (`EmoteOption.m_Text.Text`), action `EmoteOption.DoClick`;
- enemy: one option labelled with the current squelch text (for example game tag `GAMEPLAY_EMOTE_SQUELCH` "Squelch"), action `EnemyEmoteHandler.DoSquelchClick` (HSA makes it callable without arguments).

Menu keys: Up/Down (and Tab/Shift+Tab) move, read as "{text} {i} of {n}"; Home/End; Enter **or Space** chooses; Backspace closes (`HideEmotes`). The first option is read when the menu opens. Help is the menu help (`LK.MENU_HELP_WITH_BACK_BUTTON`). Any click on a card also closes an open emote menu (game behaviour).

### 10.2 Game menu and concede

Escape toggles the game menu (`BnetBar.ToggleGameMenu`). HSA makes `GameMenu` a popup: title game tag `GLOBAL_TOOLTIP_MENU_HEADER` "Game Menu", one option per active, enabled button in the menu, labelled with the button text (Concede / Leave / Options / Quit …), action `TriggerRelease`. Up/Down, Enter, Backspace or Escape to close. While it is open the gameplay screen is silent (focus gating). Concede uses the game's own flow: `GameMenu`'s concede button → (optional warning popup, made accessible elsewhere) → `GameState.Concede()`. The result is then "You lose" (CONCEDED).

---

## 11. Calling the game directly (instead of the virtual mouse)

Each HSA action and a direct equivalent. "Direct" calls are suggestions derived from the vanilla flow above; they skip the visual drag, so where a visual step matters (held card, arrow) it is noted.

| Action | HSA does | Direct path |
|---|---|---|
| Play a non-targeted, non-minion card | click (grab) + click over board (drop) | `InputManager.DoNetworkResponse(entity)` in main option mode (→ `DoNetworkOptions` → `SendOption`). For visuals the game also calls `ZoneMgr.AddLocalZoneChange(card, PLAY)` and `PredictSpentMana`, which are private; skipping them only delays the animation. |
| Play a minion / location at slot p | click, mouse to slot, click | `GameState.SetSelectedOptionPosition(ZoneMgr.PredictZonePosition(entity, zonePlay, p))`, then `InputManager.DoNetworkResponse(entity)` (use `desiredKeyword: GAME_TAG.DISGUISED` for the opposing side of a disguised card). Without the local zone change the minion appears when the server answers. |
| Play a targeted spell | grab, lift over board (game enters target mode), click target | `DoNetworkResponse(spell)` (enters target mode via `EnterOptionTargetMode`), then `DoNetworkResponse(target)` |
| Attack | click attacker, click target | `DoNetworkResponse(attacker)`, then `DoNetworkResponse(target)` |
| Hero power / location | click | `DoNetworkResponse(entity)` (+ target as above) |
| Trade / forge / prepare | grab, move to deck area, click | `DoNetworkResponse(entity, true, GAME_TAG.TRADEABLE / FORGE / PREPARE)` (forge/prepare also start `Card.WaitAndForgeCard` / `WaitAndPrepareCard` for visuals) |
| Choose a sub-option | click option card | `InputManager.HandleClickOnSubOption(entity)` (public) |
| Choose a choice card | click | `DoNetworkResponse(entity)` in CHOICE mode (→ `DoNetworkChoice`; sends at once for single choices), or `GameState.AddChosenEntity` + `SendChoices` |
| Hide/show choices | `ChoiceCardMgr.GetToggleButton().TriggerRelease()` | same |
| Reroll choice | `RerollUIManager` reroll button | same (needs the live instance; HSA adds `TryGet`) |
| Rewind / keep | `RewindUIManager.m_rewindButton / m_keepButton.TriggerRelease()` | same (fields are private in vanilla: reflection) |
| Cancel | right click | `InputManager.CancelOption()` is private; public pieces: `CancelTargetMode()`, `CancelSubOptionMode()`, `CancelReverseTargetMode()`, `ReturnHeldCardToHand()`, `GameState.CancelCurrentOptionMode()` |
| End turn | `InputManager.DoEndTurnButton()` | same |
| Mulligan toggle | click card | `MulliganManager.ToggleHoldState(Card)` (public); read the real state from `m_handCardsMarkedForReplace` |
| Mulligan confirm | `mulliganButton.TriggerRelease()` | same, or `OnMulliganButtonReleased` (private; HSA makes it internal) |
| Emotes | `EmoteHandler.ShowEmotes()`, `EmoteOption.DoClick`, `EnemyEmoteHandler.ShowEmotes()` / squelch | same (`DoSquelchClick` is private in vanilla) |
| Continue after game | `EndGameScreen.m_hitbox.TriggerRelease()` | same |
| Concede | game menu | `GameState.Get().Concede()` |

Read-only queries HSA uses to decide what to say: `GameState.GetResponseMode`, `IsInMainOptionMode`, `IsInSubOptionMode`, `IsInChoiceMode`, `IsInTargetMode`, `IsInReverseTargetMode`, `IsValidOption`, `IsValidSubOption`, `IsChoice`, `IsValidOptionTarget(entity, true)`, `HasResponse`, `EntityHasTargets`, `CanTargetCardsInHand`, `GetOptionsPacket`, `GetSelectedNetworkOption`, `GetFriendlyEntityChoices`, `GetInteractableObject`, `IsFriendlySidePlayerTurn`; `EndTurnButton.HasNoMorePlays`; `InputManager.GetHeldCard`, `GetMousedOverCard`; `ChoiceCardMgr.IsFriendlyShown`, `m_friendlyChoicesShown`; `RewindUIManager.IsShowingRewindUI`; `Entity.HasDeckAction`, `IsTradeable`, `IsForgeable`, `IsPrepareable`, `IsPassable`, `HasDisguisedAction`, `HasUsableTitanAbilities`, `CanBeAttacked`, `CanBeTargetedByOpponents`.

---

## 12. Notable quirks to decide on

1. Enter never checks validity; the game's play error is the only feedback, and an unplayable card is first picked up and announced ("Summon at the right?") before the error.
2. Locations use the minion placement strings ("Summon …?").
3. "Try Again" can be said and the click still happens.
4. In target mode the source and the number of targets are never spoken; focus is dropped if it was in the hand, so Enter right after "Choose a target" is silent.
5. Choose One (sub-option mode) cannot be cancelled with Backspace.
6. A key that cancels the end-turn question also performs its normal action.
7. The mulligan "marked" state is HSA's copy, not the game's, and is not spoken on focus.
8. Placement at an unchanged slot (Left at slot 1) is silent; Home/End/numbers repeat the query.
9. Choice lists are rebuilt (and the title re-read) every time the game shows the cards, including after "show".
10. Shift+F and Ctrl+F check the attacker list and the hero in different orders.
11. The 10-second warning probably also fires on the opponent's turn.

---

## Appendix: game API calls used

- `InputManager.Get().GetHeldCard()`, `GetMousedOverCard()`, `SetMousedOverCard(card)`, `DoEndTurnButton()`, `DoNetworkResponse(entity, checkValidInput, desiredKeyword)`, `HandleClickOnSubOption(entity)`, `CancelTargetMode()`, `CancelSubOptionMode()`, `CancelReverseTargetMode()`, `ReturnHeldCardToHand()`, `DropHeldCard()`, `StartPendingChoiceTarget()` (hooked)
- `GameState.Get()`: `GetResponseMode`, `IsInMainOptionMode`, `IsInSubOptionMode`, `IsInChoiceMode`, `IsInTargetMode`, `IsInReverseTargetMode`, `IsMulliganPhase`, `IsMulliganPhasePending`, `IsGameCreated`, `IsGameOver`, `IsFriendlySidePlayerTurn`, `IsValidOption`, `IsValidSubOption`, `IsChoice`, `IsValidOptionTarget`, `HasResponse`, `EntityHasTargets`, `CanTargetCardsInHand`, `GetOptionsPacket`, `GetSelectedNetworkOption`, `GetSelectedNetworkSubOption`, `GetFriendlyEntityChoices`, `GetInteractableObject`, `SetSelectedOption`, `SetSelectedOptionPosition`, `SetSelectedOptionTarget`, `SendOption`, `SendChoices`, `AddChosenEntity`, `CancelCurrentOptionMode`, `EnterOptionTargetMode`, `RegisterGameOverListener`, `Concede`, `GetFriendlySidePlayer`, `GetOpposingSidePlayer`, `GetEntity`, `GetGameEntity`, `GetTurn`
- `Network.Options.Option` (`Type`, `Main.ID`, `Main.IsValidTarget(id)`, `Main.Targets`, `Subs`)
- `EndTurnButton.Get().HasNoMorePlays()`
- `ZoneMgr.Get().FindZoneOfType<ZonePlay>(side)`, `PredictZonePosition(entity, zone, slot)`; `Zone.GetCardCount()`, `GetCardAtSlot(n)`, `GetLastPos()`; `Board.Get().GetDeckActionArea()`
- `Player.GetBattlefieldZone()`, `GetHandZone()`, `GetHero()`, `GetHeroCard()`, `GetHeroPower()`, `GetName()`
- `Entity.IsMinion`, `IsLocation`, `IsHero`, `IsHeroPower`, `GetZone`, `IsControlledByFriendlySidePlayer`, `HasDeckAction`, `IsTradeable`, `IsForgeable`, `IsPrepareable`, `IsPassable`, `HasDisguisedAction`, `HasUsableTitanAbilities`, `CanBeAttacked`, `CanBeTargetedByOpponents`, `GetName`, `GetCardId`
- `ChoiceCardMgr.Get()`: `ShowChoiceCards` / reveal / `ShowSubOptions` / hide / `ChooseBGTrinket` (hooked), `GetToggleButton()`, `IsFriendlyShown()`, `m_friendlyChoicesShown`, `m_confirmChoiceButton`, `m_choiceBanner.m_headline`
- `RerollUIManager` (`m_rerollButton`, live instance), `RewindUIManager.IsShowingRewindUI`, `m_rewindButton`, `m_keepButton`
- `MulliganManager`: `ToggleHoldState(Card/Actor)`, mulligan button, `GetAnomalies()`, `EndMulligan` / `WaitForOpponentToFinishMulligan` / `DealStartingCards` / coin (hooked); `MulliganTimer` (hooked)
- `TurnTimer.ComputeCountdownRemainingSec()`; `TurnStartManager` (hooked for "Your Turn")
- `PlayErrors.DisplayPlayError`, `GameplayErrorManager.DisplayMessage` (hooked to speak)
- `EmoteHandler.Get()`: `ShowEmotes`, `HideEmotes`, `AreEmotesActive`, `EmoteOption.DoClick`; `EnemyEmoteHandler.Get()`: same plus `DoSquelchClick`
- `EndGameScreen` (`Show` hooked, `m_hitbox.TriggerRelease()`), `EndGameTwoScoop.Show`, `HeroXPBar`
- `BnetBar.Get().ToggleGameMenu()`, `GameMenu` buttons
- `GameMgr.Get().IsSpectator()`, `CosmeticCoinManager.Get().IsCoinCard(cardId)`, `Options.Get()` (`ACCESSIBILITY_AUTO_ATTACK_SPEED`, `ACCESSIBILITY_SAVE_BATTLE_LOGS`, `HAS_SEEN_MULLIGAN`)
