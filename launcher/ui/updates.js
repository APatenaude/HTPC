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
// Demo (a plain browser): index.html#settings/updates, and ?upd=<state> for the other states
// (uptodate, checking, running, failed, winfound, winscan, wininstall, winrestart, wintonight,
// winstuck, restarting).

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
  if (!s) return html + '<p class="caption">Reading what is installed…</p>';

  const list = [];
  const L = s.launcher;
  list.push(updRow('launcher', 'app', '#8CC2FF', 'TV launcher',
    L.update ? `${L.installed} → ${L.latest}${L.skipped ? ' · did not start here last time' : ''}` : L.installed, { update: L.update, skipped: L.skipped, job: L.job },
    L.update && L.notes ? `<span class="notes">${esc(L.notes)}</span>` : ''));
  for (const a of s.apps) list.push(updRow(a.id, a.id === 'winget' ? 'download' : a.glyph, a.id === 'winget' ? '#B3B5BC' : a.color, a.name, updVersions(a), a));
  const self = (id, glyph, color, name, version) =>
    `<div class="srow upd-row" data-nav data-id="upd-${id}"><span class="glyph" style="color:${color}">${icon(glyph, 34)}</span>` +
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
    body += '<div class="sbutton primary" data-nav data-id="upd-all" data-act="upd-all">Update all</div>' +
      '<p class="small">A restore point is saved first, so a bad update can be undone.</p>';
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
    body += `<p style="color:var(--warn)">${esc(w.message || 'Windows Update did not answer.')}</p>` + button('upd-win-scan', 'upd-win-scan', 'Try again');
  } else if (w.result === 'ok' && w.total > 0) {
    const size = (w.updates || []).reduce((t, u) => t + (u.sizeMb || 0), 0);
    const counted = w.counted;
    const what = counted ? `${counted} update${counted === 1 ? '' : 's'}` : 'Security definitions';
    body += `<p><b>${esc(what)} ready</b>${size ? ` · ${Math.round(size)} MB` : ''}</p>` +
      `<div class="pair">${button('upd-win-now', 'upd-win-now', 'Install now', true)}${button('upd-win-tonight', 'upd-win-tonight', 'Tonight')}</div>`;
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
  shown() { send({ type: 'updates.get' }); },
  // Two columns: left and right move between the list and the cards (app.js's settingsPress, as
  // in every section); left from the list goes to the section list.
  demo() { updDemo(); },
});

// ---- Actions ------------------------------------------------------------------------------

onAction('upd-row', (el, id) => {
  const s = upd.s;
  if (!s) return;
  if (id === 'launcher') {
    const L = s.launcher;
    if (!L.update || L.job) return;
    ask({
      title: `${L.skipped ? 'Try' : 'Update'} the TV launcher ${L.skipped ? 'again ' : ''}to ${L.latest}?`,
      text: (L.skipped ? `Last time ${L.latest} did not start here and the box went back to ${L.installed}. ` : '') +
        'It downloads now; the launcher restarts by itself once you are back at Home, never over an app. Open apps keep running.' + (L.notes ? ` New: ${L.notes}` : ''),
      yes: 'Update', onYes: () => send({ type: 'updates.app', id: 'launcher' }),
    });
    return;
  }
  const a = s.apps.find((x) => x.id === id);
  if (!a || !a.update || a.job) return;
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

function updDemo() {
  const kind = new URLSearchParams(location.search).get('upd') || 'ready';
  const hour = 3600000;
  const app = (id, name, glyph, color, installed, available, update, job) => ({ id, name, glyph, color, installed, available, update, job: job || null });
  const s = {
    type: 'updates.state', checking: false, checkedAt: Date.now() - 2 * hour, checkError: null,
    launcher: { installed: '0.1.0', latest: '0.2.0', update: true, notes: 'Updates from the TV, phone remote, library', job: null },
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
  switch (kind) {
    case 'uptodate':
      s.launcher.update = false; s.launcher.latest = '0.1.0';
      for (const a of s.apps) { a.update = false; a.available = a.installed; }
      Object.assign(w, { result: 'ok', checkedAt: new Date(Date.now() - 3 * hour).toISOString() });
      break;
    case 'checking': s.checking = true; break;
    case 'running':
      s.restorePoint = { status: 'done', percent: 100, message: 'Restore point saved (21:47)' };
      s.apps[0].job = { status: 'done', percent: 100, message: 'YouTube updated to 1.8.3' };
      s.apps[1].job = { status: 'running', percent: 45, message: 'Updating Stremio' };
      s.apps[2].job = { status: 'queued', percent: 0, message: 'Waiting' };
      s.launcher.job = { status: 'queued', percent: 0, message: 'Waiting' };
      s.lane = { label: 'Updating Stremio', percent: 45, message: 'Downloading 5.0.27' };
      break;
    case 'failed':
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

