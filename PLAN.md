# CatTracker — Design Plan

A fully local cat-tracking system built on a real AirTag, an always-on Mac, and a self-hosted
.NET app. No cloud services, no hosted website, no third-party accounts beyond the Apple ID you
already use.

> **Status: built.** Phases 1–5 are implemented and tested (239 tests, 94% line coverage); Phase 0
> is the spike you run on the Mac when it arrives. Start at [README.md](README.md);
> install with [docs/SETUP-MACOS.md](docs/SETUP-MACOS.md) or
> [docs/SETUP-WINDOWS.md](docs/SETUP-WINDOWS.md).
>
> Five things changed while building, each noted in place below: **.NET 10** rather than 9,
> **EF Core** rather than Dapper, a **separate privileged reader binary** (§2), **Serilog with an
> in-app log viewer** (§6), and **two standalone setups** rather than one that spans both machines
> (§8).

---

## 1. The one hard constraint

**Apple publishes no API for AirTags.** There is no SDK, no REST endpoint, no MFi programme, and
no supported way for third-party software to ask "where is my AirTag?". Location for a genuine
AirTag exists in exactly one place: inside the Find My app on a device signed into the owning
Apple ID.

That leaves a single viable extraction route for a real AirTag:

> The macOS Find My app maintains a **decrypted local cache** of every tracked item on disk.
> A local process with Full Disk Access can read that file.

```
~/Library/Caches/com.apple.findmy.fmipcore/Items.data      # AirTags & Find My accessories
~/Library/Caches/com.apple.findmy.fmipcore/Devices.data    # iPhones, Macs, AirPods
```

`Items.data` is plain JSON, rewritten by Find My each time it refreshes. We poll it. We never
touch Apple's network, never handle credentials, never sign in to anything — Find My does all of
that, and we read its output.

### Consequences of that choice — accept these before writing code

| Consequence | Impact |
|---|---|
| Find My.app **must be running** on the Mac | If it's quit, the cache goes stale silently. Needs a keep-alive + staleness detector. |
| The Mac must **not sleep** | Energy Saver → prevent sleep, or run `caffeinate`. A sleeping Mac collects nothing. |
| **Full Disk Access** required | Granted in System Settings → Privacy & Security, to the collector binary itself. Re-granting is needed after every rebuild that changes the binary path. |
| Apple can change the format | It's an undocumented cache. A macOS update could rename fields or encrypt it. The design must degrade loudly, not silently. |
| Fixes arrive when they arrive | See below. |

### AirTags have no GPS

This is the single most important thing to internalise before designing the features. An AirTag
reports **the location of whichever stranger's iPhone last walked past it**. Practical effects:

- Update cadence is **wildly irregular** — seconds apart on a busy street, *hours* apart in a
  quiet garden at 3am.
- Accuracy varies from ~5 m (your own phone nearby) to 100–200 m (a passing phone with a poor fix).
- When the cat is home, fixes come from your own iPhone/Mac and are excellent. When the cat is two
  streets away, they may be sparse and vague.
- Every derived statistic ("time outdoors") is an **estimate over a sparse, unevenly-sampled
  signal**. The UI must show gaps as gaps, never interpolate silently.

### Other things you'll hit in the real world

- **Anti-stalking:** an AirTag separated from you for a while starts **beeping**, and nearby
  iPhones show "AirTag Found Moving With You". Expect neighbours to get alerts if your cat visits
  them. This is unavoidable and by design — you cannot disable it.
- **Weight & collar:** ~11 g plus holder. Use a **breakaway/safety collar** — a snagged cat on a
  fence is a far bigger risk than a lost AirTag.
- **Battery:** CR2032, roughly a year. `batteryStatus` is in the cache; alert on low.
- If reliable outdoor tracking matters more than this being a fun project, a GPS/LTE cat collar
  beats an AirTag outright. Noting it once — the rest of this plan assumes AirTag, as asked.

---

## 2. Architecture

The real deployment is one Mac. Windows is a first-class *second* deployment — a Windows Service
running the replay source — for developing and evaluating without Apple hardware; it cannot read a
real AirTag, because the Find My cache does not exist there. Neither setup depends on the other
machine.

### The privileged-reader split

> **Decided during the build, and it shapes everything else.** macOS grants Full Disk Access *per
> binary*, and replacing a binary revokes the grant. If the main app read the cache directly, every
> single update would mean another trip to System Settings.
>
> So the privileged part is a separate ~15 MB single-file binary, `cattracker-reader`, that does
> nothing but copy the cache to a spool directory and write a heartbeat. It is deliberately frozen:
> no parsing, no dependencies, nothing that could force it to change. You grant Full Disk Access
> once; the installer only replaces it when its contents genuinely differ.
>
> It is also plain least privilege — the component that can read your entire disk copies one file
> and exits. And the heartbeat it writes is what lets the app tell "the reader is dead" apart from
> "Find My has stopped refreshing", which is the diagnosis that matters most when everything goes
> quiet.

```
┌───────────────────────── macOS (always on, signed in) ────────────────────────┐
│                                                                               │
│   Find My.app ──writes──► Items.data (JSON cache)                             │
│                                │                                              │
│                   ┌────────────▼───────────────┐  LaunchAgent                 │
│                   │  cattracker-reader         │  HAS Full Disk Access        │
│                   │  - copies the cache        │  single file, ~15 MB, frozen │
│                   │  - writes heartbeat.json   │  polls every 15s             │
│                   └────────────┬───────────────┘                              │
│                                │ spool/items.json                             │
│                   ┌────────────▼───────────────┐  LaunchAgent                 │
│                   │  cattracker                │  needs NO permissions        │
│                   │  - IFindMyReader           │  collector (IHostedService)  │
│                   │  - dedupe by timestamp     │  + minimal API + web UI      │
│                   │  - geofence evaluation     │                              │
│                   │  - alert dispatch          │                              │
│                   └────────────┬───────────────┘                              │
│                                ▼                                              │
│           cattracker.db (SQLite, WAL) · tiles.db · logs/                       │
└────────────────────────────────┼──────────────────────────────────────────────┘
                                 │ http://mac.local:5185  (LAN only)
                    ┌────────────┴────────────┐
                    │  Your iPhone / laptop   │  browser, add to Home Screen
                    └─────────────────────────┘
```

The collector runs inside the web process as a hosted service rather than as a third binary. One
process to install, one to restart, one to update — and with `KeepAlive` a restart is about a
second. WAL mode still keeps HTTP reads out of the collector's way.

### Stack

| Layer | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (LTS) | Already in the ecosystem; self-contained, so the Mac needs no runtime. |
| Collector | `BackgroundService` inside the web host | One binary, one launch agent, one thing to update. |
| Web | ASP.NET Core Minimal API | Tiny, one file of endpoints, no MVC ceremony. |
| Data | SQLite + **EF Core 10** | Real migrations matter when the app updates itself on a machine you rarely touch: a schema change ships with the build that needs it and is applied on start. |
| Front end | Static HTML + vendored Leaflet + hand-rolled SVG charts | No build step, no npm, no CDN at runtime. Charts are ~80 lines of SVG rather than a 200 KB dependency. |
| Logging | **Serilog**, rolling daily files | Verbosity becomes a config edit on the Mac, not a rebuild on Windows. |
| Scheduling | launchd `LaunchAgent` | The macOS-native way; survives login, restarts on crash. |

### Solution layout

```
CatTracker.slnx
├── src/
│   ├── CatTracker.Core/          # domain: parser, geofence engine, statistics — no I/O
│   ├── CatTracker.Data/          # EF Core context, migrations, repository
│   ├── CatTracker.App/           # collector + minimal API + wwwroot SPA
│   └── CatTracker.Reader/        # the small privileged file-copier
├── tests/
│   └── CatTracker.Tests/         # 236 tests: unit, EF against temp databases, HTTP integration
├── samples/
│   └── items-sample.json         # cache fixture — replace with a redacted real one after Phase 0
├── setup/
│   ├── macos/                    # install.sh · uninstall.sh · spike.sh · the two plists
│   └── windows/                  # install.ps1 · uninstall.ps1 (Windows Service)
├── docs/                         # SETUP-MACOS · SETUP-WINDOWS · OPERATIONS · CONFIGURATION · API
├── publish.ps1 · deploy.ps1 · run-local.ps1 · coverage.ps1
└── PLAN.md · README.md
```

### The key abstraction

Because you develop on Windows but the data only exists on macOS:

```csharp
public interface IFindMyReader
{
    string Description { get; }

    // Returns null when nothing has changed since the last read.
    Task<FindMySnapshot?> TryReadAsync(CancellationToken cancellationToken);

    // Health of the upstream reader, when there is one.
    Task<ReaderHeartbeat?> ReadHeartbeatAsync(CancellationToken cancellationToken);
}
```

Three implementations, chosen by `FindMy:Source` in config — never by `#if OSX`:

- **`Spool`** — production. Reads what `cattracker-reader` copied, plus its heartbeat.
- **`Direct`** — reads the Find My cache from this process. Needs Full Disk Access for the app
  itself, so it is only really useful while spiking.
- **`Replay`** — a synthetic cat. Reproduces the properties that make the real signal awkward:
  irregular intervals, accuracy that degrades sharply once she leaves the garden, occasional
  multi-hour blackouts, and the odd fix Find My has already flagged as junk. On first run it
  backfills a fortnight of history *through the real processing pipeline*, so excursions, the
  timeline and every chart are exercised end to end on Windows with no Apple hardware.

Code that looks right against a tidy synthetic track and falls over on the real thing has learned
nothing — which is why the simulator models the awkwardness rather than a neat walk.

---

## 3. Data model

All timestamps stored **UTC**, as Unix milliseconds, matching the cache.

> **As built:** this schema is created by EF Core migrations (`src/CatTracker.Data/Migrations`)
> from the plain domain types in `CatTracker.Core`, mapped with fluent configuration so the domain
> project stays free of any persistence dependency. `dotnet dotnet-ef migrations add …` for a
> change; they are applied on start, which is what keeps updating to "swap the binaries".
> Map tiles live in a **separate** `tiles.db` — a seeded neighbourhood is hundreds of megabytes of
> PNGs, and nobody wants that inside the nightly backup of a cat's location history.

```sql
CREATE TABLE Tags (
    Id            INTEGER PRIMARY KEY,
    SerialNumber  TEXT NOT NULL UNIQUE,   -- stable identity from the cache
    FindMyName    TEXT NOT NULL,          -- e.g. "Kat"
    PetName       TEXT NOT NULL,
    IsActive      INTEGER NOT NULL DEFAULT 1,
    CreatedUtc    INTEGER NOT NULL
);

CREATE TABLE Fixes (
    Id                 INTEGER PRIMARY KEY,
    TagId              INTEGER NOT NULL REFERENCES Tags(Id),
    TimestampUtc       INTEGER NOT NULL,  -- from location.timeStamp
    Latitude           REAL NOT NULL,
    Longitude          REAL NOT NULL,
    HorizontalAccuracy REAL,              -- metres
    Altitude           REAL,
    PositionType       TEXT,              -- "crowdsourced" | "safeLocation" | ...
    IsOld              INTEGER NOT NULL DEFAULT 0,
    IsInaccurate       INTEGER NOT NULL DEFAULT 0,
    BatteryStatus      INTEGER,
    IngestedUtc        INTEGER NOT NULL,
    UNIQUE (TagId, TimestampUtc)          -- the dedupe guarantee
);
CREATE INDEX IX_Fixes_Tag_Time ON Fixes (TagId, TimestampUtc DESC);

CREATE TABLE Zones (
    Id          INTEGER PRIMARY KEY,
    Name        TEXT NOT NULL,
    Kind        TEXT NOT NULL,            -- Home | Watch | Hazard
    CenterLat   REAL NOT NULL,
    CenterLon   REAL NOT NULL,
    RadiusM     REAL NOT NULL,
    ExitBufferM REAL NOT NULL DEFAULT 25  -- hysteresis, see §4
);

CREATE TABLE ZoneEvents (
    Id          INTEGER PRIMARY KEY,
    TagId       INTEGER NOT NULL REFERENCES Tags(Id),
    ZoneId      INTEGER NOT NULL REFERENCES Zones(Id),
    EventType   TEXT NOT NULL,            -- Enter | Exit
    FixId       INTEGER NOT NULL REFERENCES Fixes(Id),
    OccurredUtc INTEGER NOT NULL
);

CREATE TABLE Excursions (              -- derived, rebuilt incrementally
    Id            INTEGER PRIMARY KEY,
    TagId         INTEGER NOT NULL REFERENCES Tags(Id),
    DepartedUtc   INTEGER NOT NULL,
    ReturnedUtc   INTEGER,             -- NULL = currently out
    MaxDistanceM  REAL,
    FixCount      INTEGER NOT NULL,
    CoverageRatio REAL                 -- see §5: how much of it we actually observed
);

CREATE TABLE Alerts (
    Id          INTEGER PRIMARY KEY,
    Kind        TEXT NOT NULL,         -- ZoneExit | ZoneEnter | LowBattery | DataStale
    Message     TEXT NOT NULL,
    RaisedUtc   INTEGER NOT NULL,
    DeliveredUtc INTEGER
);

CREATE TABLE Meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);  -- schema version, last poll, etc.
```

We keep **every** fix forever. At an optimistic 1 fix/minute that's ~525k rows/year — nothing for
SQLite, and it makes the history features possible. Store the raw JSON of the item only in a
debug table you can truncate, not in `Fixes`.

> **Field names above are the expected shape and must be confirmed by the Phase 0 spike.** They're
> from an undocumented cache; treat the spike output as the source of truth and adjust before
> writing the schema.

---

## 4. Geofencing that survives bad accuracy

Naïve "is the point inside the circle" logic will flap constantly, because a 150 m-accurate fix
from a passing phone will regularly place your cat across the street while she's asleep on the
sofa. Three defences, all in `CatTracker.Core` and all unit-tested:

1. **Accuracy gate.** Fixes with `HorizontalAccuracy > 100 m`, or `IsInaccurate`, or `IsOld` are
   *stored* but **excluded from geofence evaluation**. They still show on the map, greyed.
2. **Hysteresis.** Exit requires distance > `RadiusM + ExitBufferM`. Re-entry requires distance <
   `RadiusM`. The dead band prevents oscillation at the boundary.
3. **Confirmation.** A state change requires **two consecutive qualifying fixes** in the new state.
   One rogue fix never triggers an alert.

State machine per (tag, zone): `Inside → Leaving → Outside → Returning → Inside`, with the
intermediate states holding until confirmed. Persist the state so a collector restart doesn't
replay old alerts.

Distance: haversine is fine at these scales — no need for a geodesy library.

**Home zone radius:** start at 30 m and tune. Set the centre by clicking the map in the UI, not by
typing coordinates.

### Alerts

| Channel | How | Reality check |
|---|---|---|
| macOS notification | `osascript -e 'display notification ...'` | Works instantly, free, local. Useless if you're not at the Mac. |
| Audible | `afplay` a sound file | Good for a "cat is out at 2am" alarm. |
| iPhone push | **Honest answer: every instant-push route leaves your LAN.** Apple's APNs, ntfy, Pushover — all are relays. If push to phone is a must, self-hosted **ntfy** on the Mac + the ntfy iOS app is the closest to local (you run the server; only the wake-up ping transits ntfy.sh). |
| iMessage to yourself | AppleScript → Messages.app | Stays inside your own Apple account, arrives on your phone. Fragile across macOS versions but genuinely simple. |

Recommendation: macOS notification + sound in v1, iMessage-to-self as the opt-in phone channel,
and treat push as a later decision. Rate-limit every channel — one alert per zone transition, with
a minimum 10-minute cooldown per kind.

---

## 5. Statistics — and their honesty problem

Sparse sampling makes naïve stats lie. Every metric below carries a **coverage ratio**: observed
span ÷ total span, where any gap > 30 minutes counts as unobserved.

| Metric | Definition |
|---|---|
| Time outdoors / day | Sum of excursion durations. Show as a range: "3h 10m observed, up to 5h 40m including gaps." |
| Dwell heatmap | Grid the area; weight each cell by time between consecutive fixes in it (capped at 30 min so one long gap doesn't paint a false hotspot). |
| Roaming radius | Max and 95th-percentile distance from home, per day and rolling 30-day. |
| Longest excursion | Duration + max distance + a replayable track. |
| Rhythm | Departure/return times as a 24h histogram — reveals the cat's actual schedule. |
| Favourite spots | Cluster fixes (DBSCAN, ~20 m epsilon), rank by total dwell, reverse-geocode offline or just label by hand. |
| Battery trend | `batteryStatus` over time → predicted replacement date. |

Compute on read for anything under ~50k rows; SQLite handles it. Only materialise `Excursions`,
because the state machine builds it incrementally anyway.

---

## 6. Map & "fully local"

Leaflet needs tiles, and OpenStreetMap tiles come from the internet. To be genuinely local:

**Caching tile proxy.** `/tiles/{z}/{x}/{y}.png` in the Web project checks a local SQLite tile
store; on a miss it fetches once from OSM, stores it, and serves it. After you've panned around
your neighbourhood once, the map works with the network unplugged forever.

Respect the OSM tile usage policy: cache aggressively, send a real `User-Agent`, and pre-seed
only your own neighbourhood (z14–z18 over ~2 km² is a few thousand tiles — fine; a country is
not). A `seed-tiles` CLI command in the Web project handles pre-seeding.

**UI — one page, six views** (grew from three during the build):

- **Live** — hero status card tinted by state, accuracy circle, home zone, and KPI tiles for fix
  age, accuracy, distance from home and battery.
- **History** — range picker, polyline track with **gaps drawn as gaps**, greyed low-confidence
  fixes, dwell heatmap toggle, playback scrubber, and per-window coverage.
- **Timeline** — a chronological feed of departures, returns, finished trips and alerts, grouped
  by day. This turned out to be the view that actually answers "what did she do yesterday?".
- **Stats** — daily outdoors (observed *and* upper bound), rhythm histogram, roaming radius,
  favourite spots. Hand-rolled SVG rather than Chart.js: about eighty lines, no dependency.
- **Health** — collector, reader agent, position freshness, home zone, alert channels, schema,
  storage, and a live log tail. Added because a Mac in a cupboard has no screen you will ever look
  at, and "why is it quiet?" needs an answer from your phone.
- **Setup** — zones by map click, renaming, offline tile seeding, test alert.

Bind Kestrel to the LAN IP, add it to your iPhone's Home Screen with a web app manifest, and it
behaves like a native app. **No auth** is acceptable on a trusted home LAN; if not, a single
shared token in a cookie is ten lines. Do not port-forward it.

**Logging.** Serilog writes rolling daily files next to the data, with retention and a size cap,
and `/api/logs` tails them into the Health page. Levels come from configuration, so raising
verbosity on the Mac to chase a problem is a config edit and a restart rather than a rebuild and
redeploy from Windows. At `Debug` you get every poll, every geofence decision with its distance
and verdict, and every rejected fix with the reason — the log that explains a false alarm.

---

## 7. Build phases

### Phase 0 — Feasibility spike ⚠️ *do this before anything else*

On the Mac, with Find My open and the AirTag on the cat:

```bash
ls -l ~/Library/Caches/com.apple.findmy.fmipcore/
python3 -m json.tool < ~/Library/Caches/com.apple.findmy.fmipcore/Items.data | head -120
```

Then watch it for an hour: `stat -f "%m %z" Items.data` on a loop, logging changes.

**You are answering four questions, and the whole project depends on them:**
1. Does the file exist and parse, on *your* macOS version?
2. What are the **actual** field names? (Correct §3 to match.)
3. How often does the timestamp genuinely advance — with the cat indoors, and outdoors?
4. What does `horizontalAccuracy` look like in practice at your address?

Capture a redacted sample into `samples/items-sample.json`. **If this phase fails, stop** — every
later phase is built on it, and the fallback (a DIY OpenHaystack tag whose keys you own) is a
different project with different hardware.

*Deliverable: a sample file, an hour of cadence data, and a go/no-go.*

### Phase 1 — Collector + storage ✅
Core models, EF schema, `IFindMyReader` with all three implementations, poll loop, dedupe,
structured logging.

*Done: the database grows correctly and restarting creates no duplicate rows — the unique index on
`(TagId, TimestampUtc)` is what makes ingestion idempotent, since Find My hands us the same
position on every poll.*

### Phase 2 — Web API + live map ✅
Minimal API, Leaflet page, caching tile proxy.

### Phase 3 — Zones, geofencing, alerts ✅
Zone CRUD by map click, the state machine with its three defences, `ZoneEvents`, `Excursions`,
macOS notification/sound/iMessage channels, and the `DataStale` watchdog that names the likely
cause rather than just saying "no data".

### Phase 4 — History, timeline & stats ✅
Track playback, heatmap, charts, coverage ratios everywhere, chronological timeline.

### Phase 5 — Hardening ✅
launchd plists with `KeepAlive`, rolling logs with retention, `/api/health`, the in-app log viewer,
one-command idempotent install/update, and a documented Full Disk Access procedure — which the
privileged-reader split (§2) reduces to a one-time step.

**Still worth doing once it is on the Mac:** a nightly `VACUUM INTO` backup to a second disk
(see docs/OPERATIONS.md §6) — one line in a cron job or your existing backup script.

---

## 8. Deployment to the Mac

**Each platform installs itself.** One command, and the *same* command for a first install and
every later update:

```bash
./setup/macos/install.sh          # builds from source on the Mac, installs both LaunchAgents
```

```powershell
./setup/windows/install.ps1       # builds, installs a Windows Service, opens the firewall
```

Both are idempotent. Neither needs the other machine — that was a deliberate change from the
original plan, which had you developing on Windows and cross-publishing to the Mac. Cross-deploy
still exists (`./deploy.ps1 -MacHost mac.local -User you`) as a convenience for driving the Mac
from a Windows box, but it is no longer the only path.

Three details that would otherwise cost an evening each, all handled by `install.sh`:

- **Binaries cross-published from Windows arrive unsigned**, and Apple silicon refuses to execute
  unsigned code outright. The installer ad-hoc signs both and strips the quarantine attribute.
- **The reader is replaced only when its contents changed**, because that is what preserves its
  Full Disk Access grant; the installer says so explicitly when it does change.
- **Data lives outside the install directory**, so an update replaces binaries and can never touch
  your history, your zones or your settings.

Two things remain for a human, because macOS allows nothing else: granting Full Disk Access to the
reader (once), and keeping the Mac awake with Find My running. The installer detects both and
prints exactly what to do, opening the right Settings pane.

Full detail in [docs/OPERATIONS.md](docs/OPERATIONS.md).

---

## 9. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Cache format changes in a macOS update | Medium | Schema-tolerant parser; alert loudly on parse failure rather than logging silently; sample file pinned in the repo for diffing. |
| Find My quits / Mac sleeps | High | `DataStale` watchdog is a **first-class feature**, not polish. Silence must be alarming, not invisible. |
| Sparse fixes make stats misleading | High | Coverage ratios everywhere; gaps rendered as gaps. |
| Geofence false alarms at 3am | High | Accuracy gate + hysteresis + two-fix confirmation, all in Phase 3. |
| Neighbours get stalking alerts / tag beeps | Certain | Nothing to do technically. Be a good neighbour about it. |
| Collar lost, AirTag gone | Medium | Breakaway collar is the right call anyway; a `DataStale` alert is also your "collar came off" signal. |
| Full Disk Access lost after rebuild | Medium | Stable install path + explicit startup self-check with a clear message. |

---

## 10. Decisions

**Settled during the build:**

- **Dapper vs EF Core** → **EF Core.** Real migrations matter more than SQL control here: the app
  updates itself on a machine you rarely touch, so a schema change has to ship with the build that
  needs it and apply itself on start. The `Repository` class kept the SQL-shaped API either way.
- **Full Disk Access on every update** → solved by the privileged-reader split (§2).
- **Charting library** → hand-rolled SVG. Two charts, eighty lines, no 200 KB dependency and
  nothing to keep vendored up to date.

**Still open:**

- **iPhone push** — every instant-push route leaves the LAN. macOS notification + sound work today;
  iMessage-to-self is the opt-in phone channel and stays inside your own Apple account. Self-hosted
  ntfy is the next step if you want true push, and it is a trade you should make deliberately.
- **Multiple cats** — the schema and the API already handle N tags, and the UI shows a picker when
  there is more than one. The **statistics pages are still single-tag**: they show whichever cat is
  selected rather than comparing them. Say if you want side-by-side.
- **`StaleAfterMinutes`** — currently 45. This is the one setting you cannot tune until the Phase 0
  spike tells you the real update cadence at your address. Too low and it nags whenever she naps
  somewhere quiet; too high and a dead reader goes unnoticed for hours.
- **`Alerts:LowBatteryAtOrAbove`** — Find My's `batteryStatus` integer is undocumented. The app
  alerts on any *change*, which is mapping-agnostic and always meaningful; confirm what the values
  actually mean during the spike and set the threshold properly.
