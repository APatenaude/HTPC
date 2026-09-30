'use strict';
// The host's messages (onHost), the demo data without a host, and the start: fit, render, ready.
// Part of the page's script: app.js and app\*.js, loaded in index.html's order, share their globals.

// ---- Host messages ------------------------------------------------------------------------

function onHost(msg) {
  switch (msg.type) {
    case 'init':
      state.tiles = msg.tiles;
      Object.assign(state, msg.settings || {});
      if (msg.prefs) Object.assign(state.prefs, msg.prefs);
      if (msg.power) state.power = msg.power;
      if ('libraryAvailable' in msg) state.libraryAvailable = msg.libraryAvailable;
      render();
      break;
    case 'tiles': state.tiles = msg.tiles; render(); break;
    case 'blank': $('stage').classList.add('blank'); sectionHooks(underViews()); break;   // the section in view is left
    case 'opened':
      hideOpening();
      if (!msg.ok && msg.text) toast(msg.text, 'warn'); // failures come as alerts now
      break;
    case 'state':
      if (msg.running) {
        for (const t of state.tiles) t.running = msg.running.includes(t.id);
        for (const id of [...closing.keys()]) if (!msg.running.includes(id)) doneClosing(id);
        // The app the menu was opened over has closed: B and Home now lead home, not to it (not
        // the desktop: the host takes B there).
        if (state.current && state.current !== 'desktop' && !msg.running.includes(state.current)) { state.current = null; state.backdrop = null; }
      }
      for (const k of ['volume', 'brightness', 'battery', 'controller', 'alert', 'phone', 'desktop']) if (k in msg) state[k] = msg[k];
      if ('timer' in msg) state.timer = msg.timer;
      render();
      break;
    case 'input': press(msg.button, !!msg.held); break;
    case 'show': {
      hideOpening();
      const asked = performance.now();
      const view = () => {
        state.current = msg.current || null;
        state.backdrop = msg.backdrop || null;
        // focus: the element to land on (an alert's row, the tile of an app that just closed);
        // section: the Settings section to open (an alert's action).
        if (msg.focus) state.memory[msg.view] = msg.focus;
        else if (msg.view === 'settings') state.memory.settings = null;   // on the section list
        else if (msg.view === 'menu') state.memory.menu = menuOpening();
        if (msg.section) state.section = msg.section;
        reset(msg.view);
      };
      const unblank = () => {
        const stage = $('stage');
        // Over an app the backdrop is the app's own frame: no fade up from dark, it is there at once.
        if (msg.backdrop) stage.style.transition = 'none';
        stage.classList.remove('blank');
        if (msg.backdrop) { void stage.offsetWidth; stage.style.transition = ''; }
        sectionHooks(underViews());   // a Settings section back on screen is shown again
      };
      if (!msg.backdrop) { view(); unblank(); if (msg.ack) ackShown(asked, 0); break; }
      // Shown once its backdrop is decoded, so it does not flash the home screen first. The view
      // is built meanwhile under the blank stage (the window is still hidden behind the app).
      const early = $('stage').classList.contains('blank');
      if (early) view();
      const img = new Image();
      let done = false;
      const once = () => {
        if (done) return;
        done = true;
        if (!early) view();
        unblank();
        if (msg.ack) ackShown(asked, Math.round(performance.now() - asked));
      };
      img.src = msg.backdrop;
      img.decode().then(once, once);
      setTimeout(once, 400);
      break;
    }
    case 'toast': toast(msg.text, msg.kind); break;
    default: {
      // Added scripts' messages: by type ("wifi.list"), else by prefix ("wifi.").
      const handler = EXT.host[msg.type] || EXT.host[String(msg.type).split('.')[0] + '.'];
      if (handler) handler(msg);
    }
  }
}

// The Home menu over an app is drawn: the host shows its window now (MainForm.RevealPending).
// The page keeps drawing while the window is hidden, so this waits for the frame with the menu
// in it (two animation frames), and the first frame the TV gets is that one, not the black the
// page last showed. A page that draws nothing (hidden) answers at once, a slow one after 150 ms.
// load: ms to decode the backdrop; ms: from the host's message to this answer.
function ackShown(asked, load) {
  let sent = false;
  const answer = (painted) => {
    if (sent) return;
    sent = true;
    send({ type: 'shown', painted, load, ms: Math.round(performance.now() - asked) });
  };
  if (document.hidden) { answer(false); return; }
  requestAnimationFrame(() => requestAnimationFrame(() => answer(true)));
  setTimeout(() => answer(false), 150);
}

if (host) {
  host.addEventListener('message', (e) => onHost(e.data));
} else {
  // Demo data for a plain browser. ?logos=1 in the hash: logos from logos\<id>.png next to the
  // page (none ship; a missing one shows the glyph), e.g. index.html#home?logos=1.
  onHost({ type: 'init', tiles: [
    { id: 'youtube', name: 'YouTube', glyph: 'youtube', color: '#FF5B52' },
    { id: 'twitch', name: 'Twitch', glyph: 'chat', color: '#B08CFF' },
    { id: 'stremio', name: 'Stremio', glyph: 'film', color: '#7C8CFF' },
    { id: 'jellyfin', name: 'Jellyfin', glyph: 'library', color: '#3DC0F0', running: true },
    { id: 'moonlight', name: 'Moonlight', glyph: 'moon', color: '#F5D16B' },
    { id: 'edge', name: 'Browser', glyph: 'globe', color: '#3CCB9A' }
  ].map(demoLogo), settings: { controller: true, battery: 'full' } });
  // ?tiles=18 in the hash: that many tiles (more than fit: the grid scrolls), e.g. #home?tiles=18.
  const more = /[?&]tiles=(\d+)/.exec(location.hash);
  if (more) state.tiles = Array.from({ length: Number(more[1]) }, (_, i) => ({ ...state.tiles[i % 6], id: `${state.tiles[i % 6].id}${i || ''}`, name: `${state.tiles[i % 6].name}${i >= 6 ? ' ' + (i + 1) : ''}`, running: i === 3 }));
}

// Demo (no host) with ?logos=1: an item's logo is logos/<id>.png beside the page.
function demoLogo(a) {
  if (!host && /[?&]logos=1/.test(location.hash)) a.logo = a.logoUrl = `logos/${a.id}.png`;
  return a;
}

// index.html#view or #view/arg in a plain browser: #settings/wifi opens that section (with
// its demo data), #maps or #buttons/twitch an added view.
function demoRoute(hash) {
  // "?..." after the route is the screen's own demo options (#settings/tv?demo=paused): theirs to read.
  const [view, arg] = hash.split('?')[0].split('/');
  if (view === 'settings' && arg) {
    state.section = arg;
    if (EXT.sections[arg] && EXT.sections[arg].demo) EXT.sections[arg].demo();
  }
  if (EXT.views[view] && EXT.views[view].demo) EXT.views[view].demo(arg);
  // #timer/video: the sleep timer set to "when this video ends", 23 minutes left.
  if (view === 'timer' && arg === 'video') state.timer = { label: 'This video ends', endsAt: 'video', minutesLeft: 23 };
  // #menu/twitch: the Home menu over that app, open (its buttons beside the panel).
  if (view === 'menu' && arg) {
    const t = state.tiles.find((x) => x.id === arg);
    if (t) { t.running = true; state.current = arg; }
    if (typeof mapsDemo === 'function') mapsDemo();
  }
  go(view);
}

fit();
render();
// Redraw when the minute (or the timer countdown) changes; render() keeps the focus.
let shown = '';
setInterval(() => {
  const now = timeText(new Date()) + timerText();
  if (now !== shown) { shown = now; render(); }
}, 1000);
// Once the scripts after this one have added their screens: the host's first messages may
// be theirs. Demo only: index.html#settings (or #menu, #settings/sound, #maps...) opens that
// view, for screenshots.
addEventListener('DOMContentLoaded', () => {
  if (!host && location.hash) demoRoute(location.hash.slice(1));
  send({ type: 'ready' });
});
