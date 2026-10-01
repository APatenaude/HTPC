'use strict';
// Power, the sleep timer, closing an app, and the shared question (ask).
// Part of the page's script: app.js and app\*.js, loaded in index.html's order, share their globals.

const POWER = [
  { id: 'sleep', glyph: 'moon', label: 'Sleep', caption: 'Hold Home on the controller to wake' },
  { id: 'timer', glyph: 'timer', label: 'Sleep timer', caption: 'Count down, then sleep' },
  { id: 'restart', glyph: 'restart', label: 'Restart', caption: '' },
  { id: 'shutdown', glyph: 'power', label: 'Shut down', caption: '' },
  { id: 'desktop', glyph: 'desktop', label: 'Desktop mode', caption: 'Normal Windows desktop, for maintenance' }
];
// While the Windows desktop is up (desktop mode, state.desktop from the host), its card leads back.
const BACK_TO_TV = { id: 'tv', glyph: 'tv', label: 'Back to TV', caption: 'Close the Windows desktop and taskbar' };
// ... and one to the desktop as it is (Explorer stays up), for when Home took you to the dashboard.
const BACK_TO_DESKTOP = { id: 'desktop', glyph: 'desktop', label: 'Desktop', caption: 'Back to the desktop, still open' };

function renderPower() {
  POWER[0].caption = SLEEP_MODES[state.prefs.sleepMode].wake;
  // In place, as the other screens a host push or the clock redraws (patchHtml).
  patchHtml($('power-cards'), POWER.flatMap((p) => (p.id === 'desktop' && state.desktop ? [BACK_TO_DESKTOP, BACK_TO_TV] : [p])).map((p) =>
    `<div class="card" data-nav data-id="${p.id}" data-act="${p.id === 'timer' ? 'view' : 'power-action'}" data-arg="${p.id === 'timer' ? 'timer' : p.id}">` +
      `${icon(p.glyph, 72, 1.5)}<span class="label">${p.label}</span><span class="caption">${p.caption}</span></div>`).join(''));
  patchHtml($('power-note'), '');
  patchHtml($('power-hints'), hints([['A', 'Select'], ['B', 'Cancel']]));
}

const TIMER = [
  { minutes: 15, label: '15 min' }, { minutes: 30, label: '30 min' }, { minutes: 45, label: '45 min' },
  { minutes: 60, label: '1 hour' }, { minutes: 90, label: '1 h 30' }, { minutes: 120, label: '2 hours' },
  { minutes: 'video', label: 'This video ends', sub: 'When playback stops', small: true }, { minutes: 0, label: 'Off' }
];

function renderTimer() {
  patchHtml($('timer-icon'), icon('timer', 56));
  const picked = state.timer ? state.timer.label : null;
  patchHtml($('timer-grid'), TIMER.map((o, i) =>
    `<div class="opt${o.label === picked ? ' picked' : ''}" data-nav data-id="t${i}" data-act="timer" data-arg="${i}">` +
      `<span class="label${o.small ? ' small' : ''}">${o.label}</span><span class="sub">${o.sub || ''}</span></div>`).join(''));
  const status = $('timer-status');
  patchHtml(status, icon('moon', 28) + esc(state.timer ? `${timerText()}. It shows in the top bar.` : 'No timer set'));
  status.classList.toggle('on', !!state.timer);
  patchHtml($('timer-hints'), hints([['A', 'Set'], ['B', 'Back']]));
}

function renderConfirm() {
  const c = state.confirm;
  patchHtml($('confirm-box'),
    `<h2>Close ${esc(c.name)}?</h2><p>It stops, and anything playing in it ends.</p>` +
    '<div class="buttons">' +
      `<div class="button" data-nav data-id="confirm-close" data-act="confirm-close">Close</div>` +
      '<div class="button" data-nav data-id="confirm-cancel" data-act="cancel">Cancel</div>' +
    '</div>' +
    `<div class="hints" style="padding:0;height:64px">${hints([['A', 'Select'], ['B', 'Cancel']])}</div>`);
}

function setTimer(o) {
  state.timer = o.minutes === 0 ? null : { label: o.label, endsAt: o.minutes === 'video' ? 'video' : Date.now() + o.minutes * 60000 };
  send({ type: 'timer', minutes: o.minutes });
  render();
}

// +15 min on the running timer (the Home menu's row, the top bar's pill: A), as the phone and the
// last minute's card do it; X there ends it. Over a "when this video ends" timer it becomes a 15-minute countdown (SleepTimer.Extend).
function extendTimer() {
  if (!state.timer) return;
  const left = state.timer.endsAt === 'video' ? 0 : Math.max(0, state.timer.endsAt - Date.now());
  state.timer = { label: '+15 min', endsAt: Date.now() + left + 15 * 60000 };
  send({ type: 'timer.extend' });
  render();
}

function cancelTimer() {
  const inMenu = state.view === 'menu';
  setTimer({ minutes: 0 });
  // Its row or pill is gone: the focus goes to the control next to where it was.
  const next = document.querySelector(inMenu ? '#menu [data-id="volume"]' : '#home [data-id="power"]');
  if (next) setFocus(next);
}

onAction('timer-extend', () => extendTimer());
onAction('timer-cancel', () => cancelTimer());

// Settings › Sleep timer, left/right: Off, 15 min ... 2 hours, This video ends, round again.
function stepTimer(step) {
  const order = [TIMER.length - 1, ...TIMER.keys()].slice(0, TIMER.length);   // Off first
  const now = order.findIndex((i) => (state.timer ? TIMER[i].label === state.timer.label : TIMER[i].minutes === 0));
  setTimer(TIMER[order[(Math.max(now, 0) + step + order.length) % order.length]]);
}

// The shared dialog: A on the first button runs onYes; B or Cancel closes it. Its notes (a
// release's) are in a box of their own under the text, which scrolls when they are longer than
// its room: the buttons are side by side, so Up and Down scroll it, the focus staying on them.
let asking = null;
let askNew = false;       // a question just asked: its notes start at the top
let askScrolls = false;   // its notes are longer than their box: the hints say Up/Down scroll them
// Always on Cancel, never where the last question's focus was left: after one Yes, the next
// question (Shut down) would otherwise open on Yes.
function ask(q) { asking = q; askNew = true; state.memory.ask = null; go('ask'); }
function askNotes() { return $('ask').querySelector('.dialog > .notes'); }
function askHints() { return [...(askScrolls ? [['↑↓', 'Scroll']] : []), ['A', 'Select'], ['B', 'Cancel']]; }
addView('ask', {
  overlay: true,
  render() {
    const q = asking || {};
    // In place (patchHtml): redrawn by the clock and host pushes, it popped in again each time
    // (and the notes kept where they were scrolled to). data-scroll: sounds.js hears them scroll.
    patchHtml($('ask'), '<div class="dialog">' +
      `<h2>${esc(q.title || '')}</h2>${q.text ? `<p>${esc(q.text)}</p>` : ''}` +
      (q.notes ? `<div class="notes" data-scroll>${q.notes}</div>` : '') +
      '<div class="buttons">' +
        `<div class="button" data-nav data-id="ask-yes" data-act="ask-yes">${esc(q.yes || 'OK')}</div>` +
        '<div class="button" data-nav data-id="ask-no" data-act="cancel">Cancel</div>' +
      '</div>' +
      `<div class="hints" style="padding:0;height:64px">${hints(askHints())}</div></div>`);
  },
  // On screen, laid out: whether the notes are longer than their box (only then do the hints say
  // Up/Down scroll them), and their ends fade where there is more (listEdges).
  layout() {
    const box = askNotes();
    if (box && askNew) box.scrollTop = 0;
    askNew = false;
    const scrolls = !!box && box.scrollHeight > box.clientHeight + 2;
    if (box) listEdges(box);
    if (scrolls === askScrolls) return;
    askScrolls = scrolls;
    patchHtml($('ask').querySelector('.dialog > .hints'), hints(askHints()));
  },
  // Up and Down scroll the notes three lines at a time (held, they repeat), and stop at their
  // ends; with nothing to scroll they move the focus as anywhere (nothing is above or below it).
  press(button) {
    const box = askNotes();
    if (!box || !askScrolls || (button !== 'up' && button !== 'down')) return false;
    box.scrollTop += (button === 'down' ? 3 : -3) * parseFloat(getComputedStyle(box).lineHeight);
    listEdges(box);
    return true;
  },
  focus: (list) => list.find((e) => e.dataset.id === 'ask-no'),
});
onAction('ask-yes', () => { const q = asking; back(); if (q && q.onYes) q.onYes(); });
