'use strict';
// The page's own checks, in a plain browser: index.html#selftest. Results go into #selftest-results
// and the title ("SELFTEST PASS n" / "SELFTEST FAIL n"), read by launcher\dev\Test-Ui.ps1 -SelfTest.
// Each area's checks are in selftest/<area>.js, run in this order (each leaves the page as the next
// expects), then the UI audit (audit.js). A group: selftestGroup(({ check, ... }) => { ... }).
const SELFTEST_FILES = ['text', 'menu', 'home', 'network', 'settings', 'tiles', 'maps', 'resources', 'logos', 'addtile', 'sounds', 'notes'];
const SELFTEST_GROUPS = [];
function selftestGroup(run) { SELFTEST_GROUPS.push(run); }

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
  const shared = { check, tick, key, sent, lastSent, pressed, soundsRendered, sNode, focusId, tileEl, PNG1, heard };

  async function run() {
    // A group without an await runs straight into the next, as when this was one function.
    for (const group of SELFTEST_GROUPS) {
      const done = group(shared);
      if (done) await done;
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
