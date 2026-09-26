#!/bin/bash
# Rebuilds Hearthstone Access for macOS against the currently installed game.
#   ./rebuild.sh [hsa-release.zip]
# Default zip: the newest downloads/*.zip. Output goes to
# /Applications/Hearthstone/HearthstoneAccess (an extra folder next to the game;
# no file of the game itself is touched).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
GAME=/Applications/Hearthstone
MANAGED="$GAME/Hearthstone.app/Contents/Resources/Data/Managed"
DEST="$GAME/HearthstoneAccess"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ZIP="${1:-$(ls -t "$ROOT"/downloads/*.zip | head -1)}"
WORK="$ROOT/work"
echo "HSA zip: $ZIP"

command -v dotnet >/dev/null || { echo "missing .NET SDK in ~/.dotnet"; exit 1; }

echo "== build tools"
dotnet build -c Release -v q "$ROOT/tools/port" >/dev/null
dotnet build -c Release -v q "$ROOT/tolk" >/dev/null
dotnet build -c Release -v q "$ROOT/compat" >/dev/null
COMPAT="$ROOT/compat/bin/Release/net472/HSACompat.dll"
port() { dotnet "$ROOT/tools/port/bin/Release/net8.0/port.dll" "$@"; }  # quoted: paths may contain spaces
TOLK="$ROOT/tolk/bin/Release/net472/TolkDotNet.dll"
clang -dynamiclib -O2 -arch arm64 -arch x86_64 -o "$ROOT/loader/libhsaloader.dylib" "$ROOT/loader/hsaloader.c"
# speech bridge: Prism's macOS TTS backend (voiceover/prism, MPL-2.0)
clang -dynamiclib -fobjc-arc -O2 -arch arm64 -arch x86_64 -Wno-deprecated-declarations -framework Cocoa -framework AVFoundation -framework NaturalLanguage \
      -I"$ROOT/voiceover/prism" -L"$ROOT/voiceover/prism" -lprism -Wl,-rpath,@loader_path \
      -o "$ROOT/voiceover/libHSAVoiceOver.dylib" "$ROOT/voiceover/hsavoiceover.m"

echo "== unpack HSA"
rm -rf "$WORK/hsa" && mkdir -p "$WORK/hsa" "$WORK/out"
unzip -q "$ZIP" -d "$WORK/hsa"
W="$WORK/hsa/patch/Hearthstone_Data/Managed/Assembly-CSharp.dll"

echo "== transplant into the Mac Assembly-CSharp"
cp "$MANAGED/Assembly-CSharp.dll" "$WORK/vanilla-Assembly-CSharp.dll"   # snapshot, in case Battle.net patches mid-build

echo "== map HSA's source diff onto the IL"
DIFF="${ZIP%.zip}.diff.patch"   # DevTools diff.patch of the same HSA release
[ -f "$DIFF" ] || { echo "missing $DIFF"; exit 1; }
cp "$DIFF" "$WORK/diff.patch"
(cd "$WORK" && port hunks diff.patch hunks.json && port match "$W" "$WORK/vanilla-Assembly-CSharp.dll" hunks.json && port seeds hunk_matched_detail.txt hunk_edit_seeds.txt)
(cd "$WORK" && port port "$W" "$WORK/vanilla-Assembly-CSharp.dll" "$WORK/out/Assembly-CSharp.dll" "$MANAGED" > port.log 2>&1) || { tail -20 "$WORK/port.log"; exit 1; }
grep -E 'enum constants|existing types|closure|PROBLEM|missing external' "$WORK/port.log" || true

echo "== check references against the Mac assemblies"
base=$(port check "$WORK/vanilla-Assembly-CSharp.dll" "$MANAGED" | tail -1 | awk '{print $2}')
now=$(port check "$WORK/out/Assembly-CSharp.dll" "$MANAGED" "$(dirname "$TOLK")" "$(dirname "$COMPAT")" | tee "$WORK/check.log" | tail -1 | awk '{print $2}')
echo "unresolved: vanilla $base, ported $now"
[ "$now" -le "$base" ] || { echo "ported assembly has unresolved references, see $WORK/check.log"; exit 1; }

echo "== enhancements: Black Market, menus built from what the game shows (enhanced/)"
# optional: if this fails (e.g. a game update renamed something) the plain HSA port is installed
ENH=""
rm -rf "$WORK/enh" && mkdir -p "$WORK/enh"
cp "$WORK/out/Assembly-CSharp.dll" "$WORK/enh/Assembly-CSharp.dll"
if port expose "$WORK/enh/Assembly-CSharp.dll" "$MANAGED"    && dotnet build -c Release -v q "$ROOT/enhanced" -p:GameManaged="$MANAGED" -p:HsaAssembly="$WORK/enh/Assembly-CSharp.dll" -o "$WORK/enh/bin" > "$WORK/enhanced-build.log" 2>&1    && port hook "$WORK/enh/Assembly-CSharp.dll" "$WORK/enh/bin/HSAEnhanced.dll" "$WORK/enh/Assembly-CSharp.dll" "$MANAGED" \
        $( [ "$(cat "$ROOT/../menus" 2>/dev/null)" = hsa ] && echo --hsa-menus ); then
    mkdir -p "$WORK/enh/check" && cp "$WORK/enh/Assembly-CSharp.dll" "$WORK/enh/bin/HSAEnhanced.dll" "$WORK/enh/check/"
    enow=$(port check "$WORK/enh/check/Assembly-CSharp.dll" "$WORK/enh/check" "$MANAGED" "$(dirname "$TOLK")" "$(dirname "$COMPAT")" | tail -1 | awk '{print $2}')
    eadd=$(port check "$WORK/enh/check/HSAEnhanced.dll" "$WORK/enh/check" "$MANAGED" | tee "$WORK/enh/check.log" | tail -1 | awk '{print $2}')
    if [ "$enow" -le "$base" ] && [ "$eadd" -eq 0 ]; then ENH="$WORK/enh/bin/HSAEnhanced.dll"; cp "$WORK/enh/Assembly-CSharp.dll" "$WORK/out/Assembly-CSharp.dll"
    else echo "WARNING: enhancements have unresolved references (see $WORK/enh/check.log), installing plain Hearthstone Access"; fi
else
    echo "WARNING: enhancements could not be built (see $WORK/enhanced-build.log), installing plain Hearthstone Access"
fi

echo "== stage $DEST"
STAGE="$(mktemp -d "$GAME/.hsa-stage.XXXXXX")"
O="$STAGE/overlay"
mkdir -p "$O/Hearthstone.app/Contents/Resources/Data/Managed" "$O/Accessibility" "$O/Strings"
cp "$WORK/out/Assembly-CSharp.dll" "$TOLK" "$COMPAT" ${ENH:+"$ENH"} "$O/Hearthstone.app/Contents/Resources/Data/Managed/"
cp -R "$WORK/hsa/patch/Accessibility/Sounds" "$O/Accessibility/"
cp "$WORK/hsa/patch/Accessibility/hsa_manifest.json" "$O/Accessibility/" 2>/dev/null || true
for d in "$WORK"/hsa/patch/Strings/*/; do
    loc=$(basename "$d"); [ -f "$d/ACCESSIBILITY.txt" ] || continue
    mkdir -p "$O/Strings/$loc" && cp "$d/ACCESSIBILITY.txt" "$O/Strings/$loc/"
done
cp "$ROOT/loader/libhsaloader.dylib" "$ROOT/voiceover/libHSAVoiceOver.dylib" "$ROOT/voiceover/prism/libprism.dylib" \
   "$ROOT/voiceover/prism/LICENSE-prism-MPL-2.0.txt" "$STAGE/"
shasum -a 256 "$WORK/vanilla-Assembly-CSharp.dll" | awk '{print $1}' > "$STAGE/built_for.sha256"
basename "$ZIP" > "$STAGE/built_from.txt"
# swap in atomically; keep the previous build next to it
if [ -d "$DEST" ]; then rm -rf "$DEST.previous"; mv "$DEST" "$DEST.previous"; fi
mv "$STAGE" "$DEST"
chmod -R a+rX "$DEST"
rm -f "$ROOT/downloads/rebuild_pending"
echo "done: $(cat "$DEST/built_for.sha256") $(cat "$DEST/built_from.txt")"
