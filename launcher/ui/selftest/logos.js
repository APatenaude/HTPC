'use strict';
// Self-test: apps' logos. Run by selftest.js, in its order.
selftestGroup(async ({ check, until, imagesIn, lastSent, tileEl, PNG1 }) => {
  // ---- Logos: the app's own where the host has one, the glyph otherwise --------------------------
  selftestFresh();
  const plain = document.createElement('div');
  plain.innerHTML = appIcon({ glyph: 'play', color: '#FF0000' }, 40);
  check('Logos: no logo, the glyph in its colour', !!plain.querySelector('.appicon > svg') && !plain.querySelector('img') && /#FF0000/i.test(plain.innerHTML));
  const savedTiles = state.tiles;
  state.tiles = [
    { id: 'netflix', name: 'Netflix', glyph: 'play', color: '#FF4B55', logo: PNG1, logoUrl: PNG1 },
    { id: 'broken', name: 'Broken', glyph: 'film', color: '#7C8CFF', logo: 'data:image/png;base64,bm90IGFuIGltYWdl', logoUrl: 'x' },
    { id: 'chosen', name: 'Chosen', glyph: 'moon', color: '#F5D16B', logo: null, logoUrl: PNG1 },
  ];
  reset('home');
  await until(() => imagesIn($('tiles')));   // the images load (or fail)
  const tileIcon = (id) => tileEl(id).querySelector('.appicon');
  check('Logos: a tile shows its logo, not the glyph', tileIcon('netflix').classList.contains('has-logo') && getComputedStyle(tileIcon('netflix').querySelector('svg')).display === 'none');
  check('Logos: a logo that does not load gives way to the glyph', !tileIcon('broken').classList.contains('has-logo') && getComputedStyle(tileIcon('broken').querySelector('svg')).display !== 'none');
  check('Logos: a tile whose glyph was chosen shows the glyph', !tileIcon('chosen').querySelector('img'));
  state.tiles.find((t) => t.id === 'netflix').running = true;
  go('menu');
  check('Logos: the Home menu row of an open app has its logo', !!$('menu-panel').querySelector('[data-id="app:netflix"] .appicon.has-logo img'));
  reset('home');
  const ciNode = (id) => $('changeicon').querySelector(`[data-id="${id}"]`);
  EXT.actions['tile-options'](tileEl('netflix'), 'netflix');
  setFocus($('tileopts').querySelector('[data-id="opt-icon"]'));
  press('a');
  check('Logos: Change icon offers the logo first, picked while it shows', state.view === 'changeicon' && ciNode('g-logo') && ciNode('g-logo').classList.contains('on') && !$('changeicon').querySelector('.ci-glyphs .on'));
  setFocus(ciNode('g-logo'));
  press('a');
  check('Logos: A on it asks for the logo back', lastSent('tile.icon').glyph === 'logo' && lastSent('tile.icon').id === 'netflix');
  reset('home');
  EXT.actions['tile-options'](tileEl('chosen'), 'chosen');
  check('Tile options: open on their first item, not where the last ones were left', focusedEl() && focusedEl().dataset.id === 'opt-move', focusedEl() && focusedEl().dataset.id);
  setFocus($('tileopts').querySelector('[data-id="opt-icon"]'));
  press('a');
  check('Logos: a chosen glyph is the one picked, the logo still offered', ciNode('g-logo') && !ciNode('g-logo').classList.contains('on') && ciNode('g-moon').classList.contains('on'));
  state.tiles = savedTiles;
  reset('home');
});
