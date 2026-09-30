'use strict';
// Self-test: Wi-Fi, Settings > Bluetooth, Settings > Phone remote. Run by selftest.js, in its order.
selftestGroup(async ({ check, asksFirst, tick, sent, lastSent }) => {
  // ---- Wi-Fi (wifi.js) ------------------------------------------------------------------------------
  selftestFresh();
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
  check('Wi-Fi: switch off while online only through it asks first', asked.length === 1 && !lastSent('wifi.radio'), JSON.stringify(asked));
  WifiUI.press('x', node('wifi-current'));
  check('Wi-Fi: X on the network in use asks before forgetting it, and says why', asked.length === 2 && !!asked[1].text && !lastSent('wifi.forget'), JSON.stringify(asked));
  WifiUI.press('a', node('wifi-net:[Old router]'));
  check('Wi-Fi: a WEP network is refused with the reason, no form', !WifiUI.joining && toasts.length === 1, JSON.stringify(toasts));
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
  // Its status: "Connected", and for the one sound plays on, a second part.
  const btStatus = (id) => (btNode(id).querySelector('.ok') || { textContent: '' }).textContent.split('·').length;
  check('Bluetooth: paired headphones say sound plays there', btNode('bt-paired:p1') && btStatus('bt-paired:p1') === 2, btNode('bt-paired:p1') && btNode('bt-paired:p1').textContent);
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
  asksFirst('Bluetooth: X on a paired device', () => btNode('bt-paired:p2'), 'x', 'bt.forget', { id: 'p2' });
  reset('home');
  check('Bluetooth: left, the host stops', lastSent('bt.watch').on === false);

  // ---- Settings › Phone remote (phone-settings.js): Forget asks first -------------------------------
  EXT.sections.phone.demo();
  state.section = 'phone';
  reset('settings');
  for (const [id, what] of [['b2', 'a phone'], ['c3', 'a Shortcut key']]) {
    asksFirst(`Phone remote: Forget on ${what}`, () => $('settings').querySelector(`[data-id="phone-${id}"]`), 'a', 'phone.forget', { id });
  }
  reset('home');
  await tick();
});
