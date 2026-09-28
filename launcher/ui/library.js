'use strict';

// App library and tile editing (SPEC W1, W5). A feature script loaded after app.js: it registers
// its screens and actions with the launcher's UI registry (addView / onAction / hostMessage / ask)
// and drives them through the shared helpers (state, send, go, back, reset, render, toast...).
//
// Screens: addtile (Library / On this box / Website tabs), tileopts (Start on a home tile),
// rename, changeicon, installing. Home-screen bits (the "+" tile, Start = Tile options, moving a
// tile) are wired through onAction hooks app.js calls.

(function () {
  const COLS = 4;

  const STATUS = {
    home: { label: 'On home screen', glyph: 'home', color: '#8E9199' },
    installed: { label: 'Installed', glyph: 'check', color: '#7FD1AE' },
    install: { label: 'Install', glyph: 'download', color: '#8CC2FF' },
    add: { label: 'Add tile', glyph: 'plus', color: '#8CC2FF' },
  };

  // Icon-picker choices (kept in step with TileStore.IconChoices / ColorChoices host-side).
  const ICONS = ['play', 'film', 'library', 'music', 'tv', 'globe', 'chat', 'youtube', 'moon',
    'controller', 'app', 'speaker', 'search', 'download', 'home', 'sun', 'power', 'info'];
  const COLORS = ['#FF5B52', '#FF8A1F', '#F5B82E', '#1ED760', '#3DC0F0', '#3CCB9A', '#7C8CFF',
    '#B08CFF', '#FF7AB6', '#F5D16B', '#5AB0FF', '#F3F2EF'];

  const KB_ROWS = ['1234567890'.split(''), 'qwertyuiop'.split(''), 'asdfghjkl-'.split(''), 'zxcvbnm./'.split('')];

  const lib = {
    tab: 'library',
    catalog: { apps: [], sites: [] },
    programs: [],
    progress: { current: null, pending: [] },
    target: null,          // tile id being edited (tileopts / rename / changeicon)
    install: null,         // card for the Install dialog
    installing: false,     // the dialog has started a job
    website: { name: '', url: '', field: 'url' },
    moveOrigin: null,      // tile order to restore if a move is cancelled
    draft: '',             // the name being typed in the rename view
    // Installs: the ids asked with "Install and add to home" (a tile shows them installing),
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

  function moveButton(button) {
    switch (button) {
      case 'up': case 'down': case 'left': case 'right': moveTile(button); return true;
      case 'a': case 'start': dropTile(); return true;
      case 'b': cancelMove(); return true;
      case 'home': case 'homeHold': cancelMove(); return false;   // then app opens the menu / power
      default: return true;                                       // swallow everything else while moving
    }
  }

  function startMove() {
    lib.moveOrigin = state.tiles.map((t) => t.id);
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
    render();
    toast('Moved');
  }
  function cancelMove() {
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
      el('tileopts').innerHTML =
        '<aside class="to-panel">' +
          `<div class="to-head">${appIcon(t, 40)}<span class="to-name">${esc(t.name)}</span></div>` +
          rows.map(([act, glyph, label]) => `<button class="to-item" data-nav data-id="${act}" data-act="${act}">${icon(glyph, 32, 2)}${label}</button>`).join('') +
          '<div class="to-sep"></div>' +
          `<button class="to-item danger" data-nav data-id="opt-remove" data-act="opt-remove">${icon('trash', 32, 2)}Remove from home</button>` +
        '</aside>' +
        `<footer class="hints">${hints([['A', 'Select'], ['B', 'Close']])}</footer>`;
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
  onAction('opt-rename', () => { const t = targetTile(); lib.draft = t ? t.name : ''; go('rename'); });
  onAction('opt-icon', () => go('changeicon'));
  onAction('opt-remove', removeTarget);

  function removeTarget() {
    const t = targetTile();
    if (!t) return;
    // A custom tile (added website or program) keeps its details only here, so ask first (SPEC
    // decision); a catalog app can always be re-added from the library, so it goes at once.
    if (t.custom) {
      ask({
        title: `Remove ${t.name}?`,
        text: 'Its address and settings are only here, so they will be lost.',
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

  addView('rename', {
    demo() { lib.target = state.tiles[0] && state.tiles[0].id; lib.draft = (state.tiles[0] && state.tiles[0].name) || ''; },
    render() {
      const t = targetTile();
      if (!t) { back(); return; }
      el('rename').innerHTML =
        '<main class="lib-center"><div class="rn-wrap">' +
          '<div class="rn-field">' +
            '<span class="rn-label">Name</span>' +
            `<div class="rn-input">${esc(lib.draft) || '<span class="rn-ph">Type a name</span>'}<span class="rn-caret"></span></div>` +
            `<span class="rn-count">${lib.draft.length}/24</span>` +
          '</div>' +
          `<div class="kbi">${keyboardHtml('rename')}</div>` +
        '</div></main>' +
        `<footer class="hints">${hints([['A', 'Type'], ['X', 'Delete'], ['Y', 'Space'], ['Start', 'Save'], ['B', 'Cancel']])}</footer>`;
    },
    press(button) {
      if (button === 'x') { lib.draft = lib.draft.slice(0, -1); render(); return true; }
      if (button === 'y') { typeRename(' '); return true; }
      if (button === 'start') { saveRename(); return true; }
      return false;
    },
  });

  function typeRename(ch) { if (lib.draft.length < 24) { lib.draft += ch; render(); } }
  function saveRename() { send({ type: 'tile.rename', id: lib.target, name: lib.draft.trim() }); toast('Renamed'); back(); }
  onAction('renameKey', (node, ch) => typeRename(ch));

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
      el('changeicon').innerHTML =
        '<main class="lib-center"><div class="ci-wrap">' +
          `<div class="ci-preview">${appIcon(t, 80)}<span class="ci-name">${esc(t.name)}</span></div>` + logoChoice +
          '<div class="ci-glyphs">' + ICONS.map((g) =>
            `<button class="ci-glyph${!onLogo && g === t.glyph ? ' on' : ''}" data-nav data-id="g-${g}" data-act="glyph" data-arg="${g}">${icon(g, 40)}</button>`).join('') + '</div>' +
          '<div class="ci-colors">' + COLORS.map((c) =>
            `<button class="ci-color${!onLogo && c.toUpperCase() === String(t.color).toUpperCase() ? ' on' : ''}" data-nav data-id="c-${c}" data-act="color" data-arg="${c}"><span style="background:${c}"></span></button>`).join('') + '</div>' +
        '</div></main>' +
        `<footer class="hints">${hints([['A', 'Choose'], ['B', 'Done']])}</footer>`;
    },
  });

  onAction('glyph', (node, glyph) => { send({ type: 'tile.icon', id: lib.target, glyph }); render(); });
  onAction('color', (node, color) => { send({ type: 'tile.icon', id: lib.target, color }); render(); });

  // ---- Add tile: Library / On this box / Website -------------------------------------------

  const TABS = ['library', 'onbox', 'website'];

  addView('addtile', {
    demo(arg) { demoData(); lib.tab = TABS.includes(arg) ? arg : 'library'; },
    render() {
      const tabs = [['library', 'Library'], ['onbox', 'On this box'], ['website', 'Website']].map(([id, label]) =>
        `<div class="at-tab${id === lib.tab ? ' on' : ''}">${label}<span class="at-underline"></span></div>`).join('');
      const body = lib.tab === 'library' ? libraryTabHtml() : lib.tab === 'onbox' ? onboxTabHtml() : websiteTabHtml();
      const hintList = lib.tab === 'website'
        ? [['A', 'Type'], ['X', 'Delete'], ['Y', 'Space'], ['Start', 'Add tile'], ['LB', 'Tabs'], ['B', 'Back']]
        : lib.tab === 'library'
          ? [['A', 'Install or add'], ['X', 'Uninstall'], ['LB', 'Prev tab'], ['RB', 'Next tab'], ['B', 'Back']]
          : [['A', 'Add to home'], ['LB', 'Prev tab'], ['RB', 'Next tab'], ['B', 'Back']];
      // The same tab again (install progress, a logo, the catalog after an add): its cards are
      // updated in place (patchHtml), their logos not loaded again, the list's scroll and the
      // focus left be. Another tab is drawn afresh.
      const old = el('addtile').querySelector('.at-main');
      if (old && el('addtile').dataset.tab === lib.tab) { patchHtml(old, body); return; }
      el('addtile').innerHTML =
        '<header class="at-header"><h1>Add a tile</h1>' + `<nav class="at-tabs" aria-label="Tile source">${tabs}</nav></header>` +
        `<main class="at-main">${body}</main>` +
        `<footer class="hints">${hints(hintList)}</footer>`;
      el('addtile').dataset.tab = lib.tab;
    },
    // The tab's list scrolls to the focus, with room for its ring, above the hints.
    focused(node) {
      const main = node.closest('.at-main');
      if (main) { scrollIntoBox(node, main, 40); listEdges(main); }
    },
    press(button, node) {
      if (button === 'lb') { switchTab(-1); return true; }
      if (button === 'rb') { switchTab(1); return true; }
      if (lib.tab === 'website') {
        if (button === 'x') { typeKey('del'); return true; }
        if (button === 'y') { typeKey('space'); return true; }
        if (button === 'start') { saveWebsite(); return true; }
      }
      if (lib.tab === 'library' && button === 'x') { uninstallFocused(node); return true; }
      return false;
    },
  });

  function openAddTile() {
    lib.tab = 'library';
    lib.website = { name: '', url: '', field: 'url' };
    go('addtile');
    send({ type: 'library.list' });
  }
  function switchTab(step) { setTab(TABS[(TABS.indexOf(lib.tab) + step + TABS.length) % TABS.length]); }
  function setTab(tab) {
    if (!TABS.includes(tab) || tab === lib.tab) return;
    lib.tab = tab;
    if (tab === 'library') send({ type: 'library.list' });
    if (tab === 'onbox') send({ type: 'library.startMenu' });
    render();
    const first = el('addtile').querySelector('.at-main [data-nav]');
    if (first) setFocus(first);
  }
  onAction('tab', (node, id) => setTab(id));

  // What a card (and a tile being installed) says. Installing looks nothing like "Install": a
  // spinner, the progress, a bar; one that did not install says so in amber.
  function cardStatus(card) {
    if (card.state === 'installing') {
      const p = lib.progress.current;
      if (p && p.id === card.id) {
        const pct = p.phase === 'download' && p.percent != null ? p.percent : null;
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

  function libraryTabHtml() {
    if (!lib.catalog.apps.length && !lib.catalog.sites.length) return '<p class="at-empty">Loading the library…</p>';
    const apps = lib.catalog.apps.map((c) => {
      const st = cardStatus(c);
      return `<button class="lc-app${st.busy ? ' busy' : ''}${st.failed ? ' failed' : ''}" data-nav data-id="app-${esc(c.id)}" data-act="libcard" data-arg="${esc(c.id)}" data-uninstall="${c.canUninstall ? 1 : 0}">` +
        `<div class="lc-top">${appIcon(c, 40)}<span class="lc-name">${esc(c.name)}</span></div>` +
        `<span class="lc-desc">${esc(c.desc)}</span>` +
        `<span class="lc-status" style="color:${st.color}">${statusIcon(st, 22)}${esc(st.label)}</span>${progressBar(st, 'lc-bar')}</button>`;
    }).join('');
    const sites = lib.catalog.sites.map((c) => {
      const st = cardStatus(c);
      return `<button class="lc-site" data-nav data-id="site-${esc(c.id)}" data-act="sitecard" data-arg="${esc(c.id)}">` +
        appIcon({ ...c, glyph: 'globe' }, 34) +   // without its logo, a site shows as a website
        `<span class="lc-name grow">${esc(c.name)}</span>` +
        `<span style="display:flex;color:${st.color}">${icon(st.glyph, 24, 2.25)}</span></button>`;
    }).join('');
    return '<span class="at-label">Apps</span>' + `<div class="lc-grid">${apps}</div>` +
      '<span class="at-label">Streaming sites · each opens as its own app, no install</span>' + `<div class="lc-grid sites">${sites}</div>`;
  }

  function onboxTabHtml() {
    if (!lib.programs.length) return '<p class="at-empty">Reading what’s installed…</p>';
    const rows = lib.programs.map((p) => {
      if (!p.launchable)
        return `<button class="ob-row muted" data-nav data-id="ob-${esc(p.name)}" data-act="prognote" data-note="${esc(p.note || '')}">` +
          `<span style="display:flex">${icon('app', 40)}</span><span class="grow">${esc(p.name)}</span><span class="ob-tag">${esc(p.note || '')}</span></button>`;
      return `<button class="ob-row${p.onHome ? ' on-home' : ''}" data-nav data-id="ob-${esc(p.name)}" data-act="addprog" data-arg="${esc(p.name)}">` +
        `<span style="display:flex">${icon('app', 40)}</span><span class="grow">${esc(p.name)}</span>${p.onHome ? '<span class="ob-tag">On home screen</span>' : ''}</button>`;
    }).join('');
    return '<p class="at-note">Everything installed on this box, A to Z</p>' + `<div class="ob-grid">${rows}</div>`;
  }

  function websiteTabHtml() {
    const w = lib.website;
    const field = (id, label, value, ph) =>
      `<button class="ws-field${w.field === id ? ' on' : ''}" data-nav data-id="wf-${id}" data-act="field" data-arg="${id}">` +
        `<span class="ws-label">${label}</span><span class="ws-value">${esc(value) || `<span class="ws-ph">${ph}</span>`}${w.field === id ? '<span class="rn-caret"></span>' : ''}</span></button>`;
    return '<div class="ws-form">' +
      '<div class="ws-fields">' +
        field('name', 'Name', w.name, 'Optional') +
        field('url', 'Address', w.url, 'example.com') +
        '<div class="ws-hint">Opens as its own app window, like an app — no address bar or tabs.</div>' +
      '</div>' +
      '<div class="ws-preview"><span class="ws-plabel">Preview</span>' +
        `<div class="ws-card"><span style="display:flex;color:#8CC2FF">${icon('globe', 64)}</span>` +
        `<span class="ws-cname">${esc(w.name) || esc(previewName(w.url))}</span></div></div>` +
      '</div>' +
      `<div class="ws-kb">${keyboardHtml('website')}</div>`;
  }

  function previewName(url) {
    if (!url) return 'Website';
    return url.replace(/^https?:\/\//, '').replace(/^www\./, '').split('/')[0] || 'Website';
  }

  // ---- Website form editing ----------------------------------------------------------------

  function activeField() { return lib.website.field === 'name' ? 'name' : 'url'; }
  function typeText(text) {
    const f = activeField();
    lib.website[f] = (lib.website[f] + text).slice(0, f === 'name' ? 24 : 2048);
    render();
  }
  function typeKey(which) {
    const f = activeField();
    if (which === 'del') lib.website[f] = lib.website[f].slice(0, -1);
    else if (which === 'space' && f === 'name') lib.website[f] = (lib.website[f] + ' ').slice(0, 24);
    render();
  }
  function saveWebsite() { send({ type: 'library.addWebsite', name: lib.website.name, url: lib.website.url }); }

  onAction('field', (node, id) => { lib.website.field = id; render(); });
  onAction('key', (node, ch) => typeText(ch));
  onAction('space', () => typeKey('space'));
  onAction('del', () => typeKey('del'));
  onAction('dotcom', () => typeText('.com'));
  onAction('addsite', saveWebsite);

  // ---- Library card actions ----------------------------------------------------------------

  function findCard(id) { return lib.catalog.apps.find((c) => c.id === id) || lib.catalog.sites.find((c) => c.id === id); }

  onAction('libcard', (node, id) => cardAction(id));
  onAction('sitecard', (node, id) => cardAction(id));
  onAction('addprog', (node, name) => send({ type: 'library.addProgram', name }));
  onAction('prognote', (node) => toast(node && node.dataset.note ? node.dataset.note : 'That can’t be added'));

  function cardAction(id) {
    const card = findCard(id);
    if (!card) return;
    if (card.state === 'installing') { toast(`${card.name} is installing…`); return; }
    if (card.state === 'home') { toast(`${card.name} is already on your home screen`); return; }
    if (card.state === 'install') { openInstall(card); return; }
    addToHome(id);                       // an installed app or a website
    toast(`Added ${card.name}`);
    reset('home');
  }

  function addToHome(id) {
    const ids = state.tiles.map((t) => t.id);
    if (!ids.includes(id)) ids.push(id);
    send({ type: 'tile.order', ids });
  }

  function uninstallFocused(node) {
    if (!node || node.dataset.uninstall !== '1') return;
    const card = findCard(node.dataset.arg);
    if (!card || (card.state !== 'installed' && card.state !== 'home')) { toast('That app is not installed'); return; }
    if (!state.libraryAvailable) { toast('Installing from the TV isn’t set up yet', 'warn'); return; }
    ask({
      title: `Uninstall ${card.name}?`,
      text: 'It is removed from the box. Your sign-in and settings are kept.',
      yes: 'Uninstall',
      onYes: () => { send({ type: 'library.uninstall', id: card.id }); toast(`Uninstalling ${card.name}…`); },
    });
  }

  // ---- Install dialog ----------------------------------------------------------------------

  addView('installing', {
    overlay: true,
    // #installing: the choice; #installing/running, /waiting, /done, /failed: after A.
    demo(arg) {
      demoData();
      lib.install = lib.catalog.apps.find((c) => c.state === (arg === 'running' || arg === 'waiting' ? 'installing' : arg === 'done' ? 'installed' : 'install'));
      lib.installing = !!arg;
      lib.installHome = true;
      if (arg === 'waiting') lib.progress = { current: { id: 'vlc', action: 'install', phase: 'install' }, pending: [{ id: 'spotify', action: 'install' }] };
      if (arg === 'failed') lib.failed.add(lib.install.id);
    },
    // Updated in place (patchHtml): the host sends the progress twice a second, and a dialog
    // drawn afresh each time played its entrance again (a flicker) and lost its focus.
    render() {
      if (!lib.install) { back(); return; }
      const c = findCard(lib.install.id) || lib.install;
      let action, hintList = [['A', 'Select'], ['B', 'Cancel']];
      if (!lib.installing) {
        action = '<div class="il-buttons">' +
          '<button class="il-primary" data-nav data-id="il-home" data-act="installBtn" data-arg="home">Install and add to home</button>' +
          '<button class="il-secondary" data-nav data-id="il-only" data-act="installBtn" data-arg="only">Install only</button></div>';
      } else {
        const st = installState(c);
        const bar = st.bar ? `<div class="il-bar"><div class="il-fill${st.pct === null ? ' going' : ''}" style="width:${st.pct !== null ? st.pct : 100}%"></div></div>` : '';
        action = '<div class="il-progress">' +
          `<div class="il-prow"><span class="il-phase${st.cls ? ' ' + st.cls : ''}">${esc(st.label)}</span>${st.pct !== null ? `<span class="il-pct">${st.pct}%</span>` : ''}</div>` +
          bar + `<span class="il-note">${esc(st.note)}</span></div>` +
          (st.failed ? '<div class="il-buttons"><button class="il-primary" data-nav data-id="il-retry" data-act="installBtn" data-arg="retry">Try again</button></div>' : '');
        hintList = st.failed ? [['A', 'Try again'], ['B', 'Back to library']] : [['B', 'Back to library']];
      }
      patchHtml(el('installing'),
        '<div class="il-dialog">' +
          '<div class="il-head">' +
            `<span class="il-icon">${appIcon({ ...c, color: '' }, 72, 1.5)}</span>` +
            `<div class="il-text"><span class="il-name">${esc(c.name)}</span><span class="il-desc">${esc(c.desc || '')}</span></div>` +
          '</div>' + action +
        '</div>' +
        `<footer class="hints">${hints(hintList)}</footer>`);
    },
  });

  // What the dialog says once A has started the install: running (downloading, installing),
  // waiting behind another app, just out of the queue (the catalog that follows says how it
  // went: starting still), done, or not installed.
  function installState(c) {
    const p = lib.progress.current && lib.progress.current.id === c.id ? lib.progress.current : null;
    const note = 'Keep using the TV. The tile appears when it’s done.';
    if (p) {
      const pct = p.phase === 'download' && p.percent != null ? p.percent : null;
      return { label: p.phase === 'download' ? 'Downloading' : 'Installing', pct, bar: true, note };
    }
    if (lib.failed.has(c.id) && !lib.queued.has(c.id))
      return { label: 'Didn’t install', cls: 'warn', pct: null, failed: true, note: 'Check the network, then try again.' };
    if (lib.queued.has(c.id)) return { label: 'Waiting', pct: null, bar: true, note: 'Another app is installing first. Keep using the TV.' };
    if (c.state === 'installed' || c.state === 'home')
      return { label: 'Done', cls: 'ok', pct: null, note: lib.installHome ? 'Its tile is on the home screen.' : 'Add its tile from the library any time.' };
    return { label: 'Installing', pct: null, bar: true, note };
  }

  function openInstall(card) {
    if (!state.libraryAvailable) { toast('Installing from the TV isn’t set up yet. Run setup once more.', 'warn'); return; }
    lib.install = card;
    lib.installing = false;
    go('installing');
  }
  onAction('installBtn', (node, arg) => {
    if (!lib.install) return;
    if (arg !== 'retry') lib.installHome = arg === 'home';   // Try again: as asked the first time
    lib.installing = true;
    installApp(lib.install.id, lib.installHome);
    render();
  });

  function installApp(id, addToHome) {
    lib.failed.delete(id);
    if (addToHome) lib.homeBound.add(id);
    send({ type: 'library.install', id, addToHome });
  }

  // ---- Apps being installed, on the home screen ----------------------------------------------
  // "Install and add to home": until the app's own tile arrives, a tile says it is installing
  // (dimmed, a dashed edge, the progress); if it did not install, it says so (A tries again, X
  // takes it away).

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
        x: failed ? 'pending-remove' : null, hints: failed ? [['A', 'Try again'], ['X', 'Remove']] : [],
        html: `<span class="pbadge">${statusIcon(st, 22)}${esc(st.label)}</span>` +
          `<span class="pglyph">${appIcon(c, 88)}</span><span class="name">${esc(c.name)}</span>` +
          progressBar(st, 'pbar'),
      };
    });
  });
  onAction('pending-info', (node, id) => { const c = findCard(id); toast(`${c ? c.name : 'The app'} is installing. Its tile opens once it’s ready.`); });
  onAction('pending-retry', (node, id) => { installApp(id, true); render(); });
  onAction('pending-remove', (node, id) => { lib.failed.delete(id); lib.homeBound.delete(id); render(); });

  // ---- Host messages -----------------------------------------------------------------------

  hostMessage('library.', (msg) => {
    switch (msg.type) {
      case 'library.available': state.libraryAvailable = !!msg.available; break;
      case 'library.catalog': {
        // The same catalog again (the host sends it on each look, each logo, each add): nothing to draw.
        const key = JSON.stringify([msg.apps, msg.sites]), failed = [...lib.failed].join();
        const same = key === lib.catalogKey;
        lib.catalogKey = key;
        lib.catalog = { apps: msg.apps || [], sites: msg.sites || [] };
        // Out of the queue and still to install: it did not.
        for (const id of lib.finished) {
          const c = findCard(id);
          if (c && c.state === 'install') lib.failed.add(id); else lib.failed.delete(id);
        }
        lib.finished.clear();
        if (same && failed === [...lib.failed].join()) break;
        if (state.view === 'addtile' && lib.tab === 'library') { render(); focusBodyIfNeeded(); }
        else if (state.view === 'installing' || state.view === 'home') render();
        break;
      }
      case 'library.programs':
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
        // The progress shows on the cards, the dialog and the tiles being installed.
        if (state.view === 'installing' || (state.view === 'addtile' && lib.tab === 'library') || (state.view === 'home' && lib.homeBound.size)) render();
        break;
      }
      case 'library.websiteResult':
        if (msg.ok) { toast(`Added ${msg.name}`); reset('home'); }
        else toast(msg.error || 'That address did not work', 'warn');
        break;
      case 'library.programAdded': toast(`Added ${msg.name}`); reset('home'); break;
    }
  });

  function focusBodyIfNeeded() {
    const focused = focusedEl();
    const main = el('addtile').querySelector('.at-main');
    if (focused && main && main.contains(focused)) return;
    const first = main && main.querySelector('[data-nav]');
    if (first) setFocus(first);
  }

  // ---- Shared on-screen keyboard (rename + website) ----------------------------------------

  function keyboardHtml(mode) {
    const keyAct = mode === 'rename' ? 'renameKey' : 'key';
    const rows = KB_ROWS.map((row) =>
      `<div class="kbi-row">${row.map((c) =>
        `<button class="kbi-key" data-nav data-id="${mode}-k-${c}" data-act="${keyAct}" data-arg="${c}">${c === '/' ? '&#47;' : esc(c)}</button>`).join('')}</div>`).join('');
    const extras = mode === 'website' ? [':'] : [];   // '/' is on the row above
    const doneAct = mode === 'rename' ? 'renameDone' : 'addsite';
    const spaceAct = mode === 'rename' ? 'renameSpace' : 'space';
    const delAct = mode === 'rename' ? 'renameDel' : 'del';
    const bottom = '<div class="kbi-row">' +
      extras.map((c) => `<button class="kbi-key" data-nav data-id="${mode}-k-${c}" data-act="${keyAct}" data-arg="${c}">${c === '/' ? '&#47;' : esc(c)}</button>`).join('') +
      (mode === 'website' ? '<button class="kbi-key wide" data-nav data-id="website-dotcom" data-act="dotcom">.com</button>' : '') +
      `<button class="kbi-key space" data-nav data-id="${mode}-space" data-act="${spaceAct}" aria-label="Space"></button>` +
      `<button class="kbi-key" data-nav data-id="${mode}-del" data-act="${delAct}" aria-label="Delete">${icon('backspace', 30, 2)}</button>` +
      `<button class="kbi-key primary" data-nav data-id="${mode}-done" data-act="${doneAct}">${mode === 'rename' ? 'Save' : 'Add'}</button>` +
      '</div>';
    return rows + bottom;
  }
  onAction('renameDel', () => { lib.draft = lib.draft.slice(0, -1); render(); });
  onAction('renameSpace', () => typeRename(' '));
  onAction('renameDone', saveRename);

  // ---- Demo data (index.html#addtile, #addtile/website, #tileopts...) -----------------------

  function demoData() {
    if (lib.catalog.apps.length) return;
    const C = { youtube: '#FF5B52', stremio: '#7C8CFF', jellyfin: '#3DC0F0', moonlight: '#F5D16B', kodi: '#5AB0FF', vlc: '#FF8A1F', plex: '#F5B82E', spotify: '#1ED760', feishin: '#FF7AB6' };
    lib.catalog = {
      apps: [
        { id: 'youtube', name: 'YouTube', glyph: 'youtube', desc: 'YouTube’s TV interface, without ads', state: 'home' },
        { id: 'stremio', name: 'Stremio', glyph: 'film', desc: 'Movies and shows through add-ons', state: 'home' },
        { id: 'jellyfin', name: 'Jellyfin', glyph: 'library', desc: 'Your Jellyfin library, TV layout', state: 'home' },
        { id: 'moonlight', name: 'Moonlight', glyph: 'moon', desc: 'Play games streamed from your PC', state: 'home' },
        { id: 'kodi', name: 'Kodi', glyph: 'tv', desc: 'Media center for files on your network', state: 'install', canUninstall: true },
        { id: 'vlc', name: 'VLC', glyph: 'play', desc: 'Plays almost any video or audio file', state: 'installed', canUninstall: true },
        { id: 'plex', name: 'Plex HTPC', glyph: 'library', desc: 'Plex’s app made for TVs', state: 'install', canUninstall: true },
        { id: 'spotify', name: 'Spotify', glyph: 'music', desc: 'Music streaming', state: 'installing', canUninstall: true },
        { id: 'feishin', name: 'Feishin', glyph: 'music', desc: 'Music from your Navidrome server', state: 'install', canUninstall: true },
      ].map((a) => demoLogo(Object.assign(a, { color: C[a.id] || '#F3F2EF' }))),
      sites: [
        { id: 'netflix', name: 'Netflix', color: '#FF4B55', state: 'add' },
        { id: 'disneyplus', name: 'Disney+', color: '#4D8DFF', state: 'add' },
        { id: 'primevideo', name: 'Prime Video', color: '#2BB0F5', state: 'add' },
        { id: 'crunchyroll', name: 'Crunchyroll', color: '#FF8A2B', state: 'add' },
        { id: 'tubi', name: 'Tubi', color: '#FFD43B', state: 'add' },
      ].map(demoLogo),
    };
    lib.progress = { current: { id: 'spotify', name: 'Spotify', action: 'install', phase: 'download', percent: 62 }, pending: [] };
    lib.queued = new Set(['spotify']);
    lib.failed = new Set(['feishin']);
    lib.programs = [
      { name: 'File Explorer', launchable: true, onHome: false },
      { name: 'Jellyfin Media Player', launchable: true, onHome: true },
      { name: 'Microsoft Edge', launchable: true, onHome: true },
      { name: 'Notepad', launchable: true, onHome: false },
      { name: 'Store app', launchable: false, note: 'Windows app' },
      { name: 'Task Manager', launchable: true, onHome: false },
      { name: 'Windows Media Player', launchable: false, note: 'Windows Installer shortcut' },
    ];
    // #addtile?many=1, #addtile/onbox?many=1: lists longer than the screen (scrolling, rings).
    if (/[?&]many=1/.test(location.hash)) {
      const more = (list, n, f) => Array.from({ length: n }, (_, i) => f(list[i % list.length], i));
      lib.catalog.apps = lib.catalog.apps.concat(more(lib.catalog.apps, 13, (a, i) =>
        ({ ...a, id: `${a.id}${i}`, name: `${a.name} ${i + 2}`, state: 'install', logo: null })));
      lib.catalog.sites = lib.catalog.sites.concat(more(lib.catalog.sites, 7, (s, i) => ({ ...s, id: `${s.id}${i}`, name: `${s.name} ${i + 2}`, logo: null })));
      lib.programs = more(lib.programs, 26, (p, i) => ({ ...p, name: `${p.name} ${i + 1}` }));
    }
  }

  // Demo: index.html#home?installing=1, tiles for an app installing and one that did not.
  if (!(window.chrome && window.chrome.webview) && /[?&]installing=1/.test(location.hash)) {
    demoData();
    lib.homeBound = new Set(['spotify', 'feishin']);
  }
})();
