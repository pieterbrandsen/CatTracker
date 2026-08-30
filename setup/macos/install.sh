#!/usr/bin/env bash
#
# CatTracker — complete macOS setup. Run this on the Mac.
#
# It works two ways, and figures out which on its own:
#
#   • From a repo checkout        — builds from source with the .NET SDK installed here.
#     ./setup/macos/install.sh      This is the standalone Mac setup; no other machine involved.
#
#   • From an unpacked release    — uses the prebuilt binaries sitting next to it.
#     ./install.sh                  This is what deploy.ps1 from a Windows box lands on.
#
# The same command installs and updates. It is idempotent: run it as often as you like.
#
# The one thing it deliberately cannot do is grant Full Disk Access — macOS does not allow that
# from a script. It tells you exactly when that is needed, and only the tiny reader binary needs it.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_ROOT="${CATTRACKER_HOME:-$HOME/Applications/CatTracker}"
APP_DIR="$INSTALL_ROOT/app"
READER_DIR="$INSTALL_ROOT/reader"
DATA_DIR="${CATTRACKER_DATA:-$HOME/Library/Application Support/CatTracker}"
SPOOL_DIR="$DATA_DIR/spool"
LOG_DIR="$DATA_DIR/logs"
AGENT_DIR="$HOME/Library/LaunchAgents"

APP_LABEL="nl.brandsen.cattracker.app"
READER_LABEL="nl.brandsen.cattracker.reader"
PORT="${CATTRACKER_PORT:-5185}"

bold() { printf '\033[1m%s\033[0m\n' "$*"; }
info() { printf '  %s\n' "$*"; }
warn() { printf '\033[33m  ! %s\033[0m\n' "$*"; }
fail() { printf '\033[31m  x %s\033[0m\n' "$*"; exit 1; }
ok()   { printf '\033[32m  ✓ %s\033[0m\n' "$*"; }

[[ "$(uname -s)" == "Darwin" ]] || fail "This is the macOS setup. On Windows run setup\\windows\\install.ps1."

bold "CatTracker — macOS setup"

# ---- 0. find or build the binaries ------------------------------------------------------------

if [[ -x "$SCRIPT_DIR/app/cattracker" ]]; then
    STAGE="$SCRIPT_DIR"
    info "using the prebuilt release in $STAGE"

elif [[ -d "$SCRIPT_DIR/../../src/CatTracker.App" ]]; then
    REPO="$(cd "$SCRIPT_DIR/../.." && pwd)"
    command -v dotnet >/dev/null 2>&1 || fail \
        "The .NET SDK is not installed. Get it with 'brew install --cask dotnet-sdk', or from https://dot.net."

    case "$(uname -m)" in
        arm64) RID="osx-arm64" ;;
        *)     RID="osx-x64" ;;
    esac

    STAGE="$REPO/out/cattracker-$RID"
    bold "0. Building from source ($RID)"
    info "this takes a minute or two the first time"

    rm -rf "$STAGE"
    dotnet publish "$REPO/src/CatTracker.App" -c Release -r "$RID" --self-contained true \
        -o "$STAGE/app" --nologo >/dev/null
    dotnet publish "$REPO/src/CatTracker.Reader" -c Release -r "$RID" --self-contained true \
        -o "$STAGE/reader" --nologo >/dev/null
    ok "built into $STAGE"
    echo
else
    fail "Cannot find binaries to install, and no source tree to build from."
fi

[[ -x "$STAGE/app/cattracker" ]] || fail "app/cattracker is missing."
[[ -x "$STAGE/reader/cattracker-reader" ]] || fail "reader/cattracker-reader is missing."

info "install : $INSTALL_ROOT"
info "data    : $DATA_DIR"
echo

mkdir -p "$APP_DIR" "$READER_DIR" "$DATA_DIR" "$SPOOL_DIR" "$LOG_DIR" "$AGENT_DIR"

# ---- 1. stop anything already running ---------------------------------------------------------

bold "1. Stopping running agents"
for label in "$APP_LABEL" "$READER_LABEL"; do
    if launchctl print "gui/$UID/$label" >/dev/null 2>&1; then
        launchctl bootout "gui/$UID/$label" 2>/dev/null || true
        info "stopped $label"
    fi
done
ok "done"
echo

# ---- 2. the reader, only if it actually changed ------------------------------------------------

# Full Disk Access is granted per binary, and replacing the binary revokes it. So the reader is
# only touched when its contents differ — an ordinary app update leaves the grant intact.
bold "2. Reader (the only component needing Full Disk Access)"

reader_changed=1
if [[ -f "$READER_DIR/cattracker-reader" ]]; then
    old_hash="$(shasum -a 256 "$READER_DIR/cattracker-reader" | cut -d' ' -f1)"
    new_hash="$(shasum -a 256 "$STAGE/reader/cattracker-reader" | cut -d' ' -f1)"
    [[ "$old_hash" == "$new_hash" ]] && reader_changed=0
fi

if [[ "$reader_changed" -eq 1 ]]; then
    cp "$STAGE/reader/cattracker-reader" "$READER_DIR/cattracker-reader"
    chmod +x "$READER_DIR/cattracker-reader"

    # A binary cross-published from Windows arrives unsigned and quarantined, and Apple silicon
    # refuses to execute unsigned code outright. The ad-hoc signature is what makes it runnable.
    xattr -dr com.apple.quarantine "$READER_DIR/cattracker-reader" 2>/dev/null || true
    codesign --force --sign - "$READER_DIR/cattracker-reader" >/dev/null 2>&1 || \
        warn "codesign failed; the reader may refuse to start."

    warn "The reader binary changed, so its Full Disk Access grant was reset."
    NEEDS_FDA=1
else
    ok "unchanged — Full Disk Access is preserved"
    NEEDS_FDA=0
fi
echo

# ---- 3. the app ---------------------------------------------------------------------------------

bold "3. Application"
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR"
cp -R "$STAGE/app/." "$APP_DIR/"
chmod +x "$APP_DIR/cattracker"
xattr -dr com.apple.quarantine "$APP_DIR" 2>/dev/null || true
codesign --force --sign - "$APP_DIR/cattracker" >/dev/null 2>&1 || \
    warn "codesign failed; the app may refuse to start."
ok "installed to $APP_DIR"
echo

# ---- 4. launch agents ---------------------------------------------------------------------------

bold "4. Launch agents"
render() {
    sed -e "s|__APP__|$APP_DIR|g" \
        -e "s|__READER__|$READER_DIR/cattracker-reader|g" \
        -e "s|__DATA__|$DATA_DIR|g" \
        -e "s|__SPOOL__|$SPOOL_DIR|g" \
        -e "s|__LOGS__|$LOG_DIR|g" \
        "$1" > "$2"
}

render "$SCRIPT_DIR/$READER_LABEL.plist" "$AGENT_DIR/$READER_LABEL.plist"
render "$SCRIPT_DIR/$APP_LABEL.plist" "$AGENT_DIR/$APP_LABEL.plist"

launchctl bootstrap "gui/$UID" "$AGENT_DIR/$READER_LABEL.plist"
launchctl bootstrap "gui/$UID" "$AGENT_DIR/$APP_LABEL.plist"
ok "loaded and started"
echo

# ---- 5. verify -----------------------------------------------------------------------------------

bold "5. Checking it came up"
health=""
for _ in $(seq 1 30); do
    if health="$(curl -fsS "http://127.0.0.1:$PORT/api/health" 2>/dev/null)"; then break; fi
    sleep 1
done

if [[ -n "$health" ]]; then
    ok "API is up: $health"

    # Check the UI separately. A wrong content root leaves the API answering happily while every
    # page and stylesheet 404s — worth catching here rather than on your phone.
    if curl -fsS "http://127.0.0.1:$PORT/" >/dev/null 2>&1; then
        ok "Web UI is being served."
    else
        warn "The API is up but the web UI is not being served. Check $LOG_DIR/app.err.log."
    fi
else
    warn "The API did not answer on port $PORT within 30s."
    warn "Look at: $LOG_DIR/app.err.log"
fi

sleep 3
if [[ -f "$SPOOL_DIR/heartbeat.json" ]]; then
    status="$(sed -n 's/.*"status":"\([a-z_]*\)".*/\1/p' "$SPOOL_DIR/heartbeat.json")"
    case "$status" in
        ok)                ok "Reader is reading the Find My cache." ;;
        permission_denied) NEEDS_FDA=1; warn "Reader cannot read the cache: Full Disk Access is missing." ;;
        not_found)         warn "The Find My cache file does not exist. Has the Find My app ever run on this account?" ;;
        *)                 warn "Reader status: $status" ;;
    esac
else
    NEEDS_FDA=1
    warn "No heartbeat from the reader yet."
fi
echo

# ---- 6. what is left for a human -------------------------------------------------------------------

if [[ "$NEEDS_FDA" -eq 1 ]]; then
    bold "ACTION REQUIRED — grant Full Disk Access"
    info "1. The Privacy pane is opening now."
    info "2. Add this binary (⌘⇧G to paste the path):"
    info ""
    info "     $READER_DIR/cattracker-reader"
    info ""
    info "3. Then run:  launchctl kickstart -k gui/$UID/$READER_LABEL"
    open "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles" 2>/dev/null || true
    echo
fi

bold "Also make sure that"
info "• the Find My app is running and in Login Items — if it quits, the cache goes stale;"
info "• the Mac never sleeps (System Settings → Lock Screen / Energy Saver)."
echo

bold "CatTracker is at"
info "  http://$(scutil --get LocalHostName 2>/dev/null || hostname).local:$PORT"
info "  http://127.0.0.1:$PORT"
echo
info "Logs:      $LOG_DIR"
info "Data:      $DATA_DIR"
info "Settings:  $DATA_DIR/config.local.json  (optional; never overwritten by an update)"
echo
info "Restart:   launchctl kickstart -k gui/$UID/$APP_LABEL"
info "Uninstall: $SCRIPT_DIR/uninstall.sh"
