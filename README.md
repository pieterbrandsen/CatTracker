# CatTracker

Local cat tracking built on a real AirTag, an always-on Mac, and a self-hosted .NET app.
No cloud services, no hosted website, no accounts beyond the Apple ID you already have.

Find My shows you a dot on a map. CatTracker gives you the things it does not: **history,
geofenced away/home alerts, and honest statistics** about where your cat actually goes.

<!-- Screens: Live · History · Timeline · Stats · Health · Setup -->

---

## The one thing to understand first

**Apple publishes no API for AirTags.** No SDK, no REST endpoint, no MFi programme. Location for
a genuine AirTag exists in exactly one place: inside the Find My app on a device signed into the
owning Apple ID.

So CatTracker reads Find My's own decrypted cache from disk:

```
~/Library/Caches/com.apple.findmy.fmipcore/Items.data
```

It never touches Apple's network, never handles your credentials, never signs in to anything.
Find My does all of that; we read its output.

**And an AirTag has no GPS.** It reports the location of whichever stranger's iPhone last walked
past it. Updates are seconds apart on a busy street and *hours* apart in a quiet garden at 3am,
with accuracy from 5 m to 200 m. Every number this app shows is an estimate over a sparse,
unevenly sampled signal — and the UI is built to say so rather than hide it.

Read [PLAN.md](PLAN.md) for the full reasoning, the trade-offs, and the risks.

---

## Two setups, each complete on its own

Pick one. Neither needs the other machine.

| | **[macOS](docs/SETUP-MACOS.md)** | **[Windows](docs/SETUP-WINDOWS.md)** |
|---|---|---|
| Tracks a real AirTag | **Yes** | No — Windows has no Find My cache |
| Data source | The Find My cache | Replay (a synthetic cat) |
| Runs as | Two LaunchAgents | A Windows Service |
| Install & update | `./setup/macos/install.sh` | `./setup/windows/install.ps1` |
| Good for | The actual job | Evaluating, developing, demonstrating |

Both build from source on the machine they run on, and the install command is also the update
command. If you want to drive the Mac from a Windows box instead, `./deploy.ps1` does that in one
step — but it is a convenience, not a requirement.

### Try it in thirty seconds, no install

```powershell
./run-local.ps1
```

Then open <http://localhost:5185>. Or press F5 in Visual Studio — the launch profiles are set up.

The replay source generates a plausible cat — irregular update intervals, accuracy that degrades
once she leaves the garden, occasional multi-hour blackouts — and backfills a fortnight of history
on first run, so every page has real-shaped data immediately.

This is not decoration. It is what makes the project developable without Apple hardware, and
testable at all: code that looks right against a tidy synthetic track and falls over on the real
thing has learned nothing.

---

## What it does

| | |
|---|---|
| **Live** | Where she is now, how old that fix is, how far from home, battery, and whether anything is broken. |
| **History** | The track for any window, with gaps drawn *as gaps*, a dwell heatmap, and a playback scrubber. |
| **Timeline** | A chronological feed: left home, came back, trip finished, alerts. |
| **Stats** | Time outdoors per day (observed *and* upper bound), daily rhythm, roaming radius, favourite spots. |
| **Health** | Collector, reader agent, position freshness, alert channels, storage — and the live log tail. |
| **Setup** | Zones on a map, renaming, offline tile seeding, and a test-alert button. |

Alerts go to macOS notifications and a sound, optionally to iMessage. Geofencing survives bad
accuracy through an accuracy gate, a hysteresis dead band, and N-consecutive-fix confirmation —
so a single rogue fix can never wake you at 3am.

---

## Architecture

Two processes on the Mac, sharing one SQLite file.

```
Find My.app ──writes──► Items.data
                            │
                   ┌────────▼─────────┐   LaunchAgent, has Full Disk Access
                   │ cattracker-reader│   ~15 MB, single file, effectively frozen
                   └────────┬─────────┘
                            │ copies to spool + writes heartbeat.json
                   ┌────────▼─────────┐   LaunchAgent, needs NO permissions
                   │    cattracker    │   collector + API + web UI
                   └────────┬─────────┘
                            │
              cattracker.db (SQLite, WAL) · tiles.db · logs/
                            │
                    http://mac.local:5185  ← your phone, on the LAN
```

**Why the split?** macOS grants Full Disk Access per binary, and replacing a binary revokes it.
Keeping the privileged component tiny and unchanging means you grant FDA **once**, then update the
rest as often as you like without ever opening System Settings again. It is also least privilege:
the component that can read your whole disk does nothing but copy one file.

| Layer | Choice |
|---|---|
| Runtime | .NET 10 (LTS), self-contained — the Mac needs no SDK or runtime |
| Data | EF Core 10 + SQLite, real migrations, WAL |
| Web | ASP.NET Core minimal API |
| Front end | Static HTML + vendored Leaflet + hand-rolled SVG charts. No npm, no build step. |
| Logging | Serilog, rolling daily files, readable from the browser |
| Scheduling | launchd LaunchAgents |

```
src/CatTracker.Core     domain: geofence engine, statistics, cache parser — no I/O
src/CatTracker.Data     EF Core context, migrations, repository
src/CatTracker.App      collector, HTTP API, web UI
src/CatTracker.Reader   the small privileged file-copier
tests/CatTracker.Tests  236 tests, 94% line coverage
```

---

## Installing for real

**On the Mac, run the spike first.** It confirms the cache exists, parses, and actually updates on
your macOS version, and measures the real update cadence. If it fails, stop — everything else is
built on it.

```bash
./setup/macos/spike.sh 60
```

Then, on whichever machine you chose:

```bash
./setup/macos/install.sh
```

```powershell
./setup/windows/install.ps1
```

Each builds from source, installs, registers the service or agents, starts them and verifies the
API answers. Run the same command again to update.

**On macOS, three things only a human can do**, and the installer detects and explains all three:
grant Full Disk Access to `cattracker-reader` (once, not per update), keep Find My running, and
stop the Mac sleeping.

---

## Documentation

| | |
|---|---|
| **[docs/SETUP-MACOS.md](docs/SETUP-MACOS.md)** | Complete macOS setup — the one that tracks a real AirTag |
| **[docs/SETUP-WINDOWS.md](docs/SETUP-WINDOWS.md)** | Complete Windows setup — Windows Service, replay source |
| **[docs/OPERATIONS.md](docs/OPERATIONS.md)** | Updating, backups, log levels, tuning the geofence |
| **[docs/CONFIGURATION.md](docs/CONFIGURATION.md)** | Every setting, what it does, and when to change it |
| **[docs/API.md](docs/API.md)** | The HTTP API |
| **[PLAN.md](PLAN.md)** | Design, trade-offs, risks, and why it is built this way |

---

## Development

```powershell
dotnet build CatTracker.slnx
dotnet test  CatTracker.slnx
./coverage.ps1 -ShowGaps      # enforces an 80% line-coverage floor
./run-local.ps1 -Fresh        # replay source, clean database
```

Adding a schema change — `--context` is required because there are two DbContexts:

```powershell
dotnet dotnet-ef migrations add YourChange `
  --project src/CatTracker.Data --startup-project src/CatTracker.Data --context CatContext
```

Migrations are applied automatically on start, which is what keeps updating to "swap the binaries".

`TileContext` deliberately has no migrations: it is a rebuildable cache of map tiles in its own
file, and if its schema ever needs to change, deleting `tiles.db` *is* the migration.

---

## Things worth knowing before you put this on a cat

- **Use a breakaway collar.** A snagged cat on a fence is a far bigger risk than a lost AirTag.
- **It will beep.** An AirTag separated from you starts sounding, and nearby iPhones show "AirTag
  Found Moving With You". Your neighbours may get alerts. This is Apple's anti-stalking design and
  cannot be disabled.
- **Battery:** CR2032, roughly a year. CatTracker alerts on any change in reported battery status.
- **If reliable outdoor tracking matters more than this being a fun project**, a GPS/LTE cat collar
  beats an AirTag outright. This is the AirTag version, deliberately.

---

## Privacy

Everything stays on your machines. The app binds to your LAN and is not meant to be exposed to the
internet — do not port-forward it. The only outbound requests it ever makes are for OpenStreetMap
tiles, which are cached forever locally and can be switched off entirely once you have seeded your
neighbourhood (`Tiles:AllowNetwork: false`). The optional iMessage alert channel is the one thing
that leaves the LAN, and it travels through your own Apple account.
