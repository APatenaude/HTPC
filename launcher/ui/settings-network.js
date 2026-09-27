'use strict';
// Settings › Wi-Fi (design: Settings: Wi-Fi): WifiUI (wifi.js) in a Settings section. Scans run
// while the section is on screen. Demo: index.html#settings/wifi, and other states with
// ?wifi=wifi|password|hidden|location|noadapter|off before the #.

// The network list scrolls: a row the focus moves to comes into view (app.js's setFocus, wrapped).
const setFocusBeforeWifi = setFocus;
setFocus = function (el, chosen) {   // eslint-disable-line no-global-assign
  setFocusBeforeWifi(el, chosen);
  const list = el && el.closest && el.closest('.wifi-scroll');
  if (list) { el.scrollIntoView({ block: 'nearest' }); listEdges(list); }   // its ends fade where there is more
};

function wifiChanged(focusId) {
  if (state.view !== 'settings' || state.section !== 'wifi') return;
  if (focusId) state.memory.settings = focusId;
  render();
}

settingsSection('wifi', {
  render() {
    setTimeout(() => WifiUI.afterRender(), 0);   // once the pane is in the page
    return '<header><h1>Wi-Fi</h1><p>Picking a network opens the keyboard. You can also type the password on your phone.</p></header>' +
      WifiUI.html();
  },
  press: (button, el) => WifiUI.press(button, el),
  shown() { WifiUI.start({ changed: wifiChanged, ask, toast }); },
  left() { WifiUI.stop(); },
  demo() { WifiUI.demo(new URLSearchParams(location.search).get('wifi') || 'ethernet'); },
});

hostMessage('wifi.', (msg) => WifiUI.handle(msg));
