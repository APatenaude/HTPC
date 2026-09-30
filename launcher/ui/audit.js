'use strict';
// The UI audit, a "focus walker": each page set up in a stress state (more tiles, apps, networks...
// than a real box, long names) and walked with the D-pad through press(), from the first focus to
// every element it reaches. At each focus: the element, its ring and zoom whole inside every box
// that clips them and on screen, under no hint bar, covered by nothing; for the page: no text
// spilling or cut off, everything reached, no press wrapping round, B leaves, one hint bar, every
// press 50 ms at most, and the host's periodic pushes replayed without moving the focus or
// playing an entrance again.
//
// This file is the engine; the pages are in audit/<area>.js (AUDIT_FILES), loaded after it. Runs
// in the self-test (index.html#selftest), and alone on index.html, setup.html, keyboard.html
// #audit, before the page's load event ends (headless Edge's --dump-dom waits for it). #audit?page=
// <name> sets one page up, the focus on its last element, for a screenshot.
//
// EVERY VIEW MUST BE IN THE WALKER: a new view (addView), Settings section or setup step needs an
// auditPage() (what to set up, how to open it). One missing fails the audit.

const AUDIT = { pages: [], slowMs: 50, ring: 4, sent: [], tickEvery: 4 };   // sent: what the page sent the host

// Which page this is, and how the walker drives it there.
const AUDIT_PAGE = typeof goStep === 'function' ? 'setup' : typeof onButton === 'function' ? 'keyboard' : 'index';
const AUDIT_IO = {
  index: {
    press: (b) => press(b), focused: () => focusedEl(), setFocus: (e) => setFocus(e), view: () => state.view,
    root: () => viewEl(), stage: () => document.getElementById('stage'), fresh: () => auditFresh(),
    tick: () => onHost({ type: 'state', volume: state.volume }),   // a host push, as the minute clock: render()
  },
  setup: {
    press: (b) => press(b, true), focused: () => document.querySelector('[data-nav].focused'), setFocus: (e) => setFocus(e),
    view: () => state.step + (state.dialog ? ':dialog' : ''),
    root: () => document.querySelector('.tv-dialog-wrap') || document.getElementById('setup'),
    stage: () => document.getElementById('stage'), fresh: () => auditSetupFresh(), tick: () => {},
  },
  keyboard: {
    press: (b) => onButton(b), focused: () => document.querySelector('.kb-key.on'),
    setFocus: (e) => { focus = { row: Number(e.dataset.r), col: Number(e.dataset.c) }; render(); },   // eslint-disable-line no-global-assign
    view: () => 'keyboard', root: () => document.getElementById('kb'), stage: () => document.getElementById('kb'),
    fresh: () => {}, tick: () => {},
  },
}[AUDIT_PAGE];
// Its pages, walked in this order.
const AUDIT_FILES = { index: ['home', 'settings'], setup: ['setup'], keyboard: ['keyboard'] }[AUDIT_PAGE];

// name: what the report says. view: the view it opens (state.view; setup: its step). open():
// stress data, then the page (the focus where a user lands). scope: a selector for the part
// walked (Settings: the pane; the section list is a page of its own). dirs: the directions walked
// (none: only the first focus is checked). wraps: its ends wrap round on purpose (the on-screen
// keyboard; selftest.js checks where to). back: B presses that leave it (0: nothing to leave).
// left(): how to tell it was left, when not by the view. hints: hint bars expected. covers: the
// views or sections it checks. walk: a walk of its own (move mode). tick(): the host's periodic
// message for it, replayed as it is walked. last: the element to end on for a screenshot.
function auditPage(name, spec) { AUDIT.pages.push({ dirs: ['up', 'down', 'left', 'right'], back: 1, hints: AUDIT_PAGE === 'setup' ? 0 : 1, covers: [spec.view], ...spec, name }); }

// ---- Stress data used by more than one page's list --------------------------------------------

const AUDIT_LONG = 'with a name long enough to run out of room on a TV screen';
const AUDIT_GLYPHS = [['youtube', '#FF5B52'], ['chat', '#B08CFF'], ['film', '#7C8CFF'], ['library', '#3DC0F0'], ['moon', '#F5D16B'], ['globe', '#3CCB9A'], ['music', '#1ED760'], ['tv', '#5AB0FF']];
const auditClone = (o) => JSON.parse(JSON.stringify(o));

const AUDIT_WIFI = { type: 'wifi.state', adapter: true, radio: 'on', location: 'ok', wifiInternet: true, askRadioOff: false,
  wired: { name: 'Ethernet', mbps: 1000, up: true, internet: true },
  current: { ssid: '[Network in use]', signal: 85, words: 'strong signal' },
  networks: [{ ssid: '[Network in use]', signal: 85, words: 'strong signal', security: 'wpa2psk', password: true, saved: true, connected: true },
    ...Array.from({ length: 15 }, (_, i) => ({ ssid: i === 6 ? `[Network ${AUDIT_LONG}]` : `[Network ${i + 1}]`, signal: 80 - i * 4,
      words: i < 5 ? 'good signal' : 'weak signal', security: i % 5 === 3 ? 'open' : 'wpa2psk', password: i % 5 !== 3, saved: i % 4 === 0, connected: false }))] };
// The host sorts the networks again with each scan: replayed in another order.
function auditWifiTick() {
  const s = auditClone(AUDIT_WIFI);
  s.networks = [s.networks[0], ...s.networks.slice(1).reverse()];
  WifiUI.handle(s);
}

// The TV search's state again, its TVs in another order (the list is sorted as they answer).
function auditTvTick() {
  const tv = auditClone(state.tv);
  if (tv.found) tv.found.reverse();
  onHost({ type: 'tv.state', tv });
}

function auditTv() {
  state.tv = TvUi.demo('roku');
  const t = state.tv;
  t.profiles = t.profiles.concat(Array.from({ length: 5 }, (_, i) => ({ key: `tvkey${i}`, name: i === 1 ? `TV ${AUDIT_LONG}` : `Other TV ${i + 1}`,
    method: 'roku', methodLabel: 'Roku TV', input: 2, current: false, lastUsed: '2026-09-20' })));
  t.found = t.found.concat(Array.from({ length: 6 }, (_, i) => ({ ...t.found[0], id: `roku:X${i}`, name: `Roku TV ${i + 2}`, picked: false, detected: false })));
}

// The catalog's categories (catalog.json), and one the catalog does not know (its cards: Other).
const AUDIT_CATEGORIES = [['movies', 'Movies & shows'], ['canada', 'Canadian TV'], ['sports', 'Sports'], ['music', 'Music'],
  ['games', 'Games'], ['media', 'Your media & tools']].map(([id, name]) => ({ id, name }));
const auditCategory = (i) => (i % 13 === 12 ? 'unknown' : AUDIT_CATEGORIES[i % AUDIT_CATEGORIES.length].id);

// ---- Checks ---------------------------------------------------------------------------------

function auditDescribe(el) {
  if (!el) return 'nothing';
  return el.dataset && el.dataset.id ? `[${el.dataset.id}]` : el.id ? `#${el.id}` : `${el.tagName.toLowerCase()}${el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/).join('.') : ''}`;
}

function auditScale() { return AUDIT_IO.stage().getBoundingClientRect().width / 1920 || 1; }

// A press, timed: press() and the style and layout it causes (what the next frame needs).
function auditPress(button) {
  const t0 = performance.now();
  AUDIT_IO.press(button);
  void document.body.offsetHeight;
  return performance.now() - t0;
}

// Hint bars on screen: shown, not see-through, not under an opaque view (an overlay's dimmed
// backdrop still shows what is under it).
function auditHintBars() {
  return [...document.querySelectorAll('.hints')].filter((h) => {
    const r = h.getBoundingClientRect();
    if (!r.width || !r.height || getComputedStyle(h).visibility !== 'visible' || !h.querySelector('.key')) return false;
    for (let p = h; p; p = p.parentElement) if (Number(getComputedStyle(p).opacity) < 0.05) return false;
    const k = h.querySelector('.key').getBoundingClientRect();
    const hit = document.elementFromPoint(k.left + k.width / 2, k.top + k.height / 2);
    return !!hit && (h.contains(hit) || (hit.classList.contains('overlay') && !hit.contains(h)));
  });
}

// How far the focus ring reaches out of el, in stage pixels: its sharp box-shadows (a spread and
// an offset; a soft shadow is not the ring) or its outline. Some rings are 8 px (a ring set apart
// from an accent-coloured button).
function auditRing(el) {
  const cs = getComputedStyle(el);
  let ring = 0;
  if (cs.boxShadow && cs.boxShadow !== 'none') {
    for (const s of cs.boxShadow.split(/,(?![^(]*\))/)) {
      if (/inset/.test(s)) continue;
      const [ox = 0, oy = 0, blur = 0, spread = 0] = (s.replace(/rgba?\([^)]*\)/g, '').match(/-?[\d.]+px/g) || []).map(parseFloat);
      if (blur > 8) continue;
      ring = Math.max(ring, spread + blur + Math.max(Math.abs(ox), Math.abs(oy)));
    }
  }
  if (cs.outlineStyle !== 'none') ring = Math.max(ring, (parseFloat(cs.outlineWidth) || 0) + (parseFloat(cs.outlineOffset) || 0));
  return ring || AUDIT.ring;
}

function auditProblems(el) {
  const out = [];
  const r = el.getBoundingClientRect();
  if (!r.width || !r.height) return ['has no size'];
  const g = auditRing(el) * auditScale();
  const ring = { l: r.left - g, t: r.top - g, r: r.right + g, b: r.bottom + g };
  const inside = (a, b) => a.l >= b.l - 0.5 && a.t >= b.t - 0.5 && a.r <= b.r + 0.5 && a.b <= b.b + 0.5;
  const s = AUDIT_IO.stage().getBoundingClientRect();
  if (!inside(ring, { l: s.left, t: s.top, r: s.right, b: s.bottom })) out.push('its ring is off the screen');
  for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
    const cs = getComputedStyle(p);
    if (cs.overflowX === 'visible' && cs.overflowY === 'visible') continue;
    const c = p.getBoundingClientRect(), k = c.width / (p.offsetWidth || 1) || 1;
    const box = { l: c.left + p.clientLeft * k, t: c.top + p.clientTop * k, r: c.left + (p.clientLeft + p.clientWidth) * k, b: c.top + (p.clientTop + p.clientHeight) * k };
    if (!inside(ring, box)) { out.push(`its ring (${auditRing(el)} px) is cut by ${auditDescribe(p)}`); break; }
  }
  // A box the focus scrolled is overflow auto (without a scrollbar), not hidden: Chromium moves an
  // auto one on the GPU and draws a hidden one afresh at each step (on the TV at 4K, Add tile with
  // a direction held: 50-130 ms a press, 33 as auto).
  for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
    if (p.scrollTop > 0 && getComputedStyle(p).overflowY === 'hidden') { out.push(`${auditDescribe(p)} scrolls but is overflow: hidden (auto, scrollbar-width: none)`); break; }
  }
  for (const h of auditHintBars()) {
    const q = h.getBoundingClientRect();
    if (!h.contains(el) && ring.l < q.right && ring.r > q.left && ring.t < q.bottom && ring.b > q.top) out.push('it is under the hint bar');
  }
  // What it holds stays inside it (a long name that spills out, or is cut off, is not).
  for (const c of el.querySelectorAll('*')) {
    if (auditClippedWithin(c, el)) continue;
    const q = c.getBoundingClientRect();
    if (q.width && q.height && (q.left < r.left - 1 || q.top < r.top - 1 || q.right > r.right + 1 || q.bottom > r.bottom + 1)) {
      out.push(`what it holds spills out of it (${auditDescribe(c)})`);
      break;
    }
  }
  const d = Math.min(20 * auditScale(), r.width / 4, r.height / 4);
  for (const [x, y] of [[r.left + d, r.top + d], [r.right - d, r.top + d], [r.left + d, r.bottom - d], [r.right - d, r.bottom - d], [(r.left + r.right) / 2, (r.top + r.bottom) / 2]]) {
    const hit = document.elementFromPoint(x, y);
    if (hit && hit !== el && !el.contains(hit)) { out.push(`it is covered by ${auditDescribe(hit)}`); break; }
  }
  return out;
}

// c is in a box within box that clips what it holds (a line clamp, notes that scroll): the lines
// past that box's end are hidden there on purpose, as its text would be. That box itself is
// checked, as everything else box holds.
function auditClippedWithin(c, box) {
  for (let p = c.parentElement; p && p !== box; p = p.parentElement) if (getComputedStyle(p).overflowY !== 'visible') return true;
  return false;
}

// Text that runs out of its box, where the focus never lands too: a card or a row whose content
// spills out of it, or a box that clips (overflow hidden, a pane that scrolls) holding something
// wider than itself, cut off at its side. An ellipsis or a line clamp cuts text on purpose.
const AUDIT_BOXES = '.srow, .scard, .upd-card, .dialog';
function auditSpills() {
  const root = AUDIT_IO.root();
  if (!root) return [];
  const out = [];
  for (const box of root.querySelectorAll(AUDIT_BOXES)) {
    if (box.hasAttribute('data-nav')) continue;   // auditProblems, as the focus lands on it
    const r = box.getBoundingClientRect();
    if (!r.width || !r.height) continue;
    for (const c of box.querySelectorAll('*')) {
      if (auditClippedWithin(c, box)) continue;
      const q = c.getBoundingClientRect();
      if (q.width && q.height && (q.left < r.left - 1 || q.top < r.top - 1 || q.right > r.right + 1 || q.bottom > r.bottom + 1)) {
        out.push(`${auditDescribe(c)} spills out of ${auditDescribe(box)}`);
        break;
      }
    }
  }
  for (const e of [root, ...root.querySelectorAll('*')]) {
    if (!e.clientWidth) continue;
    const cs = getComputedStyle(e);
    if (cs.overflowX === 'visible' || cs.textOverflow === 'ellipsis' || (cs.webkitLineClamp && cs.webkitLineClamp !== 'none')) continue;
    if (e.scrollWidth > e.clientWidth + 1) out.push(`${auditDescribe(e)} holds something wider than itself (${e.scrollWidth} in ${e.clientWidth} px): cut off at its side`);
  }
  return out;
}

// The page's focusable elements (in its scope), shown.
function auditItems(page) {
  const view = AUDIT_IO.root();
  const root = view && page.scope ? view.querySelector(page.scope) : view;
  return root ? [...root.querySelectorAll('[data-nav]')].filter((e) => e.getBoundingClientRect().width > 0) : [];
}

function auditFind(page, id) { return auditItems(page).find((e) => e.dataset.id === id) || null; }

// Elements on screen with an entrance (a CSS animation that runs once; the audit makes it last
// 0 s, its name stays): one a host push makes anew plays its entrance again on screen.
function auditEntrances() {
  const root = AUDIT_IO.root();
  if (!root) return [];
  return [root, ...root.querySelectorAll('*')].filter((e) => {
    const cs = getComputedStyle(e);
    return cs.animationName !== 'none' && cs.animationIterationCount !== 'infinite' && e.getBoundingClientRect().width > 0;
  });
}

// The host's periodic messages for the page, replayed with the focus where it is: it stays
// there, and nothing on screen is drawn afresh.
function auditTick(page, report) {
  const before = new Set(auditEntrances());
  const f = AUDIT_IO.focused(), id = f && f.dataset.id;
  AUDIT_IO.tick();
  if (page.tick) page.tick();
  void document.body.offsetHeight;
  const now = AUDIT_IO.focused();
  if (id && (!now || now.dataset.id !== id)) report(`a host push moved the focus from [${id}] to ${auditDescribe(now)}`);
  const fresh = auditEntrances().find((e) => !before.has(e));
  if (fresh) report(`a host push draws ${auditDescribe(fresh)} afresh: its entrance plays again`);
}

// Walks one page: from its first focus, every direction from every element reached (the focus
// put back on it each time), checking each place the focus lands. Reports problems as it goes.
async function auditWalk(page, report) {
  const start = AUDIT_IO.focused();
  if (!start && !auditItems(page).length) { auditTick(page, report); return 0; }   // nothing to focus (a screen to wait on)
  if (!start) { report('no focus when it opens'); return; }
  const inScope = (e) => !!e && auditItems(page).includes(e);
  if (!inScope(start)) { report(`it opens with the focus outside what is walked, on ${auditDescribe(start)}`); return; }
  for (const e of auditItems(page)) if (!e.dataset.id) report(`${auditDescribe(e)} can take the focus but has no data-id`);
  const seen = new Set([start.dataset.id]), queue = [start.dataset.id], checked = new Set();
  let slowest = 0;
  const look = (el) => {
    if (checked.has(el.dataset.id)) return;
    checked.add(el.dataset.id);
    for (const p of auditProblems(el)) report(`${auditDescribe(el)}: ${p}`);
    // Now and then, the host's pushes with the focus here.
    if (checked.size % AUDIT.tickEvery === 1) auditTick(page, report);
  };
  look(start);
  while (queue.length) {
    const id = queue.shift();
    for (const dir of page.dirs) {
      const from = auditFind(page, id);
      if (!from) { report(`[${id}] went away as the focus moved round it`); break; }
      AUDIT_IO.setFocus(from);
      await null;
      let ms = auditPress(dir);
      // Slow once (the collector, the box busy with something else) is not slow: the same press
      // twice more, the fastest counts.
      for (let again = 0; again < 2 && ms > AUDIT.slowMs; again++) {
        const f = auditFind(page, id);
        if (!f || AUDIT_IO.view() !== page.view) break;
        AUDIT_IO.setFocus(f);
        ms = Math.min(ms, auditPress(dir));
      }
      slowest = Math.max(slowest, ms);
      if (ms > AUDIT.slowMs) report(`${dir} from [${id}]: took ${ms.toFixed(0)} ms`);
      await null;
      if (AUDIT_IO.view() !== page.view) { report(`${dir} from [${id}] left the page (to ${AUDIT_IO.view()})`); AUDIT_IO.fresh(); page.open(); continue; }
      const to = AUDIT_IO.focused();
      if (!to || to.dataset.id === id) continue;
      if (!inScope(to)) continue;   // out of what is walked (Settings: to the section list)
      // Where both are now (a list that scrolled to the new focus moved them both).
      const was = auditFind(page, id);
      if (!was) continue;
      const a = was.getBoundingClientRect(), b = to.getBoundingClientRect();
      const [ax, ay, bx, by] = [a.left + a.width / 2, a.top + a.height / 2, b.left + b.width / 2, b.top + b.height / 2];
      const back = { down: by < ay - 1, up: by > ay + 1, right: bx < ax - 1, left: bx > ax + 1 }[dir];
      if (back && !page.wraps) report(`${dir} from [${id}] wrapped round (or jumped back) to [${to.dataset.id}]`);
      look(to);
      if (!seen.has(to.dataset.id)) { seen.add(to.dataset.id); queue.push(to.dataset.id); }
    }
  }
  if (page.dirs.length) for (const e of auditItems(page)) if (e.dataset.id && !seen.has(e.dataset.id)) report(`${auditDescribe(e)} is never reached`);
  return slowest;
}

// What must be in the walker: every view and Settings section (the launcher), every step and the
// TV dialog (setup), the keyboard.
function auditMustCover() {
  if (AUDIT_PAGE === 'setup') return ['welcome', 'controller', 'wifi', 'tv', 'tv:dialog', 'input', 'apps', 'install', 'done'];
  if (AUDIT_PAGE === 'keyboard') return ['keyboard'];
  return ['home', 'menu', 'power', 'timer', 'confirm', 'settings', ...Object.keys(EXT.views), ...SECTIONS.map(([id]) => `section:${id}`)];
}

// Runs every page (or those named); check(name, ok, detail) records each result.
async function runAudit(check, only) {
  // The end state at once, no transitions; entrances last 0 s but keep their names (auditTick).
  const style = document.createElement('style');
  style.textContent = '*, *::before, *::after { transition: none !important; animation-duration: 0s !important; animation-delay: 0s !important; }';
  document.head.appendChild(style);
  const log = console.log;
  console.log = (...args) => { if (args[0] === 'to host') AUDIT.sent.push(args[1]); else log(...args); };
  const covered = new Set(AUDIT.pages.flatMap((p) => p.covers));
  for (const v of auditMustCover()) check(`audit: ${v} is in the walker`, covered.has(v), 'no auditPage() covers it (audit/<area>.js)');
  const times = [];
  for (const page of AUDIT.pages) {
    if (only && !only.includes(page.name)) continue;
    const problems = [];
    const report = (text) => problems.push(text);
    try {
      AUDIT_IO.fresh();
      AUDIT.sent = [];
      page.open();
      await null;
      if (AUDIT_IO.view() !== page.view) report(`it did not open (the view is ${AUDIT_IO.view()})`);
      else {
        for (const p of auditSpills()) report(p);
        const slowest = page.walk ? page.walk(page, report) : await auditWalk(page, report);
        times.push([page.name, slowest || 0]);
        const bars = auditHintBars().length;
        if (bars !== page.hints) report(`${bars} hint bars show, not ${page.hints}`);
        if (page.back) {
          const first = auditItems(page)[0];
          if (first && !page.walk) AUDIT_IO.setFocus(first);
          for (let i = 0; i < page.back && AUDIT_IO.view() === page.view && !(page.left && page.left()); i++) AUDIT_IO.press('b');
          if (page.left ? !page.left() : AUDIT_IO.view() === page.view) report(`B (${page.back} times) does not leave it`);
        }
      }
    } catch (e) { report(`threw ${e && e.stack ? e.stack.split('\n').slice(0, 3).join(' / ') : e}`); }
    check(`audit: ${page.name}`, !problems.length, [...new Set(problems)].join(' | '));
  }
  console.log = log;
  style.remove();
  AUDIT_IO.fresh();
  return times;
}

// #audit (real time) and #audit?page=<name> (a page to look at), on any of the three pages.
async function auditRun() {
  const route = window.auditRoute || '';
  if (!route.startsWith('audit')) return;
  const one = /[?&]page=([^&]+)/.exec(route);
  if (one) {
    const page = AUDIT.pages.find((p) => p.name === decodeURIComponent(one[1]));
    if (!page) return;
    AUDIT_IO.fresh();
    page.open();
    if (page.walk) { for (let i = 0; i < 7; i++) AUDIT_IO.press('down'); return; }
    const all = auditItems(page);
    const last = page.last ? page.last() : all[all.length - 1];
    if (last) AUDIT_IO.setFocus(last);
    return;
  }
  const results = [];
  const check = (name, ok, detail) => results.push({ name, ok: !!ok, detail: detail || '' });
  const times = await runAudit(check);
  const failed = results.filter((r) => !r.ok);
  const pre = document.createElement('pre');
  pre.id = 'audit-results';
  pre.textContent = `${innerWidth}x${innerHeight}\n` + results.map((r) => `${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.ok ? '' : '  [' + r.detail + ']'}`).join('\n') +
    '\n\nSlowest press per page (ms, real time):\n' + times.map(([n, ms]) => `  ${ms.toFixed(1).padStart(6)}  ${n}`).join('\n');
  document.body.appendChild(pre);
  document.title = failed.length ? `AUDIT FAIL ${failed.length}` : `AUDIT PASS ${results.length}`;
}

// The page lists in order (async off), then the run: still before the page's load event, which
// waits for the scripts added before it.
AUDIT.ready = new Promise((done) => {
  let left = AUDIT_FILES.length;
  for (const name of AUDIT_FILES) {
    const s = document.createElement('script');
    s.src = `audit/${name}.js`;
    s.async = false;
    s.onload = s.onerror = () => { if (--left === 0) done(); };
    document.body.appendChild(s);
  }
});
AUDIT.ready.then(auditRun);
