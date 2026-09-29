'use strict';
// Settings › Updates (SPEC N10 "Nothing updates unless asked"; design "Settings: updates").
// Loaded after app.js (index.html). This script owns the Updates section, the installed
// versions included. The host side is MainForm.Updates.cs and UpdateService.cs.
//
//   To the host:   updates.get  updates.check  updates.app {id} ("launcher" = the launcher)
//                  updates.all {close: [ids of open apps it closes]}
//                  updates.windowsScan  updates.windowsCancel  updates.windowsInstall {when: now|tonight}
//                  updates.restart {when: now|tonight}  updates.tonightCancel
//   From the host: updates.state (UpdateService.Describe)  updates.restarting {version}
//                  updates.stay (the launcher update stopped before its restart)
//
// Demo (a plain browser): index.html#settings/updates, a launcher update ready (1.0.6 to 1.0.7,
// with 1.0.7's own notes, a line per point), and ?upd=<state> for the other states (uptodate,
// checking, running, failed, downloading and waiting: the launcher's update, setup: a release
// that needs TV Box Setup (its minimumFrom), longnotes and longerrors: the longest text each
// place can get, winfound, winscan, wininstall, winrestart, wintonight, winstuck, restarting).

const upd = { s: null, restarting: null };

function updAgo(ms) {
  if (!ms) return 'never';
  const min = Math.round((Date.now() - ms) / 60000);
  if (min < 2) return 'just now';
  if (min < 60) return `${min} min ago`;
  const h = Math.round(min / 60);
  if (h < 24) return `${h} h ago`;
  const d = Math.round(h / 24);
  return d === 1 ? 'yesterday' : `${d} days ago`;
}

function updDate(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return isNaN(d) ? '' : d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
}

// A release's notes as shown (html): line by line as written, a "- " or "* " at a line's start
// drawn as a bullet (updates.css), empty lines left out. At most max characters, and as many
// lines as asked (the row: 4), cut at a word, with "…" after when there is more. update.json may
// hold 256 KB of them: the launcher's row shows its first 4 lines (updates.css clamps a line that
// wraps too), the question A asks all of them up to a sane length, in a box that scrolls (app.js
// ask's notes).
function updNotes(text, max, lines = Infinity) {
  const all = String(text || '');
  const rows = all.slice(0, 4 * max).split(/\r\n?|\n/).map((r) => r.replace(/\s+/g, ' ').trim()).filter(Boolean);
  const out = [];
  let left = max, more = all.length > 4 * max;
  for (const row of rows) {
    if (out.length === lines || left <= 0) { more = true; break; }
    const bullet = /^[-*•] /.test(row);
    const t = bullet ? row.slice(2) : row;
    if (t.length > left) {
      const cut = t.slice(0, left), at = cut.lastIndexOf(' ');
      out.push({ t: at > left * 0.7 ? cut.slice(0, at) : cut, bullet });
      more = true;
      break;
    }
    left -= t.length;
    out.push({ t, bullet });
  }
  if (more && out.length) out[out.length - 1].t = `${out[out.length - 1].t.replace(/[\s.,;:·-]+$/, '')}…`;
  return out.map((l) => `<span class="upd-note${l.bullet ? ' bullet' : ''}">${esc(l.t)}</span>`).join('');
}

function updBar(percent) { return `<div class="upd-bar"><span style="width:${Math.max(2, Math.min(100, percent || 0))}%"></span></div>`; }

// What a row says on the right: its job first, then whether it has an update.
function updStatus(row) {
  const j = row.job;
  if (j) {
    if (j.status === 'running') return ['busy', 'download', j.percent ? `${j.percent}%` : 'Updating'];
    if (j.status === 'waiting') return ['quiet', 'home', 'At Home'];
    if (j.status === 'queued') return ['quiet', 'timer', 'Waiting'];
    if (j.status === 'done') return ['ok', 'check', 'Updated'];
    if (j.status === 'failed') return ['bad', 'warn', 'Not updated'];
  }
  if (row.update) return ['update', 'download', row.skipped ? 'Try again' : 'Update'];
  if (row.error) return ['quiet', 'warn', 'Not checked'];
  return ['ok', 'check', 'Up to date'];
}

function updRow(id, glyph, color, name, detail, row, extra) {
  const [cls, g, text] = updStatus(row);
  const caption = row.job && row.job.message && (row.job.status === 'running' || row.job.status === 'failed' || row.job.status === 'waiting')
    ? row.job.message : detail;
  return `<div class="srow upd-row" data-nav data-id="upd-${esc(id)}" data-act="upd-row" data-arg="${esc(id)}">` +
    `<span class="glyph" style="color:${esc(color)}">${icon(glyph, 34)}</span>` +
    `<div class="text"><span class="label">${esc(name)}</span><span class="caption">${esc(caption || '')}</span>${extra || ''}</div>` +
    `<span class="upd-status ${cls}">${icon(g, 26, 2.25)}${esc(text)}</span></div>`;
}

function updVersions(a) {
  if (a.update && a.available) return `${a.installed || '?'} → ${a.available}`;
  return a.installed || '';
}

// A launcher version rolled back here (launcher.skipped) is not counted and not in Update all.
function updLauncherOffered(s) { return s.launcher.update && !s.launcher.skipped; }

function updPending(s) {
  return s.apps.filter((a) => a.update).length + (updLauncherOffered(s) ? 1 : 0);
}

function updRunningApps(s) {
  return s.apps.filter((a) => a.update && state.tiles.some((t) => t.id === a.id && t.running));
}

function renderUpdatesSection() {
  const s = upd.s;
  let html = '<header><h1>Updates</h1><p>Nothing updates by itself. You decide when.</p></header>';
  if (!s) return html + `<p class="caption">${hostWaitText('updates', 'Reading what is installed…', 'The list of what is installed didn’t come. Open Updates again to try once more.')}</p>`;

  const list = [];
  const L = s.launcher;
  const notes = L.update ? updNotes(L.notes, 300, 4) : '';
  list.push(updRow('launcher', 'app', '#8CC2FF', 'TV launcher',
    L.update ? `${L.installed} → ${L.latest}${L.skipped ? ' · did not start here last time' : ''}` : L.installed, { update: L.update, skipped: L.skipped, job: L.job },
    notes ? `<span class="notes">${notes}</span>` : ''));
  for (const a of s.apps) list.push(updRow(a.id, a.id === 'winget' ? 'download' : a.glyph, a.id === 'winget' ? '#B3B5BC' : a.color, a.name, updVersions(a), a));
  const self = (id, glyph, color, name, version) =>
    // data-noa: A does nothing here (no A in the hints, no select sound).
    `<div class="srow upd-row" data-nav data-id="upd-${id}" data-noa><span class="glyph" style="color:${color}">${icon(glyph, 34)}</span>` +
    `<div class="text"><span class="label">${esc(name)}</span><span class="caption">${esc(version || '')}</span></div>` +
    `<span class="upd-status quiet">${icon('restart', 26, 2)}Updates itself</span></div>`;
  if (s.builtIn.edge) list.push(self('edge', 'globe', '#3CCB9A', 'Microsoft Edge', s.builtIn.edge));
  if (s.builtIn.webview) list.push(self('webview', 'globe', '#B3B5BC', 'WebView2 (this screen)', s.builtIn.webview));

  html += `<div class="upd"><div class="upd-list">${list.join('')}</div><div class="upd-side">${updAppsCard(s)}${updWindowsCard(s.windows)}</div></div>`;
  return html;
}

function updAppsCard(s) {
  const n = updPending(s);
  const lane = s.lane;
  const appsBusy = lane && !(s.windows.job);
  let title, body = '';
  if (s.checking) title = 'Checking for updates…';
  else if (appsBusy) title = lane.label;
  else if (n) title = `${n} app update${n === 1 ? '' : 's'} ready`;
  else title = 'Everything is up to date';

  if (appsBusy) {
    body += updBar(lane.percent) + `<p>${esc(lane.message || 'Working…')}</p>`;
    if (s.restorePoint && s.restorePoint.status === 'done') body += `<span class="small">${icon('check', 20, 2.25)} ${esc(s.restorePoint.message || 'Restore point saved')}</span>`;
  } else if (n && !s.checking) {
    // Check again stays offered once updates are found: a newer release may be out since.
    body += '<div class="sbutton primary" data-nav data-id="upd-all" data-act="upd-all">Update all</div>' +
      '<p class="small">A restore point is saved first, so a bad update can be undone.</p>' +
      '<div class="sbutton" data-nav data-id="upd-check" data-act="upd-check">Check again</div>';
  } else if (!s.checking) {
    body += '<div class="sbutton" data-nav data-id="upd-check" data-act="upd-check">Check now</div>';
  }
  const checked = s.checkError ? `Last check failed: ${s.checkError}` : `Checked ${updAgo(s.checkedAt)}`;
  body += `<span class="upd-checked">${esc(checked)}</span>`;
  return `<div class="upd-card"><h2>${esc(title)}</h2>${body}</div>`;
}

function updWindowsCard(w) {
  let body = '<p>Security updates. Installed when you choose, then the box restarts.</p>';
  const job = w.job;
  const button = (id, act, label, primary) => `<div class="sbutton${primary ? ' primary' : ''}" data-nav data-id="${id}" data-act="${act}">${esc(label)}</div>`;
  if (job && job.token === 'windows-scan') {
    body += updBar(job.percent) + '<p>Looking for updates. This can take a while.</p>' + button('upd-win-cancel', 'upd-win-cancel', 'Cancel');
  } else if (job && job.token === 'windows-install') {
    const what = job.step === 'restorepoint' ? job.message : (job.message || 'Installing');
    body += updBar(job.percent) + `<p>${esc(what)}</p><span class="small">You can keep watching. The box restarts only when you say.</span>`;
  } else if (w.queued && w.queued.length) {
    body += '<p class="small">Waiting for the updates in progress…</p>';
  } else if (w.tonight) {
    body += `<p><b>Tonight</b>, between 2 and 5 while the box sleeps${w.tonight.install ? ': installed, then the box restarts' : ': the box restarts'}. The TV stays off.</p>` +
      button('upd-tonight-cancel', 'upd-tonight-cancel', 'Not tonight');
  } else if (w.rebootRequired) {
    body += '<p><b>Restart to finish</b> installing Windows updates.</p>' +
      `<div class="pair">${button('upd-restart-now', 'upd-restart-now', 'Restart now', true)}${button('upd-restart-tonight', 'upd-restart-tonight', 'Tonight')}</div>`;
  } else if (w.result === 'busy' || w.result === 'timeout' || w.result === 'failed') {
    body += `<p class="upd-error">${esc(w.message || 'Windows Update did not answer.')}</p>` + button('upd-win-scan', 'upd-win-scan', 'Try again');
  } else if (w.result === 'ok' && w.total > 0) {
    const size = (w.updates || []).reduce((t, u) => t + (u.sizeMb || 0), 0);
    const counted = w.counted;
    const what = counted ? `${counted} update${counted === 1 ? '' : 's'}` : 'Security definitions';
    body += `<p><b>${esc(what)} ready</b>${size ? ` · ${Math.round(size)} MB` : ''}</p>` +
      `<div class="pair">${button('upd-win-now', 'upd-win-now', 'Install now', true)}${button('upd-win-tonight', 'upd-win-tonight', 'Tonight')}</div>` +
      button('upd-win-scan', 'upd-win-scan', 'Check again');
  } else if (w.result === 'ok') {
    body += `<p>${icon('check', 22, 2.25)} Windows is up to date</p>` + button('upd-win-scan', 'upd-win-scan', 'Check again');
  } else {
    body += button('upd-win-scan', 'upd-win-scan', 'Check for Windows updates');
  }
  if (w.lastInstalled) body += `<span class="small">Last installed: ${esc(updDate(w.lastInstalled))}</span>`;
  if (w.checkedAt && !job) body += `<span class="small">Checked ${esc(updAgo(Date.parse(w.checkedAt)))}</span>`;
  return `<div class="upd-card"><h2>Windows</h2>${body}</div>`;
}

settingsSection('updates', {
  render: renderUpdatesSection,
  shown() { hostAsked('updates'); send({ type: 'updates.get' }); },
  // Two columns: left and right move between the list and the cards (app.js's settingsPress, as
  // in every section); left from the list goes to the section list.
  demo() { updDemo(); },
});

// ---- Actions ------------------------------------------------------------------------------

onAction('upd-row', (el, id) => {
  const s = upd.s;
  if (!s) return;
  // A row whose last try failed can be pressed again; one running or waiting cannot.
  const busy = (job) => job && job.status !== 'failed';
  if (id === 'launcher') {
    const L = s.launcher;
    if (!L.update || busy(L.job)) return;
    ask({
      title: `${L.skipped ? 'Try' : 'Update'} the TV launcher ${L.skipped ? 'again ' : ''}to ${L.latest}?`,
      text: (L.skipped ? `Last time ${L.latest} did not start here and the box went back to ${L.installed}. ` : '') +
        'It downloads now; the launcher restarts by itself once you are back at Home, never over an app. Open apps keep running.',
      // What is new in it, all of it (to 20 000 characters), in a box Up and Down scroll.
      notes: updNotes(L.notes, 20000),
      yes: 'Update', onYes: () => send({ type: 'updates.app', id: 'launcher' }),
    });
    return;
  }
  const a = s.apps.find((x) => x.id === id);
  if (!a || !a.update || busy(a.job)) return;
  const open = state.tiles.some((t) => t.id === id && t.running);
  ask({
    title: `Update ${a.name}?`,
    text: `${a.installed} → ${a.available}.` + (open ? ` ${a.name} is open: it closes first.` : ''),
    yes: 'Update', onYes: () => send({ type: 'updates.app', id }),
  });
});

onAction('upd-all', () => {
  const s = upd.s;
  if (!s) return;
  const apps = s.apps.filter((a) => a.update);
  const open = updRunningApps(s);
  const n = apps.length + (updLauncherOffered(s) ? 1 : 0);
  ask({
    title: `Update ${n === 1 ? 'it' : `all ${n}`}?`,
    text: 'A restore point is saved first.' +
      (open.length ? ` Open apps close first: ${open.map((a) => a.name).join(', ')}.` : '') +
      (updLauncherOffered(s) ? ' The TV launcher goes last and restarts by itself at Home.' : ''),
    yes: 'Update all', onYes: () => send({ type: 'updates.all', close: open.map((a) => a.id) }),
  });
});

onAction('upd-check', () => { send({ type: 'updates.check' }); });
onAction('upd-win-scan', () => send({ type: 'updates.windowsScan' }));
onAction('upd-win-cancel', () => send({ type: 'updates.windowsCancel' }));
onAction('upd-win-now', () => ask({
  title: 'Install Windows updates now?',
  text: 'A restore point is saved first. You can keep watching; the box restarts only when you say.',
  yes: 'Install now', onYes: () => send({ type: 'updates.windowsInstall', when: 'now' }),
}));
onAction('upd-win-tonight', () => send({ type: 'updates.windowsInstall', when: 'tonight' }));
onAction('upd-restart-now', () => ask({
  title: 'Restart the box now?',
  text: 'Windows finishes installing its updates, then the TV launcher comes back.',
  yes: 'Restart', onYes: () => send({ type: 'updates.restart', when: 'now' }),
}));
onAction('upd-restart-tonight', () => send({ type: 'updates.restart', when: 'tonight' }));
onAction('upd-tonight-cancel', () => send({ type: 'updates.tonightCancel' }));

hostMessage('updates.', (m) => {
  if (m.type === 'updates.state') {
    hostAnswered('updates');
    upd.s = m;
    if (state.view === 'settings' && state.section === 'updates') render();
  } else if (m.type === 'updates.restarting') {
    upd.restarting = m.version;
    if (state.view !== 'updrestart') go('updrestart');
  } else if (m.type === 'updates.stay') {
    // The update stopped before the restart: back where "Restarting" was shown from.
    if (state.view === 'updrestart') back();
  }
});

// The old launcher's last screen before it exits for the new one.
addView('updrestart', {
  render() {
    $('updrestart').innerHTML = '<div class="center">' +
      `<span class="accent-icon">${icon('restart', 96, 1.5)}</span>` +
      '<h1>Restarting the TV launcher</h1>' +
      `<p>Version ${esc(upd.restarting || '')} · back in a few seconds</p></div>`;
  },
  press() { return true; },   // nothing to do but wait
  demo() { upd.restarting = '0.2.0'; },
});

// ---- Demo data (a plain browser) -------------------------------------------------------------

// Release 1.0.7's notes, as its update.json has them: a line per point.
const UPD_DEMO_NOTES = [
  '- The Home menu shows what the box is busy with: CPU, memory, disk, network and the three programs using the most, which you can close from there.',
  '- Steam: a tap on Home is Steam\'s own menu, holding Home opens this one; Steam steps aside under the menu and quits when left with no window.',
  '- Add a tile: apps and sites grouped by category (LT and RT jump between them), program icons in On this box, programs added from there open full screen, and the Website form and Rename use the on-screen keyboard.',
  '- The on-screen keyboard is shorter, wraps around at its edges, and RT switches to the symbols.',
  '- Windows a website opens come up maximized; the home screen is quicker with many tiles; an app that is no longer installed leaves the home screen.',
  '- RetroBat and YouTube Kids are no longer offered (a box that has RetroBat keeps it; On this box can put it back on the home screen).',
].join('\n');
// Text with no place to break a line: a path, a word longer than a row.
const UPD_DEMO_UNBROKEN = 'C:\\Users\\television\\Documents\\HTPC\\logs\\' + 'uninstall-and-a-copy-of-setup-'.repeat(6) + 'log';
// The longest notes a release can have: update.json is read up to 256 KB (UpdateService, UpdateCore).
// Line by line, with Windows' line ends (the release workflow writes the tag's message with them),
// a line with no place to break first, a line with no bullet, and empty lines.
const UPD_DEMO_LONG_NOTES = [`- Unbroken first: ${UPD_DEMO_UNBROKEN}.`,
  'Then a line with no bullet and a path with backslashes, Documents\\HTPC logs\\setup, and a great deal more after it.', '',
  ...UPD_DEMO_NOTES.split('\n'), ''].join('\r\n').repeat(250).slice(0, 260000);

function updDemo(kindArg) {
  const kind = kindArg || new URLSearchParams(location.search).get('upd') || 'ready';
  const hour = 3600000;
  const app = (id, name, glyph, color, installed, available, update, job) => ({ id, name, glyph, color, installed, available, update, job: job || null });
  const s = {
    type: 'updates.state', checking: false, checkedAt: Date.now() - 2 * hour, checkError: null,
    launcher: { installed: '1.0.6', latest: '1.0.7', update: true, notes: UPD_DEMO_NOTES, job: null },
    apps: [
      app('youtube', 'YouTube', 'youtube', '#FF5B52', '1.8.2', '1.8.3', true),
      app('stremio', 'Stremio', 'film', '#7C8CFF', '5.0.26', '5.0.27', true),
      app('jellyfin', 'Jellyfin', 'library', '#3DC0F0', '1.12.0', '1.12.1', true),
      app('moonlight', 'Moonlight', 'moon', '#F5D16B', '6.1.0.0', null, false),
      app('winget', 'winget (App Installer)', 'download', '#B3B5BC', '1.29.380', '1.29.380', false),
    ],
    restorePoint: null,
    builtIn: { edge: '154.0.4258.37', webview: '153.0.4234.48' },
    windows: { result: null, message: null, counted: 0, total: 0, updates: [], rebootRequired: false, checkedAt: null,
      lastInstalled: '2026-09-26T15:36:00Z', job: null, queued: [], tonight: null, last: null },
    lane: null,
  };
  const w = s.windows;
  const found = [{ title: '2026-10 Cumulative Update (KB5131000)', kb: '5131000', sizeMb: 612, counted: true },
    { title: '2026-10 .NET Framework Security Update', kb: '5131200', sizeMb: 70, counted: true },
    { title: 'Security Intelligence Update for Microsoft Defender (KB2267602)', kb: '2267602', sizeMb: 110, counted: false }];
  const L = s.launcher;
  const updating = (percent, message) => ({ label: `Updating the TV launcher to ${L.latest}`, percent, message });
  switch (kind) {
    case 'uptodate':
      L.update = false; L.latest = L.installed;
      for (const a of s.apps) { a.update = false; a.available = a.installed; }
      Object.assign(w, { result: 'ok', checkedAt: new Date(Date.now() - 3 * hour).toISOString() });
      break;
    case 'checking': s.checking = true; break;
    // The launcher's own update (UpdateService.Describe, LauncherUpdate.ps1's progress): it
    // downloads, then waits for Home; a release whose minimumFrom is newer than this box needs setup.
    case 'downloading':
      L.job = { status: 'running', percent: 38, message: 'Downloading version 1.0.7 (21 of 55 MB)' };
      s.lane = updating(38, L.job.message);
      break;
    case 'waiting':
      L.job = { status: 'waiting', percent: 90, message: 'Waits until you are back at Home' };
      s.lane = updating(90, L.job.message);
      break;
    case 'setup':
      L.job = { status: 'failed', percent: 100, message: 'Version 1.0.7 needs setup to run again (it updates launchers from 1.0.7 on)' };
      break;
    case 'longnotes': L.notes = UPD_DEMO_LONG_NOTES; break;
    // Every message as long as it gets, with a path or a word that has no place to break.
    case 'longerrors':
      L.skipped = true;
      L.job = { status: 'failed', percent: 100, message: `The update stopped: ${UPD_DEMO_UNBROKEN} could not be replaced, it is in use by another program. Back on version 1.0.6.` };
      s.apps[1].name = `Stremio ${UPD_DEMO_UNBROKEN}`;
      s.apps[1].job = { status: 'failed', percent: 100, message: `The update asked for admin rights; stopped. ${UPD_DEMO_UNBROKEN}` };
      s.apps[3].error = `Not checked: ${UPD_DEMO_UNBROKEN}`;
      s.checkError = `GitHub is limiting requests from this box; try again later. ${UPD_DEMO_UNBROKEN}`;
      Object.assign(w, { result: 'failed', message: `Windows Update did not answer (0x8024402C): ${UPD_DEMO_UNBROKEN}. Restart the box, then try again.` });
      break;
    case 'running':
      s.restorePoint = { status: 'done', percent: 100, message: 'Restore point saved (21:47)' };
      s.apps[0].job = { status: 'done', percent: 100, message: 'YouTube updated to 1.8.3' };
      s.apps[1].job = { status: 'running', percent: 45, message: 'Updating Stremio' };
      s.apps[2].job = { status: 'queued', percent: 0, message: 'Waiting' };
      L.job = { status: 'queued', percent: 0, message: 'Waiting' };
      s.lane = { label: 'Updating Stremio', percent: 45, message: 'Downloading 5.0.27' };
      break;
    case 'failed':
      L.job = { status: 'failed', percent: 100, message: 'Not enough free disk space for version 1.0.7: 312 MB free, 668 MB needed' };
      s.apps[1].job = { status: 'failed', percent: 100, message: 'The update asked for admin rights; stopped' };
      s.checkError = 'GitHub is limiting requests from this box; try again later';
      break;
    case 'winfound': Object.assign(w, { result: 'ok', counted: 2, total: 3, updates: found, checkedAt: new Date(Date.now() - 600000).toISOString() }); break;
    case 'winscan': w.job = { token: 'windows-scan', phase: 'scan', percent: 5, message: 'Looking for Windows updates' }; s.lane = { label: 'Looking for Windows updates', percent: 5, message: '' }; break;
    case 'wininstall':
      Object.assign(w, { result: 'ok', counted: 2, total: 3, updates: found });
      w.job = { token: 'windows-install', phase: 'install', percent: 38, message: 'Installing 1 of 3: 2026-10 Cumulative Update (KB5131000)', n: 1, m: 3, step: 'install' };
      s.lane = { label: 'Installing Windows updates', percent: 38, message: '' };
      s.apps[1].job = { status: 'queued', percent: 0, message: 'Waiting for Windows updates' };
      break;
    case 'winrestart': Object.assign(w, { result: 'ok', rebootRequired: true, lastInstalled: new Date().toISOString() }); break;
    case 'wintonight': Object.assign(w, { result: 'ok', counted: 2, total: 3, updates: found, tonight: { install: true, restart: true } }); break;
    case 'winstuck': Object.assign(w, { result: 'busy', message: 'Windows Update is stuck. Restart the box, then try again.' }); break;
  }
  upd.s = s;
}

// index.html?upd=restarting shows the old launcher's last screen.
if (new URLSearchParams(location.search).get('upd') === 'restarting') {
  addEventListener('DOMContentLoaded', () => { upd.restarting = '0.2.0'; setTimeout(() => go('updrestart'), 0); });
}

