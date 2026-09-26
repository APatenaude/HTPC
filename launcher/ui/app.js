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
  section: 'sleep',        // Settings section shown
  prefs: { idleMinutes: 30, deepSleepHours: 0, deepSleepHibernate: false, stayAwakeWhilePlaying: true }
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
  if (state.timer.endsAt === 'video') return 'Sleep after this video';
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
      (state.alert ? `<div class="pill alert">${icon(state.alert.glyph || 'info', 28, 2)}<span>${esc(state.alert.text)}</span></div>` : '') +
      `<div class="pill"${low ? ' style="color: var(--warn)"' : ''}>${icon('controller', 32)}<b>${esc(batteryText())}</b></div>` +
      `<div class="round" data-nav data-id="settings" data-act="settings" aria-label="Settings">${icon('sliders', 28, 2)}</div>` +
      `<div class="round" data-nav data-id="power" data-act="power" aria-label="Power">${icon('power', 28, 2)}</div>` +
    '</div>';
}

function renderTiles() {
  $('tiles').innerHTML = state.tiles.map((t) =>
    `<div class="tile" data-nav data-id="tile:${esc(t.id)}" data-act="launch" data-arg="${esc(t.id)}">` +
      (t.running ? '<span class="badge">Running</span>' : '') +
      `<span style="display:flex;color:${esc(t.color || 'inherit')}">${icon(t.glyph, 88)}</span>` +
      `<span class="name">${esc(t.name)}</span>` +
    '</div>').join('');
  $('home-hints').innerHTML = hints([['A', 'Open'], ['Home', 'Menu'], ['Hold Home', 'Power']]);
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
  $('menu-panel').innerHTML =
    `<div class="panel-head"><span class="time">${timeText(new Date())}</span>` +
      `<span class="pad">${icon('controller', 30)}${esc(batteryText())}</span></div>` +
    `<div class="row big" data-nav data-id="home" data-act="home">${icon('home', 38, 2)}Home screen</div>` +
    `<span class="section">Open apps</span>${apps}` +
    '<span class="section">Quick</span>' +
    `<div class="row slider" data-nav data-id="volume" data-slider="volume">${icon('speaker', 34)}` +
      `<div class="track"><div class="fill" style="width:${state.volume}%"></div></div><span class="value">${state.volume}</span></div>` +
    `<div class="row slider" data-nav data-id="brightness" data-slider="brightness">${icon('sun', 34)}` +
      `<div class="track"><div class="fill white" style="width:${state.brightness}%"></div></div><span class="value">${state.brightness}</span></div>` +
    '<div class="quicks">' +
      `<div class="quick" data-nav data-id="q-buttons" data-act="soon" data-arg="Button maps">${icon('controller', 34)}Buttons</div>` +
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
  deepSleepHours: [[0, 'Never'], [1, 'After 1 hour'], [3, 'After 3 hours'], [6, 'After 6 hours'], [12, 'After 12 hours']],
  deepSleepHibernate: [[false, 'Sleep'], [true, 'Hibernate']],
  stayAwakeWhilePlaying: [[false, 'Off'], [true, 'On']]
};

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
  const deep = p.deepSleepHours > 0;
  return '<header><h1>Sleep &amp; power</h1>' +
      '<p>Sleep turns the screen and TV off; the box stays ready and wakes with Home on the controller.</p></header>' +
    settingRow('idleMinutes', 'Sleep after', 'When nothing plays and nobody touches the controller', stepper('idleMinutes')) +
    `<div class="srow" data-nav data-id="set-timer" data-act="view" data-arg="timer">` +
      '<div class="text"><span class="label">Sleep timer</span><span class="caption">A countdown you set. Also in the Home menu.</span></div>' +
      `<div class="value link">${esc(state.timer ? timerText() : 'Off')}${icon('chevright', 28, 2)}</div></div>` +
    settingRow('deepSleepHours', 'Deep sleep',
      'After sleeping this long the box sleeps fully (about 1 W instead of a few). Home on the controller cannot wake it then: use the phone, the keyboard or the power button.',
      stepper('deepSleepHours')) +
    (deep ? settingRow('deepSleepHibernate', 'Deep sleep type', 'Hibernate if the box wakes up by itself or comes back to a black screen',
      '<div class="seg">' + CHOICES.deepSleepHibernate.map(([v, l]) => `<span${v === p.deepSleepHibernate ? ' class="on"' : ''}>${l}</span>`).join('') + '</div>') : '') +
    settingRow('stayAwakeWhilePlaying', 'Stay awake while video plays', 'Even if you don’t touch the controller for hours',
      `<div class="toggle${p.stayAwakeWhilePlaying ? ' on' : ''}"><span></span></div>`) +
    '<div class="sbuttons">' +
      '<div class="sbutton" data-nav data-id="set-sleepnow" data-act="power-action" data-arg="sleep">Sleep now</div>' +
      '<div class="sbutton" data-nav data-id="set-deepnow" data-act="power-action" data-arg="deepsleep">Deep sleep now</div>' +
    '</div>';
}

function renderSettings() {
  const nav = `<nav class="snav"><div class="snav-title">${icon('chevleft', 36, 2)}<span>Settings</span></div>` +
    SECTIONS.map(([id, glyph, label]) =>
      `<div class="sitem${id === state.section ? ' on' : ''}" data-nav data-id="s-${id}" data-section="${id}">${icon(glyph, 32)}${esc(label)}</div>`).join('') +
    '</nav>';
  const title = SECTIONS.find(([id]) => id === state.section)[2];
  const body = state.section === 'sleep' ? renderSleepSection()
    : `<header><h1>${esc(title)}</h1><p>This section comes in a later update.</p></header>`;
  $('settings').innerHTML = nav + `<div class="spane"><main>${body}</main>` +
    `<footer class="hints">${hints([['A', 'Change'], ['←→', 'Adjust'], ['B', 'Back']])}</footer></div>`;
}

function changeSetting(key, step) {
  const list = CHOICES[key];
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
  const under = state.view === 'confirm' ? state.stack[state.stack.length - 1] : null;
  renderStatus();
  renderTiles();
  if (state.view === 'menu' || under === 'menu') renderMenu();
  if (state.view === 'power') renderPower();
  if (state.view === 'timer') renderTimer();
  if (state.view === 'confirm') renderConfirm();
  if (state.view === 'settings') renderSettings();
  for (const v of ['home', 'menu', 'power', 'timer', 'confirm', 'settings']) $(v).classList.toggle('on', v === state.view || v === under);
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
  setFocus((state.view === 'home' ? list.find((e) => e.classList.contains('tile')) : null) ||
    (state.view === 'settings' ? list.find((e) => e.classList.contains('srow')) : null) ||
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
  if (best) setFocus(best);
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

function toast(text, kind) {
  const el = document.createElement('div');
  el.className = 'toast' + (kind === 'warn' ? ' warn' : '');
  el.innerHTML = icon(kind === 'warn' ? 'warn' : 'info', 30, 2) + `<span>${esc(text)}</span>`;
  $('toasts').appendChild(el);
  setTimeout(() => el.remove(), 5000);
}

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
      // On an app row: that app. Anywhere else in the menu: the app it was opened over.
      if (state.view !== 'menu') break;
      const id = (el && el.dataset.close) || state.current;
      const t = id && state.tiles.find((x) => x.id === id);
      if (t) { state.confirm = { id: t.id, name: t.name }; go('confirm'); }
      break;
    }
    case 'home':
      if (state.view === 'home') { state.current = null; state.backdrop = null; go('menu'); }
      else back();
      break;
    case 'homeHold': if (state.view !== 'power') go('power'); break;
  }
}

const KEYS = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right', Enter: 'a', ' ': 'a',
  Escape: 'b', Backspace: 'b', x: 'x', h: 'home', p: 'homeHold' };
addEventListener('keydown', (e) => {
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
      render();
      break;
    case 'blank': $('stage').classList.add('blank'); break;
    case 'opened':
      hideOpening();
      if (!msg.ok) toast(msg.text, 'warn');
      break;
    case 'state':
      if (msg.running) {
        for (const t of state.tiles) t.running = msg.running.includes(t.id);
        // The app the menu was opened over has closed: B and Home now lead home, not to it.
        if (state.current && !msg.running.includes(state.current)) { state.current = null; state.backdrop = null; }
      }
      for (const k of ['volume', 'brightness', 'battery', 'controller', 'alert']) if (k in msg) state[k] = msg[k];
      if ('timer' in msg) state.timer = msg.timer;
      render();
      break;
    case 'input': press(msg.button); break;
    case 'show': {
      hideOpening();
      const apply = () => {
        state.current = msg.current || null;
        state.backdrop = msg.backdrop || null;
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
    { id: 'edge', name: 'Edge', glyph: 'globe', color: '#3CCB9A' }
  ], settings: { controller: true, battery: 'full' } });
}

fit();
render();
// Demo only: index.html#settings (or #menu, #power...) opens that view, for screenshots.
if (!host && location.hash) go(location.hash.slice(1));
// Redraw when the minute (or the timer countdown) changes; render() keeps the focus.
let shown = '';
setInterval(() => {
  const now = timeText(new Date()) + timerText();
  if (now !== shown) { shown = now; render(); }
}, 1000);
send({ type: 'ready' });
