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
  };

  const el = (id) => document.getElementById(id);

  // ---- Home hooks (app.js calls these) -----------------------------------------------------

  onAction('addtile', openAddTile);
  onAction('tile-options', (node, id) => { lib.target = id; go('tileopts'); });
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
  function moveTile(dir) {
    const arr = state.tiles;
    const i = arr.findIndex((t) => t.id === state.moving);
    if (i < 0) return;
    const j = dir === 'left' ? i - 1 : dir === 'right' ? i + 1 : dir === 'up' ? i - COLS : i + COLS;
    if (j < 0 || j >= arr.length) return;
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
          `<div class="to-head"><span style="display:flex;color:${esc(t.color || 'inherit')}">${icon(t.glyph, 40)}</span>${esc(t.name)}</div>` +
          rows.map(([act, glyph, label]) => `<button class="to-item" data-nav data-id="${act}" data-act="${act}">${icon(glyph, 32, 2)}${label}</button>`).join('') +
          '<div class="to-sep"></div>' +
          `<button class="to-item danger" data-nav data-id="opt-remove" data-act="opt-remove">${icon('trash', 32, 2)}Remove from home</button>` +
        '</aside>' +
        `<footer class="hints">${hints([['A', 'Select'], ['B', 'Close']])}</footer>`;
    },
  });

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
      const cur = { glyph: t.glyph, color: t.color };
      el('changeicon').innerHTML =
        '<main class="lib-center"><div class="ci-wrap">' +
          `<div class="ci-preview"><span style="display:flex;color:${esc(cur.color)}">${icon(cur.glyph, 80)}</span><span class="ci-name">${esc(t.name)}</span></div>` +
          '<div class="ci-glyphs">' + ICONS.map((g) =>
            `<button class="ci-glyph${g === cur.glyph ? ' on' : ''}" data-nav data-id="g-${g}" data-act="glyph" data-arg="${g}">${icon(g, 40)}</button>`).join('') + '</div>' +
          '<div class="ci-colors">' + COLORS.map((c) =>
            `<button class="ci-color${c.toUpperCase() === String(cur.color).toUpperCase() ? ' on' : ''}" data-nav data-id="c-${c}" data-act="color" data-arg="${c}"><span style="background:${c}"></span></button>`).join('') + '</div>' +
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
      el('addtile').innerHTML =
        '<header class="at-header"><h1>Add a tile</h1>' + `<nav class="at-tabs" aria-label="Tile source">${tabs}</nav></header>` +
        `<main class="at-main">${body}</main>` +
        `<footer class="hints">${hints(hintList)}</footer>`;
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

  function cardStatus(card) {
    if (card.state === 'installing') {
      const p = lib.progress.current;
      if (p && p.id === card.id) return { label: p.phase === 'download' ? `Downloading ${p.percent}%` : 'Installing', glyph: 'download', color: '#8CC2FF' };
      return { label: 'Queued', glyph: 'download', color: '#8CC2FF' };
    }
    return STATUS[card.state] || STATUS.install;
  }

  function libraryTabHtml() {
    if (!lib.catalog.apps.length && !lib.catalog.sites.length) return '<p class="at-empty">Loading the library…</p>';
    const apps = lib.catalog.apps.map((c) => {
      const st = cardStatus(c);
      return `<button class="lc-app" data-nav data-id="app-${esc(c.id)}" data-act="libcard" data-arg="${esc(c.id)}" data-uninstall="${c.canUninstall ? 1 : 0}">` +
        `<div class="lc-top"><span style="display:flex;color:${esc(c.color)}">${icon(c.glyph, 40)}</span><span class="lc-name">${esc(c.name)}</span></div>` +
        `<span class="lc-desc">${esc(c.desc)}</span>` +
        `<span class="lc-status" style="color:${st.color}">${icon(st.glyph, 22, 2.25)}${esc(st.label)}</span></button>`;
    }).join('');
    const sites = lib.catalog.sites.map((c) => {
      const st = cardStatus(c);
      return `<button class="lc-site" data-nav data-id="site-${esc(c.id)}" data-act="sitecard" data-arg="${esc(c.id)}">` +
        `<span style="display:flex;color:${esc(c.color)}">${icon('globe', 34)}</span>` +
        `<span class="lc-name grow">${esc(c.name)}</span>` +
        `<span style="display:flex;color:${st.color}">${icon(st.glyph, 24, 2.25)}</span></button>`;
    }).join('');
    return '<span class="at-label">Apps</span>' + `<div class="lc-grid">${apps}</div>` +
      '<span class="at-label">Streaming sites · open in Edge, no install</span>' + `<div class="lc-grid sites">${sites}</div>`;
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
    demo() { demoData(); lib.install = lib.catalog.apps.find((c) => c.state === 'install'); lib.installing = false; },
    render() {
      const c = lib.install;
      if (!c) { back(); return; }
      const p = lib.progress.current && lib.progress.current.id === c.id ? lib.progress.current : null;
      let action;
      if (!lib.installing) {
        action = '<div class="il-buttons">' +
          '<button class="il-primary" data-nav data-id="il-home" data-act="installBtn" data-arg="home">Install and add to home</button>' +
          '<button class="il-secondary" data-nav data-id="il-only" data-act="installBtn" data-arg="only">Install only</button></div>';
      } else {
        const pct = p && p.phase === 'download' ? p.percent : null;
        const phase = p && p.phase === 'download' ? 'Downloading' : 'Installing';
        action = '<div class="il-progress">' +
          `<div class="il-prow"><span class="il-phase">${phase}</span>${pct !== null ? `<span class="il-pct">${pct}%</span>` : ''}</div>` +
          `<div class="il-bar"><div class="il-fill" style="width:${pct !== null ? pct : 100}%${pct === null ? ';opacity:.5' : ''}"></div></div>` +
          '<span class="il-note">Keep using the TV. The tile appears when it’s done.</span></div>';
      }
      el('installing').innerHTML =
        '<div class="il-dialog">' +
          '<div class="il-head">' +
            `<span class="il-icon">${icon(c.glyph, 72, 1.5)}</span>` +
            `<div class="il-text"><span class="il-name">${esc(c.name)}</span><span class="il-desc">${esc(c.desc || '')}</span></div>` +
          '</div>' + action +
        '</div>' +
        `<footer class="hints">${hints(lib.installing ? [['B', 'Back to library']] : [['A', 'Select'], ['B', 'Cancel']])}</footer>`;
    },
  });

  function openInstall(card) {
    if (!state.libraryAvailable) { toast('Installing from the TV isn’t set up yet. Run setup once more.', 'warn'); return; }
    lib.install = card;
    lib.installing = false;
    go('installing');
  }
  onAction('installBtn', (node, arg) => {
    if (!lib.install) return;
    lib.installing = true;
    send({ type: 'library.install', id: lib.install.id, addToHome: arg === 'home' });
    render();
  });

  // ---- Host messages -----------------------------------------------------------------------

  hostMessage('library.', (msg) => {
    switch (msg.type) {
      case 'library.available': state.libraryAvailable = !!msg.available; break;
      case 'library.catalog':
        lib.catalog = { apps: msg.apps || [], sites: msg.sites || [] };
        if (state.view === 'addtile' && lib.tab === 'library') { render(); focusBodyIfNeeded(); }
        else if (state.view === 'installing') render();
        break;
      case 'library.programs':
        lib.programs = msg.list || [];
        if (state.view === 'addtile' && lib.tab === 'onbox') { render(); focusBodyIfNeeded(); }
        break;
      case 'library.progress':
        lib.progress = { current: msg.current || null, pending: msg.pending || [] };
        if (state.view === 'installing') render();
        break;
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
    const extras = mode === 'website' ? ['/', ':'] : [];
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
      ].map((a) => Object.assign(a, { color: C[a.id] || '#F3F2EF' })),
      sites: [
        { id: 'netflix', name: 'Netflix', color: '#FF4B55', state: 'add' },
        { id: 'disneyplus', name: 'Disney+', color: '#4D8DFF', state: 'add' },
        { id: 'primevideo', name: 'Prime Video', color: '#2BB0F5', state: 'add' },
        { id: 'crunchyroll', name: 'Crunchyroll', color: '#FF8A2B', state: 'add' },
        { id: 'tubi', name: 'Tubi', color: '#FFD43B', state: 'add' },
      ],
    };
    lib.progress = { current: { id: 'spotify', name: 'Spotify', action: 'install', phase: 'download', percent: 62 }, pending: [] };
    lib.programs = [
      { name: 'File Explorer', launchable: true, onHome: false },
      { name: 'Jellyfin Media Player', launchable: true, onHome: true },
      { name: 'Microsoft Edge', launchable: true, onHome: true },
      { name: 'Notepad', launchable: true, onHome: false },
      { name: 'Store app', launchable: false, note: 'Windows app' },
      { name: 'Task Manager', launchable: true, onHome: false },
      { name: 'Windows Media Player', launchable: false, note: 'Windows Installer shortcut' },
    ];
  }
})();
