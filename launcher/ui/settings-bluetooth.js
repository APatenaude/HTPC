'use strict';
// Settings › Bluetooth (design: Settings: Bluetooth): the Bluetooth switch, paired devices
// (connected, "sound plays here"; X removes one), "Pair a new device" (then nearby devices in
// pairing mode; A pairs), and the PIN a keyboard asks to be typed. Uses wifi.css's rows.
//   To the host:   bt.watch {on} · bt.scan {on} · bt.pair {id} · bt.forget {id} · bt.radio {on}
//   From the host: bt.state {adapter, radio, paired, nearby, scanning} · bt.pin {id, name, pin} · bt.result {id, ok, text}
// Demo: index.html#settings/bluetooth, other states with ?bt=scan|pin|off|none before the #.

const bt = { st: null, scanning: false, pairing: null, pin: null };   // pairing: { id, name }; pin: { name, pin }
const BT_GLYPH = { headphones: 'headphones', speaker: 'speaker', controller: 'controller', keyboard: 'keyboard', mouse: 'app', other: 'bluetooth' };

function btRow(id, glyph, label, caption, right, extra) {
  return `<div class="srow wifi-row${extra ? ' ' + extra : ''}" data-nav data-id="${id}">` +
    `<span class="wifi-glyph">${icon(glyph, 36)}</span>` +
    `<div class="text"><span class="label">${esc(label)}</span>${caption ? `<span class="caption">${caption}</span>` : ''}</div>${right || ''}</div>`;
}

function btHtml() {
  let h = '<header><h1>Bluetooth</h1><p>For headphones, speakers, controllers and keyboards. A controller with its own USB receiver doesn’t need it.</p></header>';
  const s = bt.st;
  if (!s) return h + '<p class="wifi-note">Looking for Bluetooth…</p>';
  if (!s.adapter) return h + '<p class="wifi-note">This box has no Bluetooth.</p>';
  if (bt.pin) {
    return h + '<span class="section">Pairing</span>' +
      btRow('bt-pin', 'keyboard', `Type ${bt.pin.pin} on ${bt.pin.name || 'the keyboard'}`, 'Then press Enter on it.', '', 'current') +
      '<div class="sbuttons"><div class="sbutton" data-nav data-id="bt-cancel">Close</div></div>';
  }
  const on = s.radio === 'on';
  h += `<div class="srow" data-nav data-id="bt-radio"><div class="text"><span class="label">Bluetooth</span>` +
    (s.radio === 'disabled' ? '<span class="caption">Turned off by a switch on the box or flight mode</span>' : '') + '</div>' +
    (s.radio === 'disabled' ? '' : `<div class="toggle${on ? ' on' : ''}"><span></span></div>`) + '</div>';
  if (!on) return h;
  h += '<div class="wifi-scroll">';
  if (s.paired.length) h += '<span class="section">Paired</span>';
  h += s.paired.map((d, i) => btRow('bt-paired:' + i, BT_GLYPH[d.kind] || 'bluetooth', d.name,
    d.connected ? `<span class="ok">Connected${d.soundHere ? ' · sound plays here' : ''}</span>` : 'Not connected')).join('');
  if (bt.pairing) {
    h += btRow('bt-pairing', 'bluetooth', `Pairing ${bt.pairing.name}…`, 'Keep it in pairing mode', '', 'current');
  } else if (!bt.scanning) {
    h += btRow('bt-new', 'plus', 'Pair a new device', 'Headphones, speakers, controllers, keyboards', '');
  } else {
    h += '<span class="section">Nearby · looking for devices</span>';
    h += s.nearby.length
      ? s.nearby.map((d, i) => btRow('bt-near:' + i, BT_GLYPH[d.kind] || 'bluetooth', d.name, '', '<span class="caption">A to pair</span>')).join('')
      : '<p class="wifi-note">Put the device in pairing mode (often: hold its power or Bluetooth button until a light flashes).</p>';
    h += btRow('bt-stop', 'close', 'Stop looking', '', '');
  }
  return h + '</div>';
}

function btChanged(focusId) {
  if (state.view !== 'settings' || state.section !== 'bluetooth') return;
  if (focusId) state.memory.settings = focusId;
  render();
}

function btScan(on) { bt.scanning = on; send({ type: 'bt.scan', on }); btChanged(on ? 'bt-stop' : 'bt-new'); }

settingsSection('bluetooth', {
  render: btHtml,
  press(button, el) {
    const id = el && el.dataset.id || '';
    const s = bt.st;
    if (!s) return false;
    if (button === 'a') {
      if (id === 'bt-radio') { send({ type: 'bt.radio', on: s.radio !== 'on' }); return true; }
      if (id === 'bt-new') { btScan(true); return true; }
      if (id === 'bt-stop') { btScan(false); return true; }
      if (id === 'bt-cancel') { bt.pin = null; btChanged('bt-new'); return true; }
      if (id.startsWith('bt-near:')) {
        const d = s.nearby[Number(id.slice(8))];
        if (d) { bt.pairing = { id: d.id, name: d.name }; send({ type: 'bt.pair', id: d.id }); btChanged('bt-pairing'); }
        return true;
      }
      return id.startsWith('bt-');
    }
    if (button === 'x' && id.startsWith('bt-paired:')) {
      const d = s.paired[Number(id.slice(10))];
      if (d) ask({ title: `Remove ${d.name}?`, text: 'To use it again it has to be paired again.', yes: 'Remove', onYes: () => send({ type: 'bt.forget', id: d.id }) });
      return true;
    }
    if (button === 'b' && (bt.scanning || bt.pin)) { bt.pin = null; if (bt.scanning) btScan(false); else btChanged('bt-new'); return true; }
    return false;
  },
  shown() { send({ type: 'bt.watch', on: true }); },
  left() { bt.scanning = false; bt.pin = null; send({ type: 'bt.watch', on: false }); },
  demo() {
    const v = new URLSearchParams(location.search).get('bt');
    bt.st = {
      adapter: v !== 'none', radio: v === 'off' ? 'off' : 'on', scanning: v === 'scan',
      paired: [
        { id: 'p1', name: '[Headphones]', kind: 'headphones', connected: true, soundHere: true },
        { id: 'p2', name: '[Xbox Wireless Controller]', kind: 'controller', connected: false, soundHere: false },
      ],
      nearby: [{ id: 'n1', name: '[Speaker]', kind: 'speaker' }, { id: 'n2', name: '[Keyboard]', kind: 'keyboard' }],
    };
    bt.scanning = v === 'scan';
    if (v === 'pin') bt.pin = { name: '[Keyboard]', pin: '482915' };
  },
});

hostMessage('bt.', (msg) => {
  if (msg.type === 'bt.state') { bt.st = msg; btChanged(); }
  else if (msg.type === 'bt.pin') { bt.pin = { name: msg.name, pin: msg.pin }; btChanged('bt-cancel'); }
  else if (msg.type === 'bt.result') {
    if (bt.pairing && msg.id === bt.pairing.id) { bt.pairing = null; bt.pin = null; bt.scanning = false; send({ type: 'bt.scan', on: false }); }
    toast(msg.text, msg.ok ? 'info' : 'warn');
    btChanged(msg.ok ? 'bt-paired:0' : 'bt-new');
  }
});
