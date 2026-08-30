# Setup — Windows

A complete, self-contained Windows install. Nothing here needs a Mac.

> **Read this first.** Windows has no Find My cache — that file exists only on macOS — so a
> Windows install **cannot read a real AirTag on its own**. It runs the *Replay* source: a
> synthetic cat with realistic behaviour. That is genuinely useful for evaluating the app,
> developing against it, and demonstrating it, and it exercises every code path the real thing
> uses. For real AirTag tracking, see [SETUP-MACOS.md](SETUP-MACOS.md).
>
> There is one hybrid option, covered at the end: if a Mac is already running the reader, a
> Windows install can read its spool folder over a share.

---

## Option A — just run it

For trying it out, developing, or leaving it running in a terminal.

```powershell
./run-local.ps1
```

Open <http://localhost:5185>. On first run it backfills 14 days of synthetic history, so every
page has data immediately.

```powershell
./run-local.ps1 -Fresh          # start from an empty database
./run-local.ps1 -SeedDays 30    # more history
./run-local.ps1 -Port 8080
```

Data goes to `.data\` inside the repo, so deleting the folder resets everything.

**From Visual Studio:** just press F5. Three launch profiles are configured — replay with history,
replay without, and spool. They write to `%LOCALAPPDATA%\CatTracker.Dev`.

---

## Option B — install it as a Windows Service

For leaving it running properly: starts with the machine, restarts on crash, survives logout.

From an **elevated** PowerShell:

```powershell
./setup/windows/install.ps1
```

That single command builds from source, installs to `C:\Program Files\CatTracker`, registers an
auto-starting service, opens the firewall on private networks, starts it, and verifies the API
answers. **The same command updates it later** — it is idempotent, so just run it again.

```powershell
./setup/windows/install.ps1 -Port 8080
./setup/windows/install.ps1 -SeedDays 0        # no synthetic history
./setup/windows/install.ps1 -NoFirewall        # skip the firewall rule
```

If you have a `.zip` from `publish.ps1` instead of a source checkout, unpack it and run the
`install.ps1` inside — it uses the prebuilt binaries next to it.

### Where things go

| | |
|---|---|
| Binaries | `C:\Program Files\CatTracker\app\` — replaced wholesale on update |
| Database | `C:\ProgramData\CatTracker\cattracker.db` |
| Map tiles | `C:\ProgramData\CatTracker\tiles.db` |
| Logs | `C:\ProgramData\CatTracker\logs\` |
| Your settings | `C:\ProgramData\CatTracker\config.local.json` |
| Service | `CatTracker` (Automatic), environment in the registry |

Data sits outside the install directory deliberately: an update replaces binaries and can never
touch your history or your settings.

### Running it

```powershell
Get-Service CatTracker
Restart-Service CatTracker
Stop-Service CatTracker

# Follow the log
Get-Content "$env:ProgramData\CatTracker\logs\cattracker-*.log" -Tail 40 -Wait
```

You rarely need any of these — the **Health** page in the app shows the same things, including a
live log tail, from your phone.

### Uninstalling

```powershell
./setup/windows/uninstall.ps1            # service, files and firewall rule; data kept
./setup/windows/uninstall.ps1 -Purge     # everything
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Run this from an elevated PowerShell` | Not admin | Right-click PowerShell → Run as administrator |
| Service starts then stops immediately | Usually a bad data directory or port in use | `Get-Content "$env:ProgramData\CatTracker\logs\cattracker-*.log" -Tail 40` |
| `install.ps1` fails copying files | Old binary still locked | The script stops the service and waits; if it persists, `Stop-Service CatTracker` then retry |
| Port already in use | Something else on 5185 | `./setup/windows/install.ps1 -Port 8080` |
| Cannot reach it from your phone | Firewall, or you are on a "Public" network | The rule is Private-profile only. Set the network to Private, or re-run with a rule for your profile |
| Map is blank | No cached tiles for that area | Setup → seed the current view (needs internet once) |
| "No tag yet" forever | `Source=Spool` with nothing writing to the spool | Use `-Source Replay`, or point `-SpoolDirectory` at a real spool |
| `dotnet` not found | No SDK | Install .NET 10 SDK from <https://dot.net>, or use a prebuilt `.zip` |

Full configuration reference: [CONFIGURATION.md](CONFIGURATION.md).

---

## The hybrid option: Mac reader, Windows app

If a Mac is already running `cattracker-reader`, you can run the app itself on Windows and let it
read the Mac's spool folder over a share. The Mac still has to be on and awake — it is the only
machine that can see the Find My cache — but the database, UI and alerts live on Windows.

1. On the Mac, share the spool folder (System Settings → General → Sharing → File Sharing), or
   sync it another way. It contains `items.json` and `heartbeat.json`, and is tiny.
2. On Windows:

```powershell
./setup/windows/install.ps1 -Source Spool -SpoolDirectory \\mac\CatTracker\spool
```

Two caveats worth knowing before you choose this:

- **The service runs as LocalSystem, which has no access to network shares.** You must reconfigure
  it to run as your own account (`services.msc` → CatTracker → Log On), or map the share for the
  machine account. This is the usual reason the hybrid setup silently sees nothing.
- **You now depend on two machines being up.** The all-macOS setup depends on one. If you have a
  Mac anyway, [SETUP-MACOS.md](SETUP-MACOS.md) is the simpler and more reliable choice.
