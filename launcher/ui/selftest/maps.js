'use strict';
// Self-test: button maps, and what the Home menu over an app says its buttons do. Run by selftest.js, in its order.
selftestGroup(({ check, focusId }) => {
  // ---- Button maps: the list, the editor's preset row and the picker ---------------------------
  mapsDemo();
  reset('home');
  go('maps');
  const mNode = (id) => $('maps').querySelector(`[data-id="${id}"]`);
  press('up');
  check('Button maps: up on the first app stays there', focusId() === 'm-youtube', focusId());
  for (let i = 0; i < 12; i++) press('down');
  const mlist = $('maps').querySelector('.mlist').getBoundingClientRect(), last = mNode('m-_other').getBoundingClientRect();
  check('Button maps: down to the last app, it stays and shows whole', focusId() === 'm-_other' && last.bottom <= mlist.bottom, `${focusId()} ${last.bottom} > ${mlist.bottom}`);
  setFocus(mNode('m-twitch'));
  press('a');
  const bNode = (id) => $('buttons').querySelector(`[data-id="${id}"]`);
  check('Editor: opens on a button, the preset row says how many differ', state.view === 'buttons' && focusId() === 'b-a' && /2 buttons changed/.test(bNode('b-preset').textContent));
  press('up'); press('up'); press('up');
  check('Editor: up goes to the preset row and stops there', focusId() === 'b-preset' && /Change preset/.test($('buttons').querySelector('.hints').textContent), focusId());
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
  check('Editor: A on a button opens its choice, with LB RB for the categories', !!$('buttons').querySelector('.bcats .key') && /LB\s*RB\s*Category/.test($('buttons').querySelector('.hints').textContent));
  const cat = () => $('buttons').querySelector('.bcat.on').textContent;
  press('rb');
  check('Editor: RB: the next category', cat() === 'Mouse', cat());
  press('lb'); press('lb');
  check('Editor: LB: the one before (round to the last)', cat() === 'Nothing', cat());
  press('b');
  check('Editor: B closes the choice, back on the button', !maps.picking && focusId() === 'b-start', focusId());
  reset('home');

  // ---- The Home menu over an app says what its buttons do ---------------------------------------
  state.tiles.find((t) => t.id === 'twitch').running = true;
  state.current = 'twitch';
  go('menu');
  const card = $('menu-app').textContent;
  check('Menu over an app: its buttons beside the panel, and how to go back', /Twitch/.test(card) && /Enter/.test(card) && /Back to Twitch/.test(card) && /This menu/.test(card), card.slice(0, 80));
  back();
  state.current = null;
  go('menu');
  check('Menu over the home screen: no app card', $('menu-app').textContent === '');
  reset('home');
});
