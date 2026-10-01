'use strict';
// UI audit pages: the launcher's home screen, Home menu, dialogs and Add a tile (index.html).
// Registered with auditPage() (audit.js), walked in this order, then those of audit/settings.js.

function auditTiles(n) {
  return Array.from({ length: n }, (_, i) => {
    const [glyph, color] = AUDIT_GLYPHS[i % AUDIT_GLYPHS.length];
    return { id: `app${i}`, name: i % 5 === 3 ? `App ${i + 1} ${AUDIT_LONG}` : `App ${i + 1}`, glyph, color, running: i % 4 === 1 };
  });
}

// A clean start for each page: the home screen, no app over it, no alerts, nothing moving.
function auditFresh() {
  state.moving = null; state.current = null; state.backdrop = null; state.confirm = null; state.timer = null;
  state.phone = null; state.stack = []; state.memory = {}; state.desktop = false;
  menuUsed.control = null; menuUsed.quick = null;
  if (typeof more === 'object' && more.testing) { more.testing = false; more.pad = null; }
  bt.scanning = false; bt.pin = null; bt.pairing = null;
  if (WifiUI.joining) WifiUI.stop();
  TvUi.code = '';
  hideOpening();
  notices.own = [];
  noticeUpdate({ toasts: [], rows: [], pills: [] });
  state.tiles = auditTiles(40);
  reset('home');
}

// A 1x1 PNG: a program's or an app's own icon (the host's logos.htpc), which does load.
const AUDIT_LOGO = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=';

// As big as the real library or bigger: 24 apps and 26 sites in six categories and Other, and a
// box's 110 programs (On this box), long names in each.
function auditLibrary() {
  const apps = Array.from({ length: 24 }, (_, i) => {
    const [glyph, color] = AUDIT_GLYPHS[i % AUDIT_GLYPHS.length];
    return { id: `lib${i}`, name: i === 7 ? `Library app ${AUDIT_LONG}` : `Library app ${i + 1}`, glyph, color, type: 'app', category: auditCategory(i),
      state: ['home', 'uninstalling', 'install', 'install'][i % 4], canUninstall: true, logo: i % 3 === 0 ? AUDIT_LOGO : null };
  });
  apps[5].state = 'installing';
  Object.assign(apps[12], { state: 'home', canUninstall: false, builtin: true });   // the Browser: Built in, A asks to remove it from Home
  const sites = Array.from({ length: 26 }, (_, i) => ({ id: `site${i}`, name: i === 4 ? `Streaming site ${AUDIT_LONG}` : `Site ${i + 1}`, color: '#FF4B55',
    type: 'website', category: auditCategory(i + 3), state: i % 3 === 1 ? 'home' : 'add' }));
  // lib9 went through the queue and is still to install: it did not install.
  onHost({ type: 'library.progress', current: { id: 'lib9', name: 'Library app 10', action: 'install', phase: 'download', percent: 30 }, pending: [] });
  onHost(AUDIT_PROGRESS);
  onHost({ type: 'library.catalog', apps, sites, categories: AUDIT_CATEGORIES, available: true });
  onHost({ type: 'library.programs', list: auditPrograms() });
}
// Every other one with its own icon (the host makes them in the background: the rest come later).
const auditPrograms = () => Array.from({ length: 110 }, (_, i) => ({ name: i === 3 ? `Program ${AUDIT_LONG}` : i === 40 ? `Uninstall ${AUDIT_LONG}` : `Program ${i + 1}`,
  launchable: i % 6 !== 4, onHome: i % 7 === 2, note: i % 6 === 4 ? 'No program file' : null, logo: i % 2 ? AUDIT_LOGO : null }));
// The install queue, pushed twice a second while it runs.
const AUDIT_PROGRESS = { type: 'library.progress', current: { id: 'lib5', name: 'Library app 6', action: 'install', phase: 'download', percent: 62 }, pending: [{ id: 'lib2', action: 'install' }] };

function auditMaps() {
  maps.data = null;
  mapsDemo();
  for (let i = 0; i < 14; i++) maps.data.apps.push({ id: `mapapp${i}`, name: i === 2 ? `Map app ${AUDIT_LONG}` : `Map app ${i + 1}`, glyph: 'app', color: '#B3B5BC',
    map: { preset: ['mouse', 'keyboard', 'controller'][i % 3], defaultPreset: 'mouse', changes: i % 2 ? { a: 'key:F', b: 'key:Esc' } : {} } });
}
const auditMapsTick = () => EXT.host['maps.data'](auditClone(maps.data));

// The Home menu's resource view at its widest: every number as long as it gets, names that run
// out of their row, a row of Windows' own. The host sends it every 2 s, in another order each time.
function auditRes() {
  AUDIT.resFlip = !AUDIT.resFlip;
  const top = [
    { key: 'app:app1', name: `App 2 ${AUDIT_LONG}`, app: 'app1', cpu: 100, mem: 31999, stop: true },
    { key: 'exe:program.exe', name: `Program ${AUDIT_LONG}.exe`, app: null, cpu: 88.8, mem: 12406, stop: true },
    { key: 'win:windows-update', name: `Windows Update ${AUDIT_LONG}`, app: null, cpu: 9.9, mem: 999, stop: false },
  ];
  onHost({ type: 'res.data', cpu: 100, memUsed: 65100, memTotal: 65400, disk: 9.99e9, down: 9.99e9, up: 999.4e6, top: AUDIT.resFlip ? top : top.reverse(), held: [] });
}

if (AUDIT_PAGE === 'index') {
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
  // Apps being installed (library.js): their tiles show the progress, pushed twice a second.
  auditPage('home with apps installing', { view: 'home', back: 0, tick: () => onHost(AUDIT_PROGRESS), open() {
    auditLibrary();
    EXT.actions.libcard(null, 'lib2'); reset('home');
    onHost(AUDIT_PROGRESS);
  } });
  auditPage('tile options', { view: 'tileopts', open() {
    setFocus($('tiles').querySelector('[data-id="tile:app23"]'));
    press('start');
  } });
  auditPage('rename', { view: 'rename', open() { setFocus($('tiles').querySelector('[data-id="tile:app3"]')); press('start'); EXT.actions['opt-rename'](); } });
  auditPage('change icon', { view: 'changeicon', open() { setFocus($('tiles').querySelector('[data-id="tile:app3"]')); press('start'); EXT.actions['opt-icon'](); } });
  // B over an app goes back to it: the host brings the app forward (resume), the page stays up.
  // With its resource view (resources.js), its numbers pushed again as it is walked.
  auditPage('home menu over an app', { view: 'menu', covers: ['menu'], tick: () => { auditMapsTick(); auditRes(); }, left: () => AUDIT.sent.some((m) => m.type === 'resume'), open() {
    for (const t of state.tiles.slice(0, 7)) t.running = true;
    auditMaps();
    onHost({ type: 'show', view: 'menu', current: 'app1' });
    noticeUpdate({ toasts: [], pills: [], rows: [
      { id: 'app:app2', title: `App 3 closed unexpectedly, ${AUDIT_LONG}`, body: 'It stopped working and closed.', glyph: 'warn', tone: 'bad', action: 'Reopen' },
      { id: 'tv', title: 'Can’t reach the TV', body: 'Is it on the network? Settings › TV can find it again.', glyph: 'tv', tone: 'bad', action: 'TV settings' }] });
    auditRes();
  } });
  auditPage('home menu over the home screen', { view: 'menu', tick: auditRes, open() { press('home'); auditRes(); } });
  // Nothing open: no Home screen row, no Open apps; the sliders first.
  auditPage('home menu over the home screen, nothing open', { view: 'menu', covers: [], tick: auditRes, open() {
    for (const t of state.tiles) t.running = false;
    press('home');
    auditRes();
  } });
  // Over the Windows desktop (desktop mode): Back to TV first; B goes back to the desktop.
  auditPage('home menu in desktop mode', { view: 'menu', tick: auditRes, left: () => AUDIT.sent.some((m) => m.type === 'resume'), open() {
    onHost({ type: 'state', desktop: true });
    onHost({ type: 'show', view: 'menu', current: 'desktop' });
    auditRes();
  } });
  auditPage('confirm (close an app)', { view: 'confirm', open() { setFocus($('tiles').querySelector('[data-id="tile:app1"]')); press('x'); } });
  // A sleep timer running: its pill in the top bar and its row in the Home menu (A +15 min, X cancel).
  auditPage('home with a sleep timer', { view: 'home', covers: [], back: 0, open() { auditFresh(); onHost({ type: 'state', timer: { label: '30 min', endsAt: Date.now() + 29 * 60000 } }); go('home'); } });
  auditPage('home menu with a sleep timer', { view: 'menu', covers: [], tick: auditRes, open() {
    auditFresh();
    onHost({ type: 'state', timer: { label: '30 min', endsAt: Date.now() + 29 * 60000 } });
    press('home');
    auditRes();
  } });
  auditPage('power', { view: 'power', open() { go('power'); } });
  auditPage('power in desktop mode', { view: 'power', open() { onHost({ type: 'state', desktop: true }); go('power'); } });
  auditPage('sleep timer', { view: 'timer', open() { go('timer'); } });
  auditPage('ask (a dialog)', { view: 'ask', open() { go('settings'); ask({ title: `Forget ${AUDIT_LONG}?`, text: `A question ${AUDIT_LONG}.`, yes: 'Forget' }); } });
  auditPage('add tile: library', { view: 'addtile', covers: ['addtile'], tick: () => onHost(AUDIT_PROGRESS), open() { auditLibrary(); EXT.actions.addtile(); auditLibrary(); render(); } });
  // The programs' icons arrive as the host makes them: the list is sent again, more with theirs.
  auditPage('add tile: on this box', { view: 'addtile', tick: () => onHost({ type: 'library.programs', list: auditPrograms().map((p, i) => ({ ...p, logo: i % 3 ? AUDIT_LOGO : null })) }),
    open() { auditLibrary(); EXT.actions.addtile(); press('rb'); auditLibrary(); render(); } });
  auditPage('add tile: website', { view: 'addtile', open() { auditLibrary(); EXT.actions.addtile(); press('rb'); press('rb'); } });
  // A long address and name typed in (the fields scroll their text; the preview's name wraps inside it).
  auditPage('add tile: website, a long address typed', { view: 'addtile', covers: [], open() {
    auditLibrary(); EXT.actions.addtile(); press('rb'); press('rb');
    const url = $('addtile').querySelector('[data-field="url"]'), name = $('addtile').querySelector('[data-field="name"]');
    url.focus(); textInsert(`https://www.${'a-very-long-address-'.repeat(8)}example.com/watch?list=${'x'.repeat(200)}`);
    name.focus(); textInsert(`Site ${AUDIT_LONG}`);
    name.blur();
  } });
}

// Move mode: every press moves the tile; it must stay in view (and focused) wherever it goes.
// A slow press is slow only if it is slow again, as in auditWalk: the tile back where it was, the
// same press twice more, the fastest counts.
function auditMoveWalk(page, report) {
  const seq = [...Array(7).fill('down'), ...Array(3).fill('right'), ...Array(7).fill('up'), ...Array(3).fill('left')];
  const back = { down: 'up', up: 'down', left: 'right', right: 'left' };
  let slowest = 0;
  for (const b of seq) {
    let ms = auditPress(b);
    for (let again = 0; again < 2 && ms > AUDIT.slowMs && state.moving; again++) {
      AUDIT_IO.press(back[b]);
      ms = Math.min(ms, auditPress(b));
    }
    slowest = Math.max(slowest, ms);
    const el = $('tiles').querySelector('.tile.moving');
    if (!el || !el.classList.contains('focused')) { report(`${b}: the moving tile lost the focus`); continue; }
    for (const p of auditProblems(el)) report(`${b} to ${el.dataset.id}: ${p}`);
    if (ms > AUDIT.slowMs) report(`${b}: took ${ms.toFixed(0)} ms`);
  }
  return slowest;
}
