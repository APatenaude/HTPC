'use strict';
// Self-test: the key guard in a text field, text from the on-screen keyboard. Run by selftest.js, in its order.
selftestGroup(({ check, checkRows, key, sent, lastSent, pressed }) => {
  // ---- Key guard: typing keys stay in a text field, Enter and Escape still work the dialog ----------
  selftestFresh();
  const field = document.createElement('input');
  field.type = 'password';
  field.setAttribute('aria-label', 'Password for [Network]');
  $('stage').appendChild(field);
  field.focus();
  const typed = (k) => { pressed.length = 0; const e = key(k); return [`"${k}"`, pressed.length === 0 && !e.defaultPrevented, pressed.join(',') || 'handled']; };
  const button = (k, b) => { pressed.length = 0; key(k); return [`${k} (${b})`, pressed[0] === b, pressed.join(',') || 'nothing']; };
  checkRows('key guard: typing keys stay in the field; Enter and Escape still work the dialog',
    [...['x', 'h', 'p', 'Backspace', 'ArrowLeft', 'ArrowRight', ' ', 'PageUp'].map(typed), button('Enter', 'a'), button('Escape', 'b')]);

  // ---- Text from the on-screen keyboard -----------------------------------------------------
  field.value = '';
  textInsert('ab'); textKey('left'); textInsert('X');
  check('text: insert at the caret', field.value === 'aXb', field.value);
  textKey('backspace');
  check('text: backspace before the caret', field.value === 'ab', field.value);
  textKey('right'); textInsert('!');
  check('text: caret right', field.value === 'ab!', field.value);
  let submitted = false;
  field.addEventListener('textsubmit', () => { submitted = true; });
  textKey('enter');
  check('text: Enter raises textsubmit', submitted);
  sent.length = 0;
  press('r3');
  const kb = lastSent('text.keyboard');
  check('R3 on a field asks for the keyboard, password', kb && kb.password === true && kb.field === 'Password for [Network]', JSON.stringify(kb));

  // The keyboard opens across the bottom, always (from 0.48 of the height down): a field it
  // would cover goes up above it with its screen, and back down once it closes.
  textKeyboardAt(0.48);
  check('keyboard at the bottom: a field high on the screen stays where it is', !document.querySelector('[data-kb-lift]'));
  const low = document.createElement('div');
  low.style.cssText = 'position: absolute; left: 100px; top: 900px; width: 600px; height: 60px';
  low.innerHTML = '<input type="text" aria-label="Low field" style="width: 500px; height: 50px">';
  $('stage').appendChild(low);
  low.firstChild.focus();
  const lowBottom = low.firstChild.getBoundingClientRect().bottom;
  textKeyboardAt(0.48);
  const lift = /translateY\(-(\d+)px\)/.exec(low.style.transform);
  const s = $('stage').getBoundingClientRect().height / 1080;
  check('keyboard at the bottom: a low field goes up above it, with its screen', low.dataset.kbLift === '' && lift && lowBottom - lift[1] * s <= innerHeight * 0.48 - 23 * s,
    `${low.style.transform} ${lowBottom} ${innerHeight}`);
  textKeyboardAt(null);
  check('keyboard closed: the low field comes back down', low.dataset.kbLift === undefined && low.style.transform === '');
  low.remove();
  field.blur();
  field.remove();

  // No field: the same keys are the launcher's again.
  pressed.length = 0;
  key('x');
  check('no field: x is the X button', pressed[0] === 'x', pressed.join(','));
});
