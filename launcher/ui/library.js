'use strict';

// App library and tile editing (SPEC W1, W5). Adds its own views to the launcher UI (app.js owns
// the state, focus and host messaging; this reads and drives them through window.App):
//   tileopts    Start on a home tile: Move, Rename, Change icon, Remove
//   (move mode) on the home screen, the tile follows the D-pad; A places it, B cancels
//   rename      a name and an on-screen keyboard
//   changeicon  a glyph and a colour
//   addtile     Library / On this box / Website tabs (LB, RB switch)
//   installing  the Install dialog and its progress
// Messages to the host: libraryList, libraryInstall, libraryUninstall, startMenu, addProgram,
// addWebsite, tilesOrder, tileRename, tileIcon, tileRemove.

(function () {
  const VIEWS = ['tileopts', 'rename', 'changeicon', 'addtile', 'installing'];
  const COLS = 4;

  const STATUS = {
    home: { label: 'On home screen', glyph: 'home', color: '#8E9199' },
    installed: { label: 'Installed', glyph: 'check', color: '#7FD1AE' },
    install: { label: 'Install', glyph: 'download', color: '#8CC2FF' },
    installing: { label: 'Installing', glyph: 'download', color: '#8CC2FF' },
    add: { label: 'Add tile', glyph: 'plus', color: '#8CC2FF' },
  };

  // The icon-picker choices (kept in step with TileStore.IconChoices / ColorChoices host-side).
  const ICONS = ['play', 'film', 'library', 'music', 'tv', 'globe', 'chat', 'youtube', 'moon',
    'controller', 'app', 'speaker', 'search', 'download', 'home', 'sun', 'power', 'info'];
  const COLORS = ['#FF5B52', '#FF8A1F', '#F5B82E', '#1ED760', '#3DC0F0', '#3CCB9A', '#7C8CFF',
    '#B08CFF', '#FF7AB6', '#F5D16B', '#5AB0FF', '#F3F2EF'];

  const KB_ROWS = [
    '1234567890'.split(''),
    'qwertyuiop'.split(''),
    'asdfghjkl-'.split(''),
    'zxcvbnm./'.split(''),
  ];

  const lib = {
    tab: 'library',
    catalog: { apps: [], sites: [] },
    programs: [],
    progress: { current: null, pending: [] },
    target: null,          // tile id being edited (tileopts / rename / changeicon)
    install: null,         // { id, name, glyph, color, desc } for the Install dialog
    installing: false,     // the dialog has started a job
    website: { name: '', url: '', field: 'url' },
    moveOrigin: null,      // tile order to restore if a move is cancelled
    draft: '',             // the name being typed in the rename view
    confirming: false,     // the "remove a custom tile?" confirm is showing
  };

  let A;                   // window.App, set on first use
  const app = () => (A || (A = window.App));
  const esc = (s) => app().esc(s);
  const icon = (n, s, w) => app().icon(n, s, w);
  const S = () => app().state;
  const el = (id) => document.getElementById(id);

  // Write innerHTML only when it changed, so the once-a-minute clock re-render (app.js) does not
  // reset focus or a scrolled list.
  const htmlCache = {};
  function setHtml(id, html) {
    if (htmlCache[id] === html) return;
    htmlCache[id] = html;
    el(id).innerHTML = html;
  }

  // ---- Public interface used by app.js -----------------------------------------------------

  window.Library = {
    owns: (view) => VIEWS.includes(view),
    render,
    handle,
    activate,
    onHost,
  };

  function render() {
    try { renderInner(); }
    catch (e) {
      let d = document.getElementById('jserr');
      if (!d) { d = document.createElement('div'); d.id = 'jserr'; d.style.cssText = 'position:fixed;left:0;bottom:0;z-index:99999;background:#b00020;color:#fff;font:14px monospace;padding:8px;max-width:100vw;white-space:pre-wrap'; document.body.appendChild(d); }
      d.textContent = 'LIBERR: ' + (e && e.stack ? e.stack : e);
    }
  }

  function renderInner() {
    const view = S().view;
    for (const id of VIEWS) el(id).classList.toggle('on', id === view);
    if (view === 'tileopts') renderTileOpts();
    else if (view === 'rename') renderRename();
    else if (view === 'changeicon') renderChangeIcon();
    else if (view === 'addtile') renderAddTile();
    else if (view === 'installing') renderInstalling();
  }

  // ---- Input -------------------------------------------------------------------------------

  function handle(button) {
    const view = S().view;

    // Move mode lives on the home screen.
    if (S().moving && view === 'home') return handleMove(button);

    // Start on a home tile opens its options.
    if (view === 'home' && button === 'start') {
      const f = app().focusedEl();
      const id = f && f.dataset.arg;
      const tile = id && S().tiles.find((t) => t.id === id);
      if (tile) openTileOptions(tile);
      return true;
    }

    if (view === 'tileopts' && lib.confirming && button === 'b') { lib.confirming = false; app().render(); return true; }

    if (view === 'addtile') {
      if (button === 'lb') { switchTab(-1); return true; }
      if (button === 'rb') { switchTab(1); return true; }
      if (lib.tab === 'website') return handleWebsite(button);
      if (lib.tab === 'library' && button === 'x') { uninstallFocused(); return true; }
      return false;
    }
    return false;
  }

  function handleMove(button) {
    switch (button) {
      case 'up': case 'down': case 'left': case 'right': moveTile(button); return true;
      case 'a': case 'start': dropTile(); return true;
      case 'b': cancelMove(); return true;
      case 'home': case 'homeHold': cancelMove(); return false;  // then app opens the menu / power
      default: return true;   // swallow everything else while moving
    }
  }

  function handleWebsite(button) {
    switch (button) {
      case 'x': typeKey('del'); return true;
      case 'y': typeKey('space'); return true;
      case 'start': saveWebsite(); return true;
      default: return false;  // nav and A go through app.js (A reaches activate)
    }
  }

  function activate(act, arg, node) {
    switch (act) {
      case 'addtile': openAddTile(); break;
      case 'tab': setTab(arg); break;
      case 'libcard': case 'sitecard': cardAction(arg); break;
      case 'addprog': app().send({ type: 'library.addProgram', name: arg }); break;
      case 'prognote': app().toast(node && node.dataset.note ? node.dataset.note : 'That can’t be added'); break;
      case 'opt-move': startMove(); break;
      case 'opt-rename': openRename(); break;
      case 'opt-icon': openChangeIcon(); break;
      case 'opt-remove': removeTarget(); break;
      case 'confirm-remove': doRemove(); break;
      case 'confirm-cancel': lib.confirming = false; app().render(); break;
      case 'field': lib.website.field = arg; app().render(); break;
      case 'key': typeChar(arg); break;
      case 'space': typeKey('space'); break;
      case 'del': typeKey('del'); break;
      case 'dotcom': typeText('.com'); break;
      case 'addsite': saveWebsite(); break;
      case 'renameKey': typeRename(arg); break;
      case 'renameDel': lib.draft = lib.draft.slice(0, -1); app().render(); break;
      case 'renameSpace': typeRename(' '); break;
      case 'renameDone': saveRename(); break;
      case 'glyph': setGlyph(arg); break;
      case 'color': setColor(arg); break;
      case 'iconDone': app().back(); break;
      case 'installBtn': startInstall(arg === 'home'); break;
      case 'installClose': app().back(); break;
    }
  }

  function onHost(msg) {
    switch (msg.type) {
      case 'libraryCatalog':
        lib.catalog = { apps: msg.apps || [], sites: msg.sites || [] };
        if (S().view === 'addtile' || S().view === 'installing') { refresh(); if (lib.tab === 'library') focusBodyIfNeeded(); }
        break;
      case 'programs':
        lib.programs = msg.list || [];
        if (S().view === 'addtile') { refresh(); if (lib.tab === 'onbox') focusBodyIfNeeded(); }
        break;
      case 'libraryProgress':
        lib.progress = { current: msg.current || null, pending: msg.pending || [] };
        if (S().view === 'addtile' || S().view === 'installing') refresh();
        break;
      case 'websiteResult':
        if (msg.ok) { app().toast(`Added ${msg.name}`); app().reset('home'); }
        else app().toast(msg.error || 'That address did not work', 'warn');
        break;
      case 'programAdded':
        app().toast(`Added ${msg.name}`); app().reset('home');
        break;
    }
  }

  function refresh() { if (window.Library) app().render(); }

  function focusBodyIfNeeded() {
    const f = app().focusedEl();
    const body = el('addtile-body');
    if (f && body.contains(f)) return;
    const first = body.querySelector('[data-nav]');
    if (first) app().setFocus(first);
  }

  // ---- Tile options ------------------------------------------------------------------------

  function openTileOptions(tile) {
    lib.target = tile.id;
    lib.confirming = false;
    app().go('tileopts');
  }

  function targetTile() { return S().tiles.find((t) => t.id === lib.target) || null; }

  function renderTileOpts() {
    const t = targetTile();
    if (!t) { app().back(); return; }
    const head = `<div class="to-head"><span style="display:flex;color:${esc(t.color || 'inherit')}">${icon(t.glyph, 40)}</span>${esc(t.name)}</div>`;
    if (lib.confirming) {
      setHtml('tileopts-panel', head +
        '<p class="to-confirm">Remove this tile? Its address and settings are only here, so they’ll be lost.</p>' +
        `<button class="to-item danger" data-nav data-id="rm-yes" data-act="confirm-remove">${icon('trash', 32, 2)}Remove</button>` +
        `<button class="to-item" data-nav data-id="rm-no" data-act="confirm-cancel">${icon('close', 32, 2)}Keep it</button>`);
      setHtml('tileopts-hints', app().hints([['A', 'Select'], ['B', 'Back']]));
      return;
    }
    const rows = [
      ['opt-move', 'move', 'Move'],
      ['opt-rename', 'pencil', 'Rename'],
      ['opt-icon', 'image', 'Change icon'],
    ];
    setHtml('tileopts-panel', head +
      rows.map(([act, glyph, label]) =>
        `<button class="to-item" data-nav data-id="${act}" data-act="${act}">${icon(glyph, 32, 2)}${label}</button>`).join('') +
      '<div class="to-sep"></div>' +
      `<button class="to-item danger" data-nav data-id="opt-remove" data-act="opt-remove">${icon('trash', 32, 2)}Remove from home</button>`);
    setHtml('tileopts-hints', app().hints([['A', 'Select'], ['B', 'Close']]));
  }

  // ---- Move mode ---------------------------------------------------------------------------

  function startMove() {
    lib.moveOrigin = S().tiles.map((t) => t.id);
    S().moving = lib.target;
    app().reset('home');
    focusMoving();
  }

  function moveTile(dir) {
    const arr = S().tiles;
    const i = arr.findIndex((t) => t.id === S().moving);
    if (i < 0) return;
    let j = dir === 'left' ? i - 1 : dir === 'right' ? i + 1 : dir === 'up' ? i - COLS : i + COLS;
    if (j < 0 || j >= arr.length) return;
    [arr[i], arr[j]] = [arr[j], arr[i]];
    app().render();
    focusMoving();
  }

  function focusMoving() {
    const node = document.querySelector('#tiles .tile.moving');
    if (node) app().setFocus(node);
  }

  function dropTile() {
    app().send({ type: 'tile.order', ids: S().tiles.map((t) => t.id) });
    S().moving = null;
    app().render();
    app().toast('Moved');
  }

  function cancelMove() {
    if (lib.moveOrigin) {
      const byId = Object.fromEntries(S().tiles.map((t) => [t.id, t]));
      S().tiles = lib.moveOrigin.map((id) => byId[id]).filter(Boolean);
    }
    S().moving = null;
    lib.moveOrigin = null;
    app().render();
  }

  // ---- Rename ------------------------------------------------------------------------------

  function openRename() {
    const t = targetTile();
    lib.draft = t ? t.name : '';
    app().go('rename');
  }

  function typeRename(ch) { if (lib.draft.length < 24) { lib.draft += ch; app().render(); } }

  function saveRename() {
    app().send({ type: 'tile.rename', id: lib.target, name: lib.draft.trim() });
    app().toast('Renamed');
    app().back();
  }

  function renderRename() {
    const t = targetTile();
    if (!t) { app().back(); return; }
    setHtml('rename-field',
      `<span class="rn-label">Name</span><div class="rn-input">${esc(lib.draft) || '<span class="rn-ph">Type a name</span>'}<span class="rn-caret"></span></div>` +
      `<span class="rn-count">${lib.draft.length}/24</span>`);
    setHtml('rename-kb', keyboardHtml('rename'));
    setHtml('rename-hints', app().hints([['A', 'Type'], ['X', 'Delete'], ['Y', 'Space'], ['Start', 'Save'], ['B', 'Cancel']]));
  }

  // ---- Change icon -------------------------------------------------------------------------

  function openChangeIcon() { app().go('changeicon'); }

  function currentIcon() {
    const t = targetTile();
    return t ? { glyph: t.glyph, color: t.color } : { glyph: 'app', color: '#8CC2FF' };
  }

  function setGlyph(glyph) { app().send({ type: 'tile.icon', id: lib.target, glyph }); app().render(); }
  function setColor(color) { app().send({ type: 'tile.icon', id: lib.target, color }); app().render(); }

  function renderChangeIcon() {
    const t = targetTile();
    if (!t) { app().back(); return; }
    const cur = currentIcon();
    setHtml('changeicon-preview',
      `<span style="display:flex;color:${esc(cur.color)}">${icon(cur.glyph, 80)}</span><span class="ci-name">${esc(t.name)}</span>`);
    setHtml('changeicon-glyphs', ICONS.map((g) =>
      `<button class="ci-glyph${g === cur.glyph ? ' on' : ''}" data-nav data-id="g-${g}" data-act="glyph" data-arg="${g}">${icon(g, 40)}</button>`).join(''));
    setHtml('changeicon-colors', COLORS.map((c) =>
      `<button class="ci-color${c.toUpperCase() === String(cur.color).toUpperCase() ? ' on' : ''}" data-nav data-id="c-${c}" data-act="color" data-arg="${c}" style="color:${c}"><span style="background:${c}"></span></button>`).join(''));
    setHtml('changeicon-hints', app().hints([['A', 'Choose'], ['B', 'Done']]));
  }

  // ---- Add tile: tabs ----------------------------------------------------------------------

  function openAddTile() {
    lib.tab = 'library';
    lib.website = { name: '', url: '', field: 'url' };
    app().go('addtile');
    app().send({ type: 'library.list' });
  }

  const TABS = ['library', 'onbox', 'website'];
  function switchTab(step) { setTab(TABS[(TABS.indexOf(lib.tab) + step + TABS.length) % TABS.length]); }
  function setTab(tab) {
    if (!TABS.includes(tab) || tab === lib.tab) return;
    lib.tab = tab;
    if (tab === 'library') app().send({ type: 'library.list' });
    if (tab === 'onbox') app().send({ type: 'library.startMenu' });
    app().render();
    // Focus the first item of the new tab (a card, a program row, or the Name field).
    const first = el('addtile-body').querySelector('[data-nav]');
    if (first) app().setFocus(first);
  }

  function renderAddTile() {
    const tabsHtml = [['library', 'Library'], ['onbox', 'On this box'], ['website', 'Website']].map(([id, label]) =>
      `<div class="at-tab${id === lib.tab ? ' on' : ''}" data-nav data-id="tab-${id}" data-act="tab" data-arg="${id}">${label}<span class="at-underline"></span></div>`).join('');
    setHtml('addtile-tabs', tabsHtml);
    if (lib.tab === 'library') setHtml('addtile-body', libraryTabHtml());
    else if (lib.tab === 'onbox') setHtml('addtile-body', onboxTabHtml());
    else setHtml('addtile-body', websiteTabHtml());
    setHtml('addtile-hints', app().hints(
      lib.tab === 'website'
        ? [['A', 'Type'], ['X', 'Delete'], ['Y', 'Space'], ['Start', 'Add tile'], ['LB', 'Tabs'], ['B', 'Back']]
        : lib.tab === 'library'
          ? [['A', 'Install or add'], ['X', 'Uninstall'], ['LB', 'Prev tab'], ['RB', 'Next tab'], ['B', 'Back']]
          : [['A', 'Add to home'], ['LB', 'Prev tab'], ['RB', 'Next tab'], ['B', 'Back']]));
  }

  function cardStatus(card) {
    if (card.state === 'installing') {
      const p = lib.progress.current;
      if (p && p.id === card.id) {
        const label = p.phase === 'download' ? `Downloading ${p.percent}%` : 'Installing';
        return { label, glyph: 'download', color: '#8CC2FF' };
      }
      return { label: 'Queued', glyph: 'download', color: '#8CC2FF' };
    }
    return STATUS[card.state] || STATUS.install;
  }

  function libraryTabHtml() {
    if (!lib.catalog.apps.length && !lib.catalog.sites.length)
      return '<p class="at-empty">Loading the library…</p>';
    const appCards = lib.catalog.apps.map((c) => {
      const st = cardStatus(c);
      return `<button class="lc-app" data-nav data-id="app-${esc(c.id)}" data-act="libcard" data-arg="${esc(c.id)}" data-state="${esc(c.state)}" data-uninstall="${c.canUninstall ? 1 : 0}">` +
        `<div class="lc-top"><span style="display:flex;color:${esc(c.color)}">${icon(c.glyph, 40)}</span><span class="lc-name">${esc(c.name)}</span></div>` +
        `<span class="lc-desc">${esc(c.desc)}</span>` +
        `<span class="lc-status" style="color:${st.color}">${icon(st.glyph, 22, 2.25)}${esc(st.label)}</span></button>`;
    }).join('');
    const siteCards = lib.catalog.sites.map((c) => {
      const st = cardStatus(c);
      return `<button class="lc-site" data-nav data-id="site-${esc(c.id)}" data-act="sitecard" data-arg="${esc(c.id)}" data-state="${esc(c.state)}">` +
        `<span style="display:flex;color:${esc(c.color)}">${icon('globe', 34)}</span>` +
        `<span class="lc-name grow">${esc(c.name)}</span>` +
        `<span style="display:flex;color:${st.color}">${icon(st.glyph, 24, 2.25)}</span></button>`;
    }).join('');
    return '<span class="at-label">Apps</span>' +
      `<div class="lc-grid">${appCards}</div>` +
      '<span class="at-label">Streaming sites · open in Edge, no install</span>' +
      `<div class="lc-grid sites">${siteCards}</div>`;
  }

  function onboxTabHtml() {
    if (!lib.programs.length) return '<p class="at-empty">Reading what’s installed…</p>';
    const rows = lib.programs.map((p) => {
      if (!p.launchable)
        return `<button class="ob-row muted" data-nav data-id="ob-${esc(p.name)}" data-act="prognote" data-note="${esc(p.note || '')}">` +
          `<span style="display:flex">${icon('app', 40)}</span><span class="grow">${esc(p.name)}</span><span class="ob-tag">${esc(p.note || '')}</span></button>`;
      return `<button class="ob-row${p.onHome ? ' on-home' : ''}" data-nav data-id="ob-${esc(p.name)}" data-act="addprog" data-arg="${esc(p.name)}">` +
        `<span style="display:flex">${icon('app', 40)}</span><span class="grow">${esc(p.name)}</span>` +
        `${p.onHome ? '<span class="ob-tag">On home screen</span>' : ''}</button>`;
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
        `<span class="ws-cname">${esc(w.name) || esc(app_previewName(w.url))}</span></div></div>` +
      '</div>' +
      `<div class="ws-kb">${keyboardHtml('website')}</div>`;
  }

  function app_previewName(url) {
    if (!url) return 'Website';
    return url.replace(/^https?:\/\//, '').replace(/^www\./, '').split('/')[0] || 'Website';
  }

  // ---- Shared on-screen keyboard (rename + website) ----------------------------------------

  function keyboardHtml(mode) {
    const keyAct = mode === 'rename' ? 'renameKey' : 'key';
    const rows = KB_ROWS.map((row) =>
      `<div class="kbi-row">${row.map((c) =>
        `<button class="kbi-key" data-nav data-id="${mode}-k-${c}" data-act="${keyAct}" data-arg="${c}">${c}</button>`).join('')}</div>`).join('');
    const extraChars = mode === 'website' ? ['/', ':'] : [];
    const bottom = '<div class="kbi-row">' +
      extraChars.map((c) => `<button class="kbi-key" data-nav data-id="${mode}-k-${c}" data-act="${keyAct}" data-arg="${c}">${c === '/' ? '&#47;' : c}</button>`).join('') +
      (mode === 'website' ? `<button class="kbi-key wide" data-nav data-id="website-dotcom" data-act="dotcom">.com</button>` : '') +
      `<button class="kbi-key space" data-nav data-id="${mode}-space" data-act="${mode === 'rename' ? 'renameSpace' : 'space'}" aria-label="Space"></button>` +
      `<button class="kbi-key" data-nav data-id="${mode}-del" data-act="${mode === 'rename' ? 'renameDel' : 'del'}" aria-label="Delete">${icon('backspace', 30, 2)}</button>` +
      `<button class="kbi-key primary" data-nav data-id="${mode}-done" data-act="${mode === 'rename' ? 'renameDone' : 'addsite'}">${mode === 'rename' ? 'Save' : 'Add'}</button>` +
      '</div>';
    return rows + bottom;
  }

  // ---- Website form editing ----------------------------------------------------------------

  function activeField() { return lib.website.field === 'name' ? 'name' : 'url'; }
  function typeChar(ch) { typeText(ch); }
  function typeText(text) {
    const f = activeField();
    const max = f === 'name' ? 24 : 2048;
    lib.website[f] = (lib.website[f] + text).slice(0, max);
    app().render();
  }
  function typeKey(which) {
    const f = activeField();
    if (which === 'del') lib.website[f] = lib.website[f].slice(0, -1);
    else if (which === 'space' && f === 'name') lib.website[f] = (lib.website[f] + ' ').slice(0, 24);
    app().render();
  }
  function saveWebsite() {
    app().send({ type: 'library.addWebsite', name: lib.website.name, url: lib.website.url });
  }

  // ---- Library card actions ----------------------------------------------------------------

  function findCard(id) {
    return lib.catalog.apps.find((c) => c.id === id) || lib.catalog.sites.find((c) => c.id === id);
  }

  function cardAction(id) {
    const card = findCard(id);
    if (!card) return;
    if (card.state === 'installing') { app().toast(`${card.name} is installing…`); return; }
    if (card.state === 'home') { app().toast(`${card.name} is already on your home screen`); return; }
    if (card.state === 'install') { openInstall(card); return; }
    // installed app or a website: add it to the home screen.
    addToHome(id);
    app().toast(`Added ${card.name}`);
    app().reset('home');
  }

  function addToHome(id) {
    const ids = S().tiles.map((t) => t.id);
    if (!ids.includes(id)) ids.push(id);
    app().send({ type: 'tile.order', ids });
  }

  function uninstallFocused() {
    const f = app().focusedEl();
    if (!f || f.dataset.uninstall !== '1') return;
    const id = f.dataset.arg;
    const card = findCard(id);
    if (!card || (card.state !== 'installed' && card.state !== 'home')) { app().toast('That app is not installed'); return; }
    if (!S().libraryAvailable) { app().toast('Installing from the TV isn’t set up yet', 'warn'); return; }
    app().send({ type: 'library.uninstall', id });
    app().toast(`Uninstalling ${card.name}…`);
  }

  // ---- Install dialog ----------------------------------------------------------------------

  function openInstall(card) {
    if (!S().libraryAvailable) { app().toast('Installing from the TV isn’t set up yet. Run setup once more.', 'warn'); return; }
    lib.install = card;
    lib.installing = false;
    app().go('installing');
  }

  function startInstall(addToHomeToo) {
    if (!lib.install) return;
    lib.installing = true;
    app().send({ type: 'library.install', id: lib.install.id, addToHome: addToHomeToo });
    app().render();
  }

  function renderInstalling() {
    const c = lib.install;
    if (!c) { app().back(); return; }
    const p = lib.progress.current && lib.progress.current.id === c.id ? lib.progress.current : null;
    const busy = lib.installing;
    let action;
    if (!busy) {
      action = '<div class="il-buttons">' +
        `<button class="il-primary" data-nav data-id="il-home" data-act="installBtn" data-arg="home">Install and add to home</button>` +
        `<button class="il-secondary" data-nav data-id="il-only" data-act="installBtn" data-arg="only">Install only</button>` +
        '</div>';
    } else {
      const phase = p && p.phase === 'download' ? `Downloading` : 'Installing';
      const pct = p && p.phase === 'download' ? p.percent : null;
      action = '<div class="il-progress">' +
        `<div class="il-prow"><span class="il-phase">${phase}</span>${pct !== null ? `<span class="il-pct">${pct}%</span>` : ''}</div>` +
        `<div class="il-bar"><div class="il-fill" style="width:${pct !== null ? pct : 100}%${pct === null ? ';opacity:.5' : ''}"></div></div>` +
        '<span class="il-note">Keep using the TV. The tile appears when it’s done.</span></div>';
    }
    el('installing-box').innerHTML =
      '<div class="il-head">' +
        `<span class="il-icon">${icon(c.glyph, 72, 1.5)}</span>` +
        `<div class="il-text"><span class="il-name">${esc(c.name)}</span><span class="il-desc">${esc(c.desc || '')}</span></div>` +
      '</div>' + action;
    el('installing-hints').innerHTML = app().hints(busy ? [['B', 'Back to library']] : [['A', 'Select'], ['B', 'Cancel']]);
    if (busy) {
      const box = el('installing-box').querySelector('[data-nav]');
      // Nothing to focus while busy; keep focus off the buttons.
    }
  }

  // ---- Remove ------------------------------------------------------------------------------

  function removeTarget() {
    const t = targetTile();
    if (!t) return;
    // A custom tile (added website or program) keeps its details only here, so ask first (SPEC
    // decision); a catalog app can always be re-added from the library, so it goes at once.
    if (t.custom) { lib.confirming = true; app().render(); }
    else doRemove();
  }

  function doRemove() {
    const t = targetTile();
    if (!t) return;
    app().send({ type: 'tile.remove', id: t.id });
    lib.confirming = false;
    app().toast(`${t.name} removed`);
    app().reset('home');
  }

  // ---- Demo (opened in a plain browser via index.html#addtile etc.) -------------------------

  if (!(window.chrome && window.chrome.webview)) {
    // Wait for app.js to publish App, then feed demo data. Demo hashes:
    //   #addtile #onbox #website  -> the Add tile screen on that tab
    //   #tileopts #rename #changeicon #installing -> those views (targeting the first tile)
    setTimeout(function () {
      if (!window.App) return;
      const A2 = window.App, st = A2.state;
      // Set the data a view needs BEFORE switching to it, so its render does not bail to home.
      lib.catalog = demoCatalog();
      lib.programs = demoPrograms();
      const hash = location.hash.slice(1);
      let view = hash;
      if (hash === 'onbox') { view = 'addtile'; lib.tab = 'onbox'; }
      else if (hash === 'website') { view = 'addtile'; lib.tab = 'website'; }
      else if (hash === 'addtile') { view = 'addtile'; lib.tab = 'library'; }
      if (['tileopts', 'rename', 'changeicon'].includes(view)) lib.target = st.tiles[0] && st.tiles[0].id;
      if (view === 'rename') lib.draft = (st.tiles[0] && st.tiles[0].name) || '';
      if (view === 'installing') lib.install = lib.catalog.apps.find((c) => c.state === 'install');
      if (VIEWS.includes(view)) { st.stack = ['home']; st.view = view; }
      A2.render();
    }, 0);
  }

  function demoCatalog() {
    const C = { youtube: '#FF5B52', stremio: '#7C8CFF', jellyfin: '#3DC0F0', moonlight: '#F5D16B', kodi: '#5AB0FF', vlc: '#FF8A1F', plex: '#F5B82E', spotify: '#1ED760', feishin: '#FF7AB6' };
    const apps = [
      { id: 'youtube', name: 'YouTube', glyph: 'youtube', desc: 'YouTube’s TV interface, without ads', state: 'home' },
      { id: 'stremio', name: 'Stremio', glyph: 'film', desc: 'Movies and shows through add-ons', state: 'home' },
      { id: 'jellyfin', name: 'Jellyfin', glyph: 'library', desc: 'Your Jellyfin library, TV layout', state: 'home' },
      { id: 'moonlight', name: 'Moonlight', glyph: 'moon', desc: 'Play games streamed from your PC', state: 'home' },
      { id: 'kodi', name: 'Kodi', glyph: 'tv', desc: 'Media center for files on your network', state: 'install', canUninstall: true },
      { id: 'vlc', name: 'VLC', glyph: 'play', desc: 'Plays almost any video or audio file', state: 'installed', canUninstall: true },
      { id: 'plex', name: 'Plex HTPC', glyph: 'library', desc: 'Plex’s app made for TVs', state: 'install', canUninstall: true },
      { id: 'spotify', name: 'Spotify', glyph: 'music', desc: 'Music streaming', state: 'installing', canUninstall: true },
      { id: 'feishin', name: 'Feishin', glyph: 'music', desc: 'Music from your Navidrome server', state: 'install', canUninstall: true },
    ].map((a) => Object.assign(a, { color: C[a.id] || '#F3F2EF' }));
    const sites = [
      { id: 'netflix', name: 'Netflix', color: '#FF4B55', state: 'add' },
      { id: 'disneyplus', name: 'Disney+', color: '#4D8DFF', state: 'add' },
      { id: 'primevideo', name: 'Prime Video', color: '#2BB0F5', state: 'add' },
      { id: 'crunchyroll', name: 'Crunchyroll', color: '#FF8A2B', state: 'add' },
      { id: 'tubi', name: 'Tubi', color: '#FFD43B', state: 'add' },
    ];
    lib.progress = { current: { id: 'spotify', name: 'Spotify', action: 'install', phase: 'download', percent: 62 }, pending: [] };
    return { apps, sites };
  }

  function demoPrograms() {
    return [
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
