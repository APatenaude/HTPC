'use strict';

// Launcher UI. The C# host (WebView2) sends state and controller input as web messages; the
// UI sends back what the user chose. Without a host (opened in a normal browser) it runs
// on demo data so the look can be checked anywhere; the keyboard stands in for the controller.

const host = window.chrome && window.chrome.webview;
const send = (msg) => host ? host.postMessage(msg) : console.log('to host', msg);

const state = {
  view: 'home',            // home | menu | power | timer
  tiles: [],               // { id, name, glyph, color, running }
  current: null,           // app the menu was opened over (null: over the home screen)
  volume: 50,
  brightness: 100,
  battery: null,           // null: not reported; 'wired', 'full', 'medium', 'low', 'empty'
  controller: false,
  timer: null,             // { label, endsAt (ms) | 'video' }
  alert: null,             // { text, glyph }
  memory: {},              // last focused item per view
  stack: [],               // views to go back to
  backdrop: null,
  moving: null,            // id of the tile being moved (Tile options > Move)
  libraryAvailable: true,  // whether installing from the TV is set up (the host reports it)
  section: 'sleep',        // Settings section shown
  prefs: { idleMinutes: 30, sleepMode: 'standby', sleepAfterStandbyHours: 0, stayAwakeWhilePlaying: true },
  power: { sleep: true, hibernate: true },  // sleep states this PC has (from the host)
  tv: { screen: null, profile: null, found: [], methods: [], profiles: [], caps: {}, status: 'unbound', port: 0 }   // tv.js: the screen's TV, its profile, TVs on the network
};

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// ---- Layout: scale the 1920x1080 stage to the window -------------------------------------

function fit() {
  const s = Math.min(innerWidth / 1920, innerHeight / 1080);
  const stage = $('stage');
  stage.style.transform = `translate(${(innerWidth - 1920 * s) / 2}px, ${(innerHeight - 1080 * s) / 2}px) scale(${s})`;
}
addEventListener('resize', fit);

// ---- Rendering ----------------------------------------------------------------------------

// [button, label] pairs; button can be a list of buttons for one label ([['LB', 'RB'], 'Category']).
function hints(list) {
  return list.map(([btn, label]) =>
    '<div class="hint">' + (Array.isArray(btn) ? btn : [btn]).map((b) => `<span class="key${b.length > 1 ? ' wide' : ''}">${esc(b)}</span>`).join('') +
      `<span>${esc(label)}</span></div>`).join('');
}

// An app's icon: its own logo (from its program or its site, cached by the host: logos.htpc) a
// little larger than a glyph would be, in the glyph's room (what is around it does not move),
// else its glyph in its colour. A logo that does not load gives way to the glyph (both are
// there; the image's error hides it).
function appIcon(a, size, weight) {
  const glyph = icon(a.glyph, size, weight);
  const color = `color:${esc(a.color || 'inherit')}`;
  if (!a.logo) return `<span class="appicon" style="${color}">${glyph}</span>`;
  const px = Math.round(size * 1.25), room = -Math.round((px - size) / 2);
  return `<span class="appicon has-logo" style="${color}"><img src="${esc(a.logo)}" width="${px}" height="${px}" alt="" draggable="false" ` +
    `style="margin:${room}px" onerror="this.parentNode.classList.remove('has-logo')">${glyph}</span>`;
}

function timeText(d) { return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }); }
function dateText(d) { return d.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' }); }

function timerText() {
  if (!state.timer) return '';
  // "When this video ends": nothing played yet, or the video's minutes left if its app says.
  if (state.timer.endsAt === 'video') {
    if (state.timer.waiting) return 'Sleep after the next video';
    return state.timer.minutesLeft ? `Sleep after this video · ${state.timer.minutesLeft} min` : 'Sleep after this video';
  }
  const min = Math.max(0, Math.ceil((state.timer.endsAt - Date.now()) / 60000));
  return `Sleep in ${min} min`;
}

function batteryText() {
  if (!state.controller) return 'No controller';
  return ({ full: 'Full', medium: 'Medium', low: 'Low', empty: 'Empty' })[state.battery] || 'Connected';
}

function renderStatus() {
  const now = new Date();
  const low = state.battery === 'low' || state.battery === 'empty';
  $('status').innerHTML =
    `<div class="clock"><span class="time">${timeText(now)}</span><span class="date">${esc(dateText(now))}</span></div>` +
    '<div class="pills">' +
      (state.timer ? `<div class="pill timer">${icon('timer', 28, 2)}<span>${esc(timerText())}</span></div>` : '') +
      noticePillsHtml() + // alerts (notices.js)
      `<div class="pill"${low ? ' style="color: var(--warn)"' : ''}>${icon('controller', 32)}<b>${esc(batteryText())}</b></div>` +
      `<div class="round" data-nav data-id="settings" data-act="settings" aria-label="Settings">${icon('sliders', 28, 2)}</div>` +
      `<div class="round" data-nav data-id="power" data-act="power" aria-label="Power">${icon('power', 28, 2)}</div>` +
    '</div>';
}

// Tiles are updated in place, by id: a change on one tile (an app closing, a rename) redraws
// that tile only. Redrawing them all replayed every tile's entrance and flashed the home screen.
// Their order is CSS order, so a tile that moves (Tile options > Move) never leaves the page
// either: it slides to its new place.
function renderTiles() {
  const want = state.tiles.map((t) => ({
    id: `tile:${t.id}`, cls: state.moving === t.id ? 'tile moving' : 'tile', act: 'launch', arg: t.id,
    html: (t.running ? '<span class="badge">Running</span>' : '') + appIcon(t, 88) + `<span class="name">${esc(t.name)}</span>`,
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

// More tiles than fit: the grid scrolls (the clock and the hints stay) so the focused tile shows
// whole, its zoom and ring too, with the next row peeking. By layout offsets (from .tiles-wrap,
// scroll aside), not the screen: a tile still sliding (move mode) counts where it lands.
function keepTileInView(el) {
  const wrap = el.closest('.tiles-wrap');
  if (!wrap) return;
  const room = 36, top = el.offsetTop - room, bottom = el.offsetTop + el.offsetHeight + room;
  if (top < wrap.scrollTop) wrap.scrollTop = top;
  else if (bottom > wrap.scrollTop + wrap.clientHeight) wrap.scrollTop = bottom - wrap.clientHeight;
  listEdges(wrap);
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
    $('home-hints').innerHTML = hints([['D-pad', 'Move it'], ['A', 'Drop it here'], ['B', 'Cancel']]);
    return;
  }
  const f = $('home').querySelector('.tile.focused');
  const isAdd = f && f.dataset.act === 'addtile';
  const t = f && state.tiles.find((x) => x.id === f.dataset.arg);
  const list = f && f.tileHints ? [...f.tileHints, ['Home', 'Menu'], ['Hold Home', 'Power']]
    : isAdd ? [['A', 'Add tile'], ['Home', 'Menu'], ['Hold Home', 'Power']]
    : [['A', 'Open'], ...(t && t.running ? [['X', 'Close app']] : []), ['Start', 'Tile options'], ['Home', 'Menu'], ['Hold Home', 'Power']];
  $('home-hints').innerHTML = hints(list);
}

function renderMenu() {
  const running = state.tiles.filter((t) => t.running);
  const apps = running.length
    ? running.map((t) =>
        `<div class="row" data-nav data-id="app:${esc(t.id)}" data-act="switch" data-arg="${esc(t.id)}" data-close="${esc(t.id)}">` +
          appIcon(t, 36) + `<span class="grow">${esc(t.name)}</span>` +
          (t.id === state.current ? '<span class="tag">Now</span>' : '') +
        '</div>').join('')
    : '<div class="empty">No apps open</div>';
  // Many open apps (plus alert rows): two columns of shorter rows (notices.css).
  $('menu-panel').classList.toggle('crowded', running.length > 3 || (running.length > 2 && notices.rows.length > 0));
  // In place (patchHtml): the volume changing redraws its value, not the focused row's ring.
  // Everything but the hints is in .panel-scroll: with many apps and alerts it scrolls to the
  // focus (setFocus), the hints stay at the bottom.
  patchHtml($('menu-panel'), '<div class="panel-scroll">' +
    `<div class="panel-head"><span class="time">${timeText(new Date())}</span>` +
      `<span class="pad">${icon('controller', 30)}${esc(batteryText())}</span></div>` +
    noticeRowsHtml() + // alerts with something to do (notices.js)
    // Over the Windows desktop (desktop mode) the way back comes first.
    (state.desktop ? `<div class="row big" data-nav data-id="back-tv" data-act="power-action" data-arg="tv">${icon('tv', 38, 2)}Back to TV</div>` : '') +
    `<div class="row big" data-nav data-id="home" data-act="home">${icon('home', 38, 2)}Home screen</div>` +
    `<span class="section">Open apps</span><div class="apps">${apps}</div>` +
    '<span class="section quick-label">Quick</span>' +
    `<div class="row slider" data-nav data-id="volume" data-slider="volume">${icon('speaker', 34)}` +
      `<div class="track"><div class="fill" style="width:${state.volume}%"></div></div><span class="value">${state.volume}</span></div>` +
    `<div class="row slider" data-nav data-id="brightness" data-slider="brightness">${icon('sun', 34)}` +
      `<div class="track"><div class="fill white" style="width:${state.brightness}%"></div></div><span class="value">${state.brightness}</span></div>` +
    '<div class="quicks">' +
      `<div class="quick" data-nav data-id="q-buttons" data-act="buttons">${icon('controller', 34)}Buttons</div>` +
      `<div class="quick" data-nav data-id="q-timer" data-act="view" data-arg="timer">${icon('timer', 34)}Timer</div>` +
      `<div class="quick" data-nav data-id="q-power" data-act="view" data-arg="power">${icon('power', 34)}Power</div>` +
      `<div class="quick" data-nav data-id="q-settings" data-act="settings">${icon('sliders', 34)}Settings</div>` +
    '</div></div>' +
    `<footer class="hints">${hints([['A', 'Select'], ['X', 'Close app'], ['B', 'Back']])}</footer>`);
  // Over an app: what its buttons do, beside the panel (buttons.js; replaces the hint that
  // showed for a few seconds when an app opened).
  if ($('menu-app')) patchHtml($('menu-app'), typeof menuAppCard === 'function' ? menuAppCard() : '');
}

const POWER = [
  { id: 'sleep', glyph: 'moon', label: 'Sleep', caption: 'Press Home on the controller to wake' },
  { id: 'timer', glyph: 'timer', label: 'Sleep timer', caption: 'Count down, then sleep' },
  { id: 'restart', glyph: 'restart', label: 'Restart', caption: '' },
  { id: 'shutdown', glyph: 'power', label: 'Shut down', caption: '' },
  { id: 'desktop', glyph: 'desktop', label: 'Desktop mode', caption: 'Normal Windows desktop, for maintenance' }
];
// While the Windows desktop is up (desktop mode, state.desktop from the host), its card leads back.
const BACK_TO_TV = { id: 'tv', glyph: 'tv', label: 'Back to TV', caption: 'Close the Windows desktop and taskbar' };

function renderPower() {
  POWER[0].caption = SLEEP_MODES[state.prefs.sleepMode].wake;
  $('power-cards').innerHTML = POWER.map((p) => (p.id === 'desktop' && state.desktop ? BACK_TO_TV : p)).map((p) =>
    `<div class="card" data-nav data-id="${p.id}" data-act="${p.id === 'timer' ? 'view' : 'power-action'}" data-arg="${p.id === 'timer' ? 'timer' : p.id}">` +
      `${icon(p.glyph, 72, 1.5)}<span class="label">${p.label}</span><span class="caption">${p.caption}</span></div>`).join('');
  $('power-note').innerHTML = '';
  $('power-hints').innerHTML = hints([['A', 'Select'], ['B', 'Cancel']]);
}

const TIMER = [
  { minutes: 15, label: '15 min' }, { minutes: 30, label: '30 min' }, { minutes: 45, label: '45 min' },
  { minutes: 60, label: '1 hour' }, { minutes: 90, label: '1 h 30' }, { minutes: 120, label: '2 hours' },
  { minutes: 'video', label: 'This video ends', sub: 'When playback stops', small: true }, { minutes: 0, label: 'Off' }
];

function renderTimer() {
  $('timer-icon').innerHTML = icon('timer', 56);
  const picked = state.timer ? state.timer.label : null;
  $('timer-grid').innerHTML = TIMER.map((o, i) =>
    `<div class="opt${o.label === picked ? ' picked' : ''}" data-nav data-id="t${i}" data-act="timer" data-arg="${i}">` +
      `<span class="label${o.small ? ' small' : ''}">${o.label}</span><span class="sub">${o.sub || ''}</span></div>`).join('');
  const status = $('timer-status');
  status.innerHTML = icon('moon', 28) + esc(state.timer ? `${timerText()}. It shows in the top bar.` : 'No timer set');
  status.classList.toggle('on', !!state.timer);
  $('timer-hints').innerHTML = hints([['A', 'Set'], ['B', 'Back']]);
}

const SECTIONS = [
  ['sleep', 'moon', 'Sleep & power'], ['tv', 'tv', 'TV'], ['controller', 'controller', 'Controller'],
  ['phone', 'phone', 'Phone remote'], ['wifi', 'wifi', 'Wi-Fi'], ['bluetooth', 'bluetooth', 'Bluetooth'],
  ['display', 'desktop', 'Display'], ['sound', 'speaker', 'Sound'], ['updates', 'download', 'Updates'],
  ['about', 'info', 'About & Desktop mode']
];

// Values a setting row steps through with left/right (A steps forward).
const CHOICES = {
  idleMinutes: [[15, '15 minutes'], [30, '30 minutes'], [60, '1 hour'], [120, '2 hours'], [0, 'Never']],
  // Only the modes this PC has (the host reports them); screen off always works.
  sleepMode: [['standby', 'Screen off'], ['sleep', 'Sleep'], ['hibernate', 'Hibernate']],
  sleepAfterStandbyHours: [[0, 'Never'], [1, 'After 1 hour'], [3, 'After 3 hours'], [6, 'After 6 hours'], [12, 'After 12 hours']],
  stayAwakeWhilePlaying: [[false, 'Off'], [true, 'On']]
};

// What each sleep mode means, shown under the choice and on the Power screen.
const SLEEP_MODES = {
  standby: { caption: 'The video output and the TV go off; the box stays on (a few watts). Tap Home on the controller to wake it.',
             wake: 'Tap Home on the controller to wake' },
  sleep: { caption: 'Windows sleep (S3), about 1 W. The controller and the phone can’t wake it: use the power button or the keyboard.',
           wake: 'Wake with the power button or the keyboard' },
  hibernate: { caption: 'Windows hibernate: almost no power, slower to come back. Wake with the power button or the keyboard.',
               wake: 'Wake with the power button' }
};

function availableModes() { return CHOICES.sleepMode.filter(([v]) => v === 'standby' || state.power[v]); }

function choiceLabel(key) {
  const c = CHOICES[key].find(([v]) => v === state.prefs[key]);
  return c ? c[1] : String(state.prefs[key]);
}

function settingRow(key, label, caption, control) {
  // A value (a choice, a stepper) changes with left/right only once A has picked the row
  // (data-edit, editPress below); a toggle just flips with A.
  const edit = control.includes('class="toggle') ? '' : ' data-edit';
  return `<div class="srow" data-nav data-id="set-${key}" data-setting="${key}"${edit}>` +
    `<div class="text"><span class="label">${esc(label)}</span><span class="caption">${esc(caption)}</span></div>${control}</div>`;
}

function stepper(key) {
  return `<div class="value">${icon('chevleft', 28, 2)}${esc(choiceLabel(key))}${icon('chevright', 28, 2)}</div>`;
}

function renderSleepSection() {
  const p = state.prefs;
  return '<header><h1>Sleep &amp; power</h1>' +
      '<p>Sleep from the Power menu, the sleep timer, or when nothing happens for a while.</p></header>' +
    (availableModes().length > 1
      ? settingRow('sleepMode', 'Sleep mode', SLEEP_MODES[p.sleepMode].caption,
          '<div class="seg">' + availableModes().map(([v, l]) => `<span${v === p.sleepMode ? ' class="on"' : ''}>${l}</span>`).join('') + '</div>')
      : '') +
    (p.sleepMode === 'standby' && state.power.sleep
      ? settingRow('sleepAfterStandbyHours', 'Then Windows sleep', 'After this long with the screen off, the box goes into Windows sleep (about 1 W; the controller can’t wake it from there)',
          stepper('sleepAfterStandbyHours'))
      : '') +
    settingRow('idleMinutes', 'Sleep after', 'When nothing plays and nobody touches the controller', stepper('idleMinutes')) +
    // The sleep timer is set right here, as a choice (A, then left/right: stepTimer), not on
    // the timer screen of the Home menu.
    '<div class="srow" data-nav data-id="set-timer" data-timer data-edit>' +
      '<div class="text"><span class="label">Sleep timer</span>' +
        `<span class="caption">${esc(state.timer ? `${timerText()}. It shows in the top bar.` : 'A countdown, then the box and the TV sleep. Also in the Home menu.')}</span></div>` +
      `<div class="value">${icon('chevleft', 28, 2)}${esc(state.timer ? state.timer.label : 'Off')}${icon('chevright', 28, 2)}</div></div>` +
    settingRow('stayAwakeWhilePlaying', 'Stay awake while video plays', 'Even if you don’t touch the controller for hours',
      `<div class="toggle${p.stayAwakeWhilePlaying ? ' on' : ''}"><span></span></div>`) +
    '<div class="sbuttons">' +
      '<div class="sbutton" data-nav data-id="set-sleepnow" data-act="power-action" data-arg="sleep">Sleep now</div>' +
    '</div>';
}

function toggle(on) { return `<div class="toggle${on ? ' on' : ''}"><span></span></div>`; }

// Settings › TV: tv.js (a Settings section added through settingsSection).

let shownSection = null;   // the section's content animates in only when the section changes
let editing = null;        // data-id of the Settings row whose value left/right change (editPress)
function renderSettings() {
  const nav = `<nav class="snav"><div class="snav-title">${icon('chevleft', 36, 2)}<span>Settings</span></div>` +
    SECTIONS.map(([id, glyph, label]) =>
      `<div class="sitem${id === state.section ? ' on' : ''}" data-nav data-id="s-${id}" data-section="${id}">${icon(glyph, 32)}${esc(label)}</div>`).join('') +
    '</nav>';
  const title = SECTIONS.find(([id]) => id === state.section)[2];
  const added = EXT.sections[state.section];
  const body = state.section === 'sleep' ? renderSleepSection()
    : added ? added.render()
    : `<header><h1>${esc(title)}</h1><p>This section comes in a later update.</p></header>`;
  const entering = state.section !== shownSection;
  shownSection = state.section;
  const old = $('settings').querySelector('.spane main');
  if (old) {
    // Only what changed is redrawn, in place: the section list (the focused section keeps its
    // ring as the next one shows), and within a section a value that changed, a host push, the
    // clock, with the pane's scroll and the focused row left as they were. Another section's
    // pane is new, animating in.
    patchHtml($('settings').querySelector('.snav'), nav.replace(/^<nav[^>]*>|<\/nav>$/g, ''));
    if (!entering) patchHtml(old, body);
    else {
      const main = document.createElement('main');
      main.className = 'enter';
      main.innerHTML = body;
      old.replaceWith(main);
    }
  } else {
    // The hints follow the focus (settingsFocused).
    $('settings').innerHTML = nav + `<div class="spane"><main${entering ? ' class="enter"' : ''}>${body}</main>` +
      '<footer class="hints" id="settings-hints"></footer></div>';
  }
  for (const r of $('settings').querySelectorAll('.editing')) r.classList.remove('editing');
  const row = editing && $('settings').querySelector(`.spane [data-id="${CSS.escape(editing)}"]`);
  if (row) row.classList.add('editing'); else editing = null;
}

// Puts html into el keeping the elements that stay (same tag and place, same data-id): what
// changed is updated in place, the rest is left alone, so a value that changes does not redraw
// its row and the focused element keeps its ring (no ring drawing in again). The focus and
// edit classes of an element that stays are kept (setFocus and renderSettings manage them).
// Text fields are always new ones (wifi.js wires each field it draws).
function patchHtml(el, html) {
  const t = document.createElement('template');
  t.innerHTML = html;
  patchChildren(el, t.content);
}

function patchChildren(from, to) {
  const want = [...to.childNodes];
  want.forEach((w, i) => {
    const have = from.childNodes[i];
    if (!have) from.appendChild(w);
    else if (have.nodeType === w.nodeType && (have.nodeType !== 1 ||
        (have.tagName === w.tagName && have.tagName !== 'INPUT' && have.getAttribute('data-id') === w.getAttribute('data-id')))) patchNode(have, w);
    else from.replaceChild(w, have);
  });
  while (from.childNodes.length > want.length) from.lastChild.remove();
}

function patchNode(a, b) {
  if (a.nodeType !== 1) { if (a.nodeValue !== b.nodeValue) a.nodeValue = b.nodeValue; return; }
  const cls = [b.getAttribute('class') || '', ...['focused', 'editing'].filter((c) => a.classList.contains(c))].join(' ').trim();
  for (const at of [...a.attributes]) if (!b.hasAttribute(at.name) && at.name !== 'class') a.removeAttribute(at.name);
  for (const at of [...b.attributes]) if (at.name !== 'class' && a.getAttribute(at.name) !== at.value) a.setAttribute(at.name, at.value);
  if ((a.getAttribute('class') || '') !== cls) { if (cls) a.setAttribute('class', cls); else a.removeAttribute('class'); }
  patchChildren(a, b);
}

function changeSetting(key, step) {
  if (key.startsWith('phone.') && typeof phoneSetting === 'function') { phoneSetting(key); return; }   // phone-settings.js
  const list = key === 'sleepMode' ? availableModes() : CHOICES[key];
  const i = list.findIndex(([v]) => v === state.prefs[key]);
  const next = list[(Math.max(i, 0) + step + list.length) % list.length][0];
  state.prefs[key] = next;
  send({ type: 'setting', key, value: next });
  render();
}

function renderConfirm() {
  const c = state.confirm;
  $('confirm-box').innerHTML =
    `<h2>Close ${esc(c.name)}?</h2><p>It stops, and anything playing in it ends.</p>` +
    '<div class="buttons">' +
      `<div class="button" data-nav data-id="confirm-close" data-act="confirm-close">Close</div>` +
      '<div class="button" data-nav data-id="confirm-cancel" data-act="cancel">Cancel</div>' +
    '</div>' +
    `<div class="hints" style="padding:0;height:64px">${hints([['A', 'Select'], ['B', 'Cancel']])}</div>`;
}

function render() {
  const keep = state.memory[state.view];
  // A confirmation sits over the view it was opened from, which stays visible under it.
  const over = state.view === 'confirm' || (EXT.views[state.view] && EXT.views[state.view].overlay);
  const under = over ? state.stack[state.stack.length - 1] : null;
  if (state.view !== 'settings') editing = null;
  renderStatus();
  renderTiles();
  for (const f of EXT.home) f();
  if (state.view === 'menu' || under === 'menu') renderMenu();
  if (state.view === 'power') renderPower();
  if (state.view === 'timer') renderTimer();
  if (state.view === 'confirm') renderConfirm();
  if (state.view === 'settings' || under === 'settings') renderSettings();
  for (const v of [state.view, under]) if (EXT.views[v]) EXT.views[v].render();
  sectionHooks();
  const home = $('home'), wasBehind = home.classList.contains('behind');
  for (const v of ['home', 'menu', 'power', 'timer', 'confirm', 'settings', ...Object.keys(EXT.views)]) {
    $(v).classList.toggle('on', v === state.view || v === under);
    $(v).classList.toggle('under', v === under);   // its hints hide: one hint bar, the overlay's
  }
  // Over an app the captured screen shows behind; over the home screen, home shows dimmed.
  const overApp = state.view !== 'home' && state.backdrop;
  $('backdrop').classList.toggle('on', !!overApp);
  $('backdrop').style.backgroundImage = overApp ? `url("${state.backdrop}")` : '';
  home.classList.toggle('behind', state.view !== 'home' && !overApp);
  // Home back from under an overlay (a confirmation, Tile options, the menu) was on screen all
  // along: it must not play its entrance again, a flash of the whole screen.
  if (!home.classList.contains('on')) home.classList.remove('stay');
  else if (wasBehind && !home.classList.contains('behind')) home.classList.add('stay');
  for (const v of [state.view, under]) if (EXT.views[v] && EXT.views[v].layout) EXT.views[v].layout();
  restoreFocus(keep);
}

// ---- Focus and spatial navigation ---------------------------------------------------------

function viewEl() { return $(state.view); }
function items() { return [...viewEl().querySelectorAll('[data-nav]')]; }
function focusedEl() { return viewEl().querySelector('[data-nav].focused'); }

// Only a focus the user moved to is remembered; a default pick is made again on the next
// render (the first render happens before the tiles arrive from the host).
function setFocus(el, chosen = true) {
  for (const f of document.querySelectorAll('.focused')) f.classList.remove('focused');
  if (!el) return;
  el.classList.add('focused');
  if (chosen) state.memory[state.view] = el.dataset.id;
  if (state.view === 'home') { updateHomeHints(); keepTileInView(el); }
  // Settings: moving through the section list shows each section right away.
  if (el.dataset.section && el.dataset.section !== state.section) {
    state.section = el.dataset.section;
    render();
    return;
  }
  if (state.view === 'settings') settingsFocused(el);
  const view = EXT.views[state.view];
  if (view && view.focused) view.focused(el);
  // Anything in a box that scrolls (a list, a panel, a dialog's list) comes into view, ring and
  // all. (The home grid: keepTileInView, by layout, as its tiles slide.)
  const box = state.view !== 'home' && scrollerOf(el);
  if (box) scrollIntoBox(el, box, 16);
  noticeAvoid(el);   // the alerts' cards move off it (notices.js)
}

function restoreFocus(id) {
  const list = items();
  const kept = list.find((e) => e.dataset.id === id) || list.find((e) => e.dataset.id === state.memory[state.view]);
  if (kept) { setFocus(kept); return; }
  const view = EXT.views[state.view];
  setFocus((view && view.focus ? view.focus(list) : null) ||
    (state.view === 'home' ? list.find((e) => e.classList.contains('tile')) : null) ||
    // Settings opens on the section list, on the section last shown (A or right goes into it).
    (state.view === 'settings' ? list.find((e) => e.dataset.section === state.section) : null) ||
    (state.view === 'timer' ? list[1] : null) || list[0], false);
}

// The closest element in a direction from cur, among list; null when there is none. across:
// left and right take only what is beside cur (wholly past its edge, and overlapping its
// height or within 45 degrees), not a button under a wide row or far down another column.
// Something scrolled out of its own list (a tile above the home grid's top, under the status
// bar) is not a place to go from outside that list; within it, it is (the list scrolls to it).
function nearest(cur, dir, list = items(), across = false) {
  const r = cur.getBoundingClientRect();
  const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
  const ownBox = scrollerOf(cur);
  let best = null, bestScore = Infinity;
  for (const el of list) {
    if (el === cur) continue;
    const q = el.getBoundingClientRect();
    const box = scrollerOf(el);
    if (box && box !== ownBox) {
      const b = box.getBoundingClientRect(), x = q.left + q.width / 2, y = q.top + q.height / 2;
      if (x < b.left || x > b.right || y < b.top || y > b.bottom) continue;
    }
    const dx = q.left + q.width / 2 - cx, dy = q.top + q.height / 2 - cy;
    let main, side;
    if (dir === 'right') { main = dx; side = dy; } else if (dir === 'left') { main = -dx; side = dy; }
    else if (dir === 'down') { main = dy; side = dx; } else { main = -dy; side = dx; }
    if (main <= 8) continue;
    if (across && (dir === 'left' || dir === 'right')) {
      if (dir === 'left' ? q.right > r.left + 8 : q.left < r.right - 8) continue;
      if (Math.abs(side) > main && (q.bottom <= r.top || q.top >= r.bottom)) continue;
    }
    const score = main + Math.abs(side) * 2;
    if (score < bestScore) { bestScore = score; best = el; }
  }
  return best;
}

// The box that clips el and scrolls it (a list, a pane: overflow not visible), or null.
function scrollerOf(el) {
  for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) if (getComputedStyle(p).overflowY !== 'visible') return p;
  return null;
}

// Nothing wraps round, anywhere: past the end of a row, a list or a grid the focus stays.
function move(dir) {
  const cur = focusedEl();
  if (!cur) { restoreFocus(); return; }
  const best = nearest(cur, dir);
  if (best) setFocus(best);
}

// Settings, two columns: the section list and the section's pane. Up/down stay in their column
// and stop at its ends (up/down in the list changes section). In the pane, left and right go to
// what is beside the focus; left with nothing there, B or LB go back to the list. Right, A or RB
// from the list go into the pane.
function settingsPress(button, el) {
  const inNav = el && el.dataset.section;
  const navItem = () => $('settings').querySelector(`[data-section="${state.section}"]`);
  const pane = () => [...$('settings').querySelectorAll('.spane [data-nav]')];
  const firstOption = () => pane()[0];
  const focusTo = (to) => { if (to) setFocus(to); return true; };
  switch (button) {
    case 'lb': return focusTo(navItem());
    case 'rb': return focusTo(firstOption());
    case 'up': case 'down': {
      if (!el) return false;
      const column = inNav ? [...$('settings').querySelectorAll('.snav [data-nav]')] : pane();
      return focusTo(nearest(el, button, column));
    }
    case 'a':
      return inNav ? focusTo(firstOption()) : false;
    case 'right':
      if (inNav) return focusTo(firstOption());
      return el ? focusTo(nearest(el, 'right', pane(), true)) : false;
    case 'left':
      if (inNav) return true;   // nothing to the left of the list
      return focusTo((el && nearest(el, 'left', pane(), true)) || navItem());
    case 'b':
      return inNav ? false : focusTo(navItem());
  }
  return false;
}

// Rows with a value left/right change (steppers, choices, sliders: data-edit): moving over one
// never changes it. A picks the row (it shows it: .editing), then left/right change the value
// as it goes; A or B puts the row down, the value kept, and up/down move on from it. Toggles
// have no data-edit: A flips them. True when the button was the row's.
function editPress(button, el) {
  if (!el || el.dataset.edit === undefined) return false;
  if (editing !== el.dataset.id) {
    if (button !== 'a') return false;
    setEditing(el);
    return true;
  }
  switch (button) {
    case 'left': case 'right': {
      const section = EXT.sections[state.section];
      if (section && section.press && section.press(button, el)) return true;
      if (el.dataset.slider) adjust(el, button === 'right' ? 5 : -5);
      else if (el.dataset.setting) changeSetting(el.dataset.setting, button === 'right' ? 1 : -1);
      else if (el.dataset.timer !== undefined) stepTimer(button === 'right' ? 1 : -1);
      return true;
    }
    case 'a': case 'b': setEditing(null); return true;
    case 'up': case 'down': setEditing(null); return false;
  }
  return true;   // nothing else while a value is being changed
}

function setEditing(el) {
  editing = el ? el.dataset.id : null;
  for (const r of $('settings').querySelectorAll('.editing')) r.classList.remove('editing');
  if (el) el.classList.add('editing');
  const f = focusedEl();
  if (f) settingsFocused(f);
}

// The focus in Settings: a row being changed is put down when the focus leaves it; the pane
// scrolls to the focus (its ring clear of the button hints); the hints say what the buttons do.
function settingsFocused(el) {
  if (editing && el.dataset.id !== editing) setEditing(null);
  const main = el.closest('.spane main');
  // A list that scrolls itself (Wi-Fi networks, Bluetooth devices) keeps its row in view.
  if (main && !el.parentElement.closest('.wifi-scroll')) scrollIntoBox(el, main, 28);
  const bar = $('settings-hints');
  if (bar) bar.innerHTML = hints(settingsHints(el));
}

function settingsHints(el) {
  if (el.dataset.section) return [['A', 'Open'], ['B', 'Back']];
  if (editing === el.dataset.id) return [['←→', 'Change'], ['A', 'Done']];
  const what = el.dataset.edit !== undefined ? 'Change' : el.querySelector('.toggle') ? 'On / off' : 'Select';
  return [['A', what], ['B', 'Sections']];
}

// Scrolls box (overflow hidden: only this scrolls it) so el shows whole, with room around it
// for the focus ring; the first element takes it back to the top, the lowest to the bottom (the
// notes under it show). The stage is scaled: screen pixels are turned back into the box's own.
function scrollIntoBox(el, box, room) {
  const b = box.getBoundingClientRect(), r = el.getBoundingClientRect();
  const scale = b.height / box.offsetHeight || 1;
  const all = [...box.querySelectorAll('[data-nav]')];
  const lowest = all.reduce((low, e) => (e.getBoundingClientRect().bottom > low.getBoundingClientRect().bottom ? e : low), el);
  if (all[0] === el) box.scrollTop = 0;
  else if (lowest === el) box.scrollTop = Math.min(box.scrollHeight, box.scrollTop + (r.top - b.top) / scale - room);
  else if (r.bottom > b.bottom - room * scale) box.scrollTop += (r.bottom - b.bottom) / scale + room;
  else if (r.top < b.top + room * scale) box.scrollTop -= (b.top - r.top) / scale + room;
}

// A box that scrolls says where there is more (.more-up, .more-down: its ends fade there).
function listEdges(box) {
  box.classList.toggle('more-up', box.scrollTop > 2);
  box.classList.toggle('more-down', box.scrollTop + box.clientHeight < box.scrollHeight - 2);
}

// ---- Added screens ------------------------------------------------------------------------
// Other scripts, loaded after this one (index.html), add Settings sections, views of their
// own, actions and host messages here; this file calls them when it renders, handles a button
// or gets a message. They may use everything above and below (state, send, render, go, back,
// hints, icon, esc, setFocus, toast...), when called.
//
//   settingsSection('wifi', {
//     render() -> html               the section's pane (a <header> and rows, as renderSleepSection)
//     press(button, el) -> handled   buttons while the focus is in the pane, before the usual
//     shown(), left()                the section came into view / went out of it (ask the host
//                                    for data, stop something live)
//     demo()                         sample data for index.html#settings/wifi in a browser
//   })
//   addView('maps', { render(), press(button, el) -> handled, focus(items) -> element,
//                     overlay (drawn over the view it opened from), demo(arg),
//                     layout() (once it is on screen, to place things by their size),
//                     focused(el) (the focus moved to el: to scroll a list to it) })
//                                    a <section id="maps" class="view"> of its own; go('maps')
//   onAction('wifi-join', (el, arg) => ...)   data-act="wifi-join" on a data-nav element
//   hostMessage('wifi.', (msg) => ...)        host messages by type, or by prefix ("wifi.")
//   ask({ title, text, yes, onYes })          the shared yes / cancel dialog, over any view
//   onHome(fn)                                runs with each render, to draw an extra on the home
//                                             screen (the phone remote card, phone-card.js)
//   onTiles(() => [{ id, cls, act, arg, x, label, html, hints }])
//                                             extra home tiles, before the "+" tile (apps being
//                                             installed, library.js); x: the action X runs,
//                                             hints: the hint bar's list while one is focused

const EXT = { sections: {}, views: {}, actions: {}, host: {}, home: [], tiles: [] };

function onHome(fn) { EXT.home.push(fn); }
function onTiles(fn) { EXT.tiles.push(fn); }

function settingsSection(id, section) { EXT.sections[id] = section; }

function addView(id, view) {
  if (!$(id)) {
    const el = document.createElement('section');
    el.id = id;
    el.className = 'view' + (view.overlay ? ' overlay' : '');
    $('stage').insertBefore(el, $('opening'));   // under the "opening" layer and the toasts
  }
  EXT.views[id] = view;
}

function onAction(name, fn) { EXT.actions[name] = fn; }
function hostMessage(type, fn) { EXT.host[type] = fn; }

// shown / left for the Settings section in view (none while Settings is not).
let sectionInView = null;
function sectionHooks() {
  // The TV method dialog over Settings is still the TV section (its list keeps refreshing).
  const now = state.view === 'settings' || state.view === 'tvmethod' ? state.section : null;
  if (now === sectionInView) return;
  const was = EXT.sections[sectionInView];
  sectionInView = now;
  if (was && was.left) was.left();
  if (EXT.sections[now] && EXT.sections[now].shown) EXT.sections[now].shown();
}

// The shared dialog: A on the first button runs onYes; B or Cancel closes it.
let asking = null;
function ask(q) { asking = q; go('ask'); }
addView('ask', {
  overlay: true,
  render() {
    const q = asking || {};
    $('ask').innerHTML = '<div class="dialog">' +
      `<h2>${esc(q.title || '')}</h2>${q.text ? `<p>${esc(q.text)}</p>` : ''}` +
      '<div class="buttons">' +
        `<div class="button" data-nav data-id="ask-yes" data-act="ask-yes">${esc(q.yes || 'OK')}</div>` +
        '<div class="button" data-nav data-id="ask-no" data-act="cancel">Cancel</div>' +
      '</div>' +
      `<div class="hints" style="padding:0;height:64px">${hints([['A', 'Select'], ['B', 'Cancel']])}</div></div>`;
  },
  focus: (list) => list.find((e) => e.dataset.id === 'ask-no'),
});
onAction('ask-yes', () => { const q = asking; back(); if (q && q.onYes) q.onYes(); });

// index.html#view or #view/arg in a plain browser: #settings/wifi opens that section (with
// its demo data), #maps or #buttons/twitch an added view.
function demoRoute(hash) {
  // "?..." after the route is the screen's own demo options (#settings/tv?demo=paused): theirs to read.
  const [view, arg] = hash.split('?')[0].split('/');
  if (view === 'settings' && arg) {
    state.section = arg;
    if (EXT.sections[arg] && EXT.sections[arg].demo) EXT.sections[arg].demo();
  }
  if (EXT.views[view] && EXT.views[view].demo) EXT.views[view].demo(arg);
  // #timer/video: the sleep timer set to "when this video ends", 23 minutes left.
  if (view === 'timer' && arg === 'video') state.timer = { label: 'This video ends', endsAt: 'video', minutesLeft: 23 };
  // #menu/twitch: the Home menu over that app, open (its buttons beside the panel).
  if (view === 'menu' && arg) {
    const t = state.tiles.find((x) => x.id === arg);
    if (t) { t.running = true; state.current = arg; }
    if (typeof mapsDemo === 'function') mapsDemo();
  }
  go(view);
}

// ---- Actions ------------------------------------------------------------------------------

// go: open a view on top of the current one (B comes back). reset: start over at a view.
function go(view) { state.stack.push(state.view); state.view = view; render(); }
function reset(view) { state.stack = []; state.view = view; render(); }

function showOpening(t) {
  const el = $('opening');
  el.innerHTML = `<span class="logo">${appIcon(t, 160, 1.5)}</span>` +
    `<span class="name">Opening ${esc(t.name)}</span><span class="sub">Home comes back here anytime</span>`;
  el.classList.add('on');
}
function hideOpening() { $('opening').classList.remove('on'); }

// A short message from the page, drawn with the alerts (notices.js).
function toast(text, kind) { if (text) noticeOwn(text, kind); }

function activate(el) {
  if (!el) return;
  const act = el.dataset.act, arg = el.dataset.arg;
  switch (act) {
    case 'launch': {
      const t = state.tiles.find((x) => x.id === arg);
      if (t && !t.running) showOpening(t);
      send({ type: 'launch', id: arg });
      break;
    }
    case 'switch': send({ type: 'switchTo', id: arg }); break;
    case 'home': state.current = null; state.backdrop = null; send({ type: 'home' }); reset('home'); break;
    case 'view': go(arg); break;
    case 'power': go('power'); break;
    case 'power-action':
      if (arg === 'desktop') ask({ title: 'Switch to the Windows desktop?', yes: 'Desktop mode', onYes: () => send({ type: 'power', action: 'desktop' }),
        text: 'The desktop, taskbar and Start menu open, for maintenance; open apps go down to the taskbar. To come back, press Home, then Back to TV (or the Back to TV icon on the desktop).' });
      // One wrong press of A must not switch the box off: the controller cannot turn it back on.
      else if (arg === 'shutdown') ask({ title: 'Shut down the box?', yes: 'Shut down', onYes: () => send({ type: 'power', action: 'shutdown' }),
        text: 'It turns off completely: the controller can’t turn it back on. Use the box’s power button to start it again.' });
      // Nor close every app (a film half watched) by restarting it.
      else if (arg === 'restart') ask({ title: 'Restart the box?', yes: 'Restart', onYes: () => send({ type: 'power', action: 'restart' }),
        text: 'Apps close and the box starts again; it comes back to the TV screen by itself.' });
      else send({ type: 'power', action: arg });
      break;
    case 'timer': setTimer(TIMER[Number(arg)]); break;
    case 'confirm-close': send({ type: 'close', id: state.confirm.id }); back(); break;
    case 'cancel': back(); break;
    // Settings opens on its section list (restoreFocus), not where the focus was last time.
    case 'settings': state.memory.settings = null; go('settings'); break;
    case 'soon': toast(`${arg} come in a later update`); break;
    default: if (EXT.actions[act]) EXT.actions[act](el, arg);
  }
}

function setTimer(o) {
  state.timer = o.minutes === 0 ? null : { label: o.label, endsAt: o.minutes === 'video' ? 'video' : Date.now() + o.minutes * 60000 };
  send({ type: 'timer', minutes: o.minutes });
  render();
}

// Settings › Sleep timer, left/right: Off, 15 min ... 2 hours, This video ends, round again.
function stepTimer(step) {
  const order = [TIMER.length - 1, ...TIMER.keys()].slice(0, TIMER.length);   // Off first
  const now = order.findIndex((i) => (state.timer ? TIMER[i].label === state.timer.label : TIMER[i].minutes === 0));
  setTimer(TIMER[order[(Math.max(now, 0) + step + order.length) % order.length]]);
}

function adjust(el, delta) {
  const key = el.dataset.slider;
  state[key] = Math.max(0, Math.min(100, state[key] + delta));
  send({ type: key, value: state[key] });
  render();
}

// Back through the views opened with go(); past the first one, return to the app the menu
// was opened over, or to the home screen.
function back() {
  if (state.view === 'home') return;
  const prev = state.stack.pop();
  if (prev) { state.view = prev; render(); return; }
  if (state.current) send({ type: 'resume', id: state.current });
  else reset('home');
}

// One entry point for the controller (via the host) and the keyboard.
function press(button) {
  timePress(button);
  if (typeof soundsHear === 'function') soundsHear(button);   // interface sounds (sounds.js): what this press does picks one
  const el = focusedEl();
  // Moving a tile on the home screen (Tile options > Move): the mover takes every button.
  if (state.moving && EXT.actions['tile-move'] && EXT.actions['tile-move'](el, button)) return;
  // Added views and Settings sections first (their own buttons), then the usual.
  const view = EXT.views[state.view];
  if (view && view.press && view.press(button, el)) return;
  if (state.view === 'settings') {
    if (editPress(button, el)) return;
    // A value row not picked with A: left/right are the focus's, never the section's to change it.
    const idle = el && el.dataset.edit !== undefined && (button === 'left' || button === 'right');
    const section = EXT.sections[state.section];
    if (section && section.press && !(el && el.dataset.section) && !idle && section.press(button, el)) return;
    if (settingsPress(button, el)) return;
  }
  switch (button) {
    case 'up': case 'down': move(button); break;
    case 'left': case 'right':
      if (el && el.dataset.slider) adjust(el, button === 'right' ? 5 : -5);
      else if (el && el.dataset.setting) changeSetting(el.dataset.setting, button === 'right' ? 1 : -1);
      else move(button);
      break;
    case 'a':
      if (el && el.dataset.setting) changeSetting(el.dataset.setting, 1); else activate(el);
      break;
    case 'b': back(); break;
    case 'x': {
      // An alert's row: dismisses it, and nothing else (never on to the close below).
      if (el && el.dataset.alert) { noticeDismiss(el.dataset.alert); break; }
      // An element with an X action of its own (data-x: an app that did not install, library.js).
      if (el && el.dataset.x && EXT.actions[el.dataset.x]) { EXT.actions[el.dataset.x](el, el.dataset.arg); break; }
      // Home screen: the focused tile, if it is running. Menu: the focused app row, else the
      // app the menu was opened over.
      let id = null;
      if (state.view === 'home' && el && el.dataset.arg) id = el.dataset.arg;
      else if (state.view === 'menu') id = (el && el.dataset.close) || state.current;
      else break;
      const t = id && state.tiles.find((x) => x.id === id && x.running);
      if (t) { state.confirm = { id: t.id, name: t.name }; go('confirm'); }
      break;
    }
    case 'home':
      if (state.view === 'home') {
        state.current = null; state.backdrop = null;
        // An actionable alert on screen: the menu opens on its row; otherwise where it was left.
        const f = noticeHomeFocus();
        if (f) state.memory.menu = f;
        go('menu');
      }
      else back();
      break;
    case 'homeHold': if (state.view !== 'power') go('power'); break;
    case 'r3': { const f = textField(); if (f) openKeyboardFor(f); break; }  // textinput.js
    case 'start':
      // Home screen: options for the focused tile (Move, Rename, Change icon, Remove).
      if (state.view === 'home' && el && el.dataset.act === 'launch' && EXT.actions['tile-options']) EXT.actions['tile-options'](el, el.dataset.arg);
      break;
  }
}

// How long a press takes on the box itself (4K on its small GPU, not a PC's headless Edge): from
// the press to the frame that shows it (the second animation frame after it: the first frame
// has been drawn then). Over 60 ms it goes to the launcher's log, "Slow press 180 ms in addtile
// (right)"; the host keeps the log from filling up. Not while hidden, nor without a host.
function timePress(button) {
  if (!host || document.hidden) return;
  const asked = performance.now(), view = state.view;
  requestAnimationFrame(() => requestAnimationFrame(() => {
    const ms = Math.round(performance.now() - asked);
    if (ms > 60) send({ type: 'perf', ms, view, button });
  }));
}

const KEYS = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right', Enter: 'a', ' ': 'a',
  Escape: 'b', Backspace: 'b', x: 'x', y: 'y', h: 'home', p: 'homeHold', PageUp: 'lb', PageDown: 'rb', o: 'start' };
addEventListener('keydown', (e) => {
  // Blank (standby, the launcher black in front): a real key press wakes the box.
  if ($('stage').classList.contains('blank')) { e.preventDefault(); send({ type: 'wake' }); return; }
  // A text field has the focus: the key is the field's (Backspace deletes, x types an x), only
  // Enter and Escape still confirm and cancel (textinput.js).
  if (keyGuard(e)) return;
  const b = KEYS[e.key];
  if (!b) return;
  e.preventDefault();
  press(b);
});

// ---- Host messages ------------------------------------------------------------------------

function onHost(msg) {
  switch (msg.type) {
    case 'init':
      state.tiles = msg.tiles;
      Object.assign(state, msg.settings || {});
      if (msg.prefs) Object.assign(state.prefs, msg.prefs);
      if (msg.power) state.power = msg.power;
      if ('libraryAvailable' in msg) state.libraryAvailable = msg.libraryAvailable;
      render();
      break;
    case 'tiles': state.tiles = msg.tiles; render(); break;
    case 'blank': $('stage').classList.add('blank'); break;
    case 'opened':
      hideOpening();
      if (!msg.ok && msg.text) toast(msg.text, 'warn'); // failures come as alerts now
      break;
    case 'state':
      if (msg.running) {
        for (const t of state.tiles) t.running = msg.running.includes(t.id);
        // The app the menu was opened over has closed: B and Home now lead home, not to it.
        if (state.current && state.current !== 'desktop' && !msg.running.includes(state.current)) { state.current = null; state.backdrop = null; }
      }
      for (const k of ['volume', 'brightness', 'battery', 'controller', 'alert', 'phone', 'desktop']) if (k in msg) state[k] = msg[k];
      if ('timer' in msg) state.timer = msg.timer;
      render();
      break;
    case 'input': press(msg.button); break;
    case 'show': {
      hideOpening();
      const asked = performance.now();
      const view = () => {
        state.current = msg.current || null;
        state.backdrop = msg.backdrop || null;
        // focus: the element to land on (an alert's row, the tile of an app that just closed);
        // section: the Settings section to open (an alert's action).
        if (msg.focus) state.memory[msg.view] = msg.focus;
        else if (msg.view === 'settings') state.memory.settings = null;   // on the section list
        if (msg.section) state.section = msg.section;
        reset(msg.view);
      };
      const unblank = () => {
        const stage = $('stage');
        // Over an app the backdrop is the app's own frame: no fade up from dark, it is there at once.
        if (msg.backdrop) stage.style.transition = 'none';
        stage.classList.remove('blank');
        if (msg.backdrop) { void stage.offsetWidth; stage.style.transition = ''; }
      };
      if (!msg.backdrop) { view(); unblank(); if (msg.ack) ackShown(asked, 0); break; }
      // Shown once its backdrop is decoded, so it does not flash the home screen first. The view
      // is built meanwhile under the blank stage (the window is still hidden behind the app).
      const early = $('stage').classList.contains('blank');
      if (early) view();
      const img = new Image();
      let done = false;
      const once = () => {
        if (done) return;
        done = true;
        if (!early) view();
        unblank();
        if (msg.ack) ackShown(asked, Math.round(performance.now() - asked));
      };
      img.src = msg.backdrop;
      img.decode().then(once, once);
      setTimeout(once, 400);
      break;
    }
    case 'toast': toast(msg.text, msg.kind); break;
    default: {
      // Added scripts' messages: by type ("wifi.list"), else by prefix ("wifi.").
      const handler = EXT.host[msg.type] || EXT.host[String(msg.type).split('.')[0] + '.'];
      if (handler) handler(msg);
    }
  }
}

// The Home menu over an app is drawn: the host shows its window now (MainForm.RevealPending).
// The page keeps drawing while the window is hidden, so this waits for the frame with the menu
// in it (two animation frames), and the first frame the TV gets is that one, not the black the
// page last showed. A page that draws nothing (hidden) answers at once, a slow one after 150 ms.
// load: ms to decode the backdrop; ms: from the host's message to this answer.
function ackShown(asked, load) {
  let sent = false;
  const answer = (painted) => {
    if (sent) return;
    sent = true;
    send({ type: 'shown', painted, load, ms: Math.round(performance.now() - asked) });
  };
  if (document.hidden) { answer(false); return; }
  requestAnimationFrame(() => requestAnimationFrame(() => answer(true)));
  setTimeout(() => answer(false), 150);
}

if (host) {
  host.addEventListener('message', (e) => onHost(e.data));
} else {
  // Demo data for a plain browser. ?logos=1 in the hash: logos from logos\<id>.png next to the
  // page (none ship; a missing one shows the glyph), e.g. index.html#home?logos=1.
  onHost({ type: 'init', tiles: [
    { id: 'youtube', name: 'YouTube', glyph: 'youtube', color: '#FF5B52' },
    { id: 'twitch', name: 'Twitch', glyph: 'chat', color: '#B08CFF' },
    { id: 'stremio', name: 'Stremio', glyph: 'film', color: '#7C8CFF' },
    { id: 'jellyfin', name: 'Jellyfin', glyph: 'library', color: '#3DC0F0', running: true },
    { id: 'moonlight', name: 'Moonlight', glyph: 'moon', color: '#F5D16B' },
    { id: 'edge', name: 'Browser', glyph: 'globe', color: '#3CCB9A' }
  ].map(demoLogo), settings: { controller: true, battery: 'full' } });
  // ?tiles=18 in the hash: that many tiles (more than fit: the grid scrolls), e.g. #home?tiles=18.
  const more = /[?&]tiles=(\d+)/.exec(location.hash);
  if (more) state.tiles = Array.from({ length: Number(more[1]) }, (_, i) => ({ ...state.tiles[i % 6], id: `${state.tiles[i % 6].id}${i || ''}`, name: `${state.tiles[i % 6].name}${i >= 6 ? ' ' + (i + 1) : ''}`, running: i === 3 }));
}

// Demo (no host) with ?logos=1: an item's logo is logos/<id>.png beside the page.
function demoLogo(a) {
  if (!host && /[?&]logos=1/.test(location.hash)) a.logo = a.logoUrl = `logos/${a.id}.png`;
  return a;
}

fit();
render();
// Redraw when the minute (or the timer countdown) changes; render() keeps the focus.
let shown = '';
setInterval(() => {
  const now = timeText(new Date()) + timerText();
  if (now !== shown) { shown = now; render(); }
}, 1000);
// Once the scripts after this one have added their screens: the host's first messages may
// be theirs. Demo only: index.html#settings (or #menu, #settings/sound, #maps...) opens that
// view, for screenshots.
addEventListener('DOMContentLoaded', () => {
  if (!host && location.hash) demoRoute(location.hash.slice(1));
  send({ type: 'ready' });
});
