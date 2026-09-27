'use strict';
// The UI audit, a "focus walker": every page of the launcher is set up in a stress state (24
// tiles, 20 library apps, 15 Wi-Fi networks, 10 Bluetooth devices, 8 phones and 4 keys, long
// names...) and walked with the D-pad through press(), the path the controller takes, from the
// first focus to every element it can reach. At each focus it checks that the element, its focus
// ring (4 px) and its zoom:
//   - are whole inside every box that clips them (a list that scrolls, a pane), and on screen;
//   - are not under a hint bar, nor covered by anything (elementFromPoint at the corners and the
//     centre);
// and for the page: every focusable element is reached; no press wraps round or jumps back (up at
// the first row, down at the last, stays); B leaves it; exactly one hint bar shows; every press
// (press() plus style and layout) takes 50 ms at most.
//
// Two runs, both from launcher\dev\Test-Ui.ps1 -SelfTest: in the self-test (index.html#selftest,
// headless Edge's virtual time: every check but the time, which does not move there) and on its
// own (index.html#audit, real time: the times too; it runs before the page's load event ends,
// so headless Edge's --dump-dom waits for it). index.html#audit?page=home sets one page up in its
// stress state with the focus on its last element, for a screenshot.
//
// EVERY VIEW MUST BE IN THE WALKER: a new view (addView) or Settings section is checked by adding
// an auditPage() for it below (what to set up, how to open it). One missing fails the audit.

const AUDIT = { pages: [], slowMs: 50, ring: 4, sent: [] };   // sent: what the page sent the host

// name: what the report says. view: the view it opens (state.view). open(): stress data, then
// the page (the focus where a user lands). scope: a selector for the part walked (Settings: the
// pane; the section list is a page of its own). dirs: the directions walked. back: B presses that
// leave it (0: the home screen, nothing to leave). hints: hint bars expected. covers: the views
// or sections it checks. walk: a walk of its own (move mode). last: the element to end on for a
// screenshot.
function auditPage(name, spec) { AUDIT.pages.push({ dirs: ['up', 'down', 'left', 'right'], back: 1, hints: 1, covers: [spec.view], ...spec, name }); }

// ---- Stress data ------------------------------------------------------------------------------

const AUDIT_LONG = 'with a name long enough to run out of room on a TV screen';
const AUDIT_GLYPHS = [['youtube', '#FF5B52'], ['chat', '#B08CFF'], ['film', '#7C8CFF'], ['library', '#3DC0F0'], ['moon', '#F5D16B'], ['globe', '#3CCB9A'], ['music', '#1ED760'], ['tv', '#5AB0FF']];

function auditTiles(n) {
  return Array.from({ length: n }, (_, i) => {
    const [glyph, color] = AUDIT_GLYPHS[i % AUDIT_GLYPHS.length];
    return { id: `app${i}`, name: i % 5 === 3 ? `App ${i + 1} ${AUDIT_LONG}` : `App ${i + 1}`, glyph, color, running: i % 4 === 1 };
  });
}

// A clean start for each page: the home screen, no app over it, no alerts, nothing moving.
function auditFresh() {
  state.moving = null; state.current = null; state.backdrop = null; state.confirm = null; state.timer = null;
  state.phone = null; state.stack = []; state.memory = {};
  notices.own = [];
  noticeUpdate({ toasts: [], rows: [], pills: [] });
  state.tiles = auditTiles(24);
  reset('home');
}

function auditLibrary() {
  const apps = Array.from({ length: 20 }, (_, i) => {
    const [glyph, color] = AUDIT_GLYPHS[i % AUDIT_GLYPHS.length];
    return { id: `lib${i}`, name: i === 7 ? `Library app ${AUDIT_LONG}` : `Library app ${i + 1}`, glyph, color, type: 'app',
      desc: i % 3 ? 'Plays what is on your network' : `A description ${AUDIT_LONG}, and then some more words`,
      state: ['home', 'installed', 'install', 'install'][i % 4], canUninstall: true };
  });
  apps[5].state = 'installing';
  const sites = Array.from({ length: 12 }, (_, i) => ({ id: `site${i}`, name: i === 4 ? `Streaming site ${AUDIT_LONG}` : `Site ${i + 1}`, color: '#FF4B55', type: 'website', state: 'add' }));
  // lib9 went through the queue and is still to install: it did not install.
  onHost({ type: 'library.progress', current: { id: 'lib9', name: 'Library app 10', action: 'install', phase: 'download', percent: 30 }, pending: [] });
  onHost({ type: 'library.progress', current: { id: 'lib5', name: 'Library app 6', action: 'install', phase: 'download', percent: 62 }, pending: [] });
  onHost({ type: 'library.catalog', apps, sites, available: true });
  onHost({ type: 'library.programs', list: Array.from({ length: 26 }, (_, i) => ({ name: i === 3 ? `Program ${AUDIT_LONG}` : `Program ${i + 1}`,
    launchable: i % 6 !== 4, onHome: i % 7 === 2, note: i % 6 === 4 ? 'Windows app' : null })) });
}

function auditMaps() {
  maps.data = null;
  mapsDemo();
  for (let i = 0; i < 14; i++) maps.data.apps.push({ id: `mapapp${i}`, name: i === 2 ? `Map app ${AUDIT_LONG}` : `Map app ${i + 1}`, glyph: 'app', color: '#B3B5BC',
    map: { preset: ['mouse', 'keyboard', 'controller'][i % 3], defaultPreset: 'mouse', changes: i % 2 ? { a: 'key:F', b: 'key:Esc' } : {} } });
}

function auditSettings(section) {
  state.section = section;
  reset('settings');
  const nav = $('settings').querySelector(`[data-section="${section}"]`);
  const first = $('settings').querySelector('.spane [data-nav]');
  setFocus(first || nav);
}

const AUDIT_WIFI = { type: 'wifi.state', adapter: true, radio: 'on', location: 'ok', wifiInternet: true, askRadioOff: false,
  wired: { name: 'Ethernet', mbps: 1000, up: true, internet: true },
  current: { ssid: '[Network in use]', signal: 85, words: 'strong signal' },
  networks: [{ ssid: '[Network in use]', signal: 85, words: 'strong signal', security: 'wpa2psk', password: true, saved: true, connected: true },
    ...Array.from({ length: 15 }, (_, i) => ({ ssid: i === 6 ? `[Network ${AUDIT_LONG}]` : `[Network ${i + 1}]`, signal: 80 - i * 4,
      words: i < 5 ? 'good signal' : 'weak signal', security: i % 5 === 3 ? 'open' : 'wpa2psk', password: i % 5 !== 3, saved: i % 4 === 0, connected: false }))] };

const AUDIT_BT = { type: 'bt.state', adapter: true, radio: 'on', scanning: false, nearby: [],
  paired: Array.from({ length: 10 }, (_, i) => ({ id: `bt${i}`, name: i === 3 ? `[Headphones ${AUDIT_LONG}]` : `[Device ${i + 1}]`,
    kind: ['headphones', 'controller', 'keyboard', 'speaker', 'mouse'][i % 5], connected: i < 2, soundHere: i === 0 })) };

function auditPhone() {
  EXT.host['phone.settings']({ type: 'phone.settings', phone: {
    listening: true, address: 'tv.local', ip: '192.168.1.20', requireCode: true, reach: 'ok', unpaired: 2, secure: true,
    fingerprint: '3A:9F:12:C4:7E:05:B8:61:D2:4A:90:3C:E7:18:6B:F5:21:8D:C9:47:0E:B3:5A:96:F1:2C:84:7D:63:E0:1B:A8',
    qr: 'http://192.168.1.20/?k=Qm9vc3RlZC1kZW1vLWtleQ', sendQr: 'http://192.168.1.20/send?k=U2VuZC1kZW1vLWtleS1vbmx5',
    phones: [...Array.from({ length: 8 }, (_, i) => ({ id: `ph${i}`, name: i === 2 ? `Phone ${AUDIT_LONG}` : `Phone ${i + 1}`, connected: i === 0, lastSeen: Date.now() - i * 86400000 })),
      ...Array.from({ length: 4 }, (_, i) => ({ id: `key${i}`, name: `Shortcut key ${i + 1}`, connected: false, lastSeen: Date.now() - i * 3600000, shortcut: true }))] } });
}

function auditTv() {
  state.tv = TvUi.demo('roku');
  const t = state.tv;
  t.profiles = t.profiles.concat(Array.from({ length: 5 }, (_, i) => ({ key: `tvkey${i}`, name: i === 1 ? `TV ${AUDIT_LONG}` : `Other TV ${i + 1}`,
    method: 'roku', methodLabel: 'Roku TV', input: 2, current: false, lastUsed: '2026-09-20' })));
  t.found = t.found.concat(Array.from({ length: 6 }, (_, i) => ({ ...t.found[0], id: `roku:X${i}`, name: `Roku TV ${i + 2}`, picked: false, detected: false })));
}

// ---- The pages ------------------------------------------------------------------------------

auditPage('home', { view: 'home', back: 0, open() {} });
auditPage('home with the phone card', { view: 'home', back: 0, open() {
  state.phone = { url: 'https://tv.local/', paired: false, pairingOpen: false };
  try { localStorage.removeItem('phoneCardHidden'); } catch (e) { /* no storage */ }
  render();
} });
// Alert cards at the top right: they move off the focus (Settings and Power, the top right tiles).
auditPage('home with alert cards', { view: 'home', back: 0, open() {
  noticeUpdate({ rows: [], pills: [{ id: 'updates', text: '4 updates', glyph: 'download', tone: 'warn' }], toasts: [
    { id: 'a', title: `A card ${AUDIT_LONG}`, body: `And its second line, ${AUDIT_LONG}`, glyph: 'warn', tone: 'bad', key: 'Home', action: 'Reopen' },
    { id: 'b', title: 'No internet', body: 'Check the network cable or the Wi-Fi.', glyph: 'wifi', tone: 'warn', key: 'Home', action: 'Wi-Fi settings' },
    { id: 'c', title: 'Phone remote connected', glyph: 'phone', tone: 'info' }] });
} });
auditPage('home, moving a tile', { view: 'home', back: 1, open() {
  setFocus($('tiles').querySelector('[data-id="tile:app0"]'));
  press('start'); press('a');                     // Tile options > Move
}, walk: auditMoveWalk, left: () => !state.moving });
auditPage('tile options', { view: 'tileopts', open() {
  setFocus($('tiles').querySelector('[data-id="tile:app23"]'));
  press('start');
} });
auditPage('rename', { view: 'rename', open() { setFocus($('tiles').querySelector('[data-id="tile:app3"]')); press('start'); EXT.actions['opt-rename'](); } });
auditPage('change icon', { view: 'changeicon', open() { setFocus($('tiles').querySelector('[data-id="tile:app3"]')); press('start'); EXT.actions['opt-icon'](); } });
// B over an app goes back to it: the host brings the app forward (resume), the page stays up.
auditPage('home menu over an app', { view: 'menu', covers: ['menu'], left: () => AUDIT.sent.some((m) => m.type === 'resume'), open() {
  for (const t of state.tiles.slice(0, 7)) t.running = true;
  auditMaps();
  onHost({ type: 'show', view: 'menu', current: 'app1' });
  noticeUpdate({ toasts: [], pills: [], rows: [
    { id: 'app:app2', title: `App 3 closed unexpectedly, ${AUDIT_LONG}`, body: 'It stopped working and closed.', glyph: 'warn', tone: 'bad', action: 'Reopen' },
    { id: 'tv', title: 'Can’t reach the TV', body: 'Is it on the network? Settings › TV can find it again.', glyph: 'tv', tone: 'bad', action: 'TV settings' }] });
} });
auditPage('confirm (close an app)', { view: 'confirm', open() { setFocus($('tiles').querySelector('[data-id="tile:app1"]')); press('x'); } });
auditPage('power', { view: 'power', open() { go('power'); } });
auditPage('sleep timer', { view: 'timer', open() { go('timer'); } });
auditPage('ask (a dialog)', { view: 'ask', open() { go('settings'); ask({ title: `Forget ${AUDIT_LONG}?`, text: `A question ${AUDIT_LONG}.`, yes: 'Forget' }); } });
auditPage('add tile: library', { view: 'addtile', covers: ['addtile'], open() { auditLibrary(); EXT.actions.addtile(); auditLibrary(); render(); } });
auditPage('add tile: on this box', { view: 'addtile', open() { auditLibrary(); EXT.actions.addtile(); press('rb'); auditLibrary(); render(); } });
auditPage('add tile: website', { view: 'addtile', open() { auditLibrary(); EXT.actions.addtile(); press('rb'); press('rb'); } });
auditPage('installing (dialog)', { view: 'installing', open() {
  auditLibrary(); EXT.actions.addtile(); auditLibrary(); render();
  EXT.actions.libcard(null, 'lib2');
} });
auditPage('settings: section list', { view: 'settings', covers: ['settings'], scope: '.snav', dirs: ['up', 'down'], open() { state.section = 'sleep'; reset('settings'); } });
for (const [id, , label] of SECTIONS) {
  const data = { tv: auditTv, wifi: () => WifiUI.handle(AUDIT_WIFI), bluetooth: () => onHost(AUDIT_BT), phone: auditPhone,
    sound: () => onHost({ type: 'sound.outputs', canSwitch: true, outputs: Array.from({ length: 6 }, (_, i) => ({ id: `o${i}`, name: i === 1 ? `Output ${AUDIT_LONG}` : `Output ${i + 1}`, isDefault: i === 0 })) }),
    updates: () => { EXT.sections.updates.demo(); for (let i = 0; i < 12; i++) upd.s.apps.push({ id: `u${i}`, name: `Updated app ${i + 1}`, glyph: 'app', color: '#B3B5BC', installed: '1.0', available: '1.1', update: i % 2 === 0, job: null }); },
    display: () => EXT.sections.display.demo(), about: () => EXT.sections.about.demo(), controller: () => EXT.sections.controller.demo() }[id];
  auditPage(`settings: ${label}`, { view: 'settings', covers: [`section:${id}`], scope: '.spane', back: 2, open() {
    auditSettings(id);
    if (data) { data(); render(); }
    const first = $('settings').querySelector('.spane [data-nav]');
    if (first) setFocus(first);
  } });
}
auditPage('tv: how the box controls it', { view: 'tvmethod', open() { auditTv(); auditSettings('tv'); go('tvmethod'); } });
auditPage('button maps', { view: 'maps', open() { auditMaps(); go('settings'); go('maps'); } });
auditPage('button map editor', { view: 'buttons', covers: ['buttons'], back: 2, open() { auditMaps(); go('maps'); EXT.actions['map-edit'](null, 'twitch'); } });
auditPage('button map editor: a button\'s choice', { view: 'buttons', scope: '.baside', dirs: ['up', 'down'], back: 3, open() {
  auditMaps(); go('maps'); EXT.actions['map-edit'](null, 'twitch');
  setFocus($('buttons').querySelector('[data-id="b-start"]')); press('a');
} });
auditPage('button map editor: key combination', { view: 'buttons', scope: '.baside', back: 4, open() {
  auditMaps(); go('maps'); EXT.actions['map-edit'](null, 'twitch');
  setFocus($('buttons').querySelector('[data-id="b-select"]')); press('a');
  setFocus([...$('buttons').querySelectorAll('[data-value]')].find((e) => e.dataset.value === 'combo')); press('a');
} });
auditPage('button map editor: presets', { view: 'buttons', scope: '.baside', dirs: ['up', 'down'], back: 3, open() {
  auditMaps(); go('maps'); EXT.actions['map-edit'](null, 'twitch');
  setFocus($('buttons').querySelector('[data-id="b-preset"]')); press('a');
} });
auditPage('launcher restarting', { view: 'updrestart', back: 0, hints: 0, open() { upd.restarting = '0.2.0'; go('updrestart'); } });

// Move mode: every press moves the tile; it must stay in view (and focused) wherever it goes.
function auditMoveWalk(page, report) {
  const seq = [...Array(7).fill('down'), ...Array(3).fill('right'), ...Array(7).fill('up'), ...Array(3).fill('left')];
  let slowest = 0;
  for (const b of seq) {
    const ms = auditPress(b);
    slowest = Math.max(slowest, ms);
    const el = $('tiles').querySelector('.tile.moving');
    if (!el || !el.classList.contains('focused')) { report(`${b}: the moving tile lost the focus`); continue; }
    for (const p of auditProblems(el)) report(`${b} to ${el.dataset.id}: ${p}`);
    if (ms > AUDIT.slowMs) report(`${b}: took ${ms.toFixed(0)} ms`);
  }
  return slowest;
}

// ---- Checks ---------------------------------------------------------------------------------

function auditDescribe(el) {
  if (!el) return 'nothing';
  return el.dataset && el.dataset.id ? `[${el.dataset.id}]` : el.id ? `#${el.id}` : `${el.tagName.toLowerCase()}${el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/).join('.') : ''}`;
}

function auditScale() { return $('stage').getBoundingClientRect().width / 1920 || 1; }

// A press, timed: press() and the style and layout it causes (what the next frame needs).
function auditPress(button) {
  const t0 = performance.now();
  press(button);
  void document.body.offsetHeight;
  return performance.now() - t0;
}

// Hint bars on screen: shown, not see-through, not under an opaque view (an overlay's dimmed
// backdrop still shows what is under it).
function auditHintBars() {
  return [...document.querySelectorAll('.hints')].filter((h) => {
    const r = h.getBoundingClientRect();
    if (!r.width || !r.height || getComputedStyle(h).visibility !== 'visible' || !h.querySelector('.key')) return false;
    for (let p = h; p; p = p.parentElement) if (Number(getComputedStyle(p).opacity) < 0.05) return false;
    const k = h.querySelector('.key').getBoundingClientRect();
    const hit = document.elementFromPoint(k.left + k.width / 2, k.top + k.height / 2);
    return !!hit && (h.contains(hit) || (hit.classList.contains('overlay') && !hit.contains(h)));
  });
}

function auditProblems(el) {
  const out = [];
  const r = el.getBoundingClientRect();
  if (!r.width || !r.height) return ['has no size'];
  const g = AUDIT.ring * auditScale();
  const ring = { l: r.left - g, t: r.top - g, r: r.right + g, b: r.bottom + g };
  const inside = (a, b) => a.l >= b.l - 0.5 && a.t >= b.t - 0.5 && a.r <= b.r + 0.5 && a.b <= b.b + 0.5;
  const s = $('stage').getBoundingClientRect();
  if (!inside(ring, { l: s.left, t: s.top, r: s.right, b: s.bottom })) out.push('its ring is off the screen');
  for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
    const cs = getComputedStyle(p);
    if (cs.overflowX === 'visible' && cs.overflowY === 'visible') continue;
    const c = p.getBoundingClientRect(), k = c.width / (p.offsetWidth || 1) || 1;
    const box = { l: c.left + p.clientLeft * k, t: c.top + p.clientTop * k, r: c.left + (p.clientLeft + p.clientWidth) * k, b: c.top + (p.clientTop + p.clientHeight) * k };
    if (!inside(ring, box)) { out.push(`its ring is cut by ${auditDescribe(p)}`); break; }
  }
  for (const h of auditHintBars()) {
    const q = h.getBoundingClientRect();
    if (!h.contains(el) && ring.l < q.right && ring.r > q.left && ring.t < q.bottom && ring.b > q.top) out.push('it is under the hint bar');
  }
  // What it holds stays inside it (a long name that spills out, or is cut off, is not).
  for (const c of el.querySelectorAll('*')) {
    const q = c.getBoundingClientRect();
    if (q.width && q.height && (q.left < r.left - 1 || q.top < r.top - 1 || q.right > r.right + 1 || q.bottom > r.bottom + 1)) {
      out.push(`what it holds spills out of it (${auditDescribe(c)})`);
      break;
    }
  }
  const d = Math.min(20 * auditScale(), r.width / 4, r.height / 4);
  for (const [x, y] of [[r.left + d, r.top + d], [r.right - d, r.top + d], [r.left + d, r.bottom - d], [r.right - d, r.bottom - d], [(r.left + r.right) / 2, (r.top + r.bottom) / 2]]) {
    const hit = document.elementFromPoint(x, y);
    if (hit && hit !== el && !el.contains(hit)) { out.push(`it is covered by ${auditDescribe(hit)}`); break; }
  }
  return out;
}

// The page's focusable elements (in its scope), shown.
function auditItems(page) {
  const root = page.scope ? viewEl().querySelector(page.scope) : viewEl();
  return root ? [...root.querySelectorAll('[data-nav]')].filter((e) => e.getBoundingClientRect().width > 0) : [];
}

function auditFind(page, id) { return auditItems(page).find((e) => e.dataset.id === id) || null; }

// Walks one page: from its first focus, every direction from every element reached (the focus
// put back on it each time), checking each place the focus lands. Reports problems as it goes.
async function auditWalk(page, report) {
  const start = focusedEl();
  if (!start && !auditItems(page).length) return 0;   // nothing to focus (a screen to wait on)
  if (!start) { report('no focus when it opens'); return; }
  const inScope = (e) => !!e && auditItems(page).includes(e);
  if (!inScope(start)) { report(`it opens with the focus outside what is walked, on ${auditDescribe(start)}`); return; }
  for (const e of auditItems(page)) if (!e.dataset.id) report(`${auditDescribe(e)} can take the focus but has no data-id`);
  const seen = new Set([start.dataset.id]), queue = [start.dataset.id], checked = new Set();
  let slowest = 0;
  const look = (el) => {
    if (checked.has(el.dataset.id)) return;
    checked.add(el.dataset.id);
    for (const p of auditProblems(el)) report(`${auditDescribe(el)}: ${p}`);
  };
  look(start);
  while (queue.length) {
    const id = queue.shift();
    for (const dir of page.dirs) {
      const from = auditFind(page, id);
      if (!from) { report(`[${id}] went away as the focus moved round it`); break; }
      setFocus(from);
      await null;
      let ms = auditPress(dir);
      // Slow once (the collector, the box busy with something else) is not slow: the same press
      // twice more, the fastest counts.
      for (let again = 0; again < 2 && ms > AUDIT.slowMs; again++) {
        const f = auditFind(page, id);
        if (!f || state.view !== page.view) break;
        setFocus(f);
        ms = Math.min(ms, auditPress(dir));
      }
      slowest = Math.max(slowest, ms);
      if (ms > AUDIT.slowMs) report(`${dir} from [${id}]: took ${ms.toFixed(0)} ms`);
      await null;
      if (state.view !== page.view) { report(`${dir} from [${id}] left the page (to ${state.view})`); auditFresh(); page.open(); continue; }
      const to = focusedEl();
      if (!to || to.dataset.id === id) continue;
      if (!inScope(to)) continue;   // out of what is walked (Settings: to the section list)
      // Where both are now (a list that scrolled to the new focus moved them both).
      const was = auditFind(page, id);
      if (!was) continue;
      const a = was.getBoundingClientRect(), b = to.getBoundingClientRect();
      const [ax, ay, bx, by] = [a.left + a.width / 2, a.top + a.height / 2, b.left + b.width / 2, b.top + b.height / 2];
      const back = { down: by < ay - 1, up: by > ay + 1, right: bx < ax - 1, left: bx > ax + 1 }[dir];
      if (back) report(`${dir} from [${id}] wrapped round (or jumped back) to [${to.dataset.id}]`);
      look(to);
      if (!seen.has(to.dataset.id)) { seen.add(to.dataset.id); queue.push(to.dataset.id); }
    }
  }
  for (const e of auditItems(page)) if (e.dataset.id && !seen.has(e.dataset.id)) report(`${auditDescribe(e)} is never reached`);
  return slowest;
}

// Runs every page (or those named); check(name, ok, detail) records each result.
async function runAudit(check, only) {
  const style = document.createElement('style');   // the end state at once: no transitions, animations
  style.textContent = '*, *::before, *::after { transition: none !important; animation: none !important; }';
  document.head.appendChild(style);
  const log = console.log;
  console.log = (...args) => { if (args[0] === 'to host') AUDIT.sent.push(args[1]); else log(...args); };
  const covered = new Set(AUDIT.pages.flatMap((p) => p.covers));
  for (const v of ['home', 'menu', 'power', 'timer', 'confirm', 'settings', ...Object.keys(EXT.views), ...SECTIONS.map(([id]) => `section:${id}`)])
    check(`audit: ${v} is in the walker`, covered.has(v), 'no auditPage() covers it (audit.js)');
  const times = [];
  for (const page of AUDIT.pages) {
    if (only && !only.includes(page.name)) continue;
    const problems = [];
    const report = (text) => problems.push(text);
    try {
      auditFresh();
      AUDIT.sent = [];
      page.open();
      await null;
      if (state.view !== page.view) report(`it did not open (the view is ${state.view})`);
      else {
        const slowest = page.walk ? page.walk(page, report) : await auditWalk(page, report);
        times.push([page.name, slowest || 0]);
        const bars = auditHintBars().length;
        if (bars !== page.hints) report(`${bars} hint bars show, not ${page.hints}`);
        if (page.back) {
          const first = auditItems(page)[0];
          if (first && !page.walk) setFocus(first);
          for (let i = 0; i < page.back && state.view === page.view && !(page.left && page.left()); i++) press('b');
          if (page.left ? !page.left() : state.view === page.view) report(`B (${page.back} times) does not leave it`);
        }
      }
    } catch (e) { report(`threw ${e && e.stack ? e.stack.split('\n').slice(0, 3).join(' / ') : e}`); }
    check(`audit: ${page.name}`, !problems.length, [...new Set(problems)].join(' | '));
  }
  console.log = log;
  style.remove();
  auditFresh();
  return times;
}

// index.html#audit (real time) and #audit?page=<name> (a page to look at).
(async () => {
  const route = window.auditRoute || '';
  if (!route.startsWith('audit')) return;
  const one = /[?&]page=([^&]+)/.exec(route);
  if (one) {
    const page = AUDIT.pages.find((p) => p.name === decodeURIComponent(one[1]));
    if (!page) return;
    auditFresh();
    page.open();
    if (page.walk) { for (let i = 0; i < 7; i++) press('down'); return; }
    const all = auditItems(page);
    const last = page.last ? page.last() : all[all.length - 1];
    if (last) setFocus(last);
    return;
  }
  const results = [];
  const check = (name, ok, detail) => results.push({ name, ok: !!ok, detail: detail || '' });
  const times = await runAudit(check);
  const failed = results.filter((r) => !r.ok);
  const pre = document.createElement('pre');
  pre.id = 'audit-results';
  pre.textContent = results.map((r) => `${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.ok ? '' : '  [' + r.detail + ']'}`).join('\n') +
    '\n\nSlowest press per page (ms, real time):\n' + times.map(([n, ms]) => `  ${ms.toFixed(1).padStart(6)}  ${n}`).join('\n');
  document.body.appendChild(pre);
  document.title = failed.length ? `AUDIT FAIL ${failed.length}` : `AUDIT PASS ${results.length}`;
})();
