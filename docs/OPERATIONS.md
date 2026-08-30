# Operations

Running CatTracker day to day, on either platform.

**Installing is not here.** Pick your platform:
[Windows](SETUP-WINDOWS.md) · [macOS](SETUP-MACOS.md). Each is complete on its own and neither
needs the other machine.

---

## Updating

The same command that installed it. Both are idempotent — run them as often as you like.

```powershell
./setup/windows/install.ps1          # elevated PowerShell
```

```bash
./setup/macos/install.sh
```

What that guarantees, on both platforms:

- **Your data is never touched.** Database, logs, settings and cached tiles live outside the
  install directory by design; an update replaces binaries and nothing else.
- **Schema changes apply themselves.** EF Core migrations run on start, so a schema change ships
  with the build that needs it. There is no separate migration step to remember.
- **On macOS, Full Disk Access survives.** The installer only replaces the reader binary when its
  contents actually changed, and says so loudly when it does.

Rolling back is the same motion: check out the older revision and run the installer again. The
database is forward-compatible within a schema version; if you roll back *across* a migration,
restore the matching database backup too.

---

## Where everything lives

| | Windows | macOS |
|---|---|---|
| Binaries | `C:\Program Files\CatTracker\app\` | `~/Applications/CatTracker/app/` |
| Reader | — (macOS only) | `~/Applications/CatTracker/reader/` |
| Database | `C:\ProgramData\CatTracker\cattracker.db` | `~/Library/Application Support/CatTracker/cattracker.db` |
| Map tiles | `…\tiles.db` | `…/tiles.db` |
| Logs | `…\logs\` | `…/logs/` |
| Your settings | `…\config.local.json` | `…/config.local.json` |
| Service | `CatTracker` (Windows Service) | two LaunchAgents |

---

## Health, and the failure mode that matters

Open the **Health** page. It is the point of the whole thing: the machine running this has no
screen you will ever look at, so every check it does is readable from your phone, including a live
log tail.

It reports the collector, the reader agent, position freshness, the home zone, alert channels,
schema version and storage.

**Silence is the dangerous state.** A dead reader, a quit Find My, a sleeping machine and a cat
asleep on the sofa all look identical on a map. That is why:

- staleness detection is a first-class feature, not polish;
- the `DataStale` alert tries to name the actual cause rather than saying "no data";
- the reader writes a heartbeat even when nothing changed, so "the reader is fine and Find My is
  idle" can be told apart from "the reader is dead".

Tune `FindMy:StaleAfterMinutes` to the cadence your spike measured. Too low and it nags whenever
she naps somewhere quiet; too high and a dead reader goes unnoticed for hours.

---

## Backups

The database is one file. Snapshot it consistently while the app is running:

```bash
sqlite3 <data>/cattracker.db "VACUUM INTO '/path/to/backups/cattracker-$(date +%F).db'"
```

```powershell
sqlite3 "$env:ProgramData\CatTracker\cattracker.db" `
  "VACUUM INTO 'D:\Backups\cattracker-$(Get-Date -f yyyy-MM-dd).db'"
```

A nightly scheduled task, cron job, or a line in your existing backup script is plenty.
`tiles.db` does not need backing up — it is a rebuildable cache.

---

## Turning verbosity up

Log levels are configuration, not a rebuild. Create or edit `config.local.json` in the data
directory:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": { "CatTracker": "Debug" }
    }
  }
}
```

Then restart the service or agent. At `Debug` you get every poll, every geofence decision with its
distance and verdict, and every rejected fix with the reason — the log that answers both "why did
it not tell me she had gone out?" and the more common "why did it wake me at 3am when she was on
the sofa?".

Turn it back down afterwards: `Debug` writes roughly ten times as much.

> Every key under `Override` is treated as a logger name. Do not put comment keys like `"//"` in
> there — the app will refuse to start.

Full settings reference: [CONFIGURATION.md](CONFIGURATION.md).

---

## Tuning the geofence

If you get false alarms, in order of preference:

1. **Widen the zone.** `radiusM` should cover your house and garden; `exitBufferM` is the dead band
   she must cross before counting as out. Both are on the Setup page.
2. **Raise `Geofence:ConfirmationFixes`** from 2 to 3. Costs you a few minutes of alert latency and
   removes most remaining noise.
3. **Lower `Geofence:MaxAccuracyMeters`** from 100. Rejected fixes are still stored and drawn on the
   map — they simply get no vote on where she is.

If genuine departures are *missed*, move the same dials the other way. The History page tells you
which problem you have: greyed markers are the fixes the geofence declined to trust.

---

## Housekeeping

Nothing needs doing routinely. For reference:

- **Logs** roll daily, cap at 32 MB each, and keep 30 days. Both configurable.
- **Raw cache snapshots** (a debugging aid) are pruned to the newest 200. Set
  `FindMy:KeepRawSnapshots` to 0 to disable them.
- **Fixes are kept forever.** At roughly one a minute that is ~500k rows a year, which SQLite does
  not notice.
- **Tiles are kept forever.** Delete `tiles.db` to reclaim the space; the map re-fetches what you
  look at, or you re-seed.

---

## Uninstalling

```powershell
./setup/windows/uninstall.ps1            # add -Purge to delete the data too
```

```bash
./setup/macos/uninstall.sh               # add --purge to delete the data too
```

On macOS, also remove `cattracker-reader` from Full Disk Access by hand.
