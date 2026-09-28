'use strict';

// Settings › Phone remote (SPEC N8, W3). Builds on app.js (state, render, send, icon, esc,
// settingRow, toggle, $) and registers through its settingsSection / onAction / hostMessage;
// app.js's changeSetting hands "phone.*" rows to phoneSetting(). The host (MainForm.Phone.cs) sends
// "phone.settings" with the section's data. (state.phone is the host's short summary for the home
// screen: url, paired.) The pairing code shows as an alert over whatever is on the TV (IAlerts).

(() => {
  const css = document.createElement('link');
  css.rel = 'stylesheet';
  css.href = 'phone-settings.css';
  document.head.appendChild(css);
})();

state.phoneSettings = { listening: false, address: 'tv.local', ip: null, requireCode: true, reach: 'unknown', unpaired: 0, phones: [] };
const phoneQr = { url: null, svg: '', at: 0 }, sendQr = { url: null, svg: '', at: 0 };
let phoneInfoAsked = 0;

// A fresh QR code (it carries a one-time pairing key, good for 2 minutes) while the section is open.
function askPhoneInfo(force) {
  if (!force && Date.now() - phoneInfoAsked < 90000) return;
  phoneInfoAsked = Date.now();
  send({ type: 'phone.info' });
}
setInterval(() => { if (state.view === 'settings' && state.section === 'phone') askPhoneInfo(false); }, 15000);

function phoneDate(ms) {
  const days = Math.floor((Date.now() - ms) / 86400000);
  if (days < 1 && new Date(ms).getDate() === new Date().getDate()) return 'today';
  if (days < 2) return 'yesterday';
  return new Date(ms).toLocaleDateString('en-GB', { day: 'numeric', month: 'long' });
}

const PHONE_REACH = {
  noRule: ['Phones can’t reach the box yet', 'Windows Firewall has no rule for the remote. Run TV Box Setup again: it adds one.'],
  public: ['This network is set to Public', 'The remote only answers on a Private network. Run TV Box Setup again, or make this network Private in Windows.'],
};

// HTTPS runs, but Windows may send its certificate without the intermediate (made after setup ran).
const PHONE_CHAIN = ['Run TV Box Setup again for Android’s Share','The box made a new HTTPS certificate. Setup lets Windows send it whole, so Android can check it.'];

function renderPhoneSection() {
  const p = state.phoneSettings;
  askPhoneInfo(false);
  const qrOk = p.listening && phoneQr.svg && Date.now() - phoneQr.at < 120000;
  const qr = qrOk ? phoneQr.svg : `<span class="phone-qr-wait">${icon('phone', 72, 1.5)}</span>`;
  const sendOk = p.listening && sendQr.svg && Date.now() - sendQr.at < 120000;
  const how = 'iPhone: Safari › Share › Add to Home Screen.<br>Android: Chrome › ⋮ › Add to Home screen.' +
    (p.ip ? `<br><span class="phone-ip">Or ${esc(p.ip)}, if tv.local doesn’t open</span>` : '');
  let body = '<header><h1>Phone remote</h1><p>A small companion web app for iPhone and Android. Nothing to download from a store.</p></header>' +
    '<div class="phone-cards">' +
      `<div class="phone-card"><div class="phone-qr${qrOk ? '' : ' wait'}">${qr}</div>` +
        '<div class="phone-card-text"><span class="phone-card-title">1 · The remote</span>' +
        `<span class="phone-address">${esc(p.address || 'tv.local')}</span><span class="phone-how">${how}</span></div></div>` +
      `<div class="phone-card"><div class="phone-qr${sendOk ? '' : ' wait'}">${sendOk ? sendQr.svg : icon('share', 72, 1.5)}</div>` +
        '<div class="phone-card-text"><span class="phone-card-title">2 · Share to TV</span>' +
        '<span class="phone-how">iPhone: the “Send to TV” Shortcut. Android: this box’s certificate' +
        (p.secure && p.fingerprint ? '; its SHA-256 fingerprint on the phone must read:</span>' +
            `<span class="phone-fingerprint">${esc(fingerprintLines(p.fingerprint))}</span>`
          : ', once HTTPS runs (it isn’t: the launcher log says why).</span>') + '</div></div>' +
    '</div>';
  const problem = !p.listening ? ['The remote isn’t running', 'Another program has its port. The launcher log says which ports it tried.'] : PHONE_REACH[p.reach] || (p.chainMissing ? PHONE_CHAIN : null);
  if (problem) {
    body += `<div class="srow phone-warn"><div class="text"><span class="label">${esc(problem[0])}</span>` +
      `<span class="caption">${esc(problem[1])}</span></div></div>`;
  }
  body += settingRow('phone.requireCode', 'Ask for a code on new phones', 'The first time a phone connects, a 4-digit code shows on the TV', toggle(p.requireCode));
  body += '<span class="ssection">Phones</span>';
  const shown = p.phones.filter((ph) => !ph.shortcut);
  // Shortcut keys under the phone that made them (forgetting the phone forgets them); older ones
  // (no phone) in their own list.
  const keysOf = (id) => p.phones.filter((k) => k.shortcut && k.owner === id);
  const loose = p.phones.filter((k) => k.shortcut && !shown.some((ph) => ph.id === k.owner));
  const keyRow = (k, under) => `<div class="srow phone-row${under ? ' phone-key' : ''}" data-nav data-id="phone-${esc(k.id)}" data-act="phone-forget" data-arg="${esc(k.id)}">` +
    `${icon('share', 34)}<div class="text"><span class="label">${esc(under ? 'Shortcut key' : k.name)}</span>` +
    `<span class="caption">Last used ${esc(phoneDate(k.lastSeen))}</span></div><span class="phone-forget">Forget</span></div>`;
  if (!shown.length && !p.unpaired) {
    body += '<div class="srow"><div class="text"><span class="label">No phones yet</span>' +
      '<span class="caption">Scan the code above with your phone.</span></div></div>';
  }
  for (const ph of shown) {
    body += `<div class="srow phone-row" data-nav data-id="phone-${esc(ph.id)}" data-act="phone-forget" data-arg="${esc(ph.id)}">` +
      `${icon('phone', 34)}<div class="text"><span class="label">${esc(ph.name)}</span>` +
      `<span class="caption${ph.connected ? ' good' : ''}">${ph.connected ? 'Connected now' : 'Last used ' + esc(phoneDate(ph.lastSeen))}</span></div>` +
      '<span class="phone-forget">Forget</span></div>';
    for (const k of keysOf(ph.id)) body += keyRow(k, true);
  }
  if (p.unpaired) body += `<p class="phone-more">${p.unpaired} connected without a code</p>`;
  if (loose.length) {
    body += '<span class="ssection">Share-sheet Shortcut keys</span>';
    for (const k of loose) body += keyRow(k, false);
  }  return body;
}

// "AB:CD:..." as 4 lines of 8 bytes (Android shows it the same way, colons and all).
function fingerprintLines(fp) {
  const bytes = String(fp).split(':');
  const lines = [];
  for (let i = 0; i < bytes.length; i += 8) lines.push(bytes.slice(i, i + 8).join(':'));
  return lines.join('\n');
}

// The section can be taller than the screen (every phone and key is listed): its pane scrolls on
// its own (no scrollbar) to keep the focused row in view; the rest of the screen stays put.
// (scrollIntoView would also scroll the stage and the view, overflow: hidden or not.) A render
// makes a new pane: it keeps the scroll it had, until the section is left.
const phoneScroll = { top: 0 };
function keepPhoneRowInView() {
  if (state.view !== 'settings' || state.section !== 'phone') { phoneScroll.top = 0; return; }
  const main = document.querySelector('#settings .spane main');
  const el = main && main.querySelector('.srow.focused');
  if (!el) return;
  if (main.scrollTop !== phoneScroll.top) main.scrollTop = phoneScroll.top;
  if (el === main.querySelector('[data-nav]')) main.scrollTop = 0; // the first row: the codes show again
  else {
    const m = main.getBoundingClientRect(), r = el.getBoundingClientRect();
    const scale = m.height / main.clientHeight || 1; // the stage is scaled to the screen
    const pad = 24 * scale;
    if (r.top < m.top + pad) main.scrollTop -= (m.top + pad - r.top) / scale;
    else if (r.bottom > m.bottom - pad) main.scrollTop += (r.bottom - m.bottom + pad) / scale;
  }
  phoneScroll.top = main.scrollTop;
}
new MutationObserver(keepPhoneRowInView).observe(document.body, { subtree: true, attributes: true, attributeFilter: ['class'] });

// Settings rows with keys "phone.*" (app.js's changeSetting hands them here).
function phoneSetting(key) {
  if (key !== 'phone.requireCode') return;
  state.phoneSettings.requireCode = !state.phoneSettings.requireCode;
  send({ type: 'phone.requireCode', value: state.phoneSettings.requireCode });
  render();
}

hostMessage('phone.settings', (msg) => {
  const p = msg.phone || {};
  if (p.qr && p.qr !== phoneQr.url) {
    phoneQr.url = p.qr;
    phoneQr.svg = qrSvg(p.qr, 220);
    phoneQr.at = Date.now();
  }
  if (p.sendQr && p.sendQr !== sendQr.url) {
    sendQr.url = p.sendQr;
    sendQr.svg = qrSvg(p.sendQr, 220);
    sendQr.at = Date.now();
  }
  delete p.qr;
  delete p.sendQr;
  Object.assign(state.phoneSettings, p);
  if (state.view === 'settings' && state.section === 'phone') render();
});

// Forget asks first (the dialog's focus starts on Cancel).
onAction('phone-forget', (el, id) => {
  const p = (state.phoneSettings.phones || []).find((x) => x.id === id);
  if (!p) return;
  ask({
    title: `Forget ${p.name}?`,
    text: p.shortcut ? 'The Shortcut that uses this key stops working. The phone can make a new key.'
      : (state.phoneSettings.phones || []).some((k) => k.shortcut && k.owner === p.id) ? 'Its Shortcut keys stop working too. To be a remote again, it has to pair again.'
      : 'To be a remote again, it has to pair again: scan the code on this screen.',
    yes: 'Forget',
    onYes: () => send({ type: 'phone.forget', id }),
  });
});

settingsSection('phone', {
  render: renderPhoneSection,
  shown() { askPhoneInfo(true); },
  // Demo in a plain browser: index.html#settings/phone (?phones=8&keys=4: that many more, the last one focused).
  demo() {
    const q = new URLSearchParams(location.hash.split('?')[1] || '');
    const more = [];
    for (let i = 1; i <= Number(q.get('phones') || 0); i++) more.push({ id: `p${i}`, name: `Phone ${i}`, connected: false, lastSeen: Date.now() - i * 86400000 });
    for (let i = 1; i <= Number(q.get('keys') || 0); i++) more.push({ id: `k${i}`, name: `iPhone Shortcut ${i}`, connected: false, lastSeen: Date.now() - i * 86400000, shortcut: true });
    if (more.length) state.memory.settings = `phone-${more[more.length - 1].id}`;
    EXT.host['phone.settings']({ type: 'phone.settings', phone: {
      listening: true, address: 'tv.local', ip: '192.168.1.20', requireCode: true, reach: 'ok', unpaired: 0, secure: true, chainMissing: q.get('chain') === 'missing',
      fingerprint: '3A:9F:12:C4:7E:05:B8:61:D2:4A:90:3C:E7:18:6B:F5:21:8D:C9:47:0E:B3:5A:96:F1:2C:84:7D:63:E0:1B:A8',
      qr: 'http://192.168.1.20/?k=Qm9vc3RlZC1kZW1vLWtleQ',
      sendQr: 'http://192.168.1.20/send?k=U2VuZC1kZW1vLWtleS1vbmx5',
      phones: [
        { id: 'a1', name: 'iPhone', connected: true, lastSeen: Date.now() },
        { id: 'b2', name: 'Android phone', connected: false, lastSeen: Date.now() - 5 * 86400000 },
        { id: 'c3', name: 'iPhone Shortcut', connected: false, lastSeen: Date.now() - 86400000, shortcut: true, owner: 'a1' },
        ...more,
      ] } });
  },
});
