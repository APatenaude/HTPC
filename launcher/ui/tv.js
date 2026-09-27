'use strict';
// TV control screens (SPEC N7), shared by first-run setup (setup.js) and Settings › TV (app.js):
// the TVs found, "How should the box control this TV?" (TVs found, brands, no control), a brand's
// checklist, the input the box is on, and the Settings section itself (design: Settings: TV,
// How the box controls a TV).
//   From the host: {type:'tv.state', tv} {type:'tv.read', power, input} {type:'tv.open'}
//   To the host:   tv.refresh, tv.choose {id}, tv.none, tv.input {input}, tv.option {key, value},
//                  tv.forget {key}, tv.showing {on}, tv.read, tv.test
// Loaded before setup.js in setup.html, after app.js in index.html (where it registers the
// Settings section and the dialog). Uses esc(), icon() and send() from those, when called.

const TvUi = {
  /** "on · HDMI 1", "on · its home screen", "off" or "" (not told). */
  stateText(t) {
    if (t.power === 'on' || t.on) return t.input > 0 ? `on · HDMI ${t.input}` : 'on · not on an HDMI input';
    return t.power === 'off' ? 'off' : '';
  },

  /** A found TV's second line: model, method, state. */
  line(t) {
    return [t.model, t.label + (t.beta ? ' (beta)' : ''), t.locked ? 'control is off on the TV' : TvUi.stateText(t)].filter(Boolean).join(' · ');
  },

  method(tv, id) { return (tv.methods || []).find((m) => m.id === id) || null; },

  /** A method's "turn these on" items, with what the box can tell about each (ok, warn, or unknown). */
  checklist(tv, methodId) {
    const m = TvUi.method(tv, methodId);
    if (!m) return [];
    const found = (tv.found || []).filter((t) => t.method === methodId);
    const picked = found.find((t) => t.picked) || found[0];
    return m.checklist.map((c, i) => ({
      name: c.name, where: c.where,
      // Roku: the first item is "Control by mobile apps", which the TV reports (locked or not).
      state: methodId === 'roku' && i === 0 && picked ? (picked.locked ? 'warn' : 'ok') : 'unknown',
    }));
  },

  checklistHtml(tv, methodId, cls) {
    const m = TvUi.method(tv, methodId);
    if (!m) return '';
    const items = TvUi.checklist(tv, methodId).map((c) =>
      `<div class="tv-check"><span class="${c.state === 'ok' ? 'ok' : 'warn'}">${icon(c.state === 'ok' ? 'check' : 'warn', 30, c.state === 'ok' ? 2.5 : 2)}</span>` +
      `<div><b>${esc(c.name)}</b><span>${esc(c.where)}</span></div></div>`).join('');
    const brand = m.brand || m.label;
    const article = /^[AEIOU]/i.test(brand) || brand === 'LG' ? 'an' : 'a';
    return `<div class="${cls || 'tv-side'}"><h2>For ${article} ${esc(brand)} TV${m.beta ? ' (beta)' : ''}, turn these on</h2>${items}` +
      (m.beta ? '<span class="tv-beta-note">Beta: made without this brand’s TV at hand. If something does not work, pick “No TV control” and use the TV’s remote.</span>' : '') +
      '</div>';
  },

  /** Rows for the TVs found (data-id "tvpick:<id>"). */
  foundRows(tv, rowClass) {
    return (tv.found || []).map((t) =>
      `<div class="${rowClass}${t.picked ? ' picked' : ''}" data-nav data-id="tvpick:${esc(t.id)}" data-act="tv-pick" data-arg="${esc(t.id)}">${icon('tv', 40)}` +
        `<div class="tv-lines"><b>${esc(t.name)}</b><small>${esc(TvUi.line(t))}</small></div>` +
        (t.detected ? '<span class="tv-tag">Detected</span>' : '') +
        (t.picked ? `<span class="tv-pickmark">${icon('check', 36, 2.5)}</span>` : '') + '</div>').join('');
  },

  /** The brands on offer (beta ones only when one of theirs was found) and "No TV control". */
  methodRows(tv, rowClass, hint) {
    const methods = (tv.methods || []).map((m) =>
      `<div class="${rowClass}${hint === m.id ? ' picked' : ''}" data-nav data-id="tvbrand:${m.id}" data-act="tv-brand" data-arg="${m.id}">${icon('wifi', 34)}` +
        `<div class="tv-lines"><b>${esc(m.label)}${m.beta ? ' <span class="tv-beta">beta</span>' : ''}</b><small>${esc(m.how)}</small></div>` +
        (m.detected ? '<span class="tv-tag">Detected</span>' : '') + '</div>').join('');
    const none = tv.profile && tv.profile.method === 'none';
    return methods +
      `<div class="${rowClass}${none ? ' picked' : ''}" data-nav data-id="tvbrand:none" data-act="tv-none">${icon('close', 34)}` +
        '<div class="tv-lines"><b>No TV control</b><small>Use the TV’s own remote for power and input.</small></div></div>';
  },

  /**
   * What the input step says about the TV's own report (TVs that tell their input): matches the
   * EDID's input, or the wrong-TV alarm. read: {power, input} from 'tv.read', or null.
   */
  inputStatus(tv, read) {
    const p = tv.profile;
    if (!p || !tv.caps || !tv.caps.readInput || !read) return null;
    const want = p.input || tv.port;
    if (read.power === 'on' && read.input > 0 && read.input === want) return { kind: 'ok', text: `The TV says it shows HDMI ${read.input}. That is this box.` };
    if (read.power === 'on') {
      const what = read.input > 0 ? `HDMI ${read.input}` : 'something else (not an HDMI input)';
      return { kind: 'alarm', text: `${p.name} says it shows ${what}, but this box is on HDMI ${want}. Is it the right TV? Pick again, or check the cable.` };
    }
    if (read.power === 'off') return { kind: 'alarm', text: `${p.name} says it is off, yet you are looking at this box. Is it the right TV?` };
    return { kind: 'info', text: 'The TV did not answer.' };
  },

  /** Where the input number comes from, for the input step and Settings. */
  portNote(tv) {
    return tv.port > 0 ? `The TV told the box it is on HDMI ${tv.port} (through the HDMI cable).` : 'The TV did not say which input the box is on: pick it.';
  },

  /** The code typed so far on the pairing keypad (Google TV). */
  code: '',

  /**
   * Pairing (LG: say yes on the TV; Google TV: type the code the TV shows), as a panel: every
   * control has data-id (setup) and data-act (Settings). Null when there is nothing to show.
   */
  pairHtml(tv) {
    const pr = tv.pairing;
    const unpaired = tv.status === 'unpaired' && tv.profile;
    if (!pr && !unpaired) return '';
    const btn = (id, act, label, arg) => `<div class="tv-pbtn" data-nav data-id="${id}" data-act="${act}"${arg ? ` data-arg="${arg}"` : ''}>${esc(label)}</div>`;
    const name = esc((pr && pr.name) || (tv.profile && tv.profile.name) || 'the TV');
    if (!pr || (pr.stage === 'done' && unpaired)) {
      return `<div class="tv-pair"><h2>Pair ${name}</h2><p>The box needs the TV’s OK once before it can turn it on and off.</p>` +
        `<div class="tv-pbuttons">${btn('tvpair-start', 'tv-pair', 'Pair now')}</div></div>`;
    }
    if (pr.stage === 'done') return `<div class="tv-pair done"><h2>${icon('check', 34, 2.5)} ${name} is paired</h2></div>`;
    if (pr.stage === 'failed') {
      return `<div class="tv-pair failed"><h2>Not paired</h2><p>${esc(pr.message)}</p>` +
        `<div class="tv-pbuttons">${btn('tvpair-start', 'tv-pair', 'Try again')}</div></div>`;
    }
    if (pr.stage === 'code') {
      const n = pr.codeLength || 6;
      const boxes = Array.from({ length: n }, (_, i) => `<span class="tv-cbox${i === TvUi.code.length ? ' now' : ''}">${esc(TvUi.code[i] || '')}</span>`).join('');
      const keys = '0123456789ABCDEF'.split('').map((k) => btn(`tvkey:${k}`, 'tv-key', k, k)).join('');
      return `<div class="tv-pair"><h2>Type the code on ${name}</h2><p>${esc(pr.message)}</p>` +
        `<div class="tv-code">${boxes}</div><div class="tv-keys">${keys}</div>` +
        `<div class="tv-pbuttons">${btn('tvkey:del', 'tv-key', 'Delete', 'del')}${btn('tvpair-ok', 'tv-code', 'OK')}${btn('tvpair-cancel', 'tv-cancel-pair', 'Cancel')}</div></div>`;
    }
    // working, prompt
    return `<div class="tv-pair"><h2>${pr.stage === 'prompt' ? 'Say yes on the TV' : 'Pairing'}</h2>` +
      `<p><span class="tv-spin"></span>${esc(pr.message)}</p>` +
      `<div class="tv-pbuttons">${btn('tvpair-cancel', 'tv-cancel-pair', 'Cancel')}</div></div>`;
  },

  /** A pairing control pressed (setup and Settings share this); true when it was one. */
  pairAction(id) {
    const pr = state.tv && state.tv.pairing; // "state": setup.js's or app.js's, whichever page this is
    if (id === 'tvpair-start') { TvUi.code = ''; send({ type: 'tv.pair' }); return true; }
    if (id === 'tvpair-cancel') { TvUi.code = ''; send({ type: 'tv.cancelPair' }); return true; }
    if (id === 'tvpair-ok') {
      if (pr && TvUi.code.length === (pr.codeLength || 6)) { send({ type: 'tv.code', code: TvUi.code }); TvUi.code = ''; }
      return true;
    }
    if (id.startsWith('tvkey:')) {
      const k = id.slice(6);
      if (k === 'del') TvUi.code = TvUi.code.slice(0, -1);
      else if (TvUi.code.length < ((pr && pr.codeLength) || 6)) TvUi.code += k;
      return true;
    }
    return false;
  },

  statusLine(tv) {
    const p = tv.profile;
    const s = tv.status;
    if (tv.handsOff) return { kind: 'warn', text: 'Hands off (--no-tv): the TV is watched but sent nothing.' };
    if (s === 'ok') return { kind: 'ok', text: 'Connected. Found by name, so a move or a new network address is fine.' };
    if (s === 'locked') return { kind: 'warn', text: 'This TV blocks control: on the TV, Settings › System › Advanced system settings › Control by mobile apps, set Network access to Enabled.' };
    if (s === 'missing') return { kind: 'warn', text: `${p.name} is not answering on the network. Is it plugged in and connected? Find it again below.` };
    if (s === 'paused') return { kind: 'warn', text: `Paused: ${p.paused}. Pick your TV again to go on.` };
    if (s === 'none') return { kind: 'info', text: 'The box leaves this TV to its own remote.' };
    if (s === 'unavailable') return { kind: 'warn', text: 'This control method is turned off on this box.' };
    if (s === 'unpaired') return { kind: 'warn', text: `${p.name} is not paired yet: the box cannot control it until it is.` };
    return { kind: 'info', text: 'Which TV is this box plugged into? Pick it under “How the box controls it”.' };
  },

  /** Sample TV states for screenshots (setup.html#tv?demo=..., index.html#settings/tv?demo=...). */
  demo(kind) {
    const methods = [
      { id: 'roku', label: 'Roku TV', brand: 'Roku', beta: false, how: 'Over your network. On the TV, turn on Control by mobile apps and Fast TV start.',
        checklist: [{ name: 'Control by mobile apps', where: 'Settings › System › Advanced system settings › Control by mobile apps: set Network access to Enabled' },
          { name: 'Fast TV start', where: 'Settings › System › Power › Fast TV start: On. Needed to turn the TV on over the network.' }], detected: true },
    ];
    const lg = { id: 'webos', label: 'LG (webOS)', brand: 'LG', beta: true, how: 'Over your network. Say yes to the prompt on the TV once.',
      checklist: [{ name: 'LG Connect Apps', where: 'Settings › General › Devices › External devices settings (or Network): On' },
        { name: 'Turn on via Wi-Fi', where: 'Settings › General › Devices › TV management › Turn on via Wi-Fi (Wake on LAN): On' }], detected: true };
    const roku = { id: 'roku:X00000000001', method: 'roku', label: 'Roku TV', beta: false, name: 'Living room tv', model: '65S41-CA', locked: false, on: true, power: 'on', input: 1, detected: true, picked: true };
    const profile = { method: 'roku', methodLabel: 'Roku TV', beta: false, deviceId: 'X00000000001', name: 'Living room tv', model: '65S41-CA', input: 1,
      offWithBox: true, onWithBox: true, sleepWithTv: true, paused: null };
    const caps = { off: true, follow: true, input: true, readInput: true, test: true };
    const profiles = [{ key: 'TCL-0000-00000000-65S41CA', name: 'Living room tv', method: 'roku', methodLabel: 'Roku TV', input: 1, current: true, lastUsed: '2026-09-27' },
      { key: 'GSM-C001-00000000-LG TV SSCR2', name: 'Bedroom TV', method: 'webos', methodLabel: 'LG (webOS)', input: 2, current: false, lastUsed: '2026-09-20' }];
    const base = { screen: 'TCL 65S41CA', screenKey: 'TCL-0000-00000000-65S41CA', port: 1, handsOff: false, status: 'ok', profile, caps, found: [roku], methods, profiles };
    switch (kind) {
      case 'none-found': return { ...base, status: 'unbound', profile: null, found: [], profiles: [], methods: methods.map((m) => ({ ...m, detected: false })) };
      case 'locked': return { ...base, status: 'locked', found: [{ ...roku, locked: true }] };
      case 'twins': return { ...base, status: 'unbound', profile: null, profiles: [],
        found: [{ ...roku, picked: false, detected: false }, { ...roku, id: 'roku:X00000000002', name: 'Bedroom TV', input: 3, picked: false, detected: false }] };
      case 'unbound': return { ...base, status: 'unbound', profile: null, profiles: [], found: [{ ...roku, picked: false }] };
      case 'lg': return { ...base, screen: 'LG TV SSCR2', status: 'unbound', profile: null, profiles: [], methods: [methods[0], lg].map((m) => ({ ...m, detected: m.id === 'webos' })),
        found: [{ id: 'webos:1a2b', method: 'webos', label: 'LG (webOS)', beta: true, name: 'LG OLED65C4', model: 'OLED65C4PUA', locked: false, on: true, power: 'on', input: 1, detected: true, picked: false }] };
      case 'paused': return { ...base, status: 'paused', caps: { ...caps, test: false }, profile: { ...profile, paused: 'Living room tv says it shows HDMI 3, not the box (HDMI 1)' } };
      case 'none': return { ...base, status: 'none', profile: { ...profile, method: 'none', methodLabel: 'No TV control', deviceId: '' }, caps: { off: false, follow: false, input: false, readInput: false, test: false } };
      case 'missing': return { ...base, status: 'missing', caps: { ...caps, test: false }, found: [{ ...roku, on: false, power: 'unknown' }] };
      case 'pair-prompt': case 'pair-failed': case 'pair-unpaired': {
        const lgTv = { id: 'webos:1a2b', method: 'webos', label: 'LG (webOS)', beta: true, name: 'LG OLED65C4', model: 'OLED65C4PUA', locked: false, on: false, power: 'unknown', input: 0, detected: false, picked: true, paired: false };
        const stage = kind === 'pair-prompt' ? { stage: 'prompt', message: 'Say yes on LG OLED65C4: a prompt asks to allow “TV Box”.' }
          : kind === 'pair-failed' ? { stage: 'failed', message: 'LG OLED65C4 did not accept the box. Check LG Connect Apps is on, then try again.' } : null;
        return { ...base, screen: 'LG TV SSCR2', status: 'unpaired', methods: [methods[0], { ...lg, detected: false }], found: [lgTv],
          caps: { off: true, follow: true, input: true, readInput: true, test: false }, profiles: [{ ...profiles[1], current: true }],
          profile: { ...profile, method: 'webos', methodLabel: 'LG (webOS)', beta: true, deviceId: '1a2b', name: 'LG OLED65C4', model: 'OLED65C4PUA' },
          pairing: stage && { id: 'webos:1a2b', name: 'LG OLED65C4', codeLength: 6, ...stage } };
      }
      case 'pair-code': {
        const g = { id: 'androidtv', label: 'Google TV / Android TV', brand: 'Google TV', beta: true, how: 'Over your network. Type the code the TV shows, once.',
          checklist: [{ name: 'Same network', where: 'The TV and the box on the same network.' }], detected: false };
        const gTv = { id: 'androidtv:bt-020000002b01', method: 'androidtv', label: 'Google TV / Android TV', beta: true, name: 'Living room Google TV', model: 'TCL 65Q651G', locked: false, on: false, power: 'unknown', input: 0, detected: false, picked: true, paired: false };
        TvUi.code = 'A3';
        return { ...base, screen: 'TCL 65Q651G', status: 'unpaired', methods: [methods[0], g], found: [gTv],
          caps: { off: true, follow: true, input: true, readInput: false, test: false }, profiles: [{ ...profiles[0], name: 'Living room Google TV', method: 'androidtv', methodLabel: 'Google TV / Android TV' }],
          profile: { ...profile, method: 'androidtv', methodLabel: 'Google TV / Android TV', beta: true, deviceId: 'bt-020000002b01', name: 'Living room Google TV', model: 'TCL 65Q651G' },
          pairing: { id: 'androidtv:bt-020000002b01', name: 'Living room Google TV', stage: 'code', message: 'Type the code Living room Google TV shows.', codeLength: 6 } };
      }
      default: return base;
    }
  },
};

// ---- Settings › TV (index.html only) -------------------------------------------------------------

if (typeof settingsSection === 'function') (() => {
  state.tv = state.tv || { screen: null, profile: null, found: [], methods: [], profiles: [], caps: {}, status: 'unbound', port: 0 };
  let hint = null;   // a brand picked in the dialog without a TV of it found: its checklist shows

  const TOGGLES = [
    ['onWithBox', 'Turn the TV on with the box', 'When the box starts or wakes, and switch to its input', 'follow'],
    ['offWithBox', 'Turn the TV off when the box sleeps', 'Also when the box shuts down (not when it restarts)', 'off'],
    ['sleepWithTv', 'Sleep the box when the TV turns off', 'Turn the TV off with its own remote and the box follows', 'follow'],
  ];

  function renderTv() {
    const tv = state.tv;
    const p = tv.profile;
    const status = TvUi.statusLine(tv);
    const cards = (tv.profiles || []).map((x) =>
      `<div class="tv-card${x.current ? ' current' : ''}" data-nav data-id="tvprofile:${esc(x.key)}" data-act="tv-profile" data-arg="${esc(x.key)}">` +
        `<div class="tv-card-name">${icon('tv', 34)}<span>${esc(x.name)}</span></div>` +
        `<span class="tv-card-line">${esc([x.methodLabel, x.input ? `HDMI ${x.input}` : '', x.current ? '' : x.lastUsed ? `last used ${x.lastUsed}` : ''].filter(Boolean).join(' · '))}</span>` +
        (x.current ? '<span class="tv-now"><span></span>Plugged in now</span>' : '') + '</div>').join('');
    const setUp = !p && tv.screen
      ? `<div class="tv-card add" data-nav data-id="tv-setup" data-act="tv-method">${icon('plus', 28, 2)}Set up this TV (${esc(tv.screen)})</div>` : '';
    let right = TvUi.pairHtml(tv) + `<div class="srow" data-nav data-id="tv-method" data-act="tv-method"><div class="text"><span class="label">How the box controls it</span>` +
      `<span class="caption">${esc(p ? (p.method === 'none' ? 'No TV control: its own remote' : `${p.methodLabel}${p.beta ? ' (beta)' : ''}, over your network${p.name ? ` · ${p.name}` : ''}`) : 'Not set up yet: pick your TV')}</span></div>` +
      `<div class="value">${icon('chevright', 30, 2)}</div></div>`;
    // While pairing runs, only its panel (the rest waits; the keypad needs the room).
    const pairing = tv.pairing && ['code', 'prompt', 'working'].includes(tv.pairing.stage);
    if (pairing) right = TvUi.pairHtml(tv);
    if (!pairing && p && p.method !== 'none') {
      if (tv.caps.input) right += `<div class="srow" data-nav data-id="tv-input" data-tv-input="1"><div class="text"><span class="label">Input this box is on</span>` +
        `<span class="caption">${esc(TvUi.portNote(tv))}</span></div>` +
        `<div class="value">${icon('chevleft', 26, 2)}${p.input ? `HDMI ${p.input}` : 'Unknown'}${icon('chevright', 26, 2)}</div></div>`;
      for (const [key, label, caption, cap] of TOGGLES) {
        if (!tv.caps[cap]) continue;
        right += `<div class="srow" data-nav data-id="tv-opt-${key}" data-tv-option="${key}"><div class="text"><span class="label">${esc(label)}</span>` +
          `<span class="caption">${esc(caption)}</span></div>${toggle(p[key])}</div>`;
      }
      if (!tv.caps.follow) right += '<div class="srow"><div class="text"><span class="label">Turned on only</span><span class="caption">This TV does not tell the box whether it is on, so the box only turns it on (Wake-on-LAN). Turn it off with its remote.</span></div></div>';
    }
    if (hint && (!p || p.method !== hint)) right += TvUi.checklistHtml(tv, hint, 'tv-side inline');
    right += `<div class="tv-status ${status.kind}"><span></span>${esc(status.text)}</div>` +
      '<div class="sbuttons">' +
        (tv.caps.test && p ? '<div class="sbutton" data-nav data-id="tv-test" data-act="tv-test">Test: off and on</div>' : '') +
        '<div class="sbutton" data-nav data-id="tv-refresh" data-act="tv-refresh">Find it again</div>' +
      '</div>';
    return '<header><h1>TV</h1><p>The box recognises which TV it’s plugged into and uses that TV’s settings.</p></header>' +
      `<div class="tv-cols"><div class="tv-left"><span class="tv-label">Your TVs</span>${cards}${setUp}</div><div class="tv-right">${right}</div></div>`;
  }

  settingsSection('tv', {
    render: renderTv,
    press(button, el) {
      if (!el) return false;
      if (el.dataset.tvInput && (button === 'left' || button === 'right' || button === 'a')) {
        const p = state.tv.profile;
        const max = Math.max(4, state.tv.port || 0);
        const next = ((p.input || 1) - 1 + (button === 'left' ? -1 : 1) + max) % max + 1;
        p.input = next;
        send({ type: 'tv.input', input: next });
        window.render();
        return true;
      }
      if (el.dataset.tvOption && button === 'a') {
        const key = el.dataset.tvOption;
        state.tv.profile[key] = !state.tv.profile[key];
        send({ type: 'tv.option', key, value: state.tv.profile[key] });
        window.render();
        return true;
      }
      return false;
    },
    shown() { send({ type: 'tv.showing', on: true }); },
    // (The method dialog over Settings counts as the TV section: app.js's sectionHooks.)
    left() { send({ type: 'tv.showing', on: false }); hint = null; },
    demo() { state.tv = TvUi.demo(new URLSearchParams(location.hash.split('?')[1] || '').get('demo') || 'roku'); },
  });

  // "How should the box control this TV?": the TVs found, the brands, no control.
  addView('tvmethod', {
    overlay: true,
    render() {
      const tv = state.tv;
      const found = TvUi.foundRows(tv, 'tv-mrow');
      $('tvmethod').innerHTML = '<div class="tv-dialog">' +
        `<h2>How should the box control ${esc((tv.profile && tv.profile.name) || tv.screen || 'this TV')}?</h2>` +
        `<p>${found ? 'Pick your TV. Detected: it says it shows this box’s input.' : 'No TV found on the network yet. Pick your TV’s brand to see what to turn on, or skip TV control.'}</p>` +
        (found ? `<span class="tv-label">TVs on your network</span>${found}<span class="tv-label">Not listed?</span>` : '') +
        TvUi.methodRows(tv, 'tv-mrow', hint) +
        `<div class="hints" style="padding:0;height:64px">${hints([['A', 'Choose'], ['B', 'Cancel']])}</div></div>`;
    },
    focus: (list) => list.find((e) => e.classList.contains('picked')) || list[0],
    demo() { state.tv = TvUi.demo(new URLSearchParams(location.hash.split('?')[1] || '').get('demo') || 'lg'); },
  });

  onAction('tv-method', () => go('tvmethod'));
  onAction('tv-pick', (el, id) => { send({ type: 'tv.choose', id }); hint = null; back(); });
  // A brand only shows what to turn on and searches again: the user then picks the TV by its
  // name (the only TV of that brand found can be someone else's).
  onAction('tv-brand', (el, id) => {
    hint = id;
    toast('Searching for TVs…');
    send({ type: 'tv.refresh' });
    back();
  });
  onAction('tv-none', () => ask({
    title: 'No TV control?', text: 'The box will not turn this TV on or off or change its input. Use the TV’s own remote.', yes: 'No TV control',
    onYes: () => { hint = null; send({ type: 'tv.none' }); back(); },
  }));
  onAction('tv-profile', (el, key) => {
    const x = state.tv.profiles.find((q) => q.key === key);
    if (!x) return;
    if (x.current) { go('tvmethod'); return; }
    ask({ title: `Forget ${x.name}?`, text: 'Its settings go. If the box is plugged into it again, it asks to set it up.', yes: 'Forget', onYes: () => send({ type: 'tv.forget', key }) });
  });
  // Pairing controls (the panel above): the same ids as in setup.
  for (const act of ['tv-pair', 'tv-key', 'tv-code', 'tv-cancel-pair']) onAction(act, (el) => { TvUi.pairAction(el.dataset.id); window.render(); });
  onAction('tv-test', () => { toast('Turning the TV off and back on…'); send({ type: 'tv.test' }); });
  onAction('tv-refresh', () => { toast('Searching for TVs…'); send({ type: 'tv.refresh' }); });

  hostMessage('tv.', (msg) => {
    if (msg.type === 'tv.state') {
      state.tv = msg.tv;
      if (state.view === 'settings' || state.view === 'tvmethod') window.render();
    } else if (msg.type === 'tv.open') {
      state.section = 'tv';
      if (state.view !== 'settings') { state.stack = []; state.view = 'settings'; }
      window.render();
    }
  });
})();
