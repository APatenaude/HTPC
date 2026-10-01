'use strict';
// Settings sections Controller, Sound, Display and About (design: Settings screens),
// added through app.js's settingsSection(). Loaded after app.js.
//
//   To the host:   {type:'controller.test', on} {type:'controller.rumble'} {type:'sound.outputs'}
//                  {type:'sound.output', id} {type:'sound.test'} {type:'display.decodeCheck'} {type:'display.resetBrightness'}
//                  {type:'system.info'} {type:'system.saveLogs'} {type:'system.restart'} {type:'system.setup'}
//                  {type:'setting', key, value} (pointerSpeed, preciseSpeed, scrollSpeed 1-10; showKeyboardAutomatically)
//   From the host: {type:'controller.pad', connected, buttons, lt, rt, lx, ly, rx, ry} (while testing)
//                  {type:'sound.outputs', outputs: [{id, name, isDefault}], canSwitch}
//                  {type:'display.decode', running, result} (Test-HwDecode.ps1's report, or {error})
//                  {type:'system.info', launcher, windows, edge, webview, apps: [{id, name, glyph, color, version}], boxName, hardware,
//                   fullRights (administrator rights with User Account Control off: About says a UAC-on account is safer)}

const more = {
  pad: null,            // last raw controller state (button test)
  testing: false,       // button test on: every button lights up, none navigates
  bHeldSince: 0,
  audio: null,          // { outputs, canSwitch }
  decode: null,         // { running, result }
  system: null,         // versions, box name, hardware
};

// ---- Rows ------------------------------------------------------------------------------------

// Settings › Controller speeds are levels 1-10 (5 = default).
function prefValue(key, fallback) { return state.prefs[key] === undefined ? fallback : state.prefs[key]; }

function setPref(key, value) {
  state.prefs[key] = value;
  send({ type: 'setting', key, value });
  render();
}

function track(percent, white) {
  return `<div class="strack"><div class="sfill${white ? ' white' : ''}" style="width:${percent}%"></div>` +
    `<span class="knob" style="left:${percent}%"></span></div>`;
}

function levelRow(key, label, caption) {
  const v = prefValue(key, 5);
  return `<div class="srow" data-nav data-id="set-${key}" data-level="${key}" data-edit>` +
    `<div class="text"><span class="label">${esc(label)}</span><span class="caption">${esc(caption)}</span></div>` +
    `${track(v * 10)}<span class="svalue">${v}</span></div>`;
}

function flagRow(key, label, caption) {
  return `<div class="srow" data-nav data-id="set-${key}" data-flag="${key}">` +
    `<div class="text"><span class="label">${esc(label)}</span><span class="caption">${esc(caption)}</span></div>` +
    toggle(prefValue(key, true)) + '</div>';
}

// Volume and brightness: app.js's own sliders (left/right once picked with A, sent as {type: key}).
function sliderRow(key, label, caption, white) {
  return `<div class="srow" data-nav data-id="set-${key}" data-slider="${key}" data-edit>` +
    `<div class="text"><span class="label">${esc(label)}</span><span class="caption">${esc(caption)}</span></div>` +
    `${track(state[key], white)}<span class="svalue">${state[key]}</span></div>`;
}

// The rows above: left/right on a level row (only once A has picked it: app.js's editPress),
// A on a flag.
function pressRows(button, el) {
  if (!el) return false;
  const step = button === 'right' ? 1 : button === 'left' ? -1 : 0;
  if (el.dataset.level && step) {
    const key = el.dataset.level;
    setPref(key, Math.max(1, Math.min(10, prefValue(key, 5) + step)));
    return true;
  }
  if (el.dataset.flag && button === 'a') {
    setPref(el.dataset.flag, !prefValue(el.dataset.flag, true));
    return true;
  }
  return false;
}

function when(iso) {
  const d = new Date(iso);
  return isNaN(d) ? '' : d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' }) + ', ' + timeText(d);
}

// ---- Controller ------------------------------------------------------------------------------

const TEST_BUTTONS = [['A', 0x1000], ['B', 0x2000], ['X', 0x4000], ['Y', 0x8000], ['LB', 0x0100], ['RB', 0x0200],
  ['LT', 'lt'], ['RT', 'rt'], ['Select', 0x0020], ['Start', 0x0010], ['L3', 0x0040], ['R3', 0x0080], ['Home', 0x0400],
  ['↑', 0x0001], ['↓', 0x0002], ['←', 0x0004], ['→', 0x0008]];

function lit(p, bit) {
  if (!p) return false;
  if (bit === 'lt' || bit === 'rt') return p[bit] >= 48;
  return (p.buttons & bit) !== 0;
}

function stickDot(x, y) {
  // -32768..32767 to the ring, up is up.
  const px = 50 + (x / 32768) * 38, py = 50 - (y / 32768) * 38;
  return `<span class="dot" style="left:${px.toFixed(1)}%;top:${py.toFixed(1)}%"></span>`;
}

function testCard() {
  const p = more.testing ? more.pad : null;
  const chips = TEST_BUTTONS.map(([label, bit]) => `<span class="chip${lit(p, bit) ? ' lit' : ''}">${esc(label)}</span>`).join('');
  const sticks = more.testing
    ? '<div class="sticks">' +
        `<div class="stick"><span class="ring">${stickDot(p ? p.lx : 0, p ? p.ly : 0)}</span><span class="cap">L stick</span></div>` +
        `<div class="stick"><span class="ring">${stickDot(p ? p.rx : 0, p ? p.ry : 0)}</span><span class="cap">R stick</span></div>` +
      '</div>'
    : '';
  const lead = more.testing ? 'Press anything: it lights up here. Hold B or press Home to stop.' : 'Press A, then press anything.';
  return `<div class="scard test${more.testing ? ' on' : ''}" data-nav data-id="pad-test" data-act="pad-test">` +
    `<span class="scard-title">Button test</span><span class="scard-text">${esc(lead)}</span>` +
    `<div class="chips">${chips}</div>${sticks}</div>`;
}

function batteryLine() {
  if (!state.controller) return ['Not connected. Press Home on the controller to wake it.', null];
  if (state.battery === 'wired') return ['Connected. It doesn’t report its battery.', null];
  const level = { full: 100, medium: 60, low: 25, empty: 5 }[state.battery];
  return level === undefined ? ['Connected', null] : [`Connected · battery ${state.battery}`, level];
}

function setTesting(on) {
  more.testing = on;
  more.pad = null;
  more.bHeldSince = 0;
  send({ type: 'controller.test', on });
  render();
}

settingsSection('controller', {
  render() {
    const [line, level] = batteryLine();
    const bar = level === null ? '' : `<div class="battery"><div class="${level <= 25 ? 'low' : ''}" style="width:${level}%"></div></div>`;
    return '<header><h1>Controller</h1><p>How the controller moves the pointer, and what each button does in each app.</p></header>' +
      '<div class="scols">' +
        '<div class="scol-left">' +
          `<div class="scard"><div class="scard-head">${icon('controller', 44)}<span>Controller</span></div>` +
            `<span class="scard-text">${esc(line)}</span>${bar}</div>` +
          testCard() +
          '<div class="sbuttons"><div class="sbutton" data-nav data-id="rumble" data-act="rumble">Test rumble</div></div>' +
        '</div>' +
        '<div class="scol-right">' +
          levelRow('pointerSpeed', 'Pointer speed', 'Mouse and Keyboard modes') +
          levelRow('preciseSpeed', 'Slow pointer', 'While RT is held') +
          levelRow('scrollSpeed', 'Scroll speed', 'Mouse mode, right stick') +
          '<div class="srow" data-nav data-id="set-maps" data-act="maps">' +
            '<div class="text"><span class="label">Button maps per app</span><span class="caption">What each button does in each app: keys, clicks, media, the keyboard</span></div>' +
            `<div class="value">${icon('chevright', 30, 2)}</div></div>` +
          flagRow('showKeyboardAutomatically', 'Show keyboard automatically', 'When a text or password box is selected. Never in YouTube, Jellyfin, Moonlight and Plex, which have their own.') +
          '<p class="snote">R3 (right stick press) opens the keyboard; an app’s map can give it to another button. ' +
            'Start + Up/Down changes the volume in every app, Start + Left mutes. ' +
            'Home: tap for the menu, hold for Power. In Moonlight a tap goes to the game PC and holding opens this menu.</p>' +
        '</div>' +
      '</div>';
  },
  press(button, el) {
    // Button test: no button navigates; Home (or holding B) stops it.
    if (more.testing) {
      if (button === 'home') setTesting(false);
      return true;
    }
    return pressRows(button, el);
  },
  left() { if (more.testing) setTesting(false); },
  demo() { state.controller = true; state.battery = 'wired'; },
});

onAction('pad-test', () => setTesting(!more.testing));
onAction('rumble', () => send({ type: 'controller.rumble' }));

hostMessage('controller.pad', (m) => {
  if (!more.testing) return;
  more.pad = m;
  // Hold B for 1 s to stop.
  if (m.buttons & 0x2000) {
    if (!more.bHeldSince) more.bHeldSince = Date.now();
    else if (Date.now() - more.bHeldSince > 1000) { setTesting(false); return; }
  } else more.bHeldSince = 0;
  // Only the test card changes, 30 times a second: no full render, and in place (patchNode): a
  // card drawn afresh each time drew its focus ring in again, 30 times a second.
  const card = document.querySelector('#settings .scard.test');
  if (!card) return;
  const t = document.createElement('template');
  t.innerHTML = testCard();
  patchNode(card, t.content.firstChild);
});

// ---- Sound -----------------------------------------------------------------------------------

function stepOutput(step) {
  const a = more.audio;
  if (!a || !a.canSwitch || a.outputs.length < 2) return;
  const i = a.outputs.findIndex((o) => o.isDefault);
  const next = a.outputs[(Math.max(i, 0) + step + a.outputs.length) % a.outputs.length];
  for (const o of a.outputs) o.isDefault = o === next;
  send({ type: 'sound.output', id: next.id });
  render();
}

settingsSection('sound', {
  render() {
    const a = more.audio;
    const outputs = a ? a.outputs : [];
    const current = outputs.find((o) => o.isDefault);
    const row = (caption, value, nav) =>
      `<div class="srow"${nav ? ' data-nav data-id="snd-output" data-output data-edit' : ''}>` +
      `<div class="text"><span class="label">Output</span><span class="caption">${esc(caption)}</span></div>${value}</div>`;
    let output;
    if (!a) output = row(hostWaitText('sound', 'Looking…', 'Windows didn’t list the sound outputs. Open Sound again to try once more.'), '');
    else if (!outputs.length) output = row('No sound output found', '');
    else if (a.canSwitch && outputs.length > 1) {
      output = row('Where the sound goes: the TV, a soundbar, Bluetooth headphones',
        `<div class="value">${icon('chevleft', 28, 2)}${esc(current ? current.name : 'Pick one')}${icon('chevright', 28, 2)}</div>`, true);
    } else {
      output = row(a.canSwitch ? 'The only one connected' : 'Windows would not switch from here. Connected: ' + outputs.map((o) => o.name).join(', '),
        `<div class="value">${esc(current ? current.name : '')}</div>`);
    }
    return `<header><h1>Sound</h1><p>${esc(current ? `Sound plays through ${current.name}.` : 'Windows volume, for every app.')}</p></header>` +
      output +
      sliderRow('volume', 'Volume', 'Windows volume. Set the TV’s own volume once and leave it there.') +
      soundsRow() +   // Interface sounds: Off, Low, Medium (sounds.js)
      '<div class="sbuttons"><div class="sbutton" data-nav data-id="snd-test" data-act="test-sound">Play a test sound</div></div>';
  },
  press(button, el) {
    // Left/right on the output row once A has picked it (app.js's editPress).
    if (el && el.dataset.output !== undefined && (button === 'left' || button === 'right')) {
      stepOutput(button === 'left' ? -1 : 1);
      return true;
    }
    return false;
  },
  shown() { hostAsked('sound'); send({ type: 'sound.outputs' }); },
  demo() {
    more.audio = { canSwitch: true, outputs: [{ id: '1', name: 'TCL TV (HDMI)', isDefault: true }, { id: '2', name: 'Soundbar', isDefault: false }] };
    state.volume = 62;
  },
});

onAction('test-sound', () => send({ type: 'sound.test' }));
hostMessage('sound.outputs', (m) => { hostAnswered('sound'); more.audio = m; if (state.view === 'settings') render(); });

// ---- Display (brightness, the decode check, SPEC N5 and N12) ----------------------------------

function decodeSummary(r) {
  if (!r) return 'Not checked yet';
  if (r.error) return r.error;
  if (r.cause) return r.cause; // no graphics driver (Microsoft Basic Display Adapter)
  const codecs = r.codecs || [];
  const ok = codecs.filter((c) => c.pass).length;
  return (ok === codecs.length ? `The driver decodes all ${ok} formats in 4K` : `The driver decodes ${ok} of ${codecs.length} formats in 4K`) +
    (r.checkedAt ? ` (checked ${when(r.checkedAt)})` : '');
}

function codecList(r) {
  if (!r || r.error || !r.codecs) return '';
  return '<div class="codecs">' + r.codecs.map((c) => {
    const what = !c.driver ? 'not in the driver' : !c.uhd ? 'not in 4K' : !c.formatSupported ? `no ${c.format} output` : '4K';
    return `<div class="codec ${c.pass ? 'ok' : 'bad'}">${icon(c.pass ? 'check' : 'warn', 26, 2.25)}` +
      `<span class="name">${esc(c.name)}</span><span class="what">${esc(what)}</span></div>`;
  }).join('') + '</div>';
}

settingsSection('display', {
  render() {
    const d = more.decode || {};
    const r = d.result;
    // With two GPUs (integrated and discrete) the check is the TV's; the other is named.
    const others = r && r.adapter && r.adapter.otherAdapters && r.adapter.otherAdapters.length ? ` · the TV’s GPU (also: ${r.adapter.otherAdapters.join(', ')})` : '';
    const adapter = r && r.adapter && r.adapter.name ? `${r.adapter.name} · driver ${r.adapter.driverVersion}${others}` : '';
    return '<header><h1>Display</h1><p>Brightness for every app, and which video formats the graphics chip decodes.</p></header>' +
      sliderRow('brightness', 'Brightness', 'Dims everything on screen, in every app and on the desktop. Can only go darker than the TV’s own setting.', true) +
      '<div class="srow">' +
        '<div class="text"><span class="label">Reset brightness</span>' +
          '<span class="caption">Puts the screen back to its plain, undimmed setting, should it ever stay dim.</span></div>' +
        '<div class="sbutton" data-nav data-id="brightness-reset" data-act="brightness-reset">Reset</div>' +
      '</div>' +
      '<div class="srow decode">' +
        '<div class="text"><span class="label">Hardware video decoding</span>' +
          `<span class="caption">${esc(d.running ? 'Checking… about 20 seconds' : decodeSummary(r))}</span>` +
          (adapter ? `<span class="caption">${esc(adapter)}</span>` : '') + '</div>' +
        `<div class="sbutton" data-nav data-id="decode-check" data-act="decode-check">${d.running ? 'Checking…' : 'Check now'}</div>` +
      '</div>' +
      codecList(r) +
      '<p class="snote">Check now asks the graphics driver which of these formats it can decode in 4K. It reads what the driver says; it does not play a video.</p>';
  },
  demo() {
    state.brightness = 100;
    const codec = (id, name, format) => ({ id, name, format, driver: true, uhd: true, formatSupported: true, pass: true });
    more.decode = { running: false, result: { checkedAt: '2026-09-26T22:52:23', pass: true,
      adapter: { name: 'Intel(R) UHD Graphics', driverVersion: '32.0.101.7088' },
      codecs: [codec('h264', 'H.264', 'NV12'), codec('hevc-main', 'HEVC Main', 'NV12'), codec('hevc-main10', 'HEVC Main10', 'P010'),
        codec('vp9-p0', 'VP9 Profile 0', 'NV12'), codec('vp9-p2', 'VP9 Profile 2', 'P010'), codec('av1-main8', 'AV1 Main 8-bit', 'NV12'),
        { ...codec('av1-main10', 'AV1 Main 10-bit', 'P010'), uhd: false, pass: false }] } };
  },
});

onAction('brightness-reset', () => {
  state.brightness = 100;
  send({ type: 'display.resetBrightness' });
  toast('Brightness reset');
  render();
});
onAction('decode-check', () => {
  if (more.decode && more.decode.running) return;
  more.decode = { running: true, result: more.decode && more.decode.result };
  send({ type: 'display.decodeCheck' });
  render();
});
hostMessage('display.decode', (m) => {
  more.decode = m;
  if (m.result && m.result.error && !m.running) toast(m.result.error, 'warn');
  if (state.view === 'settings') render();
});

// Settings › Updates is updates.js's (versions, updates of apps, the launcher and Windows).

hostMessage('system.info', (m) => { more.system = m; if (state.view === 'settings') render(); });

function systemDemo() {
  more.system = { launcher: '0.1.0', windows: 'Windows 11 IoT Enterprise LTSC 2024 · 24H2 · build 26100.4061', edge: '140.0.3485.94',
    webview: '140.0.3485.94', boxName: 'TV', hardware: 'Intel(R) N97 · 16 GB',
    apps: [{ id: 'youtube', name: 'YouTube', glyph: 'youtube', color: '#FF5B52', version: '1.3.9' },
      { id: 'stremio', name: 'Stremio', glyph: 'film', color: '#7C8CFF', version: '5.0.0.216' },
      { id: 'jellyfin', name: 'Jellyfin', glyph: 'library', color: '#3DC0F0', version: '1.11.1' },
      { id: 'moonlight', name: 'Moonlight', glyph: 'moon', color: '#F5D16B', version: '6.1.0' }] };
}

// ---- About -----------------------------------------------------------------------------------

settingsSection('about', {
  render() {
    const s = more.system || {};
    const decode = more.decode && more.decode.result;
    const fact = (k, v, wide) => `<div class="fact${wide ? ' wide' : ''}"><span class="fact-key">${esc(k)}</span><span class="fact-value">${esc(v || '…')}</span></div>`;
    return '<header><h1>About &amp; Desktop mode</h1></header>' +
      `<div class="scard wide">${icon('desktop', 64, 1.5)}` +
        '<div class="text"><span class="scard-title big">Desktop mode</span>' +
        '<span class="scard-text">Opens the normal Windows desktop for maintenance. To come back, press Home, then Back to TV.</span></div>' +
        '<div class="sbutton" data-nav data-id="about-desktop" data-act="power-action" data-arg="desktop">Open Desktop mode</div></div>' +
      '<div class="facts">' +
        fact('Box name', s.boxName) + fact('Launcher', s.launcher) +
        fact('Windows', s.windows, true) +
        fact('Hardware', s.hardware) + fact('Hardware video decoding', decodeSummary(decode)) +
      '</div>' +
      (s.fullRights ? '<p class="snote warn">The launcher runs with administrator rights, and so does every app opened from it: ' +
        'User Account Control is off, or this is Windows’ built-in Administrator. A TV account with User Account Control on is safer.</p>' : '') +
      '<div class="sbuttons">' +
        '<div class="sbutton" data-nav data-id="about-restart" data-act="restart-launcher">Restart launcher</div>' +
        '<div class="sbutton" data-nav data-id="about-logs" data-act="save-logs">Save logs to USB stick</div>' +
        '<div class="sbutton" data-nav data-id="about-setup" data-act="run-setup">Run setup again</div>' +
      '</div>' +
      '<p class="snote">Run setup again asks for Windows’ permission: that needs a keyboard or mouse.</p>';
  },
  shown() { send({ type: 'system.info' }); },
  demo: systemDemo,
});

onAction('restart-launcher', () => ask({ title: 'Restart the launcher?', text: 'Apps stay open. The home screen is back in a few seconds.', yes: 'Restart',
  onYes: () => send({ type: 'system.restart' }) }));
onAction('save-logs', () => { toast('Saving the logs…'); send({ type: 'system.saveLogs' }); });
onAction('run-setup', () => ask({ title: 'Run setup again?', text: 'Setup takes over the screen until it is done. Windows asks for permission first: have a keyboard or mouse at hand.', yes: 'Run setup',
  onYes: () => send({ type: 'system.setup' }) }));
