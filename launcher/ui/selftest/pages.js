'use strict';
// Self-test: fit() on every page (its fixed stage scaled to any screen), and the launcher's other
// pages in a frame: setup's app list, the on-screen keyboard. Run by selftest.js, in its order.
selftestGroup(async ({ check, checkRows }) => {
  const overlap = (a, b) => a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1;

  // A frame holding one of the launcher's other pages (the same origin: its scripts can be
  // reached), at a screen's size.
  async function framePage(body, css, scripts, w, h) {
    const f = document.createElement('iframe');
    f.style.cssText = `position: fixed; left: 0; top: 0; width: ${w}px; height: ${h}px; border: 0; visibility: hidden`;
    f.srcdoc = '<!doctype html><html><head><meta charset="utf-8">' + css.map((c) => `<link rel="stylesheet" href="${c}">`).join('') + '</head><body>' + body +
      scripts.map((s) => `<script src="${s}"></` + 'script>').join('') + '</body></html>';
    const loaded = new Promise((r) => f.addEventListener('load', r, { once: true }));
    document.body.appendChild(f);
    await loaded;
    f.contentWindow.console.log = () => {};
    return f;
  }
  // The frame at another size, once its page has had the resize (its fit() runs on it).
  const resize = (f, w, h) => new Promise((done) => {
    if (f.contentWindow.innerWidth === w && f.contentWindow.innerHeight === h) { done(); return; }
    f.contentWindow.addEventListener('resize', done, { once: true });
    f.style.width = `${w}px`;
    f.style.height = `${h}px`;
  });

  // ---- fit(): each page's stage whole on any screen ---------------------------------------------
  // Every page draws a fixed stage (1920x1080; the keyboard a 1920x440 band) that fit() scales to
  // the screen, nothing else follows its size: the stage fills it by its width or its height, whole
  // and centred (the keyboard's band at the bottom). The screen's size as fit() reads it (innerWidth
  // and innerHeight) is set for each size; the audit walks the pages at these sizes in Test-Ui
  // -AllSizes. (A band cut off on an ultrawide was fit()'s maths, fb00eff.)
  const fitAt = (win, stage, sizes, bottom) => sizes.map(([w, h]) => {
    const saved = ['innerWidth', 'innerHeight'].map((k) => [k, Object.getOwnPropertyDescriptor(win, k)]);
    Object.defineProperty(win, 'innerWidth', { configurable: true, get: () => w });
    Object.defineProperty(win, 'innerHeight', { configurable: true, get: () => h });
    try {
      win.fit();
      const r = stage.getBoundingClientRect();
      const fills = Math.abs(r.width - w) < 1 || Math.abs(r.height - h) < 1;
      const whole = r.left >= -0.5 && r.top >= -0.5 && r.right <= w + 0.5 && r.bottom <= h + 0.5;
      const placed = Math.abs(r.left - (w - r.right)) < 1 && (bottom ? Math.abs(r.bottom - h) < 1 : Math.abs(r.top - (h - r.bottom)) < 1);
      return [`${w}x${h}`, fills && whole && placed, `${Math.round(r.width)}x${Math.round(r.height)} at ${Math.round(r.left)},${Math.round(r.top)}`];
    } finally {
      for (const [k, d] of saved) { if (d) Object.defineProperty(win, k, d); else delete win[k]; }
      win.fit();
    }
  });
  // The TV's 4K at 250 %, 1080p, 720p, ultrawide, 16:10.
  const SCREENS = [[1536, 864], [1920, 1080], [1280, 720], [2560, 1080], [1920, 1200]];
  checkRows('fit(): the launcher\'s stage whole on any screen, filling it one way, centred', fitAt(window, $('stage'), SCREENS));

  // ---- Setup's app list (setup.html's body), at the TV's size and at 1080p ---------------------
  const CATS = AUDIT_CATEGORIES;   // the catalog's (audit.js), as setup's demo has them
  const su = await framePage('<div id="stage"><div id="setup"><div class="su-top"><span class="su-muted">Setup</span><div class="su-dots" id="su-dots"></div>' +
    '<span class="su-muted" id="su-step"></span></div><main class="su-main" id="su-main"></main><div class="su-buttons" id="su-buttons"></div></div><div id="toasts"></div></div>',
    ['app.css', 'setup.css', 'tv.css', 'wifi.css'], ['icons.js', 'textinput.js', 'wifi.js', 'tv.js', 'setup.js'], 1536, 864);
  const sw = su.contentWindow, sd = sw.document;
  checkRows('fit(): setup\'s stage whole on any screen, filling it one way, centred', fitAt(sw, sd.getElementById('stage'), SCREENS));
  sw.goStep('apps');
  const suHeads = [...sd.querySelectorAll('.su-group > .su-cat')].map((e) => e.textContent);
  check('Setup: the apps by category, in the catalog\'s order', suHeads.join('|') === CATS.map((c) => c.name).join('|'), suHeads.join('|'));
  const suState = sw.eval('state');
  check('Setup: each app under its category', [...sd.querySelectorAll('.su-group')].every((g, i) =>
    [...g.querySelectorAll('.su-app')].every((a) => suState.apps.find((x) => `app:${x.id}` === a.dataset.id).category === CATS[i].id)));
  // Not the audit's: a heading over an app, or the list under the buttons, covers no focus there.
  for (const [w, h] of [[1536, 864], [1920, 1080]]) {
    await resize(su, w, h);
    const list = sd.querySelector('.su-apps').getBoundingClientRect(), buttons = sd.querySelector('.su-buttons').getBoundingClientRect();
    const items = [...sd.querySelectorAll('.su-cat, .su-app')].map((e) => e.getBoundingClientRect());
    const clashes = items.filter((r, i) => items.slice(i + 1).some((q) => overlap(r, q))).length;
    check(`Setup at ${w}x${h}: headings and apps clear of each other, the list above the buttons`, !clashes && list.bottom <= buttons.top, `${clashes} overlaps, ${list.bottom} / ${buttons.top}`);
  }
  sw.setFocus(sd.querySelector('.su-group:last-child .su-app'));
  sw.setFocus(sd.querySelector('.su-group:nth-child(3) .su-app'));
  const suHead = sd.querySelector('.su-group:nth-child(3) .su-cat').getBoundingClientRect();
  check('Setup: up to a category\'s first app: its heading shows too', suHead.top >= sd.querySelector('.su-apps').getBoundingClientRect().top - 1);
  su.remove();

  // ---- The keyboard's page (keyboard.html's body), its band at 1080p's width --------------------
  const kf = await framePage('<section id="kb"><div class="kb-head"><span class="kb-into">Typing into</span><span class="kb-field" id="kb-field"></span>' +
    '<span class="kb-typed" id="kb-typed"></span></div><div class="kb-rows" id="kb-rows"></div><footer class="hints" id="kb-hints"></footer></section>',
    ['app.css', 'keyboard.css'], ['icons.js', 'keyboard.js'], 1920, 440);
  const kw = kf.contentWindow, kd = kw.document;
  // Its window: the screen's width, 440/1080 of its height (KeyboardForm).
  checkRows('fit(): the keyboard\'s band whole in its window, filling it one way, centred at the bottom',
    fitAt(kw, kd.getElementById('kb'), SCREENS.map(([w, h]) => [w, Math.round(h * 440 / 1080)]), true));
  kw.onHost({ type: 'open', field: 'Search', password: false });
  const kAt = () => kw.eval('focus.row + "," + focus.col');
  const kKey = () => kd.querySelector('.kb-key.on');
  const kRow = (r) => [...kd.querySelectorAll(`.kb-key[data-r="${r}"]`)];
  const rows = kd.querySelectorAll('.kb-row').length, row1 = kRow(1).length;
  kw.onButton('left');
  check('Keyboard: left from a row\'s first key: its last', kAt() === `1,${row1 - 1}`, kAt());
  kw.onButton('right');
  check('Keyboard: right from a row\'s last key: its first', kAt() === '1,0', kAt());
  const nearest = (row, x) => kRow(row).reduce((best, e) => { const b = e.getBoundingClientRect(); const d = Math.abs(b.left + b.width / 2 - x);
    return d < best.d ? { d, c: Number(e.dataset.c) } : best; }, { d: Infinity, c: -1 }).c;
  kw.onButton('up');
  let x = kKey().getBoundingClientRect(); x = x.left + x.width / 2;
  kw.onButton('up');
  check('Keyboard: up from the top row: the bottom row, the key nearest below', kAt() === `${rows - 1},${nearest(rows - 1, x)}`, kAt());
  x = kKey().getBoundingClientRect(); x = x.left + x.width / 2;
  kw.onButton('down');
  check('Keyboard: down from the bottom row: the top row, the key nearest above', kAt() === `0,${nearest(0, x)}`, kAt());
  const firstKey = () => kRow(1)[0].textContent;
  kw.onButton('rt');
  check('Keyboard: RT: the symbols', kw.eval('symbols') === true && firstKey() === '!', firstKey());
  kw.onButton('rt');
  check('Keyboard: RT again: the letters', kw.eval('symbols') === false && firstKey() === 'q', firstKey());
  kw.onButton('lt');
  check('Keyboard: LT is still Shift', kw.eval('shift') === 'once' && firstKey() === 'Q', firstKey());
  kw.onHost({ type: 'open', field: 'Password', password: true });
  for (const [w, h] of [[1920, 440], [1536, 352]]) {   // the band's window: the screen's width, 440/1080 of its height
    await resize(kf, w, h);
    const hints = kd.getElementById('kb-hints'), band = kd.getElementById('kb').getBoundingClientRect(), s = w / 1920;
    // Each hint's buttons, as the bar shows them: LT and RT each their own, LB RB one; a password's Select too.
    const chips = [...hints.children].map((c) => [...c.querySelectorAll('.key')].map((k) => k.textContent).join(' '));
    const lastRight = hints.lastElementChild.getBoundingClientRect().right;
    check(`Keyboard at ${w} wide: every hint fits (a password's Show too): LT, RT, LB RB, Select`, hints.scrollWidth <= hints.clientWidth
      && lastRight <= band.right - 96 * s + 1 && ['LT', 'RT', 'LB RB', 'Select'].every((c) => chips.includes(c)),
      `${hints.scrollWidth} > ${hints.clientWidth}, ${lastRight} / ${band.right - 96 * s}: ${chips.join(' | ')}`);
    const bar = hints.getBoundingClientRect();
    const out = [...kd.querySelectorAll('.kb-key')].filter((k) => { const r = k.getBoundingClientRect(); return r.top < band.top - 0.5 || r.bottom > bar.top + 0.5; });
    check(`Keyboard at ${w} wide: the band fills its window, every key in it above the hints`, Math.abs(band.height - h) < 1 && !out.length,
      `${band.height} px in ${h}; outside: ${out.map((k) => k.textContent).join(' ')}`);
  }
  kf.remove();
});
