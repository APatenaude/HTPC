'use strict';
// Self-test: tiles in place, Tile options, moving a tile, an app installing. Run by selftest.js, in its order.
selftestGroup(({ check, sent, lastSent, tileEl }) => {
  // ---- Home: tiles updated in place, Tile options, moving a tile, an app installing -------------
  state.current = null; state.backdrop = null;
  onHost({ type: 'state', running: ['jellyfin'] });
  reset('home');
  const jf = tileEl('jellyfin'), yt = tileEl('youtube');
  setFocus(jf);
  press('x');
  check('Home: X on a running tile asks to close it, on Cancel', state.view === 'confirm' && focusedEl() && focusedEl().dataset.id === 'confirm-cancel');
  press('left');
  press('a');
  check('Home: closed, back on home without its entrance again', state.view === 'home' && $('home').classList.contains('stay') && lastSent('close').id === 'jellyfin');
  onHost({ type: 'state', running: [] });
  check('Home: the app gone, only its tile changes (same elements, no badge)', tileEl('jellyfin') === jf && tileEl('youtube') === yt && !jf.querySelector('.badge'));

  setFocus(tileEl('moonlight'));
  press('start');
  const panelR = $('tileopts').querySelector('.to-panel').getBoundingClientRect(), mR = tileEl('moonlight').getBoundingClientRect();
  check('Tile options: one hint bar (the home one hidden under it)', getComputedStyle($('home-hints')).visibility === 'hidden' && !!$('tileopts').querySelector('.hints'));
  check('Tile options: beside the tile, level with it', panelR.left >= mR.right && panelR.top < mR.bottom && panelR.bottom > mR.top, `${panelR.left},${panelR.top} / ${mR.right},${mR.top}`);
  press('b');
  setFocus(tileEl('jellyfin'));
  press('start');
  const p2 = $('tileopts').querySelector('.to-panel').getBoundingClientRect(), jR = tileEl('jellyfin').getBoundingClientRect();
  check('Tile options: at the right edge, on the tile\'s left and on screen', p2.right <= jR.left && p2.left >= 0, `${p2.left}-${p2.right} / ${jR.left}`);
  press('b');

  setFocus(tileEl('moonlight'));
  press('start');
  press('a');                                       // Move
  const mv = tileEl('moonlight');
  check('Move: the tile is marked, the "+" tile stays (dimmed)', mv.classList.contains('moving') && tileEl('+add') && tileEl('+add').classList.contains('dim'));
  press('right');
  check('Move: it trades places with its neighbour, the same element', state.tiles.findIndex((t) => t.id === 'moonlight') === 5 && tileEl('moonlight') === mv && mv.classList.contains('focused'));
  press('b');
  check('Move: B puts it back', state.tiles.findIndex((t) => t.id === 'moonlight') === 4 && !state.moving);

  // Hold A on a tile to move it: the controller's A comes held, then the host's aHold / aUp.
  const pad = (button, held) => onHost({ type: 'input', button, held });
  const mlAt = () => state.tiles.findIndex((t) => t.id === 'moonlight');
  setFocus(tileEl('moonlight'));
  sent.length = 0;
  pad('a', true);
  const waited = !lastSent('launch') && !$('opening').classList.contains('on');
  pad('aUp');
  check('Hold A: a tap (A, then let go at once) still opens the tile', waited && lastSent('launch') && lastSent('launch').id === 'moonlight', JSON.stringify(sent));
  hideOpening();
  sent.length = 0;
  pad('a', true); pad('aHold');
  check('Hold A: held, the tile goes into move mode, with a buzz, and does not open', state.moving === 'moonlight' && tileEl('moonlight').classList.contains('moving') && lastSent('controller.buzz') && !lastSent('launch'));
  pad('right'); pad('aUp');
  check('Hold A: moved, then let go: dropped there', !state.moving && mlAt() === 5 && lastSent('tile.order'), `${state.moving} ${mlAt()}`);
  pad('a', true); pad('aHold'); pad('aUp');
  check('Hold A: let go without a move, move mode stays', state.moving === 'moonlight');
  sent.length = 0;
  pad('left'); pad('a', true); pad('aUp');
  check('Hold A: ... the D-pad moves it, A again drops it (its release opens nothing)', !state.moving && mlAt() === 4 && lastSent('tile.order') && !lastSent('launch'), `${state.moving} ${mlAt()}`);
  pad('a', true); pad('aHold'); pad('aUp'); pad('right'); pad('b');
  check('Hold A: ... or B puts it back', !state.moving && mlAt() === 4, `${state.moving} ${mlAt()}`);
  sent.length = 0;
  pad('a', true); pad('right'); pad('aHold'); pad('aUp');
  check('Hold A: the focus moved while A was down: nothing opens or moves', !state.moving && !lastSent('launch') && focusedEl() !== tileEl('moonlight'));
  setFocus(tileEl('moonlight'));
  pad('a');
  check('Hold A: the phone\'s A (no release follows) opens at once', lastSent('launch') && lastSent('launch').id === 'moonlight');
  hideOpening();
  // The longest tile hints (a running app: six), in stage pixels: the same at every 16:9 size.
  state.tiles.find((t) => t.id === 'moonlight').running = true;
  setFocus(tileEl('moonlight'));
  const homeBar = $('home-hints');
  check('Hold A: the tile hints say so, and all six fit (a running app)', /Hold A\s*Move/.test(homeBar.textContent) && /Close app/.test(homeBar.textContent) && homeBar.scrollWidth <= homeBar.clientWidth,
    `${homeBar.scrollWidth} > ${homeBar.clientWidth}: ${homeBar.textContent}`);
  state.tiles.find((t) => t.id === 'moonlight').running = false;

  // Install from Add tile: A asks "Install Kodi?" (on Cancel); Yes starts it and Add tile stays up
  // with the focus on its card, which shows the progress; home shows a tile installing it, then
  // that it did not.
  const kodi = { id: 'kodi', name: 'Kodi', glyph: 'tv', color: '#5AB0FF', type: 'app', state: 'install', canUninstall: true };
  state.libraryAvailable = true;
  EXT.actions.addtile();
  onHost({ type: 'library.catalog', apps: [kodi], sites: [] });
  const kodiCard = () => $('addtile').querySelector('[data-id="app-kodi"]');
  setFocus(kodiCard());
  press('a');
  check('Install: A asks "Install Kodi?" first, on Cancel', state.view === 'ask' && /Install Kodi\?/.test($('ask').textContent) && focusedEl() && focusedEl().dataset.id === 'ask-no', state.view);
  setFocus($('ask').querySelector('[data-id="ask-yes"]'));
  press('a');
  check('Install: Yes starts it, with add to home, back on Add tile', state.view === 'addtile' && lastSent('library.install').id === 'kodi' && lastSent('library.install').addToHome === true, state.view);
  onHost({ type: 'library.progress', current: { id: 'kodi', name: 'Kodi', action: 'install', phase: 'download', percent: 40 }, pending: [] });
  check('Install: Add tile stays, on its card, which shows the progress', state.view === 'addtile' && focusedEl() === kodiCard() && /Downloading/.test(kodiCard().textContent) && !!kodiCard().querySelector('.lc-bar'));
  reset('home');
  check('Install: home shows a tile installing it, with the progress', tileEl('~kodi') && /Downloading/.test(tileEl('~kodi').textContent) && !!tileEl('~kodi').querySelector('.pbar'));
  onHost({ type: 'library.progress', current: null, pending: [] });
  onHost({ type: 'library.catalog', apps: [kodi], sites: [] });
  check('Install: it did not install: the tile says so', tileEl('~kodi') && tileEl('~kodi').classList.contains('failed') && /Didn/.test(tileEl('~kodi').textContent));
  setFocus(tileEl('~kodi'));
  check('Install: its hints: A tries again, X removes', /Try again/.test($('home-hints').textContent) && /Remove/.test($('home-hints').textContent));
  press('x');
  check('Install: X takes the tile away', !tileEl('~kodi'));
});
