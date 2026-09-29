'use strict';
// Text fields in the launcher's own screens (the Wi-Fi password, a hidden network's name, a
// website's address and name, a tile's new name: library.js).
// Typing reaches them three ways, all ending here or in the field itself:
// - a real keyboard: keys stay in the field (keyGuard), except Enter (A: confirm) and Escape
//   (B: cancel), which still work the dialog;
// - the on-screen keyboard and the phone: the host posts the text (text.insert, text.key)
//   instead of typing it through Windows, so it can only land in this field;
// - R3, or a field taking the focus with the controller, asks for the on-screen keyboard. It
//   opens across the bottom of the screen, always; the host says where it starts, and a field
//   it would cover is lifted above it with its screen until it closes (textKeyboardAt).
//   From the host: {type:'text.insert', text} {type:'text.key', key: backspace|left|right|enter}
//                  {type:'text.keyboardAt', top: share of the height from the top, null: closed}
//   To the host:   {type:'text.keyboard', field, password} {type:'text.done'}
// A field's Enter (from either keyboard) raises 'textsubmit' on it; its screen decides what that does.

// The input types with a caret the page can move (email and number have none: not used here).
const TEXT_TYPES = /^(text|password|search|url|tel)$/i;

function isTextField(el) {
  return !!el && ((el.tagName === 'INPUT' && TEXT_TYPES.test(el.type || 'text')) || el.tagName === 'TEXTAREA' || el.isContentEditable === true);
}

function textField() { return isTextField(document.activeElement) ? document.activeElement : null; }

// A real key press while a field has the focus: true = the field's (leave it alone), false = the
// launcher's (Enter, Escape, or no field at all).
function keyGuard(e) {
  if (!textField()) return false;
  return e.key !== 'Enter' && e.key !== 'Escape';
}

function textInsert(text) {
  const el = textField();
  if (!el || el.isContentEditable) return;
  el.setRangeText(text, el.selectionStart ?? el.value.length, el.selectionEnd ?? el.value.length, 'end');
  el.dispatchEvent(new Event('input', { bubbles: true }));
}

function textKey(key) {
  const el = textField();
  if (!el || el.isContentEditable) return;
  const start = el.selectionStart ?? el.value.length, end = el.selectionEnd ?? start;
  switch (key) {
    case 'backspace':
      if (start !== end) el.setRangeText('', start, end, 'end');
      else if (start > 0) el.setRangeText('', start - 1, start, 'end');
      el.dispatchEvent(new Event('input', { bubbles: true }));
      break;
    case 'left': { const p = Math.max(0, start - (start === end ? 1 : 0)); el.setSelectionRange(p, p); break; }
    case 'right': { const p = Math.min(el.value.length, end + (start === end ? 1 : 0)); el.setSelectionRange(p, p); break; }
    case 'enter': el.dispatchEvent(new CustomEvent('textsubmit', { bubbles: true })); break;
  }
}

// The on-screen keyboard for this field (R3, or the field getting the focus from the controller).
function openKeyboardFor(el) {
  if (!isTextField(el)) return;
  el.focus();
  send({
    type: 'text.keyboard',
    field: el.getAttribute('aria-label') || el.placeholder || '',
    password: el.type === 'password',
  });
}

// The field's screen closed: the keyboard goes too.
function textDone() { send({ type: 'text.done' }); }

// The on-screen keyboard is up from top (a share of the window's height) down, or closed
// (null): the focused field, if it would be under it, goes up above it with its screen (the
// stage's child it is in), and back down once it closes. The keyboard itself never moves.
function textKeyboardAt(top) {
  const lifted = document.querySelector('[data-kb-lift]');
  if (lifted) { lifted.style.transform = ''; lifted.style.transition = ''; delete lifted.dataset.kbLift; }
  const el = textField(), stage = document.getElementById('stage');
  if (top == null || !el || !stage || !stage.contains(el)) return;
  let box = el;
  while (box.parentElement && box.parentElement !== stage) box = box.parentElement;
  const scale = stage.getBoundingClientRect().height / 1080 || 1;
  const over = el.getBoundingClientRect().bottom + 24 * scale - innerHeight * top;   // 24: room under the field
  if (over <= 0) return;
  box.dataset.kbLift = '';
  box.style.transition = 'transform 0.15s ease-out';
  box.style.transform = `translateY(${-Math.ceil(over / scale)}px)`;
}
