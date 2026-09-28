'use strict';
// On-screen keyboard (SPEC N11). The launcher shows this page in a window that never takes the
// focus, over the app, and sends it the controller; what it types goes to the app through
// Windows input.
//   From the host: {type:'open', field, password} {type:'input', button}
//   To the host:   {type:'ready'} {type:'type', text} {type:'close'}
//                  {type:'key', key: backspace|enter|left|right|tab|refresh|zoomIn|zoomOut|fullscreen|volumeUp|volumeDown|mute}

const host = window.chrome && window.chrome.webview;
const send = (msg) => host ? host.postMessage(msg) : console.log('to host', msg);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

const kb = document.getElementById('kb');
function fit() { kb.style.transform = `scale(${innerWidth / 1920})`; }
addEventListener('resize', fit);
fit();

const LETTERS = ['1234567890', 'qwertyuiop', 'asdfghjkl@', 'zxcvbnm.'];
const SYMBOLS = ['1234567890', '!#$%^&*()-', "_=+[]{};:'", '",<>?/\\~'];

let symbols = false;
let shift = 'off';            // off | once (next letter) | lock
let focus = { row: 1, col: 0 };
let field = '', password = false, reveal = false, typed = '';

function rows() {
  const set = symbols ? SYMBOLS : LETTERS;
  const upper = shift !== 'off' && !symbols;
  const chars = (s) => s.split('').map((c) => ({ id: 'char', text: upper ? c.toUpperCase() : c }));
  return [
    chars(set[0]),
    chars(set[1]),
    chars(set[2]),
    [{ id: 'shift', w: 132, icon: 'shift', aria: 'Shift' }, ...chars(set[3]), { id: 'back', w: 132, icon: 'backspace', aria: 'Delete' }],
    [
      { id: 'symbols', w: 132, label: symbols ? 'abc' : '#+=', aria: symbols ? 'Letters' : 'Symbols' },
      { id: 'char', w: 132, text: '.com' },
      { id: 'char', w: 400, text: ' ', aria: 'Space' },
      { id: 'left', icon: 'chevleft', aria: 'Cursor left' },
      { id: 'right', icon: 'chevright', aria: 'Cursor right' },
      { id: 'enter', w: 170, label: 'Enter', primary: true },
    ],
    // Keys a controller has no button for (website apps): sent by the launcher.
    [
      { id: 'extra', key: 'tab', w: 110, label: 'Tab' },
      { id: 'extra', key: 'refresh', w: 132, label: 'Refresh' },
      { id: 'extra', key: 'zoomOut', w: 110, label: 'Zoom −', aria: 'Zoom out' },
      { id: 'extra', key: 'zoomIn', w: 110, label: 'Zoom +', aria: 'Zoom in' },
      { id: 'extra', key: 'fullscreen', w: 150, label: 'Full screen' },
      { id: 'extra', key: 'volumeDown', w: 110, label: 'Vol −', aria: 'Volume down' },
      { id: 'extra', key: 'volumeUp', w: 110, label: 'Vol +', aria: 'Volume up' },
      { id: 'extra', key: 'mute', w: 110, label: 'Mute' },
    ],
  ];
}

function render() {
  const layout = rows();
  focus.row = Math.min(focus.row, layout.length - 1);
  focus.col = Math.min(focus.col, layout[focus.row].length - 1);
  // The keys are drawn again only when they change (shift, symbols); a move only moves the
  // ring, so a key's press flash (flash) is seen and nothing else redraws.
  const rowsEl = document.getElementById('kb-rows');
  const html = layout.map((row, r) =>
    `<div class="kb-row">${row.map((k, c) => {
      const cls = ['kb-key'];
      if (k.w) cls.push('w' + k.w);
      if (k.primary) cls.push('primary');
      if (k.id === 'extra') cls.push('extra');
      if (k.id === 'shift' && shift === 'once') cls.push('latched');
      if (k.id === 'shift' && shift === 'lock') cls.push('locked');
      const face = k.icon ? icon(k.icon, 28, 2) : esc(k.label || (k.text === ' ' ? '' : k.text));
      return `<div class="${cls.join(' ')}" data-r="${r}" data-c="${c}" role="button" aria-label="${esc(k.aria || k.label || k.text)}">${face}</div>`;
    }).join('')}</div>`).join('');
  if (rowsEl.dataset.html !== html) { rowsEl.innerHTML = html; rowsEl.dataset.html = html; }
  for (const el of rowsEl.querySelectorAll('.kb-key.on')) el.classList.remove('on');
  const on = rowsEl.querySelector(`.kb-key[data-r="${focus.row}"][data-c="${focus.col}"]`);
  if (on) on.classList.add('on');
  document.getElementById('kb-field').textContent = field || 'the app';
  document.getElementById('kb-typed').textContent = password ? (reveal ? typed : '•'.repeat(typed.length)) : '';
  const list = [['A', 'Type'], ['X', 'Delete'], ['Y', 'Space'], ['LT', 'Shift'], ['LB', '←'], ['RB', '→'], ['Start', 'Enter']];
  if (password) list.push(['Select', reveal ? 'Hide password' : 'Show password']);
  list.push(['B', 'Close']);
  document.getElementById('kb-hints').innerHTML = list.map(([btn, label]) =>
    `<div class="hint"><span class="key${btn.length > 1 ? ' wide' : ''}">${esc(btn)}</span><span>${esc(label)}</span></div>`).join('');
}

function flash(r, c) {
  const el = document.querySelector(`.kb-key[data-r="${r}"][data-c="${c}"]`);
  if (!el) return;
  el.classList.add('hit');
  setTimeout(() => el.classList.remove('hit'), 90);
}

function typeText(text) {
  send({ type: 'type', text });
  typed += text;
  if (shift === 'once') shift = 'off';
}

function backspace() { send({ type: 'key', key: 'backspace' }); typed = typed.slice(0, -1); }

function cycleShift() { shift = shift === 'off' ? 'once' : shift === 'once' ? 'lock' : 'off'; }

function press(k) {
  switch (k.id) {
    case 'char': typeText(k.text); break;
    case 'shift': cycleShift(); break;
    case 'back': backspace(); break;
    case 'symbols': symbols = !symbols; break;
    case 'left': case 'right': send({ type: 'key', key: k.id }); break;
    case 'enter': send({ type: 'key', key: 'enter' }); break;
    case 'extra': send({ type: 'key', key: k.key }); break;
  }
}

// Up and down go to the key nearest in the row above or below (rows differ in width).
function moveVertical(dir) {
  const layout = rows();
  const target = focus.row + dir;
  if (target < 0 || target >= layout.length) return;
  const from = document.querySelector(`.kb-key[data-r="${focus.row}"][data-c="${focus.col}"]`).getBoundingClientRect();
  const x = from.left + from.width / 2;
  let best = 0, bestDistance = Infinity;
  document.querySelectorAll(`.kb-key[data-r="${target}"]`).forEach((el, i) => {
    const b = el.getBoundingClientRect();
    const d = Math.abs(b.left + b.width / 2 - x);
    if (d < bestDistance) { best = i; bestDistance = d; }
  });
  focus = { row: target, col: best };
}

function onButton(button) {
  const layout = rows();
  const count = layout[focus.row].length;
  switch (button) {
    case 'up': moveVertical(-1); break;
    case 'down': moveVertical(1); break;
    // Nothing wraps round (as everywhere on the TV): the ends of a row stop the focus.
    case 'left': focus.col = Math.max(0, focus.col - 1); break;
    case 'right': focus.col = Math.min(count - 1, focus.col + 1); break;
    case 'a': flash(focus.row, focus.col); press(layout[focus.row][focus.col]); break;
    case 'x': backspace(); break;
    case 'y': typeText(' '); break;
    case 'lt': cycleShift(); break;
    case 'lb': send({ type: 'key', key: 'left' }); break;
    case 'rb': send({ type: 'key', key: 'right' }); break;
    case 'start': send({ type: 'key', key: 'enter' }); break;
    case 'select': if (password) reveal = !reveal; break;
    case 'b': send({ type: 'close' }); return;
    default: return;
  }
  render();
}

function onHost(m) {
  if (m.type === 'open') {
    field = m.field || '';
    password = !!m.password;
    symbols = false; shift = 'off'; reveal = false; typed = '';
    focus = { row: 1, col: 0 };
    render();
  } else if (m.type === 'input') {
    onButton(m.button);
  }
}

if (host) {
  host.addEventListener('message', (e) => onHost(e.data));
} else {
  // A normal browser: the keyboard stands in for the controller.
  const keys = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right', Enter: 'a', Escape: 'b',
    Backspace: 'x', ' ': 'y', Shift: 'lt', PageUp: 'lb', PageDown: 'rb', F2: 'select', F10: 'start' };
  addEventListener('keydown', (e) => { if (keys[e.key]) { e.preventDefault(); onButton(keys[e.key]); } });
  onHost({ type: 'open', field: 'Password', password: true });
}
render();
send({ type: 'ready' });
