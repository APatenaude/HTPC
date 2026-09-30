'use strict';
// Self-test: Add a tile, Rename, setup's apps, the keyboard's page. Run by selftest.js, in its order.
selftestGroup(async ({ check, sent, lastSent, tileEl, PNG1 }) => {
  // ---- Add a tile, Rename, setup's apps and the on-screen keyboard ------------------------------
  // Fields are real inputs typed with the launcher's keyboard (none drawn in the page), clear of its
  // band. The keyboard's own page is checked in a frame. (A tile filling the screen: LauncherTests.)
  {
    const BAND = 440 / 1080;   // the keyboard's share of the screen's height (KeyboardForm.HeightOf1080, keyboard.css)
    const scale = () => $('stage').getBoundingClientRect().height / 1080;
    const overlap = (a, b) => a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1;
    const at = (id) => $('addtile').querySelector(`[data-id="${CSS.escape(id)}"]`);
    const kbTop = () => innerHeight * (1 - BAND);

    // On this box: two of three icons made so far; the third comes with the list sent again.
    reset('home');
    EXT.actions.addtile();
    press('rb');
    const programs = (calcLogo) => [{ name: 'Calculator', launchable: true, logo: calcLogo }, { name: 'Paint', launchable: true, logo: PNG1 },
      { name: 'Run', launchable: false, note: 'No program file', logo: PNG1 }];
    onHost({ type: 'library.programs', list: programs(null) });
    await new Promise((r) => setTimeout(r, 100));   // the icons load
    const icon = (name) => at(`ob-${name}`) && at(`ob-${name}`).querySelector('.appicon');
    check('On this box: a program shows its own icon (the host\'s), not the glyph', icon('Paint') && icon('Paint').classList.contains('has-logo') && icon('Paint').querySelector('img').src === PNG1);
    check('On this box: ... also one that cannot be added (its icon dimmed as its name)', icon('Run') && icon('Run').classList.contains('has-logo') && getComputedStyle(icon('Run')).opacity < 1);
    check('On this box: an icon not made yet: the glyph for now', icon('Calculator') && !icon('Calculator').querySelector('img') && !!icon('Calculator').querySelector('svg'));
    setFocus(at('ob-Paint'));
    const calcRow = at('ob-Calculator');
    onHost({ type: 'library.programs', list: programs(PNG1) });
    check('On this box: its icon arrives: in place, the focus left where it was', at('ob-Calculator') === calcRow && !!icon('Calculator').querySelector('img')
      && focusedEl() === at('ob-Paint'), focusedEl() && focusedEl().dataset.id);

    // The Website form: two real fields and Add tile, the launcher's keyboard for them.
    press('rb');
    const field = (f) => $('addtile').querySelector(`input[data-field="${f}"]`);
    check('Website: real text fields, no keyboard drawn in the page', field('url') && field('name') && !$('addtile').querySelector('.kbi, [data-act="key"]') && !!at('wf-add'));
    check('Website: it opens on the address', focusedEl() === at('wf-url'), focusedEl() && focusedEl().dataset.id);
    sent.length = 0;
    press('a');
    check('Website: A on the address: the on-screen keyboard for it, the field focused', lastSent('text.keyboard') && lastSent('text.keyboard').field === 'Website address'
      && lastSent('text.keyboard').password === false && document.activeElement === field('url'), JSON.stringify(lastSent('text.keyboard')));
    textInsert('example.org/tv');                     // as the keyboard and the phone type
    check('Website: typed text shows in the preview', $('addtile').querySelector('.ws-cname').textContent === 'example.org', $('addtile').querySelector('.ws-cname').textContent);
    onHost({ type: 'state', volume: 41 });            // a host push: the tab drawn again
    render();                                         // the clock
    check('Website: typed text survives a redraw, the field keeps the focus and the caret', field('url').value === 'example.org/tv' && document.activeElement === field('url')
      && field('url').selectionStart === 14, `${field('url').value} ${document.activeElement && document.activeElement.id}`);
    textKeyboardAt(1 - BAND);
    check('Website: the keyboard up at the bottom: the address is above it, nothing lifted', !document.querySelector('[data-kb-lift]')
      && at('wf-add').getBoundingClientRect().bottom <= kbTop(), `${at('wf-add').getBoundingClientRect().bottom} > ${kbTop()}`);
    textKeyboardAt(null);
    const parts = ['.at-header', '.lib-field[data-id="wf-url"]', '.lib-field[data-id="wf-name"]', '.lib-buttons', '.ws-preview', 'footer.hints']
      .map((s) => [s, $('addtile').querySelector(s).getBoundingClientRect()]);
    const clash = parts.flatMap(([s, r], i) => parts.slice(i + 1).filter(([, q]) => overlap(r, q)).map(([t]) => `${s} / ${t}`));
    check('Website: nothing in the form overlaps anything else', !clash.length, clash.join(', '));
    check('Website: no line on how to bring the keyboard up (the hints say Type)', !/R3|keyboard/i.test($('addtile').querySelector('.ws-form').textContent)
      && /Type/.test($('addtile').querySelector('footer.hints').textContent), $('addtile').querySelector('.ws-form').textContent);
    sent.length = 0;
    textKey('enter');                                 // Enter on the keyboard
    check('Website: Enter adds it (the address typed, trimmed)', lastSent('library.addWebsite') && lastSent('library.addWebsite').url === 'example.org/tv', JSON.stringify(lastSent('library.addWebsite')));
    onHost({ type: 'library.websiteResult', ok: true, name: 'example.org' });
    check('Website: added: the form cleared for the next one, the card says so', field('url').value === '' && /Added/.test($('addtile').querySelector('.ws-preview').textContent));
    press('down');
    check('Website: the focus off the address: the field lets go', focusedEl() === at('wf-name') && document.activeElement !== field('url'));
    press('a'); sent.length = 0;
    textInsert('A name much longer than a tile can hold');
    check('Website: a name keeps to 24 characters', field('name').value.length === 24, field('name').value);
    textKey('enter');
    check('Website: Enter in the name with no address: on to the address, the keyboard with it', focusedEl() === at('wf-url') && document.activeElement === field('url')
      && lastSent('text.keyboard') && lastSent('text.keyboard').field === 'Website address' && !lastSent('library.addWebsite'));
    sent.length = 0;
    press('b');
    check('Website: B leaves, and the keyboard with the field', state.view !== 'addtile' && lastSent('text.done') && !textField());

    // Rename: the keyboard up for its field at once, Enter saves.
    reset('home');
    EXT.actions['tile-options'](tileEl('youtube'), 'youtube');
    setFocus($('tileopts').querySelector('[data-id="opt-rename"]'));
    sent.length = 0;
    press('a');
    const rn = () => $('rename').querySelector('input[data-field="draft"]');
    check('Rename: a real field, no keyboard in the page; the keyboard up for it, the caret after the name', state.view === 'rename' && rn() && !$('rename').querySelector('.kbi')
      && lastSent('text.keyboard') && lastSent('text.keyboard').field === 'Tile name' && document.activeElement === rn() && rn().selectionStart === rn().value.length && rn().value === 'YouTube');
    check('Rename: the field is above the keyboard', $('rename').querySelector('.lib-buttons').getBoundingClientRect().bottom <= kbTop());
    textKey('backspace'); textKey('backspace'); textKey('backspace');
    textInsert(' Kids');
    check('Rename: the count follows the typing', $('rename').querySelector('.rn-count').textContent === `${'YouT Kids'.length}/24`, $('rename').querySelector('.rn-count').textContent);
    textKey('enter');
    check('Rename: Enter saves the name, back on Tile options', lastSent('tile.rename') && lastSent('tile.rename').name === 'YouT Kids' && state.view === 'tileopts' && !textField(),
      JSON.stringify(lastSent('tile.rename')));
    press('b');

    // The library by category, apps then websites in each, anything else last (Other).
    const CATS = [['movies', 'Movies & shows'], ['canada', 'Canadian TV'], ['sports', 'Sports'], ['music', 'Music'], ['games', 'Games'], ['media', 'Your media & tools']]
      .map(([id, name]) => ({ id, name }));
    const card = (i, type, category) => ({ id: `${type}${i}`, name: `${type === 'app' ? 'App' : 'Site'} ${i}`, glyph: 'play', color: '#FF5B52', type, category,
      state: type === 'app' ? 'install' : 'add', canUninstall: type === 'app' });
    const libApps = Array.from({ length: 18 }, (_, i) => card(i, 'app', i === 17 ? 'nosuch' : CATS[i % 6].id));
    const libSites = Array.from({ length: 22 }, (_, i) => card(i, 'website', CATS[(i + 2) % 6].id));
    reset('home');
    EXT.actions.addtile();
    onHost({ type: 'library.catalog', apps: libApps, sites: libSites, categories: CATS });
    const heads = [...$('addtile').querySelectorAll('.lc-group > .at-label')].map((e) => e.textContent);
    check('Library: by category, in the catalog\'s order, then Other', heads.join('|') === [...CATS.map((c) => c.name), 'Other'].join('|'), heads.join('|'));
    const groupOf = (id) => at(id).closest('.lc-group').dataset.cat;
    check('Library: each card under its category, apps before websites', groupOf('app-app0') === 'movies' && groupOf('site-website4') === 'movies' && groupOf('app-app17') === ''
      && at('app-app0').compareDocumentPosition(at('site-website4')) === Node.DOCUMENT_POSITION_FOLLOWING);
    const lc = at('app-app1').getBoundingClientRect(), top = at('app-app1').querySelector('.lc-top').getBoundingClientRect(), st = at('app-app1').querySelector('.lc-status').getBoundingClientRect();
    check('Library: an app card keeps no room for a description', lc.height / scale() <= 132 && (st.top - top.bottom) / scale() <= 24, `${lc.height / scale()} px, ${(st.top - top.bottom) / scale()} px gap`);
    setFocus(at('app-app0'));
    const jumps = [];
    for (const b of ['rt', 'rt', 'rt', 'rt', 'rt', 'rt', 'rt', 'lt', 'lt']) { press(b); jumps.push(groupOf(focusedEl().dataset.id)); }
    check('Library: RT, LT: the first card of the next or the category before, and stop at the ends',
      jumps.join(',') === 'canada,sports,music,games,media,,,media,games' && focusedEl() === at('app-app4'), jumps.join(','));
    const main = $('addtile').querySelector('.at-main');
    const headShows = () => focusedEl().closest('.lc-group').querySelector('.at-label').getBoundingClientRect().top >= main.getBoundingClientRect().top - 1;
    press('lt'); press('lt');
    check('Library: back up to a category: its heading shows above its first card', headShows() && main.scrollTop > 0, `${main.scrollTop}`);
    const bar = $('addtile').querySelector('footer.hints');
    check('Library: the hints say LT RT Category and fit', /LT\s*RT\s*Category/.test(bar.textContent) && /LB\s*RB\s*Tabs/.test(bar.textContent) && bar.scrollWidth <= bar.clientWidth,
      `${bar.scrollWidth} > ${bar.clientWidth}: ${bar.textContent}`);
    reset('home');

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
    const resize = async (f, w, h) => { f.style.width = `${w}px`; f.style.height = `${h}px`; await new Promise((r) => setTimeout(r, 50)); };

    // Setup's app list (setup.html's body), at the TV's size and at 1080p.
    const su = await framePage('<div id="stage"><div id="setup"><div class="su-top"><span class="su-muted">Setup</span><div class="su-dots" id="su-dots"></div>' +
      '<span class="su-muted" id="su-step"></span></div><main class="su-main" id="su-main"></main><div class="su-buttons" id="su-buttons"></div></div><div id="toasts"></div></div>',
      ['app.css', 'setup.css', 'tv.css', 'wifi.css'], ['icons.js', 'textinput.js', 'wifi.js', 'tv.js', 'setup.js'], 1536, 864);
    const sw = su.contentWindow, sd = sw.document;
    sw.goStep('apps');
    const suHeads = [...sd.querySelectorAll('.su-group > .su-cat')].map((e) => e.textContent);
    check('Setup: the apps by category, in the catalog\'s order', suHeads.join('|') === CATS.map((c) => c.name).join('|'), suHeads.join('|'));
    const suState = sw.eval('state');
    check('Setup: each app under its category', [...sd.querySelectorAll('.su-group')].every((g, i) =>
      [...g.querySelectorAll('.su-app')].every((a) => suState.apps.find((x) => `app:${x.id}` === a.dataset.id).category === CATS[i].id)));
    for (const [w, h] of [[1536, 864], [1920, 1080]]) {
      await resize(su, w, h);
      sw.fit();
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

    // The keyboard's page (keyboard.html's body), its band at 1080p's width.
    const kf = await framePage('<section id="kb"><div class="kb-head"><span class="kb-into">Typing into</span><span class="kb-field" id="kb-field"></span>' +
      '<span class="kb-typed" id="kb-typed"></span></div><div class="kb-rows" id="kb-rows"></div><footer class="hints" id="kb-hints"></footer></section>',
      ['app.css', 'keyboard.css'], ['icons.js', 'keyboard.js'], 1920, 440);
    const kw = kf.contentWindow, kd = kw.document;
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
      kw.fit();
      const hints = kd.getElementById('kb-hints'), band = kd.getElementById('kb').getBoundingClientRect(), s = w / 1920;
      // Each hint's buttons and label, as the bar shows them: LT and RT each their own, LB RB one.
      const chips = [...hints.children].map((c) => c.textContent);
      const lastRight = hints.lastElementChild.getBoundingClientRect().right;
      check(`Keyboard at ${w} wide: every hint fits (a password's Show too): LT Shift, RT Symbols, LB RB Cursor`, hints.scrollWidth <= hints.clientWidth
        && lastRight <= band.right - 96 * s + 1 && ['LTShift', 'RTSymbols', 'LBRBCursor', 'SelectShow password'].every((c) => chips.includes(c)),
        `${hints.scrollWidth} > ${hints.clientWidth}, ${lastRight} / ${band.right - 96 * s}: ${chips.join(' | ')}`);
      const keyH = kRow(1)[0].getBoundingClientRect().height, lowest = kRow(rows - 1)[0].getBoundingClientRect(), bar = hints.getBoundingClientRect();
      check(`Keyboard at ${w} wide: 440/1080 of the screen (was 560), keys ${Math.round(keyH / s)} px`, Math.abs(band.height - 440 * s) < 1 && keyH >= 46 * s
        && lowest.bottom + 4 * s <= bar.top, `${band.height} px, keys ${keyH}, last row ${lowest.bottom} / hints ${bar.top}`);
    }
    kf.remove();
  }
});
