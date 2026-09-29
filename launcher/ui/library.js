'use strict';

// App library and tile editing (SPEC W1, W5). A feature script loaded after app.js: it registers
// its screens and actions with the launcher's UI registry (addView / onAction / hostMessage / ask)
// and drives them through the shared helpers (state, send, go, back, reset, render, toast...).
//
// Screens: addtile (Library / On this box / Website tabs), tileopts (Start on a home tile),
// rename, changeicon. Home-screen bits (the "+" tile, Start = Tile options, moving a tile) are
// wired through onAction hooks app.js calls.
//
// Typing (a website's address and name, a tile's new name) is in real text fields with the
// launcher's own on-screen keyboard, as the Wi-Fi password (textinput.js): A or R3 on a field
// opens it across the bottom of the screen, Enter there adds or saves. No keyboard of its own in
// the page any more (the owner, 29 Sept 2026: two keyboards, the page's clipping into the form).
//
// Adding stays on Add tile (the owner adds several in a row): what was just added shows as on
// the home screen where it is, the focus left on it. An app to install starts at once, with no
// dialog: its card and its home tile show the progress, and that it did not install.
//
// The owner's rule: if it's installed, it's on the home screen. An app the box installs is
// Install, installing, or "On home screen" (the host gives an installed app its tile), and
// taking it off Home is uninstalling it (X on its card, Remove on its tile, both asked first).
// A website and the Browser (Edge, built into Windows) install nothing: A adds the tile, X
// takes it away.

(function () {
  const COLS = 4;

  const STATUS = {
    home: { label: 'On home screen', glyph: 'home', color: '#7FD1AE' },
    install: { label: 'Install', glyph: 'download', color: '#8CC2FF' },
    add: { label: 'Add tile', glyph: 'plus', color: '#8CC2FF' },
  };
  const UNINSTALL_TEXT = 'Its tile goes too. Your sign-in and settings are kept.';

  // Icon-picker choices (kept in step with TileStore.IconChoices / ColorChoices host-side).
  const ICONS = ['play', 'film', 'library', 'music', 'tv', 'globe', 'chat', 'youtube', 'moon',
    'controller', 'app', 'speaker', 'search', 'download', 'home', 'sun', 'power', 'info'];
  const COLORS = ['#FF5B52', '#FF8A1F', '#F5B82E', '#1ED760', '#3DC0F0', '#3CCB9A', '#7C8CFF',
    '#B08CFF', '#FF7AB6', '#F5D16B', '#5AB0FF', '#F3F2EF'];

  const lib = {
    tab: 'library',
    catalog: { apps: [], sites: [], categories: [] },
    programs: [],
    progress: { current: null, pending: [] },
    target: null,          // tile id being edited (tileopts / rename / changeicon)
    website: { name: '', url: '', added: null },   // added: the site just added, till typing starts
    addingProgram: null,   // the On this box row whose tile the host is adding
    moveOrigin: null,      // tile order to restore if a move is cancelled
    draft: '',             // the name being typed in the rename view
    // Installs: the ids asked to install (their home tile shows them installing),
    // those queued or running, those just out of the queue (the catalog that follows says
    // whether they installed), and those that did not.
    homeBound: new Set(), queued: new Set(), finished: new Set(), failed: new Set(),
  };

  const el = (id) => document.getElementById(id);

  // ---- Home hooks (app.js calls these) -----------------------------------------------------

  onAction('addtile', openAddTile);
  // Always on its first item, never where the last one's focus was left (Remove, after one).
  onAction('tile-options', (node, id) => { lib.target = id; state.memory.tileopts = null; go('tileopts'); });
  onAction('tile-move', (node, button) => moveButton(button));

  // A held on a home tile (app.js): move mode at once, with a light buzz on the controller.
  // heldMove, while that A is still down: whether the tile has moved since. Let go after a move,
  // it drops there; without one, move mode stays (A drops, B cancels).
  let heldMove = null;
  onAction('tile-hold', (node, id) => { lib.target = id; startMove(); heldMove = { moved: false }; send({ type: 'controller.buzz' }); });

  function moveButton(button) {
    switch (button) {
      case 'up': case 'down': case 'left': case 'right': moveTile(button); return true;
      case 'a': case 'start': dropTile(); return true;
      case 'aUp': { const moved = heldMove && heldMove.moved; heldMove = null; if (moved) dropTile(); return true; }
      case 'b': cancelMove(); return true;
      case 'home': case 'homeHold': cancelMove(); return false;   // then app opens the menu / power
      default: return true;                                       // swallow everything else while moving
    }
  }

  function startMove() {
    lib.moveOrigin = state.tiles.map((t) => t.id);
    heldMove = null;
    state.moving = lib.target;
    reset('home');
    focusMoving();
  }
  // The tile trades places with the one it moves onto (the two slide, nothing else moves).
  function moveTile(dir) {
    const arr = state.tiles;
    const i = arr.findIndex((t) => t.id === state.moving);
    if (i < 0) return;
    let j = dir === 'left' ? i - 1 : dir === 'right' ? i + 1 : dir === 'up' ? i - COLS : i + COLS;
    // Down onto a shorter last row: its last tile.
    if (dir === 'down' && j >= arr.length && Math.floor(i / COLS) < Math.floor((arr.length - 1) / COLS)) j = arr.length - 1;
    if (j < 0 || j >= arr.length || j === i) return;
    [arr[i], arr[j]] = [arr[j], arr[i]];
    if (heldMove) heldMove.moved = true;
    render();
    focusMoving();
  }
  function focusMoving() {
    const node = document.querySelector('#tiles .tile.moving');
    if (node) setFocus(node);
  }
  function dropTile() {
    send({ type: 'tile.order', ids: state.tiles.map((t) => t.id) });
    state.moving = null;
    heldMove = null;
    render();
    toast('Moved');
  }
  function cancelMove() {
    heldMove = null;
    if (lib.moveOrigin) {
      const byId = Object.fromEntries(state.tiles.map((t) => [t.id, t]));
      state.tiles = lib.moveOrigin.map((id) => byId[id]).filter(Boolean);
    }
    state.moving = null;
    lib.moveOrigin = null;
    render();
  }

  // ---- Tile options ------------------------------------------------------------------------

  function targetTile() { return state.tiles.find((t) => t.id === lib.target) || null; }

  addView('tileopts', {
    overlay: true,
    demo() { lib.target = state.tiles[0] && state.tiles[0].id; },
    render() {
      const t = targetTile();
      if (!t) { back(); return; }
      const rows = [['opt-move', 'move', 'Move'], ['opt-rename', 'pencil', 'Rename'], ['opt-icon', 'image', 'Change icon']];
      // In place (patchHtml): redrawn by the clock and host pushes, it popped in again each time.
      // An installed app's tile goes only with the app (the host's tile.uninstall).
      patchHtml(el('tileopts'),
        '<aside class="to-panel">' +
          `<div class="to-head">${appIcon(t, 40)}<span class="to-name">${esc(t.name)}</span></div>` +
          rows.map(([act, glyph, label]) => `<button class="to-item" data-nav data-id="${act}" data-act="${act}">${icon(glyph, 32, 2)}${label}</button>`).join('') +
          '<div class="to-sep"></div>' +
          `<button class="to-item danger" data-nav data-id="opt-remove" data-act="opt-remove">${icon('trash', 32, 2)}${t.uninstall ? 'Uninstall' : 'Remove from home'}</button>` +
        '</aside>' +
        `<footer class="hints">${hints([['A', 'Select'], ['B', 'Close']])}</footer>`);
    },
    layout() { placeByTile(el('tileopts').querySelector('.to-panel'), lib.target); },
  });

  // Tile options open beside their tile: to its right, or to its left at the right edge, level
  // with it, and kept on screen above the hints. In stage pixels: the tile's offsets are from
  // #home, which covers the stage.
  function placeByTile(panel, id) {
    const tile = document.querySelector(`#tiles [data-id="tile:${CSS.escape(id)}"]`);
    if (!panel || !tile) return;
    const gap = 32, x = tile.offsetLeft, w = tile.offsetWidth;
    const left = x + w + gap + panel.offsetWidth <= 1920 - 48 ? x + w + gap : x - gap - panel.offsetWidth;
    panel.style.left = `${Math.max(48, left)}px`;
    panel.style.top = `${Math.max(48, Math.min(tile.offsetTop, 1080 - 96 - 24 - panel.offsetHeight))}px`;
  }

  onAction('opt-move', startMove);
  // Rename opens with the keyboard up for its field, the caret after the name.
  onAction('opt-rename', () => {
    const t = targetTile();
    lib.draft = t ? t.name : '';
    state.memory.rename = null;
    go('rename');
    typeInto(el('rename').querySelector('[data-id="rn-name"]'));
  });
  onAction('opt-icon', () => go('changeicon'));
  onAction('opt-remove', removeTarget);

  function removeTarget() {
    const t = targetTile();
    if (!t) return;
    // An installed app is on the home screen as long as it is installed: removing its tile is
    // uninstalling it (asked first; the host takes the tile away once it is done).
    if (t.uninstall) {
      if (!state.libraryAvailable) { toast('Uninstalling from the TV isn’t set up yet. Run setup once more.', 'warn'); return; }
      ask({ title: `Uninstall ${t.name}?`, text: UNINSTALL_TEXT, yes: 'Uninstall', onYes: () => { uninstallApp(t.id, t.name); reset('home'); } });
      return;
    }
    // A custom tile (added website or program) keeps its details only here, so ask first (SPEC
    // decision); a catalog app can always be re-added from the library, so it goes at once.
    if (t.custom) {
      ask({
        title: `Remove ${t.name}?`,
        // A website's own Edge profile goes too (MainForm.Library.cs RemoveTile): signed out.
        text: t.website ? 'Its address and settings are only here, so they will be lost, and its sign-in on this box goes too.'
          : 'Its address and settings are only here, so they will be lost.',
        yes: 'Remove',
        onYes: () => { send({ type: 'tile.remove', id: t.id }); toast(`${t.name} removed`); reset('home'); },
      });
    } else {
      send({ type: 'tile.remove', id: t.id });
      toast(`${t.name} removed`);
      reset('home');
    }
  }

  // ---- Rename ------------------------------------------------------------------------------
  // The name in a text field, near the top: the keyboard across the bottom never covers it.

  addView('rename', {
    demo() { lib.target = state.tiles[0] && state.tiles[0].id; lib.draft = (state.tiles[0] && state.tiles[0].name) || ''; },
    render() {
      const t = targetTile();
      if (!t) { back(); return; }
      // In place: the clock and host pushes change nothing here, and the field keeps what is typed.
      drawFields(el('rename'), () => patchHtml(el('rename'),
        '<header class="at-header"><h1>Rename tile</h1></header>' +
        '<main class="rn-main">' +
          `<div class="rn-row"><span class="rn-icon">${appIcon(t, 64)}</span>` +
            '<div class="lib-field rn-field" data-nav data-id="rn-name" data-act="field">' +
              `<span class="lib-label">Name</span>${fieldHtml('rn-input', 'draft', lib.draft, 24, 'Type a name', 'Tile name')}` +
              `<span class="rn-count">${lib.draft.length}/24</span>` +
            '</div></div>' +
          '<div class="lib-buttons">' +
            '<div class="lib-btn primary" data-nav data-id="rn-save" data-act="rn-save">Save</div>' +
            '<div class="lib-btn" data-nav data-id="rn-cancel" data-act="rn-cancel">Cancel</div>' +
          '</div>' +
          `<p class="lib-note">${icon('keyboard', 28)}Type with the on-screen keyboard (A or R3) or on your phone; Enter saves.</p>` +
        '</main>' +
        `<footer class="hints">${hints(renameHints(focusedEl()))}</footer>`));
    },
    focused(node) {
      letGoOfFields(node);
      const bar = el('rename').querySelector('footer.hints');
      if (bar) patchHtml(bar, hints(renameHints(node)));
    },
    press(button, node) {
      if (button === 'start') { saveRename(); return true; }
      if (button === 'b') { cancelRename(); return true; }
      if (button === 'r3') { typeInto(node); return true; }
      return false;
    },
  });

  function renameHints(node) {
    return [['A', node && node.dataset.act === 'field' ? 'Type' : 'Select'], ['Start', 'Save'], ['B', 'Cancel']];
  }
  function saveRename() { closeFields(); send({ type: 'tile.rename', id: lib.target, name: lib.draft.trim() }); toast('Renamed'); back(); }
  function cancelRename() { closeFields(); back(); }
  onAction('rn-save', saveRename);
  onAction('rn-cancel', cancelRename);

  // ---- Change icon -------------------------------------------------------------------------

  addView('changeicon', {
    demo() { lib.target = state.tiles[0] && state.tiles[0].id; },
    render() {
      const t = targetTile();
      if (!t) { back(); return; }
      // The app's own logo (when the host has one) comes first; picking a glyph or a colour
      // replaces it, "Logo" brings it back.
      const onLogo = !!t.logo;
      const logoChoice = t.logoUrl
        ? `<div class="ci-logo"><button class="ci-glyph wide${onLogo ? ' on' : ''}" data-nav data-id="g-logo" data-act="glyph" data-arg="logo">` +
            `${appIcon({ ...t, logo: t.logoUrl }, 40)}Logo</button></div>` : '';
      patchHtml(el('changeicon'),
        '<main class="lib-center"><div class="ci-wrap">' +
          `<div class="ci-preview">${appIcon(t, 80)}<span class="ci-name">${esc(t.name)}</span></div>` + logoChoice +
          '<div class="ci-glyphs">' + ICONS.map((g) =>
            `<button class="ci-glyph${!onLogo && g === t.glyph ? ' on' : ''}" data-nav data-id="g-${g}" data-act="glyph" data-arg="${g}">${icon(g, 40)}</button>`).join('') + '</div>' +
          '<div class="ci-colors">' + COLORS.map((c) =>
            `<button class="ci-color${!onLogo && c.toUpperCase() === String(t.color).toUpperCase() ? ' on' : ''}" data-nav data-id="c-${c}" data-act="color" data-arg="${c}"><span style="background:${c}"></span></button>`).join('') + '</div>' +
        '</div></main>' +
        `<footer class="hints">${hints([['A', 'Choose'], ['B', 'Done']])}</footer>`);
    },
  });

  // The choice shows as picked at once (the host's tiles confirm it a moment later).
  onAction('glyph', (node, glyph) => {
    const t = targetTile();
    if (t) { if (glyph === 'logo') t.logo = t.logoUrl; else { t.glyph = glyph; t.logo = null; } }
    send({ type: 'tile.icon', id: lib.target, glyph });
    render();
  });
  onAction('color', (node, color) => {
    const t = targetTile();
    if (t) { t.color = color; t.logo = null; }
    send({ type: 'tile.icon', id: lib.target, color });
    render();
  });

  // ---- Add tile: Library / On this box / Website -------------------------------------------

  const TABS = ['library', 'onbox', 'website'];

  addView('addtile', {
    demo(arg) { demoData(); lib.tab = TABS.includes(arg) ? arg : 'library'; },
    render() {
      const tabs = [['library', 'Library'], ['onbox', 'On this box'], ['website', 'Website']].map(([id, label]) =>
        `<div class="at-tab${id === lib.tab ? ' on' : ''}">${label}<span class="at-underline"></span></div>`).join('');
      const body = lib.tab === 'library' ? libraryTabHtml() : lib.tab === 'onbox' ? onboxTabHtml() : websiteTabHtml();
      const hintList = tabHints(null);
      // The same tab again (install progress, a logo, the catalog after an add): its cards are
      // updated in place (patchHtml), their logos not loaded again, the list's scroll and the
      // focus left be. Another tab is drawn afresh.
      const old = el('addtile').querySelector('.at-main');
      if (old && el('addtile').dataset.tab === lib.tab) {
        drawFields(old, () => patchHtml(old, body));
        // The focused card may have changed (installing, uninstalling, added): so have its hints.
        const f = focusedEl(), bar = el('addtile').querySelector('footer.hints');
        if (bar && f && old.contains(f)) patchHtml(bar, hints(tabHints(f)));
        return;
      }
      drawFields(el('addtile'), () => {
        el('addtile').innerHTML =
          '<header class="at-header"><h1>Add a tile</h1>' + `<nav class="at-tabs" aria-label="Tile source">${tabs}</nav></header>` +
          `<main class="at-main">${body}</main>` +
          `<footer class="hints">${hints(hintList)}</footer>`;
      });
      el('addtile').dataset.tab = lib.tab;
    },
    // The tab's list scrolls to the focus, with room for its ring (and a category's first row,
    // its heading) above the hints; the hints say what A and X do there.
    focused(node) {
      letGoOfFields(node);
      const main = node.closest('.at-main');
      if (main) { scrollIntoBox(node, main, 40); headingInView(node, main); listEdges(main); }
      const bar = el('addtile').querySelector('footer.hints');
      if (bar) patchHtml(bar, hints(tabHints(node)));
    },
    press(button, node) {
      if (button === 'lb') { switchTab(-1); return true; }
      if (button === 'rb') { switchTab(1); return true; }
      if (lib.tab === 'library' && (button === 'lt' || button === 'rt')) { jumpCategory(button === 'rt' ? 1 : -1, node); return true; }
      if (lib.tab === 'website') {
        if (button === 'start') { saveWebsite(); return true; }
        if (button === 'r3') { typeInto(node); return true; }
      }
      if (button === 'b' || button === 'home' || button === 'homeHold') closeFields();   // and on, as usual
      return false;
    },
  });

  // The hints for the focused card or row: A, named for what it offers (it asks first), where it
  // does something. The bumpers (tabs) and, in the library, the triggers (categories) share a chip each.
  function tabHints(node) {
    const tabs = [[['LB', 'RB'], 'Tabs'], ['B', 'Back']];
    if (lib.tab === 'website') {
      const field = !node || node.dataset.act === 'field';
      return [['A', field ? 'Type' : 'Add tile'], ...(field ? [['Start', 'Add tile']] : []), ...tabs];
    }
    if (lib.tab === 'onbox') {
      const add = !node || node.dataset.act === 'addprog' && !node.classList.contains('on-home');
      return [...(add ? [['A', 'Add to home']] : []), ...tabs];
    }
    const cats = [['LT', 'RT'], 'Category'];
    const card = node && node.dataset.arg ? findCard(node.dataset.arg) : null;
    if (!card) return [['A', 'Install or add'], cats, ...tabs];
    const kind = cardKind(card);
    const a = kind === 'install' ? (lib.failed.has(card.id) ? 'Try again' : 'Install') : kind === 'uninstall' ? 'Uninstall'
      : kind === 'add' ? 'Add to Home' : kind === 'remove' ? 'Remove from Home' : cardStatus(card).wizard ? 'Show the installer' : null;
    return [...(a ? [['A', a]] : []), cats, ...tabs];
  }

  function openAddTile() {
    lib.tab = 'library';
    lib.website = { name: '', url: '', added: null };
    go('addtile');
    send({ type: 'library.list' });
  }
  function switchTab(step) { setTab(TABS[(TABS.indexOf(lib.tab) + step + TABS.length) % TABS.length]); }
  function setTab(tab) {
    if (!TABS.includes(tab) || tab === lib.tab) return;
    closeFields();
    lib.tab = tab;
    if (tab === 'library') send({ type: 'library.list' });
    if (tab === 'onbox') { hostAsked('programs', 15000); send({ type: 'library.startMenu' }); }
    render();
    const first = el('addtile').querySelector('.at-main [data-nav]');
    if (first) setFocus(first);
  }
  onAction('tab', (node, id) => setTab(id));

  // What a card (and a tile being installed) says. Installing looks nothing like "Install": a
  // spinner, the progress, a bar; one that did not install says so in amber.
  function cardStatus(card) {
    if (card.state === 'uninstalling') {
      const p = lib.progress.current;
      return !p || p.id === card.id ? { label: 'Uninstalling…', spin: true, color: '#8CC2FF', busy: true, percent: null }
        : { label: 'Waiting to uninstall', glyph: 'timer', color: '#B3B5BC', busy: true, percent: 0 };
    }
    if (card.state === 'installing') {
      const p = lib.progress.current;
      // Nothing running yet: just asked, it starts (the host's progress follows).
      if (!p || p.id === card.id) {
        // An installer the user finishes on screen (RetroBat's): A brings it up again.
        if (p && p.phase === 'wizard') return { label: 'Finish the installer', spin: true, color: '#8CC2FF', busy: true, percent: null, wizard: true };
        const pct = p && p.phase === 'download' && p.percent != null ? p.percent : null;
        return { label: pct !== null ? `Downloading… ${pct}%` : 'Installing…', spin: true, color: '#8CC2FF', busy: true, percent: pct };
      }
      return { label: 'Waiting to install', glyph: 'timer', color: '#B3B5BC', busy: true, percent: 0 };
    }
    if (card.state === 'install' && lib.failed.has(card.id)) return { label: 'Didn’t install · A to try again', glyph: 'warn', color: '#F2B24C', failed: true };
    return STATUS[card.state] || STATUS.install;
  }

  function statusIcon(st, size) {
    return st.spin ? '<span class="lc-spin"></span>' : icon(st.glyph, size, 2.25);
  }

  function progressBar(st, cls) {
    return st.busy ? `<span class="${cls}${st.percent === null ? ' going' : ''}"><span style="width:${st.percent === null ? 100 : st.percent}%"></span></span>` : '';
  }

  // The library by category (the catalog's, in its order), each under its heading: its apps, then
  // its websites (their own shorter cards: nothing to install, each opens as its own app). A card
  // with no category the catalog knows goes last, under Other.
  function libraryGroups() {
    const cats = lib.catalog.categories || [];
    const known = new Set(cats.map((c) => c.id));
    const of = (c) => (known.has(c.category) ? c.category : '');
    return [...cats, { id: '', name: 'Other' }].map((g) => ({ ...g,
      apps: lib.catalog.apps.filter((c) => of(c) === g.id), sites: lib.catalog.sites.filter((c) => of(c) === g.id) }))
      .filter((g) => g.apps.length || g.sites.length);
  }

  function libraryTabHtml() {
    if (!lib.catalog.apps.length && !lib.catalog.sites.length) return '<p class="at-empty">Loading the library…</p>';
    const app = (c) => {
      const st = cardStatus(c);
      return `<button class="lc-app${st.busy ? ' busy' : ''}${st.failed ? ' failed' : ''}" data-nav data-id="app-${esc(c.id)}" data-act="libcard" data-arg="${esc(c.id)}">` +
        `<div class="lc-top">${appIcon(c, 40)}<span class="lc-name">${esc(c.name)}</span>${c.builtin ? '<span class="lc-tag">Built in</span>' : ''}</div>` +
        `<span class="lc-status" style="color:${st.color}">${statusIcon(st, 22)}${esc(st.label)}</span>${progressBar(st, 'lc-bar')}</button>`;
    };
    const site = (c) => {
      const st = cardStatus(c);
      return `<button class="lc-site" data-nav data-id="site-${esc(c.id)}" data-act="sitecard" data-arg="${esc(c.id)}">` +
        appIcon({ ...c, glyph: 'globe' }, 34) +   // without its logo, a site shows as a website
        `<span class="lc-sname"><span class="lc-name">${esc(c.name)}</span></span>` +
        `<span class="lc-sglyph" style="color:${st.color}">${icon(st.glyph, 24, 2.25)}</span></button>`;
    };
    return libraryGroups().map((g) => `<section class="lc-group" data-cat="${esc(g.id)}"><span class="at-label">${esc(g.name)}</span>` +
      (g.apps.length ? `<div class="lc-grid">${g.apps.map(app).join('')}</div>` : '') +
      (g.sites.length ? `<div class="lc-grid sites">${g.sites.map(site).join('')}</div>` : '') + '</section>').join('');
  }

  // LT / RT in the library: the first card of the category before or after the focused one's (at
  // either end, the first card of that end's category).
  function jumpCategory(step, node) {
    const groups = [...el('addtile').querySelectorAll('.lc-group')];
    const at = node ? groups.findIndex((g) => g.contains(node)) : -1;
    const to = groups[Math.max(0, Math.min(groups.length - 1, at + step))];
    const first = to && to.querySelector('[data-nav]');
    if (first && first !== node) setFocus(first);
  }

  // A card in its category's first row: the category's heading shows above it too.
  function headingInView(node, main) {
    const group = node.closest('.lc-group'), grid = node.parentElement;
    if (!group || grid !== group.querySelector('.lc-grid') || node.offsetTop - grid.firstElementChild.offsetTop > 1) return;
    const head = group.querySelector('.at-label').getBoundingClientRect(), box = main.getBoundingClientRect();
    const scale = box.height / main.offsetHeight || 1;
    if (head.top < box.top + 8 * scale) main.scrollTop -= (box.top - head.top) / scale + 8;
  }

  // Each program with its own icon (its Start menu shortcut's, made and kept by the host in the
  // background: they fill in as they come), else the glyph.
  function onboxTabHtml() {
    if (!lib.programs.length) return `<p class="at-empty">${hostWaitText('programs', 'Reading what’s installed…', 'The list of programs didn’t come. Switch tabs (LB, RB) to try once more.', 15000)}</p>`;
    const rows = lib.programs.map((p) => {
      const glyph = appIcon({ glyph: 'app', logo: p.logo }, 40);
      if (!p.launchable)
        return `<button class="ob-row muted" data-nav data-id="ob-${esc(p.name)}" data-act="prognote" data-note="${esc(p.note || '')}">` +
          `${glyph}<span class="grow">${esc(p.name)}</span><span class="ob-tag">${esc(p.note || '')}</span></button>`;
      return `<button class="ob-row${p.onHome ? ' on-home' : ''}" data-nav data-id="ob-${esc(p.name)}" data-act="addprog" data-arg="${esc(p.name)}">` +
        `${glyph}<span class="grow">${esc(p.name)}</span>${p.onHome ? '<span class="ob-tag">On home screen</span>' : ''}</button>`;
    }).join('');
    return '<p class="at-note">Everything installed on this box, A to Z</p>' + `<div class="ob-grid">${rows}</div>`;
  }

  // The address first (the name is optional), then Add tile; the preview beside them. No line on
  // how to bring the keyboard up: by then the user knows A on a field does (the owner, 29 Sept
  // 2026), and the hints say Type. Everything sits in the top half: the keyboard, up, never
  // covers the field being typed into.
  function websiteTabHtml() {
    const w = lib.website;
    const field = (id, label, value, max, placeholder, aria) =>
      `<div class="lib-field" data-nav data-id="wf-${id}" data-act="field"><span class="lib-label">${label}</span>` +
        `${fieldHtml(`wf-${id}-input`, id, value, max, placeholder, aria)}</div>`;
    return '<div class="ws-form">' +
      '<div class="ws-fields">' +
        field('url', 'Address', w.url, 2048, 'example.com', 'Website address') +
        field('name', 'Name', w.name, 24, 'Optional: its address if left empty', 'Tile name') +
        `<div class="lib-buttons"><div class="lib-btn primary" data-nav data-id="wf-add" data-act="addsite">${lib.adding ? 'Adding…' : 'Add tile'}</div></div>` +
      '</div>' +
      `<div class="ws-preview">${previewHtml()}</div>` +
    '</div>';
  }

  // Just added (the form cleared for the next one): the card shows it, on the home screen.
  function previewHtml() {
    const w = lib.website, added = w.added && !w.name && !w.url;
    return `<span class="ws-plabel">${added ? 'Added' : 'Preview'}</span>` +
      `<div class="ws-card"><span style="display:flex;color:#8CC2FF">${icon('globe', 64)}</span>` +
      `<span class="ws-cname">${esc(added ? w.added : w.name.trim() || previewName(w.url))}</span>` +
      (added ? `<span class="ws-added">${icon(STATUS.home.glyph, 22, 2.25)}${STATUS.home.label}</span>` : '') + '</div>';
  }

  function previewName(url) {
    if (!url.trim()) return 'Website';
    return url.trim().replace(/^https?:\/\//i, '').replace(/^www\./i, '').split('/')[0] || 'Website';
  }

  // Adding shows on its button ("Adding…") until the host answers; 15 s without an answer says so.
  // No address yet: the address field, with the keyboard.
  function saveWebsite() {
    if (lib.adding) return;
    if (!lib.website.url.trim()) { toast('Type the website’s address first', 'warn'); typeInto(el('addtile').querySelector('[data-id="wf-url"]')); return; }
    lib.adding = setTimeout(() => { lib.adding = null; toast('No answer about the website. Try once more.', 'warn'); if (state.view === 'addtile') render(); }, 15000);
    send({ type: 'library.addWebsite', name: lib.website.name.trim(), url: lib.website.url.trim() });
    render();
  }
  onAction('addsite', saveWebsite);

  // ---- Text fields (the website's, the tile's new name) ------------------------------------------
  // As the Wi-Fi password's (wifi.js): real <input>s the on-screen keyboard and the phone type
  // into (textinput.js). What is typed is kept in lib as it comes, so a field drawn again (patchHtml
  // makes text fields afresh: the clock, a host push) shows it, with the focus and the caret back
  // where they were.

  function fieldHtml(id, field, value, max, placeholder, aria) {
    return `<input id="${id}" data-field="${field}" type="text" value="${esc(value)}" maxlength="${max}" placeholder="${esc(placeholder)}" ` +
      `aria-label="${esc(aria)}" autocomplete="off" spellcheck="false">`;
  }

  function drawFields(root, draw) {
    const was = document.activeElement;
    const typing = was && was.dataset && was.dataset.field && root.contains(was) ? { id: was.id, from: was.selectionStart, to: was.selectionEnd } : null;
    draw();
    for (const input of root.querySelectorAll('input[data-field]')) {
      if (input.dataset.wired) continue;
      input.dataset.wired = '1';
      input.addEventListener('input', () => typed(input));
      input.addEventListener('textsubmit', () => submitted(input));
    }
    const now = typing && document.getElementById(typing.id);
    if (now && document.activeElement !== now) { now.focus(); now.setSelectionRange(typing.from, typing.to); }
  }

  // Only what shows what is typed changes: the preview, the count. (setRangeText, which the
  // keyboard's text goes through, does not keep to maxlength itself.)
  function typed(input) {
    const max = Number(input.getAttribute('maxlength')) || 2048;
    if (input.value.length > max) input.value = input.value.slice(0, max);
    const f = input.dataset.field;
    if (f === 'draft') {
      lib.draft = input.value;
      const count = el('rename').querySelector('.rn-count');
      if (count) count.textContent = `${lib.draft.length}/24`;
      return;
    }
    lib.website[f] = input.value;
    lib.website.added = null;
    const preview = el('addtile').querySelector('.ws-preview');
    if (preview) patchHtml(preview, previewHtml());
  }

  // Enter, from the on-screen keyboard or a real one: the name saved, the website added. The
  // website's name with no address yet: on to the address, the keyboard up again for it.
  function submitted(input) {
    if (input.dataset.field === 'draft') { saveRename(); return; }
    if (input.dataset.field === 'name' && !lib.website.url.trim()) { typeInto(el('addtile').querySelector('[data-id="wf-url"]')); return; }
    saveWebsite();
  }

  // A (or R3) on a field's row: the focus on it, the on-screen keyboard for its field, the caret
  // after what is there.
  function typeInto(row) {
    const input = row && row.querySelector('input[data-field]');
    if (!input) return;
    if (focusedEl() !== row) setFocus(row);
    openKeyboardFor(input);
    input.setSelectionRange(input.value.length, input.value.length);
  }
  onAction('field', (node) => typeInto(node));

  // The focus moved off a field's row: the field lets go too (the phone's text, R3, a real
  // keyboard's keys and the interface sounds are the launcher's again).
  function letGoOfFields(node) {
    const f = textField();
    if (f && f.dataset.field && !node.contains(f)) f.blur();
  }

  // The fields' screen or tab goes: the keyboard with them, if it was up for one.
  function closeFields() {
    const f = textField();
    if (!f || !f.dataset.field) return;
    f.blur();
    textDone();
  }
  // ... also when it goes some other way (Home over the rename, an alert's action, the host
  // showing another view): checked with each render.
  onHome(() => { if (state.view !== 'rename' && !(state.view === 'addtile' && lib.tab === 'website')) closeFields(); });

  // ---- Library card actions ----------------------------------------------------------------

  function findCard(id) { return lib.catalog.apps.find((c) => c.id === id) || lib.catalog.sites.find((c) => c.id === id); }

  onAction('libcard', (node, id) => cardAction(id));
  onAction('sitecard', (node, id) => cardAction(id));
  onAction('addprog', (node, name) => {
    const p = lib.programs.find((x) => x.name === name);
    if (p && p.onHome) { toast(`${name} is already on your home screen`); return; }
    lib.addingProgram = name;
    send({ type: 'library.addProgram', name });
  });
  onAction('prognote', (node) => toast(node && node.dataset.note ? node.dataset.note : 'That can’t be added'));

  // A on a card asks one yes-or-no question for what it would do, and does it on Yes: install or
  // uninstall an app, add or remove a site's (or the Browser's) tile. The card changes where it
  // is and the focus stays on it; the host's catalog, asked for again, confirms it.
  function cardAction(id) {
    const card = findCard(id);
    if (!card) return;
    if (card.state === 'installing') { if (cardStatus(card).wizard) send({ type: 'library.showInstaller' }); else toast(`${card.name} is installing…`); return; }
    if (card.state === 'uninstalling') { toast(`${card.name} is being uninstalled…`); return; }
    const kind = cardKind(card);
    if ((kind === 'install' || kind === 'uninstall') && !state.libraryAvailable) {
      toast('Installing from the TV isn’t set up yet. Run setup once more.', 'warn');
      return;
    }
    if (kind === 'install') {
      ask({ title: `${lib.failed.has(id) ? 'Try again to install' : 'Install'} ${card.name}?`, text: 'Its tile goes on Home and shows the progress.', yes: 'Install',
        onYes: () => { installApp(id); markCard(id, 'installing'); } });
    } else if (kind === 'uninstall') {
      ask({ title: `Uninstall ${card.name}?`, text: UNINSTALL_TEXT, yes: 'Uninstall',
        onYes: () => { uninstallApp(card.id, card.name); markCard(card.id, 'uninstalling'); } });
    } else if (kind === 'add') {
      ask({ title: `Add ${card.name} to Home?`, text: 'Its tile goes on the home screen.', yes: 'Add',
        onYes: () => { addToHome(id); toast(`Added ${card.name}`); markCard(id, 'home'); } });
    } else if (kind === 'remove') {
      ask({ title: `Remove ${card.name} from Home?`, text: 'You can add it again here.', yes: 'Remove',
        onYes: () => { send({ type: 'tile.remove', id: card.id }); toast(`${card.name} removed from Home`); markCard(card.id, 'add'); } });
    }
  }

  // What A on a card offers: an app the box installs is installed or not (installed means on
  // Home); a site or the Browser is only on Home or not.
  function cardKind(card) {
    if (card.state === 'install') return 'install';
    if (card.state === 'add') return 'add';
    if (card.state === 'home') return card.canUninstall ? 'uninstall' : 'remove';
    return '';
  }

  // The card shows the change at once; the host's catalog, asked for again, confirms it.
  function markCard(id, now) {
    const mark = (c) => (c.id === id ? { ...c, state: now } : c);
    lib.catalog = { ...lib.catalog, apps: lib.catalog.apps.map(mark), sites: lib.catalog.sites.map(mark) };
    lib.catalogKey = null;                 // the host's next catalog is drawn, even the same as before
    send({ type: 'library.list' });
    render();
  }

  function addToHome(id) {
    const ids = state.tiles.map((t) => t.id);
    if (!ids.includes(id)) ids.push(id);
    send({ type: 'tile.order', ids });
  }

  function uninstallApp(id, name) {
    send({ type: 'library.uninstall', id });
    toast(`Uninstalling ${name}…`);
  }

  // ---- Installing ---------------------------------------------------------------------------
  // Asked from Add tile, an app is always for the home screen: its tile shows it installing.

  function installApp(id) {
    lib.failed.delete(id);
    lib.homeBound.add(id);
    send({ type: 'library.install', id, addToHome: true });
  }

  // ---- Apps being installed, on the home screen ----------------------------------------------
  // Until the app's own tile arrives, a tile says it is installing (dimmed, a dashed edge, the
  // progress); if it did not install, it says so (A tries again, X takes it away).

  onTiles(() => {
    for (const id of lib.homeBound) if (state.tiles.some((t) => t.id === id)) lib.homeBound.delete(id);
    return [...lib.homeBound].filter((id) => lib.queued.has(id) || lib.finished.has(id) || lib.failed.has(id)).map((id) => {
      const c = findCard(id) || { id, name: id, glyph: 'app', color: '#8CC2FF' };
      const failed = lib.failed.has(id) && !lib.queued.has(id);
      // Just out of the queue: installing still, until the catalog says how it went.
      const st = failed ? { label: 'Didn’t install', glyph: 'warn' }
        : lib.queued.has(id) ? cardStatus({ ...c, state: 'installing' }) : { label: 'Installing…', spin: true, busy: true, percent: null };
      return {
        id: `tile:~${id}`, cls: `tile pending${failed ? ' failed' : ''}`, act: failed ? 'pending-retry' : 'pending-info', arg: id,
        x: failed ? 'pending-remove' : null, hints: failed ? [['A', 'Try again'], ['X', 'Remove']] : st.wizard ? [['A', 'Show the installer']] : [],
        html: `<span class="pbadge">${statusIcon(st, 22)}${esc(st.label)}</span>` +
          `<span class="pglyph">${appIcon(c, 88)}</span><span class="name">${esc(c.name)}</span>` +
          progressBar(st, 'pbar'),
      };
    });
  });
  onAction('pending-info', (node, id) => {
    const p = lib.progress.current;
    if (p && p.id === id && p.phase === 'wizard') { send({ type: 'library.showInstaller' }); return; }   // its installer, up again
    const c = findCard(id); toast(`${c ? c.name : 'The app'} is installing. Its tile opens once it’s ready.`);
  });
  onAction('pending-retry', (node, id) => { installApp(id); render(); });
  onAction('pending-remove', (node, id) => { lib.failed.delete(id); lib.homeBound.delete(id); render(); });

  // ---- Host messages -----------------------------------------------------------------------

  hostMessage('library.', (msg) => {
    switch (msg.type) {
      case 'library.available': state.libraryAvailable = !!msg.available; break;
      case 'library.catalog': {
        // The same catalog again (the host sends it on each look, each logo, each add): nothing to draw.
        const key = JSON.stringify([msg.apps, msg.sites, msg.categories]), failed = [...lib.failed].join();
        const same = key === lib.catalogKey;
        lib.catalogKey = key;
        lib.catalog = { apps: msg.apps || [], sites: msg.sites || [], categories: msg.categories || [] };
        // Out of the queue and still to install: it did not.
        for (const id of lib.finished) {
          const c = findCard(id);
          if (c && c.state === 'install') lib.failed.add(id); else lib.failed.delete(id);
        }
        lib.finished.clear();
        if (same && failed === [...lib.failed].join()) break;
        if (state.view === 'addtile' && lib.tab === 'library') { render(); focusBodyIfNeeded(); }
        else if (state.view === 'home') render();
        break;
      }
      case 'library.programs':
        hostAnswered('programs');
        lib.programs = msg.list || [];
        if (state.view === 'addtile' && lib.tab === 'onbox') { render(); focusBodyIfNeeded(); }
        break;
      case 'library.progress': {
        lib.progress = { current: msg.current || null, pending: msg.pending || [] };
        const cur = lib.progress.current;
        const now = new Set([...(cur && cur.action === 'install' ? [cur.id] : []),
          ...lib.progress.pending.filter((j) => j.action === 'install').map((j) => j.id)]);
        for (const id of lib.queued) if (!now.has(id)) lib.finished.add(id);
        lib.queued = now;
        // The progress shows on the cards and the tiles being installed.
        if ((state.view === 'addtile' && lib.tab === 'library') || (state.view === 'home' && lib.homeBound.size)) render();
        break;
      }
      // Added: Add tile stays up. The website form is cleared for the next one, its card showing
      // the one just added; a program's row says it is on the home screen.
      case 'library.websiteResult':
        clearTimeout(lib.adding);
        lib.adding = null;
        if (msg.ok) { toast(`Added ${msg.name}`); lib.website = { name: '', url: '', added: msg.name }; }
        else toast(msg.error || 'That address did not work', 'warn');
        if (state.view === 'addtile') render();
        break;
      case 'library.programAdded': {
        toast(`Added ${msg.name}`);
        const p = lib.programs.find((x) => x.name === lib.addingProgram);
        if (p) p.onHome = true;
        lib.addingProgram = null;
        if (state.view === 'addtile' && lib.tab === 'onbox') render();
        break;
      }
    }
  });

  function focusBodyIfNeeded() {
    const focused = focusedEl();
    const main = el('addtile').querySelector('.at-main');
    if (focused && main && main.contains(focused)) return;
    const first = main && main.querySelector('[data-nav]');
    if (first) setFocus(first);
  }

  // ---- Demo data (index.html#addtile, #addtile/website, #tileopts...) -----------------------

  // The real catalog (setup\catalog.json, its 40 apps and sites in their categories) and a box's
  // worth of programs (about 100, some with long names): the screens are looked at as full as a
  // box has them.
  const DEMO_CATEGORIES = [['movies', 'Movies & shows'], ['canada', 'Canadian TV'], ['sports', 'Sports'], ['music', 'Music'],
    ['games', 'Games'], ['media', 'Your media & tools']].map(([id, name]) => ({ id, name }));
  const DEMO_CATALOG = [
    ['youtube', 'YouTube', 'youtube', '#FF5B52', 'app', 'movies', 'home'], ['twitch', 'Twitch', 'chat', '#B08CFF', 'website', 'movies', 'home'],
    ['stremio', 'Stremio', 'film', '#7C8CFF', 'app', 'movies', 'home'], ['jellyfin', 'Jellyfin', 'library', '#3DC0F0', 'app', 'media', 'home'],
    ['moonlight', 'Moonlight', 'moon', '#F5D16B', 'app', 'games', 'uninstalling'], ['edge', 'Browser', 'globe', '#3CCB9A', 'app', 'media', 'home'],
    ['kodi', 'Kodi', 'play', '#5AB0FF', 'app', 'media', 'install'], ['vlc', 'VLC', 'play', '#FF8A1F', 'app', 'media', 'home'],
    ['plex', 'Plex HTPC', 'play', '#F5B82E', 'app', 'media', 'install'], ['spotify', 'Spotify', 'music', '#1ED760', 'app', 'music', 'installing'],
    ['feishin', 'Feishin', 'music', '#FF7AB6', 'app', 'music', 'install'], 
    ['playnite', 'Playnite', 'controller', '#B08CFF', 'app', 'games', 'install'], ['retroarch', 'RetroArch', 'controller', '#5AB0FF', 'app', 'games', 'home'],
    ['netflix', 'Netflix', 'play', '#FF4B55', 'website', 'movies', 'add'],
    ['disneyplus', 'Disney+', 'play', '#4D8DFF', 'website', 'movies', 'add'], ['primevideo', 'Prime Video', 'play', '#2BB0F5', 'website', 'movies', 'home'],
    ['crunchyroll', 'Crunchyroll', 'play', '#FF8A2B', 'website', 'movies', 'add'], ['hbomax', 'HBO Max', 'play', '#8A7BFF', 'website', 'movies', 'add'],
    ['appletv', 'Apple TV+', 'play', '#D9D8D4', 'website', 'movies', 'add'], ['paramountplus', 'Paramount+', 'play', '#3D8BFF', 'website', 'movies', 'add'],
    ['tubi', 'Tubi', 'play', '#FFD43B', 'website', 'movies', 'add'], ['plutotv', 'Pluto TV', 'tv', '#F5E642', 'website', 'movies', 'add'],
    ['crave', 'Crave', 'play', '#3FA9F5', 'website', 'movies', 'add'], ['cbcgem', 'CBC Gem', 'play', '#FF4B4B', 'website', 'canada', 'add'],
    ['toutv', 'ICI TOU.TV', 'play', '#F25F5C', 'website', 'canada', 'home'], ['telequebec', 'Télé-Québec', 'play', '#3DC0F0', 'website', 'canada', 'add'],
    ['tvaplus', 'TVA+', 'tv', '#6FA8FF', 'website', 'canada', 'add'], ['illicoplus', 'illico+', 'play', '#E6D65A', 'website', 'canada', 'add'],
    ['onf', 'ONF', 'film', '#FF7A6B', 'website', 'canada', 'add'], ['rds', 'RDS', 'tv', '#FF5A4E', 'website', 'sports', 'add'],
    ['tsn', 'TSN', 'tv', '#E8485C', 'website', 'sports', 'add'], ['sportsnet', 'Sportsnet+', 'tv', '#2F9BFF', 'website', 'sports', 'add'],
    ['ohdio', 'OHdio', 'speaker', '#B08CFF', 'website', 'music', 'add'], ['youtubemusic', 'YouTube Music', 'music', '#FF4E45', 'website', 'music', 'add'],
    ['applemusic', 'Apple Music', 'music', '#FF5A73', 'website', 'music', 'add'], ['geforcenow', 'GeForce NOW', 'controller', '#8CD13C', 'website', 'games', 'add'],
    ['xboxcloud', 'Xbox Cloud Gaming', 'controller', '#52C443', 'website', 'games', 'add'], ['luna', 'Amazon Luna', 'controller', '#A07CFF', 'website', 'games', 'add'],
  ].map(([id, name, glyph, color, type, category, state]) => demoLogo({ id, name, glyph, color, type, category, state, canUninstall: type === 'app' && id !== 'edge', builtin: id === 'edge' }));
  const DEMO_PROGRAMS = ['7-Zip File Manager', 'Adobe Acrobat', 'Amazon Luna', 'Audacity', 'Back to TV', 'Calculator', 'Character Map', 'Command Prompt',
    'Component Services', 'Computer Management', 'Control Panel', 'dfrgui', 'Discord', 'Disk Cleanup', 'Dolby Access', 'EA app', 'Epic Games Launcher',
    'Event Viewer', 'File Explorer', 'GIMP 2.10.38', 'GOG GALAXY', 'HandBrake', 'Hyper-V Manager', 'Intel® Graphics Command Center (Arc Control, beta)',
    'iSCSI Initiator', 'Jellyfin Media Player', 'Kodi', 'LibreOffice Calc', 'LibreOffice Impress', 'LibreOffice Writer', 'LiveCaptions', 'Magnify',
    'Memory Diagnostics Tool', 'Microsoft Edge', 'Microsoft Visual C++ 2015-2022 Redistributable Diagnostics (x64)', 'Moonlight', 'MPC-HC x64',
    'Narrator', 'Notepad', 'Notepad++', 'NVIDIA Control Panel', 'OBS Studio', 'ODBC Data Sources (32-bit)', 'ODBC Data Sources (64-bit)',
    'On-Screen Keyboard', 'Paint', 'Performance Monitor', 'Playnite', 'Plex HTPC', 'Print Management', 'qBittorrent', 'RecoveryDrive', 'Registry Editor',
    'Remote Desktop Connection', 'Resource Monitor', 'RetroArch', 'RetroArch Website', 'Run', 'Safe Mode', 'Security Configuration Management',
    'services', 'Snipping Tool', 'Sonarr', 'Steam', 'Steps Recorder', 'Stremio', 'System Configuration', 'System Information', 'Task Manager',
    'Task Scheduler', 'Ubisoft Connect', 'Uninstall', 'Uninstall Kodi', 'Uninstall RetroArch', 'VacuumTube', 'VLC media player',
    'VLC media player - reset preferences and cache files', 'VLC media player skinned', 'VMCreate', 'VoiceAccess', 'Windows Defender Firewall with Advanced Security',
    'Windows Media Player Legacy', 'Windows PowerShell', 'Windows PowerShell (x86)', 'Windows PowerShell ISE', 'Windows PowerShell ISE (x86)', 'WinRAR',
    'Wireshark', 'WordPad', 'Xbox', 'Xbox Game Bar', 'XnView MP', 'Zoom Workplace', 'Zune Music (an old program with a name long enough to run out of room)',
    'Adobe Photoshop Elements 2024 Organizer', 'Battle.net', 'Blender 4.2', 'Brave', 'Firefox', 'Google Chrome', 'Spotify']
    .sort((a, b) => a.localeCompare(b)).map((name) => ({ name, launchable: !/Website|Safe Mode|^Run$|Control Panel/.test(name),
      onHome: /^(Jellyfin Media Player|Microsoft Edge|Kodi|Moonlight)$/.test(name),
      note: /Website|Safe Mode/.test(name) ? 'Not a program' : /^Run$|Control Panel/.test(name) ? 'No program file' : null }));

  function demoData() {
    if (lib.catalog.apps.length) return;
    lib.catalog = { categories: DEMO_CATEGORIES, apps: DEMO_CATALOG.filter((a) => a.type === 'app'), sites: DEMO_CATALOG.filter((a) => a.type === 'website') };
    lib.progress = { current: { id: 'spotify', name: 'Spotify', action: 'install', phase: 'download', percent: 62 }, pending: [] };
    lib.queued = new Set(['spotify']);
    lib.failed = new Set(['feishin']);
    lib.programs = DEMO_PROGRAMS;
    // #addtile/website?long=1: a long name and address typed in.
    if (/[?&]long=1/.test(location.hash)) lib.website = { name: 'My NAS media server p', url: 'https://www.a-very-long-website-address-for-a-streaming-service.example.com/watch/home?profile=living-room', added: null };
  }

  // Demo: index.html#home?installing=1, tiles for an app installing and one that did not.
  if (!(window.chrome && window.chrome.webview) && /[?&]installing=1/.test(location.hash)) {
    demoData();
    lib.homeBound = new Set(['spotify', 'feishin']);
  }
})();
