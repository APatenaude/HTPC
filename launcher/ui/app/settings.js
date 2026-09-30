'use strict';
// Settings: its sections (SECTIONS), Sleep & power, the pane's rows and how buttons move in it.
// Part of the page's script: app.js and app\*.js, loaded in index.html's order, share their globals.

const SECTIONS = [
  ['sleep', 'moon', 'Sleep & power'], ['tv', 'tv', 'TV'], ['controller', 'controller', 'Controller'],
  ['phone', 'phone', 'Phone remote'], ['wifi', 'wifi', 'Wi-Fi'], ['bluetooth', 'bluetooth', 'Bluetooth'],
  ['display', 'desktop', 'Display'], ['sound', 'speaker', 'Sound'], ['updates', 'download', 'Updates'],
  ['about', 'info', 'About & Desktop mode']
];

// Values a setting row steps through with left/right (A steps forward).
const CHOICES = {
  idleMinutes: [[15, '15 minutes'], [30, '30 minutes'], [60, '1 hour'], [120, '2 hours'], [0, 'Never']],
  // Only the modes this PC has (the host reports them); screen off always works.
  sleepMode: [['standby', 'Screen off'], ['sleep', 'Sleep'], ['hibernate', 'Hibernate']],
  sleepAfterStandbyHours: [[0, 'Never'], [1, 'After 1 hour'], [3, 'After 3 hours'], [6, 'After 6 hours'], [12, 'After 12 hours']],
  stayAwakeWhilePlaying: [[false, 'Off'], [true, 'On']]
};

// What each sleep mode means, shown under the choice and on the Power screen.
const SLEEP_MODES = {
  standby: { caption: 'The video output and the TV go off; the box stays on, using little power. Hold Home on the controller to wake it.',
             wake: 'Hold Home on the controller to wake' },
  sleep: { caption: 'Windows sleep: less power still. The controller and the phone may not wake it: use the power button or the keyboard.',
           wake: 'Wake with the power button or the keyboard' },
  hibernate: { caption: 'Windows hibernate: almost no power, slower to come back. The controller and the phone can’t wake it: use the power button.',
               wake: 'Wake with the power button' }
};

function availableModes() { return CHOICES.sleepMode.filter(([v]) => v === 'standby' || state.power[v]); }

function choiceLabel(key) {
  const c = CHOICES[key].find(([v]) => v === state.prefs[key]);
  return c ? c[1] : String(state.prefs[key]);
}

function settingRow(key, label, caption, control) {
  // A value (a choice, a stepper) changes with left/right only once A has picked the row
  // (data-edit, editPress below); a toggle just flips with A.
  const edit = control.includes('class="toggle') ? '' : ' data-edit';
  return `<div class="srow" data-nav data-id="set-${key}" data-setting="${key}"${edit}>` +
    `<div class="text"><span class="label">${esc(label)}</span><span class="caption">${esc(caption)}</span></div>${control}</div>`;
}

function stepper(key) {
  return `<div class="value">${icon('chevleft', 28, 2)}${esc(choiceLabel(key))}${icon('chevright', 28, 2)}</div>`;
}

function renderSleepSection() {
  const p = state.prefs;
  return '<header><h1>Sleep &amp; power</h1>' +
      '<p>Sleep from the Power menu, the sleep timer, or when nothing happens for a while.</p></header>' +
    (availableModes().length > 1
      ? settingRow('sleepMode', 'Sleep mode', SLEEP_MODES[p.sleepMode].caption,
          '<div class="seg">' + availableModes().map(([v, l]) => `<span${v === p.sleepMode ? ' class="on"' : ''}>${l}</span>`).join('') + '</div>')
      : '') +
    (p.sleepMode === 'standby' && state.power.sleep
      ? settingRow('sleepAfterStandbyHours', 'Then Windows sleep', 'After this long with the screen off, the box goes into Windows sleep (the controller may not wake it from there)',
          stepper('sleepAfterStandbyHours'))
      : '') +
    settingRow('idleMinutes', 'Sleep after', 'When nothing plays and nobody touches the controller', stepper('idleMinutes')) +
    // The sleep timer is set right here, as a choice (A, then left/right: stepTimer), not on
    // the timer screen of the Home menu.
    '<div class="srow" data-nav data-id="set-timer" data-timer data-edit>' +
      '<div class="text"><span class="label">Sleep timer</span>' +
        `<span class="caption">${esc(state.timer ? `${timerText()}. It shows in the top bar.` : 'A countdown, then the box and the TV sleep. Also in the Home menu.')}</span></div>` +
      `<div class="value">${icon('chevleft', 28, 2)}${esc(state.timer ? state.timer.label : 'Off')}${icon('chevright', 28, 2)}</div></div>` +
    settingRow('stayAwakeWhilePlaying', 'Stay awake while video plays', 'Even if you don’t touch the controller for hours',
      `<div class="toggle${p.stayAwakeWhilePlaying ? ' on' : ''}"><span></span></div>`) +
    '<div class="sbuttons">' +
      '<div class="sbutton" data-nav data-id="set-sleepnow" data-act="power-action" data-arg="sleep">Sleep now</div>' +
    '</div>';
}

function toggle(on) { return `<div class="toggle${on ? ' on' : ''}"><span></span></div>`; }

// Settings › TV: tv.js (a Settings section added through settingsSection).

let shownSection = null;   // the section's content animates in only when the section changes
let editing = null;        // data-id of the Settings row whose value left/right change (editPress)
function renderSettings() {
  const nav = `<nav class="snav"><div class="snav-title">${icon('chevleft', 36, 2)}<span>Settings</span></div>` +
    SECTIONS.map(([id, glyph, label]) =>
      `<div class="sitem${id === state.section ? ' on' : ''}" data-nav data-id="s-${id}" data-section="${id}">${icon(glyph, 32)}${esc(label)}</div>`).join('') +
    '</nav>';
  const title = SECTIONS.find(([id]) => id === state.section)[2];
  const added = EXT.sections[state.section];
  const body = state.section === 'sleep' ? renderSleepSection()
    : added ? added.render()
    : `<header><h1>${esc(title)}</h1><p>This section comes in a later update.</p></header>`;
  const entering = state.section !== shownSection;
  shownSection = state.section;
  const old = $('settings').querySelector('.spane main');
  if (old) {
    // Only what changed is redrawn, in place: the section list (the focused section keeps its
    // ring as the next one shows), and within a section a value that changed, a host push, the
    // clock, with the pane's scroll and the focused row left as they were. Another section's
    // pane is new, animating in.
    patchHtml($('settings').querySelector('.snav'), nav.replace(/^<nav[^>]*>|<\/nav>$/g, ''));
    if (!entering) patchHtml(old, body);
    else {
      const main = document.createElement('main');
      main.className = 'enter';
      main.innerHTML = body;
      old.replaceWith(main);
    }
  } else {
    // The hints follow the focus (settingsFocused).
    $('settings').innerHTML = nav + `<div class="spane"><main${entering ? ' class="enter"' : ''}>${body}</main>` +
      '<footer class="hints" id="settings-hints"></footer></div>';
  }
  for (const r of $('settings').querySelectorAll('.editing')) r.classList.remove('editing');
  const row = editing && $('settings').querySelector(`.spane [data-id="${CSS.escape(editing)}"]`);
  if (row) row.classList.add('editing'); else editing = null;
}

function changeSetting(key, step) {
  if (key.startsWith('phone.') && typeof phoneSetting === 'function') { phoneSetting(key); return; }   // phone-settings.js
  const list = key === 'sleepMode' ? availableModes() : CHOICES[key];
  const i = list.findIndex(([v]) => v === state.prefs[key]);
  const next = list[(Math.max(i, 0) + step + list.length) % list.length][0];
  state.prefs[key] = next;
  send({ type: 'setting', key, value: next });
  render();
}

// Settings, two columns: the section list and the section's pane. Up/down stay in their column
// and stop at its ends (up/down in the list changes section). In the pane, left and right go to
// what is beside the focus; left with nothing there, B or LB go back to the list. Right, A or RB
// from the list go into the pane.
function settingsPress(button, el) {
  const inNav = el && el.dataset.section;
  const navItem = () => $('settings').querySelector(`[data-section="${state.section}"]`);
  const pane = () => [...$('settings').querySelectorAll('.spane [data-nav]')];
  const firstOption = () => pane()[0];
  const focusTo = (to) => { if (to) setFocus(to); return true; };
  switch (button) {
    case 'lb': return focusTo(navItem());
    case 'rb': return focusTo(firstOption());
    case 'up': case 'down': {
      if (!el) return false;
      const column = inNav ? [...$('settings').querySelectorAll('.snav [data-nav]')] : pane();
      return focusTo(nearest(el, button, column));
    }
    case 'a':
      return inNav ? focusTo(firstOption()) : false;
    case 'right':
      if (inNav) return focusTo(firstOption());
      return el ? focusTo(nearest(el, 'right', pane(), true)) : false;
    case 'left':
      if (inNav) return true;   // nothing to the left of the list
      return focusTo((el && nearest(el, 'left', pane(), true)) || navItem());
    case 'b':
      return inNav ? false : focusTo(navItem());
  }
  return false;
}

// Rows with a value left/right change (steppers, choices, sliders: data-edit): moving over one
// never changes it. A picks the row (it shows it: .editing), then left/right change the value
// as it goes; A or B puts the row down, the value kept, and up/down move on from it. Toggles
// have no data-edit: A flips them. True when the button was the row's.
function editPress(button, el) {
  if (!el || el.dataset.edit === undefined) return false;
  if (editing !== el.dataset.id) {
    if (button !== 'a') return false;
    setEditing(el);
    return true;
  }
  switch (button) {
    case 'left': case 'right': {
      const section = EXT.sections[state.section];
      if (section && section.press && section.press(button, el)) return true;
      if (el.dataset.slider) adjust(el, button === 'right' ? 5 : -5);
      else if (el.dataset.setting) changeSetting(el.dataset.setting, button === 'right' ? 1 : -1);
      else if (el.dataset.timer !== undefined) stepTimer(button === 'right' ? 1 : -1);
      return true;
    }
    case 'a': case 'b': setEditing(null); return true;
    case 'up': case 'down': setEditing(null); return false;
  }
  return true;   // nothing else while a value is being changed
}

function setEditing(el) {
  editing = el ? el.dataset.id : null;
  for (const r of $('settings').querySelectorAll('.editing')) r.classList.remove('editing');
  if (el) el.classList.add('editing');
  const f = focusedEl();
  if (f) settingsFocused(f);
}

// The focus in Settings: a row being changed is put down when the focus leaves it; the pane
// scrolls to the focus (its ring clear of the button hints); the hints say what the buttons do.
function settingsFocused(el) {
  if (editing && el.dataset.id !== editing) setEditing(null);
  const main = el.closest('.spane main');
  // A list that scrolls itself (Wi-Fi networks, Bluetooth devices) keeps its row in view.
  if (main && !el.parentElement.closest('.wifi-scroll')) scrollIntoBox(el, main, 28);
  const bar = $('settings-hints');
  if (bar) bar.innerHTML = hints(settingsHints(el));
}

function settingsHints(el) {
  if (el.dataset.section) return [['A', 'Open'], ['B', 'Back']];
  if (editing === el.dataset.id) return [['←→', 'Change'], ['A', 'Done']];
  if (el.dataset.noa !== undefined) return [['B', 'Sections']];   // a row A does nothing on
  const what = el.dataset.edit !== undefined ? 'Change' : el.querySelector('.toggle') ? 'On / off' : 'Select';
  return [['A', what], ['B', 'Sections']];
}
