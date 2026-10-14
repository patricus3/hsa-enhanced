#!/bin/bash
# Hearthstone Access Enhanced watcher: keeps Battle.net running with the mod's
# loader injected, and rebuilds the mod after a game patch. Never touches a running game.
LOADER=/Applications/Hearthstone/HearthstoneAccess/libhsaloader.dylib
GAME_ASM=/Applications/Hearthstone/Hearthstone.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll
BUILT_FOR=/Applications/Hearthstone/HearthstoneAccess/built_for.sha256
H="$HOME/Library/Application Support/HearthstoneAccess"
REBUILD="$H/src/rebuild.sh"
LOG="$HOME/Library/Logs/HearthstoneAccess/watch.log"
mkdir -p "$(dirname "$LOG")"
log() { echo "[$(date '+%F %T')] $*" >> "$LOG"; }
attempts=0; window_start=$(date +%s); last_hash_check=0; last_restart=0

has_loader() { ps eww -p "$1" -o command= 2>/dev/null | grep -q "DYLD_INSERT_LIBRARIES=$LOADER"; }

restart_bnet() {
    log "Battle.net without HSA loader -> restarting it with the loader"
    osascript -e 'quit app "Battle.net"' >/dev/null 2>&1
    for i in $(seq 1 20); do pgrep -x Battle.net >/dev/null || break; sleep 1; done
    pkill -x Battle.net 2>/dev/null
    for i in $(seq 1 10); do pgrep -f 'Agent.app/Contents/MacOS/Agent' >/dev/null || break; sleep 1; done
    pkill -f 'Agent.app/Contents/MacOS/Agent' 2>/dev/null; sleep 1
    (cd / && nohup /usr/bin/env DYLD_INSERT_LIBRARIES="$LOADER" /Applications/Battle.net.app/Contents/MacOS/Battle.net >/dev/null 2>&1 &)
    sleep 15; fix_agents
}

# An Agent that outlived the old Battle.net keeps running without the loader;
# kill it and Battle.net starts a fresh one that inherits the loader.
fix_agents() {
    for a in $(pgrep -f 'Agent.app/Contents/MacOS/Agent'); do
        has_loader "$a" || { log "Agent $a without loader -> killing it"; kill "$a" 2>/dev/null; sleep 3; kill -9 "$a" 2>/dev/null; }
    done
}

check_release_update() {
    local now last_check current json latest_tag newer asset_url asset_digest expected_digest actual_digest offered tmp installer answer
    now=$(date +%s)
    last_check=$(cat "$H/release-check-epoch" 2>/dev/null || echo 0)
    [[ "$last_check" =~ ^[0-9]+$ ]] || last_check=0
    [ $((now - last_check)) -ge 86400 ] || return 1

    echo "$now" > "$H/release-check-epoch"
    json=$(curl -fsSL --max-time 15 -H 'User-Agent: HearthstoneAccessEnhanced-Updater' \
        https://api.github.com/repos/patricus3/hsa-enhanced/releases/latest) || { log "release check failed: GitHub API request failed"; return 1; }
    latest_tag=$(printf '%s' "$json" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("tag_name", ""))') || { log "release check failed: invalid release metadata"; return 1; }
    [[ "$latest_tag" =~ ^v?[0-9]+\.[0-9]+\.[0-9]+$ ]] || { log "release check failed: unexpected release tag $latest_tag"; return 1; }
    current=$(cat "$H/release-version.txt" 2>/dev/null || echo 0.0.0)
    newer=$(python3 - "$latest_tag" "$current" <<'PY'
import re, sys
def version(text):
    match = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)", text.strip())
    return tuple(map(int, match.groups())) if match else (0, 0, 0)
print("yes" if version(sys.argv[1]) > version(sys.argv[2]) else "no")
PY
)
    [ "$newer" = yes ] || return 1
    offered="$H/release-offered.txt"
    [ "$(cat "$offered" 2>/dev/null)" != "$latest_tag" ] || return 1
    answer=$(osascript -e "button returned of (display dialog \"Hearthstone Access Enhanced $latest_tag is available (you have $current). Install it now? Hearthstone is currently closed; the official update package will be downloaded from GitHub.\" buttons {\"Not now\", \"Install\"} default button \"Install\" cancel button \"Not now\" with icon note)" 2>>"$LOG") || { log "release update prompt was dismissed"; echo "$latest_tag" > "$offered"; return 1; }
    if [ "$answer" != Install ]; then echo "$latest_tag" > "$offered"; return 1; fi

    asset_url=$(printf '%s' "$json" | python3 -c 'import json,sys; r=json.load(sys.stdin); print(next((a["browser_download_url"] for a in r.get("assets",[]) if a.get("name") == "2.Hearthstone.access.enhanced.for.Mac.zip"), ""))') || { log "release update failed: invalid asset metadata"; return 1; }
    [ -n "$asset_url" ] || { log "release update failed: Mac package is missing from $latest_tag"; return 1; }
    asset_digest=$(printf '%s' "$json" | python3 -c 'import json,sys; r=json.load(sys.stdin); print(next((a.get("digest", "") for a in r.get("assets",[]) if a.get("name") == "2.Hearthstone.access.enhanced.for.Mac.zip"), ""))') || { log "release update failed: invalid checksum metadata"; return 1; }
    if [[ "$asset_digest" =~ ^sha256:([[:xdigit:]]{64})$ ]]; then expected_digest=${BASH_REMATCH[1]}; else log "release update failed: Mac package has no valid SHA-256 checksum"; return 1; fi
    tmp=$(mktemp -d "$H/update.XXXXXX") || { log "release update failed: could not create a staging directory"; return 1; }
    if ! curl -fL --max-time 180 -o "$tmp/update.zip" "$asset_url" >>"$LOG" 2>&1 || ! unzip -tq "$tmp/update.zip" >/dev/null 2>&1; then
        log "release update failed: download or archive verification failed"
        rm -rf "$tmp"
        return 1
    fi
    actual_digest=$(shasum -a 256 "$tmp/update.zip" | awk '{print $1}')
    actual_digest=$(printf '%s' "$actual_digest" | tr '[:upper:]' '[:lower:]')
    expected_digest=$(printf '%s' "$expected_digest" | tr '[:upper:]' '[:lower:]')
    if [ "$actual_digest" != "$expected_digest" ]; then
        log "release update failed: SHA-256 verification failed"
        rm -rf "$tmp"
        return 1
    fi
    unzip -q "$tmp/update.zip" -d "$tmp" || { log "release update failed: could not extract package"; rm -rf "$tmp"; return 1; }
    installer=$(find "$tmp" -type f -name 'Install Hearthstone access.command' -print -quit)
    [ -n "$installer" ] || { log "release update failed: installer is missing"; rm -rf "$tmp"; return 1; }
    chmod +x "$installer"
    echo "$latest_tag" > "$offered"
    touch "$H/update-installing"
    open "$installer" || { rm -f "$H/update-installing"; log "release update failed: could not open the installer"; return 1; }
    log "started the installer for release $latest_tag"
    return 0
}

log "watcher started"
while true; do
    sleep 2
    [ -f "$LOADER" ] || continue
    game=$(pgrep -x Hearthstone)
    now=$(date +%s)
    [ -f "$H/update-installing" ] && continue
    # rebuild after a game patch (only while the game is closed; check once a minute)
    if [ -z "$game" ] && [ $((now - last_hash_check)) -ge 60 ] && [ -x "$REBUILD" ]; then
        last_hash_check=$now
        if [ -f "$BUILT_FOR" ] && [ "$(shasum -a 256 "$GAME_ASM" 2>/dev/null | cut -d' ' -f1)" != "$(cat "$BUILT_FOR")" ]; then
            log "game Assembly-CSharp changed -> rebuilding the mod"
            if "$REBUILD" >> "$LOG" 2>&1; then log "rebuild ok"; else log "rebuild FAILED"; fi
        fi
    fi
    [ -n "$game" ] && continue                       # never disturb a running game
    check_release_update && exit 0
    bnet=$(pgrep -x Battle.net | head -1); [ -z "$bnet" ] && continue
    if has_loader "$bnet"; then fix_agents; continue; fi
    # give a freshly (re)started Battle.net 5 s to settle (its updates restart it)
    secs=$(ps -p "$bnet" -o etime= | tr -d ' ' | awk -F'[-:]' '{n=NF; s=$n+($(n-1))*60; if(n>2)s+=$(n-2)*3600; if(n>3)s+=$(n-3)*86400; print s}')
    [ "${secs:-0}" -lt 5 ] && continue
    # if it keeps coming back without the loader (e.g. Battle.net updating itself),
    # slow down to one attempt a minute instead of giving up
    [ $((now - window_start)) -gt 300 ] && { attempts=0; window_start=$now; }
    attempts=$((attempts+1))
    if [ $attempts -gt 6 ] && [ $((now - last_restart)) -lt 60 ]; then continue; fi
    last_restart=$now
    restart_bnet
done
