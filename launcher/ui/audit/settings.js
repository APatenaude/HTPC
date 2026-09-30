'use strict';
// UI audit pages: the launcher's Settings, its sections' states, the TV method, button maps
// (index.html). Registered with auditPage() (audit.js), after audit/home.js's.

function auditSettings(section) {
  state.section = section;
  reset('settings');
  const nav = $('settings').querySelector(`[data-section="${section}"]`);
  const first = $('settings').querySelector('.spane [data-nav]');
  setFocus(first || nav);
}
function auditSettingsFirst() {
  const first = $('settings').querySelector('.spane [data-nav]');
  if (first) setFocus(first);
}

const AUDIT_BT = { type: 'bt.state', adapter: true, radio: 'on', scanning: false,
  nearby: Array.from({ length: 8 }, (_, i) => ({ id: `near${i}`, name: i === 2 ? `[Speaker ${AUDIT_LONG}]` : `[Nearby ${i + 1}]`, kind: ['speaker', 'keyboard', 'headphones', 'other'][i % 4] })),
  paired: Array.from({ length: 10 }, (_, i) => ({ id: `bt${i}`, name: i === 3 ? `[Headphones ${AUDIT_LONG}]` : `[Device ${i + 1}]`,
    kind: ['headphones', 'controller', 'keyboard', 'speaker', 'mouse'][i % 5], connected: i < 2, soundHere: i === 0 })) };
function auditBtTick() {
  const s = auditClone(AUDIT_BT);
  s.paired.reverse(); s.nearby.reverse();
  onHost(s);
}

function auditPhone() {
  EXT.host['phone.settings']({ type: 'phone.settings', phone: {
    listening: true, address: 'tv.local', ip: '192.168.1.20', requireCode: true, reach: 'ok', unpaired: 2, secure: true,
    fingerprint: '3A:9F:12:C4:7E:05:B8:61:D2:4A:90:3C:E7:18:6B:F5:21:8D:C9:47:0E:B3:5A:96:F1:2C:84:7D:63:E0:1B:A8',
    qr: 'http://192.168.1.20/?k=Qm9vc3RlZC1kZW1vLWtleQ', sendQr: 'http://192.168.1.20/send?k=U2VuZC1kZW1vLWtleS1vbmx5',
    phones: [...Array.from({ length: 8 }, (_, i) => ({ id: `ph${i}`, name: i === 2 ? `Phone ${AUDIT_LONG}` : `Phone ${i + 1}`, connected: i === 0, lastSeen: Date.now() - i * 86400000 })),
      ...Array.from({ length: 4 }, (_, i) => ({ id: `key${i}`, name: `Shortcut key ${i + 1}`, connected: false, lastSeen: Date.now() - i * 3600000, shortcut: true }))] } });
}

function auditUpdates(kind) {
  updDemo(kind);
  for (let i = 0; i < 12; i++) upd.s.apps.push({ id: `u${i}`, name: `Updated app ${i + 1}`, glyph: 'app', color: '#B3B5BC', installed: '1.0', available: '1.1', update: i % 2 === 0, job: null });
}
const auditUpdatesTick = () => onHost(auditClone(upd.s));

if (AUDIT_PAGE === 'index') {
  auditPage('settings: section list', { view: 'settings', covers: ['settings'], scope: '.snav', dirs: ['up', 'down'], open() { state.section = 'sleep'; reset('settings'); } });
  const sectionData = { tv: auditTv, wifi: () => WifiUI.handle(AUDIT_WIFI), bluetooth: () => onHost(AUDIT_BT), phone: auditPhone,
    sound: () => onHost({ type: 'sound.outputs', canSwitch: true, outputs: Array.from({ length: 6 }, (_, i) => ({ id: `o${i}`, name: i === 1 ? `Output ${AUDIT_LONG}` : `Output ${i + 1}`, isDefault: i === 0 })) }),
    updates: () => auditUpdates('ready'), display: () => EXT.sections.display.demo(), about: () => EXT.sections.about.demo(), controller: () => EXT.sections.controller.demo() };
  const sectionTick = { tv: auditTvTick, wifi: auditWifiTick, bluetooth: auditBtTick, phone: auditPhone, updates: auditUpdatesTick,
    sound: () => onHost({ type: 'sound.outputs', ...auditClone(more.audio) }) };
  for (const [id, , label] of SECTIONS) {
    auditPage(`settings: ${label}`, { view: 'settings', covers: [`section:${id}`], scope: '.spane', back: 2, tick: sectionTick[id], open() {
      auditSettings(id);
      if (sectionData[id]) { sectionData[id](); render(); }
      auditSettingsFirst();
    } });
  }
  // The states a section goes through, beyond its first look.
  const sectionState = (name, id, set, extra) => auditPage(`settings: ${name}`, { view: 'settings', covers: [], scope: '.spane', back: 2, tick: sectionTick[id], ...extra, open() {
    auditSettings(id);
    if (sectionData[id]) sectionData[id]();
    set();
    render();
    auditSettingsFirst();
  } });
  // In a Wi-Fi form, B first leaves the form (then the pane, then Settings).
  sectionState('Wi-Fi, a password to type', 'wifi', () => WifiUI.demo('password'), { back: 3, tick: () => WifiUI.handle(auditClone(AUDIT_WIFI)) });
  sectionState('Wi-Fi, a hidden network', 'wifi', () => WifiUI.demo('hidden'), { back: 3, tick: () => WifiUI.handle(auditClone(AUDIT_WIFI)) });
  sectionState('Bluetooth, looking for devices', 'bluetooth', () => { bt.scanning = true; }, { back: 3 });
  sectionState('Bluetooth, a PIN to type', 'bluetooth', () => { bt.pin = { name: `[Keyboard ${AUDIT_LONG}]`, pin: '482915' }; }, { back: 3 });
  sectionState('TV, pairing keypad', 'tv', () => { state.tv = TvUi.demo('pair-code'); });
  sectionState('Updates, apps updating', 'updates', () => auditUpdates('running'));
  sectionState('Updates, Windows updates installing', 'updates', () => auditUpdates('wininstall'));
  sectionState('Updates, an update failed', 'updates', () => auditUpdates('failed'));
  // The launcher's row in each state, and the longest text each place can get (1.0.3's notes ran
  // out of that row on a TV); a screenshot (#audit?page=) ends on that row.
  const launcherRow = { last: () => $('settings').querySelector('[data-id="upd-launcher"]') };
  sectionState('Updates, checking', 'updates', () => auditUpdates('checking'), launcherRow);
  sectionState('Updates, up to date', 'updates', () => auditUpdates('uptodate'), launcherRow);
  sectionState('Updates, the launcher downloading', 'updates', () => auditUpdates('downloading'), launcherRow);
  sectionState('Updates, the launcher waiting for Home', 'updates', () => auditUpdates('waiting'), launcherRow);
  sectionState('Updates, a release that needs setup', 'updates', () => auditUpdates('setup'), launcherRow);
  sectionState('Updates, the longest notes', 'updates', () => auditUpdates('longnotes'), launcherRow);
  sectionState('Updates, long errors', 'updates', () => auditUpdates('longerrors'), launcherRow);
  // The question A asks on that row, its notes in a box that scrolls; the tallest: the longest
  // notes under the longest title and text (a version that did not start here, asked again).
  for (const [name, kind, skipped] of [['ask: update the TV launcher', 'ready'], ['ask: update the TV launcher, the longest notes', 'longnotes'],
    ['ask: try the TV launcher again, the longest notes', 'longnotes', true]]) {
    auditPage(name, { view: 'ask', covers: [], open() {
      auditSettings('updates'); auditUpdates(kind); upd.s.launcher.skipped = !!skipped; render();
      EXT.actions['upd-row'](null, 'launcher');
    } });
  }
  // The button test takes every button (Home stops it): only its card is checked, as the pad's
  // state streams in 30 times a second.
  auditPage('settings: Controller, button test', { view: 'settings', covers: [], scope: '.scol-left', dirs: [], back: 0,
    tick: () => onHost({ type: 'controller.pad', buttons: 0x1000, lx: 12000, ly: -8000, rx: 0, ry: 0 }), open() {
      auditSettings('controller');
      EXT.sections.controller.demo();
      render();
      setFocus($('settings').querySelector('[data-id="pad-test"]'));
      press('a');
    } });
  auditPage('tv: how the box controls it', { view: 'tvmethod', tick: auditTvTick, open() { auditTv(); auditSettings('tv'); go('tvmethod'); } });
  auditPage('tv: how the box controls it, every brand', { view: 'tvmethod', tick: auditTvTick, open() { state.tv = TvUi.demo('crowd'); auditSettings('tv'); go('tvmethod'); } });
  auditPage('button maps', { view: 'maps', tick: auditMapsTick, open() { auditMaps(); go('settings'); go('maps'); } });
  auditPage('button map editor', { view: 'buttons', covers: ['buttons'], tick: auditMapsTick, back: 2, open() { auditMaps(); go('maps'); EXT.actions['map-edit'](null, 'twitch'); } });
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
}
