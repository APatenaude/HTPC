'use strict';
// Self-test: the Home menu's resource view, the menu over an app. Run by selftest.js, in its order.
selftestGroup(async ({ check, sent, lastSent, focusId }) => {
  // ---- The Home menu's resource view (resources.js) ------------------------------------------------
  // Sampled only while the menu shows; patched in place, its rows kept under the focus. The D-pad
  // goes in and out along the column (in its old card the focus was stuck on the TV).
  const rNode = (key) => $('menu-res').querySelector(`[data-id="res:${key}"]`);
  const menuNode = (id) => $('menu-panel').querySelector(`[data-id="${id}"]`);
  const menuIds = () => [...$('menu-panel').querySelectorAll('[data-nav]')].map((e) => e.dataset.id);
  const menuColumn = () => $('menu-panel').querySelector('.panel-scroll');
  const fits = () => menuColumn().scrollHeight <= menuColumn().clientHeight + 1;
  const presses = (buttons) => buttons.map((b) => { press(b); return focusId(); }).join(',');
  // The column's parts on screen, in order, and those with a line over them.
  const shownParts = () => [...menuColumn().querySelectorAll('.menu-part')].filter((p) => p.offsetHeight > 0);
  const partNames = () => shownParts().map((p) => p.dataset.part).join(',');
  const linedParts = () => shownParts().filter((p) => parseFloat(getComputedStyle(p).borderTopWidth) > 0).map((p) => p.dataset.part).join(',');
  const menuHintList = () => [...$('menu-panel').querySelectorAll('footer.hints .hint')].map((h) => h.textContent.trim()).join(',');
  const resData = (top, extra) => ({ type: 'res.data', cpu: 42.4, memUsed: 5120, memTotal: 8192, disk: 12.5e6, down: 45.2e6, up: 1.2e6, top, held: [], ...extra });
  const RJ = { key: 'app:jellyfin', name: 'Jellyfin', app: 'jellyfin', cpu: 30.2, mem: 900, stop: true };
  const RX = { key: 'exe:long.exe', name: `Program ${'with a very long name '.repeat(3)}.exe`, app: null, cpu: 7.25, mem: 2048, stop: true };
  const RW = { key: 'win:windows-update', name: 'Windows Update', app: null, cpu: 3, mem: 120, stop: false };
  check('Resources: the numbers read as the owner reads them',
    [resPercent(0), resPercent(0.04), resPercent(0.4), resPercent(9.94), resPercent(9.96), resPercent(100)].join(' ') === '0% 0% 0.4% 9.9% 10% 100%'
    && [resMemory(356), resMemory(999), resMemory(1843), resMemory(12406)].join(' ') === '356 MB 999 MB 1.8 GB 12.1 GB'
    && JSON.stringify([resRate(0, 'B'), resRate(845.3e6, 'B'), resRate(4.24e6, 'b'), resRate(1.2e9, 'b'), resRate(null, 'b')]) === '[["0","KB/s"],["845","MB/s"],["4.2","Mb/s"],["1.2","Gb/s"],null]');
  sent.length = 0;
  reset('home');
  check('Resources: nothing asked for on the home screen', !sent.some((m) => m.type === 'res.watch' && m.on));

  // Over the home screen with nothing open: neither the Home screen row nor Open apps (no part
  // for them, nor its line), the focus on the volume; all of the column fits. The box's usual top
  // three: the launcher, Windows' own, and one program it may stop.
  for (const t of state.tiles) t.running = false;
  state.memory.menu = null;
  press('home');
  check('Resources: the menu on screen asks the host to sample', lastSent('res.watch') && lastSent('res.watch').on === true);
  res.data = null;
  render();
  check('Resources: no view before the numbers (the menu\'s first frame as it was without it)', $('menu-res').innerHTML === '');
  check('Menu over the home screen, nothing open: no Home screen row, no Open apps, the focus on the volume',
    !menuIds().includes('home') && !/Open apps/i.test($('menu-panel').textContent) && focusId() === 'volume', `${menuIds().join(',')} on ${focusId()}`);
  check('Menu: nothing open, no numbers yet: the controls alone, no empty part drawn, no line',
    partNames() === 'controls' && linedParts() === '' && menuColumn().querySelector('[data-part="apps"]').offsetHeight === 0, `${partNames()}; lined: ${linedParts()}`);
  const RL = { key: 'self', name: 'TV launcher', app: null, cpu: 14, mem: 620, stop: false };
  onHost(resData([RL, RW, RX]));
  const quickBottom = () => menuNode('q-power').getBoundingClientRect().bottom;
  check('Resources: the column\'s last part, under the quick buttons, a line between them; all of it fits, no scrolling',
    $('menu-res').closest('#menu-panel .panel-scroll') && $('menu-res').getBoundingClientRect().top >= quickBottom()
    && partNames() === 'controls,monitor' && linedParts() === 'monitor' && fits(),
    `${$('menu-res').getBoundingClientRect().top} / ${quickBottom()}; ${partNames()}; lined: ${linedParts()}; ${menuColumn().scrollHeight} in ${menuColumn().clientHeight}`);
  // Small, yet read from the couch: no text under 18 px of the 1920 x 1080 stage.
  const tiny = [...$('menu-res').querySelectorAll('*')].filter((e) => [...e.childNodes].some((n) => n.nodeType === 3 && n.nodeValue.trim())
    && parseFloat(getComputedStyle(e).fontSize) < 18);
  const resRowHeights = [...$('menu-res').querySelectorAll('.rs-row')].map((r) => r.offsetHeight);
  check(`Resources: small (${$('menu-res').offsetHeight} px tall, its line included), rows of 38 px, no text under 18 px`,
    $('menu-res').offsetHeight <= 230 && resRowHeights.every((h) => h === 38) && !tiny.length,
    `${resRowHeights.join(' ')}; ${tiny.map((e) => `${e.className || e.tagName} ${getComputedStyle(e).fontSize}`).join(', ')}`);
  const inHome = presses(['down', 'down', 'down']);
  check('Resources, over the home screen: down from the volume, the brightness, a quick button, then the one program it can stop',
    new RegExp(`^brightness,q-\\w+,res:${RX.key.replace('.', '\\.')}$`).test(inHome), inHome);
  const stuckHome = presses(['down', 'left', 'right']);
  check('Resources: ... on the last it can stop, down, left and right leave the focus there', stuckHome === [1, 2, 3].map(() => `res:${RX.key}`).join(','), stuckHome);
  const outHome = presses(['up', 'up']);
  check('Resources: ... up: out of it to a quick button, then the brightness; the rows follow the host again',
    /^q-\w+,brightness$/.test(outHome) && lastSent('res.watch').hold.length === 0, `${outHome}; ${JSON.stringify(lastSent('res.watch'))}`);
  // An app opening under the menu, then closing: its part comes and goes, and the others stay
  // the same elements (redrawn by place, the monitor was made of the controls: it faded in again).
  const resEl = $('menu-res'), controlsEl = menuColumn().querySelector('[data-part="controls"]');
  onHost({ type: 'state', running: ['jellyfin'] });
  const withApp = partNames();
  onHost({ type: 'state', running: [] });
  check('Menu: an app opening under it, then closing: its part comes and goes, the controls and the monitor stay as they are, the focus too',
    withApp === 'apps,controls,monitor' && partNames() === 'controls,monitor' && $('menu-res') === resEl
    && menuColumn().querySelector('[data-part="controls"]') === controlsEl && focusId() === 'brightness', `${withApp}; ${partNames()}; on ${focusId()}`);
  reset('home');

  // Over an app, two open and an alert: the alert, the Home screen row and the open apps, then
  // the controls, then the monitor; all of it fits too.
  for (const t of state.tiles) t.running = t.id === 'jellyfin' || t.id === 'twitch';
  state.current = 'twitch';
  noticeUpdate({ toasts: [], pills: [], rows: [
    { id: 'app:stremio', title: 'Stremio closed unexpectedly', body: 'It stopped working and closed.', glyph: 'warn', tone: 'bad', action: 'Reopen' }] });
  go('menu');
  res.data = null;
  render();
  const panelRow = menuNode('home');
  setFocus(panelRow);
  onHost(resData([RJ, RX, RW], { cpu: 3 }));
  const cardEl = $('menu-res').firstElementChild;
  check('Resources: the view comes with the first numbers, the focus left where it was', cardEl && /3%/.test(cardEl.textContent) && focusedEl() === panelRow);
  onHost(resData([RJ, RX, RW]));
  check('Resources: the numbers patch the view in place, the menu and its focus untouched',
    $('menu-res').firstElementChild === cardEl && menuNode('home') === panelRow && focusedEl() === panelRow
    && /CPU\s*42%/.test($('menu-res').textContent) && /RAM\s*5\.0 \/ 8\.0 GB/.test($('menu-res').textContent) && /Disk\s*13 MB\/s/.test($('menu-res').textContent)
    && /Net\s*↓45 Mb\/s ↑1\.2 Mb\/s/.test($('menu-res').textContent), $('menu-res').textContent.slice(0, 120));
  check('Menu over an app, two open and an alert: the alert, the Home screen row and both apps, then the controls, then the monitor, a line over each of the last two; all of it fits, no scrolling',
    ['alert:app:stremio', 'home', 'app:twitch', 'app:jellyfin'].every((id) => menuNode(id) && menuNode(id).closest('[data-part="apps"]'))
    && partNames() === 'apps,controls,monitor' && linedParts() === 'controls,monitor' && fits(),
    `${menuIds().join(',')}; ${partNames()}; lined: ${linedParts()}; ${menuColumn().scrollHeight} in ${menuColumn().clientHeight}`);
  check('Resources: Windows\' own row shows but takes no focus', rNode(RW.key) && !rNode(RW.key).hasAttribute('data-nav') && rNode(RX.key).hasAttribute('data-nav'));
  const inApp = presses(['down', 'down', 'down', 'down', 'down', 'down']);
  check('Resources, over an app: down the column from Home screen, into the view on its first program',
    /^app:twitch,app:jellyfin,volume,brightness,q-\w+,res:app:jellyfin$/.test(inApp), inApp);
  check('Resources: ... the host keeps these rows for it', JSON.stringify(lastSent('res.watch').hold) === JSON.stringify([RJ.key, RX.key, RW.key]), JSON.stringify(lastSent('res.watch')));
  press('down');
  const onLong = focusedEl();
  press('down');
  check('Resources: down to the last row it can stop, and stays there', onLong === rNode(RX.key) && focusedEl() === onLong, focusedEl() && focusedEl().dataset.id);
  check('Resources: its hints: X ends the program; nothing on A', menuHintList() === 'XEnd program,BBack', menuHintList());
  onHost(resData([RW, { ...RX, cpu: 55 }, RJ]));
  check('Resources: a new ranking under the focus: the rows stay where they are, their numbers change',
    focusedEl() === onLong && rNode(RJ.key) === $('menu-res').querySelectorAll('.rs-row')[0] && /55%/.test(onLong.textContent), $('menu-res').textContent.slice(-120));
  // X ends a program, with a question first as everywhere; never A.
  sent.length = 0;
  press('a');
  check('Resources: A on a program\'s row does nothing: no question, nothing ended', state.view === 'menu' && focusedEl() === onLong && !lastSent('res.stop'), state.view);
  press('x');
  check('Resources: X asks before ending a program, on Cancel', state.view === 'ask' && /^End Program/.test(asking.title) && focusedEl().dataset.id === 'ask-no', asking && asking.title);
  press('a');
  check('Resources: ... Cancel: nothing ended, back on its row', state.view === 'menu' && !lastSent('res.stop') && focusId() === `res:${RX.key}`, `${state.view} on ${focusId()}`);
  press('x');
  press('b');
  check('Resources: ... B: nothing ended either, back on its row', state.view === 'menu' && !lastSent('res.stop') && focusId() === `res:${RX.key}`, `${state.view} on ${focusId()}`);
  press('x');
  setFocus($('ask').querySelector('[data-id="ask-yes"]'));
  press('a');
  check('Resources: yes: the host ends it, back on its row', lastSent('res.stop') && lastSent('res.stop').key === RX.key && state.view === 'menu' && focusedEl() && focusedEl().dataset.id === `res:${RX.key}`);
  onHost(resData([RJ, RW], { held: [{ key: RX.key, gone: true }] }));
  check('Resources: ended: its row stays under the focus, says so, and offers nothing more',
    focusedEl() === rNode(RX.key) && /Ended/.test(rNode(RX.key).textContent) && menuHintList() === 'BBack', menuHintList());
  const outApp = presses(['left', 'up', 'up']);
  check('Resources: left stays; up to the program above, then out to a quick button; the rows follow the host again',
    /^res:exe:long\.exe,res:app:jellyfin,q-\w+$/.test(outApp) && !rNode(RX.key) && lastSent('res.watch').hold.length === 0, outApp);
  press('down');
  check('Resources: on an app\'s row, the hints: X closes it', focusId() === `res:${RJ.key}` && menuHintList() === 'XClose app,BBack', `${focusId()}: ${menuHintList()}`);
  press('x');
  check('Resources: X on an app\'s row asks (Close)', state.view === 'ask' && asking.title === 'Close Jellyfin?');
  setFocus($('ask').querySelector('[data-id="ask-yes"]'));
  press('a');
  check('Resources: yes: the host closes it; its rows say Closing…', lastSent('res.stop').key === RJ.key && /Closing/.test(rNode(RJ.key).textContent)
    && /Closing/.test($('menu-panel').querySelector('[data-close="jellyfin"]').textContent));
  doneClosing('jellyfin');
  onHost({ type: 'blank' });
  check('Resources: the launcher blank (an app in front, standby): sampling stops, the numbers go', lastSent('res.watch').on === false && res.data === null);
  onHost({ type: 'show', view: 'menu', current: 'twitch' });
  check('Resources: the menu back: sampling again', lastSent('res.watch').on === true);
  reset('home');
  check('Resources: the menu left: sampling stops', lastSent('res.watch').on === false);
  noticeUpdate({ toasts: [], rows: [], pills: [] });
  state.current = null;
  for (const t of state.tiles) t.running = t.id === 'jellyfin';

  // The Home menu coming over an app: built at once under the blank stage while its backdrop
  // decodes, shown then, and 'shown' said to the host once drawn (it shows its window on that).
  const frame = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=';
  onHost({ type: 'blank' });
  sent.length = 0;
  onHost({ type: 'show', view: 'menu', current: 'twitch', backdrop: frame, ack: true });
  check('Menu over an app: built at once, under the blank stage', state.view === 'menu' && $('stage').classList.contains('blank') && !lastSent('shown'));
  check('Menu over an app: its panel waits to slide in until it shows', getComputedStyle($('menu').querySelector('.panel')).animationName === 'none');
  await new Promise((r) => setTimeout(r, 500));
  const said = lastSent('shown');
  check('Menu over an app: shown with its backdrop, then the host is told', !$('stage').classList.contains('blank') && $('backdrop').classList.contains('on')
    && said && typeof said.painted === 'boolean' && said.ms >= 0 && said.load >= 0, JSON.stringify(said));
  check('Menu over an app: the panel slides in once shown', getComputedStyle($('menu').querySelector('.panel')).animationName === 'slide-in');
  state.current = null; state.backdrop = null;
  reset('home');

  // Settings › TV left open, then the launcher goes (an app in front, standby): its TV search
  // (every 10 s on the host) stops until the section is on screen again.
  state.section = 'tv';
  reset('settings');
  check('Settings › TV on screen: the host searches', lastSent('tv.showing') && lastSent('tv.showing').on === true);
  onHost({ type: 'blank' });
  check('Blank stage: the TV search stops, no section in view', lastSent('tv.showing').on === false && sectionInView === null);
  onHost({ type: 'show', view: 'settings', section: 'tv' });
  check('Back on screen: the TV search starts again', lastSent('tv.showing').on === true && sectionInView === 'tv');
  reset('home');
  check('Settings left: the TV search stops', lastSent('tv.showing').on === false);
});
