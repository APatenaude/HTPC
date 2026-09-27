'use strict';
// The page's own checks, in a plain browser: index.html#selftest. Results go into the page
// (#selftest-results) and the title ("SELFTEST PASS" / "SELFTEST FAIL n"), for headless Edge:
//   msedge --headless=new --dump-dom file:///.../launcher/ui/index.html#selftest
// Covers what the host cannot see: the text-field key guard, text from the on-screen keyboard,
// X and A on an alert's row in the Home menu, Home landing on an alert's row, the crowded menu.

(async function () {
  const tick = () => new Promise((r) => setTimeout(r, 0));
  const results = [];
  const check = (name, ok, detail) => results.push({ name, ok: !!ok, detail: detail || '' });

  // What the page sends to the host: without a host, send() logs 'to host', msg.
  const sent = [];
  const log = console.log;
  console.log = (...args) => { if (args[0] === 'to host') sent.push(args[1]); else log(...args); };
  const lastSent = (type) => [...sent].reverse().find((m) => m.type === type);

  // press() calls, seen through the global binding the keydown handler uses.
  const pressed = [];
  const realPress = press;
  press = (b) => { pressed.push(b); realPress(b); };   // eslint-disable-line no-global-assign

  function key(k) {
    const e = new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true });
    (document.activeElement || document.body).dispatchEvent(e);
    return e;
  }

  // ---- Key guard ---------------------------------------------------------------------------
  reset('home');
  const field = document.createElement('input');
  field.type = 'password';
  field.setAttribute('aria-label', 'Password for [Network]');
  $('stage').appendChild(field);
  field.focus();
  for (const k of ['x', 'h', 'p', 'Backspace', 'ArrowLeft', 'ArrowRight', ' ', 'PageUp']) {
    pressed.length = 0;
    const e = key(k);
    check(`key guard: "${k}" stays in the field`, pressed.length === 0 && !e.defaultPrevented, pressed.join(','));
  }
  for (const [k, b] of [['Enter', 'a'], ['Escape', 'b']]) {
    pressed.length = 0;
    key(k);
    check(`key guard: ${k} still works the dialog (${b})`, pressed[0] === b, pressed.join(','));
  }

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
  check('R3 on a field asks for the keyboard, password, with its rectangle', kb && kb.password === true && kb.rect && kb.rect.w > 0 && kb.field === 'Password for [Network]', JSON.stringify(kb));
  field.blur();
  field.remove();

  // No field: the same keys are the launcher's again.
  pressed.length = 0;
  key('x');
  check('no field: x is the X button', pressed[0] === 'x', pressed.join(','));

  // ---- The Home menu's alert rows -------------------------------------------------------------
  const tiles = state.tiles;
  for (const t of tiles) t.running = true;          // 6 open apps
  state.current = 'jellyfin';
  noticeUpdate({
    toasts: [], pills: [],
    rows: [{ id: 'app:stremio', title: 'Stremio closed unexpectedly', body: 'It stopped working and closed.', glyph: 'warn', tone: 'bad', action: 'Reopen' }],
  });
  reset('home');
  go('menu');
  const row = $('menu').querySelector('[data-alert="app:stremio"]');
  check('menu: the alert row is there', !!row);
  setFocus(row);
  sent.length = 0;
  press('x');
  check('menu: X on the alert row dismisses it', lastSent('alerts.dismiss') && lastSent('alerts.dismiss').id === 'app:stremio', JSON.stringify(sent));
  check('menu: ... and closes no app', state.view === 'menu' && !lastSent('close'), state.view);

  noticeUpdate({ toasts: [], pills: [], rows: [{ id: 'app:stremio', title: 'Stremio closed unexpectedly', glyph: 'warn', tone: 'bad', action: 'Reopen' }] });
  setFocus($('menu').querySelector('[data-alert="app:stremio"]'));
  sent.length = 0;
  press('a');
  check('menu: A on the alert row runs its action', lastSent('alerts.act') && lastSent('alerts.act').id === 'app:stremio', JSON.stringify(sent));

  setFocus($('menu').querySelector('[data-close="youtube"]'));
  press('x');
  check('menu: X on an app row still asks to close that app', state.view === 'confirm' && state.confirm.id === 'youtube', state.view);
  back();

  // The crowded menu: 6 apps and an alert row fit without scrolling.
  const panel = $('menu-panel');
  check('menu: 6 apps + an alert row fit', panel.scrollHeight <= panel.clientHeight + 1, `${panel.scrollHeight} > ${panel.clientHeight}`);
  const settingsQuick = panel.querySelector('[data-id="q-settings"]');
  const hintsBox = panel.querySelector('.hints').getBoundingClientRect();
  check('menu: the last row is above the button hints', settingsQuick.getBoundingClientRect().bottom <= hintsBox.top + 1);

  // ---- Home lands on an actionable card's row ----------------------------------------------------
  reset('home');
  state.memory.menu = 'volume';
  noticeUpdate({
    toasts: [{ id: 'internet', title: 'No internet', glyph: 'wifi', tone: 'warn', key: 'Home', action: 'Wi-Fi settings' }],
    rows: [{ id: 'internet', title: 'No internet', glyph: 'wifi', tone: 'warn', action: 'Wi-Fi settings' }], pills: [],
  });
  press('home');
  check('Home with an actionable card up: the menu opens on its row', focusedEl() && focusedEl().dataset.id === 'alert:internet', focusedEl() && focusedEl().dataset.id);
  back();
  reset('home');
  state.memory.menu = 'volume';
  noticeUpdate({ toasts: [{ id: 'phone', title: 'Phone remote connected', glyph: 'phone', tone: 'info' }], rows: [], pills: [] });
  press('home');
  check('Home with only a passing card: the remembered focus', focusedEl() && focusedEl().dataset.id === 'volume', focusedEl() && focusedEl().dataset.id);
  back();

  // ---- Cards --------------------------------------------------------------------------------------
  noticeUpdate({ toasts: [
    { id: 'a', title: 'Newest', tone: 'bad' }, { id: 'b', title: 'Older', tone: 'info' },
  ], rows: [], pills: [{ id: 'updates', text: '4 updates', glyph: 'download', tone: 'warn' }] });
  const cards = [...$('toasts').children].map((c) => c.dataset.key);
  check('cards: newest first', cards[0] === 'alert:a' && cards[1] === 'alert:b', cards.join(','));
  toast('A message from the page');
  check('cards: the page\'s own message shows too', [...$('toasts').children].some((c) => c.textContent.includes('A message from the page')));
  reset('home');
  check('status bar: the pill shows', $('status').textContent.includes('4 updates'));

  // ---- Wi-Fi (wifi.js) ------------------------------------------------------------------------------
  const asked = [], toasts = [];
  const box = document.createElement('div');
  $('stage').appendChild(box);
  const draw = () => { box.innerHTML = WifiUI.html(); WifiUI.afterRender(); };
  const node = (id) => box.querySelector(`[data-id="${id}"]`);
  WifiUI.demo('wifi');   // online only through Wi-Fi
  sent.length = 0;
  WifiUI.start({ changed: draw, ask: (q) => asked.push(q), toast: (t) => toasts.push(t) });
  check('Wi-Fi: shown, the host starts scanning', lastSent('wifi.watch') && lastSent('wifi.watch').on === true);
  draw();
  check('Wi-Fi: the network in use is at the top', node('wifi-current') && node('wifi-current').textContent.includes('Connected'));
  WifiUI.press('a', node('wifi-radio'));
  check('Wi-Fi: switch off while online only through it asks first', asked.length === 1 && /Turn Wi-Fi off/.test(asked[0].title) && !lastSent('wifi.radio'));
  WifiUI.press('x', node('wifi-current'));
  check('Wi-Fi: X on the network in use asks before forgetting it', asked.length === 2 && /Forget/.test(asked[1].title) && /offline/.test(asked[1].text));
  WifiUI.press('a', node('wifi-net:3'));
  check('Wi-Fi: a WEP network is refused with the reason, no form', !WifiUI.joining && toasts.some((t) => /WEP/.test(t)));
  WifiUI.press('a', node('wifi-net:0'));
  draw();
  const pw = document.getElementById('wifi-password-input');
  check('Wi-Fi: a locked network opens the password form', WifiUI.joining && pw && pw.type === 'password' && pw.autocomplete === 'off');
  check('Wi-Fi: ... and the keyboard for its field', lastSent('text.keyboard') && lastSent('text.keyboard').password === true);
  pw.focus(); textInsert('hunter22');   // as the on-screen keyboard and the phone do
  WifiUI.press('a', node('wifi-go'));
  const j = lastSent('wifi.join');
  check('Wi-Fi: Join sends the name and the password', j && j.ssid === '[Network name 2]' && j.password === 'hunter22' && !j.hidden);
  WifiUI.handle({ type: 'wifi.result', ssid: '[Network name 2]', ok: false, reason: 'wrong-password', text: 'Wrong password. Check it and try again.' });
  draw();
  check('Wi-Fi: wrong password: the form stays, says so, field emptied', WifiUI.joining && box.textContent.includes('Wrong password') && document.getElementById('wifi-password-input').value === '');
  WifiUI.press('b', node('wifi-password'));
  check('Wi-Fi: B leaves the form and the keyboard', !WifiUI.joining && lastSent('text.done'));
  WifiUI.press('a', node('wifi-hidden'));
  draw();
  WifiUI.press('right', node('wifi-security'));
  document.getElementById('wifi-name-input').focus(); textInsert('[Hidden]');
  document.getElementById('wifi-password-input').focus(); textInsert('abcdefgh');
  WifiUI.press('a', node('wifi-go'));
  const h = lastSent('wifi.join');
  check('Wi-Fi: a hidden network: name, security, password', h && h.hidden === true && h.ssid === '[Hidden]' && h.security === 'wpa3sae' && h.password === 'abcdefgh');
  WifiUI.handle({ type: 'wifi.result', ssid: '[Hidden]', ok: true, reason: 'ok', text: 'Connected to [Hidden]' });
  check('Wi-Fi: joined: back to the list', !WifiUI.joining && toasts.includes('Connected to [Hidden]'));
  WifiUI.demo('location');
  draw();
  WifiUI.press('a', node('wifi-allow'));
  check('Wi-Fi: location refused: the Allow row asks the host', !!lastSent('wifi.allowLocation'));
  WifiUI.stop();
  check('Wi-Fi: hidden, the host stops scanning', lastSent('wifi.watch').on === false);
  box.remove();

  // In Settings itself: what is typed survives the pane being redrawn (a state push, the clock).
  WifiUI.demo('ethernet');
  state.section = 'wifi';
  reset('settings');
  setFocus($('settings').querySelector('[data-id="wifi-net:0"]'));
  press('a');
  await tick();
  const typed = () => document.getElementById('wifi-password-input');
  check('Settings › Wi-Fi: A on a locked network: the password field, focused', typed() && document.activeElement === typed());
  textInsert('secret1');
  onHost({ type: 'state', volume: 40 });          // a host push: app.js redraws the pane
  render();                                        // the minute clock
  await tick();
  check('Settings › Wi-Fi: typed text survives a state push and the clock', typed() && typed().value === 'secret1', typed() && typed().value);
  check('Settings › Wi-Fi: ... and the field keeps the focus (keyboard and phone text land there)', document.activeElement === typed());
  textInsert('!');
  setFocus($('settings').querySelector('[data-id="wifi-reveal"]'));
  press('a');                                      // Show password: redraws with type=text
  await tick();
  check('Settings › Wi-Fi: Show password keeps the text', typed() && typed().type === 'text' && typed().value === 'secret1!', typed() && typed().value);
  WifiUI.handle({ type: 'wifi.state', adapter: true, radio: 'on', location: 'ok', wired: null, current: null, networks: [], wifiInternet: false, askRadioOff: false });
  await tick();
  check('Settings › Wi-Fi: a Wi-Fi state push while typing keeps the text', typed() && typed().value === 'secret1!');
  setFocus($('settings').querySelector('[data-id="wifi-go"]'));
  sent.length = 0;
  press('a');
  check('Settings › Wi-Fi: Join sends what was typed', lastSent('wifi.join') && lastSent('wifi.join').password === 'secret1!');
  press('b');
  reset('home');
  await tick();

  // ---- Report -------------------------------------------------------------------------------------
  press = realPress;   // eslint-disable-line no-global-assign
  console.log = log;
  const failed = results.filter((r) => !r.ok);
  const pre = document.createElement('pre');
  pre.id = 'selftest-results';
  pre.textContent = results.map((r) => `${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.ok ? '' : '  [' + r.detail + ']'}`).join('\n');
  document.body.appendChild(pre);
  document.title = failed.length ? `SELFTEST FAIL ${failed.length}` : `SELFTEST PASS ${results.length}`;
})();
