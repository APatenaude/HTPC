'use strict';
// Self-test: Power, moving around Settings and changing a value. Run by selftest.js, in its order.
// A pane's first and last rows whole, its bottom button above the hints: the audit.
selftestGroup(async ({ check, checkRows, asksFirst, tick, sent, lastSent, sNode, focusId }) => {
  // A section on screen, the focus on its own first pick (not on what was focused before).
  const openSection = (id) => { state.section = id; state.memory.settings = null; reset('settings'); };

  // ---- Power: Restart and Shut down ask first --------------------------------------------------------
  selftestFresh();
  for (const id of ['restart', 'shutdown']) {
    reset('home');
    go('power');
    asksFirst(`Power: ${id}`, () => $('power-cards').querySelector(`[data-id="${id}"]`), 'a', 'power', { action: id });
  }
  selftestFresh();
  await tick();

  // ---- Settings: opening, moving ------------------------------------------------------------------
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
  setFocus(sNode('s-sleep'));
  press('up');
  check('Settings: up on the first section stays there', focusId() === 's-sleep' && state.section === 'sleep', focusId());

  // ---- A slider picked: A, then left and right change it; B puts it down ---------------------------
  openSection('sound');
  const vol = state.volume, bar = () => $('settings-hints').textContent;
  setFocus(sNode('set-volume'));
  const hintsBefore = bar();
  press('a');
  const picked = sNode('set-volume').classList.contains('editing'), pickedHints = bar();
  press('left'); press('left');
  const lowered = state.volume;
  press('b');
  const putDown = [state.volume, focusId(), sNode('set-volume').classList.contains('editing')];
  press('left');
  checkRows('Settings: a slider picked with A, its hints saying so: left lowers it; B puts it down, the value kept, the focus on it; then left is the list again', [
    ['A picks it', picked], ['its hints change', pickedHints !== hintsBefore, pickedHints],
    ['left twice: 10 lower', lowered === vol - 10, `${vol} to ${lowered}`],
    ['B', putDown[0] === vol - 10 && putDown[1] === 'set-volume' && !putDown[2], putDown.join(' ')],
    ['then left', focusId() === 's-sound', focusId()],
  ]);

  // ---- A value changes only once A has picked it: moving over it changes nothing -----------------
  // [what, its section, its row, its value, a toggle (flipped by A alone; the rest: A, right, A),
  // the setting the change saves (the host's 'setting' message)]
  EXT.sections.controller.demo();
  state.tv = TvUi.demo('roku');
  const pickers = [
    ['the volume, a slider', 'sound', 'set-volume', () => state.volume],
    ['the sound output, a choice', 'sound', 'snd-output', () => (lastSent('sound.output') || {}).id],
    ['interface sounds, a choice', 'sound', 'set-interfaceSounds', () => state.prefs.interfaceSounds, false, 'interfaceSounds'],
    ['the idle minutes, a stepper', 'sleep', 'set-idleMinutes', () => state.prefs.idleMinutes, false, 'idleMinutes'],
    ['stay awake while playing, a toggle', 'sleep', 'set-stayAwakeWhilePlaying', () => state.prefs.stayAwakeWhilePlaying, true],
    ['the pointer speed, a slider', 'controller', 'set-pointerSpeed', () => prefValue('pointerSpeed', 5)],
    ['the keyboard by itself, a toggle', 'controller', 'set-showKeyboardAutomatically', () => prefValue('showKeyboardAutomatically', true), true],
    ['the TV\'s input, a choice', 'tv', 'tv-input', () => state.tv.profile.input],
  ];
  checkRows('Settings: moving over a value (right) changes nothing; picked with A (a toggle: A alone), it changes', pickers.map(([what, section, id, value, toggle, saves]) => {
    openSection(section);
    sent.length = 0;
    if (!sNode(id)) return [what, false, 'no such row'];
    setFocus(sNode(id));
    const was = value();
    press('right');
    const moved = value();
    setFocus(sNode(id));
    if (toggle) press('a'); else { press('a'); press('right'); press('a'); }
    const saved = !saves || (lastSent('setting') && lastSent('setting').key === saves);
    return [what, moved === was && value() !== was && saved, `${was}, moved over: ${moved}, picked: ${value()}${saved ? '' : `, not saved as ${saves}`}`];
  }));

  // Controller: two columns; left from the right column goes beside it, not to the list.
  openSection('controller');
  setFocus(sNode('set-pointerSpeed'));
  const lefts = ['left', 'left'].map((b) => { press(b); return focusId(); }).join(',');
  check('Controller: left from the right column goes beside it, then (nothing beside) to the list', lefts === 'pad-test,s-controller', lefts);
  // TV: buttons side by side are reached with left and right.
  openSection('tv');
  setFocus(sNode('tv-test'));
  const across = ['right', 'left', 'left'].map((b) => { press(b); return focusId(); }).join(',');
  check('TV: right from Test to Find it again, left back, left again (nothing beside) to the list', across === 'tv-refresh,tv-test,s-tv', across);
  reset('home');

  // ---- Settings: a value changing redraws in place; the sleep timer set right there ----------------
  openSection('sleep');
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
  reset('home');
});
