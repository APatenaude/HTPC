'use strict';

// The phone remote's logic that needs no page: live-typing differences and touchpad gestures.
// Kept apart from phone.js so launcher\dev\phone-test.html can test it in a plain browser.

const PhoneLogic = (() => {
  // Characters as the TV deletes them: one Backspace per grapheme (an emoji with its skin tone,
  // a flag, an accented letter). Intl.Segmenter: iOS 14.5+, Chrome 87+; else code points.
  const segmenter = typeof Intl !== 'undefined' && Intl.Segmenter ? new Intl.Segmenter(undefined, { granularity: 'grapheme' }) : null;
  function graphemes(text) {
    return segmenter ? Array.from(segmenter.segment(text), (s) => s.segment) : Array.from(text);
  }

  // What to send so the TV's field goes from `sent` to `now`: Backspaces for everything after
  // the part they share at the start, then the rest of `now`. Handles typing at the end,
  // deleting, and autocorrect swapping a word (its tail is deleted and typed again).
  function diff(sent, now) {
    const a = graphemes(sent), b = graphemes(now);
    let same = 0;
    while (same < a.length && same < b.length && a[same] === b[same]) same++;
    return { back: a.length - same, text: b.slice(same).join('') };
  }

  // Messages for a change, each within the box's 256-character limit (a long paste goes in parts).
  function typeMessages(sent, now, limit = 256) {
    const { back, text } = diff(sent, now);
    const out = [];
    let backLeft = back;
    while (backLeft > limit) { out.push({ t: 'type', back: limit, text: '' }); backLeft -= limit; }
    const parts = [];
    let part = '';
    for (const g of graphemes(text)) {
      if (part.length + g.length > limit) { parts.push(part); part = ''; }
      part += g;
    }
    if (part) parts.push(part);
    if (!parts.length) { if (backLeft) out.push({ t: 'type', back: backLeft, text: '' }); return out; }
    parts.forEach((p, i) => out.push({ t: 'type', back: i === 0 ? backLeft : 0, text: p }));
    return out;
  }

  // The touchpad: one finger moves the pointer, a tap clicks, press and hold (still) then move
  // drags, two fingers scroll, a two-finger tap right-clicks. Feed it pointer events (with
  // timestamps in ms); it collects motion (take()) and returns the discrete events.
  class Gestures {
    constructor() {
      this.pointers = new Map();   // id -> { x, y, x0, y0 }
      this.reset();
    }

    reset() {
      this.dx = 0; this.dy = 0; this.sx = 0; this.sy = 0;
      this.start = 0; this.maxPointers = 0; this.moved = false; this.dragging = false; this.holdAt = 0;
    }

    static get TAP_MS() { return 250; }
    static get TAP_SLOP() { return 10; }     // CSS px a tap may wander
    static get HOLD_MS() { return 450; }

    down(id, x, y, t) {
      if (this.pointers.size === 0) { this.reset(); this.start = t; this.holdAt = t + Gestures.HOLD_MS; }
      this.pointers.set(id, { x, y, x0: x, y0: y });
      this.maxPointers = Math.max(this.maxPointers, this.pointers.size);
      if (this.pointers.size > 1) this.holdAt = 0; // two fingers never drag
      return [];
    }

    move(id, x, y, t) {
      const p = this.pointers.get(id);
      if (!p) return [];
      const ddx = x - p.x, ddy = y - p.y;
      p.x = x; p.y = y;
      if (Math.hypot(x - p.x0, y - p.y0) > Gestures.TAP_SLOP) { this.moved = true; if (!this.dragging) this.holdAt = 0; }
      if (this.pointers.size >= 2) {
        // Two fingers: the average of their motion scrolls.
        this.sx += ddx / this.pointers.size; this.sy += ddy / this.pointers.size;
      } else if (this.maxPointers === 1) {
        this.dx += ddx; this.dy += ddy;
      }
      return [];
    }

    // Call on every frame: a finger held still long enough starts a drag.
    tick(t) {
      if (this.holdAt && t >= this.holdAt && this.pointers.size === 1 && !this.moved && !this.dragging) {
        this.holdAt = 0;
        this.dragging = true;
        return [{ t: 'drag', down: true }];
      }
      return [];
    }

    up(id, t) {
      if (!this.pointers.delete(id)) return [];
      if (this.pointers.size > 0) return [];
      const events = [];
      if (this.dragging) events.push({ t: 'drag', down: false });
      else if (!this.moved && t - this.start <= Gestures.TAP_MS * (this.maxPointers > 1 ? 1.4 : 1))
        events.push({ t: 'tap', b: this.maxPointers > 1 ? 'right' : 'left' });
      this.holdAt = 0;
      this.dragging = false;
      return events;
    }

    cancel() {
      const events = this.dragging ? [{ t: 'drag', down: false }] : [];
      this.pointers.clear();
      this.reset();
      return events;
    }

    // Motion collected since the last take(), for one message per frame.
    take() {
      const m = { dx: this.dx, dy: this.dy, sx: this.sx, sy: this.sy };
      this.dx = this.dy = this.sx = this.sy = 0;
      return m;
    }
  }

  // "12:04", "1:02:03".
  function clock(seconds) {
    if (!isFinite(seconds) || seconds < 0) seconds = 0;
    const s = Math.floor(seconds % 60), m = Math.floor(seconds / 60) % 60, h = Math.floor(seconds / 3600);
    const two = (n) => String(n).padStart(2, '0');
    return h ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`;
  }

  return { graphemes, diff, typeMessages, Gestures, clock };
})();

if (typeof module !== 'undefined') module.exports = PhoneLogic;
