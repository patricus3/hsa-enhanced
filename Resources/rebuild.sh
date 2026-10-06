#!/bin/bash
# Rebuilds Hearthstone Access Enhanced for macOS against the currently installed game.
#   ./rebuild.sh
# Output goes to /Applications/Hearthstone/HearthstoneAccess (an extra folder next to the game;
# no file of the game itself is touched).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
GAME=/Applications/Hearthstone
MANAGED="$GAME/Hearthstone.app/Contents/Resources/Data/Managed"
DEST="$GAME/HearthstoneAccess"
WORK="$ROOT/work"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

command -v dotnet >/dev/null || { echo "missing .NET SDK in ~/.dotnet"; exit 1; }

echo "== build tools"
dotnet build -c Release -v q "$ROOT/tools/port" >/dev/null
dotnet build -c Release -v q "$ROOT/tolk" >/dev/null
port() { dotnet "$ROOT/tools/port/bin/Release/net8.0/port.dll" "$@"; }  # quoted: paths may contain spaces
TOLK="$ROOT/tolk/bin/Release/net472/TolkDotNet.dll"
clang -dynamiclib -O2 -arch arm64 -arch x86_64 -o "$ROOT/loader/libhsaloader.dylib" "$ROOT/loader/hsaloader.c"
# speech bridge: Prism's macOS TTS backend (voiceover/prism, MPL-2.0)
clang -dynamiclib -fobjc-arc -O2 -arch arm64 -arch x86_64 -Wno-deprecated-declarations -framework Cocoa -framework AVFoundation -framework NaturalLanguage \
      -I"$ROOT/voiceover/prism" -L"$ROOT/voiceover/prism" -lprism -Wl,-rpath,@loader_path \
      -o "$ROOT/voiceover/libHSAVoiceOver.dylib" "$ROOT/voiceover/hsavoiceover.m"

echo "== building the mod"
rm -rf "$WORK" && mkdir -p "$WORK/out" "$WORK/enh/check"
cp "$MANAGED/Assembly-CSharp.dll" "$WORK/vanilla-Assembly-CSharp.dll"   # snapshot, in case Battle.net patches mid-build
cp "$WORK/vanilla-Assembly-CSharp.dll" "$WORK/out/Assembly-CSharp.dll"
base=$(port check "$WORK/vanilla-Assembly-CSharp.dll" "$MANAGED" | tail -1 | awk '{print $2}')
dotnet build -c Release -v q "$ROOT/enhanced" -p:GameManaged="$MANAGED" -p:GameAssembly="$WORK/vanilla-Assembly-CSharp.dll" -o "$WORK/enh/bin" > "$WORK/enhanced-build.log" 2>&1 \
    || { tail -20 "$WORK/enhanced-build.log"; echo "the mod could not be built"; exit 1; }
ENH="$WORK/enh/bin/HSAEnhanced.dll"
port hook "$WORK/out/Assembly-CSharp.dll" "$ENH" "$WORK/out/Assembly-CSharp.dll" "$MANAGED" --without-hsa
cp "$WORK/out/Assembly-CSharp.dll" "$ENH" "$WORK/enh/check/"
enow=$(port check "$WORK/enh/check/Assembly-CSharp.dll" "$WORK/enh/check" "$MANAGED" "$(dirname "$TOLK")" | tail -1 | awk '{print $2}')
eadd=$(port check "$WORK/enh/check/HSAEnhanced.dll" "$WORK/enh/check" "$MANAGED" | tee "$WORK/enh/check.log" | tail -1 | awk '{print $2}')
echo "unresolved: game $base, hooked $enow, mod $eadd"
{ [ "$enow" -le "$base" ] && [ "$eadd" -eq 0 ]; } || { echo "the build has unresolved references, see $WORK/enh/check.log"; exit 1; }

echo "== stage $DEST"
STAGE="$(mktemp -d "$GAME/.hsa-stage.XXXXXX")"
O="$STAGE/overlay"
mkdir -p "$O/Hearthstone.app/Contents/Resources/Data/Managed" "$O/Strings" "$O/Accessibility"   # the loader links Accessibility/ into the game
cp "$WORK/out/Assembly-CSharp.dll" "$TOLK" "$ENH" "$O/Hearthstone.app/Contents/Resources/Data/Managed/"
# Hearthstone Access's texts, which come with the mod: the mod reads them
for d in "$ROOT"/strings/*/; do
    loc=$(basename "$d"); [ -f "$d/ACCESSIBILITY.txt" ] || continue
    mkdir -p "$O/Strings/$loc" && cp "$d/ACCESSIBILITY.txt" "$O/Strings/$loc/"
done
cp "$ROOT/loader/libhsaloader.dylib" "$ROOT/voiceover/libHSAVoiceOver.dylib" "$ROOT/voiceover/prism/libprism.dylib" \
   "$ROOT/voiceover/prism/LICENSE-prism-MPL-2.0.txt" "$STAGE/"
shasum -a 256 "$WORK/vanilla-Assembly-CSharp.dll" | awk '{print $1}' > "$STAGE/built_for.sha256"
# swap in atomically; keep the previous build next to it
if [ -d "$DEST" ]; then rm -rf "$DEST.previous"; mv "$DEST" "$DEST.previous"; fi
mv "$STAGE" "$DEST"
chmod -R a+rX "$DEST"
echo "done: built for $(cat "$DEST/built_for.sha256")"
