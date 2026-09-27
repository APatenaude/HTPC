'use strict';
// "Add the remote to your phone" (design: First-run setup, the phone step): a card on the home
// screen instead of a setup step, since the remote only runs once the box is set up. Two tiles
// wide at the end of the tiles, so nothing moves. It shows while the phone remote is up
// (state.phone from the host: { url, paired, pairingOpen }) and no phone has paired yet, until
// "Not now". Loaded after app.js and qr.js (index.html).

(() => {
  const HIDDEN = 'phoneCardHidden';
  let hidden = false;
  try { hidden = localStorage.getItem(HIDDEN) === '1'; } catch (e) { /* no storage: shows again next time */ }
  let shownUrl = null;
  let qr = '';

  function wanted() {
    const p = state.phone;
    return !hidden && p && p.url && !p.paired && typeof qrSvg === 'function';
  }

  function draw() {
    const tiles = $('tiles');
    let card = $('phone-card');
    if (!wanted()) { if (card) card.remove(); shownUrl = null; return; }
    // renderTiles rewrites the tiles only when they change: the card is added again then.
    if (card && card.parentNode === tiles && state.phone.url === shownUrl) return;
    if (state.phone.url !== shownUrl) { qr = qrSvg(state.phone.url, 176); shownUrl = state.phone.url; }
    if (!card) { card = document.createElement('div'); card.id = 'phone-card'; }
    card.innerHTML =
      `<div class="pc-qr">${qr}</div>` +
      '<div class="pc-text"><b>Add the remote to your phone</b>' +
        '<span>Point your phone’s camera at the code, then add it to your home screen ' +
        '(iPhone: Share › Add to Home Screen; Android: ⋮ › Install app).</span>' +
        '<span class="pc-wait"><span></span>Waiting for your phone</span>' +
        '<div class="pc-btn" data-nav data-id="phone-card-hide" data-act="phone-card-hide">Not now</div></div>';
    tiles.appendChild(card);
  }

  // Demo in a plain browser: index.html#home?phonecard=1.
  if (!(window.chrome && window.chrome.webview) && /[?&]phonecard=1/.test(location.hash)) {
    hidden = false;
    state.phone = { url: 'https://tv.local/', paired: false, pairingOpen: false };
  }

  onHome(draw);
  onAction('phone-card-hide', () => {
    hidden = true;
    try { localStorage.setItem(HIDDEN, '1'); } catch (e) { /* shows again next time */ }
    render();
  });
})();
