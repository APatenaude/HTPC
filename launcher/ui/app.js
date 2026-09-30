'use strict';

// Launcher UI. The C# host (WebView2) sends state and controller input as web messages; the
// UI sends back what the user chose. Without a host (opened in a normal browser) it runs
// on demo data so the look can be checked anywhere; the keyboard stands in for the controller.
// This file is the core (state, send, render, the extension API, go/back); each area is in
// app/<area>.js (home, menu, dialogs, settings, focus, input, host), loaded after it in
// index.html's order: plain scripts sharing their globals, the start (fit, render) in host.js.

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

// The views under the one shown, nearest first: an overlay (a confirmation, a dialog) sits over
// the view it was opened from, which stays visible under it; over another overlay (a question
// over the TV method dialog over Settings), the whole chain down to the first full view does.
function underViews() {
  const isOver = (v) => v === 'confirm' || !!(EXT.views[v] && EXT.views[v].overlay);
  const list = [];
  for (let v = state.view, i = state.stack.length - 1; isOver(v) && i >= 0; i--) { v = state.stack[i]; list.push(v); }
  return list;
}

let renderedView = null, pageBackdrop = '';
function render() {
  const keep = state.memory[state.view];
  // Where the focus is: if its element goes (a network, a device, a TV no longer found), the one
  // now nearest its place takes it (restoreFocus), not the view's first element.
  const was = renderedView === state.view ? focusedEl() : null;
  const wasAt = was && { id: was.dataset.id, rect: was.getBoundingClientRect(), section: !!was.dataset.section, el: was };
  const unders = underViews();
  if (state.view !== 'settings') editing = null;
  renderStatus();
  renderTiles();
  for (const f of EXT.home) f();
  if (state.view === 'menu' || unders.includes('menu')) renderMenu();
  if (state.view === 'power') renderPower();
  if (state.view === 'timer') renderTimer();
  if (state.view === 'confirm') renderConfirm();
  if (state.view === 'settings' || unders.includes('settings')) renderSettings();
  for (const v of [state.view, ...unders]) if (EXT.views[v]) EXT.views[v].render();
  sectionHooks(unders);
  const home = $('home'), wasBehind = home.classList.contains('behind');
  const chain = [...unders].reverse().concat(state.view);   // bottom to top
  for (const v of ['home', 'menu', 'power', 'timer', 'confirm', 'settings', ...Object.keys(EXT.views)]) {
    const at = chain.indexOf(v);
    $(v).classList.toggle('on', at >= 0);
    $(v).classList.toggle('under', at >= 0 && v !== state.view);   // its hints hide: one hint bar, the top one's
    // Stacked in the order they were opened, whatever their order in the page.
    const z = at >= 0 && chain.length > 1 ? String(at + 1) : '';
    if ($(v).style.zIndex !== z) $(v).style.zIndex = z;
  }
  // Over an app the captured screen shows behind; over the home screen, home shows dimmed.
  const overApp = state.view !== 'home' && state.backdrop;
  $('backdrop').classList.toggle('on', !!overApp);
  $('backdrop').style.backgroundImage = overApp ? `url("${state.backdrop}")` : '';
  // Beyond the stage (a screen that is not 16:9: 16:10, ultrawide) the app's frame fills the rest
  // of the screen too, dimmed the same, lined up with the stage's (both cover the same frame).
  const page = overApp ? `linear-gradient(rgba(13, 14, 17, 0.45), rgba(13, 14, 17, 0.45)), center / cover no-repeat url("${state.backdrop}")` : '';
  if (pageBackdrop !== page) {
    pageBackdrop = page;
    document.documentElement.style.background = page;
    document.documentElement.classList.toggle('over-app', !!page);
  }
  home.classList.toggle('behind', state.view !== 'home' && !overApp);
  // Home back from under an overlay (a confirmation, Tile options, the menu) was on screen all
  // along: it must not play its entrance again, a flash of the whole screen.
  if (!home.classList.contains('on')) home.classList.remove('stay');
  else if (wasBehind && !home.classList.contains('behind')) home.classList.add('stay');
  for (const v of chain) if (EXT.views[v] && EXT.views[v].layout) EXT.views[v].layout();
  renderedView = state.view;
  restoreFocus(keep, wasAt);
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
//   ask({ title, text, notes, yes, onYes })   the shared yes / cancel dialog, over any view; notes
//                                             (html, a release's notes): more to read under the
//                                             text, in a box Up and Down scroll when it is long
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

// shown / left for the Settings section in view (none while Settings is not, nor while the stage
// is blank: the launcher gone behind an app, or standby, where Settings › TV left open searched
// for TVs every 10 s all night). The Home menu's resource view starts and stops the same way
// (resources.js: the host samples only while the menu is on screen).
let sectionInView = null;
function sectionHooks(unders = []) {
  if (typeof resourcesInView === 'function') resourcesInView(unders);
  // A dialog or a question over Settings (the TV method dialog, "Forget this TV?") leaves the
  // section in view: its list keeps refreshing, and nothing stops and starts again under it.
  // A blank stage (the launcher behind an app, or in standby) has no section in view.
  const onScreen = !$('stage').classList.contains('blank');
  const now = onScreen && (state.view === 'settings' || unders.includes('settings')) ? state.section : null;
  if (now === sectionInView) return;
  const was = EXT.sections[sectionInView];
  sectionInView = now;
  if (was && was.left) was.left();
  if (EXT.sections[now] && EXT.sections[now].shown) EXT.sections[now].shown();
}

// ---- Actions ------------------------------------------------------------------------------

// go: open a view on top of the current one (B comes back). reset: start over at a view.
function go(view) { state.stack.push(state.view); state.view = view; render(); }
function reset(view) { state.stack = []; state.view = view; render(); }

// Back through the views opened with go(); past the first one, return to the app the menu
// was opened over, or to the home screen.
function back() {
  if (state.view === 'home') return;
  const prev = state.stack.pop();
  if (prev) { state.view = prev; render(); return; }
  if (state.current) send({ type: 'resume', id: state.current });
  else reset('home');
}

// Asking the host for something a screen waits on ("Looking…"): never an endless wait. After ms
// without an answer the screen is drawn again and says so (hostWaitText's late text).
const hostWaits = {};
function hostAsked(key, ms = 10000) {
  hostWaits[key] = Date.now();
  setTimeout(() => { if (hostWaits[key] && Date.now() - hostWaits[key] >= ms) render(); }, ms + 50);
}
function hostAnswered(key) { delete hostWaits[key]; }
function hostWaitText(key, text, late, ms = 10000) { return hostWaits[key] && Date.now() - hostWaits[key] >= ms ? late : text; }

// A short message from the page, drawn with the alerts (notices.js).
function toast(text, kind) { if (text) noticeOwn(text, kind); }
