# HTTP API

Everything the web UI does, it does through these endpoints. There is no authentication — see the
note at the end.

All timestamps are **Unix milliseconds, UTC**. Enums serialise as strings. `from`/`to` query
parameters are optional; each endpoint documents its default window.

Base URL: `http://mac.local:5185`

---

## System

### `GET /api/health`

Cheap liveness check. Used by the installer and `deploy.ps1`.

```json
{
  "status": "ok",
  "version": "1.0.0",
  "migrations": 1,
  "schema": "20260830121547_InitialSchema",
  "dataDirectory": "/Users/you/Library/Application Support/CatTracker"
}
```

### `GET /api/status`

The one call the Live and Health pages are built on.

```json
{
  "version": "1.0.0",
  "source": "Spool from cattracker-reader (/Users/you/…/spool)",
  "nowUtc": 1756557000000,
  "isStale": false,
  "lastPollUtc": 1756556995000,
  "warnings": [],
  "error": null,
  "heartbeat": {
    "writtenUtcMs": 1756556990000,
    "status": "ok",
    "detail": "updated",
    "sourceMTimeUtcMs": 1756556988000,
    "sourceSizeBytes": 4821
  },
  "home": { "id": 1, "name": "Home", "kind": "Home", "centerLat": 52.0907, "centerLon": 5.1214,
            "radiusM": 30, "exitBufferM": 25, "notifyOnExit": true, "notifyOnEnter": true },
  "tags": [{
    "id": 1, "petName": "Pluis", "findMyName": "Pluis", "serialNumber": "HK1234ABCD",
    "isActive": true,
    "latestFix": { "id": 4821, "timestampUtc": 1756556900000, "latitude": 52.0907,
                   "longitude": 5.1214, "horizontalAccuracy": 12.5, "isOld": false,
                   "isInaccurate": false, "batteryStatus": 1 },
    "ageMs": 100000,
    "isHome": true,
    "batteryStatus": 1,
    "openExcursion": null,
    "distanceFromHomeM": 4.2,
    "fixCount": 48213,
    "firstFixUtc": 1743000000000
  }],
  "alertChannels": ["log", "macos-notification", "sound"],
  "timeZone": "Europe/Amsterdam"
}
```

`isHome` is `null` until the geofence has seen its first qualifying fix — it means "we do not
know yet", not "she is out". `heartbeat` is `null` when the reader agent is not running, which is
how the UI distinguishes a dead reader from a quiet cat.

### `GET /api/logs`

Tails the rolling log files, so you can diagnose from your phone.

| Parameter | Default | |
|---|---|---|
| `lines` | `300` | 1–5000 |
| `contains` | — | Case-insensitive substring filter |
| `file` | newest | Must be one of the returned `files` |

```json
{ "file": "cattracker-20260830.log",
  "files": ["cattracker-20260830.log", "cattracker-20260829.log"],
  "lines": ["2026-08-30 14:28:30.353 +02:00 [INF] …"] }
```

An unrecognised `file` silently falls back to the newest — a log viewer is not a reason to hand
out arbitrary file reads.

---

## Alerts

### `GET /api/alerts?limit=50`

Newest first. `limit` is 1–500.

```json
[{ "id": 12, "kind": "ZoneExit", "message": "Pluis has left Home.",
   "raisedUtc": 1756556000000, "deliveredUtc": 1756556000000 }]
```

`kind` is one of `ZoneExit`, `ZoneEnter`, `LowBattery`, `DataStale`, `ReaderProblem`.
`deliveredUtc` is `null` when the alert was recorded but suppressed by a cooldown or by a zone's
notification settings.

### `POST /api/alerts/test`

Fires a real alert through every available channel. Worth its weight during setup, when the
question is always "is it broken, or has the cat simply not moved?"

```json
{ "id": 13, "channels": ["log", "macos-notification", "sound"] }
```

---

## Tags

### `GET /api/tags`

### `PATCH /api/tags/{id}`

```json
{ "petName": "Pluisje", "isActive": true }
```

`petName` is required and non-blank → `400`. Unknown id → `404`. The pet name is yours; the
Find My name keeps following whatever the Find My app says.

---

## Positions

### `GET /api/fixes?tagId=1&from=…&to=…&max=5000`

Default window: last 24 hours. `max` (100–100000) uniformly thins the track for display, always
keeping the first and last point — a fortnight of history is tens of thousands of points and the
phone does not need all of them.

```json
[{ "id": 4821, "tagId": 1, "timestampUtc": 1756556900000,
   "latitude": 52.0907, "longitude": 5.1214, "horizontalAccuracy": 12.5,
   "altitude": 3.0, "positionType": "crowdsourced",
   "isOld": false, "isInaccurate": false, "batteryStatus": 1,
   "ingestedUtc": 1756556905000 }]
```

Low-confidence fixes are returned, not filtered — the UI greys them rather than hiding them.

### `GET /api/events?tagId=1&limit=100`

Zone enter/exit events, newest first.

### `GET /api/excursions?tagId=1&from=…&to=…`

Default window: 14 days. Includes an excursion still in progress (`returnedUtc: null`).

```json
[{ "id": 88, "tagId": 1, "departedUtc": 1756540000000, "returnedUtc": 1756551000000,
   "maxDistanceM": 412.6, "fixCount": 23, "coverageRatio": 0.42 }]
```

`coverageRatio` is the fraction of the excursion we actually observed. `0.42` means over half of
it is guesswork — treat the duration as an upper bound.

---

## Zones

### `GET /api/zones`

### `POST /api/zones` → `201`

### `PUT /api/zones/{id}` → `200`, or `404`

### `DELETE /api/zones/{id}` → `204`, or `404`

```json
{
  "name": "Home",
  "kind": "Home",
  "centerLat": 52.0907,
  "centerLon": 5.1214,
  "radiusM": 30,
  "exitBufferM": 25,
  "notifyOnExit": true,
  "notifyOnEnter": true
}
```

| Field | Rule |
|---|---|
| `name` | required |
| `kind` | `Home`, `Watch` or `Hazard` (case-insensitive) |
| `centerLat` / `centerLon` | valid coordinates |
| `radiusM` | 0 < r ≤ 100000 — a zero radius would make the fence fire on every fix, forever |
| `exitBufferM` | 0 ≤ b ≤ 100000, default 25 |

Violations return `400` with `{"error": "…"}`.

**Only the first `Home` zone drives excursions and statistics.** `Watch` and `Hazard` zones only
produce events and alerts. Deleting a zone cascades its state and events.

---

## Statistics

Every one of these is an estimate over a sparse, unevenly sampled signal. Where that matters, the
response says so.

### `GET /api/stats/daily?tagId=1&days=14`

```json
[{ "date": "2026-08-29", "observedOutdoorMs": 7500000, "upperBoundOutdoorMs": 28140000,
   "excursionCount": 3, "maxDistanceM": 402.1, "fixCount": 341, "coverage": 0.27 }]
```

`observedOutdoorMs` is what we actually saw; `upperBoundOutdoorMs` includes the gaps. When they
differ by 4× — as above — the honest reading is "somewhere between 2 and 8 hours".

### `GET /api/stats/heatmap?tagId=1&from=…&to=…&cell=25`

Dwell-weighted occupancy grid, capped so one overnight gap cannot paint a fake hotspot. Up to
4000 cells, largest dwell first.

```json
[{ "lat": 52.0907, "lon": 5.1214, "cellMeters": 25, "dwellMs": 5400000 }]
```

### `GET /api/stats/clusters?tagId=1&from=…&to=…&eps=20&minPoints=5`

DBSCAN over the fixes — her favourite spots, ranked by dwell. Input is capped at 3000 points
because the algorithm is O(n²).

```json
[{ "lat": 52.0906, "lon": 5.1215, "dwellMs": 18000000, "fixCount": 240, "radiusM": 14.2 }]
```

### `GET /api/stats/rhythm?tagId=1&days=30`

```json
{ "departures": [0,0,1, …24 values], "returns": [0,2,0, …] }
```

### `GET /api/stats/roaming?tagId=1&from=…&to=…`

Requires a `Home` zone; `400` otherwise.

```json
{ "roaming": { "maxDistanceM": 512.4, "p95DistanceM": 388.1,
               "meanDistanceM": 96.3, "fixCount": 4821 },
  "coverage": 0.61, "from": 1755350000000, "to": 1756557000000 }
```

---

## Map tiles

### `GET /tiles/{z}/{x}/{y}.png`

Caching proxy. Serves from the local store; on a miss, fetches once from OpenStreetMap and keeps
it forever. With `Tiles:AllowNetwork: false` a miss is simply `404` — that is what makes the map
genuinely offline.

### `GET /api/tiles/status`

```json
{ "cachedTiles": 3412, "cachedBytes": 51200000,
  "seeding": { "running": false, "total": 0, "done": 0, "failed": 0, "cached": 0,
               "message": "Done. 3,412 tiles available (0 already cached), 0 failed." } }
```

### `POST /api/tiles/seed` → `202`

```json
{ "minLat": 52.08, "minLon": 5.11, "maxLat": 52.10, "maxLon": 5.14,
  "minZoom": 14, "maxZoom": 18 }
```

Returns `{ "planned": 2140, "cap": 20000 }` and seeds in the background; poll
`/api/tiles/status`. `409` if a run is already going, `400` for inverted bounds or when
`Tiles:AllowNetwork` is false.

A run truncated by the cap says so in its completion message — a bare "Done" over a capped run
would read as full coverage, and you would only find out when the map went blank offline.

---

## A note on security

There is no authentication, by design: this is meant for a trusted home LAN, and adding a login to
something only you can reach buys nothing. The consequence is that **anyone on your network can
read your cat's location history and change your zones**.

If that matters, put it behind a VPN or Tailscale. Do not port-forward it.
