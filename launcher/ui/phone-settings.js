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
        '<span class="phone-how">iPhone: shows how to make the “Send to TV” Shortcut.<br>' +
        (p.secure ? 'Android: installs this box’s certificate once, so the remote installs as an app and shows up in Share.'
          : 'Android needs HTTPS, which isn’t running (the launcher log says why).') + '</span></div></div>' +
    '</div>';
  const problem = !p.listening ? ['The remote isn’t running', 'Another program has its port. The launcher log says which ports it tried.'] : PHONE_REACH[p.reach];
  if (problem) {
    body += `<div class="srow phone-warn"><div class="text"><span class="label">${esc(problem[0])}</span>` +
      `<span class="caption">${esc(problem[1])}</span></div></div>`;
  }
  body += settingRow('phone.requireCode', 'Ask for a code on new phones', 'The first time a phone connects, a 4-digit code shows on the TV', toggle(p.requireCode));
  body += '<span class="ssection">Phones</span>';
  const shown = p.phones.slice(0, problem ? 1 : 2); // what fits on the screen
  if (!shown.length && !p.unpaired) {
    body += '<div class="srow"><div class="text"><span class="label">No phones yet</span>' +
      '<span class="caption">Scan the code above with your phone.</span></div></div>';
  }
  for (const ph of shown) {
    body += `<div class="srow phone-row" data-nav data-id="phone-${esc(ph.id)}" data-act="phone-forget" data-arg="${esc(ph.id)}">` +
      `${icon('phone', 34)}<div class="text"><span class="label">${esc(ph.name)}</span>` +
      `<span class="caption${ph.connected ? ' good' : ''}">${ph.shortcut ? 'Share-sheet Shortcut · ' : ''}${ph.connected ? 'Connected now' : 'Last used ' + esc(phoneDate(ph.lastSeen))}</span></div>` +
      '<span class="phone-forget">Forget</span></div>';
  }
  if (p.phones.length > shown.length) body += `<p class="phone-more">And ${p.phones.length - shown.length} more</p>`;
  if (p.unpaired) body += `<p class="phone-more">${p.unpaired} connected without a code</p>`;
  return body;
}

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

onAction('phone-forget', (el, id) => send({ type: 'phone.forget', id }));

settingsSection('phone', {
  render: renderPhoneSection,
  shown() { askPhoneInfo(true); },
  // Demo in a plain browser: index.html#settings/phone.
  demo() {
    EXT.host['phone.settings']({ type: 'phone.settings', phone: {
      listening: true, address: 'tv.local', ip: '192.168.1.20', requireCode: true, reach: 'ok', unpaired: 0, secure: true,
      qr: 'http://192.168.1.20/?k=Qm9vc3RlZC1kZW1vLWtleQ',
      sendQr: 'http://192.168.1.20/send?k=U2VuZC1kZW1vLWtleS1vbmx5',
      phones: [
        { id: 'a1', name: 'iPhone', connected: true, lastSeen: Date.now() },
        { id: 'b2', name: 'Android phone', connected: false, lastSeen: Date.now() - 5 * 86400000 },
        { id: 'c3', name: 'iPhone Shortcut', connected: false, lastSeen: Date.now() - 86400000, shortcut: true },
      ] } });
  },
});
