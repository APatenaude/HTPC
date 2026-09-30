'use strict';
// The page's own checks, in a plain browser: index.html#selftest. Results go into #selftest-results
// and the title ("SELFTEST PASS n" / "SELFTEST FAIL n"), read by launcher\dev\Test-Ui.ps1 -SelfTest.
// Each area's checks are in selftest/<area>.js, run in this order, each from a fresh page
// (selftestFresh), then the UI audit (audit.js). A group: selftestGroup(({ check, ... }) => { ... });
// many cases in one check: checkRows (its detail names what failed); a question before something
// that cannot be undone: asksFirst. What the walker checks on every page (a ring cut, a list's
// ends, one hint bar...) is not checked again here: audit.js's header.
const SELFTEST_FILES = ['text', 'menu', 'home', 'network', 'settings', 'tiles', 'maps', 'resources', 'logos', 'addtile', 'pages', 'sounds', 'notes'];
const SELFTEST_GROUPS = [];
function selftestGroup(run) { SELFTEST_GROUPS.push({ run, file: document.currentScript ? document.currentScript.src.split('/').pop() : '?' }); }

// The self-test's own tiles (not the demo's, host.js): its checks know where each one is, 4 to a
// row: Jellyfin at the right end of the first row, Moonlight first on the second.
const SELFTEST_TILES = [['youtube', 'YouTube', 'youtube', '#FF5B52'], ['twitch', 'Twitch', 'chat', '#B08CFF'], ['stremio', 'Stremio', 'film', '#7C8CFF'],
  ['jellyfin', 'Jellyfin', 'library', '#3DC0F0'], ['moonlight', 'Moonlight', 'moon', '#F5D16B'], ['edge', 'Browser', 'globe', '#3CCB9A']]
  .map(([id, name, glyph, color]) => ({ id, name, glyph, color }));

// A clean start for a group: the home screen, these tiles (running: the ids of those open), no
// app over it, no alerts, nothing moving, closing or remembered.
function selftestFresh(running = []) {
  Object.assign(state, { moving: null, current: null, backdrop: null, confirm: null, timer: null, phone: null, stack: [], memory: {}, desktop: false });
  menuUsed.control = null; menuUsed.quick = null;
  for (const id of [...closing.keys()]) doneClosing(id);
  hideOpening();
  notices.own = [];
  noticeUpdate({ toasts: [], rows: [], pills: [] });
  state.tiles = SELFTEST_TILES.map((t) => ({ ...t, running: running.includes(t.id) }));
  reset('home');
}

(function () {
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

  // The interface sounds (sounds.js), rendered offline, a channel each; started first, so the
  // render runs beside the other checks and is done when selftest/sounds.js checks it.
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
  const soundsRendered = renderSounds().catch(() => null);   // null: the check fails

  const sNode = (id) => $('settings').querySelector(`[data-id="${id}"]`);
  const focusId = () => focusedEl() && focusedEl().dataset.id;
  const tileEl = (id) => $('tiles').querySelector(`[data-id="tile:${id}"]`);
  const PNG1 = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=';
  // What a press did picks the sound (heard: what played, no rate limit in the way).
  const heard = async (fn) => {
    await tick();
    sounds.heard.length = 0; sounds.last = {}; sounds.lastMovePress = -1e9;
    fn();
    await tick();
    return sounds.heard.map((h) => h.name).join(',');
  };
  // Many cases, one check: rows of [what, ok, detail]; its detail names each that failed.
  const checkRows = (name, rows) => check(name, rows.every(([, ok]) => ok),
    rows.filter(([, ok]) => !ok).map(([what, , detail]) => (detail !== undefined && detail !== '' ? `${what}: ${detail}` : what)).join('; '));

  // A question before something that cannot be undone: the button on target() asks it, on Cancel,
  // sending nothing of msgType; B, and Cancel, close it with the focus back on target; asked again,
  // Yes sends it, with want's fields ({ id: 'p2' }). One check; returns what Yes sent.
  const asksFirst = (name, target, button, msgType, want = {}) => {
    const misses = [];
    const ask = () => { press(button); return state.view === 'ask'; };
    sent.length = 0;
    setFocus(target());
    const view = state.view, from = focusId();
    if (!ask()) misses.push(`${button} asks nothing (${state.view} on ${focusId()})`);
    else {
      if (focusId() !== 'ask-no') misses.push(`it opens on ${focusId()}, not Cancel`);
      for (const [how, k] of [['B', 'b'], ['Cancel', 'a']]) {
        if (state.view !== 'ask' && !ask()) { misses.push(`not asked again before ${how}`); continue; }
        press(k);
        if (state.view !== view || focusId() !== from) misses.push(`${how}: ${state.view} on ${focusId()}, not ${view} on ${from}`);
      }
      if (lastSent(msgType)) misses.push(`${msgType} sent before Yes`);
      if (ask()) { setFocus($('ask').querySelector('[data-id="ask-yes"]')); press('a'); }
      const msg = lastSent(msgType);
      if (!msg) misses.push(`Yes sent no ${msgType}`);
      else if (Object.keys(want).some((k) => msg[k] !== want[k])) misses.push(`Yes sent ${JSON.stringify(msg)}, not ${JSON.stringify(want)}`);
    }
    check(`${name} asks first, on Cancel; B and Cancel leave it, Yes does it`, !misses.length, misses.join('; '));
    return lastSent(msgType);
  };

  // Waits for what a check needs (a message sent, images loaded) rather than a fixed time: true
  // once ready() holds, false after ms.
  const until = async (ready, ms = 3000) => {
    const t0 = performance.now();
    while (!ready()) {
      if (performance.now() - t0 > ms) return false;
      await new Promise((r) => setTimeout(r, 5));
    }
    return true;
  };
  // Every image in el loaded (or failed: its error handler has run by then).
  const imagesIn = (el) => [...el.querySelectorAll('img')].every((i) => i.complete);

  const shared = { check, checkRows, asksFirst, until, imagesIn, tick, key, sent, lastSent, pressed, soundsRendered, sNode, focusId, tileEl, PNG1, heard };

  async function run() {
    // A group without an await runs straight into the next, as when this was one function. One that
    // throws is a failure, and the rest still run (from a fresh page).
    for (const group of SELFTEST_GROUPS) {
      try {
        const done = group.run(shared);
        if (done) await done;
      } catch (e) {
        check(`selftest: selftest/${group.file} runs to its end`, false, e && e.stack ? e.stack.split('\n').slice(0, 3).join(' / ') : String(e));
        selftestFresh();
      }
    }

    // ---- The UI audit: every page walked with the D-pad (audit.js) ---------------------------------
    press = realPress;   // eslint-disable-line no-global-assign
    let times = [];
    if (typeof runAudit === 'function') { await AUDIT.ready; times = await runAudit(check); }
    else check('audit: audit.js is loaded', false);

    // ---- Report: the results, then the audit's slowest press per page ------------------------------
    console.log = log;
    const failed = results.filter((r) => !r.ok);
    const pre = document.createElement('pre');
    pre.id = 'selftest-results';
    pre.textContent = typeof auditReport === 'function' ? auditReport(results, times) : results.map((r) => `${r.ok ? 'PASS' : 'FAIL'}  ${r.name}`).join('\n');
    document.body.appendChild(pre);
    document.title = failed.length ? `SELFTEST FAIL ${failed.length}` : `SELFTEST PASS ${results.length}`;
  }

  // The area files in order (async off), then the run.
  let left = SELFTEST_FILES.length;
  const loaded = () => { if (--left === 0) run(); };
  for (const name of SELFTEST_FILES) {
    const s = document.createElement('script');
    s.src = `selftest/${name}.js`;
    s.async = false;
    s.onload = loaded;
    s.onerror = () => { check(`selftest: selftest/${name}.js loads`, false); loaded(); };
    document.body.appendChild(s);
  }
})();
