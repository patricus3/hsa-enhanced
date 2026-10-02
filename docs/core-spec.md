# Core spec: lifecycle, focus, speech, input, menus, localization

This spec describes how the core of Hearthstone Access (HSA) behaves, so we can rebuild it from scratch in our own code. It describes behaviour only. HSA has no license, so none of its code is reproduced here. HSA class and method names appear only as reference points. Game and Unity names (UniversalInputManager, InputCollection, Input, Options, GameStrings, DialogManager, ...) are used freely.

Source: `C:/ProgramData/HearthstoneAccess/downloads/hsa.diff.patch` (HSA 46, built against game 25.0.0.158725 constants but patched up to current builds) and `Strings/enUS/ACCESSIBILITY.txt`. Checked against the vanilla `Assembly-CSharp.dll` where the patch shows only part of a game method.

Notation:
- `LK.X` is HSA's localization key `X`. Its string tag is `ACCESSIBILITY_X` unless another tag is given.
- "Frame" means one Unity frame. "Key-down" means `UnityEngine.Input.GetKeyDown` is true for this frame.

Scope: the shared machinery every screen uses. Screen-specific behaviour (gameplay, collection, and so on) is in other specs.

---


## Our decisions (2026-10-01)

| # | HSA behaviour | Ours |
|---|---|---|
| 1 | Menus don't wrap; lists wrap only with Tab | Keep |
| 2 | Speech keeps going after F8 turns accessibility off | Keep |
| 3 | Key presses interrupt speech only for SAPI; screen readers handle it themselves | Keep |
| 4 | A popup closing onto another popup leaves silence | Change: the popup landed on is read again |
| 5 | Escape and F11/F12 also reach the focused screen (one press, two actions) | Change: a key press is handled once |
| 6 | A missing string is spoken as its raw tag | Keep |
| 7 | English-only rule lowercases "ALL" even inside other words | Change: dropped |

## 0. The parts at a glance

| Part (HSA name) | What it is |
|---|---|
| AccessibilityMgr | A static hub. It holds the current screen, the stack of UIs, the forced key, the help override and the on/off flag. It routes keyboard input and filters speech by focus. |
| AccessibleSpeechMgr | A MonoBehaviour that owns the speech queue (texts and narrations) and the "game speed" (speech pacing). |
| QueuedText | One queued utterance: its text, an interrupt flag, whether it should be waited for, and start/end callbacks. |
| AccessibleSpeechOptimizer | Rewrites some gameplay texts based on the text spoken just before (English only). |
| ScreenReader | A thin wrapper over Tolk (`DavyKager.Tolk`). In our build that is retargeted to Prism (section 3.12). |
| AccessibleKey | The key bindings (key + modifiers), each with an enabled flag, in a "global" or a "screen" group. |
| AccessibleInputMgr | A MonoBehaviour. In LateUpdate it pushes the virtual mouse state and handles F8, F14 and F15. It also holds the mouse helpers and the number-key helper. |
| AccessibleUnityInput | An `IInput` put first in the game's `InputCollection`. While enabled it answers all mouse queries with the virtual mouse. |
| AccessibleComponent / AccessibleScreen / AccessibleUI | Marker and focus interfaces (section 2). |
| AccessibleElement | The base of all widgets. It keeps a parent component and speaks "as" that parent, so the focus filter applies. |
| AccessibleMenu, AccessibleHorizontalMenu, AccessibleListOfItems, AccessibleItem, AccessibleMultilineText, AccessibleCheckBox, AccessibleDropdownControl, AccessibleScrollbarControl | Reusable widgets (section 5). |
| LocalizationKey, LocalizationUtils, LocalizedText, DefaultGameStrings | String lookup, with an enUS fallback (section 6). |
| HSASoundMgr / HSASound | Earcons: WAV files loaded from `<game>/Accessibility/Sounds/` and played as one-shots. They are used only by Battlegrounds. |

---

## 1. Lifecycle

### 1.1 Initialization

- **Who calls it:** `HearthstoneApplication.Awake`, right after `LocalOptions.Get().Initialize()` and `HearthstoneLocalization.Initialize()`, and before `DeeplinkService` and the rest of startup. It is passed the application's root GameObject.
- **What it does, in order:**
  1. It logs "Accessibility initialized" to the Accessibility log.
  2. It loads the screen reader (`ScreenReader.Load`). This appends `<cwd>/Hearthstone_Data/Managed/Accessibility` to the process PATH, calls Tolk's "try SAPI" with true, then loads Tolk.
  3. It adds the AccessibleInputMgr component to the root GameObject.
  4. It sets the enabled flag to **true**. Accessibility is on from launch. Nothing is persisted.
  5. It creates a new child GameObject of the root with an AudioSource, an AccessibleSpeechMgr and an HSASoundMgr. HSASoundMgr's Awake loads all earcon WAVs.
  - Any exception is caught and logged as "FATAL ERROR - UNCAUGHT EXCEPTION" plus the exception text. Startup goes on.
- **No speech at init.** The "Loading game" announcement is commented out.
- **After the game strings load:** HSA adds a finished-listener to the `GameStrings.LoadAll` job. It logs the Hearthstone and HSA versions. An update check would announce `LK.GLOBAL_NEW_HEARTHSTONE_ACCESS_VERSION_AVAILABLE`, but the check is hard-disabled (it always returns false).
- **Game-side changes made for accessibility** (they are not part of the core, but the core relies on them): `Application.runInBackground` is already true in the game. Exception reporting to Blizzard is switched to debug-only. `Option.SOUND_VOLUME` defaults to 0.5 instead of 1.

### 1.2 Per-frame update

There is no single update method. Three things run each frame:

| When | What |
|---|---|
| `UniversalInputManager.Update` → `UpdateInput` (a game service; HearthstoneApplication drives its update loop) | `UpdateInput` first runs the game's text-input check. If a game text field has focus, it returns, and HSA is not called. Otherwise, **if accessibility is enabled**, it calls `AccessibilityMgr.HandleKeyboardInput()` and **returns**. The game's own keyboard chain is skipped entirely: InputManager, DemoMgr, cheats, DialogManager, InputMgr, PackOpening, the current scene, and BaseUI (which includes the BnetBar keys, screenshots and the Ctrl+Shift+S streamer-mode toggle). |
| `AccessibleSpeechMgr.Update` (MonoBehaviour) | It interrupts the queue on a key press (section 3.4), then starts at most one queued item. |
| `AccessibleInputMgr.LateUpdate` (MonoBehaviour, after every Update) | It copies the virtual mouse position and this frame's virtual button states into AccessibleUnityInput, then clears the pending clicks. It checks F8 (toggle), F15 (silent enable) and F14 (silent disable). These are checked here, so they work while accessibility is off. |

Unity does not define the order between the UIM update and the SpeechMgr Update. This matters for one edge case (section 3.4).

### 1.3 HandleKeyboardInput, the input router (exact order)

1. If HSA's own "text input allowed" flag is set, do nothing this frame (section 4.6).
2. If any key went down this frame (`Input.anyKeyDown`, which includes mouse buttons):
   - stop interruptible narrations (section 3.5);
   - only if the active Tolk driver reports "SAPI": flush the text queue and send an empty interrupting output to the screen reader, which silences SAPI. Real screen readers silence themselves on a key press, so HSA does not interrupt them.
3. Handle the global keys (section 4.3). These run first and **do not consume** the key: the same press still continues below.
4. If F1 (HELP) is down: speak the help (section 2.7) and **stop here** for this frame.
5. Exactly one of these runs, in priority order:
   1. A **forced key** is pending: if that key is down, clear it, then run its action. Every other key is ignored while a forced key is pending.
   2. A **notification dismiss button** is set: Enter triggers its release. (Dead in practice: nothing sets one. See 2.6.)
   3. A **UI** is open: the top UI gets `HandleAccessibleInput()`.
   4. A **screen** is set: the screen gets `HandleInput()`.
   5. Nothing happens.

Any exception is caught and logged, and the frame goes on.

### 1.4 Shutdown

In `HearthstoneApplication.OnApplicationQuit`, before the game's own shutdown:
1. Speak `LK.GLOBAL_CLOSING_GAME` "Closing game" as a notification. It goes into the queue, so it is usually not heard: the next step tears down the screen reader.
2. Log "Accessibility shutdown" and unload Tolk.

Nothing else is saved or torn down.

### 1.5 Turning accessibility on and off

| Key | Action |
|---|---|
| F8 (TOGGLE_ACCESSIBILITY, a global key) | Flips both AccessibleUnityInput's enabled flag (the virtual mouse) and the manager's enabled flag. Then speaks `LK.GLOBAL_ACCESSIBILITY_ON` "Accessibility on" or `LK.GLOBAL_ACCESSIBILITY_OFF` "Accessibility off". This goes through the focus-free path, so it is always heard. |
| F15 with any modifiers (SILENTLY_ENABLE, modifiers ignored) | Enables both, with no announcement. |
| F14 with any modifiers (SILENTLY_DISABLE) | Disables both, with no announcement. |

These keys are probably meant for external tools; F14 and F15 are not on most keyboards.

**When it is off:**
- The real mouse works again: AccessibleUnityInput answers "not handled", so InputCollection falls through to the real UnityInput.
- UIM runs the game's normal keyboard chain.
- About 60 game hooks check the on/off state and restore vanilla behaviour, for example tutorial popups, the "big card" display, cinematics, and the infographics HSA skips.
- **Speech is not silenced.** Output and the speech queue never check the flag, so screens and notifications keep talking.
- Nothing is persisted. Every launch starts on.

---

## 2. Screens, UIs and focus

### 2.1 Model

- A **component** is anything that can speak or take input. It is only a marker type.
- A **screen** is a component with: handle input, get help text, and on-gained-focus. **At most one** screen is current at a time. Typically the scene's main display (hub, collection, gameplay, ...) calls `SetScreen(this)` when it is ready.
- A **UI** is a component with: handle input and get help text. **It has no on-gained-focus.** UIs form a **stack**: the last one shown has focus. Every game `DialogBase` is a UI (DialogBase becomes abstract and must implement both methods). So are many popups and menus: OptionsMenu, GameMenu, AlertPopup, reward popups, the friend list, store screens, and others.
- Dialogs HSA has not made accessible derive from a base whose help is `LK.UI_UNKNOWN_DIALOG` "This dialog hasn't been made accessible yet. Please get help from someone sighted". They ignore all input.

**Focus rule:** the focused component is the top UI if the stack is not empty, otherwise the current screen.

### 2.2 SetScreen(screen)

- It logs and replaces the current screen.
- It calls the new screen's on-gained-focus **only if** all of these hold: the screen actually changed (by reference), **no UI is open**, and `DialogManager` has **no queued dialog requests**.
- Otherwise the screen stays silent until a later pop gives it focus (2.4).
- Setting the same screen again does nothing. To re-announce, a screen calls its own reading method.
- Setting a null screen when no UI is open makes on-gained-focus throw. The exception is caught and logged.

### 2.3 TransitioningScreens()

It sets the current screen to null. Scene teardown code calls it (login, collection manager, tutorial progress, friend challenges). Until the next SetScreen:
- speech from any screen component is dropped;
- F1 says "Loading. Please wait." (2.7);
- keys reach nobody except global keys and UIs.

### 2.4 ShowUI(ui) / HideUI(ui) and re-reading after a popup

- **ShowUI:** if the UI is not already on top, push it, even if it is already deeper in the stack (duplicates are possible). Clear the "transitioning UIs" flag. Showing a UI announces nothing by itself; the UI speaks for itself (see the pattern below).
- **HideUI(null):** ignored.
- **HideUI(ui):** remember which UI was on top, then remove **every** occurrence of this UI. If at least one was removed, check whether to refocus:
  - If a UI is still open, **nothing happens**. The newly exposed UI is **not** re-read, because UIs have no on-gained-focus. It just starts receiving keys again.
  - If the stack is now empty and a screen is set, the screen gets on-gained-focus **only if** all of these hold:
    - the removed UI was the one on top (hiding a buried UI never refocuses the screen);
    - DialogManager has no queued dialogs (the next dialog is about to appear);
    - the "transitioning UIs" flag is off.
- **TransitioningUIs()** sets that flag. FriendChallengeMgr uses it so the screen is not re-read while one challenge popup replaces another. The next ShowUI clears it.
- `DialogBase.Hide` calls HideUI first, so every dialog leaves the stack as soon as it starts hiding.

**Typical UI pattern** (AlertPopup):
1. On show, call ShowUI.
2. Speak a title with **interrupt** (for example `LK.UI_ALERT_POPUP_TITLE`), or `LK.UI_POPUP` "Popup".
3. Speak the header text, if any.
4. Build a menu whose name is the body text, and call StartReading.

**Typical screen pattern** (hub): on-gained-focus calls StartReading on the screen's current menu. It reads the menu name, then the current option, so returning from a popup re-reads both.

### 2.5 Speech from components that do not have focus

`Output(speaker, text, interrupt)`:

| Speaker | Result |
|---|---|
| null | Spoken. Used for components not made accessible yet, and for tests. |
| equal to the top UI | Spoken. |
| no UI open, and equal to the current screen | Spoken. |
| anything else | **Silently dropped.** It is not queued for later. |

Widgets (AccessibleElement subclasses) speak through their parent component, so a menu inside a hidden screen is muted automatically.

`IsCurrentlyFocused(component)` exposes the same test. Game code uses it to skip work when its UI is not in focus.

Input is only ever given to the focused component, so components without focus never see keys.

### 2.6 Forced keys and notifications

- **WaitForForcedKey(key, action)** stores one key and one action. A new call replaces the old one. While it is pending:
  - all non-global input is blocked (1.3, step 5.1);
  - F1 says `LK.PRESS_KEY_TO_CONTINUE` with the key's spoken name, for example "Press Enter to continue".
  - When the key goes down, the forced key is cleared first, then the action runs.
  - Users: the login flow (Enter before starting the tutorial), and the tutorial.
- **SetNotification(dismissButton)** would store a PegUIElement: Enter would trigger its release, and F1 would say "Press Enter to continue". The stored button clears itself when released. **Nothing in HSA calls it**, so this path is dead.
- **OutputNotification(text, interrupt)** is "speak regardless of focus". It is identical to the focus-free Output. Used for:
  - game speech-bubble notifications (`NotificationManager`): those created without sound are read; those created with sound are read only for deaf-blind mode (`AccessibilityConfig.CAN_HEAR` false), which is never the case;
  - timers ("N seconds remaining", spoken only when the countdown reaches 10);
  - game speed messages, closing, streamer mode, and others.

### 2.7 Help (F1) routing

Checked in this order. The first match wins. All of these are spoken without interrupt, through the focus-free path:

1. A help override is set (OverrideHelpSpeech): speak it. The tutorial uses this heavily. ResetHelpSpeech clears it.
2. A forced key is pending: "Press {key} to continue".
3. A notification button is set: "Press Enter to continue".
4. A UI is open: the top UI's help text.
5. A screen is set: the screen's help text.
6. Otherwise: "Loading" then "Please wait", as two separate utterances (`LK.GLOBAL_LOADING`, `LK.GLOBAL_PLEASE_WAIT`).

After F1, nothing else is processed that frame. A UI or screen that returns null or "" as help says nothing. OptionsMenu does this outside its main state.

### 2.8 Key blocking API (present but unused)

HSA has a key-blocking API: BlockAllInput, UnblockAllInput, WhitelistKeys, BlacklistKeys, BlockHelpSpeech. It enables and disables keys by group and sets help overrides. **No caller exists** in this version. Only OverrideHelpSpeech and ResetHelpSpeech are used. Note that BlockAllInput with "block global input" would also disable F8.

---

## 3. Speech

### 3.1 Entry points

| Entry | Focus filter | Window-focus gate | Queue behaviour | Waits? |
|---|---|---|---|---|
| `Output(speaker, text, interrupt=false)` | yes (2.5) | yes | interrupt=false: append. interrupt=true: flush the queue, then append an item marked "interrupt". | no |
| `OutputNotification(text, interrupt=false)` | no | yes | same as above | no |
| `OutputAndWait(text, onStart, onEnd)` | no | yes; when gated, both callbacks fire at once | append a "wait" item. Empty text after curation: callbacks fire at once and nothing is queued. | yes, for an estimated time (3.6) |
| Narrate(...) / NarrateAndWait(speech, onFinish) | no | no | Recorded narration is disabled, so it is turned into a curated text item marked "narration" with wait=true. NarrateAndWait's onFinish is effectively never called in that mode. | yes |
| Direct `ScreenReader.Output` | no | no | Bypasses the queue. Used only for the game-speed announcement and calibration. | — |

**Window-focus gate:** speech is produced only if the game window has focus (`HearthstoneApplication.HasFocus()`) **or** `Option.ACCESSIBILITY_BACKGROUND_SPEECH` is true (the default is true). When gated, the text is **dropped**, not deferred.

### 3.2 The queue

- There are two FIFO queues, texts and narrations, plus a "current text", a "current narration", and a history list of every text already played (it is never trimmed).
- **SpeechMgr.Update, each frame:**
  1. If any key went down and a current text exists, interrupt the texts (3.4).
  2. If nothing is current: if a text is queued, dequeue one, run the optimizer on it (3.8), and start it. Otherwise start a queued narration. In practice the narration queue stays empty.
- **Starting a text:** make it current, then send it to the screen reader with its interrupt flag.
  - If the screen reader returns false (empty text, not loaded, failure), the start and end callbacks fire at once.
  - Then the text "plays" until its estimated time has passed, measured by a stopwatch. When done it is added to the history and current becomes empty.
  - A non-wait item has an estimated time of 0, so it finishes in the same frame.
- **Net effect:** non-wait texts are handed to the screen reader **one per frame**, in order, without interrupt unless they were sent with interrupt. The **screen reader's own queue** decides whether they are spoken one after another (NVDA, JAWS and Prism queue non-interrupting output). A wait item blocks the HSA queue for its estimated time. Gameplay uses this to pace events: `GameState` waits while the play describer or power-task describer are busy.

### 3.3 Interrupt semantics

- `interrupt=true` on Output:
  1. Curate the text.
  2. Interrupt the texts: end the current one, and drop the queued items, firing their end callbacks (3.4).
  3. Append the new item with the interrupt flag.
  4. When it plays, the screen reader gets `Output(text, interrupt: true)`, which stops what it is saying.
  - The new item is spoken at the next queue tick. That is the same frame if the SpeechMgr Update runs later in the frame, otherwise the next frame.
- `interrupt=false`: append only. Earlier queued items are spoken first.
- Plain Output does **not** skip empty text (it goes into the queue and finishes at once). OutputAndWait and narrations do skip it.

### 3.4 Interrupting the queue

"Interrupt texts":
1. End the current text: its end callback fires. The screen reader is **not** told to stop.
2. Then dequeue items one by one, firing each one's end callback, **until a narration item is met**. That narration item is removed too, but not played and not ended. Items after it stay queued.

Triggers:
- an interrupting Output;
- in SpeechMgr.Update: any key-down while a current text exists. A current text exists only while a **wait** item is playing, so in menus this rarely matters;
- in HandleKeyboardInput: any key-down when the screen reader is SAPI. This also sends an empty interrupting output to stop SAPI's audio.

**Race:** when a wait item is playing and the user presses a key, a response that HandleKeyboardInput already queued this frame is flushed, if SpeechMgr.Update happens to run after it in the frame.

### 3.5 Narrations

Recorded narration audio is turned off ("no longer used as of 27.6"). Every narration becomes a text item marked "narration". "Interrupt narrations", run on every key-down, stops the AudioSource if the current narration is interruptible, then drops interruptible queued narrations and calls their finish callbacks. In practice it does nothing.

### 3.6 Wait-time estimate and "game speed" (F11/F12)

- Estimated time for a wait item = (text length ÷ characters per second) × 1000 ms. HSA computes this with **integer division**, so the time is a whole number of seconds rounded down. Texts shorter than the CPS value do not wait at all.
- CPS = WPM × 6 ÷ 60, where WPM comes from the current "game speed" level:

| Level | 1 | 2 | 3 (default) | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|---|---|---|---|---|---|---|---|---|---|
| WPM | 100 | 125 | 150 | 175 | 200 | 225 | 250 | 275 | 300 | 10000 |
| CPS | 10 | 12 | 15 | 17 | 20 | 22 | 25 | 27 | 30 | 1000 |

- **F12** (INCREASE_GAME_SPEED) and **F11** (DECREASE_GAME_SPEED), with no modifiers. These are screen-group keys checked in the global handler.
  - In a PvP game (VS friend, arena, ranked, casual, Tavern Brawl, Battlegrounds incl. duos and friendly, PvP Duels, Underground Arena) or while playing Battlegrounds: speak `LK.GLOBAL_GAME_SPEED_CANNOT_ADJUST_VS_PLAYERS` as a notification. CPS is forced to 1000 there, so waits are about 0.
  - Otherwise, load the saved level if not loaded yet (an out-of-range saved value is overwritten with the current one), then move by ±1. At either end, silently nothing happens.
  - On a change: save `Option.ACCESSIBILITY_GAME_SPEED`, recompute the estimates of queued items that are not playing, and speak `LK.GLOBAL_GAME_SPEED` "Game speed {n}" (curated) **directly** to the screen reader, without interrupt, bypassing the queue and the focus filter.
- This does not change Unity's time scale or the TTS rate. It only changes how long HSA waits between gameplay announcements.
- A calibration routine exists (it times how fast the screen reader reads sample sentences) but is never called.

### 3.7 Text curation (`CurateText`), exact steps

Applied to every text before it is queued. Null or empty input gives "".

1. Trim whitespace.
2. Case-sensitive substring replacements: "HIMSELF" → "himself", "ALL" → "all", "LOT" → "lot". This also hits these letters inside other all-caps words.
3. Newlines: split on `\n`. Every line except the last gets a separator appended. If the whole line is exactly `<b>…</b>`, the separator is the localized period plus a space; otherwise a single space. The lines are then joined, which puts keyword lines like "Battlecry" into their own sentence.
4. Sequential bolds: "`</b> <b>`" becomes "`</b>` + period + space + `<b>`".
5. Remove every `<…>` tag (regex `<[^>]*>`).
6. Remove the literal "[X]" and "[x]".
7. Remove every "*".
8. Remove one "(+N Attack/+N Health)" group (single digits, English only).
9. Replace "/" with a space.
10. Capitalize sentences: the first letter or digit of the text, and the first one after any sentence-ending character, is upper-cased.
11. Replace "_" with a space.
12. Collapse periods: "..." becomes "." and then ".." becomes ".", using the localized period. Skipped if the period string is empty (Thai).
13. Collapse runs of spaces (only U+0020) into one.
14. Trim. If the text is empty now, return "".
15. Add a period if needed: append the localized period unless the text ends with a sentence-ending character, ends with `."`, or ends with `:`.
16. If anything throws: log it and return "".

Sentence-ending characters come from `LK.FORMATTING_SENTENCE_ENDING_CHARACTERS`: in enUS ".!?;". The period comes from `LK.FORMATTING_PERIOD` ".".

If debug logging is on, the before and after texts go to the Accessibility_text log.

### 3.8 Speech optimizer (English only, gameplay)

Run on each text as it is dequeued. It compares the text with the **last played** text:

- **Previous text matched "X attacked Y."**
  - If the current text is "X and Y died." in either order, or "All minions died.": replace it with "Both minions died." ("Both characters died." if X or Y is "Your hero" or "Your opponent's hero"). An ordinal like "first" after "Your" or "Your opponent's" is removed from the names before comparing.
  - If only X (or only Y) died:
    - a hero: "Your hero died." (the name stays);
    - a name starting with "Your opponent's ": "Your opponent's minion died.";
    - a name starting with "Your ": "Your minion died.";
    - otherwise the name, and a warning is logged.
- **Previous text matched "X attacked Y." and the current text is "X and Y took N damage."** (either order): replace it with "Both minions took N damage." ("characters" if a hero is involved).

These strings are hard-coded English. In other locales the rule simply never matches.

### 3.9 List humanizing and sentence helpers (AccessibleSpeechUtils)

- **HumanizeList(list):**
  - 0 items: "".
  - 1 item: the item.
  - Otherwise: items joined with the separator plus a space, and the last one joined with a space, the final separator and a space. In enUS: "A, B and C". Note: the separator is `LK.FORMATTING_LIST_SEPARATOR` ",".
- **HumanizeNames(orderedNames, counts)** (mulligan, zone moves):
  - Duplicates are removed, keeping first-seen order. The joins are the same as above.
  - Each name is formatted with `LK.GAMEPLAY_DIFF_MULTIPLE_ENTITIES` "{0} {1}". When the count is above 1, that gives "2 Raid Leader" (or "N cards" when the name is the generic word "card").
  - When the count is 1, the number is left out (an empty string), which leaves a leading space ("Name" becomes " Name"; the space normalization in curation removes it later). A lone generic "card" becomes "1 card".
  - English has no plural form of names, so "2 Raid Leader" is said as is.
- **FormatZoneMovementText(cards, singularKey, pluralKey):** none gives null; one gives singularKey(name); several give pluralKey(HumanizeNames).
- **CombineLines(lines):** skips empty lines and joins the rest with a space. A period is added first if the text so far does not end with a sentence-ending character.
- **CombineSentences(a, b):** if one is empty, return the other. Otherwise "a b" if a ends with a sentence-ending character, else "a. b".
- **CombineWordsWithComma(a, b)** gives "a ; b" (a semicolon, with spaces). **CombineWordsWithColon** gives "a: b".
- **Entity naming** for gameplay: "your X" / "your opponent's X" / Battlegrounds "Bob's X". Duplicate names in a zone get ordinals ("your second X"), using `LK.FORMATTING_ORDINAL_NUMBER_1..10` when the locale translates them, digits otherwise. Covered in the combat spec.

### 3.10 Logging

- The game's Log system gets two new log names, written to file only, at Info level: **"Accessibility"** and **"Accessibility_text"**. They are written to the game's normal Logs folder.
- Always logged: init and shutdown, SetScreen, ShowUI and HideUI, transitions, versions, and fatal errors (exception plus stack).
- Debug lines are logged only when debug logging is on. That is either DEV_MODE (compiled off) or the options-menu checkbox "Enable debug logging for this run", which is not persisted. Debug lines include: every screen-reader output with its success flag, key presses, mouse moves and clicks, curation before and after, and speech start and end.

### 3.11 How speech reaches the screen reader (official HSA)

`ScreenReader.Output(text, interrupt)` returns false for empty text. Otherwise it calls Tolk's Output(text, interrupt) and logs the result (debug). Interrupting means an empty interrupting output. Tolk picks NVDA, JAWS, System Access, ... and falls back to SAPI ("TrySAPI" is on). `IsUsingSAPI` compares Tolk's detected-reader name with "SAPI".

### 3.12 How speech reaches the screen reader in OUR build (today)

- **Windows:** HSA's Assembly-CSharp keeps calling `DavyKager.Tolk`. The rebuild runs `port speech` (`Enhance.RetargetSpeech` in `Resources/tools/port/Enhance.cs`). It uses Mono.Cecil to rewrite every `DavyKager.Tolk` type reference to `HSAPrism.Speech` in a new assembly reference `HSAPrism`, and removes the `TolkDotNet` reference. No Tolk DLLs or controller clients are installed; `Remove-OldSpeech` deletes old ones.
- **`HSAPrism.Speech`** (`Resources/windows/speech/Prism.cs`, net472) has the same static API as Tolk:
  - **Load:**
    - preloads `prism.dll` from `Managed/Accessibility` with LoadLibraryEx (so PATH is not needed);
    - initializes Prism (`prism_init`);
    - takes the "best" backend: the running screen reader, else OneCore or SAPI.
  - **Backend check:** before each output, it re-checks the best backend at most every 2 s, so starting or quitting a screen reader moves speech over.
  - **Output(text, interrupt):** sends the UTF-8 text with the interrupt flag to the backend (`prism_backend_output`).
    - An empty interrupting output becomes `prism_backend_stop`.
    - If an output fails, it re-picks the backend and retries once.
  - **Silence:** calls `prism_backend_stop`.
  - **DetectScreenReader:** returns "SAPI" for a SAPI or OneCore backend, so HSA's "silence on every key" logic also covers OneCore. Otherwise it returns the backend name.
  - **Not supported:** TrySAPI and PreferSAPI do nothing; there is no braille; IsSpeaking always returns false.
  - All calls hold one lock.
- **macOS:** `Resources/tolk/Tolk.cs` is a drop-in `DavyKager.Tolk` (TolkDotNet.dll) whose calls go to `libHSAVoiceOver.dylib`:
  - Output → `hsa_vo_output`; Silence → `hsa_vo_silence`; Load → `hsa_vo_init`.
  - The dylib (`Resources/voiceover/hsavoiceover.m`) speaks through Prism's AVSpeech backend on a serial queue. It queues early output until Prism is ready.
  - It picks the voice and rate from macOS Spoken Content, with optional language detection and override files in `~/Library/Application Support/HearthstoneAccess/`.
  - It logs to `~/Library/Logs/HearthstoneAccess/speech.log`.
  - It installs a local NSEvent monitor that **stops speech on every key-down or modifier press** in the game window. The event still reaches the game.
  - DetectScreenReader returns "Prism", so HSA's SAPI branch is skipped; the dylib handles key interrupts itself.
- **Our own core today** (HSAEnhanced) speaks through HSA's `AccessibilityMgr.Output` / `OutputNotification`, so it gets HSA's focus filter, curation and queue.

**What our own core could call directly:**
- **Windows:** `HSAPrism.Speech.Output(string, bool interrupt)`, `Speech.Silence()`, `Speech.DetectScreenReader()`, `Speech.IsLoaded()` / `HasSpeech()`, `Speech.Load()` / `Unload()`. HSAPrism.dll is in `Managed`, so HSAEnhanced's `$(GameManaged)\*.dll` reference already picks it up.
- **macOS:** the shim `DavyKager.Tolk.Output` / `Silence` (TolkDotNet.dll), or P/Invoke `hsa_vo_output(const char*, int)` / `hsa_vo_silence()` in `/Applications/Hearthstone/HearthstoneAccess/libHSAVoiceOver.dylib`.
- **Recommendation:** a small `ISpeechBackend` with two implementations chosen at startup. Implement our own queue, focus filter and curation on top, and do not route through HSA's AccessibilityMgr.

---

## 4. Input

### 4.1 Key model (AccessibleKey)

- A binding has: a Unity KeyCode; flags requiring Shift, Ctrl and Alt; an "ignore all modifiers" flag; an enabled flag (initially on); and a group, global or screen (non-global). The group only matters for the unused blocking API.
- **IsPressed()** is true when all of these hold:
  - the binding is enabled;
  - **key-down this frame** for the KeyCode. Return also accepts KeypadEnter;
  - the modifiers match **exactly**: each of Shift, Ctrl and Alt must be held if required and **not held if not required**. Left and right versions both count. With "ignore all modifiers", only the key is checked.
  - So Up and Shift+Up are distinct, and Ctrl+Shift+F matches neither F nor Shift+F.
- It reads `UnityEngine.Input` directly, not InputCollection. There is **no consumption**: every binding for the same key reports true for the whole frame. Exclusivity comes only from if/else ordering and from routing to one focused component.
- A disabled binding returns false and logs a debug line if the key was pressed.
- **IsDown()** (held) checks only the Shift requirement: Shift must be held if required, else not held.
- **Spoken name (ToString):**
  - with Ctrl: `LK.INPUT_COMMAND_WITH_CTRL_FORMAT` "Ctrl + {0}". The Shift part of Ctrl+Shift is not said;
  - with Shift: `LK.INPUT_COMMAND_WITH_MODIFIER_FORMAT` "Shift + {0}";
  - the key name is `ACCESSIBILITY_INPUT_KEY_OVERRIDE_<KeyCode name>` if the **current locale** has it (enUS: Return → "Enter", A → "eh", I → "eye"), else the KeyCode enum name ("UpArrow", "Backspace", "F1", "Alpha1", ...). The enUS fallback table is not used for this check.
- **ToEnglishString** is used only for narrations. It says "Enter", "eh" and "eye", and "Ctrl + " / "Shift + ".

### 4.2 Core bindings

| Binding | Key | Group |
|---|---|---|
| CONFIRM / GLOBAL_CONFIRM | Enter (or keypad Enter) | screen / global |
| BACK / GLOBAL_BACK | Backspace | screen / global |
| OPEN_GAME_MENU | Escape | global |
| HELP | F1 | global |
| OPEN_SOCIAL_MENU | F4 | global |
| TOGGLE_ACCESSIBILITY | F8 | global |
| SILENTLY_DISABLE / ENABLE | F14 / F15, any modifiers | global |
| DECREASE / INCREASE_GAME_SPEED | F11 / F12 | screen |
| READ_NEXT_LINE / READ_PREV_LINE | Down / Up | screen |
| READ_CUR_LINE / READ_TO_END | Shift+Up / Shift+Down | screen |
| READ_NEXT_ITEM / READ_PREV_ITEM | Right / Left | screen |
| READ_NEXT_VALID_ITEM / READ_PREV_VALID_ITEM | Tab / Shift+Tab | screen |
| READ_FIRST_ITEM / READ_LAST_ITEM | Home / End | screen |
| READ_FIRST_ITEM_GLOBAL / READ_LAST_ITEM_GLOBAL | Shift+Home / Shift+End. Despite the name these are screen-group keys, so a global menu's Home/End needs Shift (see 5.1). | screen |
| READ_NEXT_PAGE / READ_PREV_PAGE | PageDown / PageUp | screen |
| SPACE, SKIP_NOTIFICATION | Space | screen |
| ESC | Escape | screen |
| GLOBAL_LEFT / GLOBAL_RIGHT | Left / Right | global |
| Menu: READ_NEXT/PREV_MENU_OPTION, READ_CUR_MENU_OPTION, READ_NEXT/PREV_VALID_MENU_OPTION | Down / Up, Shift+Up, Tab / Shift+Tab | screen, and a global twin of each |
| GLOBAL_FIND | Ctrl+F | screen |

Screen-specific bindings (hub letters, gameplay letters, Battlegrounds, arena) are listed in the screen specs.

### 4.3 Global keys (checked every frame before routing)

This is one if/else chain, so at most one global action runs per frame, and it does not consume the key:

1. **Escape**, if BnetBar exists:
   - if the friend list is showing: hide it;
   - else if any options-type menu is shown (the game menu, OptionsMenu, MiscellaneousMenu or SoundOptionsMenu): send BnetBar's escape handler, which closes or backs out of it;
   - else: toggle the game menu.
2. **F4**, if the BnetBar friend button exists and no options-type menu is shown: release the friend button, which toggles the social panel.
3. Dev-only keys, compiled off: F5 console, F6 and F7 tests, F3 quick practice game, backslash, F10.
4. **F12**: one game-speed level faster. **F11**: one level slower (3.6).

F8, F14 and F15 are handled in AccessibleInputMgr.LateUpdate instead (1.5).

### 4.4 Blocking the game's own input while on

- **Keyboard:** UniversalInputManager.UpdateInput returns right after calling HSA (1.2). No game keyboard handler runs, including Escape handling and DialogManager keys. HSA reimplements what it needs. Game text fields still work, because UIM's text-input check comes first.
- **Mouse:** AccessibleUnityInput is put **first** in `InputCollection`'s list of inputs. While enabled it answers every query as "handled":
  - mouse position: the virtual position;
  - mouse button held, down and up: the virtual buttons;
  - keys (any key, held, down, up): passed through from the real `UnityEngine.Input`.
  - So the real mouse is ignored by every game system that reads input through InputCollection: hover, clicks, drags.
  - An invalid button index (outside 0–2) answers "not handled" with false.
- **Dialog and popup suppression:** some game flows are changed for accessibility, for example infographics, intros and cinematics are skipped and tutorial popups are replaced. Those changes are made in the game hooks, not in the core.
- A game hack in InputManager: while on, a click on an "interactable" in gameplay is not activated.

### 4.5 The virtual mouse (AccessibleInputMgr + AccessibleUnityInput)

- **State:** a pending screen position (initially (0,0,0)) and three pending "click this frame" flags (left, right, middle).
- **Apply, in LateUpdate:**
  1. Copy the pending position to AccessibleUnityInput.
  2. For each button, set "was down" to the old "is down" and "is down" to the pending flag, then clear the pending flag.
  - "Button down" means pressed now but not last frame. "Button up" means the reverse.
- **Click timeline:**
  - frame N: a click is requested (the flag is set);
  - LateUpdate of N: the button is pressed;
  - frame N+1: the game sees "button down" and "held";
  - LateUpdate of N+1: the button is released;
  - frame N+2: the game sees "button up".
  - A position set in frame N also takes effect from frame N+1. A click is therefore a two-frame press and release at the position set before or in the same frame.
  - Holding and dragging are not supported.
- **Helpers:**
  - **Move to a component, transform or GameObject:** project its world position through `Camera.main.WorldToScreenPoint`, with z set to 0.
  - **Click(component, transform or GameObject):** move there, then left-click.
  - **Click(Vector3):** treats the value as a **screen** position (no projection), despite the parameter name.
  - Left-click, right-click and middle-click at the current position.
  - Move to the screen centre (width/2, height/2), and click or right-click the centre.
  - **Get the mouse position for a component** without moving the mouse.
  - **Hide:** set the position to (0,0,0), the bottom-left corner. **"Is hidden"** means the position is exactly (0,0,0).
  - Every move is debug-logged when the position changes.
- **Number keys** (`TryGetPressedNumKey`): the first of 0–9 down this frame, top row or keypad. 1–9 give 1–9, and **0 gives 10**. None gives null. No modifier check. Used by gameplay screens (choices, targets) and the collection.
- **AnyKeyUp:** true on the first frame after all keys have been released.

### 4.6 Text input

- `AllowTextInput` / `DisallowTextInput` set a flag. While it is set, HandleKeyboardInput does nothing, so HSA ignores keys and they are free for typing. UIM still skips the game's keyboard chain.
- **Set** by: collection search, chat and quick chat, the friend list's add-friend field, the store quantity prompt, the mass pack-opening quantity prompt, and the cheat console.
- **Cleared** by: UIM's text-input reset (`ClearTextInputVars`, when a game text field finishes or is cancelled), and the chat code.

---

## 5. Components

All widgets speak through their parent component (focus filter, 2.5). All reads are non-interrupting appends. The `HandleAccessibleInput` methods return true when they consumed the key.

### 5.1 AccessibleMenu (vertical list of text options)

- **Constructor:** parent, menu name, go-back action (may be null), global flag, can-confirm-with-Space flag (local menus only).
- **Options.** Each option has:
  - either fixed text or a **get-text delegate**, which is called each time the option is read, for live values such as checkbox states;
  - a click action;
  - optionally an on-read action, run after each read (fixed-text options only);
  - optionally a **hotkey**: any AccessibleKey that, when pressed, runs the click action directly, without reading and without moving the index.
- **Option speech:** `LK.MENU_OPTION_FORMAT` "{0} {1} of {2}" (name, 1-based position, count), curated. Example: "Play 1 of 5." There is no separator between name and position.
- **Keys, checked in this order (the first match wins):**

| Action | Local menu | Global menu |
|---|---|---|
| Next | Down or Tab | Down or Tab (global twins) |
| Previous | Up or Shift+Tab | Up or Shift+Tab |
| Re-read current | Shift+Up | Shift+Up |
| First | Home | **Shift+Home** |
| Last | End | **Shift+End** |
| Select | Enter, or Space if enabled | Enter |
| Back | Backspace (only if a back action exists) | Backspace |
| Hotkeys | any registered key | any registered key |

- **No wrapping.** At either end, Next and Previous do nothing and say nothing (they return false).
- **Tab means the same as Down.** There is no "valid" filtering.
- **No letter search.** No number keys.
- **Selecting before reading:** Enter does nothing until the menu has started reading (StartReading or ReadCurrentOption with at least one option). An empty menu also ignores Enter.
- **StartReading(readMenuName = true):** speaks the menu name if asked, even if it is empty or null; then reads the current option, which also starts reading. With no options it says `LK.MENU_NO_ITEMS` "No items".
- **ReadCurrentOption:** the same, without the name.
- **SetIndex(i):** sets the index with no range check and no speech.
- **Clear:** removes all options and resets the index to 0. The reading state stays.
- **GetNumItems.**
- **Help:**
  - no options: "No items." plus `LK.PRESS_KEY_TO_GO_BACK` "Press Backspace to go back", joined with CombineSentences;
  - otherwise: `LK.MENU_HELP_WITH_BACK_BUTTON` "Use the up and down arrow keys to navigate the menu. Press {Enter} to select an option. Press {Backspace} to go back", or `LK.MENU_HELP_NO_BACK_BUTTON` if there is no back action.
- **Global menus** (options menu, dropdowns) use the global key twins, so they keep working when screen keys are blocked.

### 5.2 AccessibleHorizontalMenu&lt;T : AccessibleItem&gt;

- **Options** are items, each with several lines (5.3), plus a click action and an optional on-read action. Exceptions from on-read are caught.
- **Keys:**

| Key | Action |
|---|---|
| Right / Left | next / previous option, no wrap |
| Home / End | first / last option |
| Enter | select (needs reading started) |
| Backspace | back, if a back action exists |
| any other key, once reading has started | passed to the current item (Up/Down lines and so on) |

- **Reading an option:**
  - The item's line position is reset to 0.
  - Line 0 is spoken as "{line} {n} of {count}". It is spoken even if empty, unlike the list in 5.4.
  - Then the on-read action runs.
- **StartReading:** always speaks the name, then reads the current option, or says "No items". **ReadCurrentOption** is the same without the name.
- **Other methods:** SetIndex, GetNumItems, and GetOptionBeingRead.
- **Help:** `LK.MENU_HORIZONTAL_HELP_WITH_BACK_BUTTON` / `..._NO_BACK_BUTTON`: "Use the left and right arrow keys to navigate the menu. Use the up and down arrow keys to read the options. Press {0} to select an option[. Press {1} to go back]". With no options, the same no-items help as 5.1.

### 5.3 AccessibleItem (an item with lines) and AccessibleMultilineText

- **Lines:** an item supplies its lines through an overridable method. **They are fetched again on every read**, so they stay live. The item keeps a current line position, which starts at 0 or at a given start line.
- **Keys** (handled when the parent passes them on):

| Key | Action |
|---|---|
| Down | next line. At the last line: stay, say nothing. |
| Up | previous line. At line 0: stay, say nothing. |
| Shift+Up | re-read the current line |
| Shift+Down | read from the current line to the end, one utterance per line. It only refreshes the lines when the position is 0. |
| PageDown / PageUp | only if the item has a "page two" position above 0 (used to jump to related cards, such as Colossal parts): jump to that line, or back to line 0, and read it. These keys are not reported as consumed. |

- **Finished-reading listener:** a one-shot callback fired when Down **reaches** the last line. It does not fire when the item has a single line.
- **Reset:** sets the position to 0. Lists and horizontal menus call it whenever an item gets focus.
- Reading a line past the end says nothing. Getting a line past the end returns "".
- **AccessibleMultilineText** is an item with a fixed, appendable list of lines. It can be built from:
  - a list of strings, optionally with a start line;
  - several strings;
  - several game text labels (UberText), keeping only labels that are active, enabled, not hidden and not empty.

### 5.4 AccessibleListOfItems&lt;T : AccessibleItem&gt;

- **Constructor:** parent, items, optional "no items" text, for example a dynamic "no results" message.
- **Hooks:**
  - a go-back action (settable);
  - Tab and Shift+Tab actions;
  - Home and End actions;
  - an on-read-item callback;
  - an **item-index callback** (the displayed number for an item);
  - a **count override** (the total shown, for scrolling lists that load only part of their items).
- **Keys, in order:**
  1. **Pre-pass (not exclusive):**
     - Backspace runs the go-back action if one is set;
     - Tab runs the Tab action if one is set;
     - Shift+Tab runs the Shift+Tab action.
     - Processing then continues with the same key.
  2. **Empty list with a "no items" text:** any of these keys speaks the no-items text: Left, Right, Tab, Shift+Tab, Home, End, Up, Down, Shift+Up or Shift+Down. Nothing else is done. An empty list **without** that text falls through to the next check and does nothing.
  3. **Not reading yet** (no item has focus): stop here. StartReading or a Home/End path must come first.
  4. **Right / Left:** next / previous item. **No wrap.** At an end: nothing, and the key is not consumed.
  5. **Home:** run the Home action if set; else jump to the first item and read it.
  6. **End:** run the End action if set; else jump to the last item and read it.
  7. **Otherwise, the item:** the focused item handles Up, Down, Shift+Up, Shift+Down and the page keys.
  8. **Tab / Shift+Tab:** if no Tab action (or Shift+Tab action) is set, next / previous item **with wrap-around**.
- **Reading an item:**
  1. Set the index and focus the item, then reset its line position.
  2. If line 0 is not empty, say "{line 0} {n} of {count}". Here n is the item-index callback's result if set, else index+1; count is the count override if set, else the number of items.
  3. Run the on-read-item callback.
- **StartReading:** reads the item at the current index, or says the no-items text (else "No items"). **StartReadingFromIndex(i)** sets the index first.
- **StartReadingReverse:** moves forward by (count−1) from the **current** index. That only reaches the last item when the current index is 0.
- **GetItemBeingRead:** the item at the current index, or null if the index is out of range. **GetItemBeingReadIndex:** the current index. **Items** and **Count** are also exposed.
- **UpdateItems(items, preserveFocus = false):**
  - Replaces the list.
  - Empty: the index becomes 0. Otherwise the index is clamped to the last item.
  - With preserveFocus while reading: the index follows the focused item if it is still in the list; else the index becomes 0 and that item gets focus.
  - **Without** preserveFocus, the focused item is not updated and may be stale.
- **RemoveItem(item):** equals UpdateItems with that item removed, without preserving focus.
- **Help(hasBackButton):** the horizontal help (left/right to navigate), or the no-items help.

### 5.5 Small widgets

- **AccessibleCheckBox:** a label plus either a game CheckBox or a toggle action and a state getter.
  - Its text is `LK.OPTIONS_MENU_CHECKBOX_LABEL` "{label} checkbox", then a space, then "Checked" or "Not checked".
  - **Toggle:** releases the game checkbox or runs the action, then speaks the new state word.
- **AccessibleDropdownControl:** a label plus a game DropdownControl, wrapped in a **global** menu of the dropdown's items. Its text is "{label} {selected item}".
  - **StartReading:** sets the menu index to the selected item, reads the menu (with an empty name, which may be spoken as nothing), and opens the game dropdown.
  - **Enter on an option:** releases that item and returns to the owner. **Backspace:** releases the dropdown's cancel catcher and returns to the owner.
- **AccessibleScrollbarControl:** a label plus a game ScrollbarControl.
  - Global Left and Right change the value by ±0.1, fire the update and finish events, and speak the value as a whole-number percentage ("50", no % sign).
  - Global Backspace or Enter returns to the owner.
  - Its text is "{label} {percent}".

---

## 6. Localization

### 6.1 How the strings are loaded

- HSA adds the string category **ACCESSIBILITY** to the game's `Global.GameStringCategory` enum. The game's own loader therefore reads `Strings/<locale>/ACCESSIBILITY.txt`. It uses the same tab-separated format as the game's files (TAG, TEXT, COMMENT), and is copied into the game's Strings folder by the HSA installer. The game ships enUS plus deDE, esES, esMX, frFR, itIT, jaJP, koKR, plPL, ptBR, ruRU, thTH and zhCN.
- **Fallback table:** HSA's DefaultGameStrings is a trimmed copy of `GameStrings` hard-wired to enUS and the en-US culture. It loads the enUS ACCESSIBILITY table in three places: alongside the game's ACCESSIBILITY category job in `GameStrings.Job_LoadAll`, at the end of `GameStrings.LoadAll`, and in `LoadNative`.
- **LocalizationKey** is a registry of named keys. Each key wraps one tag string. A duplicate tag throws at type load. Most tags are `ACCESSIBILITY_*`, but some keys point at game tags such as `GLOBAL_TOOLTIP_MENU_HEADER` or `GLOBAL_OPTIONS_SOUND_*_LABEL`. Those resolve through the game tables and have no enUS fallback unless the game has them.
- **LocalizationUtils:**
  - **Get(key):** the current-locale `GameStrings.Get` if the game has the tag; else the enUS fallback; else **the tag string itself**.
  - **Format(key, args):** the same chain. The fallback uses `string.Format` with the en-US culture, then the game's language rules (`|4(...)` plurals). If the tag is missing everywhere, it returns the **raw tag, unformatted**.
  - **FormatPlurals(key, pluralNumbers, args):** the same, with explicit plural numbers.
  - **HasKey(key):** current locale only, no fallback. It was used to decide whether recorded narration applies.
  - **Key-name override:** "ACCESSIBILITY_INPUT_KEY_OVERRIDE_{KeyCode}", **current locale only** (see 4.1).
  - **Period / sentence-ending characters:** `LK.FORMATTING_PERIOD` / `LK.FORMATTING_SENTENCE_ENDING_CHARACTERS` (with fallback).
  - **Ordinals 1–10:** `LK.FORMATTING_ORDINAL_NUMBER_n` if the current locale has the tag (no fallback), else digits. IsOrdinalNumber checks a word against that list.
- **LocalizedText** gives named shortcuts for common strings, for example `MENU_NO_ITEMS`, `GLOBAL_LOADING`, `UI_POPUP`, the keyword names, and "your turn".
- **AccessibleSpeech** builds composite phrases: "Press Enter to continue", menu help with key names inserted, "{name} {n} of {count}", and so on.

### 6.2 Strings the core itself speaks (enUS)

| Key (tag) | enUS text | When |
|---|---|---|
| GLOBAL_ACCESSIBILITY_ON / _OFF | Accessibility on / Accessibility off | F8 |
| GLOBAL_GAME_SPEED | Game speed {0} | F11/F12 change |
| GLOBAL_GAME_SPEED_CANNOT_ADJUST_VS_PLAYERS | You cannot adjust the game speed when playing against Human opponents | F11/F12 in PvP or Battlegrounds |
| GLOBAL_LOADING, GLOBAL_PLEASE_WAIT | Loading / Please wait | F1 with no screen or UI |
| PRESS_KEY_TO_CONTINUE | Press {0} to continue | F1 with a forced key or notification |
| GLOBAL_CLOSING_GAME | Closing game | quit |
| GLOBAL_NEW_HEARTHSTONE_ACCESS_VERSION_AVAILABLE | A new version of Hearthstone Access is available. ... | disabled |
| MENU_OPTION_FORMAT | {0} {1} of {2} | every menu or list option |
| LIST_NO_ITEMS (LK.MENU_NO_ITEMS) | No items | empty menus and lists |
| MENU_HELP_NO_BACK_BUTTON / _WITH_BACK_BUTTON | Use the up and down arrow keys to navigate the menu. Press {0} to select an option[. Press {1} to go back] | menu help |
| MENU_HORIZONTAL_HELP_NO_BACK_BUTTON / _WITH_BACK_BUTTON | Use the left and right arrow keys to navigate the menu. Use the up and down arrow keys to read the options. Press {0} to select an option[. Press {1} to go back] | horizontal menu and list help |
| PRESS_KEY_TO_GO_BACK | Press {0} to go back | empty-menu help |
| UI_POPUP | Popup | popup titles |
| UI_UNKNOWN_DIALOG / UI_UNKNOWN_POPUP | This dialog/popup hasn't been made accessible yet. Please get help from someone sighted | help of unhandled dialogs and popups |
| OPTIONS_MENU_CHECKBOX_LABEL, _CHECKED, _NOT_CHECKED | {0} checkbox / Checked / Not checked | checkboxes |
| INPUT_COMMAND_WITH_MODIFIER_FORMAT / _WITH_CTRL_FORMAT | Shift + {0} / Ctrl + {0} | key names |
| INPUT_KEY_OVERRIDE_Return / _A / _I | Enter / eh / eye | key names |
| FORMATTING_PERIOD, _SENTENCE_ENDING_CHARACTERS, _LIST_SEPARATOR, _LIST_FINAL_SEPARATOR | "." / ".!?;" / "," / "and" | curation and lists |
| FORMATTING_ORDINAL_NUMBER_1..10 | first ... tenth | duplicate names |
| GLOBAL_CARD / GLOBAL_CARD_PLURAL | card / cards | HumanizeNames |
| GAMEPLAY_DIFF_MULTIPLE_ENTITIES | {0} {1} | HumanizeNames |
| GLOBAL_STREAMER_MODE_ON / _OFF | Streamer mode on / off | Ctrl+Shift+S. Only reachable when accessibility is **off**, because BaseUI keyboard handling is skipped while on. |
| GAMEPLAY_N_SECONDS_REMAINING | (gameplay) | timer reaching 10 |

---

## 7. Options HSA stores

All five are added to the game's `Option` enum (with a description string, which is the stored name), to `ClientOption`, and to `OptionDataTables` (type and default). They are saved like any client option: locally, by the game's Options system.

| Option (stored name) | Type | Default | Set by | Effect |
|---|---|---|---|---|
| ACCESSIBILITY_GAME_SPEED (`accessibilityGameSpeed`) | int 1–10 | 3 | F11/F12 | speech pacing, CPS (3.6) |
| ACCESSIBILITY_BACKGROUND_SPEECH (`accessibilityBackgroundSpeech`) | bool | true | options menu "Speech in background" checkbox | false: speech is dropped while the window has no focus |
| ACCESSIBILITY_AUTO_ATTACK_SPEED (`accessibilityAutoAttackSpeed`) | float | 1.0 | options menu "Auto Attack Speed: {x}", a submenu with Slow (1.0), Medium (0.8), Fast (0.5) and Fastest (0.1) | default delay between automated attacks in gameplay |
| ACCESSIBILITY_SAVE_BATTLE_LOGS (`accessibilitySaveBattleLogs`) | bool | false | options menu checkbox "Automatically Save Battle history Logs to Battle Logs folder" | gameplay saves the history log at the end of a game |
| ACCESSIBILITY_BATTLEGROUNDS_NARRATE_ATTACKS (`accessibilityBattlegroundsNarrateAttacks`) | bool | true | a Battlegrounds key toggle | narrate combat attacks in Battlegrounds |

Not persisted: the accessibility on/off flag (always on at launch), and debug logging (options checkbox "Enable debug logging for this run").

**The options menu** while on is HSA's own UI on the game's OptionsMenu. It is a **global** menu titled "Options Menu", whose back action hides the menu. Its entries, in order:
1. Sound options (opens the game's sound menu)
2. Speech in background
3. Auto attack speed
4. Save battle logs
5. Resolution, quality and frame rate (dropdowns)
6. Fullscreen
7. Allow spectators
8. Screen shake
9. Debug logging

Help is given only in the main state.

---

## 8. Notable quirks to decide on

1. Speech keeps running while accessibility is "off" (F8). Only input and the hooks change.
2. HSA interrupts on a key press only for SAPI. With real screen readers it relies on the reader's own keyboard echo and interrupt. Our Windows Prism shim reports OneCore as "SAPI"; macOS silences in the dylib.
3. Plain Output never interrupts unless asked. Speech from components without focus is dropped, not deferred.
4. The wait time uses integer division: short gameplay lines do not wait at all.
5. Interrupting the queue also silently removes the first queued narration item.
6. The optimizer and two curation rules ("ALL" → "all" and so on) are English-only and can damage other all-caps words.
7. Menus do not wrap. Lists wrap only on Tab. A global menu's Home/End needs Shift.
8. A pop that exposes another UI does not re-read it. Screens are re-read only when the last UI closes and no dialog is queued.
9. The forced-key, notification-button and key-blocking APIs are partly or entirely unused.
10. Global keys (Escape, F11/F12) do not consume the key: the focused component also sees the same press.
11. `Format` with a missing tag returns the raw tag. Key-name overrides and ordinals ignore the enUS fallback.
12. A virtual click takes two frames. "Hidden" means screen position (0,0).
