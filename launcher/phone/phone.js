'use strict';

// The phone remote (SPEC N8): Remote, Type, Playing. Talks to the launcher (PhoneServer.cs) over
// one WebSocket; the launcher decides where each button goes (the TV's own screens, the
// on-screen keyboard or the app in front). Messages: PhoneProtocol.cs.
//
// ?demo=remote|arrows|type|playing|pair|asleep|sleep|timer shows a screen with made-up data and
// no box (screenshots). It sends nothing.

const PROTOCOL = 1;
const params = new URLSearchParams(location.search);
const demo = params.get('demo');
const $ = (id) => document.getElementById(id);

// Errors show on the page itself: the headless screenshot tests read them there.
window.addEventListener('error', (e) => showError(`${e.message} (${e.filename}:${e.lineno})`));
window.addEventListener('unhandledrejection', (e) => showError(`Unhandled: ${e.reason}`));
function showError(text) { const el = $('errors'); el.hidden = false; el.textContent += text + '\n'; }

// ---- Icons (the design canvas's line icons, 24x24, stroked) ----------------------------------

const ICONS = {
  power: 'M12 3v8M6.3 6.3a8 8 0 1 0 11.4 0',
  touchpad: 'M4 4h16v16H4zM4 15h16M12 15v5',
  chevup: 'M5 15l7-7 7 7', chevdown: 'M5 9l7 7 7-7', chevleft: 'M15 5l-7 7 7 7', chevright: 'M9 5l7 7-7 7',
  back: 'M10 6l-6 6 6 6M4 12h16',
  home: 'M3 11l9-8 9 8M5 9.5V21h14V9.5M10 21v-6h4v6',
  menu: 'M4 7h16M4 12h16M4 17h16',
  voldown: 'M4 9h4l5-4v14l-5-4H4zM17 12h4',
  volup: 'M4 9h4l5-4v14l-5-4H4zM17 12h4M19 10v4',
  mute: 'M4 9h4l5-4v14l-5-4H4zM17 9l5 6M22 9l-5 6',
  speaker: 'M4 9h4l5-4v14l-5-4H4zM16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12',
  sun: 'M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8zM12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4',
  enter: 'M20 5v7a3 3 0 0 1-3 3H5M9 11l-4 4 4 4',
  backspace: 'M9 5h12v14H9l-6-7zM12 9l6 6M18 9l-6 6',
  close: 'M6 6l12 12M18 6L6 18',
  tab: 'M3 12h14M13 8l4 4-4 4M21 6v12',
  shifttab: 'M21 12H7M11 8l-4 4 4 4M3 6v12',
  film: 'M4 4h16v16H4zM8 4v16M16 4v16M4 9h4M4 15h4M16 9h4M16 15h4',
  play: 'M8 5.5v13l11-6.5z',
  pause: 'M8 5v14M16 5v14',
  rewind: 'M11 6l-7 6 7 6zM20 6l-7 6 7 6z',
  forward: 'M13 6l7 6-7 6zM4 6l7 6-7 6z',
  skipprev: 'M6 5v14M19 6l-9 6 9 6z',
  skipnext: 'M18 5v14M5 6l9 6-9 6z',
  timer: 'M12 8a7 7 0 1 0 0 14a7 7 0 1 0 0-14zM12 12v3.5l2.5 1.5M10 3h4M12 3v5',
  controller: 'M6 8h12a4 4 0 0 1 4 4v2a3 3 0 0 1-5.4 1.8L15 14H9l-1.6 1.8A3 3 0 0 1 2 14v-2a4 4 0 0 1 4-4zM7 10.5v3M5.5 12h3M15.5 11.5h0M17.5 13h0',
  keyboard: 'M2 6h20v12H2zM6 10h0M10 10h0M14 10h0M18 10h0M7 14h10',
  moon: 'M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z',
  phone: 'M7 2h10v20H7zM11 18.5h2',
  share: 'M12 3v12M8 7l4-4 4 4M5 11v9h14v-9',
  tv: 'M3 5h18v12H3zM8 21h8',
};

function icon(name, size = 22, weight = 2) {
  return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="${weight}" ` +
    `stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path d="${ICONS[name] || ''}"/></svg>`;
}

function drawIcons(root) {
  for (const el of root.querySelectorAll('[data-icon]')) {
    el.querySelector('svg')?.remove();
    el.insertAdjacentHTML('afterbegin', icon(el.dataset.icon, Number(el.dataset.size || 22), Number(el.dataset.weight || 2)));
  }
}
drawIcons(document);

// Per-phone conveniences (the tab, touchpad or arrows). Private browsing may refuse storage.
const store = {
  get(key, fallback) { try { const v = localStorage.getItem('tvremote.' + key); return v === null ? fallback : v; } catch (e) { return fallback; } },
  set(key, value) { try { localStorage.setItem('tvremote.' + key, value); } catch (e) { /* not kept */ } },
};

const state = {
  tab: ['remote', 'type', 'playing'].includes(store.get('tab', '')) ? store.get('tab', '') : 'remote',
  pad: store.get('pad', ''),      // 'touchpad' | 'arrows' | '' (not chosen: touchpad, or arrows with a screen reader)
  guessArrows: false,             // a screen reader or keyboard seems to be in use
  conn: 'connecting',             // connecting | open | pairing
  box: { standby: false, volume: 50, muted: false, brightness: 100, timer: null, front: null, app: null, canType: true, media: null },
  receivedAt: Date.now(),         // when the last state came (media position and timer count from there)
  sentText: '',                   // what the Type field has sent to the TV so far
};

const vibrate = (ms = 8) => { try { if (navigator.vibrate) navigator.vibrate(ms); } catch (e) { /* no haptics */ } };

// ---- Connection -----------------------------------------------------------------------------------

let ws = null, retryMs = 500, pingTimer = 0, reconnectTimer = 0, failures = 0;
const sentLog = [];   // demo and self-test: what would have gone to the box

function send(msg) {
  if (demo) { sentLog.push(msg); return; }
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
}

function connect() {
  clearTimeout(reconnectTimer);
  if (ws && ws.readyState <= WebSocket.OPEN) return;
  if (state.conn !== 'pairing') setConn('connecting');
  // The /share page asks for the Share sheet's ticket (share=1); other tabs leave it alone.
  const socket = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws${pendingShare !== undefined ? '?share=1' : ''}`);
  ws = socket;
  socket.onopen = () => {
    retryMs = 500;
    failures = 0;
    $('lost').hidden = true;
    clearInterval(pingTimer);
    pingTimer = setInterval(() => send({ t: 'ping' }), 5000); // the box drops a phone silent for 15 s
  };
  socket.onmessage = (e) => {
    let m;
    try { m = JSON.parse(e.data); } catch (err) { return; }
    onBox(m);
  };
  socket.onclose = () => {
    if (ws !== socket) return;
    clearInterval(pingTimer);
    ws = null;
    if (state.conn === 'pairing') return; // connects again once paired
    setConn('connecting');
    // About 10 s without the box: say so (it may be off, or have a new address: the QR code again).
    if (++failures >= 4) $('lost').hidden = false;
    reconnectTimer = setTimeout(connect, retryMs);
    retryMs = Math.min(retryMs * 2, 5000);
  };
}

// Back from the background (iPhone suspends the page): connect at once.
document.addEventListener('visibilitychange', () => { if (!document.hidden && !demo && state.conn !== 'pairing') connect(); });
addEventListener('pageshow', () => { if (!demo && state.conn !== 'pairing') connect(); });
addEventListener('online', () => { if (!demo && state.conn !== 'pairing') connect(); });

function onBox(m) {
  switch (m.t) {
    case 'hello':
      if (m.v !== PROTOCOL) { reloadOnce(); return; }
      if (!m.paired) { showPairing(); return; }
      hidePairing();
      setConn('open');
      applyState(m.state);
      if (m.ca) { $('ca-fingerprint').textContent = groupFingerprint(m.ca); $('ca-here').hidden = false; }
      if (pendingShare !== undefined) handleShare(m.share || null);
      break;
    case 'shortcutKey':
      $('sc-url').value = m.url;
      $('sc-key').value = 'Bearer ' + m.token;
      toast('Key made: copy it into the Shortcut');
      break;
    case 'state': applyState(m.state); break;
    case 'toast': toast(m.text, m.kind); break;
    case 'warn': showBanner(m.text, m.extend ? '+15 min' : null, () => send({ t: 'timerExtend' })); timerBanner = !!m.extend; break;
    case 'bye': toast('This phone was removed on the TV', 'warn'); break;
  }
}

// A page from an older launcher: load the new one (once, so a mismatch cannot loop).
function reloadOnce() {
  try {
    if (sessionStorage.getItem('tvremote.reloaded') === String(PROTOCOL)) { showError('The TV box has a different version of this page.'); return; }
    sessionStorage.setItem('tvremote.reloaded', String(PROTOCOL));
  } catch (e) { /* reload anyway */ }
  location.reload();
}

function setConn(conn) {
  state.conn = conn;
  renderStatus();
}

function applyState(s) {
  if (!s || typeof s !== 'object') return;
  Object.assign(state.box, s);
  state.receivedAt = Date.now();
  // The sleep warning is over once the timer is off or has more than a minute again (+15 on the TV).
  const t = state.box.timer;
  if (timerBanner && (!t || typeof t.left !== 'number' || t.left > 60)) { $('banner').hidden = true; timerBanner = false; }
  render();
}

// ---- Rendering ---------------------------------------------------------------------------------------

const TITLES = { remote: 'TV', type: 'Type on the TV', playing: 'Now playing' };

function render() {
  renderStatus();
  renderTab();
  renderRemote();
  renderType();
  renderPlaying();
}

function renderStatus() {
  const asleep = state.conn === 'open' && state.box.standby;
  const el = $('status');
  el.className = 'status' + (asleep ? ' asleep' : state.conn === 'open' ? ' open' : '');
  $('status-text').textContent = asleep ? 'Asleep' : state.conn === 'open' ? 'Connected' : 'Connecting…';
  $('asleep').hidden = !asleep;
  const power = $('power');
  power.setAttribute('aria-label', asleep ? 'Wake the TV box' : 'Sleep the TV box');
  power.disabled = state.conn !== 'open';
}

function renderTab() {
  const tab = state.tab;
  $('app').dataset.tab = tab;
  $('title').textContent = TITLES[tab];
  for (const t of ['remote', 'type', 'playing']) $('tab-' + t).hidden = t !== tab;
  for (const b of document.querySelectorAll('.tabs button')) {
    if (b.dataset.tab === tab) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
  }
  // As on the design: the power button on Remote, the playing app's name on Playing.
  const media = state.box.media;
  $('power').hidden = tab !== 'remote';
  $('media-app').hidden = tab !== 'playing' || !media || !media.app;
  if (media && media.app) $('media-app').textContent = media.app;
  $('status').hidden = tab === 'playing';
}

function padMode() { return state.pad || (state.guessArrows ? 'arrows' : 'touchpad'); }

function renderRemote() {
  const arrows = padMode() === 'arrows';
  $('seg-touchpad').setAttribute('aria-checked', String(!arrows));
  $('seg-arrows').setAttribute('aria-checked', String(arrows));
  $('touchpad').hidden = arrows;
  $('dpad-wrap').hidden = !arrows;
  // Over the launcher's own screens the touchpad moves the focus; in an app, the pointer.
  const launcher = state.box.front === 'launcher';
  $('pad-hint').textContent = launcher ? 'Swipe to move around' : 'Swipe to move the pointer';
  $('pad-sub').textContent = launcher ? 'Tap to select · two fingers tap to go back' : 'Tap to click · two fingers to scroll';
  $('mute').setAttribute('aria-pressed', String(!!state.box.muted));
  $('mute').setAttribute('aria-label', state.box.muted ? 'Unmute' : 'Mute');
  setSlider('brightness', state.box.brightness);
}

function renderType() {
  const b = state.box;
  let lead = 'Letters appear on the TV as you type, in whatever is selected there: a search box, an address, Twitch chat.';
  if (b.front === 'launcher') lead = 'Open an app on the TV, then type here: letters appear in whatever is selected there.';
  else if (b.front === 'app' && !b.canType) lead = `Typing from the phone doesn’t work in ${b.app || 'this app'}. Use the controller.`;
  $('type-lead').textContent = lead;
}

function mediaPosition(m) {
  if (!m) return 0;
  const moved = m.playing ? (Date.now() - state.receivedAt) / 1000 : 0;
  return Math.min(m.duration || Infinity, (m.position || 0) + moved);
}

let artShown = null;
function renderPlaying() {
  const m = state.box.media;
  $('media-title').textContent = m ? (m.title || 'Playing') : 'Nothing playing';
  $('media-sub').textContent = m ? (m.subtitle || '') : 'Buttons still work: they reach whatever plays on the TV.';
  const duration = m && m.duration > 0 ? m.duration : 0;
  const seek = $('seek');
  seek.disabled = !m || !m.canSeek || !duration;
  if (!sliderBusy('seek')) {
    seek.max = String(Math.max(1, Math.round(duration)));
    seek.value = String(Math.round(mediaPosition(m)));
  }
  $('pos').textContent = PhoneLogic.clock(sliderBusy('seek') ? Number(seek.value) : mediaPosition(m));
  $('dur').textContent = PhoneLogic.clock(duration);
  const playing = !!(m && m.playing);
  const pp = $('playpause');
  if (pp.dataset.icon !== (playing ? 'pause' : 'play')) {
    pp.dataset.icon = playing ? 'pause' : 'play';
    pp.setAttribute('aria-label', playing ? 'Pause' : 'Play');
    drawIcons(pp.parentElement);
  }
  $('prev').disabled = !!m && m.canPrevious === false;
  $('next').disabled = !!m && m.canNext === false;
  const art = m && m.art ? `/art?v=${encodeURIComponent(m.art)}` : null;
  if (art !== artShown) {
    artShown = art;
    const img = $('art-img');
    img.hidden = !art;
    if (art) { img.onerror = () => { img.hidden = true; }; img.src = art; } else img.removeAttribute('src');
  }
  setSlider('volume', state.box.muted ? 0 : state.box.volume);
  const t = state.box.timer;
  $('timer-text').textContent = timerText(t);
  $('timer-action').textContent = t ? 'Change' : 'Set';
}

function timerLeftMinutes(t) {
  if (!t || t.endsAt === 'video') return null;
  const left = typeof t.left === 'number' ? t.left - (Date.now() - state.receivedAt) / 1000 : (t.endsAt - Date.now()) / 1000;
  return Math.max(0, Math.ceil(left / 60));
}

function timerText(t) {
  if (!t) return 'Sleep timer: off';
  if (t.endsAt === 'video') return 'Sleep when this video ends';
  return `Sleep timer: ${timerLeftMinutes(t)} min left`;
}

// Sliders the finger is on are not moved by the box's updates (they would jump back).
const busyUntil = {};
function sliderBusy(id) { return (busyUntil[id] || 0) > Date.now(); }
function setSlider(id, value) {
  if (sliderBusy(id)) return;
  const input = $(id);
  input.value = String(value);
  const out = $(id + '-value');
  if (out) out.textContent = String(value);
}

// Once a second: the playing position and the timer move on by themselves.
setInterval(() => { if (state.tab === 'playing') renderPlaying(); }, 1000);

// ---- Buttons ----------------------------------------------------------------------------------------

// A press that works with a finger (on touch-down, with repeat or hold where asked) and with a
// screen reader or keyboard (a click that no finger started). Keeps the text field's focus, so
// the phone's keyboard stays up while Type's buttons are used.
function pressable(el, onPress, { repeat = false, onHold = null } = {}) {
  let timer = 0, holdDone = false, touching = false, lastPointer = 0;
  const stop = () => { clearTimeout(timer); clearInterval(timer); timer = 0; el.classList.remove('pressed'); };
  el.addEventListener('pointerdown', (e) => {
    if (e.button !== 0 || el.disabled) return;
    e.preventDefault();
    touching = true; holdDone = false; lastPointer = Date.now();
    el.classList.add('pressed');
    try { el.setPointerCapture(e.pointerId); } catch (err) { /* synthetic event */ }
    if (onHold) {
      timer = setTimeout(() => { holdDone = true; vibrate(25); onHold(); el.classList.remove('pressed'); }, 600);
      return;
    }
    vibrate();
    onPress();
    if (repeat) timer = setTimeout(() => { timer = setInterval(onPress, 110); }, 400);
  });
  const end = (fire) => {
    if (touching && onHold && !holdDone && fire) { vibrate(); onPress(); }
    touching = false;
    stop();
  };
  el.addEventListener('pointerup', () => end(true));
  el.addEventListener('pointercancel', () => end(false));
  // No click (and no focus move) after a finger press: the text field keeps the focus, and the
  // phone's keyboard stays up.
  el.addEventListener('touchend', (e) => { if (e.cancelable) e.preventDefault(); });
  el.addEventListener('click', (e) => {
    if (Date.now() - lastPointer < 1000) return; // the finger already pressed it
    e.preventDefault();
    noticeAssistive();
    onPress();
  });
  el.addEventListener('contextmenu', (e) => e.preventDefault());
}

// A click that no finger started: a screen reader (VoiceOver, TalkBack) or a keyboard. Then the
// Remote tab defaults to Arrows (the touchpad cannot be used without sight).
function noticeAssistive() {
  if (state.guessArrows) return;
  state.guessArrows = true;
  renderRemote();
}

const key = (k) => send({ t: 'key', k });

for (const b of document.querySelectorAll('[data-key]')) {
  const k = b.dataset.key;
  pressable(b, () => key(k), { repeat: ['up', 'down', 'left', 'right'].includes(k) });
}
pressable($('home'), () => key('home'), { onHold: () => key('homeHold') });
for (const b of document.querySelectorAll('[data-step]')) {
  const step = Number(b.dataset.step);
  pressable(b, () => send({ t: 'volumeStep', d: step }), { repeat: true });
}
pressable($('mute'), () => send({ t: 'mute' }));

for (const b of document.querySelectorAll('.tabs button')) {
  b.addEventListener('click', () => {
    state.tab = b.dataset.tab;
    store.set('tab', state.tab);
    render();
    $('main').scrollTop = 0;
  });
}

function choosePad(mode) {
  state.pad = mode;
  store.set('pad', mode);
  renderRemote();
}
$('seg-touchpad').addEventListener('click', () => choosePad('touchpad'));
$('seg-arrows').addEventListener('click', () => choosePad('arrows'));

// Sliders: sent as they move (at most every 80 ms), and once more where they stop.
function slider(id, message) {
  const input = $(id);
  let last = 0, pending = 0;
  const out = $(id + '-value');
  const sendNow = () => { pending = 0; last = Date.now(); send(message(Number(input.value))); };
  input.addEventListener('input', () => {
    busyUntil[id] = Date.now() + 1500;
    if (out) out.textContent = input.value;
    if (Date.now() - last >= 80) sendNow();
    else if (!pending) pending = setTimeout(sendNow, 80 - (Date.now() - last));
  });
  input.addEventListener('change', () => { clearTimeout(pending); sendNow(); busyUntil[id] = Date.now() + 1000; });
}
slider('brightness', (v) => ({ t: 'brightness', v: Math.max(10, v) }));
slider('volume', (v) => ({ t: 'volume', v }));

// The power button: sleep (asked first) or, while asleep, wake.
$('power').addEventListener('click', () => {
  if (state.box.standby) { send({ t: 'wake' }); return; }
  openSheet('Sleep the TV box?', 'The TV turns off. Wake it from here, or hold Home on the controller.',
    [{ label: 'Sleep', primary: true, full: true, run: () => send({ t: 'sleep' }) }]);
});
$('wake').addEventListener('click', () => send({ t: 'wake' }));

// ---- Touchpad ---------------------------------------------------------------------------------------

const pad = $('touchpad');
const gestures = new PhoneLogic.Gestures();
let frame = 0, lastFlush = 0;

function flushMotion() {
  const now = performance.now();
  const m = gestures.take();
  const ms = lastFlush ? Math.min(now - lastFlush, 1000) : 16;
  lastFlush = now;
  const r = (v) => Math.round(v * 100) / 100;
  if (m.dx || m.dy) send({ t: 'move', dx: r(m.dx), dy: r(m.dy), ms: Math.max(1, Math.round(ms)) });
  if (m.sx || m.sy) send({ t: 'scroll', dx: r(m.sx), dy: r(m.sy) });
}

// One message per frame while a finger is down.
function padFrame() {
  frame = 0;
  for (const ev of gestures.tick(performance.now())) { vibrate(20); send(ev); }
  flushMotion();
  if (gestures.pointers.size) frame = requestAnimationFrame(padFrame);
  else lastFlush = 0;
}

pad.addEventListener('pointerdown', (e) => {
  e.preventDefault();
  try { pad.setPointerCapture(e.pointerId); } catch (err) { /* synthetic event */ }
  gestures.down(e.pointerId, e.clientX, e.clientY, performance.now());
  pad.classList.add('active');
  if (!frame) frame = requestAnimationFrame(padFrame);
});
pad.addEventListener('pointermove', (e) => { gestures.move(e.pointerId, e.clientX, e.clientY, performance.now()); });
pad.addEventListener('pointerup', (e) => {
  flushMotion();
  for (const ev of gestures.up(e.pointerId, performance.now())) { vibrate(); send(ev); }
  if (!gestures.pointers.size) pad.classList.remove('active');
});
pad.addEventListener('pointercancel', () => {
  for (const ev of gestures.cancel()) send(ev);
  pad.classList.remove('active');
});
pad.addEventListener('contextmenu', (e) => e.preventDefault());

// ---- Type -----------------------------------------------------------------------------------------------

const text = $('text');
let composing = false;

// Live typing: whatever changed since the last send (see PhoneLogic.diff). While the phone's
// keyboard composes (Japanese, Chinese, some autocorrect), wait for it to finish.
function syncText() {
  const now = text.value;
  for (const m of PhoneLogic.typeMessages(state.sentText, now)) send(m);
  state.sentText = now;
}
text.addEventListener('compositionstart', () => { composing = true; });
text.addEventListener('compositionend', () => { composing = false; syncText(); });
text.addEventListener('input', (e) => { if (!composing && !e.isComposing) syncText(); });
text.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); pressEnter(); }
});

// After Enter or Tab the TV's text is done with (sent, or another field): the phone's field starts over.
function startOver() { text.value = ''; state.sentText = ''; }
function pressEnter() { syncText(); key('enter'); startOver(); }

pressable($('enter'), pressEnter);
pressable($('delete'), () => {
  if (text.value) {
    const g = PhoneLogic.graphemes(text.value);
    g.pop();
    text.value = g.join('');
    syncText();
  } else key('backspace');
}, { repeat: true });
pressable($('clear'), () => { text.value = ''; syncText(); });
pressable($('tab'), () => { syncText(); key('tab'); startOver(); });
pressable($('shift-tab'), () => { syncText(); key('shiftTab'); startOver(); });
$('kbd-space').addEventListener('click', () => text.focus());

$('link-form').addEventListener('submit', (e) => {
  e.preventDefault();
  const url = $('link').value.trim();
  if (!url) { $('link').focus(); return; }
  send({ t: 'open', url: url.slice(0, 2048) });
  $('link').value = '';
  $('link').blur();
});

// ---- Playing ------------------------------------------------------------------------------------------

const media = (a, extra) => send(Object.assign({ t: 'media', a }, extra || {}));
pressable($('playpause'), () => {
  media('toggle');
  if (state.box.media) { state.box.media.playing = !state.box.media.playing; state.box.media.position = mediaPosition(state.box.media); state.receivedAt = Date.now(); renderPlaying(); }
});
pressable($('back10'), () => media('back10'));
pressable($('fwd10'), () => media('fwd10'));
pressable($('prev'), () => media('prev'));
pressable($('next'), () => media('next'));
$('seek').addEventListener('input', () => { busyUntil.seek = Date.now() + 1500; $('pos').textContent = PhoneLogic.clock(Number($('seek').value)); });
$('seek').addEventListener('change', () => { media('seek', { pos: Number($('seek').value) }); busyUntil.seek = Date.now() + 1500; });

const TIMER = [
  { minutes: 15, label: '15 min' }, { minutes: 30, label: '30 min' }, { minutes: 45, label: '45 min' },
  { minutes: 60, label: '1 hour' }, { minutes: 90, label: '1 h 30' }, { minutes: 120, label: '2 hours' },
  { minutes: 'video', label: 'When this video ends', full: true }, { minutes: 0, label: 'Off', full: true },
];
$('timer-button').addEventListener('click', () => {
  const t = state.box.timer;
  openSheet('Sleep timer', t ? timerText(t) + '.' : 'The box and the TV go to sleep when it runs out. A warning comes 1 minute before.',
    TIMER.map((o) => ({
      label: o.label, full: o.full,
      on: t ? (o.minutes === 'video' ? t.endsAt === 'video' : t.label === o.label) : o.minutes === 0,
      run: () => send({ t: 'timer', minutes: o.minutes }),
    })));
});

// ---- Sheets, banner, toast ------------------------------------------------------------------------------

let sheetReturn = null;
function openSheet(title, textContent, options) {
  $('sheet-title').textContent = title;
  $('sheet-text').textContent = textContent || '';
  const box = $('sheet-options');
  box.innerHTML = '';
  for (const o of options) {
    const b = document.createElement('button');
    b.type = 'button';
    b.textContent = o.label;
    b.className = (o.primary ? 'primary ' : '') + (o.full ? 'full ' : '') + (o.on ? 'on' : '');
    b.addEventListener('click', () => { closeSheet(); o.run(); });
    box.appendChild(b);
  }
  sheetReturn = document.activeElement;
  $('sheet').hidden = false;
  (box.querySelector('button') || $('sheet-cancel')).focus();
}
function closeSheet() {
  $('sheet').hidden = true;
  if (sheetReturn && sheetReturn.focus) sheetReturn.focus();
}
$('sheet-cancel').addEventListener('click', closeSheet);
$('sheet').addEventListener('click', (e) => { if (e.target === $('sheet')) closeSheet(); });
addEventListener('keydown', (e) => { if (e.key === 'Escape' && !$('sheet').hidden) closeSheet(); });

let bannerTimer = 0;
var timerBanner = false;   // the banner shows the sleep timer's warning (var: applyState may run first)
function showBanner(textContent, actionLabel, action) {
  $('banner-text').textContent = textContent;
  const b = $('banner-action');
  b.hidden = !actionLabel;
  b.textContent = actionLabel || '';
  b.onclick = () => { action(); $('banner').hidden = true; };
  $('banner').hidden = false;
  vibrate(40);
  clearTimeout(bannerTimer);
  bannerTimer = setTimeout(() => { $('banner').hidden = true; }, 60000);
}

let toastTimer = 0;
function toast(textContent, kind) {
  const el = $('toast');
  el.textContent = textContent;
  el.className = 'toast' + (kind === 'warn' ? ' warn' : '');
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 3500);
}

// ---- Pairing -----------------------------------------------------------------------------------------------

async function post(path, body) {
  try {
    const res = await fetch(path, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      credentials: 'same-origin', cache: 'no-store',
    });
    let data = {};
    try { data = await res.json(); } catch (e) { /* no body */ }
    return { status: res.status, data };
  } catch (e) {
    return { status: 0, data: {} };
  }
}

function showPairing() {
  state.conn = 'pairing';
  $('pair').hidden = false;
  $('pair-start').hidden = false;
  $('pair-form').hidden = true;
  $('pair-error').textContent = '';
  renderStatus();
}

function hidePairing() { $('pair').hidden = true; }

const later = (s) => s >= 120 ? `${Math.ceil(s / 60)} minutes` : `${s} seconds`;

function pairError(res) {
  switch (res.data.error) {
    case 'wrong': return `That code didn’t match. ${res.data.left} ${res.data.left === 1 ? 'try' : 'tries'} left.`;
    case 'locked': return `Too many wrong codes. Try again in ${later(res.data.retry || 60)}.`;
    case 'wait': return `A code was just shown and not used. Try again in ${later(res.data.retry || 30)}.`;
    case 'expired': return 'The TV isn’t showing that code any more. Ask for a new one.';
    case 'asleep': return 'The TV box is asleep. Wake it with the controller, then try again.';
    default: return res.status === 0 ? 'Can’t reach the TV box.' : 'That didn’t work. Try again.';
  }
}

$('pair-start').addEventListener('click', async () => {
  const res = await post('/api/pair/start', {});
  if (res.status !== 200) { $('pair-error').textContent = pairError(res); return; }
  // Codes were switched off on the TV meanwhile: no code needed, connect.
  if (!res.data.seconds) { state.conn = 'connecting'; hidePairing(); connect(); return; }
  $('pair-error').textContent = '';
  $('pair-start').hidden = true;
  $('pair-form').hidden = false;
  $('pair-code').value = '';
  $('pair-code').focus();
});

async function submitCode() {
  const code = $('pair-code').value.replace(/\D/g, '');
  if (code.length !== 4) return;
  const res = await post('/api/pair', { code });
  if (res.status === 200) { state.conn = 'connecting'; hidePairing(); toast('Paired'); connect(); return; }
  $('pair-error').textContent = pairError(res);
  $('pair-code').value = '';
  if (res.data.error === 'expired' || res.data.error === 'locked') { $('pair-form').hidden = true; $('pair-start').hidden = false; }
}
$('pair-form').addEventListener('submit', (e) => { e.preventDefault(); submitCode(); });
$('pair-code').addEventListener('input', () => { if ($('pair-code').value.replace(/\D/g, '').length === 4) submitCode(); });

// ---- Send to TV from other apps ---------------------------------------------------------------------------

// Android's Share target (/share?url=...&text=...): the link in what was shared, if any.
function sharedLink() {
  for (const name of ['url', 'text', 'title']) {
    const m = /https?:\/\/[^\s<>"]+/i.exec(params.get(name) || '');
    if (m) return m[0].replace(/[.,;:!?)\]'"]+$/, '');
  }
  return null;
}

// undefined: nothing shared; null: shared, but no link in it.
let pendingShare = location.pathname === '/share' ? sharedLink() : undefined;

// The Share sheet's POST comes back as /share?url=<link> (the box answers it with a redirect).
// When the box also hands back that same link from a ticket (this phone's own Share sheet), it
// plays at once; otherwise (another browser, an old ticket, a link some message or page made)
// the page asks first. Only a share with no link at all says so.
function handleShare(ticketLink) {
  const url = pendingShare || null;
  pendingShare = undefined;
  if (!demo) history.replaceState(null, '', '/');
  if (!url) { toast('No link in what was shared', 'warn'); return; }
  const go = () => { send({ t: 'open', url, share: true }); toast('Sent to the TV'); };
  if (ticketLink && ticketLink === url) { go(); return; }
  openSheet('Play this on the TV?', url, [{ label: 'Play on the TV', primary: true, full: true, run: go }]);
}

// "AB:CD:..." as 4 lines of 8 bytes, as easy to compare as Android's own display.
function groupFingerprint(fp) {
  const bytes = String(fp).split(':');
  const lines = [];
  for (let i = 0; i < bytes.length; i += 8) lines.push(bytes.slice(i, i + 8).join(':'));
  return lines.join('\n');
}

const isIphone = /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.userAgent.includes('Macintosh') && navigator.maxTouchPoints > 1);

function openSend() {
  // This phone's part first.
  $('send-iphone').classList.toggle('first', isIphone);
  $('send-android').classList.toggle('first', !isIphone);
  // The secure remote at the same name (the IP address when tv.local does not answer on this phone).
  $('secure-open').href = `https://${location.hostname}/`;
  if (location.protocol === 'https:') $('secure-open').textContent = 'This is the secure remote';
  $('send').hidden = false;
  $('send').scrollTop = 0;
}
function closeSend() {
  $('send').hidden = true;
  if (!demo && location.pathname === '/send') history.replaceState(null, '', '/');
}
$('send-open').addEventListener('click', openSend);
$('send-close').addEventListener('click', closeSend);
$('shortcut-make').addEventListener('click', () => send({ t: 'shortcutKey' }));
$('retry').addEventListener('click', () => { failures = 0; $('lost').hidden = true; connect(); });

// Copy: the clipboard API needs HTTPS; over plain HTTP, the old way (select, copy).
for (const b of document.querySelectorAll('[data-copy]')) {
  b.addEventListener('click', async () => {
    const input = $(b.dataset.copy);
    if (!input.value) return;
    try { await navigator.clipboard.writeText(input.value); }
    catch (e) { input.select(); input.setSelectionRange(0, input.value.length); document.execCommand('copy'); }
    toast('Copied');
  });
}

// ---- Start ---------------------------------------------------------------------------------------------------

const onIpAddress = () => /^\d{1,3}(\.\d{1,3}){3}$/.test(location.hostname) || location.hostname.startsWith('[');
const isLoopback = () => ['127.0.0.1', 'localhost', '[::1]'].includes(location.hostname);

// Opened on the box's IP address (the QR code): if this phone can reach tv.local, move there,
// so the Home Screen app keeps a name that survives the router handing the box a new address.
function probeTvLocal() {
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve(false), 1500);
    fetch(`${location.protocol}//tv.local${location.port ? ':' + location.port : ''}/api/hello`, { mode: 'no-cors', cache: 'no-store' })
      .then(() => { clearTimeout(timer); resolve(true); }, () => { clearTimeout(timer); resolve(false); });
  });
}

async function boot() {
  render();
  if (demo) { runDemo(demo); return; }
  const oneTimeKey = params.get('k');
  if (onIpAddress() && !isLoopback() && await probeTvLocal()) {
    location.replace(`${location.protocol}//tv.local${location.port ? ':' + location.port : ''}${location.pathname}${location.search}`);
    return;
  }
  if (oneTimeKey) {
    // The QR code in Settings › Phone remote: pairs this phone at once (whoever scanned it is at the TV).
    const rest = new URLSearchParams(location.search);
    rest.delete('k');
    history.replaceState(null, '', location.pathname + (rest.toString() ? '?' + rest : ''));
    const res = await post('/api/pair', { key: oneTimeKey });
    if (res.status === 200) toast('Paired');
  }
  if (location.pathname === '/send') openSend();
  connect();
}

// ---- Demo (screenshots) and self-test --------------------------------------------------------------------

function runDemo(view) {
  // &frame=390x844: the app at a phone's size inside a larger window (headless browsers keep
  // windows at least 540 px wide); the overlays stay inside it too.
  const frame = /^(\d{3,4})x(\d{3,4})$/.exec(params.get('frame') || '');
  if (frame) Object.assign($('app').style, { width: frame[1] + 'px', height: frame[2] + 'px', transform: 'translateZ(0)', overflow: 'hidden' });
  state.conn = 'open';
  const box = { standby: false, volume: 62, muted: false, brightness: 100, timer: null, front: 'app', app: 'Jellyfin', canType: true,
    media: { app: 'Jellyfin', title: 'Episode title', subtitle: 'Show · S2 E3', playing: true, position: 1390, duration: 3062, art: 0, canSeek: true, canNext: true, canPrevious: true } };
  state.box = box;
  state.pad = view === 'arrows' ? 'arrows' : 'touchpad';
  state.tab = { type: 'type', playing: 'playing', timer: 'playing' }[view] || 'remote';
  if (view === 'playing' || view === 'timer') box.timer = { label: '30 min', endsAt: Date.now() + 24 * 60000, left: 24 * 60 };
  if (view === 'type') text.value = 'severance';
  if (view === 'asleep') box.standby = true;
  state.receivedAt = Date.now();
  render();
  if (view === 'pair') showPairing();
  if (view === 'sleep') $('power').click();
  if (view === 'timer') $('timer-button').click();
  if (view === 'send' || view === 'sendkey') {
    onBox({ t: 'hello', v: PROTOCOL, paired: true, state: box, ca: '3A:9F:12:C4:7E:05:B8:61:D2:4A:90:3C:E7:18:6B:F5:21:8D:C9:47:0E:B3:5A:96:F1:2C:84:7D:63:E0:1B:A8' });
    openSend();
  }
  if (view === 'sendkey') onBox({ t: 'shortcutKey', token: 'demo-Qm9vc3RlZC1kZW1vLWtleS1ub3QtcmVhbA', url: 'http://tv.local/api/open' });
  if (view === 'share') { pendingShare = 'https://vimeo.com/76979871'; handleShare(false); }
  if (view === 'lost') $('lost').hidden = false;
}

boot();
