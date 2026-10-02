#!/bin/bash
# Hearthstone Access watcher: keeps Battle.net running with the HSA loader
# injected, and rebuilds the mod after a game patch. Never touches a running game.
LOADER=/Applications/Hearthstone/HearthstoneAccess/libhsaloader.dylib
GAME_ASM=/Applications/Hearthstone/Hearthstone.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll
BUILT_FOR=/Applications/Hearthstone/HearthstoneAccess/built_for.sha256
REBUILD="$HOME/Library/Application Support/HearthstoneAccess/src/rebuild.sh"
UPDATE="$HOME/Library/Application Support/HearthstoneAccess/src/update-hsa.sh"
PENDING="$HOME/Library/Application Support/HearthstoneAccess/src/downloads/rebuild_pending"
LOG="$HOME/Library/Logs/HearthstoneAccess/watch.log"
mkdir -p "$(dirname "$LOG")"
log() { echo "[$(date '+%F %T')] $*" >> "$LOG"; }
attempts=0; window_start=$(date +%s); last_hash_check=0; last_restart=0; last_hsa_check=0

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

log "watcher started"
while true; do
    sleep 2
    [ -f "$LOADER" ] || continue
    game=$(pgrep -x Hearthstone)
    now=$(date +%s)
    # a new Hearthstone Access release: look once a day
    # (not when installed --without-hsa)
    if [ $((now - last_hsa_check)) -ge 86400 ] && [ -x "$UPDATE" ] && [ "$(cat "$HOME/Library/Application Support/HearthstoneAccess/mode" 2>/dev/null)" != without-hsa ]; then
        last_hsa_check=$now
        "$UPDATE" >> "$LOG" 2>&1 && log "new Hearthstone Access release downloaded"
    fi
    # rebuild after a game patch or a new HSA release (only while the game is closed; check once a minute)
    if [ -z "$game" ] && [ $((now - last_hash_check)) -ge 60 ] && [ -x "$REBUILD" ]; then
        last_hash_check=$now
        if [ -f "$BUILT_FOR" ] && [ "$(shasum -a 256 "$GAME_ASM" 2>/dev/null | cut -d' ' -f1)" != "$(cat "$BUILT_FOR")" ]; then
            log "game Assembly-CSharp changed -> rebuilding HSA"
            if "$REBUILD" >> "$LOG" 2>&1; then log "rebuild ok"; else log "rebuild FAILED"; fi
        elif [ -f "$PENDING" ]; then
            log "new Hearthstone Access release -> rebuilding HSA"
            if "$REBUILD" >> "$LOG" 2>&1; then log "rebuild ok"; else log "rebuild FAILED"; fi
        fi
    fi
    [ -n "$game" ] && continue                       # never disturb a running game
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
