'use strict';
// The home screen: its top bar, the tiles (in place, sliding when they move), its hints.
// Part of the page's script: app.js and app\*.js, loaded in index.html's order, share their globals.

function renderStatus() {
  const now = new Date();
  const low = state.battery === 'low' || state.battery === 'empty';
  // In place (patchHtml): each minute and each host push redraw it, and the Settings and Power
  // buttons drawn afresh lost their ring and drew it in again.
  patchHtml($('status'),
    `<div class="clock"><span class="time">${timeText(now)}</span><span class="date">${esc(dateText(now))}</span></div>` +
    '<div class="pills">' +
      (state.timer ? `<div class="pill timer">${icon('timer', 28, 2)}<span>${esc(timerText())}</span></div>` : '') +
      noticePillsHtml() + // alerts (notices.js)
      `<div class="pill"${low ? ' style="color: var(--warn)"' : ''}>${icon('controller', 32)}<b>${esc(batteryText())}</b></div>` +
      // Settings, then Power: the Home menu's quick buttons follow this order (renderMenu).
      `<div class="round" data-nav data-id="settings" data-act="settings" aria-label="Settings">${icon('sliders', 28, 2)}</div>` +
      `<div class="round" data-nav data-id="power" data-act="power" aria-label="Power">${icon('power', 28, 2)}</div>` +
    '</div>');
}

// Apps asked to close and not gone yet (id -> a timer that gives up saying so after 20 s).
const closing = new Map();
function markClosing(id) {
  clearTimeout(closing.get(id));
  closing.set(id, setTimeout(() => doneClosing(id), 20000));
  render();
}
function doneClosing(id) {
  clearTimeout(closing.get(id));
  if (closing.delete(id)) render();
}

// Tiles are updated in place, by id: a change on one tile (an app closing, a rename) redraws
// that tile only. Redrawing them all replayed every tile's entrance and flashed the home screen.
// Their order is CSS order, so a tile that moves (Tile options > Move) never leaves the page
// either: it slides to its new place.
function renderTiles() {
  const want = state.tiles.map((t) => ({
    id: `tile:${t.id}`, cls: state.moving === t.id ? 'tile moving' : 'tile', act: 'launch', arg: t.id,
    html: (closing.has(t.id) ? '<span class="badge closing">Closing…</span>' : t.running ? '<span class="badge">Running</span>' : '') +
      appIcon(t, 88) + `<span class="name">${esc(t.name)}</span>`,
  }));
  for (const f of EXT.tiles) want.push(...f());   // apps being installed (library.js)
  // The "+" tile is always last (SPEC decision): A opens the library / add-tile screen. While a
  // tile moves it stays, dimmed, so the grid keeps its shape.
  want.push({ id: 'tile:+add', cls: `tile add${state.moving ? ' dim' : ''}`, act: 'addtile', label: 'Add tile',
    html: `<span style="display:flex">${icon('plus', 80, 2)}</span><span class="name">Add tile</span>` });
  const box = $('tiles');
  const old = new Map([...box.children].filter((e) => e.classList.contains('tile')).map((e) => [e.dataset.id, e]));
  const before = new Map([...old.values()].map((e) => [e, e.getBoundingClientRect()]));
  want.forEach((w, i) => {
    let el = old.get(w.id);
    old.delete(w.id);
    if (!el) { el = document.createElement('div'); el.setAttribute('data-nav', ''); el.dataset.id = w.id; box.appendChild(el); }
    const cls = w.cls + (el.classList.contains('focused') ? ' focused' : '');
    if (el.className !== cls) el.className = cls;
    el.dataset.act = w.act;
    for (const [k, v] of [['arg', w.arg], ['x', w.x]]) if (v) el.dataset[k] = v; else delete el.dataset[k];
    if (w.label) el.setAttribute('aria-label', w.label); else el.removeAttribute('aria-label');
    if (el.tileHtml !== w.html) { el.innerHTML = w.html; el.tileHtml = w.html; }
    el.tileHints = w.hints || null;
    el.style.order = i;
  });
  for (const el of old.values()) el.remove();
  slideTiles(before);
  updateHomeHints();
}

// More tiles than fit: the grid scrolls by whole rows, the room kept round the focused row being
// the grid's padding (app.css .tiles-wrap), so a row is never cut. By layout offsets, not the
// screen: a tile still sliding (move mode) counts where it lands.
function keepTileInView(el) {
  const wrap = el.closest('.tiles-wrap');
  if (!wrap) return;
  const row = el.closest('#tiles > *') || el;
  const room = parseFloat(getComputedStyle(wrap).paddingTop);
  const top = row.offsetTop - room, bottom = row.offsetTop + row.offsetHeight + room;
  if (top < wrap.scrollTop) wrap.scrollTop = top;
  else if (bottom > wrap.scrollTop + wrap.clientHeight) wrap.scrollTop = bottom - wrap.clientHeight;
}

// Tiles whose place changed slide there from where they were (FLIP), instead of jumping. Not
// while the home screen is hidden (nothing to see, no size).
function slideTiles(before) {
  const scale = $('stage').getBoundingClientRect().width / 1920 || 1;
  for (const [el, r] of before) {
    if (!el.isConnected || !r.width) continue;
    const q = el.getBoundingClientRect();
    const dx = (r.left - q.left) / scale, dy = (r.top - q.top) / scale;
    if (!q.width || (Math.abs(dx) < 1 && Math.abs(dy) < 1)) continue;
    el.style.transition = 'none';
    el.style.translate = `${dx}px ${dy}px`;
    el.getBoundingClientRect();   // drawn at the old place once, then eased to the new
    el.style.transition = '';
    el.style.translate = '';
  }
}

// Hints change with the focused tile and with move mode.
function updateHomeHints() {
  if (state.moving) {
    setHomeHints(hints([['D-pad', 'Move it'], ['A', 'Drop it here'], ['B', 'Cancel']]));
    return;
  }
  const f = $('home').querySelector('[data-nav].focused');
  const isAdd = f && f.dataset.act === 'addtile';
  const t = f && state.tiles.find((x) => x.id === f.dataset.arg);
  // Not a tile: the Settings and Power buttons of the top bar, the phone card's "Not now".
  const other = f && !f.classList.contains('tile') ? ({ settings: 'Settings', power: 'Power', 'phone-card-hide': 'Not now' })[f.dataset.act] || 'Select' : null;
  const list = other ? [['A', other], ['Home', 'Menu'], ['Hold Home', 'Power']]
    : f && f.tileHints ? [...f.tileHints, ['Home', 'Menu'], ['Hold Home', 'Power']]
    : isAdd ? [['A', 'Add tile'], ['Home', 'Menu'], ['Hold Home', 'Power']]
    : [['A', 'Open'], ['Hold A', 'Move'], ...(t && t.running ? [['X', 'Close app']] : []), ['Start', 'Tile options'], ['Home', 'Menu'], ['Hold Home', 'Power']];
  setHomeHints(hints(list));
}

// The bar is drawn again only when it says something else: from one tile to the next it mostly
// says the same, and each redraw laid it out and drew it again at 4K for nothing.
function setHomeHints(html) {
  const bar = $('home-hints');
  if (bar.hintsHtml !== html) { bar.innerHTML = html; bar.hintsHtml = html; }
}
