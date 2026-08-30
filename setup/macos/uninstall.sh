#!/usr/bin/env bash
#
# Removes CatTracker's agents and binaries. Your data is left alone unless you ask otherwise.
#
#   ./uninstall.sh              # stop and remove the agents and binaries
#   ./uninstall.sh --purge      # also delete the database, logs and settings

set -euo pipefail

INSTALL_ROOT="${CATTRACKER_HOME:-$HOME/Applications/CatTracker}"
DATA_DIR="${CATTRACKER_DATA:-$HOME/Library/Application Support/CatTracker}"
AGENT_DIR="$HOME/Library/LaunchAgents"

for label in nl.brandsen.cattracker.app nl.brandsen.cattracker.reader; do
    launchctl bootout "gui/$UID/$label" 2>/dev/null || true
    rm -f "$AGENT_DIR/$label.plist"
    echo "  removed $label"
done

rm -rf "$INSTALL_ROOT"
echo "  removed $INSTALL_ROOT"

if [[ "${1:-}" == "--purge" ]]; then
    rm -rf "$DATA_DIR"
    echo "  removed $DATA_DIR (database, logs and settings)"
else
    echo
    echo "  Your data is still at: $DATA_DIR"
    echo "  Delete it with: ./uninstall.sh --purge"
fi

echo
echo "  One thing left for you: remove cattracker-reader from"
echo "  System Settings → Privacy & Security → Full Disk Access."
