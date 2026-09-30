'use strict';
// Self-test: cards, the home screen's grid. Run by selftest.js, in its order.
selftestGroup(({ check }) => {
  // ---- Cards --------------------------------------------------------------------------------------
  selftestFresh();
  noticeUpdate({ toasts: [
    { id: 'a', title: 'Newest', tone: 'bad' }, { id: 'b', title: 'Older', tone: 'info' },
  ], rows: [], pills: [{ id: 'updates', text: '4 updates', glyph: 'download', tone: 'warn' }] });
  const cards = [...$('toasts').children].map((c) => c.dataset.key);
  check('cards: newest first', cards[0] === 'alert:a' && cards[1] === 'alert:b', cards.join(','));
  toast('A message from the page');
  check('cards: the page\'s own message shows too', [...$('toasts').children].some((c) => c.textContent.includes('A message from the page')));
  reset('home');
  check('status bar: the pill shows', $('status').textContent.includes('4 updates'));

  // ---- Home: three whole rows, a sliver of the next ---------------------------------------------
  // 38 tiles: three whole rows and a sliver of the next where there are more, a row at a time. By
  // layout offsets (as keepTileInView places them), not boxes: a tile just left still zooms out.
  {
    const own = state.tiles;
    const gridFocus = () => focusedEl() && focusedEl().dataset.id;
    state.tiles = Array.from({ length: 38 }, (_, i) => ({ ...own[i % own.length], id: `grid${i}`, name: `Tile ${i + 1}`, running: false }));
    state.memory.home = null;
    reset('home');
    const wrap = $('home').querySelector('.tiles-wrap');
    const shown = [...$('tiles').children].filter((e) => e.classList.contains('tile')).sort((a, b) => a.style.order - b.style.order);
    const tileH = shown[0].offsetHeight, step = shown[4].offsetTop - shown[0].offsetTop;
    // How much of each row shows, as a share of a tile: 1 whole, 0 none.
    const rowsShown = () => Array.from({ length: Math.ceil(shown.length / 4) }, (_, i) => {
      const top = shown[i * 4].offsetTop, from = Math.max(top, wrap.scrollTop), to = Math.min(top + tileH, wrap.scrollTop + wrap.clientHeight);
      return Math.round(Math.max(0, to - from) / tileH * 100) / 100;
    });
    const sliver = (v) => v >= 0.08 && v <= 0.16;
    const pseudo = (p) => getComputedStyle(wrap, p).content;
    check('Home: nothing drawn over the grid\'s ends (no fade, no mask)', ['::before', '::after'].every((p) => pseudo(p) === 'none' || pseudo(p) === 'normal')
      && getComputedStyle(wrap).maskImage === 'none', `${pseudo('::before')} ${pseudo('::after')} ${getComputedStyle(wrap).maskImage}`);
    let seen = rowsShown();
    check('Home, 38 tiles: rows 1 to 3 whole, a sliver of row 4 below, nothing above', wrap.scrollTop === 0 && seen.slice(0, 3).every((v) => v === 1) && sliver(seen[3])
      && seen.slice(4).every((v) => v === 0), seen.join(' '));
    press('down'); press('down');
    check('Home: down to row 3: nothing scrolls', focusedEl() === shown[8] && wrap.scrollTop === 0, `${gridFocus()} ${wrap.scrollTop}`);
    press('down');
    seen = rowsShown();
    check('Home: down to row 4: one row further, rows 2 to 4 whole, a sliver of rows 1 and 5', focusedEl() === shown[12] && wrap.scrollTop === step
      && sliver(seen[0]) && seen.slice(1, 4).every((v) => v === 1) && sliver(seen[4]) && seen.slice(5).every((v) => v === 0), `${wrap.scrollTop} / ${step}: ${seen.join(' ')}`);
    press('up'); press('up');
    check('Home: up to row 2: nothing scrolls', focusedEl() === shown[4] && wrap.scrollTop === step, `${gridFocus()} ${wrap.scrollTop}`);
    press('up');
    check('Home: up to row 1: back to the top', focusedEl() === shown[0] && wrap.scrollTop === 0, `${gridFocus()} ${wrap.scrollTop}`);
    for (let i = 0; i < 12; i++) press('down');
    seen = rowsShown();
    const last = seen.length - 1;
    check('Home: down to the last row (the "+" tile\'s): the last three whole, a sliver above, nothing below', /^tile:\+add$|^tile:grid3[67]$/.test(gridFocus())
      && seen.slice(last - 2).every((v) => v === 1) && sliver(seen[last - 3]) && seen.slice(0, last - 3).every((v) => v === 0) && wrap.scrollTop % step === 0,
      `${gridFocus()} ${wrap.scrollTop}: ${seen.join(' ')}`);
    state.tiles = own;
    state.memory.home = null;
    reset('home');
  }
});
