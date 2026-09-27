'use strict';
// Button maps (SPEC N13; design: Button maps, Button map editor): the list of apps with their
// preset and changes, and the editor for one app. Views of their own (added to app.js).
// Presets come from the host (ButtonMap.cs), never copied here; this file only knows how to
// name actions.
//
//   To the host:   {type:'maps.get'} {type:'maps.preset', id, preset} {type:'maps.control', id, control, value}
//                  {type:'maps.reset', id, control} {type:'resume', id} (test in the app)
//   From the host: {type:'maps.data', presets: {mouse: {a: 'key:Enter', ...}, ...},
//                   apps: [{id, name, glyph, color, map: {preset, defaultPreset, changes}}]}

const maps = {
  data: null,        // from the host
  id: null,          // the app being edited
  picking: null,     // the control whose action is being picked (focus in the side panel)
  cat: 'keys',       // category shown in the side panel
  combo: null,       // { mods: [], key } while building a key combination
  row: 'b-a',        // the row to come back to after picking
  choosingPreset: false,   // the preset's choice is open in the side panel
};

// Rows of the editor, two columns, pairs side by side: [control, badge]. Home is last, fixed.
const MAP_CONTROLS = [['leftStick', 'L stick'], ['rightStick', 'R stick'], ['a', 'A'], ['b', 'B'], ['x', 'X'], ['y', 'Y'],
  ['lb', 'LB'], ['rb', 'RB'], ['lt', 'LT'], ['rt', 'RT'], ['select', 'Select'], ['start', 'Start'],
  ['dpad', 'D-pad'], ['l3', 'L3'], ['r3', 'R3']];

// What a button can do, by category (design: Keys, Mouse, Media, Launcher, Nothing).
const MAP_ACTIONS = {
  keys: [['key:Enter', 'Enter'], ['key:Space', 'Space'], ['key:Esc', 'Esc'], ['key:Tab', 'Tab'], ['key:Backspace', 'Backspace'],
    ['key:Delete', 'Delete'], ['key:PageUp', 'Page Up'], ['key:PageDown', 'Page Down'], ['key:Home', 'Home key'], ['key:End', 'End'],
    ['key:Alt+Left', 'Back (Alt+←)'], ['key:Alt+Right', 'Forward (Alt+→)'], ['key:Ctrl+Tab', 'Next tab'], ['key:Ctrl+Shift+Tab', 'Previous tab'],
    ['key:F', 'F (full screen on video sites)'], ['key:M', 'M (mute on video sites)'], ['key:F11', 'F11 (full screen)'], ['key:F5', 'F5 (refresh)'],
    ['key:Menu', 'Menu key'], ['combo', 'Key combination…']],
  mouse: [['mouse:left', 'Click (hold to drag)'], ['mouse:right', 'Right-click'], ['mouse:middle', 'Middle click'], ['mouse:precise', 'Hold: slow pointer']],
  media: [['key:MediaPlayPause', 'Play / pause'], ['key:MediaNext', 'Next'], ['key:MediaPrev', 'Previous'], ['key:MediaStop', 'Stop'],
    ['do:volumeUp', 'Volume up'], ['do:volumeDown', 'Volume down'], ['do:mute', 'Mute']],
  launcher: [['do:menu', 'Home menu'], ['do:keyboard', 'On-screen keyboard'], ['do:power', 'Power menu'], ['do:timer', 'Sleep timer']],
  none: [['none', 'Nothing']],
};
const MAP_CATS = [['keys', 'Keys'], ['mouse', 'Mouse'], ['media', 'Media'], ['launcher', 'Launcher'], ['none', 'Nothing']];
const STICK_ACTIONS = [['pointer', 'Move the pointer'], ['scroll', 'Scroll'], ['arrows', 'Arrow keys'], ['none', 'Nothing']];
const DPAD_ACTIONS = [['arrows', 'Arrow keys'], ['none', 'Nothing']];

// Keys for a combination: names as ButtonMapStore.cs knows them, [name, face].
const COMBO_KEYS = [
  ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'.split('').map((c) => [c, c]),
  ...Array.from({ length: 12 }, (_, i) => [`F${i + 1}`, `F${i + 1}`]),
  ['Esc', 'Esc'], ['Tab', 'Tab'], ['Enter', 'Enter'], ['Space', 'Space'], ['Backspace', 'Bksp'], ['Delete', 'Del'],
  ['Home', 'Home'], ['End', 'End'], ['PageUp', 'PgUp'], ['PageDown', 'PgDn'],
  ['Left', '←'], ['Up', '↑'], ['Right', '→'], ['Down', '↓'],
  ['Minus', '-'], ['Equal', '='], ['Comma', ','], ['Period', '.'], ['Slash', '/'], ['Semicolon', ';'], ['Quote', "'"],
  ['BracketLeft', '['], ['BracketRight', ']'], ['Backslash', '\\'], ['Backquote', '`'],
];
const MODS = ['Ctrl', 'Alt', 'Shift'];
const KEY_FACE = Object.fromEntries(COMBO_KEYS.map(([n, f]) => [n, f]).concat([['PageUp', 'Page Up'], ['PageDown', 'Page Down'],
  ['Backspace', 'Backspace'], ['Delete', 'Delete'], ['Menu', 'Menu key'], ['MediaPlayPause', 'Play/pause'],
  ['MediaNext', 'Next track'], ['MediaPrev', 'Previous track'], ['MediaStop', 'Stop'], ['BrowserBack', 'Browser back'],
  ['BrowserForward', 'Browser forward'], ['BrowserRefresh', 'Refresh']]));

function comboText(value) {
  return value.slice(4).split('+').map((k) => KEY_FACE[k] || k).join('+');
}

// "key:Alt+T" → "Alt+T"; a named action by its name.
function actionLabel(control, value) {
  const lists = control === 'leftStick' || control === 'rightStick' ? [STICK_ACTIONS] : control === 'dpad' ? [DPAD_ACTIONS] : Object.values(MAP_ACTIONS);
  for (const list of lists) {
    const hit = list.find(([v]) => v === value);
    if (hit) return hit[1];
  }
  return value && value.startsWith('key:') ? comboText(value) : value || 'Nothing';
}

function categoryOf(value) {
  for (const [cat] of MAP_CATS) if (MAP_ACTIONS[cat].some(([v]) => v === value)) return cat;
  return value && value.startsWith('key:') ? 'keys' : 'none';
}

function mapApp(id) { return maps.data && maps.data.apps.find((a) => a.id === id); }

function valueOf(app, control) {
  const changed = app.map.changes[control];
  if (changed !== undefined) return changed;
  const preset = maps.data.presets[app.map.preset] || {};
  return preset[control];
}

const PRESET_NAMES = { controller: 'Controller', mouse: 'Mouse', keyboard: 'Keyboard' };

// ---- The list (Settings › Controller › Button maps) --------------------------------------------

function settingsNavStatic(active) {
  return `<nav class="snav"><div class="snav-title">${icon('chevleft', 36, 2)}<span>Settings</span></div>` +
    SECTIONS.map(([id, glyph, label]) => `<div class="sitem${id === active ? ' on' : ''}">${icon(glyph, 32)}${esc(label)}</div>`).join('') +
    '</nav>';
}

function changesText(app) {
  const n = Object.keys(app.map.changes).length;
  return n ? `${n} button${n > 1 ? 's' : ''} changed` : '';
}

addView('maps', {
  wrap: false,   // up on the first app, down on the last: stays there
  render() {
    const apps = maps.data ? maps.data.apps : [];
    const rows = apps.map((a) =>
      `<div class="mrow" data-nav data-id="m-${esc(a.id)}" data-act="map-edit" data-arg="${esc(a.id)}">` +
        `<span style="display:flex;color:${esc(a.color || 'inherit')}">${icon(a.glyph, 34)}</span>` +
        `<span class="mname">${esc(a.name)}</span>` +
        `<span class="mpreset">${esc(PRESET_NAMES[a.map.preset] || a.map.preset)}</span>` +
        `<span class="mchanges">${esc(changesText(a))}</span>${icon('chevright', 28, 2)}</div>`).join('');
    renderKeepingScroll($('maps'), settingsNavStatic('controller') +
      '<div class="spane"><main>' +
        `<header><span class="back">${icon('chevleft', 22, 2)}Controller</span><h1>Button maps</h1>` +
        '<p>Each app gets its own buttons. Home always opens the menu.</p></header>' +
        `<div class="mlist">${rows || '<p class="snote">Loading…</p>'}</div>` +
      `</main><footer class="hints">${hints([['A', 'Edit'], ['B', 'Back']])}</footer></div>`, '.mlist', 'maps');
    setTimeout(() => keepInView('maps', '.mlist'), 0);
  },
  press(button, el) {
    if (button !== 'up' && button !== 'down') return false;
    stepFocus(el, button);
    keepInView('maps', '.mlist');
    return true;
  },
  demo() { mapsDemo(); },
});

onAction('maps', () => { send({ type: 'maps.get' }); go('maps'); });
onAction('map-edit', (el, id) => openEditor(id));

// Home menu › Buttons: the editor for the app the menu was opened over, else the list.
onAction('buttons', () => {
  send({ type: 'maps.get' });
  if (state.current && mapApp(state.current)) openEditor(state.current); else go('maps');
});

function openEditor(id) {
  maps.id = id;
  maps.picking = null;
  maps.combo = null;
  maps.choosingPreset = false;
  go('buttons');
}

hostMessage('maps.data', (m) => {
  maps.data = m;
  if (state.view === 'maps' || state.view === 'buttons') render();
});

// The focused element of a scrolling list stays in view, clear of the list's faded ends (the
// list scrolls, nothing around it: app.js's scrollIntoBox); the ends fade only where there is
// more to scroll to (.more-up, .more-down).
function keepInView(view, listSelector) {
  const f = document.querySelector(`#${view} [data-nav].focused`);
  const list = f && f.closest(listSelector);
  if (list) scrollIntoBox(f, list, 64);
  for (const l of document.querySelectorAll(`#${view} :is(${listSelector})`)) listEdges(l);
}

function listEdges(list) {
  list.classList.toggle('more-up', list.scrollTop > 2);
  list.classList.toggle('more-down', list.scrollTop + list.clientHeight < list.scrollHeight - 2);
}

// Re-renders a view with its scrolling list left where it was (the clock and the host redraw
// every so often), unless the list now shows something else (another key).
function renderKeepingScroll(el, html, listSelector, key) {
  const old = el.querySelector(listSelector);
  const top = old && el.dataset.listKey === key ? old.scrollTop : 0;
  el.innerHTML = html;
  el.dataset.listKey = key;
  const list = el.querySelector(listSelector);
  if (list) { list.scrollTop = top; listEdges(list); }
}

// Moves the focus in a direction, no wrapping round (the lists and the grid stop at their ends);
// left and right only to what is beside it (not off the wide preset row into the grid).
function stepFocus(el, dir) {
  const to = el && nearest(el, dir, items(), true);
  if (to) setFocus(to);
}

// ---- The editor ------------------------------------------------------------------------------

function editorRows(app) {
  const controller = app.map.preset === 'controller';
  const moonlight = app.id === 'moonlight';
  const rows = MAP_CONTROLS.map(([control, badge]) => {
    const value = valueOf(app, control);
    const changed = app.map.changes[control] !== undefined;
    let does = actionLabel(control, value);
    if (controller) does = control === 'r3' && !moonlight ? 'On-screen keyboard' : 'To the app';
    const editing = maps.picking === control;
    const nav = controller || maps.picking || maps.choosingPreset ? '' : ` data-nav data-id="b-${control}" data-control="${control}"`;
    return `<div class="brow${editing ? ' editing' : ''}${controller ? ' fixed' : ''}"${nav}>` +
      `<span class="bkey${badge.length > 1 ? ' wide' : ''}">${esc(badge)}</span>` +
      `<span class="bdoes">${esc(does)}</span>${changed ? '<span class="bdot" aria-label="Changed"></span>' : ''}</div>`;
  });
  rows.push('<div class="brow fixed"><span class="bkey wide">Home</span><span class="bdoes">Home menu (fixed)</span></div>');
  return rows.join('');
}

const PRESET_ORDER = ['controller', 'mouse', 'keyboard'];
const STICK_TEXT = { pointer: 'moves the pointer', scroll: 'scrolls', arrows: 'is the arrow keys', none: 'does nothing' };

// What a preset does, from its own map (the host's), so the two never disagree.
function presetText(id) {
  if (id === 'controller') return 'Every button goes to the app, which reads the controller itself.';
  const p = (maps.data && maps.data.presets[id]) || {};
  return `L stick ${STICK_TEXT[p.leftStick] || 'does nothing'}, R stick ${STICK_TEXT[p.rightStick] || 'does nothing'}; the buttons press keys and click.`;
}

// The row in focus: the preset row ('b-preset') or a button's ('b-a'...), also while its
// choice is open in the side panel.
function focusedRow(app) {
  if (maps.choosingPreset || app.map.preset === 'controller') return 'b-preset';
  if (maps.picking) return 'b-' + maps.picking;
  const f = document.querySelector('#buttons .bmain .focused');
  return (f && f.dataset.id) || maps.row;
}

// The side panel: what the focused row is (the preset and what each preset does, or what the
// focused button does), or the choice open for it.
function presetAside(app) {
  const check = `<span class="bcheck">${icon('check', 24, 2.5)}</span>`;
  const items = PRESET_ORDER.map((p) => {
    const on = p === app.map.preset;
    return `<div class="bitem tall${on ? ' on' : ''}"${maps.choosingPreset ? ` data-nav data-id="bp-${p}" data-preset-value="${p}"` : ''}>` +
      `<span class="grow"><b>${esc(PRESET_NAMES[p])}</b><small>${esc(presetText(p))}</small></span>${on ? check : ''}</div>`;
  }).join('');
  return '<span class="btitle">Preset</span>' +
    `<p class="snote">What ${esc(app.name)}’s buttons start from. Another preset puts every button back as it has them.</p>` +
    `<div class="blist">${items}</div>`;
}

function buttonAside(app, control, badge) {
  const value = valueOf(app, control);
  const stick = control === 'leftStick' || control === 'rightStick' || control === 'dpad';
  const cat = stick ? '' : (MAP_CATS.find(([c]) => c === categoryOf(value)) || [null, ''])[1];
  const preset = PRESET_NAMES[app.map.preset];
  const changed = app.map.changes[control] !== undefined;
  const was = (maps.data.presets[app.map.preset] || {})[control];
  return `<span class="btitle">${esc(badge)} does</span>` +
    `<div class="bnow"><span class="bnow-value">${esc(actionLabel(control, value))}</span>${cat ? `<span class="bnow-cat">${esc(cat)}</span>` : ''}</div>` +
    `<p class="snote">${changed ? `Changed. In the ${esc(preset)} preset: ${esc(actionLabel(control, was))}. X puts that back.`
      : `As in the ${esc(preset)} preset.`} A picks another action.</p>`;
}

function asideList(app, control) {
  const value = valueOf(app, control);
  const kind = control === 'leftStick' || control === 'rightStick' ? STICK_ACTIONS : control === 'dpad' ? DPAD_ACTIONS : null;
  const list = kind || MAP_ACTIONS[maps.cat];
  const items = list.map(([v, label], i) => {
    const current = v === value || (v === 'combo' && value && value.startsWith('key:') && !MAP_ACTIONS.keys.some(([k]) => k === value));
    const text = v === 'combo' && current ? `Key combination: ${comboText(value)}` : label;
    return `<div class="bitem${current ? ' on' : ''}" data-nav data-id="bi-${i}" data-value="${esc(v)}">` +
      `<span class="grow">${esc(text)}</span>${current ? `<span class="bcheck">${icon('check', 24, 2.5)}</span>` : ''}</div>`;
  }).join('');
  // The categories, LB and RB (or left and right) at their ends go from one to the next.
  const cats = kind ? '' : '<div class="bcats"><span class="key wide">LB</span>' + MAP_CATS.map(([c, label]) =>
    `<span class="bcat${c === maps.cat ? ' on' : ''}">${esc(label)}</span>`).join('') + '<span class="key wide">RB</span></div>';
  return cats + `<div class="blist">${items}</div>`;
}

function asideCombo() {
  const c = maps.combo;
  const mods = '<div class="bmods">' + MODS.map((m) =>
    `<div class="bmod${c.mods.includes(m) ? ' on' : ''}" data-nav data-id="bm-${m}" data-mod="${m}">${m}</div>`).join('') + '</div>';
  const keys = '<div class="bkeys">' + COMBO_KEYS.map(([name, face]) =>
    `<div class="bk${c.key === name ? ' on' : ''}" data-nav data-id="bk-${esc(name)}" data-key="${esc(name)}">${esc(face)}</div>`).join('') + '</div>';
  const text = c.key ? [...c.mods, c.key].map((k) => KEY_FACE[k] || k).join(' + ') : 'Pick a key';
  return mods + keys +
    `<div class="bcombo"><span class="grow">${esc(text)}</span>` +
    `<div class="buse${c.key ? '' : ' off'}" data-nav data-id="b-use" data-use>Use it</div></div>`;
}

// The preset row: which preset the app's buttons start from, and how many differ from it.
function presetRow(app) {
  const n = Object.keys(app.map.changes).length;
  const caption = app.map.preset === 'controller' ? `${esc(app.name)} reads the controller itself: every button goes to it.`
    : n ? `${n} button${n > 1 ? 's' : ''} changed from it, marked <span class="bdot"></span>` : 'Every button as the preset has it.';
  const nav = maps.picking || maps.choosingPreset || maps.combo ? '' : ' data-nav data-id="b-preset" data-preset';
  return `<div class="srow bpreset${maps.choosingPreset ? ' editing' : ''}"${nav}>` +
    `<div class="text"><span class="label">Preset: <b>${esc(PRESET_NAMES[app.map.preset] || app.map.preset)}</b></span>` +
    `<span class="caption">${caption}</span></div><div class="value">Change${icon('chevright', 28, 2)}</div></div>`;
}

addView('buttons', {
  wrap: false,
  render() {
    const app = mapApp(maps.id);
    if (!app) { $('buttons').innerHTML = '<div class="bedit"><p class="snote">Loading…</p></div>'; return; }
    const row = focusedRow(app);
    const control = row === 'b-preset' ? null : row.slice(2);
    const badge = control && (MAP_CONTROLS.find(([c]) => c === control) || [null, 'Home'])[1];
    // Not picking: the category is the focused button's, for when A opens its choice.
    if (control && !maps.picking && !maps.combo) maps.cat = categoryOf(valueOf(app, control));
    const aside = maps.combo ? `<span class="btitle">Key combination for ${esc(badge)}</span>${asideCombo()}`
      : maps.picking ? `<span class="btitle">${esc(badge)} does…</span>${asideList(app, control)}`
      : control ? buttonAside(app, control, badge) : presetAside(app);
    const test = state.current === app.id ? [['LB', 'Test in the app']] : [];
    const fixedList = control === 'leftStick' || control === 'rightStick' || control === 'dpad';
    const list = maps.combo ? [['A', 'Pick'], ['Start', 'Use it'], ['B', 'Back']]
      : maps.picking ? [['A', 'Pick'], ...(fixedList ? [] : [[['LB', 'RB'], 'Category']]), ['B', 'Cancel']]
      : maps.choosingPreset ? [['A', 'Use it'], ['B', 'Cancel']]
      : control ? [['A', 'Change'], ...(app.map.changes[control] !== undefined ? [['X', 'Put back']] : []), ...test, ['B', 'Back']]
      : [['A', 'Change preset'], ...test, ['B', 'Back']];
    renderKeepingScroll($('buttons'), '<div class="bedit"><div class="bmain">' +
      `<header><span class="back">${icon('chevleft', 22, 2)}Button maps</span><h1>Buttons for ${esc(app.name)}</h1></header>` +
      presetRow(app) +
      `<div class="bgrid">${editorRows(app)}</div></div>` +
      `<aside class="baside">${aside}</aside></div>` +
      `<footer class="hints">${hints(list)}</footer>`,
      '.blist, .bkeys', `${app.id}:${row}:${maps.picking ? maps.cat : ''}:${maps.combo ? 'combo' : ''}:${maps.choosingPreset ? 'preset' : ''}`);
    setTimeout(() => keepInView('buttons', '.blist, .bkeys'), 0); // after app.js has put the focus back
  },
  focus(list) {
    if (maps.combo) return list.find((e) => e.dataset.key === maps.combo.key) || list.find((e) => e.dataset.key);
    if (maps.picking) return list.find((e) => e.classList.contains('on') && e.dataset.value) || list.find((e) => e.dataset.value);
    if (maps.choosingPreset) return list.find((e) => e.classList.contains('on') && e.dataset.presetValue) || list[0];
    return list.find((e) => e.dataset.id === maps.row) || list.find((e) => e.dataset.control) || list[0];
  },
  press(button, el) {
    const app = mapApp(maps.id);
    if (!app) return false;
    if (maps.combo) return comboPress(button, el, app);
    if (maps.picking) return pickPress(button, el, app);
    if (maps.choosingPreset) return presetPress(button, el, app);
    switch (button) {
      case 'a':
        if (el && el.dataset.control) { startPicking(app, el.dataset.control); return true; }
        if (el && el.dataset.preset !== undefined) { maps.choosingPreset = true; state.memory.buttons = null; render(); return true; }
        return false;
      case 'x':
        if (el && el.dataset.control && app.map.changes[el.dataset.control] !== undefined) {
          delete app.map.changes[el.dataset.control];
          send({ type: 'maps.reset', id: app.id, control: el.dataset.control });
          render();
        }
        return true;
      case 'lb':
        if (state.current === app.id) send({ type: 'resume', id: app.id });
        return true;
      case 'up': case 'down': case 'left': case 'right':
        // Around the preset row and the grid, stopping at their edges; the side panel follows.
        stepFocus(el, button);
        if (focusedEl()) maps.row = focusedEl().dataset.id;
        render();
        return true;
    }
    return false;
  },
  // #buttons/twitch, #buttons/twitch:start:media (picking Start's action), #buttons/twitch:select:combo,
  // #buttons/twitch:preset (choosing the preset).
  demo(arg) {
    mapsDemo();
    const [id, control, cat] = (arg || 'twitch').split(':');
    maps.id = id;
    if (control === 'preset') { maps.choosingPreset = true; maps.row = 'b-preset'; }
    else if (control) { maps.picking = control; maps.row = 'b-' + control; }
    if (cat === 'combo') maps.combo = { mods: ['Alt'], key: 'T' };
    else if (cat) maps.cat = cat;
  },
});

// The preset's choice in the side panel: A takes one (asking first when buttons were changed,
// since they go back to it), B leaves it as it was.
function presetPress(button, el, app) {
  switch (button) {
    case 'up': case 'down': stepFocus(el, button); return true;
    case 'a': if (el && el.dataset.presetValue) choosePreset(app, el.dataset.presetValue); return true;
    case 'b': closePreset(); return true;
  }
  return true;
}

function closePreset() {
  maps.choosingPreset = false;
  maps.row = 'b-preset';
  state.memory.buttons = 'b-preset';
  render();
}

function choosePreset(app, next) {
  if (next === app.map.preset) { closePreset(); return; }
  const apply = () => {
    app.map.preset = next;
    app.map.changes = {};
    send({ type: 'maps.preset', id: app.id, preset: next });
    closePreset();
  };
  const n = Object.keys(app.map.changes).length;
  if (n) ask({ title: `Switch ${app.name} to the ${PRESET_NAMES[next]} preset?`, yes: 'Switch', onYes: apply,
    text: `Its ${n} changed button${n > 1 ? 's go' : ' goes'} back to what the preset has.` });
  else apply();
}

function startPicking(app, control) {
  maps.picking = control;
  maps.row = 'b-' + control;
  maps.cat = categoryOf(valueOf(app, control));
  state.memory.buttons = null;
  render();
}

function stopPicking() {
  maps.picking = null;
  state.memory.buttons = maps.row;
  render();
}

function setControl(app, control, value) {
  app.map.changes[control] = value;
  send({ type: 'maps.control', id: app.id, control, value });
}

function pickPress(button, el, app) {
  const control = maps.picking;
  const fixedList = control === 'leftStick' || control === 'rightStick' || control === 'dpad';
  switch (button) {
    case 'a':
      if (!el || !el.dataset.value) return true;
      if (el.dataset.value === 'combo') {
        const value = valueOf(app, control) || '';
        const parts = value.startsWith('key:') ? value.slice(4).split('+') : [];
        maps.combo = { mods: parts.filter((p) => MODS.includes(p)), key: parts.find((p) => !MODS.includes(p)) || null };
        state.memory.buttons = null;
        render();
        return true;
      }
      setControl(app, control, el.dataset.value);
      stopPicking();
      return true;
    case 'b': stopPicking(); return true;
    case 'lb': case 'rb': case 'left': case 'right': {
      if (fixedList) return true;
      const i = MAP_CATS.findIndex(([c]) => c === maps.cat);
      const step = button === 'rb' || button === 'right' ? 1 : -1;
      maps.cat = MAP_CATS[(i + step + MAP_CATS.length) % MAP_CATS.length][0];
      state.memory.buttons = null;
      render();
      return true;
    }
    case 'up': case 'down':   // along the list, which scrolls; it stops at its ends
      stepFocus(el, button);
      keepInView('buttons', '.blist');
      return true;
  }
  return true;
}

function comboPress(button, el, app) {
  const c = maps.combo;
  const use = () => {
    if (!c.key) return;
    const ordered = MODS.filter((m) => c.mods.includes(m));
    setControl(app, maps.picking, 'key:' + [...ordered, c.key].join('+'));
    maps.combo = null;
    stopPicking();
  };
  switch (button) {
    case 'a':
      if (!el) return true;
      if (el.dataset.mod) {
        c.mods = c.mods.includes(el.dataset.mod) ? c.mods.filter((m) => m !== el.dataset.mod) : [...c.mods, el.dataset.mod];
      } else if (el.dataset.key) c.key = el.dataset.key;
      else if (el.dataset.use !== undefined) { use(); return true; }
      state.memory.buttons = el.dataset.id;
      render();
      return true;
    case 'start': use(); return true;
    case 'b':
      maps.combo = null;
      state.memory.buttons = null;
      render();
      return true;
    case 'up': case 'down': case 'left': case 'right': return false;   // around the grid (no wrapping)
  }
  return true;
}

// ---- Demo data (a plain browser) ---------------------------------------------------------------

function mapsDemo() {
  if (maps.data) return;
  const mouse = { leftStick: 'pointer', rightStick: 'scroll', dpad: 'arrows', a: 'key:Enter', b: 'key:Esc', x: 'mouse:left', y: 'key:Space',
    lb: 'key:Alt+Left', rb: 'key:Alt+Right', lt: 'mouse:right', rt: 'mouse:precise', select: 'key:Esc', start: 'key:MediaPlayPause',
    l3: 'mouse:middle', r3: 'do:keyboard' };
  const keyboard = { leftStick: 'arrows', rightStick: 'pointer', dpad: 'arrows', a: 'key:Enter', b: 'key:Esc', x: 'key:Space', y: 'key:Tab',
    lb: 'key:PageUp', rb: 'key:PageDown', lt: 'key:Home', rt: 'key:End', select: 'key:Backspace', start: 'key:Menu', l3: 'mouse:left', r3: 'do:keyboard' };
  const app = (id, name, glyph, color, preset, changes) => ({ id, name, glyph, color, map: { preset, defaultPreset: preset, changes: changes || {} } });
  maps.data = {
    presets: { controller: {}, mouse, keyboard },
    apps: [
      app('youtube', 'YouTube', 'youtube', '#FF5B52', 'controller'),
      app('twitch', 'Twitch', 'chat', '#B08CFF', 'mouse', { select: 'key:Alt+T', start: 'key:F' }),
      app('stremio', 'Stremio', 'film', '#7C8CFF', 'mouse'),
      app('jellyfin', 'Jellyfin', 'library', '#3DC0F0', 'controller'),
      app('moonlight', 'Moonlight', 'moon', '#F5D16B', 'controller'),
      app('edge', 'Browser', 'globe', '#3CCB9A', 'mouse'),
      app('netflix', 'Netflix', 'play', '#FF4B55', 'keyboard'),
      app('_other', 'Other windows', 'app', '#B3B5BC', 'mouse'),
    ],
  };
}
