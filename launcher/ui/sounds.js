'use strict';
// Interface sounds (Settings › Sound › Interface sounds: Off, Low, Medium): short, soft sounds
// made here with the Web Audio API, from oscillators and noise under envelopes (no audio files).
// Loaded after app.js. What a press did picks its sound: app.js's press() calls soundsHear
// first, and the page is looked at again once the press is done (the focus moved, nothing did
// at a list's end, a toggle flipped, a view opened...). Also the Home menu coming up over an
// app, and an alert's card arriving.
// None while the launcher is hidden or blank (an app in front, standby) or a text field has
// the focus (someone typing), and none in setup (setup.html does not load this).
// The controller's presses reach the page as host messages, not user gestures: the launcher's
// WebView2 starts with --autoplay-policy=no-user-gesture-required (MainForm.InitWebView), so
// the AudioContext runs from the first press. Without a host (a plain browser) it waits for a
// real key press, so the headless self-test never opens the audio device.

const SOUND_LEVELS = { off: 0, low: 0.4, medium: 1 };   // Low is 8 dB under Medium
CHOICES.interfaceSounds = [['off', 'Off'], ['low', 'Low'], ['medium', 'Medium']];
if (state.prefs.interfaceSounds === undefined) state.prefs.interfaceSounds = 'low';   // until the host's init (a plain browser: Low)

const sounds = {
  ctx: null,             // the AudioContext, from the first sound on (suspended when unused)
  rest: 0,               // timer: suspends it after a while without sounds
  press: null,           // the press being heard: { button, was }
  pendingOpen: 0,        // the Home menu came up over an app while the page was still hidden: until then
  last: {},              // rate limits: when each kind last played (performance.now())
  lastMovePress: -1e9,
  heard: [],             // what played, newest last: { name, gain } (the self-test reads it)
};

// ---- The sounds --------------------------------------------------------------------------------
// Each draws into out from time t; peaks are for Medium (Low scales them all down).

// One partial: an oscillator (gliding from freq to `to`), a quick attack, an exponential fall.
function tone(ctx, out, t, { type = 'sine', freq, to = freq, glide = 0.05, peak, attack = 0.002, decay }) {
  const osc = ctx.createOscillator(), env = ctx.createGain();
  osc.type = type;
  osc.frequency.setValueAtTime(freq, t);
  if (to !== freq) osc.frequency.exponentialRampToValueAtTime(to, t + glide);
  env.gain.setValueAtTime(0, t);
  env.gain.linearRampToValueAtTime(peak, t + attack);
  env.gain.exponentialRampToValueAtTime(0.0001, t + attack + decay);
  osc.connect(env);
  env.connect(out);
  osc.start(t);
  osc.stop(t + attack + decay + 0.01);
}

// A breath of filtered noise: a click's edge, a swoosh, a thud's body.
function hiss(ctx, out, t, { filter = 'bandpass', freq, to = freq, q = 1, peak, attack = 0.001, dur }) {
  const src = ctx.createBufferSource(), f = ctx.createBiquadFilter(), env = ctx.createGain();
  src.buffer = noiseBuffer(ctx);
  f.type = filter;
  f.Q.value = q;
  f.frequency.setValueAtTime(freq, t);
  if (to !== freq) f.frequency.exponentialRampToValueAtTime(to, t + dur);
  env.gain.setValueAtTime(0, t);
  env.gain.linearRampToValueAtTime(peak, t + attack);
  env.gain.exponentialRampToValueAtTime(0.0001, t + dur);
  src.connect(f);
  f.connect(env);
  env.connect(out);
  src.start(t);
  src.stop(t + dur + 0.01);
}

// Half a second of white noise, the same each time (a fixed seed), made once per context.
const noiseBuffers = new WeakMap();
function noiseBuffer(ctx) {
  let b = noiseBuffers.get(ctx);
  if (b) return b;
  b = ctx.createBuffer(1, Math.round(ctx.sampleRate / 2), ctx.sampleRate);
  const d = b.getChannelData(0);
  for (let i = 0, s = 1; i < d.length; i++) { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; d[i] = s / 2147483648 - 1; }
  noiseBuffers.set(ctx, b);
  return b;
}

const SOUNDS = {
  // The focus moving: a very soft, short tick (lower while a direction is held).
  move(ctx, out, t, o) {
    tone(ctx, out, t, { freq: o.repeat ? 1650 : 1850, peak: 0.05, attack: 0.001, decay: 0.03 });
    hiss(ctx, out, t, { freq: 5200, q: 0.8, peak: 0.04, dur: 0.008 });
  },
  // A: a warm pluck, its pitch settling at once, with a little of its octave.
  select(ctx, out, t) {
    tone(ctx, out, t, { type: 'triangle', freq: 800, to: 740, glide: 0.02, peak: 0.16, decay: 0.14 });
    tone(ctx, out, t, { freq: 1480, peak: 0.05, decay: 0.07 });
    hiss(ctx, out, t, { freq: 3000, peak: 0.05, dur: 0.006 });
  },
  // B: lower and softer, falling a little.
  back(ctx, out, t) {
    tone(ctx, out, t, { type: 'triangle', freq: 520, to: 440, glide: 0.06, peak: 0.13, attack: 0.003, decay: 0.12 });
    tone(ctx, out, t, { freq: 880, to: 740, glide: 0.06, peak: 0.03, attack: 0.003, decay: 0.06 });
  },
  // An app opening, the Home menu over an app: two notes rising (D, A), a breath rising under them.
  open(ctx, out, t) {
    tone(ctx, out, t, { freq: 587.3, peak: 0.11, attack: 0.008, decay: 0.26 });
    tone(ctx, out, t + 0.075, { freq: 880, peak: 0.13, attack: 0.008, decay: 0.32 });
    hiss(ctx, out, t, { freq: 700, to: 2600, q: 0.9, peak: 0.06, attack: 0.08, dur: 0.24 });
  },
  // Closing, cancelling, back to the app: the same, falling and softer.
  close(ctx, out, t) {
    tone(ctx, out, t, { freq: 880, peak: 0.09, attack: 0.006, decay: 0.2 });
    tone(ctx, out, t + 0.07, { freq: 587.3, peak: 0.1, attack: 0.006, decay: 0.26 });
    hiss(ctx, out, t, { freq: 2400, to: 600, q: 0.9, peak: 0.05, attack: 0.05, dur: 0.2 });
  },
  // Nowhere further (a list's end): a dull low thud, with enough above 150 Hz for a TV's speakers.
  bump(ctx, out, t) {
    tone(ctx, out, t, { freq: 160, to: 85, glide: 0.08, peak: 0.17, decay: 0.1 });
    tone(ctx, out, t, { type: 'triangle', freq: 320, to: 180, glide: 0.06, peak: 0.05, decay: 0.06 });
    hiss(ctx, out, t, { filter: 'lowpass', freq: 600, q: 0.7, peak: 0.1, dur: 0.03 });
  },
  // A toggle: two ticks going up (on) or down (off).
  on(ctx, out, t) {
    tone(ctx, out, t, { freq: 1320, peak: 0.08, attack: 0.001, decay: 0.045 });
    tone(ctx, out, t + 0.055, { freq: 1760, peak: 0.09, attack: 0.001, decay: 0.07 });
  },
  off(ctx, out, t) {
    tone(ctx, out, t, { freq: 1760, peak: 0.08, attack: 0.001, decay: 0.045 });
    tone(ctx, out, t + 0.055, { freq: 1174.7, peak: 0.09, attack: 0.001, decay: 0.07 });
  },
  // An alert's card arriving: a soft two-note bell (C, G), each with a faint inharmonic partial.
  notice(ctx, out, t) {
    for (const [at, freq, peak] of [[0, 1046.5, 0.09], [0.12, 1568, 0.07]]) {
      tone(ctx, out, t + at, { freq, peak, attack: 0.004, decay: 0.7 });
      tone(ctx, out, t + at, { freq: freq * 2.76, peak: peak * 0.15, decay: 0.2 });
    }
  },
};

// ---- Playing -----------------------------------------------------------------------------------

function soundLevel() { return SOUND_LEVELS[state.prefs.interfaceSounds] ?? SOUND_LEVELS.low; }

// Only for someone looking at the launcher: not hidden or blank (an app in front, standby), and
// not while a text field has the focus (typing).
function soundsAllowed() {
  return soundLevel() > 0 && !document.hidden && !$('stage').classList.contains('blank') && !textField();
}

function soundContext() {
  const c = sounds.ctx;
  if (c) { if (c.state === 'suspended') c.resume().catch(() => {}); return c; }
  if (!host && !(navigator.userActivation && navigator.userActivation.hasBeenActive)) return null;
  try { sounds.ctx = new AudioContext({ latencyHint: 'interactive' }); } catch (e) { return null; }
  return sounds.ctx;
}

// The context is suspended when no sound has played for a while (or the launcher went away):
// no audio stream open under the apps; the next sound resumes it.
function soundsRest(after) {
  clearTimeout(sounds.rest);
  sounds.rest = setTimeout(() => { const c = sounds.ctx; if (c && c.state === 'running') c.suspend().catch(() => {}); }, after);
}

function soundPlay(name, gain = 1, o = {}) {
  if (!gain || !soundsAllowed()) return;
  sounds.heard.push({ name, gain });
  if (sounds.heard.length > 40) sounds.heard.shift();
  const ctx = soundContext();
  if (!ctx) return;
  const out = ctx.createGain();
  out.gain.value = soundLevel() * gain;
  out.connect(ctx.destination);
  SOUNDS[name](ctx, out, ctx.currentTime + 0.005, o);
  setTimeout(() => out.disconnect(), 1500);
  soundsRest(30000);
}

// Holding a direction repeats it every 110 ms (ControllerService). A move within 200 ms of the
// one before is a repeat: half as loud, and at least 180 ms after the last tick (so every other
// repeat); other moves at least 60 ms apart. The gain, 0 for none.
function soundMoveGain(now) {
  const repeat = now - sounds.lastMovePress < 200;
  sounds.lastMovePress = now;
  if (now - (sounds.last.move ?? -1e9) < (repeat ? 180 : 60)) return 0;
  sounds.last.move = now;
  return repeat ? 0.5 : 1;
}

// A list's end once per 350 ms (a direction held there repeats), a card once per 1.5 s.
const SOUND_GAPS = { bump: 350, notice: 1500 };

function soundsDo(name, now = performance.now()) {
  if (name === 'move') {
    const gain = soundMoveGain(now);
    soundPlay(name, gain, { repeat: gain < 1 });
    return;
  }
  if (SOUND_GAPS[name]) {
    if (now - (sounds.last[name] ?? -1e9) < SOUND_GAPS[name]) return;
    sounds.last[name] = now;
  }
  soundPlay(name);
}

// ---- What a press did ----------------------------------------------------------------------------

function soundsLook() {
  const el = focusedEl();
  const toggle = el && el.querySelector('.toggle');
  return {
    view: state.view, depth: state.stack.length, el, id: el ? el.dataset.id : null,
    // A value stepped, a toggle flipped, a tile moved: the focused element itself changed.
    sig: el ? el.style.order + '|' + el.innerHTML : '',
    editing, act: el ? el.dataset.act : null, noA: !!el && el.dataset.noa !== undefined, toggle: toggle ? toggle.classList.contains('on') : null,
    opening: $('opening').classList.contains('on'),
    // B or Home from here goes back to the app the menu was opened over (the host hides the launcher).
    leaving: state.view !== 'home' && !state.stack.length && !!state.current,
    testing: typeof more === 'object' && more.testing,   // Settings › Controller's button test takes every button
  };
}

function soundFor(button, was, now) {
  if (was.testing) return null;
  const changed = now.view !== was.view || now.depth !== was.depth || now.el !== was.el || now.id !== was.id ||
    now.sig !== was.sig || now.editing !== was.editing;
  if (now.opening && !was.opening) return 'open';   // an app starting (its tile, Reopen)
  switch (button) {
    case 'up': case 'down': case 'left': case 'right': return changed ? 'move' : 'bump';
    case 'lb': case 'rb': return changed ? 'move' : null;
    case 'a':
      if (!was.el || (was.noA && !changed)) return null;   // a row that does nothing (data-noa)
      if (was.act === 'launch' || was.act === 'switch') return 'open';
      if (was.act === 'cancel' || was.act === 'confirm-close') return 'close';
      if (was.toggle !== null && now.id === was.id && now.toggle !== null && now.toggle !== was.toggle) return now.toggle ? 'on' : 'off';
      return 'select';
    case 'b': case 'home': case 'homeHold':
      if (was.leaving && !changed && button !== 'homeHold') return 'close';
      if (!changed) return null;
      return now.depth > was.depth || (was.view === 'home' && now.view !== 'home') ? 'open' : 'back';
    case 'x': return now.view !== was.view ? 'select' : changed ? 'close' : null;   // a dialog; a card or tile gone
    default: return changed ? 'select' : null;   // Start (Tile options), Y, R3...
  }
}

// Called by press() before it does anything; the sound is picked once it is done (the press
// before is finished first, for presses made one after another in the same task).
function soundsHear(button) {
  soundsHeard();
  sounds.press = { button, was: soundsLook() };
  queueMicrotask(soundsHeard);
}

function soundsHeard() {
  const p = sounds.press;
  if (!p) return;
  sounds.press = null;
  const name = soundFor(p.button, p.was, soundsLook());
  if (name) soundsDo(name);
}

// ---- The Home menu over an app, alerts, the launcher going away ----------------------------------

// The host's "show" takes the page out of blank; the window shows a moment later (the page is
// still hidden then), so the sound waits for that, a second at most. Blank again: the launcher
// has gone (an app in front, standby).
let soundsBlank = $('stage').classList.contains('blank');
new MutationObserver(() => {
  const blank = $('stage').classList.contains('blank');
  if (soundsBlank && !blank && state.current && (state.view === 'menu' || state.view === 'power')) {
    if (document.hidden) sounds.pendingOpen = performance.now() + 1000; else soundsDo('open');
  }
  if (blank && !soundsBlank) soundsRest(1500);
  soundsBlank = blank;
}).observe($('stage'), { attributes: true, attributeFilter: ['class'] });

addEventListener('visibilitychange', () => {
  if (document.hidden) { soundsRest(1500); return; }
  if (performance.now() < sounds.pendingOpen) soundsDo('open');
  sounds.pendingOpen = 0;
});

// An alert's card arriving (notices.js adds an element for each new card); not the page's own
// short messages (toast(): "Moved", "Searching for TVs…"), which follow a press that had its sound.
new MutationObserver((changes) => {
  if (changes.some((c) => [...c.addedNodes].some((n) => !(n.dataset && String(n.dataset.key).startsWith('own:'))))) soundsDo('notice');
}).observe($('toasts'), { childList: true });

// ---- Settings › Sound ----------------------------------------------------------------------------

// A choice (A, then left/right: app.js's editPress and changeSetting), sent as the setting interfaceSounds.
function soundsRow() {
  const now = state.prefs.interfaceSounds;
  return settingRow('interfaceSounds', 'Interface sounds', 'Soft clicks as you move and select in the launcher. Never in apps.',
    '<div class="seg">' + CHOICES.interfaceSounds.map(([v, l]) => `<span${v === now ? ' class="on"' : ''}>${l}</span>`).join('') + '</div>');
}
