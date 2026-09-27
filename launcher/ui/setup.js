'use strict';
// First-run setup: the launcher in setup mode ("TV Box Setup"). Steps: welcome, controller
// check, TV, apps, install (setup.ps1 elevated, one Windows permission prompt), done.
//   From the host: {type:'init', apps, tv, controller, battery, canInstall} {type:'state', controller, battery}
//                  {type:'tv', tv} {type:'input', button} {type:'progress', steps, running, results, done}
//                  {type:'installed', ok, results, restartNeeded} {type:'declined'} {type:'toast', text, kind}
//   To the host:   {type:'ready'} {type:'install', apps, tiles} {type:'finish'} and the TV messages
//                  Settings uses (tvChoose, tvRefresh, tvTest)

const host = window.chrome && window.chrome.webview;
const send = (msg) => host ? host.postMessage(msg) : console.log('to host', msg);
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

function fit() {
  const s = Math.min(innerWidth / 1920, innerHeight / 1080);
  $('stage').style.transform = `translate(${(innerWidth - 1920 * s) / 2}px, ${(innerHeight - 1080 * s) / 2}px) scale(${s})`;
}
addEventListener('resize', fit);

const STEPS = ['welcome', 'controller', 'tv', 'apps', 'install', 'done'];
const BUTTONS = [['a', 'A'], ['b', 'B'], ['x', 'X'], ['y', 'Y'], ['lb', 'LB'], ['rb', 'RB'], ['lt', 'LT'], ['rt', 'RT'],
  ['select', 'Select'], ['start', 'Start'], ['home', 'Home']];
const STEP_NAMES = { RestorePoint: 'Restore point', Winget: 'App installer', Apps: 'Apps', Codecs: 'Video codecs',
  Edge: 'Edge settings', Power: 'Power and sleep', Updates: 'Windows updates', System: 'No pop-ups, network, time',
  AutoLogon: 'Sign-in without a password', Launcher: 'Home screen', PhoneRemote: 'Phone remote', DecodeCheck: 'Video decoding check' };

const state = {
  step: 0,
  apps: [],               // catalog: { id, name, glyph, color, default, type }
  picked: new Set(),
  pressed: new Set(),     // controller buttons seen on the controller step
  controller: false, battery: null,
  tv: { screen: null, profile: null, found: [] },
  canInstall: true,
  progress: null,         // setup-progress.json while installing
  result: null,           // { ok, results, restartNeeded } when setup has finished
  declined: false,        // the Windows permission prompt was declined
  focus: null,            // data-id of the focused element
};

// ---- Rendering ----------------------------------------------------------------------------

function button(id, label, primary, off) {
  return `<div class="su-btn${primary ? ' primary' : ''}${off ? ' off' : ''}" data-nav data-id="${id}">${esc(label)}</div>`;
}

function views() {
  const s = STEPS[state.step];
  if (s === 'welcome') return {
    main: '<div class="su-col" style="max-width:1200px"><h1 class="big">Let’s set up your TV box</h1>' +
      '<p>A few questions, then it installs your apps and sets up Windows for the TV. Keep the TV remote nearby.</p></div>',
    buttons: button('next', 'Start', true),
  };
  if (s === 'controller') {
    const all = BUTTONS.every(([b]) => state.pressed.has(b));
    const status = state.controller || state.pressed.size
      ? `Controller connected${state.battery && state.battery !== 'wired' ? ` · battery ${state.battery}` : ''}`
      : 'No controller yet: switch it on (hold its Home button), or carry on with a keyboard';
    return {
      main: '<div class="su-col"><div class="su-head"><h1>Press each button once</h1>' +
        `<p>${esc(status)}</p></div>` +
        `<div class="su-chips">${BUTTONS.map(([b, label]) =>
          `<span class="su-chip${state.pressed.has(b) ? ' done' : ''}">${state.pressed.has(b) ? icon('check', 28, 2.5) : ''}${label}</span>`).join('')}</div>` +
        `<span class="su-note">${all ? 'Every button works.' : 'A button that does not light up needs a look. Hold Home to skip this.'}</span></div>`,
      buttons: all ? button('back', 'Back') + button('next', 'Next', true) : '',
    };
  }
  if (s === 'tv') {
    const tv = state.tv, profile = tv.profile;
    const rows = tv.found.map((t) => {
      const picked = profile && profile.deviceId === t.id;
      return `<div class="su-row${picked ? ' picked' : ''}" data-nav data-id="tv:${esc(t.id)}">${icon('tv', 40)}` +
        `<div class="su-lines"><b>${esc(t.name)}</b><small>${esc(t.model)} · Roku TV${t.locked ? ' · control is off on the TV' : ''}</small></div>` +
        (picked ? `<span class="su-check">${icon('check', 36, 2.5)}</span>` : '') + '</div>';
    }).join('');
    const locked = tv.found.some((t) => profile && t.id === profile.deviceId && t.locked);
    const input = profile && profile.input ? `Plugged into HDMI ${profile.input}` : '';
    return {
      main: '<div class="su-two"><div class="su-grow">' +
        '<h1 style="margin-bottom:12px">Find your TV</h1>' +
        (rows || '<div class="su-row">No TV found on the network yet. The box can still be used; TV control can be set up later in Settings.</div>') +
        `<div class="su-row" data-nav data-id="tv-refresh">${icon('restart', 34)}Search again</div>` +
        (profile ? `<div class="su-row" data-nav data-id="tv-test">${icon('power', 34)}Test: turn the TV off, then back on</div>` : '') +
        (input ? `<span class="su-note">${esc(input)}${tv.screen ? ` · screen ${esc(tv.screen)}` : ''}</span>` : '') +
        '</div>' +
        '<div class="su-side"><h2>For a Roku TV, turn these on</h2>' +
        `<div class="su-item"><span class="${locked ? 'warn' : 'ok'}">${icon(locked ? 'warn' : 'check', 30, 2.5)}</span>` +
          '<div><b>Control by mobile apps</b><span>Settings › System › Advanced system settings. Set it to Enabled.</span></div></div>' +
        `<div class="su-item"><span class="warn">${icon('warn', 30, 2)}</span>` +
          '<div><b>Fast TV start</b><span>Settings › System › Power. Needed to turn the TV on over the network.</span></div></div>' +
        '</div></div>',
      buttons: button('back', 'Back') + button('next', 'Next', true),
    };
  }
  if (s === 'apps') {
    return {
      main: '<div class="su-col"><div class="su-head"><h1>Pick your apps</h1>' +
        '<p>Ticked apps install now and get a tile on the home screen. The rest stay in the library for later.</p></div>' +
        `<div class="su-apps">${state.apps.map((a) => {
          const on = state.picked.has(a.id);
          return `<div class="su-app${on ? ' on' : ''}" data-nav data-id="app:${esc(a.id)}" role="checkbox" aria-checked="${on}">` +
            `<span class="su-box">${on ? icon('check', 24, 3) : ''}</span>` +
            `<span style="display:flex;color:${esc(a.color)}">${icon(a.glyph, 32)}</span><span class="su-name">${esc(a.name)}</span></div>`;
        }).join('')}</div>` +
        `<span class="su-note">${state.picked.size} picked${state.canInstall ? '' : ' · this copy cannot install (not the setup exe)'}</span></div>`,
      buttons: button('back', 'Back') + button('install', 'Install', true),
    };
  }
  if (s === 'install') {
    const p = state.progress;
    const list = p ? p.steps : [];
    const results = (p && p.results) || {};
    const rows = list.map((name) => {
      const r = results[name];
      const cls = r === 'OK' ? 'ok' : r ? 'failed' : name === p.running ? 'running' : '';
      const mark = r === 'OK' ? icon('check', 32, 2.5) : r ? icon('warn', 30, 2) : name === p.running ? '<span class="su-spin"></span>' : '<span class="su-pending"></span>';
      const label = name === 'Apps' ? `Apps (${state.picked.size})` : (STEP_NAMES[name] || name);
      return `<div class="su-step ${cls}"><span class="su-icon">${mark}</span>${esc(label)}</div>`;
    }).join('');
    const lead = state.declined
      ? 'Windows did not get permission, so nothing was changed. Try again and choose Yes.'
      : p ? 'Installing and setting up. This takes a while; the box can be left alone.'
      : 'Windows now asks once for permission to change its settings: choose Yes.';
    return {
      main: `<div class="su-col"><div class="su-head"><h1>Setting up</h1><p>${esc(lead)}</p></div>` +
        `<div class="su-steps" style="grid-template-rows:repeat(${Math.ceil(list.length / 2)}, auto)">${rows}</div></div>`,
      buttons: state.declined ? button('back', 'Back') + button('retry', 'Try again', true) : '',
    };
  }
  // done
  const r = state.result || { results: {}, restartNeeded: [] };
  const failed = Object.entries(r.results || {}).filter(([, v]) => v !== 'OK');
  const lines = [];
  if (BUTTONS.every(([b]) => state.pressed.has(b))) lines.push(['ok', 'Controller works']);
  lines.push(state.tv.profile ? ['ok', `TV: ${state.tv.profile.name} turns on and off with the box`] : ['warn', 'TV control: set it up later in Settings']);
  lines.push(['ok', `${state.picked.size} apps on the home screen`]);
  for (const [name, v] of failed) lines.push(['warn', `${STEP_NAMES[name] || name}: ${String(v).replace(/^FAILED: /, '')}`]);
  if (r.restartNeeded && r.restartNeeded.length) lines.push(['warn', 'Restart the box once to finish (the name change)']);
  return {
    main: `<div class="su-col"><h1 class="big">${failed.length ? 'Almost set' : 'All set'}</h1>` +
      `<div class="su-summary">${lines.map(([k, text]) =>
        `<span class="su-line"><span class="${k}">${icon(k === 'ok' ? 'check' : 'warn', 34, 2.5)}</span>${esc(text)}</span>`).join('')}</div>` +
      '<p>Press Home any time to get back to your apps.</p></div>',
    buttons: button('finish', 'Go to home screen', true),
  };
}

let shownStep = -1;
function render() {
  const v = views();
  $('su-dots').innerHTML = STEPS.map((_, i) => `<span class="su-dot${i === state.step ? ' now' : i < state.step ? ' done' : ''}"></span>`).join('');
  $('su-step').textContent = `Step ${state.step + 1} of ${STEPS.length}`;
  const main = $('su-main');
  main.innerHTML = v.main;
  if (shownStep !== state.step) { main.style.animation = 'none'; void main.offsetWidth; main.style.animation = ''; shownStep = state.step; }
  $('su-buttons').innerHTML = v.buttons;
  const list = items();
  const keep = list.find((e) => e.dataset.id === state.focus);
  setFocus(keep || list.find((e) => e.classList.contains('primary')) || list[0] || null);
}

// ---- Focus ----------------------------------------------------------------------------------

function items() { return [...document.querySelectorAll('[data-nav]')]; }
function setFocus(el) {
  for (const f of document.querySelectorAll('.focused')) f.classList.remove('focused');
  if (!el) { state.focus = null; return; }
  el.classList.add('focused');
  state.focus = el.dataset.id;
}

function move(dir) {
  const cur = document.querySelector('[data-nav].focused');
  if (!cur) { render(); return; }
  const r = cur.getBoundingClientRect();
  const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
  let best = null, bestScore = Infinity;
  for (const el of items()) {
    if (el === cur) continue;
    const q = el.getBoundingClientRect();
    const dx = q.left + q.width / 2 - cx, dy = q.top + q.height / 2 - cy;
    const [main, side] = dir === 'right' ? [dx, dy] : dir === 'left' ? [-dx, dy] : dir === 'down' ? [dy, dx] : [-dy, dx];
    if (main <= 8) continue;
    const score = main + Math.abs(side) * 2;
    if (score < bestScore) { bestScore = score; best = el; }
  }
  if (best) setFocus(best);
}

// ---- Actions --------------------------------------------------------------------------------

function goStep(n) {
  state.step = Math.max(0, Math.min(STEPS.length - 1, n));
  state.focus = null;
  render();
}

function install() {
  state.declined = false;
  state.progress = null;
  const picked = state.apps.filter((a) => state.picked.has(a.id)).map((a) => a.id);
  send({ type: 'install', apps: picked, tiles: picked });
  goStep(STEPS.indexOf('install'));
}

function activate(id) {
  if (!id) return;
  if (id === 'next') goStep(state.step + 1);
  else if (id === 'back') goStep(state.step - 1);
  else if (id === 'install' || id === 'retry') install();
  else if (id === 'finish') send({ type: 'finish' });
  else if (id === 'tv-refresh') { toast('Searching for TVs…'); send({ type: 'tvRefresh' }); }
  else if (id === 'tv-test') { toast('Turning the TV off and back on…'); send({ type: 'tvTest' }); }
  else if (id.startsWith('tv:')) send({ type: 'tvChoose', id: id.slice(3) });
  else if (id.startsWith('app:')) {
    const app = id.slice(4);
    if (state.picked.has(app)) state.picked.delete(app); else state.picked.add(app);
    render();
  }
}

// Controller step: every button lights its chip and does nothing else, until all have been
// pressed; holding Home skips it. The keyboard works as usual (it is how to go on without a
// controller).
function press(button, fromController) {
  const step = STEPS[state.step];
  if (step === 'install' && !state.declined) return; // nothing to do but wait
  if (fromController && step === 'controller' && !BUTTONS.every(([b]) => state.pressed.has(b))) {
    if (button === 'homeHold') { goStep(state.step + 1); return; }
    if (BUTTONS.some(([b]) => b === button)) { state.pressed.add(button); render(); }
    return;
  }
  switch (button) {
    case 'up': case 'down': case 'left': case 'right': move(button); break;
    case 'a': activate(state.focus); break;
    case 'b': if (state.step > 0 && step !== 'done') goStep(state.step - 1); break;
  }
}

function toast(text, kind) {
  const el = document.createElement('div');
  el.className = 'toast' + (kind === 'warn' ? ' warn' : '');
  el.innerHTML = icon(kind === 'warn' ? 'warn' : 'info', 30, 2) + `<span>${esc(text)}</span>`;
  $('toasts').appendChild(el);
  setTimeout(() => el.remove(), 5000);
}

const KEYS = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right', Enter: 'a', ' ': 'a', Escape: 'b' };
addEventListener('keydown', (e) => {
  const b = KEYS[e.key];
  if (!b) return;
  e.preventDefault();
  // The keyboard moves on from the controller step even with buttons left unpressed.
  if (STEPS[state.step] === 'controller' && b === 'a' && !state.focus) { goStep(state.step + 1); return; }
  press(b, false);
});

// ---- Host messages --------------------------------------------------------------------------

function onHost(m) {
  switch (m.type) {
    case 'init':
      state.apps = m.apps || [];
      state.picked = new Set(state.apps.filter((a) => a.default).map((a) => a.id));
      if (m.tv) state.tv = m.tv;
      state.controller = !!m.controller; state.battery = m.battery || null;
      state.canInstall = m.canInstall !== false;
      render();
      break;
    case 'state':
      if ('controller' in m) state.controller = m.controller;
      if ('battery' in m) state.battery = m.battery;
      if (STEPS[state.step] === 'controller') render();
      break;
    case 'tv': state.tv = m.tv; if (STEPS[state.step] === 'tv') render(); break;
    case 'input': press(m.button, true); break;
    case 'progress': state.progress = m; if (STEPS[state.step] === 'install') render(); break;
    case 'declined': state.declined = true; render(); break;
    case 'installed':
      state.result = m;
      goStep(STEPS.indexOf('done'));
      break;
    case 'toast': toast(m.text, m.kind); break;
  }
}

if (host) {
  host.addEventListener('message', (e) => onHost(e.data));
} else {
  onHost({ type: 'init', controller: true, battery: 'full', apps: [
    { id: 'youtube', name: 'YouTube', glyph: 'youtube', color: '#FF5B52', default: true },
    { id: 'twitch', name: 'Twitch', glyph: 'chat', color: '#B08CFF', default: true },
    { id: 'stremio', name: 'Stremio', glyph: 'film', color: '#7C8CFF', default: true },
    { id: 'jellyfin', name: 'Jellyfin', glyph: 'library', color: '#3DC0F0', default: true },
    { id: 'moonlight', name: 'Moonlight', glyph: 'moon', color: '#F5D16B', default: true },
    { id: 'edge', name: 'Edge', glyph: 'globe', color: '#3CCB9A', default: true },
    { id: 'kodi', name: 'Kodi', glyph: 'tv', color: '#5AB0FF' },
    { id: 'vlc', name: 'VLC', glyph: 'play', color: '#FF8A1F' },
  ], tv: { screen: 'TCL 65S41CA', profile: { deviceId: 'x', name: 'Living room tv', input: 2 }, found: [{ id: 'x', name: 'Living room tv', model: '65S41-CA' }] } });
  // Demo: setup.html#3 opens step 4, for screenshots.
  if (location.hash) goStep(Number(location.hash.slice(1)));
}
fit();
render();
send({ type: 'ready' });
