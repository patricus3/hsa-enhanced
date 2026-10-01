# Combat spec, part 1: navigating a match and reading cards

This spec describes how Hearthstone Access (HSA) behaves, so we can rebuild the same behaviour from scratch. It describes behaviour only. HSA has no license, so none of its code is reproduced here.

Scope: the traditional (constructed, arena, solo) gameplay screen. Battlegrounds uses its own card class and different keys, so it is only marked "differs" below.

Notation:
- `LK.X` means HSA's localization key `X`. Unless noted otherwise, its string tag is `ACCESSIBILITY_X` in `Strings/enUS/ACCESSIBILITY.txt`. Where the tag is a game string instead (GLOBAL.txt / GAMEPLAY.txt / GLUE.txt), the tag is given.
- `{0}`, `{1}` are format arguments. `|4(card,cards)` is the game's own plural syntax.
- A key name inside help text comes from the key binding. It is read from the KeyCode name, with overrides from `ACCESSIBILITY_INPUT_KEY_OVERRIDE_<Key>`: Return is read as "Enter", A as "eh", I as "eye". A modifier is added with `LK.INPUT_COMMAND_WITH_MODIFIER_FORMAT` "Shift + {0}" or `LK.INPUT_COMMAND_WITH_CTRL_FORMAT` "Ctrl + {0}".

## Our decisions (2026-10-01)

Where our version keeps HSA's behaviour and where it changes it:

| # | HSA behaviour | Ours |
|---|---|---|
| 1 | Focus reads only the name; statuses (Ready!, Cursed, ...) are separate lines | Keep |
| 2 | Nothing is said when a minion can't attack | Change: say why it can't (exhausted, frozen, ...) in the game's words |
| 3 | Tab on the opponent's turn says "You have no valid plays" and drops focus | Change: keep the focus |
| 4 | Arrows don't wrap; Tab wraps | Keep (Tab wraps, zones don't, for clarity) |
| 5 | A hidden cost in hand reads "0 mana" | Change: no cost when it is hidden |
| 6 | A minion in play reads its cost near the end | Keep |
| 7 | Weapons get no status words | Change: statuses like minions and heroes |
| 8 | Shift+S is called "count" in help but moves onto enemy secrets | Change: help says what it does |
| 9 | The opponent's hand can only be counted | Change: browsable (each card as the game shows it: hidden, or revealed) |
| 10 | I and K need the virtual mouse hovering the card | Change: read keywords and original stats from the game's data |

---

## 0. Speech output rules (they affect everything below)

- **Queue.** Each spoken item (an "utterance") is added to a FIFO queue and spoken in order. Several outputs from one key press (for example, a zone name and then a card) are separate utterances.
- **Interrupt on any key.** Any key-down interrupts the utterance being spoken and clears the queue (queued help narrations are also cut). So each new key press cuts off the previous output. With SAPI, the screen reader is also told to stop.
- **Focus gating.** An utterance is spoken only if its source is the screen or popup that has focus. While a popup (UI) is open, the gameplay screen is silent. Speech is also dropped when the game window is not focused, unless the option "background speech" is on.
- **Text curation.** Every utterance passes through one cleaning pipeline, in this order:
  1. Trim.
  2. Lowercase three known all-caps words: HIMSELF, ALL and LOT.
  3. Newlines become spaces. A line that is entirely bold (`<b>…</b>`, such as a lone "Taunt" or "Battlecry" line) gets a period after it.
  4. `</b> <b>` (two bold runs in a row) becomes `</b>. <b>`.
  5. Remove all `<…>` tags.
  6. Remove `[x]` and `[X]`.
  7. Remove `*`, the marker for spell damage that the game added to the text.
  8. Remove a "(+N Attack/+N Health)" parenthetical.
  9. `/` becomes a space.
  10. Capitalize the first letter or digit of each sentence.
  11. `_` (the game's non-breaking marker) becomes a space.
  12. `...` and `..` become `.`.
  13. Collapse runs of spaces.
  14. Trim.
  15. If the text does not end in a sentence-ending character, `."` or `:`, add a period.
  An empty result is not spoken.
- **List joining** (used for status words and hero stats): "A, B and C". The separators come from `ACCESSIBILITY_FORMATTING_LIST_SEPARATOR` "," and `ACCESSIBILITY_FORMATTING_LIST_FINAL_SEPARATOR` "and".
- **Joining help lines.** Lines are joined with ". " unless the previous line already ends in a sentence-ending character (`ACCESSIBILITY_FORMATTING_PERIOD`).

---

## 1. Phases and states

### 1.1 Phase (recomputed every frame from GameState)

| Phase | Condition (checked in this order) | Navigation available |
|---|---|---|
| MULLIGAN | `GameState.IsMulliganPhase()` and not in the "choose one" that starts after the mulligan | Mulligan list only (out of scope). The O and I keys work. |
| GAME_OVER | `GameState.IsGameOver()` | None. Only I and F1. |
| WAITING_FOR_GAME_TO_START | `GameState.IsMulliganPhasePending()`, or the game is not created yet | None. Only I and F1. |
| PLAYING | `GameState.IsGameCreated()` | Full navigation (see states). |
| WAITING_FOR_OPPONENT_MULLIGAN | Set by an event, then overwritten by the per-frame check above | Effectively never seen. Its help is almost unreachable. |

The I (tooltip) key is handled before the phase switch, so it works in every phase.

### 1.2 Sub-state during PLAYING (recomputed every frame, first match wins)

1. OPPONENT_TURN: `!GameState.IsFriendlySidePlayerTurn()`.
2. Internal "auto action" states (send minions to face, deck action, reverse-target drag). These belong to part 2.
3. CONFIRMING_END_TURN (part 2).
4. BROWSING_HISTORY: the history log is open.
5. A card is held (`InputManager.GetHeldCard()`):
   - a minion gives SUMMONING_MINION;
   - a location gives POSITIONING_LOCATION;
   - anything else gives PLAYING_CARD (only in main option mode).
6. MAIN_OPTION_MODE: `GameState.IsInMainOptionMode()`.
7. SUB_OPTION_MODE: `IsInSubOptionMode()`.
8. Choice mode (`IsInChoiceMode()`):
   - the rewind UI is showing gives RewindMode;
   - a target-type choice or visible friendly choice cards give CHOICE_MODE;
   - otherwise CHOICE_MODE_CHOICES_HIDDEN.
9. TARGET_MODE: `IsInTargetMode()`, or response mode OPTION_TARGET or OPTION_REVERSE_TARGET.
10. UNKNOWN: anything else (normally the network gap between turns).

### 1.3 Which navigation and reading handlers run in each state

| State | Card line reading (arrows Up/Down, K) | Status keys (A, D, C, O…) | Zone arrows, Home/End, numbers | Tab / Shift+Tab | Zone keys | History (Y) |
|---|---|---|---|---|---|---|
| WAITING / UNKNOWN / OPPONENT_TURN | yes | yes | yes | yes (see quirk 2.4) | yes | yes |
| MAIN_OPTION_MODE | yes | yes | yes | yes | yes | yes |
| TARGET_MODE | yes | yes | yes | yes | yes, but if the game cannot target cards in hand (`GameState.CanTargetCardsInHand()` is false), C, S and Shift+S do nothing | no |
| SUMMONING_MINION / POSITIONING_LOCATION | yes (the card focused before) | yes | no (the arrows move the placement slot, part 2) | no | no | no |
| PLAYING_CARD | no | yes | no | no | no | no |
| CHOICE_MODE / SUB_OPTION_MODE | the choice list (part 3) | yes | no | Tab hides the choices | no | no |
| CHOICE_MODE_CHOICES_HIDDEN / RewindMode | yes | yes | yes | Tab brings the choices back | yes | no |
| BROWSING_HISTORY | no | no | no | no | no | only history keys |

In OPPONENT_TURN and MAIN_OPTION_MODE, Space on a hero in play opens the emote menu (own hero gives emotes, enemy hero gives squelch). While that menu is open it takes all input. Not available to spectators.

---

## 2. Keys

### 2.1 The focus model

- There is one focused card (the "card being read") and its zone (the "current zone"). The zone is whatever `Card.GetZone()` returns, except hero powers, which use a virtual zone (see R).
- **Focusing a card** creates a fresh reader for it, so the line cursor resets to 0, even when you re-focus the same card. Then:
  1. Announce the zone change if needed (2.3).
  2. Speak line 0 of the card (section 3). In list-type zones it is spoken as `LK.MENU_OPTION_FORMAT` "{0} {1} of {2}" (line 0, 1-based position, zone card count), for example "Murloc Raider 2 of 4."
  3. Move the virtual mouse onto the card (section 4).
- **List-type zones** (position is spoken): own hand, opponent hand, own minions, opponent minions, own secrets, opponent secrets, and the hero-power zone when it holds more than one hero power. Hero, weapon and a single hero power are spoken without a position.
- **Automatic focus loss.** Every frame, if the focused card's zone is no longer the current zone (it was played, died, bounced or was stolen), the focus is cleared silently and the mouse is hidden. The arrows and number keys then do nothing until a zone key or Tab focuses something again.
- **Live content.** Card lines are rebuilt from live entity data on every key press, so values change while you read.

### 2.2 Zone keys (jump to a zone)

"First card" means slot 1. A zone key always announces the zone name ("forced"), then the card. Hero and weapon keys announce a zone only if the zone changed.

| Key | Action | Empty case (spoken) | Data source |
|---|---|---|---|
| C | Own hand, first card. Says "Hand", then "{card} 1 of N" | `LK.GAMEPLAY_SEE_ZONE_PLAYER_HAND_EMPTY` "Your hand is empty" | `Player.GetHandZone()` (ZoneHand) |
| B | Own minions, leftmost. Says "Minions", then the card | `LK.GAMEPLAY_SEE_ZONE_PLAYER_MINIONS_EMPTY` "You have no summoned minions" | `GetBattlefieldZone()` (ZonePlay) |
| G | Opponent minions, leftmost. Says "Opponent's minions", then the card | `LK.GAMEPLAY_SEE_ZONE_OPPONENT_MINIONS_EMPTY` "Your opponent has no summoned minions" | opposing `GetBattlefieldZone()` |
| S | Own secrets/quests zone, first. Says "Secrets", then the card | `LK.GAMEPLAY_SEE_ZONE_PLAYER_SECRETS_EMPTY` "You have no secrets" | `GetSecretZone()` (ZoneSecret) |
| Shift+S | Opponent secrets, first. Says "Opponent's secrets", then the card. The help text calls this "count", but it really focuses the zone | `LK.GAMEPLAY_SEE_ZONE_OPPONENT_SECRETS_EMPTY` "Your opponent has no secrets" | opposing `GetSecretZone()` |
| V | Own hero. No zone word, because line 0 is already "Your Hero" | (always exists) | `Player.GetHeroCard()` |
| F | Opponent hero ("Opponent's Hero") | — | opposing `GetHeroCard()` |
| R | Own hero power(s). Says "Hero Power", then the card (with "1 of 2" only if there are several) | `LK.GAMEPLAY_SEE_ZONE_PLAYER_HERO_POWER_EMPTY` "You have no Hero Power" | Every ZoneHeroPower of that side via `ZoneMgr.FindZonesOfType<ZoneHeroPower>`. The first card of each is ordered by the tag ADDITIONAL_HERO_POWER_INDEX. This list is rebuilt on every press. |
| Shift+R | Opponent hero power(s). Says "Opponent's Hero Power", then the card | `LK.GAMEPLAY_SEE_ZONE_OPPONENT_HERO_POWER_EMPTY` "Your opponent has no Hero Power" | same, opposing side |
| W | Own weapon. Says "Weapon" if the zone changed | `LK.GAMEPLAY_SEE_ZONE_PLAYER_WEAPON_EMPTY` "You have no equipped weapon" | `Player.GetWeaponCard()` |
| Shift+W | Opponent weapon ("Opponent's weapon") | `LK.GAMEPLAY_SEE_ZONE_OPPONENT_WEAPON_EMPTY` "Your opponent has no equipped weapon" | opposing `GetWeaponCard()` |

No key focuses the opponent's hand. Its cards cannot be reached in normal play; Shift+C only counts them.

When several zone keys are pressed in one frame, the first match wins, in this order: C, S, Shift+S, B, G, F, V, R, Shift+R, W, Shift+W.

### 2.3 Zone-change announcement

When focus moves to a card whose zone differs from the previous one (or when forced), one zone word is spoken before the card. The checks run in this order:

| Condition | Key / English |
|---|---|
| Card is a friendly hero power | `LK.GAMEPLAY_ZONE_PLAYER_HERO_POWER` "Hero Power" |
| Card is an opposing hero power | `LK.GAMEPLAY_ZONE_OPPONENT_HERO_POWER` "Opponent's Hero Power" |
| Card is the friendly weapon | `LK.GAMEPLAY_ZONE_PLAYER_WEAPON` "Weapon" |
| Card is the opposing weapon | `LK.GAMEPLAY_ZONE_OPPONENT_WEAPON` "Opponent's weapon" |
| Own hand | `LK.GAMEPLAY_ZONE_PLAYER_HAND` "Hand" |
| Opponent hand | `LK.GAMEPLAY_ZONE_OPPONENT_HAND` "Opponent's hand" (practically unused) |
| Own minions | `LK.GAMEPLAY_ZONE_PLAYER_MINIONS` "Minions" |
| Opponent minions | `LK.GAMEPLAY_ZONE_OPPONENT_MINIONS` "Opponent's minions" |
| Own secrets | `LK.GAMEPLAY_ZONE_PLAYER_SECRETS` "Secrets" |
| Opponent secrets | `LK.GAMEPLAY_ZONE_OPPONENT_SECRETS` "Opponent's secrets" |
| Hero zone | nothing (the hero's line 0 names it) |

### 2.4 Moving within the current zone

These keys do nothing if no zone is current.

| Key | Behaviour |
|---|---|
| Right / Left arrow | Next / previous slot. **No wrapping.** Past either end nothing is spoken and the focus stays. In a single-card zone (hero, weapon, one hero power) the arrows are silent. |
| Home / End | First / last slot of the zone. |
| 1–9, 0 (top row or numpad) | Jump to slot N, where 0 means 10. A slot out of range is silent. |

Each landing is a normal focus (no zone word, because the zone is unchanged), for example "Chillwind Yeti 3 of 5." The data comes from `Zone.GetCardAtSlot(n)` and `GetCardCount()`. The position spoken is the card's index in the zone (`FindCardPos`).

### 2.5 Tab / Shift+Tab: cycle through valid items

- **Candidate list** (fixed order). Friendly first: own hand (left to right), own weapon, own hero, own hero power, own minions (left to right). Then opponent: minions, hero, weapon, hero power. In target mode, if the source card's effect is classified as hostile ("UNFRIENDLY"), the opponent block comes first.
- **Validity test** depends on the game mode:

  | Mode | Test |
  |---|---|
  | Main option mode | `GameState.IsValidOption(entity)` |
  | Sub-option mode | `IsValidSubOption` |
  | Choice mode | `IsChoice` |
  | Target or reverse-target mode | `IsValidOptionTarget(entity, true)` |

- **No valid items.** Says `LK.GAMEPLAY_NO_VALID_PLAYS` "You have no valid plays" and **clears the focus** (mouse hidden). Quirk: on the opponent's turn and in UNKNOWN nothing is valid, so Tab always says this and drops your focus.
- **Exactly one valid item.** Focus it and force the zone word.
- **Nothing focused.** Tab focuses the first valid item, Shift+Tab the last.
- **Otherwise.** Find the focused card's index in the candidate list (index 0 if it is not a candidate, for example a secret). Step forward or backward, **wrapping**, to the next valid candidate and focus it. The zone word is spoken only when the zone changes.
- **Target mode.** If your own hero power is focused, the search restarts from the start of the list.

### 2.6 Reading lines of the focused card

| Key | Behaviour |
|---|---|
| Down | Next line. At the last line it is silent (no wrap). |
| Up | Previous line. At line 0 it is silent. |
| Shift+Up | Repeat the current line. |
| Shift+Down | Read from the current line to the end, each line a separate utterance. |
| Page Down / Page Up | Only on cards with a "second page" (see 3.2, related-card token): jump to the first line of page two, or back to line 0. |

Line 0 is spoken on focus. Down then reads line 1.

### 2.7 Information keys

| Key | Spoken output (each bullet is one utterance) | Source |
|---|---|---|
| A | See "A key output" below | `Player.GetNumAvailableResources()`, tag RESOURCES, OVERLOAD_OWED, OVERLOAD_LOCKED, `CorpseCounter.ShouldShowCorpseCounter`, `Player.GetNumAvailableCorpses()`, `Entity.IsLaunchpad()`, `GameUtils.StarshipLaunchCost` |
| Shift+A | The same for the opponent, using the `GAMEPLAY_READ_OPPONENT_MANA*` keys ("Your opponent has {0} out of {1} mana" / "Your opponent has {0} mana"). Overload uses `LK.GAMEPLAY_READ_OPPONENT_OVERLOADED_MANA`, tag `GAMEPLAY_TOOLTIP_ENEMYOVERLOAD_DESC` "{0} Mana Crystal(s) Overloaded.". There is no locked-mana line. Corpses and starship are the same as for A. | opposing Player |
| D | `LK.GAMEPLAY_READ_PLAYER_DECK` "Deck. {0} card(s)" | `GetDeckZone().GetCardCount()` |
| Shift+D | `LK.GAMEPLAY_READ_OPPONENT_DECK` "Opponent's Deck. {0} card(s)" | opposing deck zone |
| Shift+C | `LK.GAMEPLAY_READ_OPPONENT_HAND` "Opponent's hand. {0} card(s)" | opposing `GetHandZone().GetCardCount()` |
| O | No anomalies: `LK.GAMEPLAY_NO_ANOMELIES` (tag `ACCESSIBILITY_GAMEPLAY_NO_ANOMALIES`) "There are no anomalies". Otherwise, for each anomaly, `LK.BATTLEGROUNDS_GAMEPLAY_READ_ANOMALY` "Anomaly: {0}; {1}" with the name and the card text in hand. Works in the mulligan too; not in Battlegrounds. | `MulliganManager.GetAnomalies()` gives entity ids, then the card DB id, then the EntityDef |
| I | For each active tooltip panel: its name, then its body. Then for each tutorial keyword panel: its name, then its body. Silent if none are shown. Works in every phase. | `TooltipPanelManager` panels (HSA adds a getter for the panel list), `TutorialKeywordManager.GetPanels()` |
| K | Minions only (on any other card K is silent). See "K key output" below. | EntityDef stats, `BigCard`, `Entity.GetDisplayedEnchantments()` |
| Y | Opens the history log (part 4). It speaks the newest entry as `LK.GAMEPLAY_HISTORY_LOG_ENTRY_FORMAT` "{2} ; {0} of {1}", or `LK.GAMEPLAY_HISTORY_LOG_EMPTY` "Nothing has been played yet". In the log: Up/Down move, Shift+Up repeats, Shift+Down reads to the end, Backspace closes with `LK.GAMEPLAY_HISTORY_LOG_CLOSE` "Stopped reading history". | HSA's own event log |
| F1 | Context help (section 5). | — |

**A key output** (your own resources):
- If available mana differs from the RESOURCES total: `LK.GAMEPLAY_READ_PLAYER_MANA_CURRENT_AND_TOTAL` "You have {0} out of {1} mana". Otherwise `LK.GAMEPLAY_READ_PLAYER_MANA` "You have {0} mana". Temporary mana can produce "3 out of 2".
- If OVERLOAD_OWED > 0: `LK.GAMEPLAY_READ_PLAYER_OVERLOADED_MANA`, which is game tag `GAMEPLAY_TOOLTIP_MANA_OVERLOAD_DESCRIPTION` "{0} Mana Crystal(s) **Overloaded**." (the bold is stripped).
- If OVERLOAD_LOCKED > 0: `LK.GAMEPLAY_READ_PLAYER_LOCKED_MANA`, which is game tag `GAMEPLAY_TOOLTIP_MANA_LOCKED_DESCRIPTION` "{0} Mana Crystal(s) Overloaded last turn."
- If the corpse counter applies: `LK.GAMEPLAY_READ_CORPSES` "{0} corpse(s)" or `LK.GAMEPLAY_READ_CORPSES_EMPTY` "no corpses".
- If a launchpad minion is on your board: `LK.GAMEPLAY_READ_STARSHIP_LAUNCH_COST` "{0} mana to launch starship".

Some tutorials switch off the mana and corpse counters. A and Shift+A are then silent.

**K key output** ("original stats and enchantments"):
- `LK.READ_CARD_ATK_HEALTH` "{0} {1}" with the **printed** attack and health (`EntityDef.GetATK/GetHealth`). Skipped if the tag HIDE_STATS is set.
- If the BigCard is showing an enchantment banner, the banner text (for example "turns until this revives").
- For each distinct enchantment (deduplicated by name and text):
  - "Enchantment" (tag `GLOBAL_CARDTYPE_ENCHANTMENT`);
  - the header: the name, or game tag `GAMEPLAY_ENCHANTMENT_MULTIPLIER_HEADER` "{0}x {1}" when the count is above 1 (the count adds up max(SPAWN_TIME_COUNT, 1) across duplicates);
  - the enchantment's text in hand.

---

## 3. Card reading: the line lists

### 3.0 Shared building blocks

**Header.** Every list starts with these items, **each as its own line**:
1. **Name.**
   - Hero in PLAY: `LK.GAMEPLAY_ZONE_PLAYER_HERO` "Your Hero" or `LK.GAMEPLAY_ZONE_OPPONENT_HERO` "Opponent's Hero".
   - Non-friendly secret: `LK.GLOBAL_SECRET` "{0} secret", with the class name from `GameStrings.GetClassName(entity.GetClass())`, for example "Mage secret".
   - Otherwise `Entity.GetName()`.
2. "Cursed" if the tag EVIL_GLOW is set. Key `LK.GLOBAL_CURSED`, tag `ACCESSIBILITY_GLOBAL_KEYWORD_CURSED`.
3. "haunted" if the tag VALEERASHADOW is set. Key `LK.GLOBAL_HAUNTED`, tag `ACCESSIBILITY_GLOBAL_KEYWORD_HAUNTED`.
4. "Ready!" (`LK.GLOBAL_READY`, game tag `GAMEPLAY_LETTUCE_READY_BUTTON`). Its meaning depends on the card:
   - **Location:** it is in PLAY and not exhausted.
   - **Every other card:** its Actor state (`Actor.GetActorStateType()`) is CARD_POWERED_UP or CARD_COMBO, which is the game's green or yellow "playable" glow. That covers a playable card in hand, a minion or hero that can attack, and a usable hero power. It only appears for your own things on your turn.
5. For an objective in the SECRET zone: its custom banner text (`Entity.GetCustomObjectiveBannerText()`).
6. (Battlegrounds Duos ping, ignore.)

Because these are separate lines, line 0 is **only the name**, and "Ready!" is heard by pressing Down. There is **no "exhausted" word**: a minion that cannot attack simply has no "Ready!".

**Cost line.** `LK.READ_CARD_COST` "{0} mana", from `Entity.GetCost()` (the live cost):
- If the cost is hidden (tag HIDE_STATS or HIDE_COST), the cost is forced to 0. Quirk: normal cards in hand then say "0 mana".
- If the card costs health instead of mana (`GetRealTimeCardCostsHealth()`): `LK.READ_HERO_CARD_HEALTH` "{0} health".
- Battlegrounds has its own variants.

**Resources line.** Built from live values:

| Card | Format |
|---|---|
| Minion | `LK.READ_CARD_ATK_HEALTH` "{0} {1}" with `GetATK()` and `GetCurrentHealth()`, for example "3 2" |
| Weapon | `LK.READ_CARD_ATK_DURABILITY` "{0} {1}" (attack, current durability) |
| Location | `LK.READ_CARD_DURABILITY` "{0} durability" (current health) |
| Hero | A joined list: `LK.READ_HERO_CARD_ATK` "{0} attack" (only if > 0 and not hidden), `LK.READ_HERO_CARD_ARMOR` "{0} armor" (only if > 0), `LK.READ_HERO_CARD_HEALTH` "{0} health" (unless hidden). Example: "4 attack, 5 armor and 22 health". Attack and health are hidden by HIDE_STATS, or by HIDE_ATTACK and HIDE_HEALTH respectively. |
| Spell and others | none |

The resources line is empty if:
- the card is dormant (`IsDormant()`) or has the tag DORMANT_VISUAL, unless it is a launchpad; or
- it has HIDE_STATS = 1, unless it is a starship.

**Status words ("effects").** These are built only for cards in PLAY. The order is fixed, and the words are joined as a list:

| Word | Condition | Key / game tag |
|---|---|---|
| Silence | silenced minion in play | GLOBAL_KEYWORD_SILENCE |
| Divine Shield | `HasDivineShield()` | GLOBAL_KEYWORD_DIVINE_SHIELD |
| Frozen | `IsFrozen()` | GLOBAL_KEYWORD_FROZEN |
| Lifesteal | `HasLifesteal()` | GLOBAL_KEYWORD_LIFESTEAL |
| Deathrattle | `HasDeathrattle()` | GLOBAL_KEYWORD_DEATHRATTLE |
| Poisonous | `IsPoisonous()` | GLOBAL_KEYWORD_POISONOUS |
| Stealth | `IsStealthed()` | GLOBAL_KEYWORD_STEALTH |
| Taunt | `HasTaunt()` | GLOBAL_KEYWORD_TAUNT |
| Elusive | tag ELUSIVE | GLOBAL_KEYWORD_ELUSIVE |
| Immune | `IsImmune()` | GLOBAL_KEYWORD_IMMUNE |
| Dormant | `IsDormant()` | GLOBAL_KEYWORD_DORMANT |
| Reborn | `HasReborn()` | GLOBAL_KEYWORD_REBORN |
| Windfury | `HasWindfury()` | GLOBAL_KEYWORD_WINDFURY |
| Venomous | `IsVenomous()` | GLOBAL_KEYWORD_VENOMOUS |
| Dark Gift | tag HAS_DARK_GIFT | GLOBAL_KEYWORD_DARKGIFT |

Battlegrounds adds a Darkmoon ticket word. Rush, Charge, spell damage and "exhausted" are **not** status words; Rush and Charge can still appear in the description text. In play, the words are **appended to the resources line after a space**, for example "3 2 Divine Shield and Taunt." If there is no resources line (for example a dormant minion), they form their own line.

**Description.** `Entity.GetCardTextBuilder().BuildCardTextInHand(entity)`, the live text including spell damage and dynamic numbers. A silenced minion in play gives just "Silence" instead.

**Race / spell school.** `EntityDef.GetRaceText()`.

**Faction.** Game tag `GLOBAL_KEYWORD_PROTOSS`, `GLOBAL_KEYWORD_TERRAN` or `GLOBAL_KEYWORD_ZERG` if the EntityDef has that tag.

**Type.** `GameStrings.GetCardTypeName(cardType)`, for example "Minion", "Spell", "Weapon", "Location", "Hero".

**Rarity.** If the card is elite: "Legendary". Otherwise `GameStrings.GetRarityText`. Omitted for FREE and INVALID.

Absent (empty) lines are skipped. Line numbers therefore shift from card to card.

### 3.1 Choosing the reader (first match wins)

1. Hero, then 3.4 or 3.5.
2. Hero power, then 3.6.
3. Weapon, then 3.7.
4. Quest, then 3.8.
5. Side quest, then 3.8.
6. Questline, then 3.8.
7. Secret **not** controlled by you, then 3.9.
8. Tag IS_NIGHTMARE_BONUS. This reads the linked minion (tag TAG_SCRIPT_DATA_ENT_1) as a normal card, with this entity added as the "extra" line.
9. Otherwise a normal card, 3.2 (minion, spell, location, your own secret, an objective, and so on).

### 3.2 Normal card (minion, spell, location, own secret)

| # | Line | Hand / other non-play zone | In PLAY | In SECRET zone |
|---|---|---|---|---|
| 1 | Header (name + statuses, separate lines) | ✓ | ✓ | ✓ |
| 2 | Cost "{n} mana" | ✓ | – | – |
| 3 | Resources | ✓ ("3 2"; spells have none) | ✓ with status words appended | ✓ if any |
| 4 | Description | ✓ | ✓ ("Silence" if silenced) | ✓ |
| 5 | Extra entity "{name}: {text}". Dark Gift: the entity in tag DARK_GIFT_ENTITY (if > 1); nightmare bonus: the bonus entity | if present | if present | if present |
| 6 | Race / spell school | if any | if any | if any |
| 7 | Faction | if any | if any | if any |
| 8 | Cost "{n} mana" | – | ✓ minions only (not in Battlegrounds), placed late | – |
| 9 | Type | ✓ | – | ✓ |
| 10 | Rarity | if any | if any | if any |
| 11 | **Page two** (hand only, if the tag DISPLAY_CARD_ON_MOUSEOVER is set, for example Colossal appendages): the related card's EntityDef lines: name (with "{0} - golden/diamond/signature edition" by premium; normal premium here), "{cost} mana", rune cost, resources from the def, text in hand, race, faction, type, rarity. Page Down jumps here. | ✓ | – | – |

Examples:
- Minion in hand: "Chillwind Yeti 2 of 5." / "Ready!" / "4 mana." / "4 5." / "Minion." / (rarity).
- Minion in play: "Chillwind Yeti 1 of 3." / "4 5 Taunt." / description / race / "4 mana." / rarity.

### 3.3 Locations

Locations are normal cards. In play they show "Ready!" when not exhausted. The resources line is "{n} durability". There is no exhausted or cooldown word.

### 3.4 Hero in PLAY

1. Header. Line 0 is "Your Hero" or "Opponent's Hero", then "Ready!" if the hero can attack.
2. Resources line (attack, armor, health, as in 3.0) with status words appended (Frozen, Immune, Stealth…). If there are no stats, the status words stand alone.
3. The hero's real name (`EntityDef.GetName()`, for example "Jaina Proudmoore").
4. In PvP games only:
   - the controller's player name (`Player.GetName()`);
   - the class name;
   - the rank text. Your own rank is always given. The opponent's rank is given only if you are at Legend rank; the server sends it either way.

Turn-counter missions (`GameEntity.IsTurnCounterBasedMission()`, opposing hero only): header, then `MissionEntity.GetTurnCounterText()`, then the real name.

### 3.5 Hero card not in play (hand, choices)

1. Header.
2. Cost.
3. Type ("Hero").
4. `LK.READ_HERO_CARD_ARMOR` "{n} armor", always given, even 0.
5. Description.
6. The hero power lines for that hero (`GameUtils.GetHeroPowerCardIdFromHero`):
   - "Hero Power. {name}" (`LK.GAMEPLAY_ZONE_PLAYER_HERO_POWER` combined with the name);
   - "{n} mana" unless hidden;
   - its text.
7. Rarity.

### 3.6 Hero power

1. Header. Line 0 is the name, then "Ready!" if usable. There is no "used" or "exhausted" word.
2. Cost (skipped if hidden).
3. Description.

### 3.7 Weapon

1. Header.
2. Cost, only if not wielded. "Wielded" means the card's zone is a ZoneWeapon.
3. Resources "{atk} {durability}". Status words are **not** appended for weapons.
4. Description.
5. Type, only if not wielded.
6. Rarity.

### 3.8 Quests

- **Quest:**
  1. header;
  2. description;
  3. `LK.TOAST_QUEST_PROGRESS_TOAST_PROGRESS` "Current progress is {0} out of {1}" (tags QUEST_PROGRESS and QUEST_PROGRESS_TOTAL);
  4. reward lines (from `QuestController.GetRewardCardIDFromQuestCardID`):
     - `LK.UI_QUEST_REWARD_DESCRIPTION` "Rewards {name}";
     - "{n} mana" unless hidden;
     - minion or weapon stats;
     - text (dynamic quest reward text via `Actor.FormatDynamicQuestRewardText`, otherwise the def text);
     - race;
     - type;
     - rarity.
- **Side quest:** header, description, progress.
- **Questline:**
  1. header;
  2. description;
  3. progress;
  4. the current part's label (the questline progress UI's left detail text, if active);
  5. each active requirement text plus " Checked" or " Not checked" (`LK.OPTIONS_MENU_CHECKBOX_CHECKED` / `_NOT_CHECKED`, chosen by whether the checkmark object is active);
  6. reward lines;
  7. the next part's label (right detail text).

  The questline parts are read from the on-screen `QuestlineController` UI, not from game tags.

### 3.9 Opponent's secret

The header only: "{Class} secret" plus any trailing statuses. Nothing else is revealed. The opponent's quests and objectives in the secret zone are read in full by the quest or normal readers, because those checks come first.

### 3.10 Hidden opponent cards and Battlegrounds

- Opponent hand: not navigable; Shift+C gives the count only.
- Opponent secrets: see 3.9.
- Battlegrounds uses a different card reader and key map (for example P for hero powers, T for the tavern). It **differs** and is not covered here.

### 3.11 Enchantments

Enchantments are not listed in the line list. Their effects show up through the live values (current attack and health, live description, cost). K lists them explicitly (2.7).

---

## 4. Virtual mouse during navigation

HSA owns the game's mouse input. Each frame it injects a position (and clicks) into the game's input layer, in place of the real mouse.

- **Every frame in PLAYING** (except during the auto-attack, deck-action and reverse-drag states):
  - if a card is held, the mouse is left where the placement logic put it;
  - otherwise, if a card is focused, the mouse is moved onto that card;
  - otherwise the mouse is "hidden", which means the position is set to screen (0,0).

  Losing focus (Tab with nothing valid, the focused card leaving its zone, and so on) hides the mouse.
- **Where the mouse is put.**
  - A hand card: the card's **mana gem** transform (`Actor.m_manaObject`), and only if the card is not already moused over (`Card.IsMousedOver()`). Hovering a hand card lifts and enlarges it, and re-aiming every frame would make it flicker. If the mana gem is missing, the attack gem is used, then the health gem.
  - Any other card: the card's world position projected to the screen with the main camera.
- **Why.**
  1. The game reacts to the hover as for a sighted player. `InputManager` sets its moused-over card and shows the BigCard or the raised hand card, and the keyword tooltip panels (`TooltipPanelManager`) appear. **I** reads exactly those panels, and **K** reads the BigCard's enchantment banner. Without the hover, both are silent.
  2. Later actions (Enter to play, attack or target) are plain left-clicks at the current mouse position. They only work because the mouse is already on the focused card. Before such a click, HSA checks `InputManager.GetMousedOverCard()`. If it is not the focused card, HSA says `LK.GAMEPLAY_TRY_AGAIN` "Try Again" and forces the moused-over card (part 2).
  3. HSA patches `InputManager` so that the hover does not trigger "interactable" activation while accessibility is on. It also uses the mouse-over hook to re-announce "Choose a target" (part 2).
- Tutorials can turn off hiding the mouse.
- If our version drops the virtual mouse, it must obtain tooltips, the BigCard banner and clicking some other way. Card lines and "Ready!" do not depend on hovering, because they come from the entity and actor state.

---

## 5. Help (F1)

F1 speaks the help of the focused popup if there is one, otherwise the gameplay screen's help. It is one utterance; the lines are joined as in section 0. `{key}` stands for the bound key's spoken name.

| Phase / state | Help content |
|---|---|
| WAITING_FOR_GAME_TO_START | `LK.GAMEPLAY_MULLIGAN_HELP_WAITING_FOR_GAME_TO_START` "The game is about to start. Please wait" |
| MULLIGAN | `LK.GAMEPLAY_MULLIGAN_HELP` "Use the arrow keys to go through your starting cards. Use {Space} to mark the cards you'd like to replace. Press {Enter} once you're done" |
| WAITING_FOR_OPPONENT_MULLIGAN | `LK.GAMEPLAY_WAITING_FOR_OPPONENT` "Waiting for opponent" |
| GAME_OVER | `LK.GAMEPLAY_GAME_OVER_GENERIC` "Game over" |
| PLAYING, emote menu open | the emote menu's help |
| OPPONENT_TURN | `LK.GAMEPLAY_OPPONENT_TURN` "Opponent's turn" |
| WAITING / UNKNOWN | empty (nothing spoken) |
| MAIN_OPTION_MODE | dynamic, see below |
| TARGET_MODE | `LK.GAMEPLAY_CHOOSE_TARGET_HELP` "Use {Tab} or the Zone keys to go through your targets. Press {Enter} to choose a target or {Backspace} to cancel" |
| CHOICE_MODE / SUB_OPTION_MODE | `LK.GAMEPLAY_CHOICE_MODE_HELP` "Use the arrow keys to go through your options. Press {Enter} to choose one. Press {Tab} to inspect the board". While awaiting confirmation: `LK.TUTORIAL_HOGGER_2_5` "Press {Enter} to confirm or {Backspace} to cancel". |
| CHOICE_MODE_CHOICES_HIDDEN | `LK.GAMEPLAY_HIDDEN_CHOICE_HELP` "Use the zone keys to inspect the state of the game board. Press {Tab} to return to the choices" |
| RewindMode | `LK.GAMEPLAY_REWIND_MODE_HELP` "…Press {U} to rewind, or {J} to keep the current state." |
| SUMMONING_MINION | `LK.GAMEPLAY_SUMMON_MINION_HELP` "Press {Enter} to summon this minion" |
| POSITIONING_LOCATION | `LK.GAMEPLAY_POSITION_LOCATION_HELP` "Press {Enter} to place this location" |
| PLAYING_CARD | `LK.GAMEPLAY_PLAY_CARD_HELP` "Press {Enter} to play this card" |
| CONFIRMING_END_TURN | `LK.GAMEPLAY_CONFIRM_END_TURN_HELP` "Press {Enter} or {E} to end your turn. Press any other key to cancel" |
| BROWSING_HISTORY | `LK.GAMEPLAY_READ_HISTORY_HELP` "Use the up and down arrow keys to read the play history. Press {Backspace} to close the history log" |
| Deck action pending | `LK.TUTORIAL_HOGGER_2_5` "Press {0} to confirm or {1} to cancel" |

### 5.1 MAIN_OPTION_MODE help, nothing focused

Built in this order:
1. If there are no valid options: `LK.GAMEPLAY_END_TURN_HELP` "Press {E} to end your turn".
2. If there are valid options: `LK.GAMEPLAY_SEE_VALID_OPTIONS` "Use {Tab} to go through your valid options".
3. If your hand is not empty: `LK.GAMEPLAY_SEE_PLAYER_HAND_HELP` "Press {C} to look at your hand".
4. If you have minions: `LK.GAMEPLAY_SEE_PLAYER_MINIONS_HELP` "Press {B} to look at your summoned minions".
5. If you have secrets: `LK.GAMEPLAY_SEE_PLAYER_SECRETS_HELP` "Press {S} to look at your secrets".
6. `LK.GAMEPLAY_SEE_PLAYER_HERO_HELP` "Press {V} to look at your hero".
7. If you have a hero power: `LK.GAMEPLAY_READ_PLAYER_HERO_POWER_HELP` "Press {R} to read your hero power".
8. If the opponent has a hero power: `LK.GAMEPLAY_READ_OPPONENT_HERO_POWER_HELP` "Press {Shift + R} to read your opponent's hero power".
9. `LK.GAMEPLAY_SEE_OPPONENT_HERO_HELP` "Press {F} to look at your opponent's hero".
10. If the opponent has minions: `LK.GAMEPLAY_SEE_OPPONENT_MINIONS_HELP` "Press {G} to look at your opponent's minions".
11. Not in a tutorial:
    - `LK.GAMEPLAY_COUNT_PLAYER_DECK_HELP` "Press {D} to count the remaining cards in your deck";
    - `LK.GAMEPLAY_COUNT_OPPONENT_DECK_HELP` "Press {Shift + D} to count the remaining cards in your opponent's deck";
    - `LK.GAMEPLAY_COUNT_OPPONENT_HAND_HELP` "Press {Shift + C} to count the cards in your opponent's hand";
    - `LK.GAMEPLAY_COUNT_OPPONENT_SECRETS_HELP` "Press {Shift + S} to count your opponent's secrets".
12. `LK.GAMEPLAY_READ_PLAYER_MANA_HELP` "Press {eh} to read your mana".
13. Not in a tutorial:
    - `LK.GAMEPLAY_READ_OPPONENT_MANA_HELP` "Press {Shift + eh} to read your opponent's mana";
    - `LK.GAMEPLAY_OPEN_HISTORY_LOG_HELP` "Press {Y} to open the play history log".

A game is a tutorial if its game entity is a TutorialEntity subclass. The weapon help strings exist (`GAMEPLAY_READ_PLAYER_WEAPON_HELP` / `…OPPONENT_WEAPON_HELP`) but are never added. Anomalies and K are not mentioned either.

### 5.2 MAIN_OPTION_MODE help, a card focused

1. If there are no valid options: "Press {E} to end your turn". In that case it is not repeated later.
2. `LK.GAMEPLAY_READ_CARD_HELP` "Use the up and down arrow keys to read this card".
3. If any tooltip or keyword panels are showing: `LK.GAMEPLAY_READ_CARD_TOOLTIP_HELP` "Press {eye} to get a description of this card's abilities".
4. If the focused card is a valid option:
   - **In hand:** `LK.GAMEPLAY_PLAY_CARD_HELP` "Press {Enter} to play this card". Add `…TRADE_CARD_HELP` / `…FORGE_CARD_HELP` / `…PREPARE_CARD_HELP` "Press {T} to trade / forge / prepare this card" if the card is tradeable, forgeable or prepareable.
   - **Own minion:** `LK.GAMEPLAY_ATTACK_WITH_MINION_HELP` "Press {Enter} to attack with this minion".
   - **Own hero:** `LK.GAMEPLAY_ATTACK_WITH_HERO_HELP`.
   - **Own hero power:** `LK.GAMEPLAY_USE_HERO_POWER_HELP` "Press {Enter} to use your hero power".
5. If it is not valid and it is in your hand: the mana help ("Press {eh} to read your mana") moves to the front of the general list.
6. Then the whole 5.1 list (minus the end-turn line if already said, and minus the mana line if moved).

---

## 6. Notable quirks to decide on

1. "Ready!" is a separate line after the name, and its meaning depends on the card (playable, can attack, or location not exhausted). There is no word for "exhausted" or "can't attack".
2. On the opponent's turn, Tab always says "You have no valid plays" and **drops the focus**.
3. The arrows do not wrap; Tab does wrap.
4. A card in hand with a hidden cost reads "0 mana".
5. A minion in play reads its cost near the end (after race), not after the name.
6. Weapons never get status words; heroes and minions do.
7. Shift+S is described as "count" in the help but actually focuses the opponent's secrets. Shift+C really only counts.
8. Zone keys always repeat the zone word. Tab, hero and weapon keys say it only when the zone changes. The hero zone never has a zone word.
9. The WAITING_FOR_OPPONENT_MULLIGAN phase is overwritten every frame, so its help is practically never heard.
10. I and K depend on the virtual-mouse hover (BigCard and tooltip panels).

---

## Out of scope (later parts)

- **Part 2, actions:** Enter (play, attack, use), held-card placement (arrows, Home/End and numbers choose the summon slot; Ctrl+Up/Down switch side for Disguise cards), PLAYING_CARD confirm/cancel (Enter/Backspace), target mode and "Choose a target", reverse-target, T trade/forge/prepare, E / Shift+E end turn and its confirmation, Shift+F (all minions to face), Ctrl+F (focused minion to face), "Try Again".
- **Part 3, choices:** mulligan (Space to mark, Enter to confirm, list navigation), Discover / Choose One / sub-options, hidden choices (Tab toggle), quest reroll (R), Rewind (U/J), trinket confirmation.
- **Part 4, announcements:** turn start/end, coin and first player, "vs player" intro, cards drawn and played (play describer, power-task-list describer), history log entries, game-over result, reconnect, emotes.
- **Battlegrounds** gameplay and card reading.
