# Hearthstone Access Enhanced (unofficial)

An accessibility mod that makes Hearthstone playable for blind players, for **Windows and macOS**, with speech through [Prism](https://github.com/ethindp/prism) (your screen reader, or the system voices).

Since version 2.0 it is **our own mod from top to bottom**: it no longer needs or downloads [Hearthstone Access](https://hearthstoneaccess.com). It is built on your machine against your installed game, and everything it says comes from the game's own texts (plus Hearthstone Access's text files, which it ships with its makers' agreement).

This project is not affiliated with Blizzard Entertainment or with the Hearthstone Access developers. Use it at your own risk.

## What it reads

Every screen is a menu or a list: arrows move, Enter acts, Backspace goes back, F1 tells the keys of the screen you are on.

- **Main menu** with every button the box shows, your gold and runestones; **Game Modes**, **Adventures** (books, chapters, missions and why one is locked), **Tavern Brawl**, **Arena** (drafts with full card descriptions and navigable legendary buckets, redrafts and deck editing), **Battlegrounds** (lobby, hero choice, recruit phase, collection), **Mercenaries**, **Journal** (quests, reward tracks, events, achievements), **Friends**, **Shop** (gold on every page, real money asks twice), **Black Market**, **Darkmoon Faire Treasures**, **Mail**, **Options**, **Credits**.
- **My Collection**: the book page by page in its class tabs (Tab: next class, number keys: mana, Ctrl+F: search), every card's lines (cost, stats, text, tribe, type, rarity, copies, set), its view (read it, its flavor and related cards, craft or disenchant), I for keyword explanations; Change set, Crafting with its filters and mass disenchant; decks (edit, new with format and recipe, delete, paste, copy), See deck, sideboards (E.T.C., Zilliax), card backs, coins, heroes, pets.
- **Packs**: the pack list, opening one or several, cards turned one by one or at random, highlights and the summary by rarity.
- **Matches**: zones and cards read line by line, Tab to the next playable card, Enter to play, attack and target (with the game's sounds and voice lines), placing minions, Discover, Choose One, Rewind (U / J), mulligan, emotes (Space on your hero on either turn), Y to browse match announcement history, Z for your pet and the board's clickable decorations; announcements of what happens (plays, attacks, triggers, damage, deaths, draws, turns, quest progress, the rope) and of the way in (opponent, who goes first, the coin).
- **End of a match**: the result, rank and stars, win streak, card back progress, reward XP and levels, quests finished.
- **Popups** of every kind, the Escape menu, quest and achievement toasts, the launch's loading texts.

## Download

Ready-to-use packages are in [`release/`](release/) and on the [releases page](../../releases):

- **1 Hearthstone access enhanced for Windows.zip**, **2 Hearthstone access enhanced for Mac.zip**, source in **3 Hearthstone access enhanced sourcecode.zip**. Both packages contain the same `Resources`; only the installer differs.
- **4 Hearthstone access for Mac (port).zip** and **5 … sourcecode.zip** are the older plain port of Hearthstone Access to the Mac (the [`mac-port`](../../tree/mac-port) branch), kept for those who want it.

## Windows

Requirements: Hearthstone installed with Battle.net, an internet connection during installation. The .NET 8 SDK is downloaded if missing.

1. Quit Hearthstone.
2. Run **Install Hearthstone access.bat** (it asks for administrator rights; the game lives in Program Files).
3. When it says DONE, start the game from Battle.net.

Speech goes to your screen reader (NVDA, JAWS and others Prism supports), or to the Windows voices when none is running. If you had the official Hearthstone Access installed, repair the game in Battle.net first (Options > Scan and Repair); an earlier version of this mod is replaced as it is.

A scheduled task repairs the mod by itself: it checks every minute and rebuilds after a game update (or a new language, which replaces the game's files too). If the game is running at that point, the new build is made meanwhile and a message asks whether to close Hearthstone now and put the mod back; if you say no, it goes back in the moment you close the game. **Uninstall Hearthstone access.bat** removes everything and puts the game's own files back. Logs: `C:\ProgramData\HearthstoneAccess\logs`.

## macOS

Requirements: a Mac with Apple silicon (Intel is untested), Hearthstone in `/Applications/Hearthstone`, Xcode Command Line Tools (the installer asks for them), .NET 8 SDK (downloaded into `~/.dotnet`).

1. Quit Hearthstone.
2. Open **Install Hearthstone access.command**.
3. When it says DONE, start the game from Battle.net.

Speech uses the macOS system voice and its settings (Spoken Content), per language. A watcher rebuilds the mod after a game update. **Uninstall Hearthstone access.command** removes it. Logs: `~/Library/Logs/HearthstoneAccess`.

## How it works

No Blizzard code is kept in this repository; the mod is compiled on the player's machine against the installed game's own assemblies.

- `Resources/enhanced`: the mod (its core: speech, keys, focus, menus, text input; and every screen), compiled against the game's own class library.
- `Resources/tools/port`: a Mono.Cecil tool that hooks the mod into the game's `Assembly-CSharp.dll` (the game's input update and its error messages) and checks every reference against the game's own assemblies.
- `Resources/strings`: Hearthstone Access's text files (`ACCESSIBILITY.txt` per language), reused with its makers' agreement; texts only, no code.
- Windows: `Resources/windows` (install, rebuild and uninstall scripts; `speech` is the Prism bridge; `prism` holds `prism.dll`).
- macOS: a loader (`Resources/loader`) serves the mod's files to the game without modifying it; `Resources/tolk` + `Resources/voiceover` speak through Prism's AVSpeech backend; `Resources/hsa-watch.sh` keeps Battle.net running with the loader.

## Credits

- Hearthstone Access (Guide Dev and the Hearthstone Access community developers), whose work this mod grew from and whose texts it reuses.
- Prism by Ethin Probst, MPL-2.0 (`Resources/windows/prism/LICENSES`, `Resources/voiceover/prism/LICENSE-prism-MPL-2.0.txt`); its third-party notices are in `Resources/windows/prism/NOTICE`.
- Hearthstone is a trademark of Blizzard Entertainment.
