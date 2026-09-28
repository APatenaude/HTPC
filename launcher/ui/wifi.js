'use strict';
// Wi-Fi (design: Settings: Wi-Fi), as a piece any screen can hold: Settings › Wi-Fi
// (settings-network.js) and the first-run Wi-Fi step. It draws rows with data-nav / data-id
// (both pages move the focus over those), handles A and X on them and B inside its forms, and
// talks to the host. It needs the page's send(), esc() and icon(), and textinput.js for the
// password field (the on-screen keyboard; the phone's typing).
//
//   WifiUI.start(opts)    shown: the host starts scanning. opts: { changed(focusId), ask?({title, text, yes, onYes}), toast?(text, kind) }
//                         changed: redraw (html() again), then focus data-id focusId if given
//   WifiUI.stop()         hidden: scanning stops, a form in progress is dropped
//   WifiUI.handle(msg)    host messages wifi.state / wifi.result; true if it was one
//   WifiUI.html()         the rows (list, or the join form)
//   WifiUI.press(button, el) -> handled   el: the focused data-nav element
//   WifiUI.afterRender()  after the page drew html(): the form's field gets the focus and the keyboard
//   WifiUI.demo(name)     sample data (a plain browser): ethernet, wifi, password, hidden, location, noadapter, off
//   From the host: {type:'wifi.state', adapter, radio, location, wired, current, networks, wifiInternet, askRadioOff}
//                  {type:'wifi.result', ssid, ok, reason, text}
//   To the host:   {type:'wifi.watch', on} {type:'wifi.join', ssid, password?, hidden?, security?}
//                  {type:'wifi.forget', ssid} {type:'wifi.radio', on} {type:'wifi.allowLocation'}

// Two line icons from the design that icons.js lacks.
if (typeof ICONS === 'object') Object.assign(ICONS, {
  lock: 'M6 11h12v10H6zM8 11V8a4 4 0 0 1 8 0v3',
  cable: 'M7 3v5M17 3v5M5 8h14v4a7 7 0 0 1-14 0zM12 19v3',
});

const WifiUI = (() => {
  let st = null;                 // the host's last wifi.state
  let opts = {};
  // While joining: { ssid, hidden, security, error, busy, reveal, name, password, caret, focused }.
  // The typed name and password live here, not only in the inputs: the page redraws the pane
  // (the clock, state pushes, Show password) and afterRender puts them back, with the focus.
  let form = null;
  let wantField = false;         // afterRender: put the focus in the field
  const HIDDEN_SECURITY = [['wpa2psk', 'WPA2'], ['wpa3sae', 'WPA3'], ['open', 'None (open)']];

  const changed = (focusId) => { if (opts.changed) opts.changed(focusId); };
  const say = (text, kind) => { if (opts.toast) opts.toast(text, kind); };

  function row(id, glyph, label, caption, right, extra) {
    return `<div class="srow wifi-row${extra ? ' ' + extra : ''}" data-nav data-id="${id}">` +
      (glyph ? `<span class="wifi-glyph">${icon(glyph, 36)}</span>` : '') +
      `<div class="text"><span class="label">${esc(label)}</span>${caption ? `<span class="caption">${caption}</span>` : ''}</div>${right || ''}</div>`;
  }
  const toggle = (on) => `<div class="toggle${on ? ' on' : ''}"><span></span></div>`;

  function listHtml() {
    if (!st) return '<p class="wifi-note">Looking for Wi-Fi…</p>';
    let h = '';
    const w = st.wired;
    if (w && w.up) {
      h += row('wifi-cable', 'cable', 'Network cable', `<span class="ok">Connected · ${w.mbps >= 1000 ? w.mbps / 1000 + ' Gb/s' : w.mbps + ' Mb/s'}${w.internet ? ' · in use' : ''}</span>`, '');
    }
    if (!st.adapter) return h + '<p class="wifi-note">This box has no Wi-Fi.</p>';
    const radioOn = st.radio === 'on';
    h += row('wifi-radio', null, 'Wi-Fi', st.radio === 'disabled' ? 'Turned off by a switch on the box or flight mode' : '', st.radio === 'disabled' ? '' : toggle(radioOn));
    if (!radioOn) return h;
    if (st.location === 'denied') {
      h += row('wifi-allow', 'warn', 'Allow location to see networks', 'Windows shows Wi-Fi networks only to apps allowed to know the location.', '<div class="value link">Allow</div>', 'warn');
    } else if (st.location === 'device') {
      h += row('wifi-location-off', 'warn', 'Location is off for this box', 'Windows lists Wi-Fi networks only with location on. Use a network cable, or turn location on in Windows’ privacy settings (setup turns it on too).', '', 'warn');
    }
    if (st.current) {
      h += row('wifi-current', 'wifi', st.current.ssid, `<span class="ok">Connected · ${esc(st.current.words)}</span>`, '', 'current');
    }
    const others = (st.networks || []).filter((n) => !n.connected);
    h += '<span class="section">Other networks</span><div class="wifi-scroll">';
    h += others.map((n, i) =>
      row('wifi-net:' + i, 'wifi', n.ssid, [n.saved ? 'Saved' : '', esc(n.words)].filter(Boolean).join(' · '),
        n.security === 'open' || n.security === 'owe' ? '' : `<span class="wifi-lock">${icon('lock', 28)}</span>`)).join('');
    h += row('wifi-hidden', 'plus', 'Hidden network', 'A network that does not show its name', '');
    h += '</div>';
    if (st.wifiInternet && !(w && w.up)) h += `<p class="wifi-note">${icon('cable', 28)}A network cable, if one reaches the TV, is steadier for 4K streaming.</p>`;
    return h;
  }

  function formHtml() {
    const f = form;
    const net = f.hidden ? null : (st.networks || []).find((n) => n.ssid === f.ssid);
    const needsPassword = f.hidden ? f.security !== 'open' : !!(net && net.password);
    let h = `<span class="section">${f.hidden ? 'Hidden network' : 'Join ' + esc(f.ssid)}</span>`;
    if (f.hidden) {
      h += `<div class="srow wifi-field" data-nav data-id="wifi-name"><div class="text"><span class="label">Network name</span>` +
        `<input id="wifi-name-input" type="text" autocomplete="off" spellcheck="false" aria-label="Network name" maxlength="32"></div></div>`;
      // data-edit: in Settings, left/right change it only once A has picked the row (app.js).
      h += `<div class="srow" data-nav data-id="wifi-security" data-wifi-step="1" data-edit><div class="text"><span class="label">Security</span></div>` +
        `<div class="value">${icon('chevleft', 28, 2)}${esc(HIDDEN_SECURITY.find(([v]) => v === f.security)[1])}${icon('chevright', 28, 2)}</div></div>`;
    }
    if (needsPassword) {
      h += `<div class="srow wifi-field" data-nav data-id="wifi-password"><div class="text"><span class="label">Password</span>` +
        `<input id="wifi-password-input" type="${f.reveal ? 'text' : 'password'}" autocomplete="off" spellcheck="false" aria-label="Password for ${esc(f.ssid || 'the network')}" maxlength="64"></div></div>`;
      h += `<div class="srow" data-nav data-id="wifi-reveal"><div class="text"><span class="label">Show password</span></div>${toggle(f.reveal)}</div>`;
    }
    if (f.error) h += `<p class="wifi-note error">${icon('warn', 28, 2)}${esc(f.error)}</p>`;
    h += '<div class="sbuttons">' +
      `<div class="sbutton" data-nav data-id="wifi-go">${f.busy ? 'Joining…' : 'Join'}</div>` +
      '<div class="sbutton" data-nav data-id="wifi-cancel">Cancel</div></div>';
    h += `<p class="wifi-note">${icon('keyboard', 28)}Type with the on-screen keyboard (R3), or on your phone.</p>`;
    return h;
  }

  const field = (id) => document.getElementById(id);

  function openForm(f, focusId) {
    form = { error: null, busy: false, reveal: false, name: '', password: '', caret: null, focused: null, ...f };
    wantField = true;
    changed(focusId);
  }

  function closeForm(focusId) {
    form = null;
    if (typeof textDone === 'function') textDone();
    if (document.activeElement && document.activeElement.blur) document.activeElement.blur();
    changed(focusId);
  }

  function join() {
    const f = form;
    if (!f || f.busy) return;
    if (f.hidden) f.ssid = f.name.trim();
    if (!f.ssid) { f.error = 'Type the network’s name.'; changed('wifi-name'); return; }
    const msg = { type: 'wifi.join', ssid: f.ssid };
    if (field('wifi-password-input')) msg.password = f.password;
    if (f.hidden) { msg.hidden = true; msg.security = f.security; }
    f.busy = true;
    f.error = null;
    changed('wifi-go');
    send(msg);
  }

  function pickNetwork(n) {
    if (n.refusal) { say(n.refusal, 'warn'); return; }
    if (n.saved || !n.password) { send({ type: 'wifi.join', ssid: n.ssid }); say(`Joining ${n.ssid}…`); return; }
    openForm({ ssid: n.ssid, hidden: false }, 'wifi-password');
  }

  function forget(ssid) {
    const go = () => send({ type: 'wifi.forget', ssid });
    const inUse = st.current && st.current.ssid === ssid && st.wifiInternet;
    const text = inUse ? 'The box is online through it: it goes offline, and so does the phone remote.' : 'The box won’t join it again until you type its password.';
    if (opts.ask) opts.ask({ title: `Forget ${ssid}?`, text, yes: 'Forget', onYes: go }); else go();
  }

  function setRadio(on) {
    const go = () => send({ type: 'wifi.radio', on });
    if (!on && st.askRadioOff && opts.ask) opts.ask({ title: 'Turn Wi-Fi off?', text: 'The box is online only through Wi-Fi: it goes offline, and so does the phone remote.', yes: 'Turn off', onYes: go });
    else go();
  }

  function others() { return (st && st.networks || []).filter((n) => !n.connected); }

  return {
    start(o) { opts = o || {}; send({ type: 'wifi.watch', on: true }); },
    stop() { if (form) closeForm(); send({ type: 'wifi.watch', on: false }); },
    handle(msg) {
      if (msg.type === 'wifi.state') {
        const typing = form && document.activeElement && document.activeElement.tagName === 'INPUT';
        st = msg;
        if (!typing) changed();   // not while typing: a redraw would take the field away
        return true;
      }
      if (msg.type === 'wifi.result') {
        if (form && msg.ssid === form.ssid) {
          form.busy = false;
          if (msg.ok) { closeForm('wifi-current'); say(msg.text); }
          else {
            form.error = msg.text;
            if (msg.reason === 'wrong-password') { form.password = ''; form.caret = 0; }
            changed(msg.reason === 'wrong-password' ? 'wifi-password' : 'wifi-go');
          }
        } else say(msg.text, msg.ok ? 'info' : 'warn');
        return true;
      }
      return false;
    },
    html() { return form ? formHtml() : listHtml(); },
    afterRender() {
      if (!form) return;
      const pw = field('wifi-password-input'), name = field('wifi-name-input');
      for (const [el, key] of [[pw, 'password'], [name, 'name']]) {
        if (!el) continue;
        el.value = form[key] || '';
        if (!el.dataset.wired) {
          el.dataset.wired = '1';
          el.addEventListener('textsubmit', join);
          // Only into the form this field was drawn for: a late event from an earlier form's
          // field (a queued 'select') must not fill this one.
          const owner = form;
          const keep = () => { if (form === owner) { form[key] = el.value; form.caret = el.selectionStart; form.focused = key; } };
          for (const type of ['input', 'keyup', 'focus', 'select']) el.addEventListener(type, keep);
        }
        // The field had the focus before the redraw: again, with the caret where it was.
        if (form.focused === key && !wantField) {
          el.focus();
          const at = Math.min(form.caret ?? el.value.length, el.value.length);
          el.setSelectionRange(at, at);
        }
      }
      if (wantField) {
        wantField = false;
        const el = form.hidden ? name : pw;
        if (el && typeof openKeyboardFor === 'function') openKeyboardFor(el);
      }
    },
    press(button, el) {
      const id = el && el.dataset.id || '';
      if (form) {
        if (button === 'b') { closeForm(); return true; }
        if (button === 'a') {
          if (id === 'wifi-go') join();
          else if (id === 'wifi-cancel') closeForm();
          else if (id === 'wifi-reveal') { form.reveal = !form.reveal; const pw = field('wifi-password-input'); if (pw) pw.type = form.reveal ? 'text' : 'password'; changed('wifi-reveal'); }
          else if (id === 'wifi-password') openKeyboardFor(field('wifi-password-input'));
          else if (id === 'wifi-name') openKeyboardFor(field('wifi-name-input'));
          else if (id === 'wifi-security') { const i = HIDDEN_SECURITY.findIndex(([v]) => v === form.security); form.security = HIDDEN_SECURITY[(i + 1) % HIDDEN_SECURITY.length][0]; changed('wifi-security'); }
          return true;
        }
        if ((button === 'left' || button === 'right') && id === 'wifi-security') {
          const i = HIDDEN_SECURITY.findIndex(([v]) => v === form.security), n = HIDDEN_SECURITY.length;
          form.security = HIDDEN_SECURITY[(i + (button === 'right' ? 1 : -1) + n) % n][0];
          changed('wifi-security');
          return true;
        }
        return false;
      }
      if (!st) return false;
      if (button === 'a') {
        if (id === 'wifi-radio') { setRadio(st.radio !== 'on'); return true; }
        if (id === 'wifi-allow') { send({ type: 'wifi.allowLocation' }); return true; }
        if (id === 'wifi-hidden') { openForm({ ssid: '', hidden: true, security: 'wpa2psk' }, 'wifi-name'); return true; }
        if (id.startsWith('wifi-net:')) { const n = others()[Number(id.slice(9))]; if (n) pickNetwork(n); return true; }
        return id.startsWith('wifi-');
      }
      if (button === 'x') {
        if (id === 'wifi-current' && st.current) { forget(st.current.ssid); return true; }
        if (id.startsWith('wifi-net:')) { const n = others()[Number(id.slice(9))]; if (n && n.saved) forget(n.ssid); return true; }
      }
      return false;
    },
    // Sample data for screenshots in a plain browser (made-up names only).
    demo(name) {
      const nets = [
        { ssid: '[Network name 2]', signal: 80, words: 'strong signal', security: 'wpa2psk', password: true, saved: false, connected: false },
        { ssid: '[Network name 3]', signal: 45, words: 'good signal', security: 'wpa3transition', password: true, saved: true, connected: false },
        { ssid: '[Guest network]', signal: 30, words: 'weak signal', security: 'open', password: false, saved: false, connected: false },
        { ssid: '[Old router]', signal: 20, words: 'weak signal', security: 'wep', password: false, refusal: 'This network uses WEP, an old security the box doesn’t use. Change the router to WPA2 or WPA3.', saved: false, connected: false },
      ];
      st = { adapter: true, radio: 'on', location: 'ok', wired: { name: 'Ethernet', mbps: 1000, up: true, internet: true }, current: null, networks: nets, wifiInternet: false, askRadioOff: false };
      if (name === 'wifi') {
        st.wired = null; st.wifiInternet = true; st.askRadioOff = true;
        st.current = { ssid: '[Network name]', signal: 85, words: 'strong signal' };
        st.networks = [{ ...st.current, security: 'wpa2psk', password: true, saved: true, connected: true }, ...nets];
      }
      if (name === 'location') { st.location = 'denied'; st.networks = []; }
      if (name === 'noadapter') st.adapter = false;
      if (name === 'off') st.radio = 'off';
      if (name === 'password') form = { ssid: '[Network name 2]', hidden: false, error: 'Wrong password. Check it and try again.', busy: false, reveal: false, name: '', password: '', caret: null, focused: null };
      if (name === 'hidden') form = { ssid: '', hidden: true, security: 'wpa3sae', error: null, busy: false, reveal: false, name: '', password: '', caret: null, focused: null };
    },
    get joining() { return !!form; },
  };
})();
