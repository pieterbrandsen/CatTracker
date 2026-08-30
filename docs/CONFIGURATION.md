# Configuration

Every setting, what it does, and when you would change it.

## Where settings come from

Later sources win:

1. `appsettings.json` — shipped defaults, inside the install directory.
2. `appsettings.Development.json` — only when `ASPNETCORE_ENVIRONMENT=Development`.
3. **`<DataDirectory>/config.local.json`** — yours. Lives with your data, not your binaries, so an
   update can never overwrite it. This is the file to edit on the Mac.
4. Environment variables prefixed `CATTRACKER_`, using `__` for nesting.

```bash
# equivalent to {"CatTracker": {"FindMy": {"PollSeconds": 5}}}
CATTRACKER_CatTracker__FindMy__PollSeconds=5
```

A minimal `config.local.json`:

```json
{
  "CatTracker": {
    "TimeZone": "Europe/Amsterdam",
    "Alerts": { "SoundName": "Sosumi", "IMessageTo": "+31600000000" },
    "Geofence": { "ConfirmationFixes": 3 },
    "Tiles": { "AllowNetwork": false }
  }
}
```

Restart the app afterwards: `launchctl kickstart -k gui/$UID/nl.brandsen.cattracker.app`.

---

## `CatTracker` — top level

| Key | Default | What it does |
|---|---|---|
| `DataDirectory` | `~/Library/Application Support/CatTracker` on macOS; `./.data` elsewhere | Database, logs, spool, tiles and this config file. Set it via the launch agent, not here. |
| `TimeZone` | system zone | IANA (`Europe/Amsterdam`) or Windows id. Only affects how days are cut for daily statistics and the rhythm chart. An unrecognised value silently falls back to the system zone. |

## `CatTracker:FindMy` — the position source

| Key | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Runs the background poll loop. Off in tests. |
| `Source` | `Spool` | `Spool` (normal — read what `cattracker-reader` copied), `Direct` (read the Find My cache from this process; needs Full Disk Access for the *app*, useful only for spiking), `Replay` (synthetic cat, no Apple hardware). |
| `SpoolDirectory` | `<DataDirectory>/spool` | Where the reader writes `items.json` and `heartbeat.json`. |
| `DirectPath` | `~/Library/Caches/com.apple.findmy.fmipcore/Items.data` | Only used when `Source` is `Direct`. |
| `PollSeconds` | `10` | How often to check the source. Cheap — it compares mtime and size and does nothing when unchanged. |
| `StaleAfterMinutes` | `45` | How long without a new position before raising a `DataStale` alert. **Tune this to the cadence your Phase 0 spike measured.** Too low and you get nagged whenever she naps somewhere quiet; too high and a dead reader goes unnoticed for hours. |
| `KeepRawSnapshots` | `200` | Raw cache payloads kept for debugging. `0` disables. |

## `CatTracker:Geofence` — deciding when she has actually left

These three settings are what stand between you and a 3am false alarm. A fix located by a
stranger's phone two streets away routinely reports 150 m accuracy, which would otherwise place a
sleeping cat outside the garden.

| Key | Default | What it does |
|---|---|---|
| `MaxAccuracyMeters` | `100` | Fixes vaguer than this are **stored and drawn** but get no vote on where she is. Lower it if you get false alarms; raise it if genuine departures are missed. |
| `ConfirmationFixes` | `2` | Consecutive qualifying fixes required before a transition is believed. `3` is a good move if your area gives noisy fixes. |
| `RejectOldFixes` | `true` | Ignore fixes Find My has already flagged as stale. |

The fourth defence is per-zone: `exitBufferM` (see Setup in the UI). Leaving requires
distance > `radiusM + exitBufferM`; returning requires distance ≤ `radiusM`. That dead band is
what stops the boundary oscillating all night.

## `CatTracker:Alerts`

| Key | Default | What it does |
|---|---|---|
| `MacNotifications` | `true` | Native notification banners. macOS only. |
| `Sound` | `true` | Plays a system sound — the one that actually wakes you. |
| `SoundName` | `Submarine` | Any name from `/System/Library/Sounds`. |
| `IMessageTo` | *(empty)* | Phone number or Apple ID. Empty disables it. **The only channel that leaves the LAN**, and it goes through your own Apple account rather than a third-party push service. Requires granting the app Automation access to Messages on first use. |
| `CooldownMinutes` | `60` | Default minimum gap between deliveries of the same alert key. Alerts are always *recorded*; only delivery is rate limited. |
| `LowBatteryAtOrAbove` | `3` | Find My's `batteryStatus` integer is undocumented, so CatTracker alerts on any **change** (always meaningful) and calls it low at or above this value. Confirm the real mapping during the spike. |

## `CatTracker:Tiles` — the offline map

| Key | Default | What it does |
|---|---|---|
| `AllowNetwork` | `true` | When `false`, only tiles already on disk are served — genuinely offline. Turn this off once you have seeded your neighbourhood. |
| `UpstreamUrl` | `https://tile.openstreetmap.org/{z}/{x}/{y}.png` | Tile source. |
| `UserAgent` | `CatTracker/1.0 (...)` | OSM's tile policy requires an identifying User-Agent. Do not send a browser's. |
| `MaxSeedTiles` | `20000` | Hard cap on one seeding run, so a stray bounding box cannot hammer OSM. A truncated run says so in its completion message. |
| `SeedRatePerSecond` | `4` | Upstream request rate while seeding. |

A neighbourhood at z14–z18 is a few thousand tiles and a reasonable ask. A province is not.

## `CatTracker:Diagnostics` — logging

| Key | Default | What it does |
|---|---|---|
| `RetainedDays` | `30` | Rolling log files to keep. Roughly 1–5 MB a day at `Information`. |
| `FileSizeLimitMb` | `32` | A single file rolls at this size, so one bad day cannot fill the disk. |
| `Console` | `true` | Also write to stdout; launchd captures that into `logs/app.out.log`. |

## `CatTracker:Replay` — the synthetic cat

Only used when `FindMy:Source` is `Replay`.

| Key | Default | What it does |
|---|---|---|
| `SeedDays` | `14` | Days of synthetic history generated on first run, through the real processing pipeline. `0` disables. |
| `HomeLat` / `HomeLon` | Utrecht | Where the fake cat lives. |
| `Seed` | `1712` | Random seed. Same seed, same cat. |
| `PetName` | `Demo Cat` | |

---

## `Serilog` — log levels

Not under `CatTracker`; this is Serilog's own section.

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning",
        "CatTracker": "Debug"
      }
    }
  }
}
```

Setting `CatTracker` to `Debug` gives you every poll, every geofence decision with its distance
and verdict, and every rejected fix with the reason. That is the log that explains a false alarm.

> **Every key under `Override` is treated as a logger name.** Do not put comment keys like `"//"`
> in there — the app will refuse to start.

`Microsoft.EntityFrameworkCore` is pinned to `Warning` deliberately: EF logs every SQL statement at
`Information`, and seeding alone would write about 50,000 lines.

---

## Server binding

`urls` is a top-level key, not under `CatTracker`:

| Value | Effect |
|---|---|
| `http://0.0.0.0:5185` | Default. Reachable from your phone on the LAN. |
| `http://127.0.0.1:5185` | This machine only. |

There is no authentication. That is a deliberate choice for a trusted home LAN — **do not
port-forward it**. If you need it from outside, put it behind a VPN or Tailscale rather than
opening a port.
