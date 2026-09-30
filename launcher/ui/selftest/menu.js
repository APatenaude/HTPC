'use strict';
// Self-test: the Home menu: its alert rows, where it opens, its quick buttons. Run by selftest.js, in its order.
selftestGroup(({ check, sent, lastSent }) => {
  // ---- The Home menu's alert rows -------------------------------------------------------------
  const tiles = state.tiles;
  for (const t of tiles) t.running = true;          // 6 open apps
  state.current = 'jellyfin';
  noticeUpdate({
    toasts: [], pills: [],
    rows: [{ id: 'app:stremio', title: 'Stremio closed unexpectedly', body: 'It stopped working and closed.', glyph: 'warn', tone: 'bad', action: 'Reopen' }],
  });
  reset('home');
  go('menu');
  const row = $('menu').querySelector('[data-alert="app:stremio"]');
  check('menu: the alert row is there', !!row);
  setFocus(row);
  sent.length = 0;
  press('x');
  check('menu: X on the alert row dismisses it', lastSent('alerts.dismiss') && lastSent('alerts.dismiss').id === 'app:stremio', JSON.stringify(sent));
  check('menu: ... and closes no app', state.view === 'menu' && !lastSent('close'), state.view);

  noticeUpdate({ toasts: [], pills: [], rows: [{ id: 'app:stremio', title: 'Stremio closed unexpectedly', glyph: 'warn', tone: 'bad', action: 'Reopen' }] });
  setFocus($('menu').querySelector('[data-alert="app:stremio"]'));
  sent.length = 0;
  press('a');
  check('menu: A on the alert row runs its action', lastSent('alerts.act') && lastSent('alerts.act').id === 'app:stremio', JSON.stringify(sent));
  // Reopen shows "Opening Stremio", which takes every press but Home and B until the host says
  // the window is up: here, no host.
  check('menu: while "Opening" shows, X does nothing under it', (press('x'), state.view === 'menu' && !lastSent('alerts.dismiss')));
  press('b');
  check('menu: B takes "Opening" away and tells the host', !$('opening').classList.contains('on') && lastSent('launchDismissed') && lastSent('launchDismissed').id === 'stremio' && state.view === 'menu');

  setFocus($('menu').querySelector('[data-close="youtube"]'));
  press('x');
  check('menu: X on an app row still asks to close that app', state.view === 'confirm' && state.confirm.id === 'youtube', state.view);
  // Close: its menu row and its tile say "Closing…" until the host no longer lists it running.
  setFocus($('confirm').querySelector('[data-id="confirm-close"]'));
  press('a');
  const ytRow = () => $('menu').querySelector('[data-close="youtube"]');
  check('Close: the app row says Closing… until it is gone', lastSent('close') && lastSent('close').id === 'youtube' && ytRow() && /Closing/.test(ytRow().textContent), ytRow() && ytRow().textContent);
  check('Close: so does its tile', /Closing/.test($('tiles').querySelector('[data-id="tile:youtube"]').textContent));
  onHost({ type: 'state', running: tiles.filter((t) => t.id !== 'youtube').map((t) => t.id) });
  check('Close: gone once the host says so', !ytRow() && !/Closing/.test($('tiles').querySelector('[data-id="tile:youtube"]').textContent));
  for (const t of tiles) t.running = true;
  render();

  // The crowded menu: 6 apps and an alert row fit down to the quick buttons without scrolling;
  // with a second alert, the resource view under them comes into view with the focus.
  const panel = $('menu-panel'), rowsBox = panel.querySelector('.panel-scroll');
  const quicks = [...panel.querySelectorAll('.quicks [data-nav]')];
  const hintsBox = panel.querySelector('.hints').getBoundingClientRect();
  setFocus(panel.querySelector('[data-nav]'));   // the first: the column at its top
  check('menu: 6 apps + an alert row fit, down to the quick buttons', rowsBox.scrollTop === 0 && quicks[quicks.length - 1].getBoundingClientRect().bottom <= rowsBox.getBoundingClientRect().bottom,
    `${quicks[quicks.length - 1].getBoundingClientRect().bottom} > ${rowsBox.getBoundingClientRect().bottom}`);
  check('menu: the quick buttons are above the button hints', quicks[quicks.length - 1].getBoundingClientRect().bottom <= hintsBox.top + 1);
  noticeUpdate({ toasts: [], pills: [], rows: [...notices.rows,
    { id: 'tv', title: 'Can’t reach the TV', body: 'Is it on the network? Settings › TV can find it again.', glyph: 'tv', tone: 'bad', action: 'TV settings' }] });
  setFocus(panel.querySelector('[data-id="q-timer"]'));
  press('down');
  const lowRow = focusedEl(), lowBox = rowsBox.getBoundingClientRect();
  check('menu: crowded (6 apps, 2 alerts), down from the quick buttons to a program: the column scrolls, the row shows whole', lowRow && lowRow.closest('#menu-res') && rowsBox.scrollTop > 0
    && lowRow.getBoundingClientRect().bottom <= lowBox.bottom && lowRow.getBoundingClientRect().top >= lowBox.top, `${focusedEl() && focusedEl().dataset.id}, ${rowsBox.scrollTop}`);

  // Its quick buttons in the order of the home screen's top bar (Settings, then Power).
  const quickIds = quicks.map((e) => e.dataset.id);
  const barIds = [...$('status').querySelectorAll('[data-nav]')].map((e) => e.dataset.id);
  check('menu: Settings then Power, in the order of the home screen\'s top bar',
    barIds.indexOf('settings') >= 0 && barIds.indexOf('settings') < barIds.indexOf('power') &&
    quickIds.join(',') === 'q-buttons,q-timer,q-settings,q-power', `menu ${quickIds.join(',')}; top bar ${barIds.join(',')}`);
  setFocus(panel.querySelector('[data-id="q-timer"]'));
  const walked = [];
  for (let i = 0; i < 3; i++) { press('right'); walked.push(focusedEl() && focusedEl().dataset.id); }
  press('left');
  walked.push(focusedEl() && focusedEl().dataset.id);
  check('menu: right from Timer: Settings, Power, and stays; left: Settings', walked.join(',') === 'q-settings,q-power,q-power,q-settings', walked.join(','));
  press('a');
  const toSettings = state.view;
  press('b');
  check('menu: A on Settings opens Settings, B comes back to it', toSettings === 'settings' && state.view === 'menu' && focusedEl() && focusedEl().dataset.id === 'q-settings',
    `${toSettings}, then ${state.view} on ${focusedEl() && focusedEl().dataset.id}`);
  setFocus(panel.querySelector('[data-id="q-power"]'));
  press('a');
  const toPower = state.view;
  press('b');
  check('menu: A on Power opens Power, B comes back to it', toPower === 'power' && state.view === 'menu' && focusedEl() && focusedEl().dataset.id === 'q-power',
    `${toPower}, then ${state.view} on ${focusedEl() && focusedEl().dataset.id}`);

  // ---- Home lands on an actionable card's row ----------------------------------------------------
  reset('home');
  state.memory.menu = 'volume';
  noticeUpdate({
    toasts: [{ id: 'internet', title: 'No internet', glyph: 'wifi', tone: 'warn', key: 'Home', action: 'Wi-Fi settings' }],
    rows: [{ id: 'internet', title: 'No internet', glyph: 'wifi', tone: 'warn', action: 'Wi-Fi settings' }], pills: [],
  });
  press('home');
  check('Home with an actionable card up: the menu opens on its row', focusedEl() && focusedEl().dataset.id === 'alert:internet', focusedEl() && focusedEl().dataset.id);
  back();
  reset('home');
  state.memory.menu = 'volume';
  menuUsed.control = 'brightness';
  noticeUpdate({ toasts: [{ id: 'phone', title: 'Phone remote connected', glyph: 'phone', tone: 'info' }], rows: [], pills: [] });
  press('home');
  check('Home with only a passing card: the menu opens as usual (over the home screen, the first open app)', focusedEl() && focusedEl().dataset.id === 'app:youtube', focusedEl() && focusedEl().dataset.id);
  back();

  // ---- Where the Home menu opens, and where up and down land in its quick buttons -----------------
  // It opens on Volume, then on the control used last (across openings); with apps open, on the
  // first; over an app, on its Home screen row. Into the quick buttons: the one used last.
  {
    const on = () => focusedEl() && focusedEl().dataset.id;
    const running = (ids) => { for (const t of tiles) t.running = ids.includes(t.id); };
    noticeUpdate({ toasts: [], rows: [], pills: [] });
    menuUsed.control = null; menuUsed.quick = null;
    running([]);
    reset('home');
    press('home');
    const first = on();
    press('down'); press('right');                          // the brightness, changed
    press('b'); press('home');
    const afterBrightness = on();
    const nearestBefore = nearest(focusedEl(), 'down', items(), true);   // where down went before
    press('down');                                          // into the quick buttons, none used yet
    const nearestQuick = on();
    press('right'); press('right');                         // Power
    press('a');
    const toPower = state.view;
    press('b');
    const backOnPower = on();
    press('b'); press('home');
    const afterPower = on();
    check('Menu over the home screen, nothing open: on Volume at first, then on the control used last (the brightness changed, then Power pressed)',
      first === 'volume' && afterBrightness === 'brightness' && toPower === 'power' && backOnPower === 'q-power' && afterPower === 'q-power',
      `${first}; ${afterBrightness}; ${toPower} ${backOnPower}; ${afterPower}`);
    check('Menu: down from the brightness into the quick buttons, none used yet: the one nearest, as before',
      nearestBefore && nearestBefore.classList.contains('quick') && nearestQuick === nearestBefore.dataset.id, `${nearestQuick} (nearest: ${nearestBefore && nearestBefore.dataset.id})`);
    const walk = ['up', 'down', 'down', 'up', 'left', 'up', 'down'].map((b) => { press(b); return on(); });
    check('Menu: into the quick buttons from the brightness or the monitor: the one used last (Power); along the row, as ever',
      walk[0] === 'brightness' && walk[1] === 'q-power' && /^res:/.test(walk[2] || '') && walk[3] === 'q-power' && walk[4] === 'q-settings'
      && walk[5] === 'brightness' && walk[6] === 'q-power', walk.join(','));
    press('b');
    running(['twitch', 'stremio']);
    press('home');
    check('Menu over the home screen with apps open: the first of them, whatever was used last', on() === 'app:twitch', on());
    press('b');
    onHost({ type: 'show', view: 'menu', current: 'twitch' });
    check('Menu over an app: its Home screen row, whatever was used last', on() === 'home', on());
    noticeUpdate({ toasts: [], pills: [], rows: [{ id: 'tv', title: 'Can’t reach the TV', glyph: 'tv', tone: 'bad', action: 'TV settings' }] });
    onHost({ type: 'show', view: 'menu', current: 'twitch', focus: 'alert:tv' });
    check('Menu over an app with an alert on screen: its row, as before', on() === 'alert:tv', on());
    noticeUpdate({ toasts: [], rows: [], pills: [] });
    onHost({ type: 'state', desktop: true });
    onHost({ type: 'show', view: 'menu', current: 'desktop' });
    check('Menu over the desktop: Back to TV, as before', on() === 'back-tv', on());
    onHost({ type: 'state', desktop: false });
    state.current = null; state.backdrop = null;
    menuUsed.control = null; menuUsed.quick = null;
    running(tiles.map((t) => t.id));
    reset('home');
  }
});
