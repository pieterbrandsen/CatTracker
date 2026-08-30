#!/usr/bin/env bash
#
# Phase 0 — the feasibility spike. Run this on the Mac BEFORE trusting anything else here.
#
#   ./spike.sh          # inspect the cache once
#   ./spike.sh 60       # then watch it for 60 minutes and report the real update cadence
#
# It answers the four questions the whole project rests on:
#
#   1. Does the Find My cache exist and parse on YOUR macOS version?
#   2. What are the ACTUAL field names? (The parser accepts several spellings, but confirm.)
#   3. How often does the timestamp genuinely advance — indoors, and outdoors?
#   4. What does horizontalAccuracy look like in practice at your address?
#
# If this fails, stop: every later phase is built on it, and the fallback (a DIY OpenHaystack
# tag whose keys you own) is a different project with different hardware.

set -uo pipefail

CACHE="${1:-$HOME/Library/Caches/com.apple.findmy.fmipcore/Items.data}"
[[ "$CACHE" =~ ^[0-9]+$ ]] && { WATCH_MINUTES="$CACHE"; CACHE="$HOME/Library/Caches/com.apple.findmy.fmipcore/Items.data"; }
WATCH_MINUTES="${WATCH_MINUTES:-${2:-0}}"

bold() { printf '\033[1m%s\033[0m\n' "$*"; }
warn() { printf '\033[33m  ! %s\033[0m\n' "$*"; }
ok()   { printf '\033[32m  ✓ %s\033[0m\n' "$*"; }

bold "CatTracker — Phase 0 spike"
echo "  cache: $CACHE"
echo

# ---- 1. does it exist and can we read it? ----------------------------------------------------

bold "1. Cache file"
if [[ ! -f "$CACHE" ]]; then
    # "Not found" and "not allowed to look" are indistinguishable from a [[ -f ]] test, because
    # TCC makes a protected path fail the same way an absent one does. Ask the directory itself,
    # which reports the difference.
    probe="$(ls -ld "$(dirname "$CACHE")" 2>&1 >/dev/null)"

    if [[ "$probe" == *"Operation not permitted"* ]]; then
        warn "Blocked by macOS privacy protection — not actually missing."
        echo
        echo "     Whichever terminal app you are running this in needs Full Disk Access:"
        echo "       System Settings → Privacy & Security → Full Disk Access → +"
        echo
        echo "     Add the app you are typing into RIGHT NOW — Terminal, iTerm, VS Code, Warp —"
        echo "     the grant is per app. Then QUIT IT COMPLETELY (Cmd-Q) and reopen: the"
        echo "     permission is only picked up by a freshly launched process."
        echo
        echo "     Opening that pane for you now."
        open "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles" 2>/dev/null
        exit 1
    fi

    warn "Genuinely not present at the expected path."
    echo "     Searching for where Find My keeps it on this macOS version..."
    echo

    found=0
    while IFS= read -r hit; do
        [[ -n "$hit" ]] || continue
        found=1
        echo "       $hit  ($(stat -f%z "$hit" 2>/dev/null) bytes)"
    done < <(find "$HOME/Library" -iname "Items.data" -maxdepth 6 2>/dev/null)

    if [[ "$found" -eq 1 ]]; then
        echo
        echo "     Re-run against one of those:  $0 <path> [minutes]"
    else
        echo "       nothing found."
        echo
        echo "     Check: is the AirTag actually paired to THIS Apple ID, and has the Find My"
        echo "     app been opened and left to refresh on this machine (not just on your phone)?"
        echo "     If it never appears, this approach does not work on your macOS version and"
        echo "     the fallback is a DIY OpenHaystack tag — different hardware, different project."
    fi

    echo
    echo "     macOS $(sw_vers -productVersion 2>/dev/null)"
    exit 1
fi

if ! head -c 1 "$CACHE" >/dev/null 2>&1; then
    warn "Exists but cannot be read — Full Disk Access is missing."
    echo "     System Settings → Privacy & Security → Full Disk Access → add your terminal app,"
    echo "     then quit it completely (Cmd-Q) and reopen."
    open "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles" 2>/dev/null
    exit 1
fi

ok "readable, $(stat -f%z "$CACHE") bytes, modified $(stat -f%Sm "$CACHE")"
echo

# ---- 2. structure -----------------------------------------------------------------------------

bold "2. Structure (confirm these field names against CatTracker.Core/FindMyParser.cs)"
python3 - "$CACHE" <<'PY'
import json, sys

with open(sys.argv[1], encoding="utf-8") as handle:
    data = json.load(handle)

items = data if isinstance(data, list) else data.get("items", [])
print(f"  root is a {type(data).__name__} holding {len(items)} item(s)")

for item in items:
    name = item.get("name", "?")
    serial = item.get("serialNumber", "?")
    battery = item.get("batteryStatus")
    print(f"\n  --- {name}  (serial {serial}, batteryStatus {battery})")
    print(f"      item keys    : {sorted(item.keys())}")

    location = item.get("location") or item.get("crowdSourcedLocation")
    if not location:
        print("      location     : none yet (nobody has walked past it)")
        continue

    print(f"      location keys: {sorted(location.keys())}")
    for key in ("latitude", "longitude", "timeStamp", "horizontalAccuracy",
                "isOld", "isInaccurate", "positionType"):
        print(f"        {key:<20} = {location.get(key)!r}")
PY
echo

# ---- 3. cadence -------------------------------------------------------------------------------

if [[ "$WATCH_MINUTES" -gt 0 ]]; then
    bold "3. Watching for $WATCH_MINUTES minutes"
    echo "  Keep the Find My app open. Every change to the file is one line below."
    echo "  This is the number that decides what the rest of the project can honestly claim."
    echo

    last_mtime=""
    last_change=$(date +%s)
    changes=0
    deadline=$(( $(date +%s) + WATCH_MINUTES * 60 ))

    while [[ $(date +%s) -lt $deadline ]]; do
        mtime="$(stat -f%m "$CACHE" 2>/dev/null || echo 0)"
        if [[ "$mtime" != "$last_mtime" ]]; then
            now=$(date +%s)
            gap=$(( now - last_change ))
            [[ -n "$last_mtime" ]] && printf '  %s  changed after %4ds\n' "$(date +%H:%M:%S)" "$gap"
            last_mtime="$mtime"
            last_change=$now
            changes=$(( changes + 1 ))
        fi
        sleep 5
    done

    echo
    ok "$changes change(s) in $WATCH_MINUTES minutes"
    echo "     Roughly one update every $(( WATCH_MINUTES * 60 / (changes > 0 ? changes : 1) ))s."
    echo "     Expect this to be far worse when she is two streets away."
else
    bold "3. Cadence"
    echo "  Skipped. Run './spike.sh 60' with the AirTag on the cat to measure it."
fi

echo
bold "Next"
echo "  • Copy a redacted cache into samples/items-sample.json for the replay tests."
echo "  • Correct CatTracker.Core/FindMyParser.cs if any field name above differs."
echo "  • Then deploy: see docs/OPERATIONS.md."
