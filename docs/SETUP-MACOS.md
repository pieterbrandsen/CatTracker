# Setup — macOS

A complete, self-contained macOS install. Nothing here needs a Windows machine.

This is the setup that tracks a **real AirTag**, because the Mac is the only place the Find My
cache exists.

---

## 0. The spike — do this first

The whole system rests on an undocumented cache file. Confirm it exists, parses, and actually
updates on *your* macOS version before installing anything.

```bash
./setup/macos/spike.sh          # inspect once
./setup/macos/spike.sh 60       # then watch for an hour, with the AirTag on the cat
```

It answers four questions:

1. Does the cache exist and parse?
2. What are the **actual** field names? (The parser accepts several spellings — confirm yours.)
3. How often does the timestamp genuinely advance, indoors and outdoors?
4. What does `horizontalAccuracy` look like at your address?

The cadence from question 3 is the number that decides what the rest of the project can honestly
claim, and it is what you should set `FindMy:StaleAfterMinutes` from. Write it down.

If the spike fails, **stop**. The fallback is a DIY OpenHaystack tag whose keys you own — a
different project with different hardware.

> The spike needs Terminal to have Full Disk Access
> (System Settings → Privacy & Security → Full Disk Access → add Terminal).

---

## 1. Install

You need the .NET 10 SDK on the Mac:

```bash
brew install --cask dotnet-sdk
```

Then, from a checkout of this repo:

```bash
./setup/macos/install.sh
```

That single command builds both binaries from source, installs them, registers two launch agents,
starts them, and verifies the API answers. **The same command updates it later** — it is
idempotent, so just `git pull` and run it again.

No SDK on the Mac? Build a release elsewhere with `./publish.ps1`, copy the tarball over, unpack
it, and run the `install.sh` inside — it uses the prebuilt binaries next to it. Or drive the whole
thing from a Windows box with `./deploy.ps1 -MacHost mac.local -User you`.

### What it does, and why

1. Stops both launch agents.
2. Copies the reader **only if its contents changed** — this is what preserves Full Disk Access.
3. Replaces the app directory wholesale.
4. Ad-hoc signs both binaries and strips the quarantine flag. A binary built elsewhere arrives
   unsigned, and Apple silicon refuses to execute unsigned code at all.
5. Renders and loads the launch agents.
6. Waits for `/api/health`, reads the reader's heartbeat, and tells you what is still wrong.

---

## 2. The three things only you can do

The installer detects all three and tells you which are outstanding.

### Grant Full Disk Access to the reader

macOS will not let a script do this.

1. System Settings → Privacy & Security → **Full Disk Access**
2. `+`, then ⌘⇧G and paste:
   ```
   ~/Applications/CatTracker/reader/cattracker-reader
   ```
3. Restart the agent:
   ```bash
   launchctl kickstart -k gui/$UID/nl.brandsen.cattracker.reader
   ```

**Once**, not on every update. That is the entire reason the privileged reader is a separate,
frozen binary: ordinary updates leave it untouched and the grant intact. The installer says so
explicitly on the rare occasion the reader does change.

### Keep Find My running

The cache only refreshes while the Find My app is running. Add it to Login Items
(System Settings → General → Login Items) and do not quit it.

### Stop the Mac sleeping

System Settings → Lock Screen: turning the *display* off is fine. What matters is that the machine
does not sleep — Energy Saver → *Prevent automatic sleeping when the display is off*. A sleeping
Mac collects nothing.

---

## 3. Where things go

| | |
|---|---|
| App binaries | `~/Applications/CatTracker/app/` — replaced on every update |
| Reader binary | `~/Applications/CatTracker/reader/` — replaced only when it changes |
| Database | `~/Library/Application Support/CatTracker/cattracker.db` |
| Map tiles | `~/Library/Application Support/CatTracker/tiles.db` |
| Spool | `~/Library/Application Support/CatTracker/spool/` |
| Logs | `~/Library/Application Support/CatTracker/logs/` |
| Your settings | `~/Library/Application Support/CatTracker/config.local.json` |
| Launch agents | `~/Library/LaunchAgents/nl.brandsen.cattracker.*.plist` |

Data lives outside the install directory deliberately: an update replaces binaries and can never
touch your history or your settings.

---

## 4. First run in the app

Open `http://<your-mac>.local:5185` on your phone and add it to the Home Screen.

1. **Setup → Zones.** Click the map on your house and save a `Home` zone. Start with a 30 m radius
   and a 25 m exit buffer. Without a Home zone there are no excursions, no away alerts and no
   statistics.
2. **Setup → Send test alert.** Confirms notifications actually work, so silence later means "all
   is well" rather than "it is broken".
3. **Setup → Seed this view.** Downloads the visible map area once so the map works with no
   internet. Keep it small — OpenStreetMap serves these for free.
4. **Setup → Cat.** Give her a proper name.

Then leave it for a few days before tuning anything. You will make far better decisions about zone
sizes and staleness thresholds once you can see what the real signal looks like at your address.

---

## 5. Running it

```bash
# Status
launchctl print gui/$UID/nl.brandsen.cattracker.app | head -20

# Restart
launchctl kickstart -k gui/$UID/nl.brandsen.cattracker.app
launchctl kickstart -k gui/$UID/nl.brandsen.cattracker.reader

# Follow the log
tail -f ~/Library/Application\ Support/CatTracker/logs/cattracker-*.log

# Is the reader alive and happy?
cat ~/Library/Application\ Support/CatTracker/spool/heartbeat.json
```

The **Health** page shows all of this from your phone, including a live log tail. That is the
point of it — the Mac has no screen you are ever going to look at.

### Backups

One file, snapshotted consistently while running:

```bash
sqlite3 ~/Library/Application\ Support/CatTracker/cattracker.db \
  "VACUUM INTO '/Volumes/Backup/cattracker-$(date +%F).db'"
```

`tiles.db` does not need backing up — it is a rebuildable cache.

---

## 6. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| "No heartbeat from cattracker-reader" | Reader agent not running | `launchctl kickstart -k gui/$UID/nl.brandsen.cattracker.reader`, then check `logs/reader.err.log` |
| Reader status `permission_denied` | Full Disk Access missing or reset | Re-add the reader binary (section 2) |
| Reader status `not_found` | Find My has never populated the cache | Open Find My and wait for it to refresh |
| "Reader is healthy, so Find My has stopped refreshing" | Find My quit, or the Mac slept | Reopen Find My; check sleep settings |
| App will not start, log says "killed" | Unsigned binary on Apple silicon | Re-run `./install.sh` — it ad-hoc signs both |
| Positions stop overnight | Mac is sleeping | Energy Saver → prevent automatic sleeping |
| Constant leave/return alerts | Home zone too small for your accuracy | Raise `radiusM` and `exitBufferM`; set `Geofence:ConfirmationFixes` to 3 |
| No alerts at all | Zone notifications off, or no channels | Setup → **Send test alert** names the live channels |
| Unreachable from the phone | macOS firewall | System Settings → Network → Firewall → allow `cattracker` |
| Everything looks fine but she has not moved for hours | She may genuinely be asleep — **or** you are blind | Health page: freshness versus reader status distinguishes them |

That last row is the one that matters, and it is why staleness detection is a first-class feature
rather than polish. A dead reader, a quit Find My, a sleeping Mac and a sleeping cat all look
identical on a map.

More detail, including turning log verbosity up: [OPERATIONS.md](OPERATIONS.md).

---

## 7. Uninstalling

```bash
./setup/macos/uninstall.sh            # agents and binaries; data kept
./setup/macos/uninstall.sh --purge    # everything
```

Then remove `cattracker-reader` from Full Disk Access by hand.
