Hearthstone Access Enhanced (unofficial)
========================================

Unofficial build of Hearthstone Access (https://hearthstoneaccess.com), the mod that makes Hearthstone playable for blind players, for Windows and macOS, with speech through Prism (https://github.com/ethindp/prism) and menus built from what the game shows.

This project is not affiliated with Blizzard Entertainment or with the Hearthstone Access developers. Use it at your own risk.


What it adds to Hearthstone Access
----------------------------------

- Black Market: items with price, stock and haggle state, timers, your Bloodstones; buy (with quantity) and haggle through the game's own popup.
- Darkmoon Faire Treasures (lucky draw): the draw with its price, the rewards and which you own.
- Menus from the game: the main menu lists every button the box shows (Shop, Black Market, lucky draw, set rotation, ...); Game Modes and Adventures list every mode and adventure the game has; every Hearthstone Access menu gets the buttons the game shows around it, read through the game's widgets (labels from their text, data models and tooltips; presses through the widget's own events).
- Adventure books (Descent of Dragons, Galakrond, ...): chapters and missions with locked / not owned / completed states; locked missions say why instead of failing on the server.
- New year of Hearthstone (set rotation): the "Year of ..." intro Hearthstone Access turns off is back, as sighted players get it: the box's rotation button, every banner of the clock (Standard, the new year, the button banner) read out with Enter to continue, the format picker with its texts, the rotated sets and the switch-format step.
- Death Knight runes: the deck editor lists the deck's three rune slots with their runes; Enter on one moves to the next rune (Empty, Blood, Frost, Unholy), as a click does.
- Options: Signature Card Appearance (and Language where the game offers it) through Hearthstone Access's own dropdown reader; Miscellaneous and Privacy, and their menus, read from the game's buttons.
- Credits: each year's credits by section, roles with their names, the year buttons, and every credits card as it appears.
- A way back everywhere: menus without one get the game's own back navigation; a back that does nothing is followed up; as a last resort Back returns to the main menu.
- Screens Hearthstone Access does not know get a menu of what they show.
- Fixes: HSA changes the transplant used to lose (operator-only edits, early returns) are carried over, which fixes e.g. adventure mission screens and the new-year set rotation that kept the Shop, Black Market and lucky draw hidden; a second "find game" request is no longer sent; a hand card is picked up only once the pointer is on it, so the card next to it is no longer played while the hand moves after a draw or discover.

--use-hsa-menus (installer option) keeps Hearthstone Access's own menus; the other additions stay.


Download
--------

Ready-to-use packages are in release/. There are two versions:

- Hearthstone Access Enhanced: 1 Hearthstone access enhanced for Windows.zip, 2 Hearthstone access enhanced for Mac.zip, source in 3 Hearthstone access enhanced sourcecode.zip. Both packages contain the same Resources; only the installer differs.
- Hearthstone Access for Mac (plain port, the mac-port branch): 4 Hearthstone access for Mac (port).zip, source in 5 Hearthstone access for Mac (port) sourcecode.zip. Hearthstone Access exactly as on Windows, without the enhancements, speech through the macOS system voice.

Both Mac versions install into the same place; running one installer replaces the other.


Windows
-------

Requirements: Hearthstone installed with Battle.net, an internet connection during installation. The .NET 8 SDK is downloaded if missing.

1. Quit Hearthstone.
2. Run Install Hearthstone access.bat (it asks for administrator rights; the game lives in Program Files). For Hearthstone Access's own menus: "Install Hearthstone access.bat" --use-hsa-menus.
3. When it says DONE, start the game from Battle.net.

Speech goes to your screen reader (NVDA, JAWS and others Prism supports), or to the Windows voices when none is running; it follows a screen reader started or closed while playing. Tolk is not used.

A scheduled task rebuilds the mod after a game update and when a new Hearthstone Access release comes out (checked daily), while the game is closed. Uninstall Hearthstone access.bat removes everything and puts the game's own files back. Logs: C:\ProgramData\HearthstoneAccess\logs.


macOS
-----

Requirements: a Mac with Apple silicon (Intel is untested), Hearthstone in /Applications/Hearthstone, Xcode Command Line Tools (the installer asks for them), .NET 8 SDK (downloaded into ~/.dotnet).

1. Quit Hearthstone.
2. Open Install Hearthstone access.command (or bash "Install Hearthstone access.command" [--use-hsa-menus] in Terminal).
3. When it says DONE, start the game from Battle.net.

Speech uses the macOS system voice and its settings (Spoken Content), per language. A watcher rebuilds the mod after a game update or a new Hearthstone Access release. Uninstall Hearthstone access.command removes it. Logs: ~/Library/Logs/HearthstoneAccess.


How it works
------------

No Hearthstone Access or Blizzard code is kept in this repository: the installer downloads the official Hearthstone Access release and its source diff from the DevTools (https://github.com/antonshusharin/DevTools) repository on the player's machine and builds the mod there.

- Resources/tools/port: a Mono.Cecil tool that transplants the methods Hearthstone Access changed into the installed game's Assembly-CSharp.dll (matched from the source diff), checks every reference against the game's own assemblies, and hooks the additions in.
- Resources/enhanced: the additions (menus, Black Market, lucky draw, adventure books, fallback menus), compiled against the game's own class library. If a game update breaks them, the plain Hearthstone Access build is installed instead.
- Windows: Resources/windows (install, rebuild and uninstall scripts; speech is the Prism bridge that replaces Tolk; prism holds prism.dll).
- macOS: a loader (Resources/loader) serves the mod's files to the game without modifying it; Resources/tolk + Resources/voiceover speak through Prism's AVSpeech backend; Resources/hsa-watch.sh keeps Battle.net running with the loader.


Credits
-------

- Hearthstone Access: Guide Dev and the Hearthstone Access community developers.
- Prism by Ethin Probst, MPL-2.0 (Resources/windows/prism/LICENSES, Resources/voiceover/prism/LICENSE-prism-MPL-2.0.txt); its third-party notices are in Resources/windows/prism/NOTICE.
- Hearthstone is a trademark of Blizzard Entertainment.
