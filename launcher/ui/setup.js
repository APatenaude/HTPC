'use strict';
// First-run setup: the launcher in setup mode ("TV Box Setup"; design: First-run setup). Steps:
// welcome, controller check, Wi-Fi (only without a network cable), find the TV, the TV input
// (for TVs whose input the box sets or reads), apps, install (setup.ps1, no prompt: the setup exe
// asked Windows for permission once, as it opened), done. The phone remote is not a step: the
// home screen offers it.
//   From the host: {type:'init', apps, tv, controller, battery, canInstall, wired} {type:'state', controller, battery}
//                  {type:'tv.state', tv} {type:'tv.read', power, input} {type:'input', button}
//                  {type:'setupStarted'} {type:'progress', steps, running, results, done}
//                  {type:'installed', ok, results, restartNeeded} {type:'toast', text, kind}
//   To the host:   {type:'ready'} {type:'install', apps, tiles} {type:'finish'} {type:'restart'} and the
//                  TV messages (tv.js)

const host = window.chrome && window.chrome.webview;
const send = (msg) => host ? host.postMessage(msg) : console.log('to host', msg);
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

function fit() {
  const s = Math.min(innerWidth / 1920, innerHeight / 1080);
  $('stage').style.transform = `translate(${(innerWidth - 1920 * s) / 2}px, ${(innerHeight - 1080 * s) / 2}px) scale(${s})`;
}
addEventListener('resize', fit);

const BUTTONS = [['a', 'A'], ['b', 'B'], ['x', 'X'], ['y', 'Y'], ['lb', 'LB'], ['rb', 'RB'], ['lt', 'LT'], ['rt', 'RT'],
  ['select', 'Select'], ['start', 'Start'], ['home', 'Home']];
const STEP_NAMES = { RestorePoint: 'Restore point', Winget: 'App installer', Apps: 'Apps', Codecs: 'Video codecs',
  Edge: 'Edge settings', Power: 'Power and sleep', Drivers: 'Drivers', Updates: 'Windows updates', Bluetooth: 'Bluetooth driver', System: 'No pop-ups, network, time',
  AutoLogon: 'Sign-in without a password', Launcher: 'Home screen', Library: 'Installing from the TV', PhoneRemote: 'Phone remote',
  Shell: 'Start straight into the home screen', DecodeCheck: 'Video decoding check' };

const state = {
  step: 'welcome',
  apps: [],               // catalog: { id, name, glyph, color, default, type, category }
  categories: [],         // the catalog's: { id, name }, in the order they show
  picked: new Set(),
  pressed: new Set(),     // controller buttons seen on the controller step
  controller: false, battery: null,
  wired: true,            // a network cable: no Wi-Fi step
  tv: { screen: null, profile: null, found: [], methods: [], profiles: [], caps: {}, status: 'unbound', port: 0 },
  tvHint: null,           // a brand picked under "Not listed?" (its checklist shows)
  dialog: false,          // "How should the box control this TV?" is open
  read: null,             // what the TV says it shows: { power, input } (input step)
  canInstall: true,
  starting: false,        // install pressed, setup.ps1 not started yet
  progress: null,         // setup-progress.json while installing
  result: null,           // { ok, results, restartNeeded } when setup has finished
  focus: null,            // data-id of the focused element
};

// ---- Steps ------------------------------------------------------------------------------------

/** The steps as they stand: Wi-Fi only without a cable; the input only for a TV the box controls. */
function steps() {
  const list = ['welcome', 'controller'];
  if (state.wired === false) list.push('wifi');
  list.push('tv');
  const p = state.tv.profile;
  if (p && p.method !== 'none' && state.tv.caps && (state.tv.caps.input || state.tv.caps.readInput)) list.push('input');
  list.push('apps', 'install', 'done');
  return list;
}

function index() { return Math.max(0, steps().indexOf(state.step)); }

/** A setup.ps1 result "skipped: <why>" (not OK, not a failure). */
const skipped = (v) => /^skipped/.test(String(v || ''));

// ---- Rendering ----------------------------------------------------------------------------

function button(id, label, primary, off) {
  return `<div class="su-btn${primary ? ' primary' : ''}${off ? ' off' : ''}" data-nav data-id="${id}">${esc(label)}</div>`;
}

function views() {
  const s = state.step;
  if (s === 'welcome') return {
    main: '<div class="su-col" style="max-width:1200px"><h1 class="big">Let’s set up your TV box</h1>' +
      '<p>A few questions, then it installs your apps and sets up Windows for the TV. Keep the TV remote nearby.</p>' +
      // The one prompt came before this page (SetupElevation.cs), for cmd.exe, not this exe.
      '<span class="su-note">Windows asked for permission once, as setup opened: its prompt names Windows Command Processor (Microsoft), which starts setup from Program Files. Nothing else will ask.</span></div>',
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
  if (s === 'wifi') return {
    // The Wi-Fi component (WifiUI, wifi.js, the same as Settings › Wi-Fi) draws its network list
    // into #su-wifi (see wifiHtml below). setup.html loads its script before this one.
    main: '<div class="su-col"><div class="su-head"><h1>Connect to your network</h1>' +
      '<p>No network cable is plugged in. Pick your Wi-Fi network, or plug in a cable. The box finds the TV and installs apps over the network.</p></div>' +
      `<div id="su-wifi">${wifiHtml()}</div></div>`,
    buttons: button('back', 'Back') + button('next', 'Next', true),
  };
  if (s === 'tv') return tvView();
  if (s === 'input') return inputView();
  if (s === 'apps') {
    // Windows' permission was asked for once, as the setup exe opened: Install asks nothing more.
    const note = state.starting ? 'Starting…'
      : `${state.picked.size} picked · ${state.canInstall ? 'Install starts right away, with no more questions' : 'installing needs TV Box Setup: open it from its own icon'}`;
    const app = (a) => {
      const on = state.picked.has(a.id);
      return `<div class="su-app${on ? ' on' : ''}" data-nav data-id="app:${esc(a.id)}" role="checkbox" aria-checked="${on}">` +
        `<span class="su-box">${on ? icon('check', 24, 3) : ''}</span>` +
        `<span style="display:flex;color:${esc(a.color)}">${icon(a.glyph, 32)}</span><span class="su-name">${esc(a.name)}</span></div>`;
    };
    // By category, as in Add a tile's library (the catalog's, in its order; anything else last,
    // under Other), each under its heading in the list that scrolls.
    const known = new Set(state.categories.map((c) => c.id));
    const groups = [...state.categories, { id: '', name: 'Other' }]
      .map((c) => ({ ...c, apps: state.apps.filter((a) => (known.has(a.category) ? a.category : '') === c.id) })).filter((g) => g.apps.length);
    return {
      main: '<div class="su-col"><div class="su-head"><h1>Pick your apps</h1>' +
        '<p>Ticked apps install now and get a tile on the home screen. The rest stay in the library for later.</p></div>' +
        `<div class="su-apps">${groups.map((g) => `<div class="su-group"><span class="su-cat">${esc(g.name)}</span>` +
          `<div class="su-grid">${g.apps.map(app).join('')}</div></div>`).join('')}</div>` +
        `<span class="su-note">${esc(note)}</span></div>`,
      buttons: state.starting ? '' : button('back', 'Back') + button('install', 'Install', true),
    };
  }
  if (s === 'install') {
    const p = state.progress;
    const list = p ? p.steps : [];
    const results = (p && p.results) || {};
    const rows = list.map((name) => {
      const r = results[name];
      // "skipped: <why>": the step did not apply here (no launcher given, a virtual machine).
      const cls = r === 'OK' ? 'ok' : skipped(r) ? 'skipped' : r ? 'failed' : name === p.running ? 'running' : '';
      const mark = r === 'OK' ? icon('check', 32, 2.5) : skipped(r) ? icon('info', 30, 2) : r ? icon('warn', 30, 2) : name === p.running ? '<span class="su-spin"></span>' : '<span class="su-pending"></span>';
      const label = (name === 'Apps' ? `Apps (${state.picked.size})` : (STEP_NAMES[name] || name)) + (skipped(r) ? ' (skipped)' : '');
      return `<div class="su-step ${cls}"><span class="su-icon">${mark}</span>${esc(label)}</div>`;
    }).join('');
    return {
      main: '<div class="su-col"><div class="su-head"><h1>Setting up</h1><p>Installing and setting up. This takes a while; the box can be left alone.</p></div>' +
        `<div class="su-steps" style="grid-template-rows:repeat(${Math.ceil(list.length / 2)}, auto)">${rows}</div></div>`,
      buttons: '',
    };
  }
  // done
  const r = state.result || { results: {}, restartNeeded: [] };
  const failed = Object.entries(r.results || {}).filter(([, v]) => v !== 'OK' && !skipped(v));
  const skips = Object.entries(r.results || {}).filter(([, v]) => skipped(v));
  const lines = [];
  if (BUTTONS.every(([b]) => state.pressed.has(b))) lines.push(['ok', 'Controller works']);
  const tv = state.tv, p = tv.profile;
  if (p && p.method === 'none') lines.push(['ok', 'TV: use its own remote for power and input']);
  else if (p && tv.status === 'ok') {
    lines.push(['ok', `TV: ${p.name} turns on and off with the box${p.beta ? ' (beta)' : ''}`]);
    if (p.input) lines.push(['ok', `TV input: HDMI ${p.input}`]);
  } else lines.push(['warn', 'TV control: set it up later in Settings › TV']);
  lines.push(['ok', `${state.picked.size} apps on the home screen`]);
  for (const [name, v] of failed) lines.push(['warn', `${STEP_NAMES[name] || name}: ${String(v).replace(/^FAILED: /, '')}`]);
  for (const [name, v] of skips) lines.push(['warn', `${STEP_NAMES[name] || name}: skipped, ${String(v).replace(/^skipped: /, '')}`]);
  const restart = r.restartNeeded && r.restartNeeded.length;
  if (restart) lines.push(['warn', r.restartNeeded.includes('shell')
    ? 'Restart the box once to finish: from then on it starts straight into this home screen'
    : 'Restart the box once to finish setting it up']); // a new name, drivers, Windows servicing...
  // Steps that did not work: the list can be taller than the screen, so its lines take the focus
  // (up from the buttons) and it scrolls to them.
  return {
    main: `<div class="su-col"><h1 class="big">${failed.length ? 'Almost set' : 'All set'}</h1>` +
      `<div class="su-summary">${lines.map(([k, text], n) =>
        `<span class="su-line"${failed.length ? ` data-nav data-id="line:${n}"` : ''}><span class="${k}">${icon(k === 'ok' ? 'check' : 'warn', 34, 2.5)}</span>${esc(text)}</span>`).join('')}</div>` +
      '<p>Press Home any time to get back to your apps.</p></div>',
    // "Restart now" is live once setup has finished (the host restarts Windows).
    buttons: button('restart', 'Restart now', !!restart, !state.result) + button('finish', 'Go to home screen', !restart),
  };
}

function tvView() {
  const tv = state.tv, p = tv.profile;
  const rows = TvUi.foundRows(tv, 'su-row');
  const side = state.tvHint || (p && p.method !== 'none' ? p.method : null) || ((tv.found[0] || {}).method) || 'roku';
  const status = TvUi.statusLine(tv);
  const showStatus = ['locked', 'missing', 'paused', 'unavailable'].includes(tv.status) || tv.handsOff;
  return {
    // The TVs found and what follows them scroll (su-tvlist) when there are many.
    main: '<div class="su-two"><div class="su-grow">' +
      '<h1 style="margin-bottom:12px">Find your TV</h1>' +
      '<div class="su-tvlist">' +
      (rows || '<div class="su-row">No TV found on the network yet. Check it is on and connected, or pick its brand below.</div>') +
      `<div class="su-row" data-nav data-id="tv-other">${icon('pencil', 34)}` +
        `${p && p.method === 'none' ? 'No TV control (its own remote). Change?' : 'Not listed? Pick your TV’s brand, or skip TV control'}</div>` +
      `<div class="su-row" data-nav data-id="tv-refresh">${icon('restart', 34)}Search again</div>` +
      (tv.caps && tv.caps.test && p ? `<div class="su-btn" data-nav data-id="tv-test" style="align-self:flex-start;margin-top:12px">Test: turn the TV off and back on</div>` : '') +
      (showStatus ? `<div class="tv-status ${status.kind}"><span></span>${esc(status.text)}</div>` : '') +
      '</div></div>' +
      // Pairing the picked TV takes the side panel's place while it runs (or while the TV waits for it).
      (TvUi.pairHtml(tv) ? `<div class="tv-side">${TvUi.pairHtml(tv)}</div>` : p && p.method === 'none' ? '' : TvUi.checklistHtml(tv, side)) +
      '</div>' + (state.dialog ? tvDialog() : ''),
    buttons: button('back', 'Back') + button('next', 'Next', true),
  };
}

function tvDialog() {
  const tv = state.tv;
  const found = TvUi.foundRows(tv, 'tv-mrow');
  // The TVs and brands scroll inside the dialog (to the focus: setFocus), as in Settings.
  return '<div class="tv-dialog-wrap"><div class="tv-dialog">' +
    `<h2>How should the box control ${esc((tv.profile && tv.profile.name) || 'this TV')}?</h2>` +
    `<p>${found ? 'Pick your TV, its brand to see what to turn on, or skip TV control.' : 'Pick your TV’s brand to see what to turn on, or skip TV control.'}</p>` +
    '<div class="tv-list">' +
      (found ? `<span class="tv-label">TVs on your network</span>${found}<span class="tv-label">Not listed?</span>` : '') +
      TvUi.methodRows(tv, 'tv-mrow', state.tvHint) + '</div></div></div>';
}

function inputView() {
  const tv = state.tv, p = tv.profile;
  const max = Math.max(4, tv.port || 0, p.input || 0);
  const say = TvUi.inputStatus(tv, state.read);
  const inputs = Array.from({ length: max }, (_, i) => i + 1).map((n) =>
    `<div class="tv-input${p.input === n ? ' picked' : ''}" data-nav data-id="tvin:${n}">HDMI ${n}` +
      (tv.port === n ? '<small>from the cable</small>' : '') + '</div>').join('');
  return {
    main: '<div class="su-col" style="gap:36px"><div class="su-head"><h1>Which input is this box plugged into?</h1>' +
      `<p>${esc(TvUi.portNote(tv))} The box switches ${esc(p.name || 'the TV')} to it when it turns it on.</p></div>` +
      `<div class="tv-inputs">${inputs}</div>` +
      (say ? `<div class="tv-say ${say.kind}">${icon(say.kind === 'ok' ? 'check' : say.kind === 'alarm' ? 'warn' : 'info', 34, 2.5)}<span>${esc(say.text)}</span></div>` : '') +
      (say && say.kind === 'alarm' ? `<div class="su-btn" data-nav data-id="tv-again" style="align-self:flex-start">Pick the TV again</div>` : '') +
      '</div>',
    buttons: button('back', 'Back') + button('next', 'Next', true),
  };
}

let shownStep = null;
function render() {
  const v = views();
  const list = steps();
  const i = index();
  patchHtml($('su-dots'), list.map((_, n) => `<span class="su-dot${n === i ? ' now' : n < i ? ' done' : ''}"></span>`).join(''));
  $('su-step').textContent = `Step ${i + 1} of ${list.length}`;
  const main = $('su-main');
  // Where the focus was: if its row goes (a network or a TV no longer found), the row now
  // nearest that place takes it, not the first button.
  const was = shownStep === state.step && document.querySelector('[data-nav].focused');
  const at = was && was.getBoundingClientRect();
  // A new step is drawn afresh (it animates in); the same step again (the TV search, the Wi-Fi
  // scans, the controller's buttons) is updated in place: the TV dialog does not pop in again,
  // the lists keep their scroll, the focus its ring.
  if (shownStep !== state.step) {
    main.innerHTML = v.main;
    main.style.animation = 'none'; void main.offsetWidth; main.style.animation = '';
    shownStep = state.step;
  } else patchHtml(main, v.main);
  patchHtml($('su-buttons'), v.buttons);
  if (state.step === 'wifi' && typeof WifiUI !== 'undefined') WifiUI.afterRender();
  for (const box of main.querySelectorAll(SCROLLERS)) listEdges(box);
  const nav = items();
  const keep = nav.find((e) => e.dataset.id === state.focus) || (at && state.focus && was.dataset.id === state.focus && !was.isConnected ? nearestTo(at, nav) : null);
  // While pairing, its controls first (the first key, or its button).
  const pairFirst = nav.find((e) => e.closest('.tv-pair'));
  setFocus(keep || pairFirst || nav.find((e) => e.classList.contains('picked')) || nav.find((e) => e.classList.contains('primary')) || nav[0] || null);
}

// The element among list whose middle is closest to rect's (a row that went: its neighbour).
function nearestTo(rect, list) {
  const cx = rect.left + rect.width / 2, cy = rect.top + rect.height / 2;
  let best = null, bestD = Infinity;
  for (const e of list) {
    const q = e.getBoundingClientRect();
    const d = Math.abs(q.left + q.width / 2 - cx) + 2 * Math.abs(q.top + q.height / 2 - cy);
    if (d < bestD) { bestD = d; best = e; }
  }
  return best;
}

/**
 * The Wi-Fi step's list: the Wi-Fi component (wifi.js, WifiUI, the same as Settings › Wi-Fi)
 * draws its data-nav rows (the controller moves through them like any other) and sends its
 * "wifi.*" messages to the host's [UiMessages("wifi.")] handler. goStep starts and stops its
 * scanning with the step; press and onHost hand it its buttons and messages; render calls its
 * afterRender.
 */
function wifiHtml() {
  return typeof WifiUI === 'undefined' ? '<div class="su-row">Plug in a network cable, then press Next.</div>' : WifiUI.html();
}

// Puts html into el keeping the elements that stay (same tag and place, same data-id), as
// app.js's patchHtml: what changed is updated in place, the rest is left alone. Text fields are
// always new ones (wifi.js wires each field it draws).
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

// Scrolls box (overflow hidden: only this scrolls it) so el shows whole with room for its ring;
// the first element takes it back to the top. The stage is scaled: screen pixels are turned back
// into the box's own. The box's ends fade where there is more (.more-up, .more-down).
function scrollIntoBox(el, box, room) {
  const b = box.getBoundingClientRect(), r = el.getBoundingClientRect();
  const scale = b.height / box.offsetHeight || 1;
  if (box.querySelector('[data-nav]') === el) box.scrollTop = 0;
  else if (r.bottom > b.bottom - room * scale) box.scrollTop += (r.bottom - b.bottom) / scale + room;
  else if (r.top < b.top + room * scale) box.scrollTop -= (b.top - r.top) / scale + room;
  listEdges(box);
}
function listEdges(box) {
  box.classList.toggle('more-up', box.scrollTop > 2);
  box.classList.toggle('more-down', box.scrollTop + box.clientHeight < box.scrollHeight - 2);
}

function wifiRedraw(focusId) {
  if (focusId) state.focus = focusId;
  if (state.step === 'wifi') render();
}

// ---- Focus ----------------------------------------------------------------------------------

/** What the controller moves between: the dialog's rows while it is open, else the page. */
function items() {
  const scope = document.querySelector('.tv-dialog-wrap') || document;
  return [...scope.querySelectorAll('[data-nav]')].filter((e) => !e.classList.contains('off'));
}

function setFocus(el) {
  for (const f of document.querySelectorAll('.focused')) f.classList.remove('focused');
  if (!el) { state.focus = null; return; }
  el.classList.add('focused');
  state.focus = el.dataset.id;
  // The lists that scroll (Wi-Fi networks, the TV dialog's TVs and brands, the Done page's
  // lines, the apps): a row the focus moves to comes into view, ring and all; an app in its
  // category's first row, with the category's heading above it.
  const box = el.closest(SCROLLERS);
  if (box) scrollIntoBox(el, box, 16);
  const group = el.closest('.su-group');
  if (box && group && el.offsetTop - group.querySelector('.su-app').offsetTop <= 1) {
    const head = group.querySelector('.su-cat').getBoundingClientRect(), b = box.getBoundingClientRect();
    const scale = b.height / box.offsetHeight || 1;
    if (head.top < b.top + 8 * scale) { box.scrollTop -= (b.top - head.top) / scale + 8; listEdges(box); }
  }
}

// The lists that scroll: the Wi-Fi networks (and the Wi-Fi step, for its forms), the TVs found,
// the TV dialog's list, the Done page's lines.
const SCROLLERS = '.wifi-scroll, .tv-list, .su-summary, .su-tvlist, #su-wifi, .su-apps';

function move(dir) {
  const cur = document.querySelector('[data-nav].focused');
  if (!cur) { render(); return; }
  const r = cur.getBoundingClientRect();
  const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
  const ownBox = cur.closest(SCROLLERS);
  let best = null, bestScore = Infinity;
  for (const el of items()) {
    if (el === cur) continue;
    const q = el.getBoundingClientRect();
    // A row scrolled out of its list is not a place to go from outside that list (from Next up
    // into the list, from the rows above it down); within it, it is (the list scrolls to it).
    const box = el.closest(SCROLLERS);
    if (box && box !== ownBox) {
      const b = box.getBoundingClientRect(), x = q.left + q.width / 2, y = q.top + q.height / 2;
      if (x < b.left || x > b.right || y < b.top || y > b.bottom) continue;
    }
    const dx = q.left + q.width / 2 - cx, dy = q.top + q.height / 2 - cy;
    const [main, side] = dir === 'right' ? [dx, dy] : dir === 'left' ? [-dx, dy] : dir === 'down' ? [dy, dx] : [-dy, dx];
    if (main <= 8) continue;
    const score = main + Math.abs(side) * 2;
    if (score < bestScore) { bestScore = score; best = el; }
  }
  if (best) setFocus(best);
}

// ---- Actions --------------------------------------------------------------------------------

let readTimer = null;

/** The TV steps tell the host they show (search often, bind on evidence); the input step asks the TV what it shows. */
function goStep(name) {
  const was = state.step;
  state.step = name;
  state.focus = null;
  state.dialog = false;
  // The Wi-Fi step scans only while it shows.
  if (typeof WifiUI !== 'undefined' && (name === 'wifi') !== (was === 'wifi')) {
    if (name === 'wifi') WifiUI.start({ changed: wifiRedraw, toast });
    else WifiUI.stop();
  }
  const tvNow = name === 'tv' || name === 'input', tvBefore = was === 'tv' || was === 'input';
  if (tvNow !== tvBefore) send({ type: 'tv.showing', on: tvNow });
  clearInterval(readTimer);
  if (name === 'input') { state.read = null; send({ type: 'tv.read' }); readTimer = setInterval(() => send({ type: 'tv.read' }), 4000); }
  render();
}

function stepBy(delta) {
  const list = steps();
  goStep(list[Math.max(0, Math.min(list.length - 1, index() + delta))]);
}

function install() {
  state.starting = true;
  state.progress = null;
  const picked = state.apps.filter((a) => state.picked.has(a.id)).map((a) => a.id);
  send({ type: 'install', apps: picked, tiles: picked });
  render();
}

function activate(id) {
  if (!id) return;
  if (TvUi.pairAction(id)) { render(); return; }
  if (id === 'next') stepBy(1);
  else if (id === 'back') stepBy(-1);
  else if (id === 'install') install();
  else if (id === 'finish') send({ type: 'finish' });
  else if (id === 'restart') { if (state.result) send({ type: 'restart' }); }
  else if (id === 'tv-refresh') { toast('Searching for TVs…'); send({ type: 'tv.refresh' }); }
  else if (id === 'tv-test') { toast('Turning the TV off and back on…'); send({ type: 'tv.test' }); }
  else if (id === 'tv-other') { state.dialog = true; state.focus = null; render(); }
  else if (id === 'tv-again') goStep('tv');
  else if (id.startsWith('tvpick:')) { send({ type: 'tv.choose', id: id.slice(7) }); state.tvHint = null; closeDialog(); }
  else if (id === 'tvbrand:none') { send({ type: 'tv.none' }); state.tvHint = null; closeDialog(); }
  else if (id.startsWith('tvbrand:')) {
    // A brand only shows what to turn on and searches again: the user then picks the TV by its
    // name (the only TV of that brand found can be someone else's).
    state.tvHint = id.slice(8);
    toast('Searching for TVs…');
    send({ type: 'tv.refresh' });
    closeDialog();
  }
  else if (id.startsWith('tvin:')) {
    const n = Number(id.slice(5));
    state.tv.profile.input = n;
    send({ type: 'tv.input', input: n });
    render();
  }
  else if (id.startsWith('app:')) {
    const app = id.slice(4);
    if (state.picked.has(app)) state.picked.delete(app); else state.picked.add(app);
    render();
  }
}

function closeDialog() { state.dialog = false; state.focus = null; render(); }

// Controller step: every button lights its chip and does nothing else, until all have been
// pressed; holding Home skips it. The keyboard works as usual (it is how to go on without a
// controller).
function press(button, fromController) {
  const step = state.step;
  if (step === 'install' || (step === 'apps' && state.starting)) return; // nothing to do but wait
  if (fromController && step === 'controller' && !BUTTONS.every(([b]) => state.pressed.has(b))) {
    if (button === 'homeHold') { stepBy(1); return; }
    if (BUTTONS.some(([b]) => b === button)) { state.pressed.add(button); render(); }
    return;
  }
  const el = document.querySelector('[data-nav].focused');
  // A value row (data-edit: the hidden network's Security) changes only once A has picked it,
  // as in Settings: then left/right change it, A or B put it down, up/down move on.
  if (el && el.dataset.edit !== undefined) {
    const on = el.classList.contains('editing');
    if (!on && button === 'a') { el.classList.add('editing'); return; }
    if (on) {
      if (button === 'a' || button === 'b') { el.classList.remove('editing'); return; }
      if (button === 'left' || button === 'right') { if (typeof WifiUI !== 'undefined') WifiUI.press(button, el); return; }
      if (button !== 'up' && button !== 'down') return;
      el.classList.remove('editing');
    } else if (button === 'left' || button === 'right') { move(button); return; }
  }
  // The Wi-Fi list and its password form take their own buttons first (A on a network, B in the form).
  if (step === 'wifi' && typeof WifiUI !== 'undefined' && WifiUI.press(button, el)) return;
  switch (button) {
    case 'up': case 'down': case 'left': case 'right': move(button); break;
    case 'a': activate(state.focus); break;
    case 'b':
      if (state.dialog) closeDialog();
      else if (step !== 'welcome' && step !== 'done') stepBy(-1);
      break;
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
  // A key typed into a field (the Wi-Fi password) stays in it (textinput.js).
  if (typeof keyGuard === 'function' && keyGuard(e)) return;
  const b = KEYS[e.key];
  if (!b) return;
  e.preventDefault();
  // The keyboard moves on from the controller step even with buttons left unpressed.
  if (state.step === 'controller' && b === 'a' && !state.focus) { stepBy(1); return; }
  press(b, false);
});

// ---- Host messages --------------------------------------------------------------------------

function onHost(m) {
  if (typeof WifiUI !== 'undefined' && WifiUI.handle(m)) return; // wifi.state, wifi.result
  switch (m.type) {
    // The on-screen keyboard's and the phone's typing, into the focused field (textinput.js).
    case 'text.insert': if (typeof textInsert === 'function') textInsert(m.text); break;
    case 'text.key': if (typeof textKey === 'function') textKey(m.key); break;
    case 'text.keyboardAt': if (typeof textKeyboardAt === 'function') textKeyboardAt(m.top); break;
    case 'init':
      state.apps = m.apps || [];
      state.categories = m.categories || [];
      state.picked = new Set(state.apps.filter((a) => a.default).map((a) => a.id));
      if (m.tv) state.tv = m.tv;
      if ('wired' in m) state.wired = m.wired;
      state.controller = !!m.controller; state.battery = m.battery || null;
      state.canInstall = m.canInstall !== false;
      render();
      break;
    case 'state':
      if ('controller' in m) state.controller = m.controller;
      if ('battery' in m) state.battery = m.battery;
      if (state.step === 'controller') render();
      break;
    case 'tv.state':
      state.tv = m.tv;
      // The input step goes when the TV turned out not to be controllable ("No TV control").
      if (!steps().includes(state.step)) { goStep('tv'); break; } // through goStep: stops the input step's reads
      if (state.step === 'tv' || state.step === 'input' || state.step === 'done') render();
      break;
    case 'tv.read': state.read = { power: m.power, input: m.input }; if (state.step === 'input') render(); break;
    case 'input': press(m.button, true); break;
    case 'setupStarted': state.starting = false; goStep('install'); break;
    case 'progress': state.starting = false; state.progress = m; if (state.step === 'install') render(); break;
    case 'installed':
      state.starting = false;
      state.result = m;
      goStep('done');
      break;
    case 'toast': toast(m.text, m.kind); break;
  }
}

if (host) {
  host.addEventListener('message', (e) => onHost(e.data));
} else {
  // Demo data in a plain browser. setup.html#tv?demo=lg opens that step with that TV sample
  // (tv.js: roku, none-found, locked, twins, unbound, lg, paused, none, missing); more flags:
  // dialog=1 (the method dialog), read=3 (the TV says HDMI 3), wired=0 (Wi-Fi step),
  // starting=1 (apps), restart=1 (done).
  const [name, query] = location.hash.slice(1).split('?');
  const q = new URLSearchParams(query || '');
  // The real catalog as setup gets it (38: all but Spotify and RetroBat, which install only from
  // the library), in its categories: more than one screen, as on a box, so the audit walks a list
  // that has to scroll (a list of 8 hid that it didn't).
  const categories = [['movies', 'Movies & shows'], ['canada', 'Canadian TV'], ['sports', 'Sports'], ['music', 'Music'],
    ['games', 'Games'], ['media', 'Your media & tools']].map(([id, name]) => ({ id, name }));
  const apps = [
    ['youtube', 'YouTube', 'youtube', '#FF5B52', 'movies', true], ['twitch', 'Twitch', 'chat', '#B08CFF', 'movies', true],
    ['stremio', 'Stremio', 'film', '#7C8CFF', 'movies', true], ['jellyfin', 'Jellyfin', 'library', '#3DC0F0', 'media', true],
    ['moonlight', 'Moonlight', 'moon', '#F5D16B', 'games', true], ['edge', 'Browser', 'globe', '#3CCB9A', 'media', true],
    ['kodi', 'Kodi', 'play', '#5AB0FF', 'media'], ['vlc', 'VLC', 'play', '#FF8A1F', 'media'], ['plex', 'Plex HTPC', 'play', '#F5B82E', 'media'],
    ['feishin', 'Feishin', 'music', '#FF7AB6', 'music'], ['steam', 'Steam', 'controller', '#66C0F4', 'games'],
    ['playnite', 'Playnite', 'controller', '#B08CFF', 'games'], ['retroarch', 'RetroArch', 'controller', '#5AB0FF', 'games'],
    ['netflix', 'Netflix', 'play', '#FF4B55', 'movies'], ['disneyplus', 'Disney+', 'play', '#4D8DFF', 'movies'],
    ['primevideo', 'Prime Video', 'play', '#2BB0F5', 'movies'], ['crunchyroll', 'Crunchyroll', 'play', '#FF8A2B', 'movies'],
    ['hbomax', 'HBO Max', 'play', '#8A7BFF', 'movies'], ['appletv', 'Apple TV+', 'play', '#D9D8D4', 'movies'],
    ['paramountplus', 'Paramount+', 'play', '#3D8BFF', 'movies'], ['tubi', 'Tubi', 'play', '#FFD43B', 'movies'],
    ['plutotv', 'Pluto TV', 'tv', '#F5E642', 'movies'], ['crave', 'Crave', 'play', '#3FA9F5', 'movies'],
    ['cbcgem', 'CBC Gem', 'play', '#FF4B4B', 'canada'], ['toutv', 'ICI TOU.TV', 'play', '#F25F5C', 'canada'],
    ['telequebec', 'Télé-Québec', 'play', '#3DC0F0', 'canada'], ['tvaplus', 'TVA+', 'tv', '#6FA8FF', 'canada'],
    ['illicoplus', 'illico+', 'play', '#E6D65A', 'canada'], ['onf', 'ONF', 'film', '#FF7A6B', 'canada'],
    ['rds', 'RDS', 'tv', '#FF5A4E', 'sports'], ['tsn', 'TSN', 'tv', '#E8485C', 'sports'], ['sportsnet', 'Sportsnet+', 'tv', '#2F9BFF', 'sports'],
    ['ohdio', 'OHdio', 'speaker', '#B08CFF', 'music'], ['youtubemusic', 'YouTube Music', 'music', '#FF4E45', 'music'],
    ['applemusic', 'Apple Music', 'music', '#FF5A73', 'music'], ['geforcenow', 'GeForce NOW', 'controller', '#8CD13C', 'games'],
    ['xboxcloud', 'Xbox Cloud Gaming', 'controller', '#52C443', 'games'], ['luna', 'Amazon Luna', 'controller', '#A07CFF', 'games'],
  ].map(([id, name, glyph, color, category, dflt]) => ({ id, name, glyph, color, category, default: !!dflt }));
  onHost({ type: 'init', controller: true, battery: 'full', wired: q.get('wired') !== '0', apps, categories, tv: TvUi.demo(q.get('demo') || 'roku') });
  // setup.html#audit (#audit?page=...): the UI audit's setup pages (audit.js), loaded before the
  // page's load event, which waits for it.
  if (name && name.startsWith('audit')) {
    window.auditRoute = location.hash.slice(1);
    const s = document.createElement('script');
    s.src = 'audit.js';
    document.body.appendChild(s);
  } else if (name) {
    // A step by name, or by number (1 = the first) as before.
    const list = steps();
    const step = /^\d+$/.test(name) ? list[Math.min(list.length - 1, Number(name))] : name;
    state.starting = q.get('starting') === '1';
    // done?failed=12: that many steps that did not work (the list scrolls).
    if (step === 'done') state.result = { ok: true, restartNeeded: q.get('restart') === '1' ? ['ComputerName'] : [],
      results: Object.fromEntries(Object.keys(STEP_NAMES).slice(0, Number(q.get('failed') || 0)).map((k) => [k, 'FAILED: it stopped with an error (details in the setup log)'])) };
    goStep(step);
    // setup.html#wifi?wired=0&wifi=password: the Wi-Fi component's sample states (wifi.js demo).
    if (step === 'wifi' && typeof WifiUI !== 'undefined') { WifiUI.demo(q.get('wifi') || 'wifi'); render(); }
    if (q.get('read')) onHost({ type: 'tv.read', power: q.get('read') === 'off' ? 'off' : 'on', input: Number(q.get('read')) || 0 });
    if (q.get('dialog') === '1') { state.dialog = true; render(); }
  }
}
fit();
render();
send({ type: 'ready' });
