'use strict';
// Self-test: Power, moving around Settings and changing a value. Run by selftest.js, in its order.
selftestGroup(async ({ check, tick, sent, lastSent, sNode, focusId }) => {
  // ---- Power: Restart and Shut down ask first, Cancel focused ------------------------------------
  const powerCard = (id) => $('power-cards').querySelector(`[data-id="${id}"]`);
  for (const [id, title, yes] of [['restart', 'Restart the box?', 'Restart'], ['shutdown', 'Shut down the box?', 'Shut down']]) {
    reset('home');
    go('power');
    sent.length = 0;
    setFocus(powerCard(id));
    press('a');
    const asked = state.view === 'ask' && focusedEl() && focusedEl().dataset.id === 'ask-no';
    check(`Power: ${id} asks first, Cancel focused`, asked && $('ask').textContent.includes(title) && !lastSent('power'), state.view);
    press('a');
    check(`Power: ... Cancel on ${id} sends nothing`, state.view === 'power' && !lastSent('power'), state.view);
    setFocus(powerCard(id));
    press('a');
    setFocus($('ask').querySelector('[data-id="ask-yes"]'));
    press('a');
    check(`Power: ... ${yes} sends it`, lastSent('power') && lastSent('power').action === id, JSON.stringify(sent));
  }
  reset('home');
  await tick();
  // ---- Settings: opening, moving, changing a value ----------------------------------------------
  EXT.sections.sound.demo();
  state.section = 'sound';
  state.memory.settings = 'set-volume';
  reset('home');
  activate({ dataset: { act: 'settings' } });                 // the Settings button
  check('Settings opens on the section list, on the last section', focusId() === 's-sound', focusId());
  press('right');
  check('Settings: right from the list goes into the section', focusId() === 'snd-output', focusId());
  press('up');
  check('Settings: up on the first row stays there', focusId() === 'snd-output', focusId());
  setFocus(sNode('snd-test'));
  press('down');
  check('Settings: down on the last row stays there', focusId() === 'snd-test', focusId());
  const vol = state.volume;
  setFocus(sNode('set-volume'));
  press('right');
  check('Settings: right on a slider not picked changes nothing', state.volume === vol && focusId() === 'set-volume', `${state.volume} ${focusId()}`);
  press('left');
  check('Settings: left on a slider not picked goes back to the list, unchanged', state.volume === vol && focusId() === 's-sound', `${state.volume} ${focusId()}`);
  setFocus(sNode('set-volume'));
  press('a');
  check('Settings: A picks the slider', sNode('set-volume').classList.contains('editing') && /Done/.test($('settings-hints').textContent));
  press('left'); press('left');
  check('Settings: picked, left lowers the volume', state.volume === vol - 10 && focusId() === 'set-volume', `${state.volume} ${focusId()}`);
  press('b');
  check('Settings: B puts the slider down, the value kept, the focus stays', state.volume === vol - 10 && focusId() === 'set-volume' && !sNode('set-volume').classList.contains('editing'));
  press('left');
  check('Settings: ... then left goes to the list again', focusId() === 's-sound', focusId());
  setFocus(sNode('snd-output'));
  sent.length = 0;
  press('right');
  check('Settings: the output is not switched by moving over it', !lastSent('sound.output'));
  press('a'); press('right');
  check('Settings: picked with A, right switches the output', lastSent('sound.output') && lastSent('sound.output').id === '2');
  press('a');
  press('up');
  check('Settings: up on the first row of a section stays in it', focusId() === 'snd-output', focusId());
  setFocus(sNode('s-sleep'));
  press('up');
  check('Settings: up on the first section stays there', focusId() === 's-sleep' && state.section === 'sleep', focusId());

  // Sleep & power: a stepper waits for A; the toggle flips with A only.
  setFocus(sNode('set-idleMinutes'));
  const idle = state.prefs.idleMinutes;
  press('right');
  check('Sleep: right on a stepper not picked changes nothing', state.prefs.idleMinutes === idle, String(state.prefs.idleMinutes));
  setFocus(sNode('set-idleMinutes'));
  press('a'); press('right');
  check('Sleep: A then right steps the stepper', state.prefs.idleMinutes !== idle && lastSent('setting').key === 'idleMinutes');
  press('a');
  const awake = state.prefs.stayAwakeWhilePlaying;
  setFocus(sNode('set-stayAwakeWhilePlaying'));
  press('right');
  check('Sleep: right on a toggle does not flip it', state.prefs.stayAwakeWhilePlaying === awake);
  setFocus(sNode('set-stayAwakeWhilePlaying'));
  press('a');
  check('Sleep: A flips the toggle', state.prefs.stayAwakeWhilePlaying === !awake);

  // Controller: two columns; left from the right column goes beside it, not to the list.
  EXT.sections.controller.demo();
  setFocus(sNode('s-controller'));
  const speed = prefValue('pointerSpeed', 5), kbd = prefValue('showKeyboardAutomatically', true);
  setFocus(sNode('set-pointerSpeed'));
  press('left');
  check('Controller: left on a speed not picked goes beside it, unchanged', prefValue('pointerSpeed', 5) === speed && focusId() === 'pad-test', focusId());
  press('left');
  check('Controller: ... left again, nothing beside: the list', focusId() === 's-controller', focusId());
  setFocus(sNode('set-showKeyboardAutomatically'));
  press('right');
  check('Controller: right on a toggle does not flip it', prefValue('showKeyboardAutomatically', true) === kbd);

  // TV: buttons side by side are reached with left and right.
  state.tv = TvUi.demo('roku');
  setFocus(sNode('s-tv'));
  setFocus(sNode('tv-test'));
  press('right');
  check('TV: right from Test goes to Find it again', focusId() === 'tv-refresh', focusId());
  press('left');
  check('TV: left comes back to Test', focusId() === 'tv-test', focusId());
  press('left');
  check('TV: left from Test (nothing beside it) goes to the list', focusId() === 's-tv', focusId());
  setFocus(sNode('tv-test'));
  const main = $('settings').querySelector('.spane main');
  const ring = sNode('tv-test').getBoundingClientRect(), paneBox = main.getBoundingClientRect();
  check('TV: the focused bottom button shows whole above the hints', ring.bottom + 4 <= paneBox.bottom, `${ring.bottom} > ${paneBox.bottom}`);
  setFocus(sNode('tv-input'));
  press('right');
  check('TV: the input is not changed by moving over it', state.tv.profile.input === 1);
  reset('home');

  // ---- Settings: a value changing redraws in place; the sleep timer set right there ----------------
  state.section = 'sleep';
  reset('settings');
  const navSleep = sNode('s-sleep');
  press('down');
  check('Settings: moving through the sections keeps the list (its ring not drawn again)', sNode('s-sleep') === navSleep && sNode('s-tv').classList.contains('focused') && state.section === 'tv');
  press('up');
  setFocus(sNode('set-idleMinutes'));
  const idleRow = sNode('set-idleMinutes'), timerRow = sNode('set-timer');
  press('a'); press('right');
  check('Settings: a stepper changing keeps its row (the ring is not drawn again)', sNode('set-idleMinutes') === idleRow && idleRow.classList.contains('focused') && idleRow.classList.contains('editing'));
  press('a');
  setFocus(sNode('set-stayAwakeWhilePlaying'));
  const awakeRow = sNode('set-stayAwakeWhilePlaying');
  press('a');
  check('Settings: a toggle flipping keeps its row', sNode('set-stayAwakeWhilePlaying') === awakeRow && awakeRow.classList.contains('focused'));
  state.timer = null;
  setFocus(timerRow);
  press('a');
  check('Sleep timer: A picks the row, it does not open the timer screen', state.view === 'settings' && timerRow.classList.contains('editing'));
  press('right');
  check('Sleep timer: right sets 15 min, right there', state.view === 'settings' && state.timer && state.timer.label === '15 min' && lastSent('timer').minutes === 15 && sNode('set-timer') === timerRow);
  press('right');
  check('Sleep timer: right again, 30 min', state.timer && state.timer.label === '30 min' && lastSent('timer').minutes === 30);
  press('left'); press('left');
  check('Sleep timer: left back to Off', state.timer === null && lastSent('timer').minutes === 0);
  press('left');
  check('Sleep timer: left from Off goes round to This video ends', state.timer && state.timer.endsAt === 'video');
  press('right');
  press('a');
  // A list that scrolls itself keeps the focus ring of its last row whole.
  WifiUI.demo('wifi');
  setFocus(sNode('s-wifi'));
  const rows = [...$('settings').querySelectorAll('.wifi-scroll [data-nav]')];
  setFocus(rows[rows.length - 1]);
  const wl = $('settings').querySelector('.wifi-scroll').getBoundingClientRect(), wr = rows[rows.length - 1].getBoundingClientRect();
  check('Wi-Fi: the last row of the list shows whole, its ring too', wr.bottom + 4 <= wl.bottom && wr.top >= wl.top, `${wr.bottom} / ${wl.bottom}`);
  setFocus(rows[0]);
  const w0 = rows[0].getBoundingClientRect(), wl0 = $('settings').querySelector('.wifi-scroll').getBoundingClientRect();
  check('Wi-Fi: back on the first row, its ring shows whole', w0.top - 4 >= wl0.top, `${w0.top} / ${wl0.top}`);
  reset('home');
});
