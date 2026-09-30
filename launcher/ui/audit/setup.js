'use strict';
// UI audit pages: first-run setup (setup.html). Registered with auditPage() (audit.js).

function auditSetupFresh() {
  if (typeof WifiUI !== 'undefined' && WifiUI.joining) WifiUI.stop();
  Object.assign(state, { tvHint: null, dialog: false, read: null, starting: false, progress: null, result: null, wired: true, controller: true, battery: 'full' });
  state.pressed = new Set(BUTTONS.map(([b]) => b));
  // As many as the real catalog gives setup (38) and more, in its categories and one it does not know.
  state.apps = Array.from({ length: 44 }, (_, i) => {
    const [glyph, color] = AUDIT_GLYPHS[i % AUDIT_GLYPHS.length];
    return { id: `sapp${i}`, name: i === 5 ? `App ${AUDIT_LONG}` : `App ${i + 1}`, glyph, color, default: i < 8, category: auditCategory(i) };
  });
  state.categories = AUDIT_CATEGORIES;
  state.picked = new Set(state.apps.filter((a) => a.default).map((a) => a.id));
  state.tv = TvUi.demo('roku');
  TvUi.code = '';
  goStep('welcome');
}

const auditSetupWifi = () => { const s = auditClone(AUDIT_WIFI); s.wired = null; return s; };

if (AUDIT_PAGE === 'setup') {
  auditPage('setup: welcome', { view: 'welcome', back: 0, open() {} });
  auditPage('setup: controller, every button pressed', { view: 'controller', tick: () => onHost({ type: 'state', controller: true, battery: 'full' }), open() { goStep('controller'); } });
  auditPage('setup: controller, none pressed yet', { view: 'controller', covers: [], back: 0, open() { state.pressed = new Set(); goStep('controller'); } });
  auditPage('setup: Wi-Fi', { view: 'wifi', tick: () => { const s = auditSetupWifi(); s.networks = [s.networks[0], ...s.networks.slice(1).reverse()]; WifiUI.handle(s); }, open() {
    state.wired = false; goStep('wifi'); WifiUI.handle(auditSetupWifi());
  } });
  // B leaves the form first (the step stays).
  auditPage('setup: Wi-Fi, a password to type', { view: 'wifi', covers: [], left: () => !WifiUI.joining, tick: () => WifiUI.handle(auditSetupWifi()), open() {
    state.wired = false; goStep('wifi'); WifiUI.demo('password'); render();
  } });
  auditPage('setup: Wi-Fi, a hidden network', { view: 'wifi', covers: [], left: () => !WifiUI.joining, tick: () => WifiUI.handle(auditSetupWifi()), open() {
    state.wired = false; goStep('wifi'); WifiUI.demo('hidden'); render();
  } });
  auditPage('setup: find the TV', { view: 'tv', tick: auditTvTick, open() { auditTv(); goStep('tv'); } });
  auditPage('setup: how the box controls the TV (dialog)', { view: 'tv:dialog', tick: auditTvTick, left: () => !state.dialog, open() {
    state.tv = TvUi.demo('crowd'); goStep('tv'); state.dialog = true; render();
  } });
  auditPage('setup: TV pairing keypad', { view: 'tv', covers: [], tick: auditTvTick, open() { state.tv = TvUi.demo('pair-code'); goStep('tv'); } });
  auditPage('setup: the TV input', { view: 'input', tick: auditTvTick, open() {
    goStep('input'); onHost({ type: 'tv.read', power: 'on', input: 3 });
  } });
  auditPage('setup: apps', { view: 'apps', open() { goStep('apps'); } });
  auditPage('setup: installing', { view: 'install', back: 0, tick: () => onHost(auditSetupProgress()), open() { goStep('install'); onHost(auditSetupProgress()); } });
  auditPage('setup: done', { view: 'done', back: 0, open() {
    state.result = { ok: true, results: {}, restartNeeded: [] }; goStep('done');
  } });
  auditPage('setup: done, many steps failed', { view: 'done', covers: [], back: 0, open() {
    state.result = { ok: false, restartNeeded: ['shell'], results: Object.fromEntries(Object.keys(STEP_NAMES).map((k, i) => [k, i % 5 === 0 ? 'OK' : `FAILED: it stopped, ${AUDIT_LONG}`])) };
    goStep('done');
  } });
}

function auditSetupProgress() {
  const names = Object.keys(STEP_NAMES);
  return { type: 'progress', steps: names, running: names[4], results: Object.fromEntries(names.slice(0, 4).map((k, i) => [k, i === 2 ? 'FAILED: no network' : 'OK'])) };
}
