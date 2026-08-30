/*
 * CatTracker front end. No framework, no build step: edit, refresh, done.
 *
 * The one idea worth knowing before reading this: an AirTag has no GPS, so the position data is
 * sparse, irregular and sometimes wrong by more than the size of a garden. Wherever that matters
 * the UI says so — stale fixes go red, low-confidence fixes go grey, gaps in the track are drawn
 * as gaps, and every duration is shown as observed-versus-upper-bound rather than a single
 * confident number.
 */

'use strict';

const MAX_GAP_MS = 30 * 60 * 1000;   // beyond this we admit we do not know where she was
const LOW_CONFIDENCE_M = 100;        // matches the server's default geofence accuracy gate

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

async function api(path, options) {
  const response = await fetch(path, options);
  if (!response.ok) {
    const body = await response.text();
    throw new Error(body || `${response.status} ${response.statusText}`);
  }
  return response.status === 204 ? null : response.json();
}

const send = (path, body, method = 'POST') =>
  api(path, { method, headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });

// ---- formatting --------------------------------------------------------------------------------

function fmtDuration(ms) {
  if (ms === null || ms === undefined) return '—';
  const minutes = Math.round(ms / 60000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ${minutes % 60}m`;
  return `${Math.floor(hours / 24)}d ${hours % 24}h`;
}

/** "just now" already reads as a time; everything else needs the "ago". */
const fmtAgo = (ms) => {
  const spelled = fmtDuration(ms);
  return spelled === 'just now' || spelled === '—' ? spelled : `${spelled} ago`;
};

function fmtShort(ms) {
  if (!ms) return '0m';
  const minutes = Math.round(ms / 60000);
  if (minutes < 60) return `${minutes}m`;
  return `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, '0')}m`;
}

const fmtDistance = (m) =>
  m === null || m === undefined ? '—' : m < 1000 ? `${Math.round(m)} m` : `${(m / 1000).toFixed(2)} km`;

const fmtBytes = (b) =>
  b < 1024 ? `${b} B` : b < 1048576 ? `${(b / 1024).toFixed(0)} KB` : `${(b / 1048576).toFixed(1)} MB`;

const fmtTime = (ms) => new Date(ms).toLocaleString(undefined, {
  month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
});

const fmtClock = (ms) => new Date(ms).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });

const fmtDay = (ms) => new Date(ms).toLocaleDateString(undefined, {
  weekday: 'long', day: 'numeric', month: 'short',
});


const isLowConfidence = (fix) =>
  fix.isInaccurate || fix.isOld || fix.horizontalAccuracy == null || fix.horizontalAccuracy > LOW_CONFIDENCE_M;

const esc = (value) => String(value).replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/**
 * Frames a set of fixes on a map.
 *
 * Two things have to be right or the view ends up uselessly wide. The container must have been
 * measured since it became visible — Leaflet fits a 0x0 map by zooming out to the whole world —
 * and the padding must be in pixels, because a proportional pad pushes the fit over a zoom
 * threshold and doubles the area shown.
 */
function fitTrack(map, fixes) {
  map.invalidateSize();
  if (!fixes.length || map.getSize().x < 50) return;

  const bounds = L.latLngBounds(fixes.map((f) => [f.latitude, f.longitude]));
  if (!bounds.isValid()) return;

  map.fitBounds(bounds, { padding: [28, 28], maxZoom: 18 });
}

/** Mirrors Stats.CoverageRatio on the server: gaps over 30 minutes count as unobserved. */
function coverageOf(fixes, from, to) {
  const total = to - from;
  if (total <= 0 || !fixes.length) return 0;

  let unobserved = 0;
  let previous = from;
  for (const fix of fixes) {
    const gap = fix.timestampUtc - previous;
    if (gap > MAX_GAP_MS) unobserved += gap;
    previous = fix.timestampUtc;
  }
  if (to - previous > MAX_GAP_MS) unobserved += to - previous;

  return Math.max(0, Math.min(1, (total - unobserved) / total));
}

// ---- shared state ------------------------------------------------------------------------------

const state = {
  tags: [],
  tagId: null,
  status: null,
  zones: [],
  historyHours: 24,
  timelineDays: 2,
  maps: {},
  layers: {},
  historyFixes: [],
  editingZoneId: null,
  centred: false,
  logTimer: null,
};

const currentTag = () =>
  state.tags.find((t) => t.id === state.tagId) ?? state.tags[0] ?? null;

// ---- maps --------------------------------------------------------------------------------------

function makeMap(elementId, centre, zoom) {
  // zoomSnap 0.25: with whole-number zoom, fitBounds has to round *down* to guarantee everything
  // fits, so a track that needs 16.2 drops to 16 and shows four times the area you wanted.
  const map = L.map(elementId, { zoomControl: true, zoomSnap: 0.25, zoomDelta: 0.5 })
    .setView(centre, zoom);

  // Tiles come from our own caching proxy, so the map keeps working with no internet once the
  // area has been visited or seeded.
  L.tileLayer('/tiles/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; OpenStreetMap contributors',
  }).addTo(map);

  return map;
}

const catIcon = () => L.divIcon({
  html: '<div style="font-size:28px;line-height:28px;filter:drop-shadow(0 2px 3px rgba(0,0,0,.45))">🐈</div>',
  className: '', iconSize: [28, 28], iconAnchor: [14, 14],
});

const zoneColour = (kind) =>
  kind === 'Home' ? '#14915c' : kind === 'Hazard' ? '#d33a4a' : '#b8730a';

function drawZones(map, zones, layerKey) {
  if (state.layers[layerKey]) map.removeLayer(state.layers[layerKey]);
  const group = L.layerGroup().addTo(map);

  for (const zone of zones) {
    const colour = zoneColour(zone.kind);

    L.circle([zone.centerLat, zone.centerLon], {
      radius: zone.radiusM, color: colour, weight: 2, fillOpacity: 0.08,
    }).bindTooltip(`${esc(zone.name)} · ${zone.kind}`).addTo(group);

    // The hysteresis band: she only counts as "out" past the dashed ring.
    if (zone.exitBufferM > 0) {
      L.circle([zone.centerLat, zone.centerLon], {
        radius: zone.radiusM + zone.exitBufferM,
        color: colour, weight: 1, dashArray: '4 6', fill: false, opacity: 0.55,
      }).addTo(group);
    }
  }

  state.layers[layerKey] = group;
}

// ---- tabs --------------------------------------------------------------------------------------

const loaders = {
  history: () => loadHistory(),
  timeline: () => loadTimeline(),
  stats: () => loadStats(),
  health: () => loadHealth(),
  setup: () => loadSetup(),
};

$$('.tab').forEach((tab) => tab.addEventListener('click', () => {
  $$('.tab').forEach((t) => t.classList.toggle('is-active', t === tab));
  $$('.panel').forEach((p) => p.classList.toggle('is-active', p.id === `panel-${tab.dataset.tab}`));

  // Leaflet cannot measure a container that was display:none, and a map it believes is 0x0 will
  // happily fit a 600 m track at zoom 0 — the entire planet. Re-measure synchronously, now that
  // the panel is visible and before any loader tries to fit bounds to it.
  Object.values(state.maps).forEach((m) => m.invalidateSize());

  loaders[tab.dataset.tab]?.();
}));

// ---- live --------------------------------------------------------------------------------------

function heroState(status, tag) {
  if (!tag?.latestFix) return 'unknown';
  if (status.isStale) return 'lost';
  if (tag.isHome === true) return 'home';
  if (tag.isHome === false) return 'out';
  return 'unknown';
}

function describeHeartbeat(status) {
  const beat = status.heartbeat;
  if (!beat) return 'No heartbeat from cattracker-reader — the reader agent looks dead.';
  if (beat.status === 'permission_denied') return 'cattracker-reader needs Full Disk Access.';
  if (beat.status === 'not_found') return 'The Find My cache file is missing.';
  if (beat.status === 'error') return `Reader error: ${beat.detail ?? 'unknown'}`;
  return 'The reader is healthy, so Find My itself has stopped refreshing. Is it running, and is the Mac awake?';
}

function renderHero(status) {
  const hero = $('#hero');
  const tag = currentTag();

  if (!tag) {
    hero.className = 'hero state-unknown';
    hero.innerHTML = `
      <div class="hero-top">
        <div class="hero-avatar">🐈</div>
        <div><div class="hero-name">No tag yet</div>
        <div class="hero-sub">Nothing has been read from Find My yet.</div></div>
      </div>
      <div class="notice info">Once the collector sees your AirTag it will appear here on its own.
        Source: <span class="mono">${esc(status.source)}</span></div>`;
    return;
  }

  const kind = heroState(status, tag);
  hero.className = `hero state-${kind}`;

  const label = { home: 'At home', out: 'Out', lost: 'Out of contact', unknown: 'Unknown' }[kind];
  const tone = { home: 'good', out: 'warn', lost: 'bad', unknown: 'plain' }[kind];

  const excursion = tag.openExcursion;
  const sub = excursion
    ? `Out for ${fmtDuration(status.nowUtc - excursion.departedUtc)} · ${fmtDistance(excursion.maxDistanceM)} at furthest`
    : tag.ageMs != null
      ? `Last seen ${fmtAgo(tag.ageMs)}`
      : 'No position yet';

  const trouble = [];
  if (status.isStale) {
    trouble.push(`<div class="notice"><strong>No recent positions.</strong> ${esc(describeHeartbeat(status))}</div>`);
  }
  if (status.error) trouble.push(`<div class="notice">Collector error: ${esc(status.error)}</div>`);
  for (const warning of status.warnings ?? []) {
    trouble.push(`<div class="notice warn">${esc(warning)}</div>`);
  }
  if (!status.home) {
    trouble.push(`<div class="notice warn">No <strong>Home</strong> zone yet — set one in Setup to get
      away/home alerts, excursions and every statistic.</div>`);
  }

  hero.innerHTML = `
    <div class="hero-top">
      <div class="hero-avatar">🐈</div>
      <div class="grow">
        <div class="hero-name">${esc(tag.petName)}</div>
        <div class="hero-sub">${esc(sub)}</div>
      </div>
      <span class="pill ${tone}"><span class="dot live"></span>${label}</span>
    </div>
    ${trouble.join('')}`;

  $('#brand-name').textContent = tag.petName;
  $('#brand-state').textContent = label.toLowerCase();
}

function renderLiveKpis(status) {
  const tag = currentTag();
  const host = $('#live-kpis');
  if (!tag) { host.innerHTML = ''; return; }

  const fix = tag.latestFix;
  const ageTone = tag.ageMs == null ? 'info' : tag.ageMs < 600000 ? 'good' : tag.ageMs < 2700000 ? 'warn' : 'bad';

  host.innerHTML = `
    <div class="kpi ${ageTone}">
      <div class="v">${tag.ageMs == null ? '—' : fmtDuration(tag.ageMs)}</div>
      <div class="l">Since last fix</div>
    </div>
    <div class="kpi info">
      <div class="v">${fix?.horizontalAccuracy != null ? `±${Math.round(fix.horizontalAccuracy)} m` : '—'}</div>
      <div class="l">Accuracy</div>
    </div>
    <div class="kpi">
      <div class="v">${fmtDistance(tag.distanceFromHomeM)}</div>
      <div class="l">From home</div>
    </div>
    <div class="kpi ${tag.batteryStatus >= 3 ? 'warn' : ''}">
      <div class="v">${tag.batteryStatus ?? '—'}</div>
      <div class="l">Battery status</div>
    </div>`;
}

function renderLiveMap(status) {
  const map = state.maps.live;
  const tag = currentTag();

  drawZones(map, status.home ? [status.home] : [], 'liveZones');

  if (state.layers.livePos) map.removeLayer(state.layers.livePos);
  if (!tag?.latestFix) return;

  const fix = tag.latestFix;
  const group = L.layerGroup().addTo(map);

  if (fix.horizontalAccuracy) {
    L.circle([fix.latitude, fix.longitude], {
      radius: fix.horizontalAccuracy, color: '#2f8fed', weight: 1, fillOpacity: 0.12,
    }).addTo(group);
  }

  L.marker([fix.latitude, fix.longitude], { icon: catIcon() })
    .bindPopup(`<strong>${esc(tag.petName)}</strong><br>${fmtTime(fix.timestampUtc)}<br>
                ±${Math.round(fix.horizontalAccuracy ?? 0)} m`)
    .addTo(group);

  state.layers.livePos = group;

  if (!state.centred) {
    map.setView([fix.latitude, fix.longitude], 17);
    state.centred = true;
  }
}

async function renderAlerts() {
  const alerts = await api('/api/alerts?limit=12');
  $('#alert-count').textContent = alerts.length;

  $('#alert-list').innerHTML = alerts.length
    ? alerts.map((a) => `<li><span>${esc(a.message)}</span>
        <span class="when">${fmtTime(a.raisedUtc)}</span></li>`).join('')
    : '<li class="empty">Nothing yet.</li>';
}

function renderTagPicker() {
  const host = $('#tag-picker');
  if (state.tags.length <= 1) { host.innerHTML = ''; return; }

  host.innerHTML = `<select>${state.tags.map((t) =>
    `<option value="${t.id}" ${t.id === state.tagId ? 'selected' : ''}>${esc(t.petName)}</option>`)
    .join('')}</select>`;

  $('select', host).addEventListener('change', (e) => {
    state.tagId = Number(e.target.value);
    state.centred = false;
    refreshStatus();
  });
}

async function refreshStatus() {
  try {
    const status = await api('/api/status');
    state.status = status;
    state.tags = status.tags;
    if (state.tagId === null && status.tags.length) state.tagId = status.tags[0].id;

    renderTagPicker();
    renderHero(status);
    renderLiveKpis(status);
    renderLiveMap(status);
    await renderAlerts();
  } catch (error) {
    $('#hero').innerHTML = `<div class="notice">Cannot reach CatTracker: ${esc(error.message)}</div>`;
    $('#brand-state').textContent = 'offline';
  }
}

// ---- history -----------------------------------------------------------------------------------

$$('#range-chips .chip').forEach((chip) => chip.addEventListener('click', () => {
  $$('#range-chips .chip').forEach((c) => c.classList.toggle('is-active', c === chip));
  state.historyHours = Number(chip.dataset.hours);
  loadHistory();
}));

['#toggle-heatmap', '#toggle-lowconf', '#toggle-playback'].forEach((sel) =>
  $(sel).addEventListener('change', () => loadHistory()));

async function loadHistory() {
  if (state.tagId === null) return;

  const to = Date.now();
  const from = to - state.historyHours * 3600 * 1000;
  const map = state.maps.history;

  const [fixes, excursions, zones] = await Promise.all([
    api(`/api/fixes?tagId=${state.tagId}&from=${from}&to=${to}&max=6000`),
    api(`/api/excursions?tagId=${state.tagId}&from=${from}&to=${to}`),
    api('/api/zones'),
  ]);

  state.historyFixes = fixes;
  drawZones(map, zones, 'historyZones');

  if (state.layers.track) map.removeLayer(state.layers.track);
  const group = L.layerGroup().addTo(map);

  // Draw the track segment by segment, so a four-hour blackout is visibly a blackout rather than
  // a confident straight line across the neighbourhood.
  for (let i = 1; i < fixes.length; i++) {
    const a = fixes[i - 1];
    const b = fixes[i];
    const gap = b.timestampUtc - a.timestampUtc;
    const line = [[a.latitude, a.longitude], [b.latitude, b.longitude]];

    if (gap > MAX_GAP_MS) {
      L.polyline(line, { color: '#8b95aa', weight: 1.5, opacity: 0.5, dashArray: '3 7' })
        .bindTooltip(`No data for ${fmtDuration(gap)}`).addTo(group);
    } else {
      L.polyline(line, { color: '#2f8fed', weight: 3.5, opacity: 0.8 }).addTo(group);
    }
  }

  const showLow = $('#toggle-lowconf').checked;
  const marks = fixes.length > 500 ? fixes.filter((_, i) => i % Math.ceil(fixes.length / 500) === 0) : fixes;

  for (const fix of marks) {
    const low = isLowConfidence(fix);
    if (low && !showLow) continue;

    L.circleMarker([fix.latitude, fix.longitude], {
      radius: low ? 3 : 4,
      color: low ? '#b8730a' : '#2f8fed',
      opacity: low ? 0.55 : 0.9,
      fillOpacity: low ? 0.25 : 0.7,
    }).bindTooltip(
      `${fmtTime(fix.timestampUtc)}<br>±${Math.round(fix.horizontalAccuracy ?? 0)} m${low ? ' · low confidence' : ''}`,
    ).addTo(group);
  }

  state.layers.track = group;

  fitTrack(map, fixes);

  await renderHeatmap(from, to);
  renderPlayback(fixes);

  const coverage = coverageOf(fixes, from, to);
  const outdoorMs = excursions.reduce((sum, e) => sum + ((e.returnedUtc ?? to) - e.departedUtc), 0);
  const furthest = excursions.reduce((max, e) => Math.max(max, e.maxDistanceM), 0);

  $('#history-kpis').innerHTML = `
    <div class="kpi info"><div class="v">${fixes.length}</div><div class="l">Fixes</div></div>
    <div class="kpi ${coverage < 0.5 ? 'warn' : 'good'}">
      <div class="v">${Math.round(coverage * 100)}%</div><div class="l">Window observed</div></div>
    <div class="kpi"><div class="v">${fmtShort(outdoorMs)}</div><div class="l">Time out</div></div>
    <div class="kpi"><div class="v">${fmtDistance(furthest)}</div><div class="l">Furthest</div></div>`;

  $('#excursion-count').textContent = excursions.length;

  $('#excursion-list').innerHTML = excursions.length
    ? excursions.slice().reverse().map((e) => {
      const end = e.returnedUtc ?? Date.now();
      return `<li class="clickable" data-from="${e.departedUtc}" data-to="${end}">
        <span>${fmtTime(e.departedUtc)} → ${e.returnedUtc ? fmtClock(e.returnedUtc) : 'still out'}
          <br><span class="muted small">${fmtDuration(end - e.departedUtc)} ·
          max ${fmtDistance(e.maxDistanceM)} · ${Math.round(e.coverageRatio * 100)}% observed</span></span>
      </li>`;
    }).join('')
    : '<li class="empty">No excursions in this window.</li>';

  $$('#excursion-list li.clickable').forEach((li) => li.addEventListener('click', () => {
    const inRange = state.historyFixes.filter(
      (f) => f.timestampUtc >= Number(li.dataset.from) && f.timestampUtc <= Number(li.dataset.to));
    fitTrack(map, inRange);
  }));
}

async function renderHeatmap(from, to) {
  const map = state.maps.history;
  if (state.layers.heat) { map.removeLayer(state.layers.heat); state.layers.heat = null; }
  if (!$('#toggle-heatmap').checked) return;

  const cells = await api(`/api/stats/heatmap?tagId=${state.tagId}&from=${from}&to=${to}&cell=25`);
  if (!cells.length) return;

  const max = Math.max(...cells.map((c) => c.dwellMs));
  const group = L.layerGroup().addTo(map);

  for (const cell of cells) {
    const weight = Math.sqrt(cell.dwellMs / max); // sqrt so the long tail stays visible
    L.circleMarker([cell.lat, cell.lon], {
      radius: 6 + weight * 13,
      stroke: false,
      fillColor: weight > 0.66 ? '#d33a4a' : weight > 0.33 ? '#b8730a' : '#2f8fed',
      fillOpacity: 0.15 + weight * 0.45,
    }).bindTooltip(`${fmtDuration(cell.dwellMs)} here`).addTo(group);
  }

  state.layers.heat = group;
}

function renderPlayback(fixes) {
  const wrap = $('#playback');
  const range = $('#playback-range');
  const on = $('#toggle-playback').checked && fixes.length > 1;

  wrap.hidden = !on;
  if (state.layers.ghost) { state.maps.history.removeLayer(state.layers.ghost); state.layers.ghost = null; }
  if (!on) return;

  range.max = String(fixes.length - 1);
  range.value = String(fixes.length - 1);

  const marker = L.marker([fixes.at(-1).latitude, fixes.at(-1).longitude], { icon: catIcon() })
    .addTo(state.maps.history);
  state.layers.ghost = marker;

  const update = () => {
    const fix = fixes[Number(range.value)];
    marker.setLatLng([fix.latitude, fix.longitude]);
    $('#playback-label').textContent = fmtTime(fix.timestampUtc);
  };

  range.oninput = update;
  update();
}

// ---- timeline ----------------------------------------------------------------------------------

$$('#timeline-chips .chip').forEach((chip) => chip.addEventListener('click', () => {
  $$('#timeline-chips .chip').forEach((c) => c.classList.toggle('is-active', c === chip));
  state.timelineDays = Number(chip.dataset.days);
  loadTimeline();
}));

async function loadTimeline() {
  if (state.tagId === null) return;

  const to = Date.now();
  const from = to - state.timelineDays * 86400000;

  const [events, excursions, alerts, zones] = await Promise.all([
    api(`/api/events?tagId=${state.tagId}&limit=400`),
    api(`/api/excursions?tagId=${state.tagId}&from=${from}&to=${to}`),
    api('/api/alerts?limit=200'),
    api('/api/zones'),
  ]);

  const zoneName = (id) => zones.find((z) => z.id === id)?.name ?? `zone ${id}`;
  const items = [];

  for (const event of events) {
    if (event.occurredUtc < from) continue;
    items.push({
      at: event.occurredUtc,
      kind: event.eventType === 'Enter' ? 'enter' : 'exit',
      title: event.eventType === 'Enter' ? `Came back to ${zoneName(event.zoneId)}` : `Left ${zoneName(event.zoneId)}`,
      body: '',
    });
  }

  for (const excursion of excursions) {
    if (!excursion.returnedUtc) continue;
    items.push({
      at: excursion.returnedUtc,
      kind: 'trip',
      title: `Trip finished — ${fmtDuration(excursion.returnedUtc - excursion.departedUtc)} out`,
      body: `Furthest ${fmtDistance(excursion.maxDistanceM)} from home · ${excursion.fixCount} fixes · ` +
            `${Math.round(excursion.coverageRatio * 100)}% of it observed`,
    });
  }

  for (const alert of alerts) {
    if (alert.raisedUtc < from) continue;
    if (alert.kind === 'ZoneEnter' || alert.kind === 'ZoneExit') continue; // already covered above
    items.push({ at: alert.raisedUtc, kind: 'alert', title: alert.message, body: alert.kind });
  }

  items.sort((a, b) => b.at - a.at);

  const host = $('#timeline');
  if (!items.length) {
    host.innerHTML = '<p class="muted small">Nothing recorded in this window yet.</p>';
  } else {
    let lastDay = '';
    host.innerHTML = items.map((item) => {
      const day = new Date(item.at).toDateString();
      const separator = day === lastDay ? '' : `<div class="day-sep">${fmtDay(item.at)}</div>`;
      lastDay = day;

      return `${separator}
        <div class="tl-item ${item.kind}">
          <div class="tl-title">${esc(item.title)} <span class="tl-time">${fmtClock(item.at)}</span></div>
          ${item.body ? `<div class="tl-body">${esc(item.body)}</div>` : ''}
        </div>`;
    }).join('');
  }

  const longest = excursions
    .filter((e) => e.returnedUtc)
    .sort((a, b) => (b.returnedUtc - b.departedUtc) - (a.returnedUtc - a.departedUtc))
    .slice(0, 8);

  $('#top-trips').innerHTML = longest.length
    ? longest.map((e) => `<li>
        <span>${fmtTime(e.departedUtc)}<br>
          <span class="muted small">max ${fmtDistance(e.maxDistanceM)} ·
          ${Math.round(e.coverageRatio * 100)}% observed</span></span>
        <span class="when">${fmtDuration(e.returnedUtc - e.departedUtc)}</span></li>`).join('')
    : '<li class="empty">No finished trips yet.</li>';
}

// ---- stats -------------------------------------------------------------------------------------

const loading = (host, what) => {
  $(host).innerHTML = `<li class="empty">Working out ${what}…</li>`;
};

async function loadStats() {
  if (state.tagId === null) return;

  // Clustering a fortnight of fixes takes a moment. Say so, rather than leaving an empty card
  // that is indistinguishable from "there is nothing to show".
  loading('#cluster-list', 'her favourite spots');
  $('#roaming').innerHTML = '<p class="muted small">Loading…</p>';

  const [daily, rhythm] = await Promise.all([
    api(`/api/stats/daily?tagId=${state.tagId}&days=14`),
    api(`/api/stats/rhythm?tagId=${state.tagId}&days=60`),
  ]);

  state.charts = { daily, rhythm };
  drawCharts();

  const to = Date.now();
  const from = to - 14 * 86400000;

  try {
    const roaming = await api(`/api/stats/roaming?tagId=${state.tagId}&from=${from}&to=${to}`);
    $('#roaming').innerHTML = `
      <div class="kpi warn"><div class="v">${fmtDistance(roaming.roaming.maxDistanceM)}</div>
        <div class="l">Furthest from home</div></div>
      <div class="kpi info"><div class="v">${fmtDistance(roaming.roaming.p95DistanceM)}</div>
        <div class="l">95th percentile</div></div>
      <div class="kpi"><div class="v">${fmtDistance(roaming.roaming.meanDistanceM)}</div>
        <div class="l">Average</div></div>
      <div class="kpi ${roaming.coverage < 0.5 ? 'warn' : 'good'}">
        <div class="v">${Math.round(roaming.coverage * 100)}%</div>
        <div class="l">Of 14 days observed</div></div>`;
  } catch (error) {
    $('#roaming').innerHTML = `<p class="muted small">${esc(error.message)}</p>`;
  }

  const clusters = await api(`/api/stats/clusters?tagId=${state.tagId}&from=${from}&to=${to}`);
  $('#cluster-list').innerHTML = clusters.length
    ? clusters.slice(0, 10).map((c, i) => `<li>
        <span>#${i + 1} <span class="mono muted">${c.lat.toFixed(5)}, ${c.lon.toFixed(5)}</span><br>
          <span class="muted small">${c.fixCount} fixes within ${Math.round(c.radiusM)} m</span></span>
        <span class="when">${fmtDuration(c.dwellMs)}</span></li>`).join('')
    : '<li class="empty">Not enough data yet.</li>';
}

/**
 * Charts are drawn at the container's real pixel width rather than a fixed viewBox that the
 * browser then scales. A 640-wide viewBox in a 340px card renders at 47%, which turns 10px axis
 * labels into illegible 4.7px ones and squashes the plot to a third of its intended height.
 */
function drawCharts() {
  if (!state.charts) return;

  const width = (selector) => Math.max(320, Math.round($(selector).clientWidth || 640));

  $('#chart-daily').innerHTML = dailyChart(state.charts.daily, width('#chart-daily'));
  $('#chart-rhythm').innerHTML = rhythmChart(state.charts.rhythm, width('#chart-rhythm'));
}

// Re-draw at the new width when the window changes shape, so the charts stay 1:1.
let resizeTimer = null;
window.addEventListener('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    if ($('#panel-stats').classList.contains('is-active')) drawCharts();
  }, 200);
});

/**
 * Time outdoors per day. The pale bar is the upper bound including unobserved gaps; the solid bar
 * is what we actually saw. Drawing only one of them would be a lie in either direction.
 */
function dailyChart(days, W = 640) {
  if (!days.length) return '<p class="muted small">No excursions recorded yet.</p>';

  const H = Math.round(Math.min(340, Math.max(210, W * 0.34)));
  const pad = { l: 38, r: 10, t: 14, b: 28 };
  const maxMs = Math.max(...days.map((d) => d.upperBoundOutdoorMs), 3600000);
  const bw = (W - pad.l - pad.r) / days.length;
  const y = (ms) => H - pad.b - (ms / maxMs) * (H - pad.t - pad.b);

  const gridlines = [];
  const stepHours = Math.max(1, Math.ceil(maxMs / 3600000 / 4));
  for (let h = 0; h <= maxMs / 3600000; h += stepHours) {
    const yy = y(h * 3600000);
    gridlines.push(`<line x1="${pad.l}" x2="${W - pad.r}" y1="${yy}" y2="${yy}"
      stroke="currentColor" opacity=".12"/><text x="4" y="${yy + 3}">${h}h</text>`);
  }

  const bars = days.map((d, i) => {
    const x = pad.l + i * bw + bw * 0.16;
    const w = bw * 0.68;
    const label = i % Math.ceil(days.length / 7) === 0
      ? `<text x="${x + w / 2}" y="${H - 8}" text-anchor="middle">${d.date.slice(5)}</text>` : '';

    return `<g>
      <title>${d.date}: ${fmtShort(d.observedOutdoorMs)} observed, up to ${fmtShort(d.upperBoundOutdoorMs)} · ${d.excursionCount} trip(s)</title>
      <rect x="${x}" y="${y(d.upperBoundOutdoorMs)}" width="${w}"
            height="${Math.max(0, H - pad.b - y(d.upperBoundOutdoorMs))}" fill="url(#gpale)" rx="3"/>
      <rect x="${x}" y="${y(d.observedOutdoorMs)}" width="${w}"
            height="${Math.max(0, H - pad.b - y(d.observedOutdoorMs))}" fill="url(#gsolid)" rx="3"/>
      ${label}</g>`;
  });

  return `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Time outdoors per day">
    <defs>
      <linearGradient id="gsolid" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stop-color="#8b7bff"/><stop offset="100%" stop-color="#59a6ff"/>
      </linearGradient>
      <linearGradient id="gpale" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stop-color="#8b7bff" stop-opacity=".38"/>
        <stop offset="100%" stop-color="#59a6ff" stop-opacity=".16"/>
      </linearGradient>
    </defs>
    ${gridlines.join('')}${bars.join('')}</svg>`;
}

function rhythmChart(rhythm, W = 640) {
  const total = rhythm.departures.reduce((a, b) => a + b, 0) + rhythm.returns.reduce((a, b) => a + b, 0);
  if (!total) return '<p class="muted small">Not enough excursions yet.</p>';

  const H = Math.round(Math.min(280, Math.max(180, W * 0.27)));
  const pad = { l: 26, r: 10, t: 14, b: 26 };
  const max = Math.max(...rhythm.departures, ...rhythm.returns, 1);
  const bw = (W - pad.l - pad.r) / 24;
  const h = (n) => (n / max) * (H - pad.t - pad.b);

  const bars = [];
  for (let hour = 0; hour < 24; hour++) {
    const x = pad.l + hour * bw;
    const dh = h(rhythm.departures[hour]);
    const rh = h(rhythm.returns[hour]);

    bars.push(`<g><title>${hour}:00 — ${rhythm.departures[hour]} out, ${rhythm.returns[hour]} home</title>
      <rect x="${x + bw * 0.1}" y="${H - pad.b - dh}" width="${bw * 0.36}" height="${dh}" fill="#e9a93c" rx="2"/>
      <rect x="${x + bw * 0.52}" y="${H - pad.b - rh}" width="${bw * 0.36}" height="${rh}" fill="#59a6ff" rx="2"/>
      ${hour % 3 === 0 ? `<text x="${x + bw / 2}" y="${H - 8}" text-anchor="middle">${hour}</text>` : ''}
    </g>`);
  }

  return `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Departures and returns by hour">
    <line x1="${pad.l}" x2="${W - pad.r}" y1="${H - pad.b}" y2="${H - pad.b}" stroke="currentColor" opacity=".2"/>
    ${bars.join('')}</svg>`;
}

// ---- health ------------------------------------------------------------------------------------

const checkRow = (tone, label, detail) => `
  <div class="check-row">
    <span class="dot ${tone}"></span>
    <div class="body"><div class="label">${esc(label)}</div><div class="detail">${esc(detail)}</div></div>
  </div>`;

async function loadHealth() {
  const [status, health, tiles] = await Promise.all([
    api('/api/status'), api('/api/health'), api('/api/tiles/status'),
  ]);

  const tag = status.tags.find((t) => t.id === state.tagId) ?? status.tags[0];
  const rows = [];

  const pollAge = status.lastPollUtc ? status.nowUtc - status.lastPollUtc : null;
  rows.push(checkRow(
    pollAge != null && pollAge < 120000 ? 'ok' : 'bad',
    'Collector',
    pollAge == null ? "Has not polled yet." : `Last poll ${fmtAgo(pollAge)}.`));

  rows.push(checkRow('ok', 'Position source', status.source));

  const beat = status.heartbeat;
  rows.push(checkRow(
    !beat ? 'bad' : beat.status === 'ok' ? 'ok' : 'warn',
    'Reader agent',
    !beat
      ? 'No heartbeat. On the Mac: launchctl kickstart -k gui/$UID/nl.brandsen.cattracker.reader'
      : `${beat.status}${beat.detail ? ` — ${beat.detail}` : ""} (${fmtAgo(status.nowUtc - beat.writtenUtcMs)})`));

  // Silence is the failure mode that matters: a dead reader and a sleeping cat look identical.
  rows.push(checkRow(
    status.isStale ? 'bad' : tag?.ageMs == null ? 'warn' : 'ok',
    'Position freshness',
    tag?.ageMs == null ? 'No positions yet.'
      : status.isStale ? `Stale — last fix ${fmtAgo(tag.ageMs)}. ${describeHeartbeat(status)}`
        : `Last fix ${fmtAgo(tag.ageMs)}.`));

  rows.push(checkRow(
    status.home ? 'ok' : 'warn',
    'Home zone',
    status.home
      ? `${status.home.name} · ${Math.round(status.home.radiusM)} m radius, ${Math.round(status.home.exitBufferM)} m buffer`
      : 'Not set. Without one there are no excursions, no away alerts and no statistics.'));

  rows.push(checkRow(
    status.alertChannels.length ? 'ok' : 'warn',
    'Alert channels',
    status.alertChannels.length
      ? status.alertChannels.join(', ')
      : 'None available — expected on Windows; macOS channels appear on the Mac.'));

  rows.push(checkRow(
    (status.warnings ?? []).length ? 'warn' : 'ok',
    'Find My cache',
    (status.warnings ?? []).length ? status.warnings.join(' · ') : 'Parsing cleanly.'));

  rows.push(checkRow(status.error ? 'bad' : 'ok', 'Last poll result',
    status.error ?? 'No errors.'));

  rows.push(checkRow('ok', 'Schema',
    `${health.migrations} migration(s) applied · ${health.schema ?? 'none'}`));

  rows.push(checkRow('ok', 'Version', `CatTracker ${health.version} · ${status.timeZone}`));

  $('#checks').innerHTML = rows.join('');

  const span = tag?.firstFixUtc ? status.nowUtc - tag.firstFixUtc : 0;
  $('#storage-kpis').innerHTML = `
    <div class="kpi info"><div class="v">${(tag?.fixCount ?? 0).toLocaleString()}</div>
      <div class="l">Stored fixes</div></div>
    <div class="kpi"><div class="v">${span ? Math.round(span / 86400000) : 0}d</div>
      <div class="l">History span</div></div>
    <div class="kpi"><div class="v">${tiles.cachedTiles.toLocaleString()}</div>
      <div class="l">Map tiles</div></div>
    <div class="kpi"><div class="v">${fmtBytes(tiles.cachedBytes)}</div>
      <div class="l">Tile cache</div></div>`;

  $('#storage-note').textContent = `Data directory: ${health.dataDirectory}`;

  refreshLogs();
}

// ---- logs --------------------------------------------------------------------------------------

async function refreshLogs() {
  const output = $('#log-output');
  try {
    const params = new URLSearchParams({ lines: $('#log-lines').value || '300' });
    const filter = $('#log-filter').value.trim();
    if (filter) params.set('contains', filter);
    if ($('#log-file').value) params.set('file', $('#log-file').value);

    const page = await api(`/api/logs?${params}`);

    const select = $('#log-file');
    if (select.options.length !== page.files.length) {
      select.innerHTML = page.files.map((f) => `<option value="${esc(f)}">${esc(f)}</option>`).join('');
      select.value = page.file;
    }

    output.innerHTML = page.lines.length
      ? page.lines.map((line) => {
        const level = /\[(VRB|DBG|INF|WRN|ERR|FTL)\]/.exec(line)?.[1] ?? 'INF';
        return `<span class="lvl-${level}">${esc(line)}</span>`;
      }).join('\n')
      : 'No matching lines.';

    // Pin to the newest line, the way tail -f behaves.
    output.scrollTop = output.scrollHeight;
  } catch (error) {
    output.textContent = `Could not read logs: ${error.message}`;
  }
}

$('#log-refresh').addEventListener('click', refreshLogs);
$('#log-filter').addEventListener('change', refreshLogs);
$('#log-file').addEventListener('change', refreshLogs);

$('#log-auto').addEventListener('change', (event) => {
  clearInterval(state.logTimer);
  state.logTimer = event.target.checked ? setInterval(refreshLogs, 4000) : null;
  if (event.target.checked) refreshLogs();
});

// ---- setup -------------------------------------------------------------------------------------

async function loadSetup() {
  const zones = await api('/api/zones');
  state.zones = zones;
  drawZones(state.maps.setup, zones, 'setupZones');

  $('#zone-count').textContent = zones.length;
  $('#zone-list').innerHTML = zones.length
    ? zones.map((z) => `<li>
        <span><strong>${esc(z.name)}</strong> <span class="zone-badge ${z.kind}">${z.kind}</span><br>
          <span class="muted small">${Math.round(z.radiusM)} m + ${Math.round(z.exitBufferM)} m buffer</span></span>
        <span class="actions">
          <button class="btn tiny" data-edit="${z.id}">Edit</button>
          <button class="btn tiny danger" data-delete="${z.id}">✕</button>
        </span></li>`).join('')
    : '<li class="empty">No zones yet. Click the map to place one.</li>';

  $$('#zone-list [data-edit]').forEach((b) => b.addEventListener('click', () => {
    const zone = zones.find((z) => z.id === Number(b.dataset.edit));
    fillZoneForm(zone);
    state.maps.setup.setView([zone.centerLat, zone.centerLon], 17);
  }));

  $$('#zone-list [data-delete]').forEach((b) => b.addEventListener('click', async () => {
    if (!confirm('Delete this zone? Its history of enter and exit events goes with it.')) return;
    await api(`/api/zones/${b.dataset.delete}`, { method: 'DELETE' });
    loadSetup();
  }));

  const tag = currentTag();
  if (tag) $('#tag-name').value = tag.petName;

  refreshTileStatus();
}

function fillZoneForm(zone) {
  state.editingZoneId = zone?.id ?? null;
  $('#zone-id').value = zone?.id ?? '';
  $('#zone-name').value = zone?.name ?? '';
  $('#zone-kind').value = zone?.kind ?? 'Home';
  $('#zone-lat').value = zone?.centerLat ?? '';
  $('#zone-lon').value = zone?.centerLon ?? '';
  $('#zone-radius').value = zone?.radiusM ?? 30;
  $('#zone-buffer').value = zone?.exitBufferM ?? 25;
  $('#zone-notify-exit').checked = zone?.notifyOnExit ?? true;
  $('#zone-notify-enter').checked = zone?.notifyOnEnter ?? true;
}

$('#zone-reset').addEventListener('click', () => fillZoneForm(null));

$('#zone-form').addEventListener('submit', async (event) => {
  event.preventDefault();

  const body = {
    name: $('#zone-name').value,
    kind: $('#zone-kind').value,
    centerLat: Number($('#zone-lat').value),
    centerLon: Number($('#zone-lon').value),
    radiusM: Number($('#zone-radius').value),
    exitBufferM: Number($('#zone-buffer').value),
    notifyOnExit: $('#zone-notify-exit').checked,
    notifyOnEnter: $('#zone-notify-enter').checked,
  };

  try {
    if (state.editingZoneId) await send(`/api/zones/${state.editingZoneId}`, body, 'PUT');
    else await send('/api/zones', body);
    fillZoneForm(null);
    loadSetup();
    refreshStatus();
  } catch (error) {
    alert(`Could not save zone: ${error.message}`);
  }
});

$('#tag-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  await send(`/api/tags/${state.tagId}`, { petName: $('#tag-name').value }, 'PATCH');
  refreshStatus();
});

$('#test-alert').addEventListener('click', async () => {
  const status = $('#test-alert-status');
  status.textContent = 'Sending…';
  try {
    const result = await api('/api/alerts/test', { method: 'POST' });
    status.textContent = result.channels.length
      ? `Sent via: ${result.channels.join(', ')}`
      : 'No channels available on this machine (expected on Windows — the macOS ones appear on the Mac).';
  } catch (error) {
    status.textContent = error.message;
  }
});

$('#seed-btn').addEventListener('click', async () => {
  const bounds = state.maps.setup.getBounds();
  const body = {
    minLat: bounds.getSouth(), minLon: bounds.getWest(),
    maxLat: bounds.getNorth(), maxLon: bounds.getEast(),
    minZoom: Number($('#seed-minz').value), maxZoom: Number($('#seed-maxz').value),
  };

  try {
    const result = await send('/api/tiles/seed', body);
    $('#seed-status').textContent = `Queued ${result.planned} tiles (cap ${result.cap})…`;
    const timer = setInterval(async () => {
      if (await refreshTileStatus()) clearInterval(timer);
    }, 1500);
  } catch (error) {
    $('#seed-status').textContent = error.message;
  }
});

async function refreshTileStatus() {
  const status = await api('/api/tiles/status');
  $('#seed-status').textContent =
    `${status.cachedTiles} tiles cached (${fmtBytes(status.cachedBytes)}). ${status.seeding.message}`;
  return !status.seeding.running;
}

// ---- boot --------------------------------------------------------------------------------------

async function boot() {
  const status = await api('/api/status').catch(() => null);
  const home = status?.home;
  const centre = home ? [home.centerLat, home.centerLon]
    : status?.tags?.[0]?.latestFix
      ? [status.tags[0].latestFix.latitude, status.tags[0].latestFix.longitude]
      : [52.0907, 5.1214];

  state.maps.live = makeMap('map', centre, 17);
  state.maps.history = makeMap('history-map', centre, 16);
  state.maps.setup = makeMap('setup-map', centre, 17);

  state.maps.setup.on('click', (event) => {
    $('#zone-lat').value = event.latlng.lat.toFixed(6);
    $('#zone-lon').value = event.latlng.lng.toFixed(6);
    if (!$('#zone-name').value) $('#zone-name').value = 'Home';
  });

  await refreshStatus();
  setInterval(refreshStatus, 10000);
}

boot();
