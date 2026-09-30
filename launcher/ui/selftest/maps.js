'use strict';
// Self-test: button maps, and what the Home menu over an app says its buttons do. Run by selftest.js, in its order.
// The list's ends (it stops, its rows whole): the audit.
selftestGroup(({ check, focusId }) => {
  // ---- Button maps: the editor's preset row and the picker ------------------------------------------
  selftestFresh();
  mapsDemo();
  go('maps');
  const mNode = (id) => $('maps').querySelector(`[data-id="${id}"]`);
  const keys = (el) => [...el.querySelectorAll('.key')].map((k) => k.textContent);
  setFocus(mNode('m-twitch'));
  press('a');
  const bNode = (id) => $('buttons').querySelector(`[data-id="${id}"]`);
  const changed = Object.keys(mapApp('twitch').map.changes).length;
  check('Editor: opens on a button, the preset row says how many differ', state.view === 'buttons' && focusId() === 'b-a' && bNode('b-preset').textContent.includes(String(changed)),
    `${focusId()}: ${bNode('b-preset').textContent}`);
  press('up'); press('up'); press('up');
  check('Editor: up goes to the preset row and stops there', focusId() === 'b-preset', focusId());
  press('left');
  check('Editor: left on the preset row changes nothing', mapApp('twitch').map.preset === 'mouse' && focusId() === 'b-preset');
  press('a');
  check('Editor: A on the preset row opens the presets, on the current one', focusId() === 'bp-mouse');
  press('down'); press('a');
  check('Editor: another preset with buttons changed asks first', state.view === 'ask' && mapApp('twitch').map.preset === 'mouse');
  press('b'); press('b');
  check('Editor: cancelled, B closes the presets as they were', state.view === 'buttons' && focusId() === 'b-preset' && mapApp('twitch').map.preset === 'mouse', focusId());
  setFocus(bNode('b-start'));
  press('a');
  const hintKeys = keys($('buttons').querySelector('.hints'));
  check('Editor: A on a button opens its choice, with LB RB for the categories', !!$('buttons').querySelector('.bcats .key') && hintKeys.includes('LB') && hintKeys.includes('RB'), hintKeys.join(' '));
  const cats = () => [...$('buttons').querySelectorAll('.bcat')];
  const cat = () => cats().findIndex((c) => c.classList.contains('on'));
  const first = cat();
  press('rb');
  const next = cat();
  press('lb'); press('lb');
  check('Editor: RB: the next category; LB: the one before, round to the last', next === first + 1 && cat() === (first + cats().length - 1) % cats().length,
    `${first}, RB ${next}, LB LB ${cat()} of ${cats().length}`);
  press('b');
  check('Editor: B closes the choice, back on the button', !maps.picking && focusId() === 'b-start', focusId());
  reset('home');

  // ---- The Home menu over an app says what its buttons do ---------------------------------------
  state.tiles.find((t) => t.id === 'twitch').running = true;
  state.current = 'twitch';
  go('menu');
  const card = $('menu-app'), head = card.querySelector('.ma-head b');
  const rowKeys = [...card.querySelectorAll('.ma-row .key')].map((k) => k.textContent);
  check('Menu over an app: its buttons beside the panel, and how to go back', head && head.textContent === 'Twitch' && rowKeys.length > 3 && rowKeys.includes('Home')
    && keys(card.querySelector('.ma-foot')).includes('B'), `${head && head.textContent}: ${rowKeys.join(' ')}`);
  back();
  state.current = null;
  go('menu');
  check('Menu over the home screen: no app card', $('menu-app').textContent === '');
  reset('home');
});
