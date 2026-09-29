'use strict';
// The page's own checks, in a plain browser: index.html#selftest. Results go into the page
// (#selftest-results) and the title ("SELFTEST PASS" / "SELFTEST FAIL n"), for headless Edge:
//   msedge --headless=new --dump-dom file:///.../launcher/ui/index.html#selftest
// Covers what the host cannot see: the text-field key guard, text from the on-screen keyboard,
// X and A on an alert's row in the Home menu, Home landing on an alert's row, the crowded menu,
// its quick buttons in the order of the home screen's top bar (Settings, then Power),
// Power's Restart and Shut down asking first, moving around Settings and changing a value there only once A has picked its row, the
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
  press = (b, held) => { pressed.push(b); realPress(b, held); };   // eslint-disable-line no-global-assign

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
  // Reopen shows "Opening Stremio", which takes every press but Home and B until the host says
  // the window is up: here, no host.
  check('menu: while "Opening" shows, X does nothing under it', (press('x'), state.view === 'menu' && !lastSent('alerts.dismiss')));
  press('b');
  check('menu: B takes "Opening" away and tells the host', !$('opening').classList.contains('on') && lastSent('launchDismissed') && lastSent('launchDismissed').id === 'stremio' && state.view === 'menu');

  setFocus($('menu').querySelector('[data-close="youtube"]'));
  press('x');
  check('menu: X on an app row still asks to close that app', state.view === 'confirm' && state.confirm.id === 'youtube', state.view);
  back();

  // The crowded menu: 6 apps and an alert row fit without scrolling.
  const panel = $('menu-panel'), rowsBox = panel.querySelector('.panel-scroll');
  check('menu: 6 apps + an alert row fit', rowsBox.scrollHeight <= rowsBox.clientHeight + 1, `${rowsBox.scrollHeight} > ${rowsBox.clientHeight}`);
  const quicks = [...panel.querySelectorAll('.quicks [data-nav]')];
  const hintsBox = panel.querySelector('.hints').getBoundingClientRect();
  check('menu: the last row is above the button hints', quicks[quicks.length - 1].getBoundingClientRect().bottom <= hintsBox.top + 1);

  // Its quick buttons: Settings, then Power, as on the home screen's top bar. The D-pad goes along
  // them in that order and stops at the end; A opens each, and B from there comes back to it.
  const quickIds = quicks.map((e) => e.dataset.id);
  const barIds = [...$('status').querySelectorAll('[data-nav]')].map((e) => e.dataset.id);
  check('menu: Settings then Power, in the order of the home screen\'s top bar',
    barIds.indexOf('settings') >= 0 && barIds.indexOf('settings') < barIds.indexOf('power') &&
    quickIds.join(',') === 'q-buttons,q-timer,q-settings,q-power', `menu ${quickIds.join(',')}; top bar ${barIds.join(',')}`);
  setFocus(panel.querySelector('[data-id="q-timer"]'));
  const walked = [];
  for (let i = 0; i < 3; i++) { press('right'); walked.push(focusedEl() && focusedEl().dataset.id); }
  press('left');
  walked.push(focusedEl() && focusedEl().dataset.id);
  check('menu: right from Timer: Settings, Power, and stays; left: Settings', walked.join(',') === 'q-settings,q-power,q-power,q-settings', walked.join(','));
  press('a');
  const toSettings = state.view;
  press('b');
  check('menu: A on Settings opens Settings, B comes back to it', toSettings === 'settings' && state.view === 'menu' && focusedEl() && focusedEl().dataset.id === 'q-settings',
    `${toSettings}, then ${state.view} on ${focusedEl() && focusedEl().dataset.id}`);
  setFocus(panel.querySelector('[data-id="q-power"]'));
  press('a');
  const toPower = state.view;
  press('b');
  check('menu: A on Power opens Power, B comes back to it', toPower === 'power' && state.view === 'menu' && focusedEl() && focusedEl().dataset.id === 'q-power',
    `${toPower}, then ${state.view} on ${focusedEl() && focusedEl().dataset.id}`);

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
  WifiUI.press('a', node('wifi-net:[Old router]'));
  check('Wi-Fi: a WEP network is refused with the reason, no form', !WifiUI.joining && toasts.some((t) => /WEP/.test(t)));
  WifiUI.press('a', node('wifi-net:[Network name 2]'));
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
  // The host sorts the networks again with each scan: the focus stays on its network, and when
  // that one goes, on the row nearest its place, not back on the section list.
  const wnet = (ssid) => ({ ssid, signal: 50, words: 'good signal', security: 'wpa2psk', password: true, saved: false, connected: false });
  const wstate = (names) => ({ type: 'wifi.state', adapter: true, radio: 'on', location: 'ok', wired: null, current: null, networks: names.map(wnet), wifiInternet: false, askRadioOff: false });
  onHost(wstate(['[Net A]', '[Net B]', '[Net C]']));
  setFocus($('settings').querySelector('[data-id="wifi-net:[Net B]"]'));
  onHost(wstate(['[Net B]', '[Net C]', '[Net A]']));
  check('Settings › Wi-Fi: the list sorted again: the focus stays on its network', focusedEl() && focusedEl().dataset.id === 'wifi-net:[Net B]', focusedEl() && focusedEl().dataset.id);
  onHost(wstate(['[Net C]', '[Net A]']));
  check('Settings › Wi-Fi: its network gone: the nearest row takes the focus, not the section list', focusedEl() && /^wifi-net:/.test(focusedEl().dataset.id), focusedEl() && focusedEl().dataset.id);
  WifiUI.demo('ethernet');
  render();
  setFocus($('settings').querySelector('[data-id="wifi-net:[Network name 2]"]'));
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
  check('Bluetooth: paired headphones say sound plays there', btNode('bt-paired:p1') && btNode('bt-paired:p1').textContent.includes('sound plays here'));
  setFocus(btNode('bt-new'));
  press('a');
  check('Bluetooth: Pair a new device looks for devices', lastSent('bt.scan') && lastSent('bt.scan').on === true && !!btNode('bt-near:n1'));
  setFocus(btNode('bt-near:n1'));
  press('a');
  check('Bluetooth: A on a nearby device pairs it', lastSent('bt.pair') && lastSent('bt.pair').id === 'n1' && !!btNode('bt-pairing'));
  onHost({ type: 'bt.pin', id: 'n1', name: '[Keyboard]', pin: '482915' });
  check('Bluetooth: a keyboard\'s PIN is shown to type', btNode('bt-pin') && btNode('bt-pin').textContent.includes('482915'));
  onHost({ type: 'bt.result', id: 'n1', ok: true, text: '[Keyboard] paired' });
  check('Bluetooth: paired: back to the list, looking stops', !btNode('bt-pin') && !btNode('bt-pairing') && lastSent('bt.scan').on === false);
  setFocus(btNode('bt-paired:p2'));
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

  // ---- Power: Restart and Shut down ask first, Cancel focused ------------------------------------
  const powerCard = (id) => $('power-cards').querySelector(`[data-id="${id}"]`);
  for (const [id, title, yes] of [['restart', 'Restart the box?', 'Restart'], ['shutdown', 'Shut down the box?', 'Shut down']]) {
    reset('home');
    go('power');
    sent.length = 0;
    setFocus(powerCard(id));
    press('a');
    const asked = state.view === 'ask' && focusedEl() && focusedEl().dataset.id === 'ask-no';
    check(`Power: ${id} asks first, Cancel focused`, asked && $('ask').textContent.includes(title) && !lastSent('power'), state.view);
    press('a');
    check(`Power: ... Cancel on ${id} sends nothing`, state.view === 'power' && !lastSent('power'), state.view);
    setFocus(powerCard(id));
    press('a');
    setFocus($('ask').querySelector('[data-id="ask-yes"]'));
    press('a');
    check(`Power: ... ${yes} sends it`, lastSent('power') && lastSent('power').action === id, JSON.stringify(sent));
  }
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
  check('Home: X on a running tile asks to close it, on Cancel', state.view === 'confirm' && focusedEl() && focusedEl().dataset.id === 'confirm-cancel');
  press('left');
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

  // Hold A on a tile to move it: the controller's A comes held, then the host's aHold / aUp.
  const pad = (button, held) => onHost({ type: 'input', button, held });
  const mlAt = () => state.tiles.findIndex((t) => t.id === 'moonlight');
  setFocus(tileEl('moonlight'));
  sent.length = 0;
  pad('a', true);
  const waited = !lastSent('launch') && !$('opening').classList.contains('on');
  pad('aUp');
  check('Hold A: a tap (A, then let go at once) still opens the tile', waited && lastSent('launch') && lastSent('launch').id === 'moonlight', JSON.stringify(sent));
  hideOpening();
  sent.length = 0;
  pad('a', true); pad('aHold');
  check('Hold A: held, the tile goes into move mode, with a buzz, and does not open', state.moving === 'moonlight' && tileEl('moonlight').classList.contains('moving') && lastSent('controller.buzz') && !lastSent('launch'));
  pad('right'); pad('aUp');
  check('Hold A: moved, then let go: dropped there', !state.moving && mlAt() === 5 && lastSent('tile.order'), `${state.moving} ${mlAt()}`);
  pad('a', true); pad('aHold'); pad('aUp');
  check('Hold A: let go without a move, move mode stays', state.moving === 'moonlight');
  sent.length = 0;
  pad('left'); pad('a', true); pad('aUp');
  check('Hold A: ... the D-pad moves it, A again drops it (its release opens nothing)', !state.moving && mlAt() === 4 && lastSent('tile.order') && !lastSent('launch'), `${state.moving} ${mlAt()}`);
  pad('a', true); pad('aHold'); pad('aUp'); pad('right'); pad('b');
  check('Hold A: ... or B puts it back', !state.moving && mlAt() === 4, `${state.moving} ${mlAt()}`);
  sent.length = 0;
  pad('a', true); pad('right'); pad('aHold'); pad('aUp');
  check('Hold A: the focus moved while A was down: nothing opens or moves', !state.moving && !lastSent('launch') && focusedEl() !== tileEl('moonlight'));
  setFocus(tileEl('moonlight'));
  pad('a');
  check('Hold A: the phone\'s A (no release follows) opens at once', lastSent('launch') && lastSent('launch').id === 'moonlight');
  hideOpening();
  // The longest tile hints (a running app: six), in stage pixels: the same at every 16:9 size.
  state.tiles.find((t) => t.id === 'moonlight').running = true;
  setFocus(tileEl('moonlight'));
  const homeBar = $('home-hints');
  check('Hold A: the tile hints say so, and all six fit (a running app)', /Hold A\s*Move/.test(homeBar.textContent) && /Close app/.test(homeBar.textContent) && homeBar.scrollWidth <= homeBar.clientWidth,
    `${homeBar.scrollWidth} > ${homeBar.clientWidth}: ${homeBar.textContent}`);
  state.tiles.find((t) => t.id === 'moonlight').running = false;

  // Install from Add tile: A starts it at once (no dialog), Add tile stays up with the focus on
  // its card, which shows the progress; home shows a tile installing it, then that it did not.
  const kodi = { id: 'kodi', name: 'Kodi', glyph: 'tv', color: '#5AB0FF', desc: '', type: 'app', state: 'install', canUninstall: true };
  state.libraryAvailable = true;
  EXT.actions.addtile();
  onHost({ type: 'library.catalog', apps: [kodi], sites: [] });
  const kodiCard = () => $('addtile').querySelector('[data-id="app-kodi"]');
  setFocus(kodiCard());
  press('a');
  check('Install: A starts it at once, with add to home, no dialog', state.view === 'addtile' && lastSent('library.install').id === 'kodi' && lastSent('library.install').addToHome === true, state.view);
  onHost({ type: 'library.progress', current: { id: 'kodi', name: 'Kodi', action: 'install', phase: 'download', percent: 40 }, pending: [] });
  check('Install: Add tile stays, on its card, which shows the progress', state.view === 'addtile' && focusedEl() === kodiCard() && /Downloading/.test(kodiCard().textContent) && !!kodiCard().querySelector('.lc-bar'));
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

  // The Home menu coming over an app: built at once under the blank stage while its backdrop
  // decodes, shown then, and 'shown' said to the host once drawn (it shows its window on that).
  const frame = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=';
  onHost({ type: 'blank' });
  sent.length = 0;
  onHost({ type: 'show', view: 'menu', current: 'twitch', backdrop: frame, ack: true });
  check('Menu over an app: built at once, under the blank stage', state.view === 'menu' && $('stage').classList.contains('blank') && !lastSent('shown'));
  check('Menu over an app: its panel waits to slide in until it shows', getComputedStyle($('menu').querySelector('.panel')).animationName === 'none');
  await new Promise((r) => setTimeout(r, 500));
  const said = lastSent('shown');
  check('Menu over an app: shown with its backdrop, then the host is told', !$('stage').classList.contains('blank') && $('backdrop').classList.contains('on')
    && said && typeof said.painted === 'boolean' && said.ms >= 0 && said.load >= 0, JSON.stringify(said));
  check('Menu over an app: the panel slides in once shown', getComputedStyle($('menu').querySelector('.panel')).animationName === 'slide-in');
  state.current = null; state.backdrop = null;
  reset('home');

  // Settings › TV left open, then the launcher goes (an app in front, standby): its TV search
  // (every 10 s on the host) stops until the section is on screen again.
  state.section = 'tv';
  reset('settings');
  check('Settings › TV on screen: the host searches', lastSent('tv.showing') && lastSent('tv.showing').on === true);
  onHost({ type: 'blank' });
  check('Blank stage: the TV search stops, no section in view', lastSent('tv.showing').on === false && sectionInView === null);
  onHost({ type: 'show', view: 'settings', section: 'tv' });
  check('Back on screen: the TV search starts again', lastSent('tv.showing').on === true && sectionInView === 'tv');
  reset('home');
  check('Settings left: the TV search stops', lastSent('tv.showing').on === false);

  // ---- Logos: the app's own where the host has one, the glyph otherwise --------------------------
  const PNG1 = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=';
  const plain = document.createElement('div');
  plain.innerHTML = appIcon({ glyph: 'play', color: '#FF0000' }, 40);
  check('Logos: no logo, the glyph in its colour', !!plain.querySelector('.appicon > svg') && !plain.querySelector('img') && /#FF0000/i.test(plain.innerHTML));
  const savedTiles = state.tiles;
  state.tiles = [
    { id: 'netflix', name: 'Netflix', glyph: 'play', color: '#FF4B55', logo: PNG1, logoUrl: PNG1 },
    { id: 'broken', name: 'Broken', glyph: 'film', color: '#7C8CFF', logo: 'data:image/png;base64,bm90IGFuIGltYWdl', logoUrl: 'x' },
    { id: 'chosen', name: 'Chosen', glyph: 'moon', color: '#F5D16B', logo: null, logoUrl: PNG1 },
  ];
  reset('home');
  await new Promise((r) => setTimeout(r, 300));   // the images load (or fail)
  const tileIcon = (id) => tileEl(id).querySelector('.appicon');
  check('Logos: a tile shows its logo, not the glyph', tileIcon('netflix').classList.contains('has-logo') && getComputedStyle(tileIcon('netflix').querySelector('svg')).display === 'none');
  check('Logos: a logo that does not load gives way to the glyph', !tileIcon('broken').classList.contains('has-logo') && getComputedStyle(tileIcon('broken').querySelector('svg')).display !== 'none');
  check('Logos: a tile whose glyph was chosen shows the glyph', !tileIcon('chosen').querySelector('img'));
  state.tiles.find((t) => t.id === 'netflix').running = true;
  go('menu');
  check('Logos: the Home menu row of an open app has its logo', !!$('menu-panel').querySelector('[data-id="app:netflix"] .appicon.has-logo img'));
  reset('home');
  const ciNode = (id) => $('changeicon').querySelector(`[data-id="${id}"]`);
  EXT.actions['tile-options'](tileEl('netflix'), 'netflix');
  setFocus($('tileopts').querySelector('[data-id="opt-icon"]'));
  press('a');
  check('Logos: Change icon offers the logo first, picked while it shows', state.view === 'changeicon' && ciNode('g-logo') && ciNode('g-logo').classList.contains('on') && !$('changeicon').querySelector('.ci-glyphs .on'));
  setFocus(ciNode('g-logo'));
  press('a');
  check('Logos: A on it asks for the logo back', lastSent('tile.icon').glyph === 'logo' && lastSent('tile.icon').id === 'netflix');
  reset('home');
  EXT.actions['tile-options'](tileEl('chosen'), 'chosen');
  check('Tile options: open on their first item, not where the last ones were left', focusedEl() && focusedEl().dataset.id === 'opt-move', focusedEl() && focusedEl().dataset.id);
  setFocus($('tileopts').querySelector('[data-id="opt-icon"]'));
  press('a');
  check('Logos: a chosen glyph is the one picked, the logo still offered', ciNode('g-logo') && !ciNode('g-logo').classList.contains('on') && ciNode('g-moon').classList.contains('on'));
  state.tiles = savedTiles;
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

  // ---- The UI audit: every page walked with the D-pad (audit.js) ---------------------------------
  press = realPress;   // eslint-disable-line no-global-assign
  if (typeof runAudit === 'function') await runAudit(check);
  else check('audit: audit.js is loaded', false);

  // ---- Report -------------------------------------------------------------------------------------
  console.log = log;
  const failed = results.filter((r) => !r.ok);
  const pre = document.createElement('pre');
  pre.id = 'selftest-results';
  pre.textContent = results.map((r) => `${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.ok ? '' : '  [' + r.detail + ']'}`).join('\n');
  document.body.appendChild(pre);
  document.title = failed.length ? `SELFTEST FAIL ${failed.length}` : `SELFTEST PASS ${results.length}`;
})();
