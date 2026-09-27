'use strict';
// Alerts over apps (AlertsForm). The host sends the cards; the page lays them out and answers
// with their sizes in design px, so the host can size the window and cut it to the cards.
//   From the host: {type:'alerts', list:[{id, title, body, glyph, tone: info|warn|bad, key, action}]}
//   To the host:   {type:'ready'} {type:'size', height, cards:[{y, h}]}

const host = window.chrome && window.chrome.webview;
const send = (msg) => host ? host.postMessage(msg) : console.log('to host', msg);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

const box = document.getElementById('alerts');
function fit() { box.style.transform = `scale(${innerWidth / 680})`; }
addEventListener('resize', fit);
fit();

let last = [];
// The fonts may arrive after the first layout, changing the cards' heights: measure again.
document.fonts.ready.then(() => { if (last.length) render(last); });

function render(list) {
  last = list;
  box.innerHTML = list.map((a) =>
    `<div class="alert ${esc(a.tone || 'info')}">` +
      `<span class="badge">${icon(a.glyph || 'info', 30, 2)}</span>` +
      `<div class="text"><span class="title">${esc(a.title)}</span>${a.body ? `<span class="body">${esc(a.body)}</span>` : ''}</div>` +
      (a.action ? `<span class="action"><span class="key${(a.key || 'A').length > 1 ? ' wide' : ''}">${esc(a.key || 'A')}</span>${esc(a.action)}</span>` : '') +
    '</div>').join('');
  const cards = [...box.children].map((el) => ({ y: el.offsetTop, h: el.offsetHeight }));
  send({ type: 'size', height: box.offsetHeight, cards });
}

if (host) {
  host.addEventListener('message', (e) => { if (e.data.type === 'alerts') render(e.data.list || []); });
} else {
  render([
    { id: 'timer', title: 'Sleeping in 1 minute', body: 'The video ended', glyph: 'timer', tone: 'warn', key: 'Home', action: '+15 min' },
    { id: 'volume', title: 'Volume 45', glyph: 'speaker', tone: 'info' }
  ]);
}
send({ type: 'ready' });
