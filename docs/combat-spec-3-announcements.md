# Combat spec, part 3: what a match announces

This spec describes how Hearthstone Access (HSA) announces what happens during a traditional Hearthstone match, so we can rebuild the same behaviour from scratch. It describes behaviour only. HSA has no license, so none of its code is reproduced here. HSA class and method names are used only as reference points. Game names (GameState, PowerTaskList, Entity, ...) are used freely.

Source: `C:/ProgramData/HearthstoneAccess/downloads/hsa.diff.patch`, mainly `AccessiblePowerTaskListDescriber`, `AccessiblePlayDescriber`, `AccessibleInGameState`, `AccessibleGameplay`, `AccessibleSpeechUtils`, `AccessibleHistoryMgr`, and the game files HSA patched to call them. Checked against the vanilla `Assembly-CSharp.dll`.

Scope: constructed, arena, casual, practice, adventures and tutorials. Battlegrounds swaps in subclasses of the two describers and adds many special cases. It is marked "BG" where it changes a shared rule, and otherwise left out.

Related specs:
- `core-spec.md` covers the speech queue (3.1–3.4), wait items and game speed (3.6), text curation (3.7), the speech optimizer (3.8) and list helpers (3.9). This spec does not repeat them.
- `combat-spec-1-navigation-reading.md` covers keys, focus and card reading.

Notation:
- `LK.X` is HSA's localization key X. Its tag is `ACCESSIBILITY_X` unless another tag is given. Texts are enUS, from `Strings/enUS/ACCESSIBILITY.txt` or the game's own string files.
- `{0}`, `{1}` are format arguments. `|4(a,b)` is the game's plural syntax.
- "PTL" means one `PowerTaskList`. "Utterance" means one item in the speech queue.
- "Wait output" is `OutputAndWait` (core 3.1): no focus filter, it blocks the HSA queue for an estimated time, and it has an end callback. "Plain output" is `Output(gameplayScreen, text)`: focus-filtered and does not wait. "Notification output" is `OutputNotification`: no focus filter, does not wait.

---

## 0. The parts at a glance

| Part (HSA name) | Role |
|---|---|
| Task-list describer (`AccessiblePowerTaskListDescriber`) | The main announcer. When each PTL finishes, it builds one utterance: the action line (play, attack, fatigue, trigger, burn), turn lines, and a description of everything that changed since the last snapshot. While that utterance is spoken, it holds back the game's power queue. |
| Play describer (`AccessiblePlayDescriber`) | Builds the action lines. It also speaks two things early, as soon as the game shows them: the "big card" (the card shown large when the opponent plays or a trigger fires) and the attack, at the moment the attacker launches. It remembers which PTLs it has described, so nothing is said twice. |
| Game-state snapshot (`AccessibleInGameState`) | Takes a copy of the whole board and compares two copies (the "diff"). The diff becomes sentences such as "Your opponent's minions took 2 damage". |
| Gameplay screen (`AccessibleGameplay`) | Mulligan, coin, "You're playing against", game over, choices, errors. Its outputs are plain outputs, so they are focus-filtered. |
| History log (`AccessibleHistoryMgr`) | Keeps the announcements in a list. Y reads it (spec 1). It can be saved to a file at game end. |

---

## 1. Where HSA hooks in, and what it reads

### 1.1 Hook points used for announcements

| Game type.method | Where | HSA call | Purpose |
|---|---|---|---|
| `Gameplay.Start` | first line | gameplay screen "screen start" | Resets the screen state and the history log, creates the screen (BG subclass if in Battlegrounds), sets it as the current screen. |
| `GameState.FireCreateGameEvent` | first line (runs from `GameState.OnTaskListEnded` when a PTL holds `CREATE_GAME`) | describer "game start" | Resets the describer: no last snapshot, no snapshotting, no deferred lines, step INVALID, game not officially started, turn not started, end-turn counter 0. Resets the play describer's "described" set and the weapon-sheathe sets. It does **not** reset the play describer's busy counter (see 7.3). |
| `PowerTask.DoTask`, `PowerTask.DoRealTimeTask`, and the early-concede task variant | first line, for a task not yet completed | PTL "task started" → describer "PTL start" (once per PTL) | HSA adds a task-start callback to `PowerTask`, and `PowerTaskList.CreateTask` wires every task to it. The first task that runs in a PTL calls "PTL start". That call only registers a "PTL complete" callback on the PTL and logs the block type. A PTL with no tasks never starts, so it is never described. |
| `GameState.OnTaskListEnded(taskList)` | after the `CREATE_GAME` loop, **before** `RemoveQueuedEntitiesFromGame()` | PTL "fire complete" → describer "PTL end" | The main announcement point (section 2). `PowerProcessor.EndCurrentTaskList` calls `OnTaskListEnded` after every task in the PTL has run (animations included). |
| `GameState.CanProcessPowerQueue` | just before the final `return true` | returns false if the task-list describer **or** the play describer is busy (only while accessibility is on) | This is how gameplay waits for speech (section 7). |
| `HistoryCard.ShowBigCard(path)` | end of the "entity not null" branch, after the big card starts moving | gameplay screen "big card shown" | If the history entry type is CARD_PLAYED → play describer "big card played". If it is TRIGGER → "big card triggered". |
| `HistoryManager.CreatePlayedBigCard`, `CreateTriggeredBigCard`, `CreateFastBigCardFromMetaData` | signature changed | gains a "origin PTL" parameter, stored in the big-card entry and passed to `HistoryCard.LoadBigCard` | So the big-card announcement knows which PTL it belongs to. `PowerProcessor` passes `m_currentTaskList` at every call site. |
| `HistoryManager.CreatePlayedBigCard` / `CreateTriggeredBigCard` | start | the "game entity doesn't show big cards → finish now" early exit is skipped while accessibility is on | Some tutorials hide big cards. HSA needs them as announcement triggers. |
| `PowerProcessor.ShouldShowPlayedBigCard` / `ShouldShowTriggeredBigCard` | made static/internal | read by the play describer | The same "would the game show a big card" rules decide the short or long play text. |
| `AttackSpellController.AddPowerSourceAndTargets` | end | keeps the PTL | For the next row. |
| `AttackSpellController.LaunchAttack` | first line | play describer "attack" | "X attacked Y" is spoken the moment the attacker lunges, not when the PTL ends. |
| `TurnStartManager.DisplayTwoScoops` | inside the "indicator exists" branch, after `Show()` and the "your turn" sound | gameplay screen "turn start" → describer "turn start" | "Your Turn. You have N mana." plus deferred lines (section 6). |
| `MulliganManager.DealStartingCards` | before the deal loop; at the coin flip (after `CoinEffect.DoAnim`); after `OnMulliganCardsDealt`; after the first-time mulligan check | "starting hand", "coin result", "cards dealt" (does nothing), "mulligan choice start" | Section 8. |
| `MulliganManager.ShowMultiplayerWaitingArea` | the same "mulligan choice start" call; an "enter waiting area" call in the skip branch (BG only) | | |
| `MulliganManager.WaitForOpponentToFinishMulligan` | first line | "waiting for opponent" | |
| `MulliganManager.EndMulligan` | first line | "end mulligan" | Speaks the replacement cards and registers the game-over listener. |
| `MulliganManager.CoinCardSummonFinishedCallback` | first line | "you get the coin" | |
| `Banner.ShowHearthstoneAnomalyIntro` | end | reads the anomalies, adds them to history | |
| `TurnTimer.Update` / `MulliganTimer` update | each frame | timer check | "10 seconds remaining". |
| `GameplayErrorManager` show-message method | after the error sound | plain output of the error text | For example "Not enough mana". |
| `JoustSpellController.ShowJouster` (end), `PlayNoJousterSpell` | | plain output + history | Joust reveals. |
| `NotificationManager` popup text, speech bubbles, quotes | at creation | notification output of the bubble text, only for those created without sound | Section 9.4. |
| `MissionEntity` turn-counter update (HSA-added) | on counter change | plain output "{n} {label}" | Adventure boss counters. |
| `EndGameScreen.Show` / shown | | sets the end screen as current screen | Says nothing itself. |

Game files that HSA patched but that do nothing for announcements: `Card.cs` "draw unknown opponent card", "reveal drawn opponent card" and "card to deck" call empty methods. `OnMulliganCardsDealt` is empty. The gameplay screen's "reconnected" method is never called (section 10.4).

### 1.2 Data read

- **PowerTaskList:** `GetBlockStart()`, `GetBlockType()` / `IsBlockType(t)`, `IsOrigin()` (true for the first PTL of a block; a block is split into several PTLs around nested blocks), `IsPlayBlock()` (PLAY), `IsTriggerBlock()` (TRIGGER), `IsBlockType(FATIGUE / ATTACK / DECK_ACTION)`, `GetSourceEntity()` (first entity of the block start), `GetAttacker()`, `GetDefender()`, `GetProposedDefender()`, `GetTaskList()`, `GetId()`.
- **PowerTask / PowerHistory:** `GetPower()`. HSA looks for:
  - `META_DATA` with meta type `BURNED_CARD`: its `Info` list holds the burned entity ids;
  - `TAG_CHANGE` of `IS_USING_TRADE_OPTION` = 1, or a `SHOW_ENTITY` of the source whose tags include `IS_USING_TRADE_OPTION` = 1: a trade;
  - `SHOW_ENTITY` whose tags include `START_OF_GAME_KEYWORD` = 1: a start-of-game reveal.
- **Game entity tags:** `STEP` and `NEXT_STEP` (`TAG_STEP`).
- **GameState:** `IsGameCreated`, `GetCurrentPlayer`, `GetFriendlySidePlayer`, `GetOpposingSidePlayer`, `IsFriendlySidePlayerTurn`, `GetTurn`, `GetEntity(id)`, `GetEntityMap`, `IsMulliganPhase`, `IsMulliganManagerActive`, `RegisterGameOverListener`.
- **Player:** `GetNumAvailableResources()`, tag `RESOURCES`, `TotalSpellpower(self)`, `GetNumTurnsInPlay`, `GetHero`, `GetHeroPower`, `GetWeaponCard`, `HasWeapon`, and the zones Hand, Deck, Graveyard, Battlefield and Secret (`Zone.GetCards()`).
- **Entity:** zone, controller and side, card type and kind tests (IsMinion, IsWeapon, IsSpell, IsHero, IsHeroPower, IsSecret, IsQuest, IsSideQuest, IsQuestline, IsLocation, IsObjective, IsCharacter, IsGame), `GetName`, `GetClass`, `GetATK`, `GetHealth` (max health), `GetCurrentHealth`, `GetDamage`, `GetArmor`, the keyword tests (Taunt, Deathrattle, Battlecry, Charge, Lifesteal, Rush, Windfury, Divine Shield, Reborn, Frozen, Dormant, Immune, Poisonous, Stealthed, Silenced, Exhausted), and the tags `FREEZE`, `ELUSIVE`, `MAGNET`, `VALEERASHADOW`, `EVIL_GLOW` (cursed), `CANT_BE_ATTACKED`, `UNTOUCHABLE`, `QUEST_PROGRESS`, `QUEST_PROGRESS_TOTAL`, `REVEALED` (`IsRevealed`). Also `GetCardTextBuilder().BuildCardTextInHand(entity)`.
- **Entity clone:** HSA adds `Entity.CloneForAccessibility()`, a detached copy that keeps the card id, all tags, the card reference, sub-card ids, load state and creator names. Its EntityDef is the current one, unless that has no valid display name and an EntityDef was saved at the last HideEntity (HSA records it in the hide-entity path); then the saved one is used. Reason: since patch 28.2 an entity can be shown and hidden again inside one PTL, and a snapshot taken at the PTL end would otherwise see an unnamed card.

---

## 2. The task-list describer pipeline

### 2.1 State it keeps (per game)

| Field | Meaning |
|---|---|
| last snapshot | The board copy from the last described PTL (null at game start). |
| blocked | True from "PTL end" until its utterance finishes (or until it is decided that nothing will be said now). |
| can snapshot | Whether diffs are taken at all yet (2.3). |
| waiting for "your turn" banner | While true, every utterance is deferred instead of spoken. |
| deferred lines | A list of lines held until the next "turn start". |
| previous step | The STEP value at the last PTL end. |
| game officially started | Becomes true the first time a PTL ends while STEP = MAIN_READY. Tutorials have no mulligan, so this is the start signal for them too. |
| turn started | Set by the turn change at MAIN_READY. Cleared at turn end. |

### 2.2 "PTL end", step by step

Runs for every PTL that executed at least one task, once the game is created.

1. Set **blocked**.
2. Start an empty line list. "Can describe" = game officially started (2.1).
3. **Action line** (only one of these, only for an **origin** PTL):
   - PLAY block → the play line (3.1).
   - FATIGUE block → the fatigue line (3.5).
   - ATTACK block → the attack line (3.3), usually empty because it was already spoken at launch. Then "can describe" becomes **false**, unless the attacker or the defender is a hero. HSA's reason: the damage matters most when a hero is involved.
4. **Trigger line:** if the PTL is an origin TRIGGER block, add the trigger line (3.2). This is separate from step 3, but a block has only one type, so in practice it never adds to a play line.
5. **Burn lines:** for every `BURNED_CARD` metadata entity in this PTL, one burn line (3.6).
6. (BG: in the shop phase "can describe" is forced true.)
7. **Step handling and snapshot**, section 2.3.

If anything throws, blocked is cleared and the error is logged.

### 2.3 Step handling, snapshot and output

Read the game entity's STEP. Then, in this order (only the first matching branch runs):

| STEP now | What happens |
|---|---|
| MAIN_END | If the step changed since the last PTL end (and BG hero-attack bookkeeping). If also **turn started**: add `LK.GAMEPLAY_TURN_ENDED` "Turn ended" (not in BG), run "turn ended" (6.3), set can-snapshot. |
| MAIN_READY | Add the turn-change lines (6.1). Set can-snapshot. |
| MAIN_START_TRIGGERS | Set can-snapshot. |
| MAIN_START | Set can-snapshot. |
| MAIN_ACTION, and the step changed | BG hook only. |
| BEGIN_MULLIGAN | Set can-snapshot. If it is the friendly player's turn and the turn has not started, set **waiting for banner**. Reason: start-of-game cards (C'Thun-style reveals) trigger during the mulligan, and the "Your Turn" banner comes first. |
| previous step was BEGIN_MULLIGAN and the step changed | Clear can-snapshot. |

Then:
1. Remember the step as "previous step".
2. If there is **no last snapshot yet**, set can-snapshot (BG also uses this as "first PTL", for reconnects).
3. **If can-snapshot is off:** if waiting for the banner, add the lines to the deferred list and clear blocked. Otherwise output the lines (2.4). **Stop here.** No snapshot is taken, so the changes stay pending and appear in the next diff.
4. Take a new snapshot (section 4.1).
5. If "can describe": compute the diff text between the last snapshot and the new one (section 4) and add it as one more line.
6. The new snapshot becomes the last snapshot, **whether or not it was described**. So changes in an undescribed PTL (a minion-only attack, or anything before the game officially started) are swallowed: they never appear in a later diff.
7. **Can output?** Not in BG: true unless waiting for the banner. BG has its own rules.
   - Yes → output the lines (2.4).
   - No → add the lines to the deferred list, clear blocked.
8. BG hooks (opponent hero changed, a hero gained attack, Tarecgosa) run last.

### 2.4 Output

- No lines (or all empty) → clear blocked. Nothing is said.
- Otherwise join the lines with the sentence joiner (core 3.9 CombineLines: skip empty lines; add a period before the next line if the text so far does not end with a sentence-ending character; one space) and send the result as a **wait output**. Its end callback clears blocked. The same text is added to the history log.
- So one PTL produces **at most one utterance**, made of several sentences.

**Line order in one utterance:** action line → trigger line → burn lines → "Turn ended" or turn-change lines → the diff text (whose own order is in 4.8).

Because a game action is split across several PTLs (the play block, nested POWER/TRIGGER/DEATHS blocks, continuations), one card play usually produces several utterances in a row, for example "You played Flamestrike." then "Your opponent's minions took 5 damage." then "Your opponent's minions died."

---

## 3. Action lines (play describer)

The play describer remembers every PTL it has described (a set of PTL objects, cleared at game start). Every action-line producer first checks this set and gives nothing if the PTL is already in it, then adds it. That is how a line spoken early (big card, attack launch) is not repeated at "PTL end".

### 3.1 A card is played (PLAY block)

Source = clone of the PTL source entity. "Friendly" = controlled by the friendly-side player.

**Which text:** if the game would show a played big card for this source and block start (vanilla `PowerProcessor.ShouldShowPlayedBigCard`: the game uses big cards **and** one of these: decision input is not permitted, which includes spectating and the opponent's turn; the source is controlled by the opposing side; the block start has `ForceShowBigCard`; it is a Mercenaries ability), the **long** text is used (3.1.2). Otherwise the **short** text is used (3.1.1).

In practice: the opponent's plays get the long text, normally spoken early at the big card (3.4). Your own plays get the short text at "PTL end".

#### 3.1.1 Short play texts

`{0}` = the card's own name (`Entity.GetName()`; BG adds "golden").

| Card | Friendly | Opponent |
|---|---|---|
| Trade (never reached, see quirk 6) | `LK.GAMEPLAY_PLAYER_TRADED_CARD` "You traded {0}" | `LK.GAMEPLAY_OPPONENT_TRADED_CARD` "Your opponent traded {0}" |
| Minion | `LK.GAMEPLAY_PLAYER_SUMMONED_MINION` "You summoned {0}" | `LK.GAMEPLAY_OPPONENT_SUMMONED_MINION` "Your opponent summoned {0}" |
| Weapon | `LK.GAMEPLAY_PLAYER_EQUIPPED_WEAPON` "You equipped {0}" | `LK.GAMEPLAY_OPPONENT_EQUIPPED_WEAPON` "Your opponent equipped {0}" |
| Hero power | `LK.GAMEPLAY_PLAYER_USED_HERO_POWER` "You used {0}" | `LK.GAMEPLAY_OPPONENT_USED_HERO_POWER` "Your opponent used {0}" |
| Secret | `LK.GAMEPLAY_PLAYER_CAST_SECRET` "You cast {0}" | `LK.GAMEPLAY_OPPONENT_CAST_SECRET` "Your opponent cast 1 {0} secret", with {0} = the class name (`GameStrings.GetClassName`) |
| Anything else (spell, location, hero card, ...) | `LK.GAMEPLAY_PLAYER_PLAYED_CARD` "You played {0}" | `LK.GAMEPLAY_OPPONENT_PLAYED_CARD` "Your opponent played {0}" |

(BG: shop UI cards such as "Refresh" are skipped.)

#### 3.1.2 Long ("big card") play texts

`{text}` = `BuildCardTextInHand(entity)`; curation later removes the markup.

| Card | Friendly | Opponent |
|---|---|---|
| Trade | as short | as short |
| Minion | `..._PLAYER_SUMMONED_MINION_BIG_CARD` "You summoned {0}; {1} {2} {3}" (name, attack, health, text) | `..._OPPONENT_SUMMONED_MINION_BIG_CARD`, same layout. If the card hides its stats, attack and health are empty strings. |
| Spell | `..._PLAYER_PLAYED_CARD_BIG_CARD` "You played {0}; {1}" | `..._OPPONENT_PLAYED_CARD_BIG_CARD` "Your opponent played {0}; {1}" |
| Hero power | `..._PLAYER_USED_HERO_POWER_BIG_CARD` "You used {0}; {1}" | `..._OPPONENT_USED_HERO_POWER_BIG_CARD` |
| Weapon | `..._PLAYER_EQUIPPED_WEAPON_BIG_CARD` "You equipped {0}; {1} {2} {3}" (name, attack, durability = `GetHealth`, text) | `..._OPPONENT_EQUIPPED_WEAPON_BIG_CARD` |
| Secret | `..._PLAYER_CAST_SECRET_BIG_CARD` "You cast {0}; {1}" | the short opponent secret text (class only; the secret is never revealed) |
| Enchantment-type entity | the "played card" big texts | |
| Location | `..._PLAYER_PLAYED_LOCATION_BIG_CARD` "You played {0}; {1} durability {2}" (name, durability, text) | `..._OPPONENT_PLAYED_LOCATION_BIG_CARD` |
| Other types (hero cards, ...) | "" (logged). At "PTL end" this falls back to the short text. At a big card, nothing is said. | |

The ";" is a sentence-ending character in enUS, so curation keeps the parts as separate sentences.

### 3.2 A trigger fires (TRIGGER block, origin PTL)

Source = clone of the PTL source entity. Nothing is said if the source is missing, already described, or the game entity (Duels passive treasures trigger from the game entity).

1. (BG: hero-power triggers in the shop phase are silent.)
2. **Big-card trigger:** if the game would show a triggered big card (vanilla: the source is in HAND, not hidden, and has a trigger visual), **or** the source is controlled by the opposing side and any task in the PTL is a `SHOW_ENTITY` with `START_OF_GAME_KEYWORD`:
   - friendly: `LK.GAMEPLAY_PLAYER_CARD_TRIGGERED` "Your {0} triggered" ({0} = card name, no text);
   - opponent: `LK.GAMEPLAY_OPPONENT_CARD_TRIGGERED_BIG_CARD` "Your opponent's {0} triggered; {1}" ({1} = card text).
   - This text is also added to history on its own.
3. **Otherwise** a callout is made only if the source is a character (but **not a hero**), a weapon, a secret, a quest, questline or side quest, or a hero power: `LK.GAMEPLAY_CARD_IN_ZONE_TRIGGERED` "{0} triggered", with {0} = the full name in zone (4.10), for example "Your second Knife Juggler triggered", "Your opponent's HeroPower triggered". Spells, enchantments and heroes: no line.

So deathrattles, "whenever"/"after" effects, start/end-of-turn effects and secret reveals are called out by source. A **Battlecry** is a POWER block inside the PLAY block, not a TRIGGER, so it gets no line of its own. Only its effects appear in the diff.

**Secret reveals:** the game shows a triggered big card for every secret (`CreateTriggeredBigCard` with isSecret). So an opponent's secret is announced early as "Your opponent's Counterspell triggered; Secret: When your opponent casts a spell, Counter it." and your own as "Your Counterspell triggered".

### 3.3 Attacks

- **Text** (built once per PTL): if a proposed defender exists and differs from the actual defender: `LK.GAMEPLAY_ENTITY_ATTACKED_OTHER` "{0} attempted to attack {1} but attacked {2} instead" (attacker, proposed defender, actual defender). Otherwise `LK.GAMEPLAY_ENTITY_ATTACKED` "{0} attacked {1}". All names are full names in zone (4.10), for example "Your Raid Leader attacked your opponent's Hero". The text is added to history on its own.
- **When:** `AttackSpellController.LaunchAttack` → the text as a **wait output** (BG: only for minion attacks, and only if the "narrate attacks" option is on; hero attacks only flag the hero-attack phase). At "PTL end" of the ATTACK block the text is normally already described, so it is not repeated. If the launch never happened, it comes at "PTL end" instead.
- **What follows:** for minion-against-minion attacks the ATTACK PTL's diff is suppressed (2.2 step 3), so the damage is never said. The DEATHS block that follows says "X and Y died", which the optimizer (core 3.8) turns into "Both minions died." or "Your minion died.". When a hero is attacker or defender, the diff is described: "Your opponent's Hero took 3 damage", or with both damaged "Your Hero and your opponent's Raid Leader took 2 damage", which the optimizer turns into "Both characters took 2 damage.".

### 3.4 Big cards spoken early

`HistoryCard.ShowBigCard` → by history type:
- **CARD_PLAYED** → if the PTL is already described and is not a TRIGGER block, nothing. (A TRIGGER block may get a second, played-type big card; the Splendiferous Whizbang deck reveal is the example.) Otherwise the long play text (3.1.2) as a **wait output**, and the busy counter goes up (it goes down when that utterance ends). Mark the PTL described, add the text to history.
- **TRIGGER** → if already described, nothing. Skip BG shop hero powers. Otherwise the big-card trigger text (3.2 step 2) as a **wait output**, and the busy counter goes up. Mark described.
- Time: the big card is shown while the PTL is still running, so this is spoken **before** that PTL's "PTL end". The entity used is the history card's entity (or, for Titans, the ability entity the game shows).

### 3.5 Fatigue

FATIGUE block, origin: friendly → `LK.GAMEPLAY_PLAYER_DREW_CARD_FROM_EMPTY_DECK` "You tried to draw a card from an empty deck". Opponent → `LK.GAMEPLAY_OPPONENT_DREW_CARD_FROM_EMPTY_DECK`. The damage comes from the diff in the same utterance: "Your Hero took 3 damage".

### 3.6 Burned cards (hand full)

One line per `BURNED_CARD` entity: `LK.GAMEPLAY_PLAYER_DREW_CARD_BUT_BURNED` "You drew 1 {0} but it burned" or `LK.GAMEPLAY_OPPONENT_DREW_CARD_BUT_BURNED`, with {0} = the card's name (also for the opponent, since a burned card is shown). The same card also moves deck → graveyard in the diff, which is announced as a discard (4.6). So a burned card is likely mentioned twice.

---

## 4. The board diff

### 4.1 What a snapshot contains

Taken only when the game is created:
- the current STEP and current player;
- for each side: available mana (`GetNumAvailableResources`), mana crystals (tag RESOURCES), total spell power;
- clones of: both heroes, both hero powers, both weapons, and every card in both hands, decks, graveyards, battlefields and secret zones;
- outside BG: clones of every entity in the SETASIDE zone whose type is hero, spell, minion, weapon or location. Patchwerk-style hand destruction sends cards there.

The entity list keeps this order: heroes, hero powers, weapons, hands, decks, graveyards, battlefields, secrets, set-aside. Entities are matched between two snapshots by entity id.

### 4.2 Per-entity diff (one for every entity present in both snapshots)

Definitions:
- **Can live:** a character, weapon, objective (aura) or location.
- **Alive:** can live, and in PLAY or SECRET. **Dead:** can live, and in GRAVEYARD, REMOVEDFROMGAME or SETASIDE.
- **Died:** alive before and dead after. Exception: a hero that went PLAY → SETASIDE did not die (a hero card replaced it). If a weapon died and its controller now has no weapon, the hero is remembered as "weapon broke" (4.9).
- **Revealed:** it was in SECRET before, did not move, and its name changed.
- **Transformed:** it did not die, it is in the same zone, it was not revealed, and its name changed.
- **Moved:** (it did not die and its zone changed) **or** its controller changed.
- **Has changes:** it did not die, did not transform, and its description (4.3) is not empty.

### 4.3 Per-entity description (the "changes" sentence part)

Empty if: it died, transformed or was revealed; a hero card is being played (hand → play); either side has INVALID card type (unknown card); it is dead before and after; or it moved to a zone other than PLAY (stat changes are not called out when bouncing).

Otherwise parts are collected in this fixed order. Each part has a singular and a plural form (for "Your X" and for groups), shown as singular / plural:

1. Armor lost: "lost {n armor}" (`..._ENTITY_LOST_STATS` + `..._ENTITY_N_ARMOR` "{0} armor"). Armor comes first so it reads "lost 2 armor and took 3 damage".
2. Damage taken (characters only: damage tag increase): `LK.GAMEPLAY_DIFF_ENTITY_TOOK_N_DAMAGE` "took {0} damage". Weapon durability loss is computed but **not** said.
3. Healed (damage tag decrease): characters `..._ENTITY_RECOVERED_N_HEALTH` "recovered {0} health". Weapons: "{0} durability" goes into the **gained** list.
4. Became invulnerable (could be attacked before, cannot now: tags CANT_BE_ATTACKED / UNTOUCHABLE), unless in a tutorial or it also became immune: "became invulnerable".
5. Became cursed (EVIL_GLOW): "was cursed" / "were cursed".
6. Became haunted (VALEERASHADOW): "was haunted" / "were haunted".
7. A location stopped being exhausted: "reopened".
8. Became silenced: "was silenced" / "were silenced". **If silenced, step 9 is skipped.**
9. Otherwise:
   - Keywords gained or lost, collected into a gained list and a lost list, using the game's keyword names: Taunt, Elusive, Deathrattle, Battlecry, Charge, Lifesteal, Rush, Windfury, Divine Shield, Freeze (tag FREEZE, the "freezes what it hits" keyword), Reborn.
   - State changes, each a full phrase: dormant ("became dormant" / "is no longer dormant", plural "are no longer dormant"), frozen ("became frozen" / "is no longer frozen"), immune (not for weapons; not for Bob in BG): "became immune" / "is no longer immune", magnetic ("became magnetic" / "is no longer magnetic"), poisonous ("became poisonous" / "is no longer poisonous"), stealth (**gain only**): "became stealthed".
10. Stats (not for Bob's minions in the BG shop):
    - Attack: the gain or loss goes into the gained or lost list as "{0} attack". Exceptions that suppress it (each mark is used once, then removed): a loss right after the hero sheathed its weapon at turn end, a loss after its weapon broke, a gain after it unsheathed at turn end.
    - Max health (`GetHealth`): "{0} health" into gained or lost.
    - Armor gained: "{0} armor" into gained.
11. If the gained list is not empty: "gained {list}" (`..._ENTITY_GAINED_STATS` "gained {0}"), with the list joined "A, B and C". Then the same for lost: "lost {list}".
12. The whole description = all parts joined as a list: "took 3 damage, lost Divine Shield and gained 2 attack".

### 4.4 New entities

"New" = present after but not before, **or** in SETASIDE before and now elsewhere. Classified by the entity's **current live zone** (`Card.GetZone()`); the name is the entity name, or `LK.GLOBAL_CARD` "card" for an unknown card:

| Where it is now | Line |
|---|---|
| Your hand | collected → `..._MOVEMENT_CARD_ADDED_TO_PLAYER_HAND` "1 {0} was added to your hand" / `..._CARDS_ADDED_TO_PLAYER_HAND` "{0} were added to your hand" |
| Opponent hand | `..._CARD(S)_ADDED_TO_OPPONENT_HAND` "1 {0} was added to your opponent's hand" / "{0} were added to ..." (usually "1 card") |
| Your deck / opponent deck | `..._ADDED_TO_PLAYER_DECK` / `..._OPPONENT_DECK` "1 {0} was shuffled into your deck" / "{0} were shuffled into ..." |
| Your battlefield / opponent battlefield | `..._ADDED_TO_PLAYER_BATTLEFIELD` / `..._OPPONENT_BATTLEFIELD` "1 {0} was summoned into your battlefield" / "{0} were summoned into ..." |
| Your weapon slot | immediately: `LK.GAMEPLAY_PLAYER_EQUIPPED_WEAPON` "You equipped {0}" |
| Opponent weapon slot | immediately: `..._MOVEMENT_OPPONENT_EQUIPPED_WEAPON` "your opponent's Hero equipped 1 {0}" |
| Your secret zone | collected but **never spoken** |
| Opponent secret zone (secrets only) | class name collected → 1: `LK.GAMEPLAY_OPPONENT_CAST_SECRET` "Your opponent cast 1 Mage secret"; more: `LK.GAMEPLAY_OPPONENT_CAST_N_SECRETS` "Your opponent cast {0} secrets" (count) |
| It is your hero power / opponent hero power | immediately: `..._MOVEMENT_PLAYER_HERO_POWER_CHANGED` "your Hero Power became {0}" / opponent version |
| Anywhere else (graveyard, set-aside, ...) | nothing |

Collected lists with several names use the names-with-counts helper (core 3.9): "2 Murloc Scout and Coin were added to your hand".

Order: the immediate lines in entity order, then hand (you, opponent), deck (you, opponent), battlefield (you, opponent), opponent secrets.

### 4.5 Zone movements (entities that moved)

Candidates: entities that moved (4.2), in after-snapshot order (so two draws come out in hand order). The PTL's **source entity is excluded**, because the action line covers it. For each, by its before and after zone (zones mapped to the controller's Hand / Deck / Graveyard / Battlefield / Secret zone objects; other zones count as "no zone"):

1. From SETASIDE: skip (handled as new).
2. To GRAVEYARD: if it came from somewhere other than PLAY or SECRET (hand, deck), it is a **discard** (yours or opponent's), name or "card". Otherwise skip (a death covers it). So milled cards (deck → graveyard) are reported as discards.
3. HAND → SETASIDE: discard (Patchwerk-style destruction).
4. Your weapon that was in PLAY (only friendly; HSA's opponent branch here is unreachable): to your hand → "returned to your hand". Otherwise "moved to {zone}", where the argument is the zone object itself, not its spoken name (quirk 9).
5. Either zone is "no zone":
   - Hero power → SETASIDE and the controller now has a different hero power: immediate line "your Hero Power became {0}" / opponent version (hero card played). This can repeat the new-entity line of 4.4.
   - Other → SETASIDE: skip a hero that left PLAY; otherwise a discard.
   - Else: "moved zones" (`..._MOVEMENT_ENTITY_MOVED_ZONES_GENERIC`).
6. Both zones known:

| From → to | Result |
|---|---|
| your deck → your hand | **your draw**: card name |
| opponent deck → opponent hand | **opponent draw**: "card", or the real name if the entity has REVEALED (tradeable or shuffled cards the client already knows) |
| your battlefield or secrets → your hand | "returned to your hand" / "returned to your hand" |
| opponent battlefield or secrets → opponent hand | "returned to your opponent's hand" |
| your battlefield → opponent battlefield | "your opponent took control of {0}" (list) |
| opponent battlefield → your battlefield | "you took control of {0}" (list) |
| → opponent secrets (a secret) | opponent secret by class (as 4.4) |
| → your secrets (a secret) | `LK.GAMEPLAY_PLAYER_CAST_SECRET` "You cast {list}" |
| any other pair | "moved from {from zone} to {to zone}" with zone names `LK.GAMEPLAY_DIFF_ZONE_*`: "your hand", "your opponent's hand", "your battlefield", "your opponent's battlefield", "your secrets", "your opponent's secrets", "your deck", "your opponent's deck", "your graveyard", "your opponent's graveyard" |

   Only the "after" zone known: "moved to {zone name}". Neither: "moved zones".

7. Output order: `LK.GAMEPLAY_PLAYER_DREW_CARDS` "You drew {list}", `LK.GAMEPLAY_OPPONENT_DREW_CARDS` "Your opponent drew {list}" ("Your opponent drew 2 cards"), `LK.GAMEPLAY_PLAYER_DISCARDED_CARDS` "You discarded {list}", `LK.GAMEPLAY_OPPONENT_DISCARDED_CARDS`, "you took control of", "your opponent took control of", "You cast", opponent secrets, then the per-entity movement sentences grouped as in 4.7 (named with list ordinals, 4.10; an entity whose before copy is unknown but after copy is known, such as deck → battlefield, is named from the after copy: "Your Raid Leader moved from your deck to your battlefield").

### 4.6 Transforms and deaths

- **Transforms** (only if the entity is now outside DECK, SETASIDE, GRAVEYARD and REMOVEDFROMGAME, so Dredge deck reveals don't count): `..._ENTITY_TRANSFORMED` "transformed into 1 {0}" (after name). They are grouped as 4.7, named by position in zone: "Your opponent's Ragnaros transformed into 1 Sheep".
- **Deaths** (died, and the death phrase is not empty):

| Entity | Phrase (singular / plural) |
|---|---|
| Weapon, controller now has no weapon | "broke" |
| Weapon, controller has a new weapon | "" → not announced (weapon replaced) |
| Objective (aura) | "faded" |
| Location | "was destroyed" / "were destroyed" |
| Anything else | "died" / "died" |

  Deaths are grouped as 4.7. Remaining names use the ordinal **among the dying entities** with the same full name, not board position: "Your Wisp and your opponent's Wisp died".

### 4.7 Grouping and merging (the key to "several damages merged")

Groups are built from the **before** snapshot:

| Order | Group name (`LK.GAMEPLAY_DIFF_GROUP_*`) | Members | Only if |
|---|---|---|---|
| 1 | "Everyone" | all minions + both heroes | more than 2 members |
| 2 | "Both heroes" | both heroes | always |
| 3 | "All minions" | all minions | both sides have minions |
| 4 | "Your enemies" | opponent minions + opponent hero | more than 1 |
| 5 | "Your friendly characters" | your minions + your hero | more than 1 |
| 6 | "Your opponent's minions" | opponent minions | more than 1 |
| 7 | "Your minions" | your minions | more than 1 |
| 8–13 | "Everyone else", "All other minions", "Your other enemies", "Your other friendly characters", "Your other opponent's minions", "Your other minions" | groups 1, 3, 4, 5, 6, 7 without the PTL's source entity | a source exists and more than 1 member remain |
| 14 | "Your {0} {1}" (count, name) | your minions sharing one name | 2 or more |
| 15 | "Your opponent's {0} {1}" | opponent minions sharing one name | 2 or more |

(BG shop: the opponent groups say "Bob's" / bartender variants.)

**Grouping** (separately for changes, transforms, deaths and movements):
1. For each group in the order above: if it has more than one member, **every** member is in the affected set, no member is already handled, and all members have exactly the same phrase (singular and plural) and it is not empty → the group takes that phrase, and its members are marked handled.
2. Every entity not handled keeps its own phrase, under its own name.
3. **Merging:** entries (group names and single names) that share an identical phrase are joined into one sentence: `LK.GAMEPLAY_DIFF_ENTITY_SPEECHES_FORMAT` "{0} {1}" = the names joined "A, B and C", then the phrase. The **plural** phrase is used if there is more than one name, or if the single name is a group. Otherwise the singular.
4. Sentence order: first-seen order of phrases (groups first, then remaining entities).

Examples:
- Consecration (2 damage to all enemies): "Your enemies took 2 damage."
- Arcane Explosion on three of four enemy minions, all surviving: no group fits, so "Your opponent's Wisp, your opponent's Raid Leader and your opponent's Yeti took 1 damage."
- A minion that dies is not "affected" (has changes = false). So when half the board dies, the survivors' damage is listed by name and the deaths come as a separate sentence.

### 4.8 The whole diff text

Only when both snapshots exist and the game is created (the very first diff after game creation is skipped). Parts in this order, joined with ". ":

1. **Quest progress:** for every quest, side quest or questline whose progress line changed: `LK.TOAST_QUEST_PROGRESS_TOAST_PROGRESS` "Current progress is {0} out of {1}" (no quest name).
2. **Hero replaced** (different entity id): `..._MOVEMENT_PLAYER_HERO_CHANGED` "your Hero became {0}" / `..._OPPONENT_HERO_CHANGED` (new hero's name).
3. **Board sentences:** new entities (4.4) → movements (4.5) → transforms → deaths → other changes (4.3/4.7).
4. **Mana**, only when the new STEP is strictly between MAIN_START and MAIN_END in enum order (MAIN_ACTION, MAIN_COMBAT), so not during turn start or end. Not in BG. For each side, with Δavail = change in available mana and Δcrystals = change in RESOURCES:

| Condition | Line (you / opponent) |
|---|---|
| Δcrystals > 0 and Δavail ≥ Δcrystals | `..._PLAYER_GAINED_MANA` "You gained {0} mana" (Δavail) |
| Δcrystals > 0 and 0 < Δavail < Δcrystals | `..._GAINED_MANA_AND_EMPTY_MANA_CRYSTALS` "You gained {0} mana and {1} empty mana crystal(s)" (Δavail, Δcrystals − Δavail) |
| Δcrystals > 0 and Δavail ≤ 0 | `..._GAINED_EMPTY_MANA_CRYSTALS` "You gained {0} empty mana crystal(s)" (Δcrystals) |
| Δcrystals ≤ 0 and Δavail > 0 | "You gained {0} mana" (The Coin, Innervate) |
| otherwise | nothing: **spending mana and losing crystals are never said** |

   Opponent texts start "Your opponent gained ...".
5. **Spell damage** (each side, on a change of total spell power): `..._PLAYER_GAINED_SPELL_DAMAGE` "You gained {0} Spell Damage" / `..._LOST_...` "You lost {0} Spell Damage" / opponent versions.

### 4.9 Weapon sheathe bookkeeping

At a real turn end (6.3): if the player whose turn ended has a weapon, their hero is marked "sheathed". If the other player has a weapon, their hero is marked "unsheathed". A weapon that breaks with no replacement marks its hero "weapon broke". The next attack loss (sheathed or broke) or attack gain (unsheathed) of that hero is not announced, so the turn-change attack swings of weapons are silent. The marks are cleared at game start. Equipping a weapon on your turn is **not** suppressed: "You equipped Fiery War Axe." can be followed by "Your Hero gained 3 attack.".

### 4.10 Naming rules

| Helper | Result |
|---|---|
| base name | hero (outside mulligan) → card type name `GLOBAL_CARDTYPE_HERO` "Hero"; hero power → `GLOBAL_CARDTYPE_HEROPOWER` "**HeroPower**" (enUS, no space); unknown card (INVALID type) → "card"; otherwise `Entity.GetName()` (BG adds golden) |
| full name | `LK.GAMEPLAY_DIFF_PLAYER_ENTITY_FULL_NAME` "Your {0}" / `..._OPPONENT_ENTITY_FULL_NAME` "Your opponent's {0}" (BG shop: Bob's) |
| full name in list | Count the entities in the list with the same full name, and find this entity's position among them. One → plain full name. Several → `..._PLAYER_ENTITY_FULL_NAME_IN_LIST` "Your {0} {1}" / opponent version, with {0} = ordinal `LK.FORMATTING_ORDINAL_NUMBER_n` "first" ... "tenth" when the locale has it, else digits. Example: "Your second Raid Leader". |
| full name in zone | If the entity is in GRAVEYARD, INVALID, REMOVEDFROMGAME or SETASIDE → full name. Otherwise "full name in list" over the cards of its card's **live** zone, in zone order (left to right). |

Used: the attack text and trigger callout use "in zone". Change and transform sentences use "in zone" of the before copy (ordinal by live board position). Death and movement sentences use "in list" over the other entities left in that sentence group. Play lines, draws, discards and new-entity lines use the plain card name, with no "your".

---

## 5. Catalogue of announced events

Unless stated, the line is part of the PTL utterance (a wait output). "Early" = a wait output when the game shows it. "Plain" = focus-filtered, no wait. "Notif" = notification output.

| Event | When / source | Tag(s) | enUS | Notes |
|---|---|---|---|---|
| You play a minion / spell / weapon / hero power / secret / location | PLAY PTL end | `ACCESSIBILITY_GAMEPLAY_PLAYER_SUMMONED_MINION`, `_PLAYER_PLAYED_CARD`, `_PLAYER_EQUIPPED_WEAPON`, `_PLAYER_USED_HERO_POWER`, `_PLAYER_CAST_SECRET` | "You summoned {0}" ... | Short text. Long text when spectating. |
| Opponent plays a card | early (big card); fallback PLAY PTL end | `..._OPPONENT_*_BIG_CARD`, opponent secret `..._OPPONENT_CAST_SECRET` | "Your opponent summoned {0}; {1} {2} {3}" | Includes stats and text. |
| Trade | (intended) PLAY PTL end | `..._PLAYER/OPPONENT_TRADED_CARD` | "You traded {0}" | Unreachable from PTL end (quirk 6). Shows as hand/deck movement and a draw. |
| Minion attacks | early (launch) | `ACCESSIBILITY_GAMEPLAY_ENTITY_ATTACKED`, `_ENTITY_ATTACKED_OTHER` | "{0} attacked {1}" | Redirected attacks get "attempted to attack ... instead". |
| Damage | diff | `..._DIFF_ENTITY_TOOK_N_DAMAGE` / `_MULTIPLE_` | "took {0} damage" | Grouped and merged. Not for minion-only attacks. Optimizer rewrites "X and Y took N damage" after an attack. |
| Armor lost / gained | diff | `..._ENTITY_LOST_STATS` + `_N_ARMOR`; gained list | "lost 2 armor" / "gained 5 armor" | Armor loss is said before damage. |
| Healing | diff | `..._ENTITY_RECOVERED_N_HEALTH` | "recovered {0} health" | |
| Buffs / debuffs (attack, max health) | diff | `..._ENTITY_GAINED_STATS` / `_LOST_STATS` + `_N_ATTACK` / `_N_HEALTH` | "gained 2 attack and 2 health" | Also for cards in hand with known identity (hand buffs). Not for cards that moved out of play. |
| Keyword gained / lost | diff | game keyword tags inside "gained/lost {0}" | "lost Divine Shield", "gained Taunt" | List in 4.3. |
| Silence | diff | `..._ENTITY_BECAME_SILENCED` | "was silenced" | Hides keyword losses. |
| Frozen / unfrozen, dormant, immune, poisonous, magnetic, stealth, invulnerable, cursed, haunted, location reopened | diff | `..._BECAME_*`, `_NO_LONGER_*`, `_WAS_CURSED`, `_WAS_HAUNTED`, `_LOCATION_REOPENED` | "became frozen", "is no longer frozen" ... | Stealth: gain only. |
| Transform | diff | `..._ENTITY_TRANSFORMED` | "transformed into 1 {0}" | |
| Death | diff | `..._ENTITY_DIED` / `_MULTIPLE_ENTITIES_DIED` | "died" | Optimizer: "Both minions died." |
| Weapon breaks | diff | `..._DIFF_WEAPON_BROKE` | "Your Fiery War Axe broke" | Silent if replaced. |
| Location destroyed / aura faded | diff | `..._LOCATION_WAS_DESTROYED`, `_AURA_FADED` | | |
| Card drawn | diff | `..._PLAYER_DREW_CARDS`, `_OPPONENT_DREW_CARDS` | "You drew Fireball", "Your opponent drew 1 card" | Opponent: name only if revealed. |
| Card burned | PTL end | `..._PLAYER/OPPONENT_DREW_CARD_BUT_BURNED` | "You drew 1 {0} but it burned" | Likely also as "discarded". |
| Discard / mill / hand destruction | diff | `..._PLAYER/OPPONENT_DISCARDED_CARDS` | "You discarded {0}" | Mill reads as discard. |
| Card generated into hand / deck / battlefield | diff (new entity) | `..._MOVEMENT_CARD(S)_ADDED_TO_*` | "1 Coin was added to your hand", "2 Silver Hand Recruit were summoned into your battlefield" | |
| Returned to hand | diff | `..._MOVEMENT_ENTITY_RETURNED_TO_*_HAND` | "Your Yeti returned to your hand" | |
| Mind control | diff | `..._MOVEMENT_CARDS_TAKEN_CONTROL_BY_*` | "you took control of {0}" | |
| Other moves | diff | `..._MOVEMENT_ENTITY_MOVED_FROM_ZONE_TO_ZONE`, `_MOVED_TO_ZONE`, `_MOVED_ZONES_GENERIC` | "moved from your hand to your deck" | |
| Secret appears (effect) | diff | `..._OPPONENT_CAST_SECRET`, `_OPPONENT_CAST_N_SECRETS`, `_PLAYER_CAST_SECRET` | "Your opponent cast 1 Hunter secret" | |
| Secret revealed | early (triggered big card) | `..._OPPONENT_CARD_TRIGGERED_BIG_CARD`, `_PLAYER_CARD_TRIGGERED` | "Your opponent's {0} triggered; {1}" | |
| Trigger (deathrattle, aura-type triggers, end of turn, ...) | TRIGGER PTL end | `..._CARD_IN_ZONE_TRIGGERED` | "Your Knife Juggler triggered" | Characters (not heroes), weapons, secrets, quests, hero powers. |
| Weapon equipped by effect | diff (new entity) | `..._PLAYER_EQUIPPED_WEAPON`, `..._MOVEMENT_OPPONENT_EQUIPPED_WEAPON` | "your opponent's Hero equipped 1 {0}" | |
| Hero replaced (hero card) | diff | `..._MOVEMENT_PLAYER/OPPONENT_HERO_CHANGED` | "your Hero became {0}" | The hero card's own play line is "You played {0}". |
| Hero power replaced | diff | `..._MOVEMENT_PLAYER/OPPONENT_HERO_POWER_CHANGED` | "your Hero Power became {0}" | May be said twice. |
| Mana gained mid-turn | diff | `..._DIFF_PLAYER/OPPONENT_GAINED_MANA[_AND_EMPTY_MANA_CRYSTALS]`, `_GAINED_EMPTY_MANA_CRYSTALS` | "You gained 1 mana" | Only during MAIN_ACTION/COMBAT. |
| Spell damage change | diff | `..._DIFF_PLAYER/OPPONENT_GAINED/LOST_SPELL_DAMAGE` | "You gained 1 Spell Damage" | |
| Quest progress | diff | `ACCESSIBILITY_TOAST_QUEST_PROGRESS_TOAST_PROGRESS` | "Current progress is 3 out of 10" | |
| Fatigue | FATIGUE PTL end | `..._PLAYER/OPPONENT_DREW_CARD_FROM_EMPTY_DECK` | "You tried to draw a card from an empty deck" | Damage follows in the same utterance. |
| Your turn begins | turn-start banner | `GAMEPLAY_YOUR_TURN` (game string), `ACCESSIBILITY_GAMEPLAY_PLAYER_TURN_START_READ_MANA` | "Your Turn. You have 5 mana." | Plus deferred lines (6.2). |
| Opponent's turn begins | MAIN_READY PTL end | `ACCESSIBILITY_GAMEPLAY_OPPONENT_TURN` | "Opponent's turn" | |
| Turn 1 pre-placed board | with the first turn line | `..._SPECIAL_STARTING_PLAYER_BATTLEFIELD`, `_OPPONENT_BATTLEFIELD` | "Your battlefield starts with {0}" | Book of Heroes and similar. |
| Turn ends (either player) | MAIN_END PTL end | `ACCESSIBILITY_GAMEPLAY_TURN_ENDED` | "Turn ended" | |
| Rope / timer | TurnTimer, MulliganTimer, each frame | `ACCESSIBILITY_GAMEPLAY_N_SECONDS_REMAINING` | "10 seconds remaining" | Notif, once, when the rounded remaining seconds drop to exactly 10. Any player's turn. |
| Game over | game-over listener | `..._GAME_OVER_WON`, `_LOST` (also conceded), `_TIED`, `_GENERIC` | "You win", "You lose", "You tied", "Game over" | Plain. No special concede text. |
| Joust | JoustSpellController | `..._JOUST_PLAYER/OPPONENT_REVEALED_CARD`, `_NO_VALID_CARDS` | "You revealed a {0} cost {1}" | Plain + history. Opponent reveal only if the card type is known. |
| Gameplay error | GameplayErrorManager | the game's own message | "Not enough mana" ... | Plain. |
| Anomalies | anomaly intro banner | anomaly texts / `ACCESSIBILITY_GAMEPLAY_NO_ANOMALIES` | | Plain + history. |
| Adventure turn counter | MissionEntity | `{n} {mission label}` | "3 turns left" (example) | Plain. |
| Discover / Choose One appear | ChoiceCardMgr | banner headline / `GAMEPLAY_CHOOSE_ONE` | | Covered with choices; added to history as "{title}: {names}". |

---

## 6. Turn flow

### 6.1 Turn change (at a PTL end with STEP = MAIN_READY)

Runs once per turn (guarded by "turn started"):
- Set turn started.
- **Your turn:** set "waiting for banner". From now on every PTL utterance (plays, triggers, draws, diffs) is put on the deferred list instead of spoken.
- **Opponent's turn:** add "Opponent's turn" (not in BG). If the game turn number is 1, add the pre-placed boards: "Your battlefield starts with {names}" and/or "Your opponent's battlefield starts with {names}" (names with counts). These lines go into the current utterance, before the diff. The opponent's turn-start draw follows in the same utterance: "Opponent's turn. Your opponent drew 1 card.".

### 6.2 Turn start (your turn banner)

`TurnStartManager.DisplayTwoScoops` runs when the "Your Turn" banner is shown. In vs-AI games the game itself waits 1 s first. Then:
1. Clear "waiting for banner", set turn started.
2. One utterance (wait output; the describer does not mark itself busy for it) made of:
   - "Your Turn" (game string `GAMEPLAY_YOUR_TURN`);
   - unless the mission disabled mana counters (tutorial 1): "You have {0} mana" with the friendly player's available mana;
   - turn 1 only: the pre-placed boards;
   - every deferred line (start-of-turn triggers, your draw, start-of-game reveals from the mulligan), in order. Then the deferred list is cleared.
3. Added to history as one entry.

Example: "Your Turn. You have 4 mana. You drew Fireball."

The banner is created only if the game entity does not have `DISABLE_TURN_INDICATORS`. Without a banner this call never happens (quirk 3).

### 6.3 Turn end (at a PTL end with STEP = MAIN_END, the step just changed, and turn started)

- Add "Turn ended" (both players' turns).
- Weapon sheathe bookkeeping (4.9).
- Clear turn started.
- A per-turn "end turn attempts" counter is incremented. It exists for cards that end the turn twice (Hemet, Illidan) but never filters anything.
- Your own E-to-end-turn confirmation questions ("You can still attack. Are you sure?") are input responses, covered in part 2.

---

## 7. Waiting and pacing

### 7.1 How gameplay waits on speech

- The game processes server power history only when `GameState.CanProcessPowerQueue()` is true. HSA adds: false while the **task-list describer is blocked** or the **play describer has pending readings**, while accessibility is on.
- So when a PTL ends, the **next** PTL does not start (no animation, no new action) until its utterance has finished its estimated time. The PTL that is already running is not paused. A big card or an attack-launch utterance is spoken while the current PTL keeps animating; it then holds the next PTL.
- The wait length is the queue's estimate: whole seconds of text length ÷ characters per second (core 3.6), integer division. At the default game speed 3 (15 cps), "You played Flamestrike. Your opponent's minions died." (54 chars) holds the game about 3 s. Texts under 15 characters do not wait at all.
- In PvP game types (ranked, casual, arena, friend, brawl, Duels PvP, Underground Arena) the game speed is locked to 1000 cps, so the wait is about 0. Speech then only queues in the screen reader and can fall behind the game. In practice, adventures and tutorials the F11/F12 game speed sets how long the game pauses after each announcement.
- **A key press skips the wait:** any key-down while a wait item plays ends it (core 3.4). Its end callback fires, which unblocks the game at once.
- If the window has no focus and background speech is off, the text is dropped and both callbacks fire at once: no wait.
- If the screen reader rejects the output, the callbacks fire at once: no wait.

### 7.2 What does not wait

Plain outputs (mulligan, coin, game over, errors, joust, choice titles) and notification outputs (timer, popups) never hold the game.

### 7.3 The play describer's busy counter (as built)

- A big card adds 1 and its end callback subtracts 1.
- The attack-launch utterance **does not add 1, but its end callback still subtracts 1**. Each launched attack therefore leaves the counter one lower. Game start does not reset it. After the first attack, later big cards bring it back to 0 at most, and "busy" (> 0) is false. In practice, after the first attack of the session, big-card speech no longer holds the game. Only the task-list describer's block still does. Quirk 2.

### 7.4 Ordering consequences

- The opponent's big card ("Your opponent summoned ...") is spoken before that PLAY PTL's own utterance, which is usually just its diff (the play line is already described).
- Speech-queue order = event order. The optimizer compares with the last **played** text, so "X attacked Y." followed by the death utterance works because they are adjacent.

---

## 8. Game start, mulligan and game end

### 8.1 Mulligan sequence (all plain outputs, focus-filtered)

| Moment (hook) | Speech |
|---|---|
| `DealStartingCards`, before dealing | PvP game types only: `LK.GAMEPLAY_VS_PLAYER_ANNOUNCEMENT` "You're playing against {0}, the {1}" (opponent's player name, class name of the opponent hero); also added to history. Then always: `LK.GAMEPLAY_YOU_START_WITH_N_CARDS` "You start with {0} card(s)" (starting cards count; with the bonus card for the second player). |
| coin flip | `LK.GAMEPLAY_YOU_GO_FIRST` "You go first" or `LK.GAMEPLAY_OPPONENT_GOES_FIRST` "Your opponent goes first". |
| first mulligan ever (option HAS_SEEN_MULLIGAN false, not spectating, not choose-one) | Five tutorial narrations `IN_GAME_TUTORIAL_MULLIGAN_FIRST_TIME` .. `_E`, played one after another, instead of the innkeeper quote. |
| mulligan choice starts | `GAMEPLAY_MULLIGAN_STARTING_HAND` "Starting Hand", `GAMEPLAY_MULLIGAN_SUBTITLE` "Keep or Replace Cards", then the card list starts reading (first card "{name} 1 of N" etc.; spec 1 and part 2). |
| card marked (Space) | `LK.GAMEPLAY_MULLIGAN_WILL_BE_REPLACED` / `_WILL_NOT_BE_REPLACED`. |
| `WaitForOpponentToFinishMulligan` | `LK.GAMEPLAY_WAITING_FOR_OPPONENT` "Waiting for opponent". |
| `EndMulligan` | If any card was replaced: `LK.GAMEPLAY_PLAYER_DREW_CARDS` "You drew {new card names}" (new hand cards, the coin excluded). History gets "You discarded {old names}" and "You drew {new names}"; only "You drew" is **spoken**. Then the game-over listener is registered. |
| coin card summoned to your hand (`CoinCardSummonFinishedCallback`) | `LK.GAMEPLAY_YOU_GET_THE_COIN` "You get the coin". |
| anomaly intro (anomaly modes) | each anomaly text, or "There are no anomalies". |

The describer stays mostly silent through the mulligan: diffs start only once STEP reaches MAIN_READY. Start-of-game reveals during the mulligan are deferred to the first "Your Turn" when you go first (2.3, BEGIN_MULLIGAN). When the opponent goes first, they are spoken immediately.

### 8.2 First turn

Going first: "Your Turn. You have 1 mana." plus deferred lines. Going second: "Opponent's turn." (+ turn-1 boards) and, later on your turn, the coin is already in hand (announced at the mulligan).

### 8.3 Game end

- `GameState` game-over listener (registered at mulligan end): the gameplay screen is in GAME_OVER phase and speaks one plain line by your play state: WON → "You win", LOST or CONCEDED → "You lose", TIED → "You tied", anything else → "Game over". No line is added to history.
- If the option "Automatically save battle history logs" is on (not in BG): the history log is written to `<game>/battle logs/<yyyy-M-d H_mm> <your name> v <opponent name>.txt`, one curated entry per line. A save error is spoken.
- The final blow itself comes through the normal diff ("Your opponent's Hero took 6 damage. Your opponent's Hero died.") if the PTL ends before the game-over event.
- The end screen (`EndGameScreen`) becomes the current screen. It says nothing by itself; Enter continues and F1 gives "Press Enter to continue". The score screen is disabled while accessibility is on (`GameState.CanShowScoreScreen` → false). Rewards and the end-of-game flow belong to the rewards spec.

---

## 9. Other announcements

### 9.1 Timers

`TurnTimer.Update` and the mulligan timer: remaining seconds are rounded to the nearest integer each frame. When the value goes down and equals 10, the line "10 seconds remaining" (`LK.GAMEPLAY_N_SECONDS_REMAINING`, plural-aware) is a notification output. HSA notes it used to say 15 and 5. The mulligan timer also has a "say seconds remaining now" helper, used by a key.

### 9.2 Gameplay errors

Every message shown by `GameplayErrorManager` (the red "Not enough mana", "You must attack the minion with Taunt", ...) is spoken as a plain output, in the game's words.

### 9.3 History log

Entries are added by: every PTL utterance (as one entry), attack texts, big-card played and triggered texts, joust lines, anomalies, the vs-player line, mulligan discards and draws, the turn-start utterance, and choice titles. Some actions are therefore listed twice, for example an attack text and then the PTL utterance that carried it. Newlines become spaces. It is cleared at gameplay screen start. Reading it (Y, arrows, Backspace) is navigation: "{entry} ; {n} of {count}", "Nothing has been played yet", "Stopped reading history".

### 9.4 Speech bubbles and popups

`NotificationManager`: popup texts and character quotes **created without sound** are read as notification outputs. Speech bubbles (including **emote bubbles**) and quotes with sound are not read, because the "deaf-blind" branch is never on. So opponent emotes ("Greetings", "Well played") are **not announced**. Tutorial notification texts are covered in the tutorial spec.

---

## 10. Spectating, practice, tutorials, reconnect

### 10.1 Spectating

No special announcement code. "Friendly" is the side the game treats as friendly (the spectated player). Because decision input is not permitted while spectating, the game shows a big card for **every** play, so both sides' plays get the long text. Emote menus are disabled. "Your Turn" follows the game's banner. The game-speed lock follows the spectated game type.

### 10.2 Practice and other AI games

Same pipeline. The game speed can be changed, so every PTL utterance really pauses the game. The game's own 1 s pause before the "Your Turn" banner applies in AI games.

### 10.3 Tutorials and adventures

- Tutorial games have no mulligan. "Game officially started" waits for the first MAIN_READY step. Until then diffs are not described.
- Big cards are forced on even where the mission hides them, so opponent plays are still spoken.
- Tutorial 1 disables the "You have N mana" line. A tutorial also disables corpse reading.
- "Became invulnerable" is never said in tutorials (they toggle it randomly).
- Tutorial scripts speak their own texts (narrations, help overrides, plain outputs) and register card-selected / summoning listeners. Not covered here.
- Turn-counter missions speak "{n} {label}" each time the counter changes.

### 10.4 Reconnect

The gameplay screen's "reconnected" handler (it would say "Reconnected", then "Your Turn" or "Opponent's turn", re-register the game-over listener and mark the describer as started) is **never called** in this build. After a reconnect, the first PTL simply takes a fresh snapshot.

---

## 11. What is NOT announced

- Mana spent, crystals lost, and the start-of-turn mana refill (only "You have N mana" on your own turn). The opponent's mana at their turn start.
- Weapon durability loss when attacking. Weapon attack swings at turn change.
- Damage in minion-against-minion attacks, including the surviving minion's damage. Any change during a PTL whose diff was suppressed.
- Anything before the game officially starts, except play, trigger and burn lines.
- The identity of cards the opponent draws (unless revealed), and secrets' names (class only).
- Your own new secrets or quests that appear by effect.
- Cards created directly in the graveyard or set-aside.
- Cost changes, enchantments themselves (only their stat/keyword effects), spell-school changes, dormant countdowns, location cooldowns (only "reopened"), exhaustion or "can attack" changes.
- Battlecry and Combo as such (only their effects). Hero triggers (mission events) and spell triggers.
- Hand order changes and minion position changes.
- Opponent emotes and any speech bubble with voice.
- Timer except at exactly 10 seconds.
- Concede as such (only "You win" / "You lose").
- Your own trades as trades.

---

## 12. Game APIs and hook points our implementation needs

| Hook (game type.method) | Patch point | Why |
|---|---|---|
| `Gameplay.Start` | prefix | Reset per-game announcer state and history; create our gameplay screen. |
| `GameState.FireCreateGameEvent` (or `GameState.OnTaskListEnded` seeing `CREATE_GAME`) | prefix | Reset the describer at game creation. |
| `GameState.OnTaskListEnded(PowerTaskList)` | prefix or postfix (HSA runs **before** `RemoveQueuedEntitiesFromGame`; a prefix that runs after the CREATE_GAME handling matches best) | Main "PTL end" point. Skip null lists and lists with no tasks (`GetTaskList().Count == 0`), which HSA never describes. |
| `GameState.CanProcessPowerQueue` | postfix: `__result &= !busy` | Hold the power queue while our utterance plays. |
| `HistoryCard.ShowBigCard(Vector3[])` | postfix | Early big-card announcement. We need the origin PTL: either patch `HistoryManager.CreatePlayedBigCard/CreateTriggeredBigCard` (prefix: record `PowerProcessor.GetCurrentTaskList()` against the entity), or read `GameState.Get().GetPowerProcessor().GetCurrentTaskList()` at show time. Read `HistoryCard.m_historyInfoType` (private) and `GetEntity()`. |
| `HistoryManager.CreatePlayedBigCard` / `CreateTriggeredBigCard` | transpiler or prefix | Only needed to force big cards in missions where `GameEntity.ShouldShowBigCard()` is false. |
| `PowerProcessor.ShouldShowPlayedBigCard` / `ShouldShowTriggeredBigCard` (private instance) | call through reflection, or reimplement (rules in 3.1, 3.2) | Short vs long play text. |
| `AttackSpellController.LaunchAttack` | prefix; read the PTL from `AddPowerSourceAndTargets` or the controller's task list | Speak the attack at the lunge. |
| `TurnStartManager.DisplayTwoScoops` | postfix, when `m_turnStartInstance` exists and was just shown (or hook `TurnStartIndicator.Show`) | "Your Turn" and deferred lines. Also handle games with `DISABLE_TURN_INDICATORS` (e.g. fall back to `GameState.FireFriendlyTurnStartedEvent` / `RegisterFriendlyTurnStartedListener`). |
| `MulliganManager.DealStartingCards` (iterator) | hard to patch inside a coroutine. Alternatives: `GameEntity.OnMulliganCardsDealt` postfix for "you start with", `CoinEffect.DoAnim(bool)` prefix for who goes first, `MulliganManager.m_startingCards` | Starting hand and coin result. |
| `MulliganManager.WaitForOpponentToFinishMulligan`, `EndMulligan`, `CoinCardSummonFinishedCallback` | prefix | Waiting text, replaced cards, coin. |
| `Banner.ShowHearthstoneAnomalyIntro` | postfix on the iterator end (or `MulliganManager.GetAnomalies` at mulligan) | Anomalies. |
| `TurnTimer.Update`, `MulliganTimer.Update` | postfix; `TurnTimer.ComputeCountdownRemainingSec()` | "10 seconds remaining". |
| `GameplayErrorManager.ShowMessage` (the instance show method) | postfix | Error texts. |
| `JoustSpellController.ShowJouster`, `PlayNoJousterSpell` | postfix | Joust reveals. |
| `NotificationManager.CreatePopupText`, `PlayQuoteWithoutSound`, `CreateSpeechBubble(SpeechBubbleOptions)` | postfix | Popup texts (and, if we choose, emotes). |
| `GameState.RegisterGameOverListener` (call) | at game start or mulligan end | Game result. |
| `EndGameScreen.Show` | postfix | End screen as current screen. |
| `GameState.CanShowScoreScreen` | postfix → false (optional) | HSA hides the score screen. |
| Read-only APIs | `PowerTaskList.GetBlockStart/GetBlockType/IsBlockType/IsOrigin/IsPlayBlock/IsTriggerBlock/GetSourceEntity/GetAttacker/GetDefender/GetProposedDefender/GetTaskList/GetId`; `PowerTask.GetPower()`; `Network.HistMetaData` (`MetaType == BURNED_CARD`, `Info`), `Network.HistTagChange`, `Network.HistShowEntity` (`Entity.Tags`); `GameState.GetGameEntity().GetTag(STEP)`, `GetFriendlySidePlayer/GetOpposingSidePlayer/GetCurrentPlayer/GetTurn/GetEntityMap/IsFriendlySidePlayerTurn/IsMulliganManagerActive/IsGameCreated`; `Player.GetNumAvailableResources/GetTag(RESOURCES)/TotalSpellpower/GetHero/GetHeroPower/GetWeaponCard/GetHandZone/GetDeckZone/GetGraveyardZone/GetBattlefieldZone/GetSecretZone/GetName`; `Zone.GetCards()`; `Card.GetZone()/GetEntity()`; `Entity` tag accessors and `GetCardTextBuilder().BuildCardTextInHand`; `GameStrings.GetClassName`, `GameStrings.GetCardTypeName`; `GameMgr.Get().GetGameType()/IsSpectator()/IsAI()`; `CosmeticCoinManager.IsCoinCard` | Building the lines. |
| Snapshot copies | We cannot add `Entity.CloneForAccessibility`. Instead copy what we need into our own record per entity: id, card id, name, controller side, zone, card type, the tags in 4.3 (or the whole tag map via `GetTags().GetMap()`), and the live card's zone. For the 28.2 hide/show problem, remember the last valid name per entity id (e.g. postfix on `Entity.OnHideEntity` or keep the name from the previous snapshot). | The diff. |

---

## 13. Notable quirks to decide on

1. **Swallowed changes.** A suppressed diff (minion-only attack, pre-start) still replaces the snapshot, so those changes are never described, not even later.
2. **Busy counter drift.** Attack-launch speech decrements the play describer's counter without incrementing it, so after the first attack, big-card speech stops holding the game.
3. **No banner, no speech.** In games with DISABLE_TURN_INDICATORS (or any path where the banner is not shown), "waiting for banner" never clears and every later PTL utterance is deferred forever.
4. **Turn-start utterance unblocks early.** Its end callback clears the describer's block even if a different PTL utterance set it.
5. **HeroPower.** Hero powers in attack, trigger and diff names read "your HeroPower" (enUS card-type string). Heroes read "your Hero", never the hero's name.
6. **Trades.** The trade text is only built for PLAY blocks, but trades are DECK_ACTION blocks, so it is never spoken at PTL end. Only an opponent trade shown as a big card could say it.
7. **Duplicates.** Burned cards (burn line + discard), hero power changes (new entity + set-aside path), history entries (attack text + PTL utterance).
8. **Mill = discard.** Deck → graveyard is read as "discarded".
9. **Weapon move.** A friendly weapon leaving play to a zone other than hand formats the zone object instead of its name.
10. **Your new secret is silent** when created by an effect (the line is collected but never used).
11. **Mana** is only said for gains during MAIN_ACTION/MAIN_COMBAT. Spending is never said.
12. **Group names use the before board** and need every member affected with the same phrase. A minion that dies breaks the "took damage" group for the others.
13. **Ordinals:** changes use live board position, deaths use position among the dying. The same minion can get different ordinals in one utterance.
14. **"Turn ended"** is said for both players' turns and comes **before** the end-of-turn effects' diff in the same utterance.
15. **Focus.** PTL utterances are wait outputs, so they are heard even with a popup or the game menu open. Mulligan, coin, game over and errors are plain outputs and are lost while a popup has focus.
16. **Emotes** are never read. **Reconnect** speech is dead code.
17. **PvP pacing.** In PvP the wait is about 0, so announcements can trail the game in the screen reader's own queue.
