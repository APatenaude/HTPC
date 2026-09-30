'use strict';
// Self-test: Add a tile, Rename. Run by selftest.js, in its order.
// Fields are real inputs typed with the launcher's keyboard (none drawn in the page), clear of its
// band. (A tile filling the screen: LauncherTests.)
selftestGroup(async ({ check, pin, until, imagesIn, sent, lastSent, tileEl, PNG1 }) => {
  // ---- Add a tile: on this box, a website ------------------------------------------------------
  selftestFresh();
  state.libraryAvailable = true;
  const BAND = 440 / 1080;   // the keyboard's share of the screen's height (KeyboardForm.HeightOf1080, keyboard.css)
  const at = (id) => $('addtile').querySelector(`[data-id="${CSS.escape(id)}"]`);
  const kbTop = () => innerHeight * (1 - BAND);
  const scale = () => $('stage').getBoundingClientRect().height / 1080;
  const overlap = (a, b) => a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1;

  // On this box: two of three icons made so far; the third comes with the list sent again.
  EXT.actions.addtile();
  press('rb');
  const programs = (calcLogo) => [{ name: 'Calculator', launchable: true, logo: calcLogo }, { name: 'Paint', launchable: true, logo: PNG1 },
    { name: 'Run', launchable: false, note: 'No program file', logo: PNG1 }];
  onHost({ type: 'library.programs', list: programs(null) });
  await until(() => imagesIn($('addtile')));   // the icons load
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
  // Every part clear of the others (the walker sees only what covers a field or a button, not a
  // field painted over the preview or the header, nor the preview over the hints).
  const parts = ['.at-header', '.lib-field[data-id="wf-url"]', '.lib-field[data-id="wf-name"]', '.lib-buttons', '.ws-preview', 'footer.hints']
    .map((s) => [s, $('addtile').querySelector(s).getBoundingClientRect()]);
  const clash = parts.flatMap(([s, r], i) => parts.slice(i + 1).filter(([, q]) => overlap(r, q)).map(([u]) => `${s} / ${u}`));
  check('Website: nothing in the form overlaps anything else', !clash.length, clash.join(', '));
  // No line on how to bring the keyboard up (the hints say Type, d69fec9): no button chip in the form.
  const form = $('addtile').querySelector('.ws-form');
  pin('the Website form: no line on how to bring the keyboard up', !form.querySelector('.key') && !/R3/.test(form.textContent), form.textContent.slice(0, 120));
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

  // ---- Rename: the keyboard up for its field at once, Enter saves ---------------------------------
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

  // ---- The library by category, apps then websites in each, anything else last (Other) -----------
  const CATS = AUDIT_CATEGORIES;   // the catalog's (audit.js)
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
  // No room kept for a description (9c3bc25 took them out; the size, 7c9465f), in the stage's pixels.
  const lc = at('app-app1').getBoundingClientRect(), top = at('app-app1').querySelector('.lc-top').getBoundingClientRect(), st = at('app-app1').querySelector('.lc-status').getBoundingClientRect();
  pin('a library app card at most 132 px tall, its status at most 24 px under its name', lc.height / scale() <= 132 && (st.top - top.bottom) / scale() <= 24,
    `${Math.round(lc.height / scale())} px, ${Math.round((st.top - top.bottom) / scale())} px gap`);
  setFocus(at('app-app0'));
  const jumps = [];
  for (const b of ['rt', 'rt', 'rt', 'rt', 'rt', 'rt', 'rt', 'lt', 'lt']) { press(b); jumps.push(groupOf(focusedEl().dataset.id)); }
  check('Library: RT, LT: the first card of the next or the category before, and stop at the ends',
    jumps.join(',') === 'canada,sports,music,games,media,,,media,games' && focusedEl() === at('app-app4'), jumps.join(','));
  const main = $('addtile').querySelector('.at-main');
  const headShows = () => focusedEl().closest('.lc-group').querySelector('.at-label').getBoundingClientRect().top >= main.getBoundingClientRect().top - 1;
  press('lt'); press('lt');
  check('Library: back up to a category: its heading shows above its first card', headShows() && main.scrollTop > 0, `${main.scrollTop}`);
  const bar = $('addtile').querySelector('footer.hints'), barKeys = [...bar.querySelectorAll('.key')].map((k) => k.textContent);
  check('Library: the hints have LT RT (categories) and LB RB (tabs), and fit', ['LT', 'RT', 'LB', 'RB'].every((k) => barKeys.includes(k)) && bar.scrollWidth <= bar.clientWidth,
    `${bar.scrollWidth} > ${bar.clientWidth}: ${barKeys.join(' ')}`);
  reset('home');
});
