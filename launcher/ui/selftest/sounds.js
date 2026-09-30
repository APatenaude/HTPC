'use strict';
// Self-test: the interface sounds. Run by selftest.js, in its order.
selftestGroup(async ({ check, tick, sent, lastSent, soundsRendered, sNode, tileEl, heard }) => {
  // ---- Interface sounds (sounds.js) -------------------------------------------------------------
  // Each sound rendered offline (no audio device), at Medium: heard, never loud, short. The render
  // was started first thing (selftest.js) and is done by now.
  const longest = { move: 0.06, select: 0.25, back: 0.25, open: 0.5, close: 0.45, bump: 0.2, on: 0.2, off: 0.2, notice: 1 };
  const rendered = await Promise.race([soundsRendered, tick().then(() => null)]);
  check('Sounds: rendered offline', rendered);
  const level = {};
  if (rendered) Object.keys(SOUNDS).forEach((name, i) => {
    const d = rendered.getChannelData(i);
    let peak = 0, end = 0;
    for (let s = 0; s < d.length; s++) { const a = Math.abs(d[s]); if (a > peak) peak = a; if (a > 0.001) end = s; }
    level[name] = peak;
    const ms = Math.round(end / rendered.sampleRate * 1000), db = (20 * Math.log10(peak)).toFixed(0);
    check(`Sound "${name}": ${ms} ms, peak ${db} dBFS`, peak > 0.03 && peak < 0.3 && ms >= 5 && ms <= longest[name] * 1000, `peak ${peak}, ${ms} ms`);
  });
  check('Sound: the move tick is the softest', rendered && Object.keys(level).every((n) => n === 'move' || level.move < level[n]), JSON.stringify(level));

  state.prefs.interfaceSounds = 'low';
  reset('home');
  setFocus(tileEl('youtube'));
  check('Sounds: the focus moving ticks', await heard(() => press('right')) === 'move');
  check('Sounds: Home on the home screen: the menu opens', await heard(() => press('home')) === 'open');
  check('Sounds: B: back', await heard(() => press('b')) === 'back');
  setFocus(tileEl('youtube'));
  check('Sounds: A on a tile: the app opening', await heard(() => press('a')) === 'open');
  hideOpening();
  EXT.sections.sound.demo();
  state.section = 'sound';
  reset('settings');
  setFocus(sNode('snd-output'));
  check('Sounds: up at the top of a list: the end, a thud', await heard(() => press('up')) === 'bump');
  state.section = 'sleep';
  reset('settings');
  state.prefs.stayAwakeWhilePlaying = false;
  render();
  setFocus(sNode('set-stayAwakeWhilePlaying'));
  check('Sounds: A on a toggle: on, then off', await heard(() => press('a')) === 'on' && await heard(() => press('a')) === 'off');
  const moves = [0, 400, 510, 620, 730, 840, 950].map((t) => soundMoveGain(t)).join(',');
  check('Sounds: a direction held: a tick, then every other repeat at half the level', moves === '1,1,0,0.5,0,0.5,0', moves);
  check('Sounds: a list\'s end held: a thud once per 350 ms', await heard(() => [5000, 5110, 5220, 5330, 5440].forEach((t) => soundsDo('bump', t))) === 'bump,bump');

  // None while the launcher is hidden or blank (an app in front, standby), or a field has the focus.
  reset('home');
  onHost({ type: 'blank' });
  check('Sounds: none while blank (an app in front, standby)', await heard(() => press('right')) === '');
  onHost({ type: 'show', view: 'home' });
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
  const whileHidden = await heard(() => { press('left'); soundsDo('notice'); });
  delete document.hidden;
  check('Sounds: none while the launcher is hidden', whileHidden === '', whileHidden);
  const typing = document.createElement('input');
  $('stage').appendChild(typing);
  typing.focus();
  check('Sounds: none while a text field has the focus', await heard(() => press('right')) === '');
  typing.remove();
  onHost({ type: 'blank' });
  check('Sounds: the Home menu coming up over an app', await heard(() => onHost({ type: 'show', view: 'menu', current: 'twitch' })) === 'open');
  state.current = null;
  reset('home');
  check('Sounds: an alert\'s card arriving', await heard(() => noticeUpdate({ toasts: [{ id: 'snd', title: 'Test', tone: 'info' }], rows: [], pills: [] })) === 'notice');
  noticeUpdate({ toasts: [], rows: [], pills: [] });

  // Settings › Sound › Interface sounds: a choice, changed only once A has picked it.
  state.section = 'sound';
  reset('settings');
  const soundsOn = () => sNode('set-interfaceSounds').querySelector('.seg .on').textContent;
  check('Interface sounds: a row in Settings › Sound, on Low', sNode('set-interfaceSounds') && soundsOn() === 'Low');
  setFocus(sNode('set-interfaceSounds'));
  sent.length = 0;
  press('right');
  check('Interface sounds: right on the row not picked changes nothing', state.prefs.interfaceSounds === 'low' && !lastSent('setting'));
  setFocus(sNode('set-interfaceSounds'));
  press('a'); press('right');
  check('Interface sounds: A then right: Medium, saved as the setting', soundsOn() === 'Medium' && lastSent('setting').key === 'interfaceSounds' && lastSent('setting').value === 'medium');
  press('right');
  check('Interface sounds: right again, round to Off', state.prefs.interfaceSounds === 'off' && soundsOn() === 'Off');
  check('Interface sounds: Off is silent', await heard(() => { press('a'); press('down'); }) === '');
  setFocus(sNode('set-interfaceSounds'));
  press('a'); press('left'); press('left'); press('a');
  check('Interface sounds: left twice from Off, back to Low', state.prefs.interfaceSounds === 'low' && lastSent('setting').value === 'low');
  reset('home');
  check('Sounds: headless (no host, no real key): the audio device is never opened', sounds.ctx === null);
});
