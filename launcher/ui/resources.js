'use strict';
// The Home menu's resource view: the box's CPU, memory, disk and network use, and the three
// programs using the most, in the menu's column under its sliders and quick buttons (the owner,
// 29 Sept 2026: there, not in a card on the right). The host samples only while the page shows
// the menu (MainForm.Resources.cs, ResourceWatch.cs): the menu opens as fast as ever, the view
// comes with the first numbers a moment later, and every 2 s only its numbers change
// (patchHtml), never the menu around it. Down from the quick buttons goes to its programs, up
// from them back (app.js move, as anywhere in the column).
// A (or X) on a program's row asks, then stops it: an app is closed as its tile's X closes it,
// another program ended. Windows' own and the launcher's rows show, but never take the focus.
// While the focus is on a row, the rows stay where they are (their numbers still change): what
// A stops is what the focus is on.
//   To the host:   res.watch {on, hold: [keys of the rows kept in place]}  res.stop {key}
//   From the host: res.data {cpu (%), memUsed, memTotal (MB), disk (bytes/s), down, up (bits/s),
//                  top: [{key, name, app, cpu, mem (MB), stop}], held: [{...} or {key, gone}]}

const res = {
  on: false,     // the host was told the menu is on screen
  data: null,    // its last res.data (none yet: no view)
  hold: null,    // the rows' keys kept in place while the focus is on one of them
  rows: [],      // the rows shown
};

// 0%, 0.4%, 9.7%, 18%, 100%: a decimal only where it says something.
function resPercent(v) { return v < 0.05 ? '0%' : v < 9.95 ? `${v.toFixed(1)}%` : `${Math.round(v)}%`; }

// A program's memory, from MB: 356 MB, 1.8 GB, 12.1 GB.
function resMemory(mb) { return mb < 999.5 ? `${Math.round(mb)} MB` : `${(mb / 1024).toFixed(1)} GB`; }

// A rate per second, in bytes ('B': the disk) or bits ('b': the network), 1000 to the k:
// [number, unit], null when the host has none (no disk counters, no network adapter up).
function resRate(v, unit) {
  if (v === null || v === undefined) return null;
  const [k, m, g] = unit === 'B' ? ['KB/s', 'MB/s', 'GB/s'] : ['kb/s', 'Mb/s', 'Gb/s'];
  if (v < 999.5e3) return [String(Math.round(v / 1e3)), k];
  if (v < 999.5e6) { const n = v / 1e6; return [n < 9.95 ? n.toFixed(1) : String(Math.round(n)), m]; }
  return [(v / 1e9).toFixed(1), g];
}

// The rows to show: the host's top three, or while the focus is on one of them the same rows in
// the same order, with their new numbers (in top, or held when they left it: gone once ended).
// A row the host has not heard is held yet keeps its last numbers.
function resRows() {
  const d = res.data;
  if (!d) return [];
  if (!res.hold) return d.top.slice(0, 3);
  const fresh = [...d.top, ...(d.held || [])];
  return res.hold.map((key) => {
    const was = res.rows.find((r) => r.key === key);
    const now = fresh.find((r) => r.key === key);
    if (now && now.gone) return was && { ...was, gone: true, stop: false };
    return now || was;
  }).filter(Boolean);
}

// A label and its number on one line, a bar or a second line under them.
function resMeter(label, value, detail, high) {
  return `<div class="rs-meter${high ? ' high' : ''}"><div class="rs-top"><span class="rs-label">${label}</span>` +
    `<span class="rs-value">${value}</span></div>${detail}</div>`;
}

function resBar(percent) {
  return `<div class="track"><div class="fill" style="width:${percent === null ? 0 : Math.min(100, Math.max(0, percent)).toFixed(1)}%"></div></div>`;
}

// A number and its unit, the unit smaller; a dash when the host has none (no disk counters).
function resAmount(pair) { return pair ? `${esc(pair[0])}<small> ${esc(pair[1])}</small>` : '–'; }

function resRowHtml(r) {
  const tile = r.app && state.tiles.find((t) => t.id === r.app);
  // The launcher itself, Windows' own programs, any other program: a glyph of their own.
  const glyph = r.key === 'self' ? 'tv' : r.key.startsWith('win:') ? 'desktop' : 'app';
  const pic = tile ? appIcon(tile, 28) : `<span class="appicon rs-glyph">${icon(glyph, 28)}</span>`;
  const tag = r.gone ? '<span class="tag">Ended</span>' : r.app && closing.has(r.app) ? '<span class="tag">Closing…</span>' : '';
  const numbers = r.gone ? '' : `<span class="rs-cpu">${resPercent(r.cpu)}</span><span class="rs-mem">${resMemory(r.mem)}</span>`;
  // Only what may be stopped takes the focus (a held row that ended keeps it until the focus leaves).
  const nav = r.stop || r.gone ? ` data-nav data-act="res-stop" data-x="res-stop" data-arg="${esc(r.key)}" data-res="${esc(r.key)}"` : '';
  return `<div class="row rs-row${nav ? '' : ' fixed'}" data-id="res:${esc(r.key)}"${nav}>${pic}` +
    `<span class="grow">${esc(r.name)}</span>${tag}${numbers}</div>`;
}

// The view (app.js's renderMenu draws it into #menu-res, at the bottom of the menu's column; the
// host's numbers patch it in place). None until the first numbers: the menu's first frame is
// what it was without it, and the view fades in with them, a moment after, under the rest.
function resViewHtml() {
  const d = res.data;
  res.rows = resRows();
  if (!d) return '';
  const memory = d.memTotal ? d.memUsed / d.memTotal * 100 : null;
  const down = resRate(d.down, 'b'), up = resRate(d.up, 'b');
  return '<div class="rs-view"><span class="section">System</span><div class="rs-meters">' +
      resMeter('CPU', `${Math.round(d.cpu)}<small>%</small>`, resBar(d.cpu), d.cpu >= 85) +
      resMeter('Memory', `${(d.memUsed / 1024).toFixed(1)}<small> / ${(d.memTotal / 1024).toFixed(1)} GB</small>`, resBar(memory), memory >= 90) +
      resMeter('Disk', resAmount(resRate(d.disk, 'B')), '<span class="rs-sub">read and written</span>') +
      resMeter('Network', down ? `↓ ${resAmount(down)}` : '–', `<span class="rs-sub">${up ? `↑ ${esc(up[0])} ${esc(up[1])}` : '&nbsp;'}</span>`) +
    '</div>' +
    '<span class="section">Using the most</span>' +
    `<div class="rs-rows">${res.rows.length ? res.rows.map(resRowHtml).join('') : '<span class="empty">Nothing running</span>'}</div>` +
  '</div>';
}

// The view again with what is known now (the host's numbers, the focus leaving the rows), and
// the hints if the focus is on a row that changed (one that ended: A no longer stops it).
function resPatch() {
  if (!res.on || !$('menu-res')) return;
  patchHtml($('menu-res'), resViewHtml());
  const f = state.view === 'menu' ? focusedEl() : null;
  const bar = $('menu-panel').querySelector('footer.hints');
  if (f && f.dataset.res && bar) patchHtml(bar, hints(menuHints(f)));
}

function resTell() { send({ type: 'res.watch', on: res.on, hold: res.hold || [] }); }

// What is on screen changed (app.js sectionHooks: each render, the stage blank or back): the
// host samples while the Home menu shows (under a question too: "End it?"), never while the
// launcher is blank (behind an app, standby). Gone, its numbers go too: the next opening shows
// no view until fresh ones come, never those of minutes ago.
function resourcesInView(unders) {
  const on = !$('stage').classList.contains('blank') && (state.view === 'menu' || unders.includes('menu'));
  if (on === res.on) return;
  res.on = on;
  if (!on) { res.data = null; res.hold = null; res.rows = []; }
  resTell();
  if (on && !host) resDemo();
}

// The focus moved in the Home menu (app.js setFocus): onto the rows, they stay where they are;
// off them, they follow the host's ranking again, at once.
function resFocus(el) {
  const hold = el && el.closest('#menu-res') ? res.rows.map((r) => r.key) : null;
  if (JSON.stringify(hold) === JSON.stringify(res.hold)) return;
  res.hold = hold;
  if (!hold) resPatch();
  if (res.on) resTell();
}

// The Home menu's hints on a row (app.js menuHints).
function resHints(el) {
  const r = res.rows.find((x) => x.key === el.dataset.res);
  return r && r.stop && !r.gone ? [['A', r.app ? 'Close app' : 'End program'], ['B', 'Back']] : [['B', 'Back']];
}

onAction('res-stop', (el, key) => {
  const r = res.rows.find((x) => x.key === key);
  if (!r) return;
  if (r.gone) { toast(`${r.name} has ended`); return; }
  if (!r.stop) return;
  if (r.app) {
    ask({ title: `Close ${r.name}?`, text: 'It stops, and anything playing in it ends.', yes: 'Close', onYes: () => {
      send({ type: 'res.stop', key: r.key });
      // Its tile and its row in the menu say "Closing…" until the host no longer lists it
      // running, as after X (a copy the host did not start has no tile: the host says).
      if (state.tiles.some((t) => t.id === r.app && t.running)) { markClosing(r.app); toast(`Closing ${r.name}…`); }
    } });
  } else {
    // The host says how it went (ended, or Windows denied access).
    ask({ title: `End ${r.name}?`, text: 'Its processes are ended at once: anything not saved in it is lost.', yes: 'End',
      onYes: () => send({ type: 'res.stop', key: r.key }) });
  }
});

hostMessage('res.data', (msg) => {
  if (!res.on) return;   // late: the menu has gone
  res.data = msg;
  resPatch();
});

// ---- Demo (a plain browser): the numbers as big as a busy box's, the names as long ----------------

const RES_DEMO = {
  type: 'res.data', cpu: 100, memUsed: 32570, memTotal: 32684, disk: 845300000, down: 912400000, up: 848200000,
  top: [
    { key: 'app:jellyfin', name: 'Jellyfin', app: 'jellyfin', cpu: 71.2, mem: 1843, stop: true },
    { key: 'exe:vendorupdatehelperservice-x64-setup.exe', name: 'VendorUpdateHelperService-x64-Setup.exe', app: null, cpu: 18.4, mem: 12406, stop: true },
    { key: 'win:windows-update', name: 'Windows Update', app: null, cpu: 9.7, mem: 356, stop: false },
  ],
  held: [],
};

function resDemo() {
  res.data = JSON.parse(JSON.stringify(RES_DEMO));
  resPatch();
}
