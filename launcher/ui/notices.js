'use strict';
// Alerts on the launcher (design: Alerts): cards at the top right, a row at the top of the Home
// menu for each alert with something to do (A does it, X dismisses it), pills in the status
// bar. AlertCenter (C#) decides what shows and for how long; this only draws it. Also the
// page's own short messages (toast() in app.js). Loaded before app.js, because app.js's status
// bar and Home menu draw the pills and rows from here; its helpers (state, send, icon, esc,
// render, hints) and registries are used only once the page has loaded.
//   From the host: {type:'alerts.update', toasts:[{id, title, body, glyph, tone, key, action, claimsHome}],
//                   rows:[{id, title, body, glyph, tone, action}], pills:[{id, text, glyph, tone}]}
//   To the host:   {type:'alerts.act', id} {type:'alerts.dismiss', id}

const notices = {
  toasts: [],       // the host's cards, newest first
  rows: [],         // Home menu rows
  pills: [],        // status bar pills
  own: [],          // the page's own messages: { nkey, title, tone, until }
  seen: new Map(),  // card key -> when it first showed (keeps the stack's order steady)
};
let noticeCount = 0;   // the page's own messages
let noticeOrder = 0;   // arrival order of cards
const NOTICE_MAX = 3;

function noticeUpdate(msg) {
  notices.toasts = msg.toasts || [];
  notices.rows = msg.rows || [];
  notices.pills = msg.pills || [];
  renderNotices();
  // The menu and the status bar show rows and pills: redraw the view if it has them.
  if (state.view === 'home' || state.view === 'menu' || state.view === 'confirm') render();
}

// A short message from the page itself (a test result, "comes in a later update").
function noticeOwn(text, kind) {
  notices.own.unshift({ nkey: 'own:' + (++noticeCount), title: text, tone: kind === 'warn' ? 'warn' : 'info', until: Date.now() + 5000 });
  renderNotices();
  setTimeout(renderNotices, 5100);
}

function noticeCardHtml(n) {
  const chip = n.action
    ? `<span class="action"><span class="key${(n.key || 'Home').length > 1 ? ' wide' : ''}">${esc(n.key || 'Home')}</span>${esc(n.action)}</span>`
    : '';
  return `<span class="badge">${icon(n.glyph || (n.tone === 'info' ? 'info' : 'warn'), 30, 2)}</span>` +
    `<div class="text"><span class="title">${esc(n.title)}</span>${n.body ? `<span class="body">${esc(n.body)}</span>` : ''}</div>` + chip;
}

// Cards are kept by key, so one that stays does not replay its entrance when another comes or goes.
function renderNotices() {
  const box = $('toasts');
  if (!box) return;
  const now = Date.now();
  notices.own = notices.own.filter((o) => o.until > now);
  const list = [
    ...notices.toasts.map((t) => ({ ...t, nkey: 'alert:' + t.id })),
    ...notices.own,
  ];
  // Newcomers rank by arrival; several arriving together keep the host's order (newest first).
  for (const n of [...list].reverse()) if (!notices.seen.has(n.nkey)) notices.seen.set(n.nkey, ++noticeOrder);
  list.sort((a, b) => notices.seen.get(b.nkey) - notices.seen.get(a.nkey));
  const shown = list.slice(0, NOTICE_MAX);
  const keys = new Set(shown.map((n) => n.nkey));
  for (const k of [...notices.seen.keys()]) if (!list.some((n) => n.nkey === k)) notices.seen.delete(k);
  for (const el of [...box.children]) if (!keys.has(el.dataset.key)) el.remove();
  let before = box.firstChild;
  for (const n of shown) {
    const html = noticeCardHtml(n);
    let el = [...box.children].find((c) => c.dataset.key === n.nkey);
    if (!el) {
      el = document.createElement('div');
      el.dataset.key = n.nkey;
      el.setAttribute('role', 'status');
      box.insertBefore(el, before);
    }
    el.className = `notice ${n.tone || 'info'}`;
    if (el.dataset.html !== html) { el.innerHTML = html; el.dataset.html = html; }
    before = el.nextSibling;
  }
  noticeAvoid(typeof focusedEl === 'function' ? focusedEl() : null);
}

// The cards never cover the focus (the status bar's Settings and Power, a tile at the top
// right): over it, only the newest shows, on one line; still over it, at the bottom right,
// above the hints; until the focus moves on.
function noticeAvoid(el) {
  const box = $('toasts');
  if (!box) return;
  box.classList.remove('compact', 'low');
  if (!el || !box.children.length) return;
  const over = () => {
    const a = box.getBoundingClientRect(), r = el.getBoundingClientRect(), g = 8 * ($('stage').getBoundingClientRect().width / 1920 || 1);
    return r.left - g < a.right && r.right + g > a.left && r.top - g < a.bottom && r.bottom + g > a.top;
  };
  if (!over()) return;
  box.classList.add('compact');
  if (!over()) return;
  box.classList.add('low');
  if (over()) box.classList.remove('low');
}

// Home pressed while an actionable card is on screen: the menu opens on its row.
function noticeHomeFocus() {
  const t = notices.toasts.find((x) => x.action && !x.claimsHome);
  return t ? 'alert:' + t.id : null;
}

function noticeRowsHtml() {
  return notices.rows.map((r) =>
    `<div class="row alert-row ${esc(r.tone || 'info')}" data-nav data-id="alert:${esc(r.id)}" data-act="alert" data-arg="${esc(r.id)}" data-alert="${esc(r.id)}">` +
      `<span class="badge">${icon(r.glyph || 'warn', 28, 2)}</span>` +
      `<span class="grow"><span class="t">${esc(r.title)}</span>${r.body ? `<span class="b">${esc(r.body)}</span>` : ''}</span>` +
      `<span class="chip"><span class="key">A</span>${esc(r.action)}</span>` +
    '</div>').join('');
}

function noticePillsHtml() {
  return notices.pills.slice(0, 2).map((p) =>
    `<div class="pill alert ${esc(p.tone || 'warn')}">${icon(p.glyph || 'warn', 28, 2)}<span>${esc(p.text)}</span></div>`).join('');
}

// A on an alert's row: its action (the host runs it). Reopen and Try again show the opening screen.
function noticeAct(id) {
  const row = notices.rows.find((r) => r.id === id);
  if (row && id.startsWith('app:') && /^(Reopen|Try again)$/.test(row.action)) {
    const t = state.tiles.find((x) => x.id === id.slice(4));
    if (t) showOpening(t);
  }
  send({ type: 'alerts.act', id });
}

function noticeDismiss(id) {
  notices.rows = notices.rows.filter((r) => r.id !== id);
  send({ type: 'alerts.dismiss', id });
  render();
}

// ---- Demo (a plain browser): index.html#alerts, #menu-alerts --------------------------------

const NOTICE_DEMO = {
  toasts: [
    { id: 'app:stremio', title: 'Stremio closed unexpectedly', body: 'It stopped working and closed.', glyph: 'warn', tone: 'bad', key: 'Home', action: 'Reopen' },
    { id: 'internet', title: 'No internet', body: 'Check the network cable or the Wi-Fi.', glyph: 'wifi', tone: 'warn', key: 'Home', action: 'Wi-Fi settings' },
    { id: 'phone', title: 'Phone remote connected', body: '[Your phone]', glyph: 'phone', tone: 'info' },
  ],
  rows: [
    { id: 'app:stremio', title: 'Stremio closed unexpectedly', body: 'It stopped working and closed.', glyph: 'warn', tone: 'bad', action: 'Reopen' },
    { id: 'tv', title: 'Can’t reach the TV', body: 'Is it on the network? Settings › TV can find it again.', glyph: 'tv', tone: 'bad', action: 'TV settings' },
  ],
  pills: [{ id: 'internet', text: 'No internet', glyph: 'wifi', tone: 'warn' }, { id: 'updates', text: '4 updates', glyph: 'download', tone: 'warn' }],
};

// True when the route was one of these demos (index.html#alerts...).
function noticeDemo(route) {
  if (route === 'alerts') { noticeUpdate(NOTICE_DEMO); return true; }
  if (route === 'alerts-sleep') {
    noticeUpdate({ toasts: [{ id: 'sleep', title: 'Sleeping in 1 minute', body: 'Sleep timer', glyph: 'timer', tone: 'warn', key: 'Home', action: '+15 min', claimsHome: true }], rows: [], pills: [] });
    return true;
  }
  if (route === 'menu-alerts') {
    for (const t of state.tiles) t.running = true;
    state.current = 'jellyfin';
    noticeUpdate({ toasts: [], rows: NOTICE_DEMO.rows.slice(0, 1), pills: [] });
    go('menu');
    return true;
  }
  return false;
}

// Once app.js has loaded (this runs before its own DOMContentLoaded handler, which says
// 'ready' and runs demo routes): the registrations, and this file's demo routes, which then
// take the route away from app.js's. The host sends what is up on each 'ready' ([UiReady]).
addEventListener('DOMContentLoaded', () => {
  onAction('alert', (el, arg) => noticeAct(arg));
  hostMessage('alerts.update', noticeUpdate);
  hostMessage('text.', (msg) => { if (msg.type === 'text.insert') textInsert(msg.text); else if (msg.type === 'text.key') textKey(msg.key); });
  if (host || !location.hash) return;
  const route = location.hash.slice(1);
  // #selftest: the self-test, the UI audit (audit.js) at its end; #audit, #audit?page=...: the
  // audit alone. In order (async off); loaded before the page's load event, which waits for them.
  const load = (src) => { const s = document.createElement('script'); s.src = src; s.async = false; document.body.appendChild(s); };
  if (route === 'selftest') { load('audit.js'); load('selftest.js'); }
  else if (route.startsWith('audit')) { window.auditRoute = route; load('audit.js'); }
  else if (!noticeDemo(route)) return;
  history.replaceState(null, '', location.pathname + location.search);
});
