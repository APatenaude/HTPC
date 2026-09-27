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

function hints(list) {
  return list.map(([btn, label]) =>
    `<div class="hint"><span class="key${btn.length > 1 ? ' wide' : ''}">${esc(btn)}</span><span>${esc(label)}</span></div>`).join('');
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

// Tiles are rebuilt only when something on them changed, so their entrance animation does not
// replay on every refresh.
let tilesHtml = '';
function renderTiles() {
  const tiles = state.tiles.map((t) =>
    `<div class="tile${state.moving === t.id ? ' moving' : ''}" data-nav data-id="tile:${esc(t.id)}" data-act="launch" data-arg="${esc(t.id)}">` +
      (t.running ? '<span class="badge">Running</span>' : '') +
      `<span style="display:flex;color:${esc(t.color || 'inherit')}">${icon(t.glyph, 88)}</span>` +
      `<span class="name">${esc(t.name)}</span>` +
    '</div>').join('');
  // The "+" tile is always last (SPEC decision): A opens the library / add-tile screen.
  const add = state.moving ? '' :
    '<div class="tile add" data-nav data-id="tile:+add" data-act="addtile" aria-label="Add tile">' +
      `<span style="display:flex">${icon('plus', 80, 2)}</span><span class="name">Add tile</span></div>`;
  const html = tiles + add;
  if (html !== tilesHtml) { $('tiles').innerHTML = html; tilesHtml = html; }
  updateHomeHints();
}

// Hints change with the focused tile and with move mode.
function updateHomeHints() {
  if (state.moving) {
    $('home-hints').innerHTML = hints([['←→↑↓', 'Move'], ['A', 'Place here'], ['B', 'Cancel']]);
    return;
  }
  const f = $('home').querySelector('.tile.focused');
  const isAdd = f && f.dataset.act === 'addtile';
  const t = f && state.tiles.find((x) => x.id === f.dataset.arg);
  const list = isAdd
    ? [['A', 'Add tile'], ['Home', 'Menu'], ['Hold Home', 'Power']]
    : [['A', 'Open'], ...(t && t.running ? [['X', 'Close app']] : []), ['Start', 'Tile options'], ['Home', 'Menu']];
  $('home-hints').innerHTML = hints(list);
}

function renderMenu() {
  const running = state.tiles.filter((t) => t.running);
  const apps = running.length
    ? running.map((t) =>
        `<div class="row" data-nav data-id="app:${esc(t.id)}" data-act="switch" data-arg="${esc(t.id)}" data-close="${esc(t.id)}">` +
          `<span style="display:flex;color:${esc(t.color)}">${icon(t.glyph, 36)}</span>` +
          `<span class="grow">${esc(t.name)}</span>` +
          (t.id === state.current ? '<span class="tag">Now</span>' : '') +
        '</div>').join('')
    : '<div class="empty">No apps open</div>';
  // Many open apps (plus alert rows): two columns of shorter rows (notices.css).
  $('menu-panel').classList.toggle('crowded', running.length > 3 || (running.length > 2 && notices.rows.length > 0));
  $('menu-panel').innerHTML =
    `<div class="panel-head"><span class="time">${timeText(new Date())}</span>` +
      `<span class="pad">${icon('controller', 30)}${esc(batteryText())}</span></div>` +
    noticeRowsHtml() + // alerts with something to do (notices.js)
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
    '</div>' +
    `<footer class="hints">${hints([['A', 'Select'], ['X', 'Close app'], ['B', 'Back']])}</footer>`;
}

const POWER = [
  { id: 'sleep', glyph: 'moon', label: 'Sleep', caption: 'Press Home on the controller to wake' },
  { id: 'timer', glyph: 'timer', label: 'Sleep timer', caption: 'Count down, then sleep' },
  { id: 'restart', glyph: 'restart', label: 'Restart', caption: '' },
  { id: 'shutdown', glyph: 'power', label: 'Shut down', caption: '' },
  { id: 'desktop', glyph: 'desktop', label: 'Desktop mode', caption: 'Normal Windows desktop, for maintenance' }
];

function renderPower() {
  POWER[0].caption = SLEEP_MODES[state.prefs.sleepMode].wake;
  $('power-cards').innerHTML = POWER.map((p) =>
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
  return `<div class="srow" data-nav data-id="set-${key}" data-setting="${key}">` +
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
    `<div class="srow" data-nav data-id="set-timer" data-act="view" data-arg="timer">` +
      '<div class="text"><span class="label">Sleep timer</span><span class="caption">A countdown you set. Also in the Home menu.</span></div>' +
      `<div class="value link">${esc(state.timer ? timerText() : 'Off')}${icon('chevright', 28, 2)}</div></div>` +
    settingRow('stayAwakeWhilePlaying', 'Stay awake while video plays', 'Even if you don’t touch the controller for hours',
      `<div class="toggle${p.stayAwakeWhilePlaying ? ' on' : ''}"><span></span></div>`) +
    '<div class="sbuttons">' +
      '<div class="sbutton" data-nav data-id="set-sleepnow" data-act="power-action" data-arg="sleep">Sleep now</div>' +
    '</div>';
}

function toggle(on) { return `<div class="toggle${on ? ' on' : ''}"><span></span></div>`; }

// Settings › TV: tv.js (a Settings section added through settingsSection).

let shownSection = null;   // the section's content animates in only when the section changes
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
  $('settings').innerHTML = nav + `<div class="spane"><main${entering ? ' class="enter"' : ''}>${body}</main>` +
    `<footer class="hints">${hints([['A', 'Change'], ['←→', 'Adjust'], ['LB', 'Sections'], ['RB', 'Options'], ['B', 'Back']])}</footer></div>`;
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
  for (const v of ['home', 'menu', 'power', 'timer', 'confirm', 'settings', ...Object.keys(EXT.views)]) $(v).classList.toggle('on', v === state.view || v === under);
  // Over an app the captured screen shows behind; over the home screen, home shows dimmed.
  const overApp = state.view !== 'home' && state.backdrop;
  $('backdrop').classList.toggle('on', !!overApp);
  $('backdrop').style.backgroundImage = overApp ? `url("${state.backdrop}")` : '';
  $('home').classList.toggle('behind', state.view !== 'home' && !overApp);
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
  if (state.view === 'home') updateHomeHints();
  // Settings: moving through the section list shows each section right away.
  if (el.dataset.section && el.dataset.section !== state.section) {
    state.section = el.dataset.section;
    render();
  }
}

function restoreFocus(id) {
  const list = items();
  const kept = list.find((e) => e.dataset.id === id) || list.find((e) => e.dataset.id === state.memory[state.view]);
  if (kept) { setFocus(kept); return; }
  const view = EXT.views[state.view];
  setFocus((view && view.focus ? view.focus(list) : null) ||
    (state.view === 'home' ? list.find((e) => e.classList.contains('tile')) : null) ||
    (state.view === 'settings' ? list.find((e) => e.classList.contains('srow')) || list.find((e) => e.dataset.section === state.section) : null) ||
    (state.view === 'timer' ? list[1] : null) || list[0], false);
}

function move(dir) {
  const cur = focusedEl();
  if (!cur) { restoreFocus(); return; }
  const r = cur.getBoundingClientRect();
  const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
  let best = null, bestScore = Infinity;
  for (const el of items()) {
    if (el === cur) continue;
    const q = el.getBoundingClientRect();
    const dx = q.left + q.width / 2 - cx, dy = q.top + q.height / 2 - cy;
    let main, side;
    if (dir === 'right') { main = dx; side = dy; } else if (dir === 'left') { main = -dx; side = dy; }
    else if (dir === 'down') { main = dy; side = dx; } else { main = -dy; side = dx; }
    if (main <= 8) continue;
    const score = main + Math.abs(side) * 2;
    if (score < bestScore) { bestScore = score; best = el; }
  }
  if (!best) best = wrapTarget(cur, dir);
  if (best) setFocus(best);
}

// Past the end of a row or a list: the far end of the same row or column.
function wrapTarget(cur, dir) {
  const r = cur.getBoundingClientRect();
  const horizontal = dir === 'left' || dir === 'right';
  let far = null, farDist = 0;
  for (const el of items()) {
    if (el === cur) continue;
    const q = el.getBoundingClientRect();
    const inLine = horizontal ? (q.top < r.bottom && q.bottom > r.top) : (q.left < r.right && q.right > r.left);
    if (!inLine) continue;
    const dist = dir === 'right' ? r.left - q.left : dir === 'left' ? q.left - r.left : dir === 'down' ? r.top - q.top : q.top - r.top;
    if (dist > farDist) { farDist = dist; far = el; }
  }
  return far;
}

// Settings, two columns: LB (or left, off a value) goes to the section list, RB (or right) into
// the section's options; B from the options also returns to the list. Up/down in the list
// changes section.
function settingsPress(button, el) {
  const inNav = el && el.dataset.section;
  const navItem = () => $('settings').querySelector(`[data-section="${state.section}"]`);
  const firstOption = () => $('settings').querySelector('.spane [data-nav]');
  switch (button) {
    case 'lb': setFocus(navItem()); return true;
    case 'rb': if (firstOption()) setFocus(firstOption()); return true;
    case 'a':
      if (inNav) { if (firstOption()) setFocus(firstOption()); return true; }
      return false;
    case 'right':
      if (inNav) { if (firstOption()) setFocus(firstOption()); return true; }
      return false;
    case 'left':
      if (!inNav && !(el && el.dataset.setting)) { setFocus(navItem()); return true; }
      return inNav; // nothing to the left of the list
    case 'b':
      if (!inNav) { setFocus(navItem()); return true; }
      return false;
  }
  return false;
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
//                     overlay (drawn over the view it opened from), demo(arg) })
//                                    a <section id="maps" class="view"> of its own; go('maps')
//   onAction('wifi-join', (el, arg) => ...)   data-act="wifi-join" on a data-nav element
//   hostMessage('wifi.', (msg) => ...)        host messages by type, or by prefix ("wifi.")
//   ask({ title, text, yes, onYes })          the shared yes / cancel dialog, over any view
//   onHome(fn)                                runs with each render, to draw an extra on the home
//                                             screen (the phone remote card, phone-card.js)

const EXT = { sections: {}, views: {}, actions: {}, host: {}, home: [] };

function onHome(fn) { EXT.home.push(fn); }

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
  go(view);
}

// ---- Actions ------------------------------------------------------------------------------

// go: open a view on top of the current one (B comes back). reset: start over at a view.
function go(view) { state.stack.push(state.view); state.view = view; render(); }
function reset(view) { state.stack = []; state.view = view; render(); }

function showOpening(t) {
  const el = $('opening');
  el.innerHTML = `<span class="logo" style="color:${esc(t.color)}">${icon(t.glyph, 160, 1.5)}</span>` +
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
    case 'power-action': send({ type: 'power', action: arg }); break;
    case 'timer': {
      const o = TIMER[Number(arg)];
      state.timer = o.minutes === 0 ? null : { label: o.label, endsAt: o.minutes === 'video' ? 'video' : Date.now() + o.minutes * 60000 };
      send({ type: 'timer', minutes: o.minutes });
      render();
      break;
    }
    case 'confirm-close': send({ type: 'close', id: state.confirm.id }); back(); break;
    case 'cancel': back(); break;
    case 'settings': go('settings'); break;
    case 'soon': toast(`${arg} come in a later update`); break;
    default: if (EXT.actions[act]) EXT.actions[act](el, arg);
  }
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
  const el = focusedEl();
  // Moving a tile on the home screen (Tile options > Move): the mover takes every button.
  if (state.moving && EXT.actions['tile-move'] && EXT.actions['tile-move'](el, button)) return;
  // Added views and Settings sections first (their own buttons), then the usual.
  const view = EXT.views[state.view];
  if (view && view.press && view.press(button, el)) return;
  if (state.view === 'settings') {
    const section = EXT.sections[state.section];
    if (section && section.press && !(el && el.dataset.section) && section.press(button, el)) return;
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
      if (state.view === 'home' && el && el.dataset.arg && EXT.actions['tile-options']) EXT.actions['tile-options'](el, el.dataset.arg);
      break;
  }
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
        if (state.current && !msg.running.includes(state.current)) { state.current = null; state.backdrop = null; }
      }
      for (const k of ['volume', 'brightness', 'battery', 'controller', 'alert', 'phone']) if (k in msg) state[k] = msg[k];
      if ('timer' in msg) state.timer = msg.timer;
      render();
      break;
    case 'input': press(msg.button); break;
    case 'show': {
      hideOpening();
      const apply = () => {
        state.current = msg.current || null;
        state.backdrop = msg.backdrop || null;
        // focus: the element to land on (an alert's row, the tile of an app that just closed);
        // section: the Settings section to open (an alert's action).
        if (msg.focus) state.memory[msg.view] = msg.focus;
        if (msg.section) state.section = msg.section;
        reset(msg.view);
        $('stage').classList.remove('blank');
      };
      if (!msg.backdrop) { apply(); break; }
      // Show the menu once its backdrop has loaded, so it does not flash the home screen first.
      const img = new Image();
      let done = false;
      const once = () => { if (!done) { done = true; apply(); } };
      img.onload = once;
      img.onerror = once;
      setTimeout(once, 400);
      img.src = msg.backdrop;
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

if (host) {
  host.addEventListener('message', (e) => onHost(e.data));
} else {
  // Demo data for a plain browser.
  onHost({ type: 'init', tiles: [
    { id: 'youtube', name: 'YouTube', glyph: 'youtube', color: '#FF5B52' },
    { id: 'twitch', name: 'Twitch', glyph: 'chat', color: '#B08CFF' },
    { id: 'stremio', name: 'Stremio', glyph: 'film', color: '#7C8CFF' },
    { id: 'jellyfin', name: 'Jellyfin', glyph: 'library', color: '#3DC0F0', running: true },
    { id: 'moonlight', name: 'Moonlight', glyph: 'moon', color: '#F5D16B' },
    { id: 'edge', name: 'Browser', glyph: 'globe', color: '#3CCB9A' }
  ], settings: { controller: true, battery: 'full' } });
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
