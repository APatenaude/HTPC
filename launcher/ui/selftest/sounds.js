'use strict';
// Self-test: the interface sounds. Run by selftest.js, in its order.
selftestGroup(async ({ check, checkRows, tick, lastSent, soundsRendered, sNode, tileEl, heard }) => {
  // ---- Interface sounds (sounds.js) -------------------------------------------------------------
  // Each sound rendered offline (no audio device), at Medium: heard, never loud, short. The render
  // was started first thing (selftest.js) and is done by now.
  selftestFresh();
  const longest = { move: 0.06, select: 0.25, back: 0.25, open: 0.5, close: 0.45, bump: 0.2, on: 0.2, off: 0.2, notice: 1 };
  const rendered = await Promise.race([soundsRendered, tick().then(() => null)]);
  const level = {};
  checkRows('Sounds: each rendered offline, at Medium: heard, not loud (peak 0.03 to 0.3), short', rendered ? Object.keys(SOUNDS).map((name, i) => {
    const d = rendered.getChannelData(i);
    let peak = 0, end = 0;
    for (let s = 0; s < d.length; s++) { const a = Math.abs(d[s]); if (a > peak) peak = a; if (a > 0.001) end = s; }
    level[name] = peak;
    const ms = Math.round(end / rendered.sampleRate * 1000);
    return [name, peak > 0.03 && peak < 0.3 && ms >= 5 && ms <= longest[name] * 1000, `peak ${peak.toFixed(3)}, ${ms} ms`];
  }) : [['rendered offline', false]]);
  check('Sound: the move tick is the softest', rendered && Object.keys(level).every((n) => n === 'move' || level.move < level[n]), JSON.stringify(level));

  // What each press plays (heard: what played, no rate limit in the way).
  const played = [];
  const hears = async (what, want, act) => { const got = await heard(act); played.push([what, got === want, got || 'nothing']); };
  state.prefs.interfaceSounds = 'low';
  reset('home');
  setFocus(tileEl('youtube'));
  await hears('the focus moving: a tick', 'move', () => press('right'));
  await hears('Home on the home screen: the menu opening', 'open', () => press('home'));
  await hears('B: back', 'back', () => press('b'));
  setFocus(tileEl('youtube'));
  await hears('A on a tile: the app opening', 'open', () => press('a'));
  hideOpening();
  EXT.sections.sound.demo();
  state.section = 'sound';
  reset('settings');
  setFocus(sNode('snd-output'));
  await hears('up at the top of a list: the end, a thud', 'bump', () => press('up'));
  state.section = 'sleep';
  reset('settings');
  state.prefs.stayAwakeWhilePlaying = false;
  render();
  setFocus(sNode('set-stayAwakeWhilePlaying'));
  await hears('A on a toggle: on', 'on', () => press('a'));
  await hears('... then off', 'off', () => press('a'));
  await hears('a list\'s end held: a thud once per 350 ms', 'bump,bump', () => [5000, 5110, 5220, 5330, 5440].forEach((t) => soundsDo('bump', t)));
  onHost({ type: 'blank' });
  await hears('the Home menu coming up over an app', 'open', () => onHost({ type: 'show', view: 'menu', current: 'twitch' }));
  state.current = null;
  reset('home');
  await hears('an alert\'s card arriving', 'notice', () => noticeUpdate({ toasts: [{ id: 'snd', title: 'Test', tone: 'info' }], rows: [], pills: [] }));
  noticeUpdate({ toasts: [], rows: [], pills: [] });
  checkRows('Sounds: each press, and what the host shows, its own sound', played);
  const moves = [0, 400, 510, 620, 730, 840, 950].map((t) => soundMoveGain(t)).join(',');
  check('Sounds: a direction held: a tick, then every other repeat at half the level', moves === '1,1,0,0.5,0,0.5,0', moves);

  // None while the launcher is hidden or blank (an app in front, standby), or a field has the focus.
  const silent = [];
  const hearsNone = async (what, act) => { const got = await heard(act); silent.push([what, got === '', got]); };
  reset('home');
  onHost({ type: 'blank' });
  await hearsNone('blank (an app in front, standby)', () => press('right'));
  onHost({ type: 'show', view: 'home' });
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
  await hearsNone('the launcher hidden', () => { press('left'); soundsDo('notice'); });
  delete document.hidden;
  const typing = document.createElement('input');
  $('stage').appendChild(typing);
  typing.focus();
  await hearsNone('a text field focused', () => press('right'));
  typing.remove();
  checkRows('Sounds: none while blank, hidden, or typing', silent);

  // Settings › Sound › Interface sounds (picked with A: settings.js's table): its choices go round,
  // Off is silent.
  state.section = 'sound';
  state.memory.settings = null;
  reset('settings');
  setFocus(sNode('set-interfaceSounds'));
  press('a'); press('right'); press('right');   // Low, Medium, then round to Off
  const off = state.prefs.interfaceSounds;
  const offHeard = await heard(() => { press('a'); press('down'); });
  setFocus(sNode('set-interfaceSounds'));
  press('a'); press('left'); press('left'); press('a');
  checkRows('Interface sounds: right from Medium goes round to Off, which is silent; left twice from Off: Low, saved', [
    ['round to Off', off === 'off', off], ['Off is silent', offHeard === '', offHeard],
    ['back to Low', state.prefs.interfaceSounds === 'low' && lastSent('setting').key === 'interfaceSounds' && lastSent('setting').value === 'low', JSON.stringify(lastSent('setting'))],
  ]);
  reset('home');
  check('Sounds: headless (no host, no real key): the audio device is never opened', sounds.ctx === null);
});
