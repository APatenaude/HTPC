'use strict';
// The page's own checks, in a plain browser: index.html#selftest. Results go into the page
// (#selftest-results) and the title ("SELFTEST PASS" / "SELFTEST FAIL n"), for headless Edge:
//   msedge --headless=new --dump-dom file:///.../launcher/ui/index.html#selftest
// Covers what the host cannot see: the text-field key guard, text from the on-screen keyboard,
// X and A on an alert's row in the Home menu, Home landing on an alert's row, the crowded menu,
// moving around Settings and changing a value there only once A has picked its row, the
// interface sounds (rendered offline; which sound a press picks; none while hidden).

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

  // The interface sounds (sounds.js), rendered offline, a channel each; checked near the end.
  // Started first, so the render runs beside the other checks: waiting for it with nothing else
  // to do would let headless Edge's virtual time (--virtual-time-budget) run out first.
  async function renderSounds() {
    const names = Object.keys(SOUNDS), rate = 48000;
    const offline = new OfflineAudioContext(names.length, rate * 1.5, rate);
    const merger = offline.createChannelMerger(names.length);
    merger.connect(offline.destination);
    names.forEach((name, i) => {
      const out = offline.createGain();
      out.connect(merger, 0, i);
      SOUNDS[name](offline, out, 0, {});
    });
    return offline.startRendering();
  }
  const soundsRendered = renderSounds().catch(() => null);   // null: the check below fails

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

  // ---- Settings › Bluetooth (settings-bluetooth.js) ------------------------------------------------
  EXT.sections.bluetooth.demo();
  state.section = 'bluetooth';
  sent.length = 0;
  reset('settings');
  const btNode = (id) => $('settings').querySelector(`[data-id="${id}"]`);
  check('Bluetooth: shown, the host lists devices', lastSent('bt.watch') && lastSent('bt.watch').on === true);
  check('Bluetooth: paired headphones say sound plays there', btNode('bt-paired:0') && btNode('bt-paired:0').textContent.includes('sound plays here'));
  setFocus(btNode('bt-new'));
  press('a');
  check('Bluetooth: Pair a new device looks for devices', lastSent('bt.scan') && lastSent('bt.scan').on === true && !!btNode('bt-near:0'));
  setFocus(btNode('bt-near:0'));
  press('a');
  check('Bluetooth: A on a nearby device pairs it', lastSent('bt.pair') && lastSent('bt.pair').id === 'n1' && !!btNode('bt-pairing'));
  onHost({ type: 'bt.pin', id: 'n1', name: '[Keyboard]', pin: '482915' });
  check('Bluetooth: a keyboard\'s PIN is shown to type', btNode('bt-pin') && btNode('bt-pin').textContent.includes('482915'));
  onHost({ type: 'bt.result', id: 'n1', ok: true, text: '[Keyboard] paired' });
  check('Bluetooth: paired: back to the list, looking stops', !btNode('bt-pin') && !btNode('bt-pairing') && lastSent('bt.scan').on === false);
  setFocus(btNode('bt-paired:1'));
  press('x');
  check('Bluetooth: X on a paired device asks before removing it', state.view === 'ask' && !lastSent('bt.forget'));
  press('b');
  reset('home');
  check('Bluetooth: left, the host stops', lastSent('bt.watch').on === false);

  // ---- Settings › Phone remote (phone-settings.js): Forget asks first -------------------------------
  EXT.sections.phone.demo();
  state.section = 'phone';
  sent.length = 0;
  reset('settings');
  for (const [id, what] of [['phone-b2', 'a phone'], ['phone-c3', 'a Shortcut key']]) {
    setFocus($('settings').querySelector(`[data-id="${id}"]`));
    press('a');
    const dialog = state.view === 'ask' && focusedEl() && focusedEl().dataset.id === 'ask-no';
    check(`Phone remote: Forget on ${what} asks first, Cancel focused`, dialog && !lastSent('phone.forget'));
    press('a');
    check(`Phone remote: ... Cancel keeps ${what}`, state.view === 'settings' && !lastSent('phone.forget'));
  }
  setFocus($('settings').querySelector('[data-id="phone-c3"]'));
  press('a');
  setFocus($('ask').querySelector('[data-id="ask-yes"]'));
  press('a');
  check('Phone remote: ... Forget forgets it', lastSent('phone.forget') && lastSent('phone.forget').id === 'c3');
  reset('home');
  await tick();
  // ---- Settings: opening, moving, changing a value ----------------------------------------------
  const sNode = (id) => $('settings').querySelector(`[data-id="${id}"]`);
  const focusId = () => focusedEl() && focusedEl().dataset.id;
  EXT.sections.sound.demo();
  state.section = 'sound';
  state.memory.settings = 'set-volume';
  reset('home');
  activate({ dataset: { act: 'settings' } });                 // the Settings button
  check('Settings opens on the section list, on the last section', focusId() === 's-sound', focusId());
  press('right');
  check('Settings: right from the list goes into the section', focusId() === 'snd-output', focusId());
  press('up');
  check('Settings: up on the first row stays there', focusId() === 'snd-output', focusId());
  setFocus(sNode('snd-test'));
  press('down');
  check('Settings: down on the last row stays there', focusId() === 'snd-test', focusId());
  const vol = state.volume;
  setFocus(sNode('set-volume'));
  press('right');
  check('Settings: right on a slider not picked changes nothing', state.volume === vol && focusId() === 'set-volume', `${state.volume} ${focusId()}`);
  press('left');
  check('Settings: left on a slider not picked goes back to the list, unchanged', state.volume === vol && focusId() === 's-sound', `${state.volume} ${focusId()}`);
  setFocus(sNode('set-volume'));
  press('a');
  check('Settings: A picks the slider', sNode('set-volume').classList.contains('editing') && /Done/.test($('settings-hints').textContent));
  press('left'); press('left');
  check('Settings: picked, left lowers the volume', state.volume === vol - 10 && focusId() === 'set-volume', `${state.volume} ${focusId()}`);
  press('b');
  check('Settings: B puts the slider down, the value kept, the focus stays', state.volume === vol - 10 && focusId() === 'set-volume' && !sNode('set-volume').classList.contains('editing'));
  press('left');
  check('Settings: ... then left goes to the list again', focusId() === 's-sound', focusId());
  setFocus(sNode('snd-output'));
  sent.length = 0;
  press('right');
  check('Settings: the output is not switched by moving over it', !lastSent('sound.output'));
  press('a'); press('right');
  check('Settings: picked with A, right switches the output', lastSent('sound.output') && lastSent('sound.output').id === '2');
  press('a');
  press('up');
  check('Settings: up on the first row of a section stays in it', focusId() === 'snd-output', focusId());
  setFocus(sNode('s-sleep'));
  press('up');
  check('Settings: up on the first section stays there', focusId() === 's-sleep' && state.section === 'sleep', focusId());

  // Sleep & power: a stepper waits for A; the toggle flips with A only.
  setFocus(sNode('set-idleMinutes'));
  const idle = state.prefs.idleMinutes;
  press('right');
  check('Sleep: right on a stepper not picked changes nothing', state.prefs.idleMinutes === idle, String(state.prefs.idleMinutes));
  setFocus(sNode('set-idleMinutes'));
  press('a'); press('right');
  check('Sleep: A then right steps the stepper', state.prefs.idleMinutes !== idle && lastSent('setting').key === 'idleMinutes');
  press('a');
  const awake = state.prefs.stayAwakeWhilePlaying;
  setFocus(sNode('set-stayAwakeWhilePlaying'));
  press('right');
  check('Sleep: right on a toggle does not flip it', state.prefs.stayAwakeWhilePlaying === awake);
  setFocus(sNode('set-stayAwakeWhilePlaying'));
  press('a');
  check('Sleep: A flips the toggle', state.prefs.stayAwakeWhilePlaying === !awake);

  // Controller: two columns; left from the right column goes beside it, not to the list.
  EXT.sections.controller.demo();
  setFocus(sNode('s-controller'));
  const speed = prefValue('pointerSpeed', 5), kbd = prefValue('showKeyboardAutomatically', true);
  setFocus(sNode('set-pointerSpeed'));
  press('left');
  check('Controller: left on a speed not picked goes beside it, unchanged', prefValue('pointerSpeed', 5) === speed && focusId() === 'pad-test', focusId());
  press('left');
  check('Controller: ... left again, nothing beside: the list', focusId() === 's-controller', focusId());
  setFocus(sNode('set-showKeyboardAutomatically'));
  press('right');
  check('Controller: right on a toggle does not flip it', prefValue('showKeyboardAutomatically', true) === kbd);

  // TV: buttons side by side are reached with left and right.
  state.tv = TvUi.demo('roku');
  setFocus(sNode('s-tv'));
  setFocus(sNode('tv-test'));
  press('right');
  check('TV: right from Test goes to Find it again', focusId() === 'tv-refresh', focusId());
  press('left');
  check('TV: left comes back to Test', focusId() === 'tv-test', focusId());
  press('left');
  check('TV: left from Test (nothing beside it) goes to the list', focusId() === 's-tv', focusId());
  setFocus(sNode('tv-test'));
  const main = $('settings').querySelector('.spane main');
  const ring = sNode('tv-test').getBoundingClientRect(), paneBox = main.getBoundingClientRect();
  check('TV: the focused bottom button shows whole above the hints', ring.bottom + 4 <= paneBox.bottom, `${ring.bottom} > ${paneBox.bottom}`);
  setFocus(sNode('tv-input'));
  press('right');
  check('TV: the input is not changed by moving over it', state.tv.profile.input === 1);
  reset('home');

  // ---- Settings: a value changing redraws in place; the sleep timer set right there ----------------
  state.section = 'sleep';
  reset('settings');
  const navSleep = sNode('s-sleep');
  press('down');
  check('Settings: moving through the sections keeps the list (its ring not drawn again)', sNode('s-sleep') === navSleep && sNode('s-tv').classList.contains('focused') && state.section === 'tv');
  press('up');
  setFocus(sNode('set-idleMinutes'));
  const idleRow = sNode('set-idleMinutes'), timerRow = sNode('set-timer');
  press('a'); press('right');
  check('Settings: a stepper changing keeps its row (the ring is not drawn again)', sNode('set-idleMinutes') === idleRow && idleRow.classList.contains('focused') && idleRow.classList.contains('editing'));
  press('a');
  setFocus(sNode('set-stayAwakeWhilePlaying'));
  const awakeRow = sNode('set-stayAwakeWhilePlaying');
  press('a');
  check('Settings: a toggle flipping keeps its row', sNode('set-stayAwakeWhilePlaying') === awakeRow && awakeRow.classList.contains('focused'));
  state.timer = null;
  setFocus(timerRow);
  press('a');
  check('Sleep timer: A picks the row, it does not open the timer screen', state.view === 'settings' && timerRow.classList.contains('editing'));
  press('right');
  check('Sleep timer: right sets 15 min, right there', state.view === 'settings' && state.timer && state.timer.label === '15 min' && lastSent('timer').minutes === 15 && sNode('set-timer') === timerRow);
  press('right');
  check('Sleep timer: right again, 30 min', state.timer && state.timer.label === '30 min' && lastSent('timer').minutes === 30);
  press('left'); press('left');
  check('Sleep timer: left back to Off', state.timer === null && lastSent('timer').minutes === 0);
  press('left');
  check('Sleep timer: left from Off goes round to This video ends', state.timer && state.timer.endsAt === 'video');
  press('right');
  press('a');
  // A list that scrolls itself keeps the focus ring of its last row whole.
  WifiUI.demo('wifi');
  setFocus(sNode('s-wifi'));
  const rows = [...$('settings').querySelectorAll('.wifi-scroll [data-nav]')];
  setFocus(rows[rows.length - 1]);
  const wl = $('settings').querySelector('.wifi-scroll').getBoundingClientRect(), wr = rows[rows.length - 1].getBoundingClientRect();
  check('Wi-Fi: the last row of the list shows whole, its ring too', wr.bottom + 4 <= wl.bottom && wr.top >= wl.top, `${wr.bottom} / ${wl.bottom}`);
  setFocus(rows[0]);
  const w0 = rows[0].getBoundingClientRect(), wl0 = $('settings').querySelector('.wifi-scroll').getBoundingClientRect();
  check('Wi-Fi: back on the first row, its ring shows whole', w0.top - 4 >= wl0.top, `${w0.top} / ${wl0.top}`);
  reset('home');

  // ---- Home: tiles updated in place, Tile options, moving a tile, an app installing -------------
  const tileEl = (id) => $('tiles').querySelector(`[data-id="tile:${id}"]`);
  state.current = null; state.backdrop = null;
  onHost({ type: 'state', running: ['jellyfin'] });
  reset('home');
  const jf = tileEl('jellyfin'), yt = tileEl('youtube');
  setFocus(jf);
  press('x');
  check('Home: X on a running tile asks to close it', state.view === 'confirm');
  press('a');
  check('Home: closed, back on home without its entrance again', state.view === 'home' && $('home').classList.contains('stay') && lastSent('close').id === 'jellyfin');
  onHost({ type: 'state', running: [] });
  check('Home: the app gone, only its tile changes (same elements, no badge)', tileEl('jellyfin') === jf && tileEl('youtube') === yt && !jf.querySelector('.badge'));

  setFocus(tileEl('moonlight'));
  press('start');
  const panelR = $('tileopts').querySelector('.to-panel').getBoundingClientRect(), mR = tileEl('moonlight').getBoundingClientRect();
  check('Tile options: one hint bar (the home one hidden under it)', getComputedStyle($('home-hints')).visibility === 'hidden' && !!$('tileopts').querySelector('.hints'));
  check('Tile options: beside the tile, level with it', panelR.left >= mR.right && panelR.top < mR.bottom && panelR.bottom > mR.top, `${panelR.left},${panelR.top} / ${mR.right},${mR.top}`);
  press('b');
  setFocus(tileEl('jellyfin'));
  press('start');
  const p2 = $('tileopts').querySelector('.to-panel').getBoundingClientRect(), jR = tileEl('jellyfin').getBoundingClientRect();
  check('Tile options: at the right edge, on the tile\'s left and on screen', p2.right <= jR.left && p2.left >= 0, `${p2.left}-${p2.right} / ${jR.left}`);
  press('b');

  setFocus(tileEl('moonlight'));
  press('start');
  press('a');                                       // Move
  const mv = tileEl('moonlight');
  check('Move: the tile is marked, the "+" tile stays (dimmed)', mv.classList.contains('moving') && tileEl('+add') && tileEl('+add').classList.contains('dim'));
  press('right');
  check('Move: it trades places with its neighbour, the same element', state.tiles.findIndex((t) => t.id === 'moonlight') === 5 && tileEl('moonlight') === mv && mv.classList.contains('focused'));
  press('b');
  check('Move: B puts it back', state.tiles.findIndex((t) => t.id === 'moonlight') === 4 && !state.moving);

  // "Install and add to home": a tile shows it installing, then that it did not install.
  const kodi = { id: 'kodi', name: 'Kodi', glyph: 'tv', color: '#5AB0FF', desc: '', type: 'app', state: 'install', canUninstall: true };
  onHost({ type: 'library.catalog', apps: [kodi], sites: [] });
  state.libraryAvailable = true;
  EXT.actions.libcard(null, 'kodi');
  EXT.actions.installBtn(null, 'home');
  check('Install: asked with add to home', lastSent('library.install').id === 'kodi' && lastSent('library.install').addToHome === true);
  onHost({ type: 'library.progress', current: { id: 'kodi', name: 'Kodi', action: 'install', phase: 'download', percent: 40 }, pending: [] });
  reset('home');
  check('Install: home shows a tile installing it, with the progress', tileEl('~kodi') && /Downloading/.test(tileEl('~kodi').textContent) && !!tileEl('~kodi').querySelector('.pbar'));
  onHost({ type: 'library.progress', current: null, pending: [] });
  onHost({ type: 'library.catalog', apps: [kodi], sites: [] });
  check('Install: it did not install: the tile says so', tileEl('~kodi') && tileEl('~kodi').classList.contains('failed') && /Didn/.test(tileEl('~kodi').textContent));
  setFocus(tileEl('~kodi'));
  check('Install: its hints: A tries again, X removes', /Try again/.test($('home-hints').textContent) && /Remove/.test($('home-hints').textContent));
  press('x');
  check('Install: X takes the tile away', !tileEl('~kodi'));

  // ---- Button maps: the list, the editor's preset row and the picker ---------------------------
  mapsDemo();
  reset('home');
  go('maps');
  const mNode = (id) => $('maps').querySelector(`[data-id="${id}"]`);
  press('up');
  check('Button maps: up on the first app stays there', focusId() === 'm-youtube', focusId());
  for (let i = 0; i < 12; i++) press('down');
  const mlist = $('maps').querySelector('.mlist').getBoundingClientRect(), last = mNode('m-_other').getBoundingClientRect();
  check('Button maps: down to the last app, it stays and shows whole', focusId() === 'm-_other' && last.bottom <= mlist.bottom, `${focusId()} ${last.bottom} > ${mlist.bottom}`);
  setFocus(mNode('m-twitch'));
  press('a');
  const bNode = (id) => $('buttons').querySelector(`[data-id="${id}"]`);
  check('Editor: opens on a button, the preset row says how many differ', state.view === 'buttons' && focusId() === 'b-a' && /2 buttons changed/.test(bNode('b-preset').textContent));
  press('up'); press('up'); press('up');
  check('Editor: up goes to the preset row and stops there', focusId() === 'b-preset' && /Change preset/.test($('buttons').querySelector('.hints').textContent), focusId());
  press('left');
  check('Editor: left on the preset row changes nothing', mapApp('twitch').map.preset === 'mouse' && focusId() === 'b-preset');
  press('a');
  check('Editor: A on the preset row opens the presets, on the current one', focusId() === 'bp-mouse');
  press('down'); press('a');
  check('Editor: another preset with buttons changed asks first', state.view === 'ask' && mapApp('twitch').map.preset === 'mouse');
  press('b'); press('b');
  check('Editor: cancelled, B closes the presets as they were', state.view === 'buttons' && focusId() === 'b-preset' && mapApp('twitch').map.preset === 'mouse', focusId());
  setFocus(bNode('b-start'));
  press('a');
  check('Editor: A on a button opens its choice, with LB RB for the categories', !!$('buttons').querySelector('.bcats .key') && /LB\s*RB\s*Category/.test($('buttons').querySelector('.hints').textContent));
  const cat = () => $('buttons').querySelector('.bcat.on').textContent;
  press('rb');
  check('Editor: RB: the next category', cat() === 'Mouse', cat());
  press('lb'); press('lb');
  check('Editor: LB: the one before (round to the last)', cat() === 'Nothing', cat());
  press('b');
  check('Editor: B closes the choice, back on the button', !maps.picking && focusId() === 'b-start', focusId());
  reset('home');

  // ---- The Home menu over an app says what its buttons do ---------------------------------------
  state.tiles.find((t) => t.id === 'twitch').running = true;
  state.current = 'twitch';
  go('menu');
  const card = $('menu-app').textContent;
  check('Menu over an app: its buttons beside the panel, and how to go back', /Twitch/.test(card) && /Enter/.test(card) && /Back to Twitch/.test(card) && /This menu/.test(card), card.slice(0, 80));
  back();
  state.current = null;
  go('menu');
  check('Menu over the home screen: no app card', $('menu-app').textContent === '');
  reset('home');

  // ---- Interface sounds (sounds.js) -------------------------------------------------------------
  // Each sound rendered offline (no audio device), at Medium: heard, never loud, short. The
  // render was started first thing (renderSounds, above) and has long finished by now.
  const longest = { move: 0.06, select: 0.25, back: 0.25, open: 0.5, close: 0.45, bump: 0.2, on: 0.2, off: 0.2, notice: 1 };
  const rendered = await Promise.race([soundsRendered, tick().then(() => null)]);
  check('Sounds: rendered offline', rendered);
  const level = {};
  if (rendered) Object.keys(SOUNDS).forEach((name, i) => {
    const d = rendered.getChannelData(i);
    let peak = 0, end = 0;
    for (let s = 0; s < d.length; s++) { const a = Math.abs(d[s]); if (a > peak) peak = a; if (a > 0.001) end = s; }
    level[name] = peak;
    const ms = Math.round(end / rendered.sampleRate * 1000), db = (20 * Math.log10(peak)).toFixed(0);
    check(`Sound "${name}": ${ms} ms, peak ${db} dBFS`, peak > 0.03 && peak < 0.3 && ms >= 5 && ms <= longest[name] * 1000, `peak ${peak}, ${ms} ms`);
  });
  check('Sound: the move tick is the softest', rendered && Object.keys(level).every((n) => n === 'move' || level.move < level[n]), JSON.stringify(level));

  // What a press did picks the sound (heard: what played, no rate limit in the way).
  const heard = async (fn) => {
    await tick();
    sounds.heard.length = 0; sounds.last = {}; sounds.lastMovePress = -1e9;
    fn();
    await tick();
    return sounds.heard.map((h) => h.name).join(',');
  };
  state.prefs.interfaceSounds = 'low';
  reset('home');
  setFocus(tileEl('youtube'));
  check('Sounds: the focus moving ticks', await heard(() => press('right')) === 'move');
  check('Sounds: Home on the home screen: the menu opens', await heard(() => press('home')) === 'open');
  check('Sounds: B: back', await heard(() => press('b')) === 'back');
  setFocus(tileEl('youtube'));
  check('Sounds: A on a tile: the app opening', await heard(() => press('a')) === 'open');
  hideOpening();
  EXT.sections.sound.demo();
  state.section = 'sound';
  reset('settings');
  setFocus(sNode('snd-output'));
  check('Sounds: up at the top of a list: the end, a thud', await heard(() => press('up')) === 'bump');
  state.section = 'sleep';
  reset('settings');
  state.prefs.stayAwakeWhilePlaying = false;
  render();
  setFocus(sNode('set-stayAwakeWhilePlaying'));
  check('Sounds: A on a toggle: on, then off', await heard(() => press('a')) === 'on' && await heard(() => press('a')) === 'off');
  const moves = [0, 400, 510, 620, 730, 840, 950].map((t) => soundMoveGain(t)).join(',');
  check('Sounds: a direction held: a tick, then every other repeat at half the level', moves === '1,1,0,0.5,0,0.5,0', moves);
  check('Sounds: a list\'s end held: a thud once per 350 ms', await heard(() => [5000, 5110, 5220, 5330, 5440].forEach((t) => soundsDo('bump', t))) === 'bump,bump');

  // None while the launcher is hidden or blank (an app in front, standby), or a field has the focus.
  reset('home');
  onHost({ type: 'blank' });
  check('Sounds: none while blank (an app in front, standby)', await heard(() => press('right')) === '');
  onHost({ type: 'show', view: 'home' });
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
  const whileHidden = await heard(() => { press('left'); soundsDo('notice'); });
  delete document.hidden;
  check('Sounds: none while the launcher is hidden', whileHidden === '', whileHidden);
  const typing = document.createElement('input');
  $('stage').appendChild(typing);
  typing.focus();
  check('Sounds: none while a text field has the focus', await heard(() => press('right')) === '');
  typing.remove();
  onHost({ type: 'blank' });
  check('Sounds: the Home menu coming up over an app', await heard(() => onHost({ type: 'show', view: 'menu', current: 'twitch' })) === 'open');
  state.current = null;
  reset('home');
  check('Sounds: an alert\'s card arriving', await heard(() => noticeUpdate({ toasts: [{ id: 'snd', title: 'Test', tone: 'info' }], rows: [], pills: [] })) === 'notice');
  noticeUpdate({ toasts: [], rows: [], pills: [] });

  // Settings › Sound › Interface sounds: a choice, changed only once A has picked it.
  state.section = 'sound';
  reset('settings');
  const soundsOn = () => sNode('set-interfaceSounds').querySelector('.seg .on').textContent;
  check('Interface sounds: a row in Settings › Sound, on Low', sNode('set-interfaceSounds') && soundsOn() === 'Low');
  setFocus(sNode('set-interfaceSounds'));
  sent.length = 0;
  press('right');
  check('Interface sounds: right on the row not picked changes nothing', state.prefs.interfaceSounds === 'low' && !lastSent('setting'));
  setFocus(sNode('set-interfaceSounds'));
  press('a'); press('right');
  check('Interface sounds: A then right: Medium, saved as the setting', soundsOn() === 'Medium' && lastSent('setting').key === 'interfaceSounds' && lastSent('setting').value === 'medium');
  press('right');
  check('Interface sounds: right again, round to Off', state.prefs.interfaceSounds === 'off' && soundsOn() === 'Off');
  check('Interface sounds: Off is silent', await heard(() => { press('a'); press('down'); }) === '');
  setFocus(sNode('set-interfaceSounds'));
  press('a'); press('left'); press('left'); press('a');
  check('Interface sounds: left twice from Off, back to Low', state.prefs.interfaceSounds === 'low' && lastSent('setting').value === 'low');
  reset('home');
  check('Sounds: headless (no host, no real key): the audio device is never opened', sounds.ctx === null);

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
