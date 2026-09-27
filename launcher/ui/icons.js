// Line icons from the design canvas (24x24, stroked). Tiles use each app's glyph and colour
// while it has no logo of its own (appIcon in app.js).
const ICONS = {
  play: 'M8 5.5v13l11-6.5z',
  youtube: 'M3 8a3 3 0 0 1 3-3h12a3 3 0 0 1 3 3v8a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3zM10 9.5v5l4.5-2.5z',
  chat: 'M4 4h16v11h-6l-4 4v-4H4zM10 8v3M15 8v3',
  film: 'M4 4h16v16H4zM8 4v16M16 4v16M4 9h4M4 15h4M16 9h4M16 15h4',
  library: 'M4 8h16v12H4zM7 5h10M10 11v6l5-3z',
  moon: 'M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z',
  globe: 'M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM3 12h18M12 3c2.5 2.6 3.8 5.6 3.8 9s-1.3 6.4-3.8 9M12 3c-2.5 2.6-3.8 5.6-3.8 9s1.3 6.4 3.8 9',
  plus: 'M12 5v14M5 12h14',
  sliders: 'M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1M15 4v4M9 10v4M17 16v4',
  power: 'M12 3v8M6.3 6.3a8 8 0 1 0 11.4 0',
  restart: 'M4 12a8 8 0 1 0 2.4-5.7M4 4v4h4',
  desktop: 'M3 5h18v11H3zM8 20h8M12 16v4',
  tv: 'M3 5h18v12H3zM8 21h8',
  speaker: 'M4 9h4l5-4v14l-5-4H4zM16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12',
  download: 'M12 4v11M7 10l5 5 5-5M5 20h14',
  info: 'M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM12 11v6M12 7.5h0',
  controller: 'M6 8h12a4 4 0 0 1 4 4v2a3 3 0 0 1-5.4 1.8L15 14H9l-1.6 1.8A3 3 0 0 1 2 14v-2a4 4 0 0 1 4-4zM7 10.5v3M5.5 12h3M15.5 11.5h0M17.5 13h0',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  cursor: 'M5 3l14 8-6 1.5L10 19z',
  headphones: 'M4 15v-3a8 8 0 0 1 16 0v3M4 15h3v5H4zM17 15h3v5h-3z',
  warn: 'M12 3l10 18H2zM12 10v5M12 18h0',
  close: 'M6 6l12 12M18 6L6 18',
  home: 'M3 11l9-8 9 8M5 9.5V21h14V9.5M10 21v-6h4v6',
  app: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
  music: 'M9 18V5l11-2v13M9 18a3 3 0 1 1-6 0a3 3 0 1 1 6 0zM20 16a3 3 0 1 1-6 0a3 3 0 1 1 6 0z',
  sun: 'M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8zM12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4',
  timer: 'M12 8a7 7 0 1 0 0 14a7 7 0 1 0 0-14zM12 12v3.5l2.5 1.5M10 3h4M12 3v5',
  wifi: 'M2 9a15 15 0 0 1 20 0M5 12.5a10 10 0 0 1 14 0M8.5 16a5 5 0 0 1 7 0M12 19.5h0',
  bluetooth: 'M7 7l10 10-5 4V3l5 4L7 17',
  phone: 'M7 2h10v20H7zM11 18.5h2',
  share: 'M12 3v12M8 7l4-4 4 4M5 11v9h14v-9',
  chevleft: 'M15 5l-7 7 7 7',
  chevright: 'M9 5l7 7-7 7',
  move: 'M12 3v18M3 12h18M9 6l3-3 3 3M9 18l3 3 3-3M6 9l-3 3 3 3M18 9l3 3-3 3',
  pencil: 'M4 20h4L19 9l-4-4L4 16zM13 7l4 4',
  image: 'M3 5h18v14H3zM3 16l5-5 4 4 3-3 6 6M15.5 9h0',
  trash: 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13',
  search: 'M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14zM16 16l4.5 4.5',
  backspace: 'M9 5h12v14H9l-6-7zM12 9l6 6M18 9l-6 6',
  shift: 'M12 4l8 8h-4v8H8v-8H4z',
  enter: 'M20 5v7a3 3 0 0 1-3 3H5M9 11l-4 4 4 4',
  keyboard: 'M2 6h20v12H2zM6 10h0M10 10h0M14 10h0M18 10h0M7 14h10',
  pencil: 'M4 20h4L19.5 8.5a2.1 2.1 0 0 0-4-4L4 16zM13.5 6.5l4 4'
};

function icon(name, size, weight) {
  const d = ICONS[name] || ICONS.play;
  return `<svg class="icon" width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" ` +
    `stroke-width="${weight || 1.75}" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${d}"/></svg>`;
}
