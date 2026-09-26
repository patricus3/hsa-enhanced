#!/bin/bash
# Fetches the Hearthstone Access release (hearthstoneaccess.com) and its source diff
# (DevTools on GitHub) into downloads/. Nothing of HSA is kept in this repository; the build
# takes what it needs from these two files on the player's Mac.
#   exit 0: a new release pair was stored (a rebuild is due; downloads/rebuild_pending is set)
#   exit 1: nothing new, or the new pair was unusable and the last good one is kept
#   exit 2: failed and there is no usable pair at all
ROOT="$(cd "$(dirname "$0")" && pwd)"
D="$ROOT/downloads"
mkdir -p "$D"
have_pair() { [ -f "$D/hsa.zip" ] && [ -f "$D/hsa.diff.patch" ]; }
keep_old() { echo "HSA update: $1"; rm -f "$D/hsa.zip.new" "$D/hsa.diff.patch.new"; if have_pair; then echo "keeping the release already downloaded"; exit 1; fi; exit 2; }

curl -fsSL -o "$D/hsa.zip.new" https://hearthstoneaccess.com/files/pre_patch.zip || keep_old "could not download the release"
curl -fsSL -o "$D/hsa.diff.patch.new" https://raw.githubusercontent.com/antonshusharin/DevTools/master/diff.patch || keep_old "could not download diff.patch"
zipver=$(unzip -p "$D/hsa.zip.new" patch/Accessibility/hsa_manifest.json 2>/dev/null | python3 -c 'import json,sys;print(json.load(sys.stdin)["accessibility_version"])' 2>/dev/null)
gitver=$(curl -fsSL https://raw.githubusercontent.com/antonshusharin/DevTools/master/hsa_version | tr -d '[:space:]')
echo "HSA version in the release: ${zipver:-?}, in the repository: ${gitver:-?}"
# right after an HSA update one of the two may lag behind: wait for both
[ -n "$zipver" ] && [ "$zipver" = "$gitver" ] || keep_old "the release and its source diff do not match yet"

new=$(cat "$D/hsa.zip.new" "$D/hsa.diff.patch.new" | shasum -a 256 | awk '{print $1}')
if have_pair && [ "$new" = "$(cat "$D/hsa.sha256" 2>/dev/null)" ]; then
    rm -f "$D/hsa.zip.new" "$D/hsa.diff.patch.new"; echo "HSA is up to date ($zipver)"; exit 1
fi
mv "$D/hsa.zip.new" "$D/hsa.zip"
mv "$D/hsa.diff.patch.new" "$D/hsa.diff.patch"
echo "$new" > "$D/hsa.sha256"
touch "$D/rebuild_pending"
echo "HSA $zipver downloaded"
exit 0
