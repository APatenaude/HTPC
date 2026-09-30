'use strict';
// The focus: setting it, keeping it across renders, and where the D-pad takes it (nearest).
// Part of the page's script: app.js and app\*.js, loaded in index.html's order, share their globals.

// ---- Focus and spatial navigation ---------------------------------------------------------

function viewEl() { return $(state.view); }
function items() { return [...viewEl().querySelectorAll('[data-nav]')]; }
function focusedEl() { return viewEl().querySelector('[data-nav].focused'); }

// Only a focus the user moved to is remembered; a default pick is made again on the next
// render (the first render happens before the tiles arrive from the host).
function setFocus(el, chosen = true) {
  for (const f of document.querySelectorAll('.focused')) f.classList.remove('focused');
  if (!el) return;
  el.classList.add('focused');
  if (chosen) state.memory[state.view] = el.dataset.id;
  if (state.view === 'home') { updateHomeHints(); keepTileInView(el); }
  // Settings: moving through the section list shows each section right away.
  if (el.dataset.section && el.dataset.section !== state.section) {
    state.section = el.dataset.section;
    render();
    return;
  }
  if (state.view === 'settings') settingsFocused(el);
  if (state.view === 'menu') {
    if (typeof resFocus === 'function') resFocus(el);   // resources.js: its rows stay in place under the focus
    const bar = $('menu-panel').querySelector('footer.hints');
    if (bar) patchHtml(bar, hints(menuHints(el)));
  }
  const view = EXT.views[state.view];
  if (view && view.focused) view.focused(el);
  // Anything in a box that scrolls (a list, a panel, a dialog's list) comes into view, ring and
  // all. (The home grid: keepTileInView, by layout, as its tiles slide.)
  const box = state.view !== 'home' && scrollerOf(el);
  if (box) scrollIntoBox(el, box, 16);
  noticeAvoid(el);   // the alerts' cards move off it (notices.js)
}

// prev: { id, rect, section, el } of the element focused before this render, in the same view
// (render), else null. Not chosen (the view's first pick), it stays where it is all the same
// (an alert's row arriving above it does not take the focus from under the user), but on the
// home screen, whose first pick waits for the tiles. Taken away by the render: its new element
// if there is one (the same TV, the list sorted again), else the one nearest its place.
function restoreFocus(id, prev) {
  const list = items();
  const kept = list.find((e) => e.dataset.id === id) || list.find((e) => e.dataset.id === state.memory[state.view]);
  if (kept) { setFocus(kept); return; }
  // A memory set to null asks for the view's first pick again (a question opening on Cancel, a
  // new list in the button editor): then neither.
  const gone = prev && !prev.el.isConnected && state.memory[state.view] !== null ? prev : null;
  if (prev && state.memory[state.view] !== null && (gone || state.view !== 'home')) {
    const same = list.find((e) => e.dataset.id === prev.id);
    if (same) { setFocus(same, false); return; }
  }
  if (gone) {
    const r = gone.rect, cx = r.left + r.width / 2, cy = r.top + r.height / 2;
    let best = null, bestD = Infinity;
    for (const e of list) {
      if (!!e.dataset.section !== gone.section) continue;   // Settings: in the same column
      const q = e.getBoundingClientRect();
      const d = Math.abs(q.left + q.width / 2 - cx) + 2 * Math.abs(q.top + q.height / 2 - cy);
      if (d < bestD) { bestD = d; best = e; }
    }
    if (best) { setFocus(best); return; }
  }
  const view = EXT.views[state.view];
  setFocus((view && view.focus ? view.focus(list) : null) ||
    // Home: the first tile as shown (CSS order). The "+" tile, made by the first render before the
    // tiles came, is first in the page but shows last: after a first boot the focus sat on it.
    (state.view === 'home' ? list.filter((e) => e.classList.contains('tile'))
      .sort((a, b) => (Number(a.style.order) || 0) - (Number(b.style.order) || 0))[0] : null) ||
    // Settings opens on the section list, on the section last shown (A or right goes into it).
    (state.view === 'settings' ? list.find((e) => e.dataset.section === state.section) : null) ||
    // A confirmation opens on Cancel: one press of A never closes an app by mistake.
    (state.view === 'confirm' ? list.find((e) => e.dataset.id === 'confirm-cancel') : null) ||
    (state.view === 'timer' ? list[1] : null) || list[0], false);
}

// The closest element in a direction from cur, among list; null when there is none. across (the
// Home menu's wide rows and rows of buttons): left and right take only what is beside cur (past
// its edge, within 45 degrees); up and down go by the gap between the edges, so a wide row goes
// up to the quick buttons over it, not past them to the slider whose centre is in line with its
// own. Something scrolled out of its own list is not a place to go from outside that list.
function nearest(cur, dir, list = items(), across = false) {
  const r = cur.getBoundingClientRect();
  const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
  const ownBox = scrollerOf(cur);
  let best = null, bestScore = Infinity, bestOff = Infinity;
  for (const el of list) {
    if (el === cur) continue;
    const q = el.getBoundingClientRect();
    const box = scrollerOf(el);
    if (box && box !== ownBox) {
      const b = box.getBoundingClientRect(), x = q.left + q.width / 2, y = q.top + q.height / 2;
      if (x < b.left || x > b.right || y < b.top || y > b.bottom) continue;
    }
    const dx = q.left + q.width / 2 - cx, dy = q.top + q.height / 2 - cy;
    let main, side;
    if (dir === 'right') { main = dx; side = dy; } else if (dir === 'left') { main = -dx; side = dy; }
    else if (dir === 'down') { main = dy; side = dx; } else { main = -dy; side = dx; }
    if (main <= 8) continue;
    const off = Math.abs(side);   // off the line through cur's middle: across, the tie-break
    if (across && (dir === 'left' || dir === 'right')) {
      if (dir === 'left' ? q.right > r.left + 8 : q.left < r.right - 8) continue;
      if (Math.abs(side) > main && (q.bottom <= r.top || q.top >= r.bottom)) continue;
    } else if (across) {
      const gap = dir === 'down' ? q.top - r.bottom : r.top - q.bottom;
      if (gap < -8) continue;
      main = Math.max(0, gap);
      side = q.right > r.left && q.left < r.right ? 0 : Math.min(Math.abs(q.left - r.right), Math.abs(r.left - q.right));
    }
    const score = main + Math.abs(side) * 2;
    if (score < bestScore || (across && score === bestScore && off < bestOff)) { bestScore = score; bestOff = off; best = el; }
  }
  return best;
}

// The box that clips el and scrolls it (a list, a pane: overflow not visible), or null.
function scrollerOf(el) {
  for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) if (getComputedStyle(p).overflowY !== 'visible') return p;
  return null;
}

// Nothing wraps round, anywhere: past the end of a row, a list or a grid the focus stays. In the
// Home menu left and right go only to what is beside the focus (nearest's across: from a wide row,
// right went up or down to a quick button); into the quick buttons from above or below, the one
// used last this session (menuUsed), else the nearest.
function move(dir) {
  const cur = focusedEl();
  if (!cur) { restoreFocus(); return; }
  let best = nearest(cur, dir, items(), state.view === 'menu');
  if (best && state.view === 'menu' && (dir === 'up' || dir === 'down') && best.classList.contains('quick') && !cur.classList.contains('quick')) {
    const used = menuUsed.quick && $('menu').querySelector(`.quicks [data-id="${menuUsed.quick}"]`);
    if (used) best = used;
  }
  if (best) setFocus(best);
}

// Scrolls box (overflow hidden: only this scrolls it) so el shows whole, with room around it
// for the focus ring; the first element takes it back to the top, the lowest to the bottom (the
// notes under it show). The stage is scaled: screen pixels are turned back into the box's own.
function scrollIntoBox(el, box, room) {
  const b = box.getBoundingClientRect(), r = el.getBoundingClientRect();
  const scale = b.height / box.offsetHeight || 1;
  const all = [...box.querySelectorAll('[data-nav]')];
  const lowest = all.reduce((low, e) => (e.getBoundingClientRect().bottom > low.getBoundingClientRect().bottom ? e : low), el);
  if (all[0] === el) box.scrollTop = 0;
  else if (lowest === el) box.scrollTop = Math.min(box.scrollHeight, box.scrollTop + (r.top - b.top) / scale - room);
  else if (r.bottom > b.bottom - room * scale) box.scrollTop += (r.bottom - b.bottom) / scale + room;
  else if (r.top < b.top + room * scale) box.scrollTop -= (b.top - r.top) / scale + room;
}

// A box that scrolls says where there is more (.more-up, .more-down: its ends fade there).
function listEdges(box) {
  box.classList.toggle('more-up', box.scrollTop > 2);
  box.classList.toggle('more-down', box.scrollTop + box.clientHeight < box.scrollHeight - 2);
}
