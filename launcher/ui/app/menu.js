'use strict';
// The Home menu: its column (alerts, open apps; the controls; the resource view) and hints.
// Part of the page's script: app.js and app\*.js, loaded in index.html's order, share their globals.

// One column in three parts, a line between two: the alerts, the way home and the open apps;
// the sliders and quick buttons; the resource view (resources.js). An empty part is not drawn,
// but all three are always in the page, so a redraw (patchHtml, by place) never makes one part
// of another.
function renderMenu() {
  const running = state.tiles.filter((t) => t.running);
  const apps = running.map((t) =>
    `<div class="row" data-nav data-id="app:${esc(t.id)}" data-act="switch" data-arg="${esc(t.id)}" data-close="${esc(t.id)}">` +
      appIcon(t, 32) + `<span class="grow">${esc(t.name)}</span>` +
      (closing.has(t.id) ? '<span class="tag">Closing…</span>' : t.id === state.current ? '<span class="tag">Now</span>' : '') +
    '</div>').join('');
  const openApps = noticeRowsHtml() + // alerts with something to do (notices.js)
    // Over the Windows desktop (desktop mode) the way back comes first.
    (state.desktop ? `<div class="row big" data-nav data-id="back-tv" data-act="power-action" data-arg="tv">${icon('tv', 32, 2)}Back to TV</div>` : '') +
    // Over an app or the dashboard with the desktop still up: back to it, Explorer left running.
    (state.desktop && state.current !== 'desktop' ? `<div class="row big" data-nav data-id="to-desktop" data-act="power-action" data-arg="desktop">${icon('desktop', 32, 2)}Desktop</div>` : '') +
    (state.current ? `<div class="row big" data-nav data-id="home" data-act="home">${icon('home', 32, 2)}Home screen</div>` : '') +
    (running.length ? `<span class="section">Open apps</span><div class="apps">${apps}</div>` : '');
  // Many open apps (plus alert rows): two columns of shorter rows (notices.css).
  $('menu-panel').classList.toggle('crowded', running.length > 3 || (running.length > 2 && notices.rows.length > 0));
  // In place (patchHtml): the volume changing redraws its value, not the focused row's ring, and
  // the resource view's numbers change in it alone (resPatch). Everything but the hints is in
  // .panel-scroll: with many apps and alerts it scrolls to the focus (setFocus), the hints stay
  // at the bottom.
  patchHtml($('menu-panel'), '<div class="panel-scroll">' +
    `<div class="panel-head"><span class="time">${timeText(new Date())}</span>` +
      `<span class="pad">${icon('controller', 28)}${esc(batteryText())}</span></div>` +
    `<div class="menu-part" data-part="apps">${openApps}</div>` +
    '<div class="menu-part" data-part="controls">' +
      // A sleep timer running: what is left, A adds 15 min, X ends it.
      (state.timer ? `<div class="row timer-row" data-nav data-id="sleep-timer" data-act="timer-extend" data-x="timer-cancel" data-alabel="+15 min" data-xlabel="Cancel timer">` +
        `${icon('timer', 30)}<span class="grow">${esc(timerText())}</span><span class="tag">+15 min</span></div>` : '') +
      `<div class="row slider" data-nav data-id="volume" data-slider="volume">${icon('speaker', 30)}` +
        `<div class="track"><div class="fill" style="width:${state.volume}%"></div></div><span class="value">${state.volume}</span></div>` +
      `<div class="row slider" data-nav data-id="brightness" data-slider="brightness">${icon('sun', 30)}` +
        `<div class="track"><div class="fill white" style="width:${state.brightness}%"></div></div><span class="value">${state.brightness}</span></div>` +
      // Settings, then Power: in the order of the home screen's top bar (renderStatus).
      '<div class="quicks">' +
        `<div class="quick" data-nav data-id="q-buttons" data-act="buttons">${icon('controller', 30)}Buttons</div>` +
        `<div class="quick" data-nav data-id="q-timer" data-act="view" data-arg="timer">${icon('timer', 30)}Timer</div>` +
        `<div class="quick" data-nav data-id="q-settings" data-act="settings">${icon('sliders', 30)}Settings</div>` +
        `<div class="quick" data-nav data-id="q-power" data-act="view" data-arg="power">${icon('power', 30)}Power</div>` +
      '</div>' +
    '</div>' +
    // The box's CPU, memory, disk and network, and what uses the most (resources.js): nothing,
    // nor its line, until the host's first numbers, which then change in it alone.
    `<div class="menu-part" id="menu-res" data-part="monitor">${typeof resViewHtml === 'function' ? resViewHtml() : ''}</div>` +
    '</div>' +
    `<footer class="hints">${hints(menuHints($('menu').querySelector('[data-nav].focused')))}</footer>`);
  // Over an app: what its buttons do, beside the panel (buttons.js; replaces the hint that
  // showed for a few seconds when an app opened).
  if ($('menu-app')) patchHtml($('menu-app'), typeof menuAppCard === 'function' ? menuAppCard() : '');
}

// The controls used last this session, by data-id (null: none yet): the menu opens on the
// control (menuOpening), and up and down into the quick buttons land on the quick button (move).
const menuUsed = { control: null, quick: null };

function menuUse(el) {
  if (state.view !== 'menu' || !el || !el.closest('[data-part="controls"]')) return;
  menuUsed.control = el.dataset.id;
  if (el.classList.contains('quick')) menuUsed.quick = el.dataset.id;
}

// Where the Home menu opens, unless an alert with something to do on screen takes it (the host's
// focus, noticeHomeFocus). Over an app, on its Home screen row (over the desktop, on Back to TV,
// its first row, as ever); over the home screen, on the first open app, else on the control
// used last this session (Volume at first).
function menuOpening() {
  if (state.current) return state.current === 'desktop' ? 'back-tv' : 'home';
  const open = state.tiles.find((t) => t.running);
  return open ? `app:${open.id}` : menuUsed.control || 'volume';
}

// The Home menu's hints follow the focus: X only where it does something (an alert's row: it
// dismisses it; an open app's row, or the app the menu is over: it closes it), left/right on a
// slider (A does nothing there). A program's row in the resource view: resources.js's own.
function menuHints(el) {
  if (el && el.dataset.res && typeof resHints === 'function') return resHints(el);
  const list = [el && el.dataset.slider ? ['←→', 'Change'] : ['A', (el && el.dataset.alabel) || 'Select']];
  if (el && el.dataset.alert) list.push(['X', 'Dismiss']);
  else if (el && el.dataset.xlabel) list.push(['X', el.dataset.xlabel]);
  else {
    const id = (el && el.dataset.close) || state.current;
    if (id && state.tiles.some((t) => t.id === id && t.running)) list.push(['X', 'Close app']);
  }
  list.push(['B', 'Back']);
  return list;
}
