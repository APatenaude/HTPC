'use strict';
// Presses: the controller (through the host) and the keyboard, and what A does (activate).
// Part of the page's script: app.js and app\*.js, loaded in index.html's order, share their globals.

// "Opening X" covers the page until the host says the app's window is up ('opened', also when
// it did not open). Home or B take it away at once (press), and it never stays past 40 s (the
// host gives up at 30 s, with an alert).
let opening = null;   // { id, timer }
function showOpening(t) {
  const el = $('opening');
  el.innerHTML = `<span class="logo">${appIcon(t, 160, 1.5)}</span>` +
    `<span class="name">Opening ${esc(t.name)}…</span>`;
  el.classList.add('on');
  if (opening) clearTimeout(opening.timer);
  opening = { id: t.id, timer: setTimeout(hideOpening, 40000) };
}
function hideOpening() {
  $('opening').classList.remove('on');
  if (opening) clearTimeout(opening.timer);
  opening = null;
}

function activate(el) {
  if (!el) return;
  const act = el.dataset.act, arg = el.dataset.arg;
  switch (act) {
    case 'launch': {
      const t = state.tiles.find((x) => x.id === arg);
      if (t && !t.running) showOpening(t);
      send({ type: 'launch', id: arg });
      break;
    }
    case 'switch': send({ type: 'switchTo', id: arg }); break;
    case 'home': state.current = null; state.backdrop = null; send({ type: 'home' }); reset('home'); break;
    case 'view': go(arg); break;
    case 'power': go('power'); break;
    case 'power-action':
      if (arg === 'desktop' && state.desktop) send({ type: 'power', action: 'desktop' }); // back to it: Explorer is up, nothing to ask
      else if (arg === 'desktop') ask({ title: 'Switch to the Windows desktop?', yes: 'Desktop mode', onYes: () => send({ type: 'power', action: 'desktop' }),
        text: 'The desktop, taskbar and Start menu open, for maintenance; open apps go down to the taskbar. To come back, press Home, then Back to TV (or the Back to TV icon on the desktop).' });
      // One wrong press of A must not switch the box off: the controller cannot turn it back on.
      else if (arg === 'shutdown') ask({ title: 'Shut down the box?', yes: 'Shut down', onYes: () => send({ type: 'power', action: 'shutdown' }),
        text: 'It turns off completely: the controller can’t turn it back on. Use the box’s power button to start it again.' });
      // Nor close every app (a film half watched) by restarting it.
      else if (arg === 'restart') ask({ title: 'Restart the box?', yes: 'Restart', onYes: () => send({ type: 'power', action: 'restart' }),
        text: 'Apps close and the box starts again; it comes back to the TV screen by itself.' });
      else send({ type: 'power', action: arg });
      break;
    case 'timer': setTimer(TIMER[Number(arg)]); break;
    case 'confirm-close': {
      // Closing can take seconds (an app asked nicely first, then ended): its tile and its menu row
      // say "Closing…" until the host's state no longer lists it running.
      const { id, name } = state.confirm;
      send({ type: 'close', id });
      markClosing(id);
      back();
      toast(`Closing ${name}…`);
      break;
    }
    case 'cancel': back(); break;
    // Settings opens on its section list (restoreFocus), not where the focus was last time.
    case 'settings': state.memory.settings = null; go('settings'); break;
    case 'soon': toast(`${arg} come in a later update`); break;
    default: if (EXT.actions[act]) EXT.actions[act](el, arg);
  }
}

function adjust(el, delta) {
  const key = el.dataset.slider;
  state[key] = Math.max(0, Math.min(100, state[key] + delta));
  send({ type: key, value: state[key] });
  render();
}

// One entry point for the controller (via the host) and the keyboard. held: the controller's A,
// whose release follows ('aUp'; 'aHold' after 0.5 s): see aWaitStart.
function press(button, held) {
  if (button === 'aHold' || button === 'aUp') { aHeldPress(button); return; }
  aWaitDrop();   // any other press first: a waiting A is dropped
  if (held && button === 'a' && aWaitStart()) return;
  timePress(button);
  if (typeof soundsHear === 'function') soundsHear(button);   // interface sounds (sounds.js): what this press does picks one
  // "Opening X" is up: nothing under it takes a press. Home or B take it away (the host then
  // leaves the app behind the launcher when its window comes); Home goes on to the menu.
  if (opening) {
    if (button !== 'home' && button !== 'homeHold' && button !== 'b') return;
    send({ type: 'launchDismissed', id: opening.id });
    hideOpening();
    if (button === 'b') return;
  }
  const el = focusedEl();
  // Moving a tile on the home screen (Tile options > Move): the mover takes every button.
  if (state.moving && EXT.actions['tile-move'] && EXT.actions['tile-move'](el, button)) return;
  // Added views and Settings sections first (their own buttons), then the usual.
  const view = EXT.views[state.view];
  if (view && view.press && view.press(button, el)) return;
  if (state.view === 'settings') {
    if (editPress(button, el)) return;
    // A value row not picked with A: left/right are the focus's, never the section's to change it.
    const idle = el && el.dataset.edit !== undefined && (button === 'left' || button === 'right');
    const section = EXT.sections[state.section];
    if (section && section.press && !(el && el.dataset.section) && !idle && section.press(button, el)) return;
    if (settingsPress(button, el)) return;
  }
  switch (button) {
    case 'up': case 'down': move(button); break;
    case 'left': case 'right':
      if (el && el.dataset.slider) { menuUse(el); adjust(el, button === 'right' ? 5 : -5); }
      else if (el && el.dataset.setting) changeSetting(el.dataset.setting, button === 'right' ? 1 : -1);
      else move(button);
      break;
    case 'a':
      if (el && el.dataset.setting) changeSetting(el.dataset.setting, 1);
      else { if (el && el.classList.contains('quick')) menuUse(el); activate(el); }
      break;
    case 'b': back(); break;
    case 'x': {
      // An alert's row: dismisses it, and nothing else (never on to the close below).
      if (el && el.dataset.alert) { noticeDismiss(el.dataset.alert); break; }
      // An element with an X action of its own (data-x: an app that did not install, library.js).
      if (el && el.dataset.x && EXT.actions[el.dataset.x]) { EXT.actions[el.dataset.x](el, el.dataset.arg); break; }
      // Home screen: the focused tile, if it is running. Menu: the focused app row, else the
      // app the menu was opened over.
      let id = null;
      if (state.view === 'home' && el && el.dataset.arg) id = el.dataset.arg;
      else if (state.view === 'menu') id = (el && el.dataset.close) || state.current;
      else break;
      const t = id && state.tiles.find((x) => x.id === id && x.running);
      if (t) { state.confirm = { id: t.id, name: t.name }; state.memory.confirm = null; go('confirm'); }
      break;
    }
    case 'home':
      if (state.view === 'home') {
        state.current = null; state.backdrop = null;
        // An actionable alert on screen: the menu opens on its row; otherwise as menuOpening says.
        state.memory.menu = noticeHomeFocus() || menuOpening();
        go('menu');
      }
      else back();
      break;
    case 'homeHold': if (state.view !== 'power') go('power'); break;
    case 'r3': { const f = textField(); if (f) openKeyboardFor(f); break; }  // textinput.js
    case 'start':
      // Home screen: options for the focused tile (Move, Rename, Change icon, Remove).
      if (state.view === 'home' && el && el.dataset.act === 'launch' && EXT.actions['tile-options']) EXT.actions['tile-options'](el, el.dataset.arg);
      break;
  }
}

// Hold A on a home tile to move it. The controller's A on an app or site tile waits: let go
// before 0.5 s ('aUp'), it opens the tile; held ('aHold'), the tile goes into move mode
// (library.js). Another press or 3 s drops the wait. The phone's and the keyboard's A have no
// release: they act at once.
let aWait = null;   // { el, timer }
function aWaitStart() {
  const el = focusedEl();
  if (opening || state.moving || state.view !== 'home' || !el || el.dataset.act !== 'launch' || !EXT.actions['tile-hold']) return false;
  aWait = { el, timer: setTimeout(aWaitDrop, 3000) };
  return true;
}
function aWaitDrop() {
  if (aWait) clearTimeout(aWait.timer);
  aWait = null;
}
function aHeldPress(button) {
  const w = aWait;
  aWaitDrop();
  if (w && state.view === 'home' && !opening && !state.moving && focusedEl() === w.el) {
    if (button === 'aUp') { press('a'); return; }
    EXT.actions['tile-hold'](w.el, w.el.dataset.arg);
    if (typeof soundsDo === 'function') soundsDo('select');
    return;
  }
  // Let go in move mode: the mover decides (a move started by this hold drops on it).
  if (button === 'aUp' && state.moving && EXT.actions['tile-move']) {
    EXT.actions['tile-move'](focusedEl(), button);
    if (!state.moving && typeof soundsDo === 'function') soundsDo('select');
  }
}

// How long a press takes on the box itself (4K on its small GPU, not a PC's headless Edge): from
// the press to the frame that shows it (the second animation frame after it: the first frame
// has been drawn then). Over 60 ms it goes to the launcher's log, "Slow press 180 ms in addtile
// (right)"; the host keeps the log from filling up. Not while hidden, nor without a host.
function timePress(button) {
  if (!host || document.hidden) return;
  const asked = performance.now(), view = state.view;
  requestAnimationFrame(() => requestAnimationFrame(() => {
    const ms = Math.round(performance.now() - asked);
    if (ms > 60) send({ type: 'perf', ms, view, button });
  }));
}

const KEYS = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right', Enter: 'a', ' ': 'a',
  Escape: 'b', Backspace: 'b', x: 'x', y: 'y', h: 'home', p: 'homeHold', PageUp: 'lb', PageDown: 'rb', o: 'start' };
addEventListener('keydown', (e) => {
  // Blank (standby, the launcher black in front): a real key press wakes the box.
  if ($('stage').classList.contains('blank')) { e.preventDefault(); send({ type: 'wake' }); return; }
  // A text field has the focus: the key is the field's (Backspace deletes, x types an x), only
  // Enter and Escape still confirm and cancel (textinput.js).
  if (keyGuard(e)) return;
  const b = KEYS[e.key];
  if (!b) return;
  e.preventDefault();
  press(b);
});
