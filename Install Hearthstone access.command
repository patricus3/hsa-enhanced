#!/bin/bash
# Hearthstone Access for macOS installer (unofficial port, speech via the macOS
# synthesizer through Prism). Builds the mod on this Mac from your installed game
# and the official HSA release, installs a watcher that injects the mod whenever
# Battle.net starts, and restarts Battle.net. No game file is modified.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
H="$HOME/Library/Application Support/HearthstoneAccess"
SRC="$H/src"
AGENT="$HOME/Library/LaunchAgents/pl.hsa-mac.watch.plist"
LOADER=/Applications/Hearthstone/HearthstoneAccess/libhsaloader.dylib
say() { echo; echo "== $*"; }
fail() { echo; echo "ERROR: $*"; echo "Installation aborted. Press Enter to close."; read -r _; exit 1; }

say "Hearthstone Access for Mac - installation"
# --use-hsa-menus: Hearthstone Access's own menus instead of the enhanced menu system
mkdir -p "$H"; MENUS=enhanced
for a in "$@"; do [ "$a" = "--use-hsa-menus" ] && MENUS=hsa; done
echo "$MENUS" > "$H/menus"; echo "menus: $MENUS"
[ -d /Applications/Hearthstone/Hearthstone.app ] || fail "Hearthstone not found in /Applications/Hearthstone. Install it with Battle.net first."
[ -d /Applications/Battle.net.app ] || fail "Battle.net not found in the Applications folder."
pgrep -x Hearthstone >/dev/null && fail "Hearthstone is running. Quit the game and run the installer again."

say "Checking tools"
xcode-select -p >/dev/null 2>&1 || { xcode-select --install >/dev/null 2>&1; fail "Xcode Command Line Tools are missing. Their installer window has opened; when it finishes, run this installer again."; }
command -v python3 >/dev/null || fail "python3 is missing (it comes with the Command Line Tools)."
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if ! dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
    say "Installing the .NET 8 SDK from Microsoft into ~/.dotnet (about 200 MB, this takes a while)"
    curl -sSL -o /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh || fail "Could not download the .NET installer."
    bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet" >/dev/null || fail ".NET installation failed."
fi

say "Copying mod files"
mkdir -p "$SRC/downloads"
FILES=""
for d in Resources zrodla; do [ -f "$HERE/$d/rebuild.sh" ] && { FILES="$HERE/$d"; break; }; done
[ -n "$FILES" ] || fail "The mod files folder (Resources) was not found next to the installer."
rsync -a --delete --exclude downloads --exclude work "$FILES/" "$SRC/" || fail "Could not copy the files."
chmod +x "$SRC/rebuild.sh" "$SRC/hsa-watch.sh" "$SRC/update-hsa.sh"

say "Downloading Hearthstone Access from hearthstoneaccess.com and its source diff from GitHub"
"$SRC/update-hsa.sh"; [ $? -eq 2 ] && fail "Could not get a matching Hearthstone Access release and source diff. Try again later, once the HSA developers have published both."

say "Building the mod for your game version (about a minute)"
"$SRC/rebuild.sh" "$SRC/downloads/hsa.zip" || fail "Building the mod failed. Details are above."

say "Installing the watcher that enables the mod whenever Battle.net starts"
mkdir -p "$HOME/Library/LaunchAgents" "$HOME/Library/Logs/HearthstoneAccess"
cp "$SRC/hsa-watch.sh" "$H/hsa-watch.sh"; chmod +x "$H/hsa-watch.sh"
cat > "$AGENT" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>pl.hsa-mac.watch</string>
    <key>ProgramArguments</key><array><string>/bin/bash</string><string>$H/hsa-watch.sh</string></array>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>EnvironmentVariables</key><dict><key>PATH</key><string>/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin</string></dict>
</dict>
</plist>
EOF
launchctl bootout "gui/$(id -u)/pl.hsa-mac.watch" 2>/dev/null
launchctl bootstrap "gui/$(id -u)" "$AGENT" || fail "Could not start the watcher."

say "Starting Battle.net with the mod"
if pgrep -x Battle.net >/dev/null; then
    osascript -e 'quit app "Battle.net"' >/dev/null 2>&1
    for i in $(seq 1 20); do pgrep -x Battle.net >/dev/null || break; sleep 1; done
    pkill -f 'Agent.app/Contents/MacOS/Agent' 2>/dev/null; sleep 2
fi
(cd / && nohup /usr/bin/env DYLD_INSERT_LIBRARIES="$LOADER" /Applications/Battle.net.app/Contents/MacOS/Battle.net >/dev/null 2>&1 &)
sleep 15
for a in $(pgrep -f 'Agent.app/Contents/MacOS/Agent'); do
    ps eww -p "$a" -o command= | grep -q "DYLD_INSERT_LIBRARIES=$LOADER" || { kill "$a" 2>/dev/null; sleep 3; kill -9 "$a" 2>/dev/null; }
done

echo
echo "DONE. Hearthstone Access is installed permanently."
echo "Start the game from Battle.net as usual (Play). The mod turns on by itself and speaks with your macOS system voice."
echo "After a game update the mod rebuilds itself while the game is closed."
echo "To remove the mod, use Uninstall Hearthstone access.command."
echo "Press Enter to close this window."
read -r _
