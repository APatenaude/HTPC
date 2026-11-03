// Linux stand-in for Test-Ui.ps1 -SelfTest [-AllSizes] and Test-All's phone page: the UI
// self-test (index.html#selftest), the audits (#audit) and dev/phone-test.html, in headless
// Chrome. One line per run, its FAIL lines; exit 0 = all passed.
// Usage: node launcher/dev/linux/test-ui.mjs [--all-sizes]
import { chromium } from 'playwright';

const ui = new URL('../../ui/', import.meta.url);
const audits = [['setup.html', '1536x864'], ['keyboard.html', '1536x352']];
if (process.argv.includes('--all-sizes'))
  audits.push(['index.html', '1920x1080'], ['index.html', '1280x720'], ['index.html', '2560x1080'], ['index.html', '1920x1200'],
    ['setup.html', '1920x1080'], ['setup.html', '1280x720'], ['keyboard.html', '1920x440'], ['keyboard.html', '2560x440']);
const runs = [
  { name: 'UI self-test', page: 'index.html', route: 'selftest', size: '1536x864', pre: 'selftest-results', title: 'SELFTEST' },
  ...audits.map(([page, size]) => ({ name: 'UI audit', page, route: 'audit', size, pre: 'audit-results', title: 'AUDIT' })),
];

// chromiumSandbox: false, since Chrome refuses its sandbox as root (containers).
// --allow-file-access-from-files: the phone page's layout audit looks inside its frames.
const browser = await chromium.launch({ channel: 'chrome', chromiumSandbox: false, args: ['--disable-gpu', '--allow-file-access-from-files'] });
let failed = 0;

// A fresh page; reads go through raw CDP, not page.evaluate: Playwright's evaluate counts as a
// user gesture, which lets sounds.js open the AudioContext and fails the "never opened" check.
async function open(url, size) {
  const [width, height] = size.split('x').map(Number);
  const context = await browser.newContext({ viewport: { width, height } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  // "commit": the polls below are the wait; a busy machine can hold "load" past 30 s.
  await page.goto(url, { waitUntil: 'commit' });
  const cdp = await context.newCDPSession(page);
  const read = async (expression) => (await cdp.send('Runtime.evaluate', { expression, returnByValue: true })).result.value ?? '';
  const until = async (expression, done) => {
    const started = Date.now();
    let value = '';
    while (!done(value) && Date.now() - started < 300000) {
      await new Promise((res) => setTimeout(res, 250));
      value = await read(expression);
    }
    return done(value) ? value : null;
  };
  return { context, errors, read, until };
}

function report(name, lines, seconds, errors) {
  const fails = lines.filter((l) => l.startsWith('FAIL'));
  console.log(`${name}: ${lines.filter((l) => l.startsWith('PASS')).length} passed, ${fails.length} failed (${seconds} s)`);
  fails.forEach((l) => console.log('  ' + l));
  if (errors.length) console.log('  page errors: ' + errors.join('; '));
  return fails.length > 0;
}

try {
  for (const [i, r] of runs.entries()) {
    const started = Date.now();
    const p = await open(`${new URL(r.page, ui).href}?run=${i + 1}#${r.route}`, r.size);
    const name = `${r.name}, ${r.page} at ${r.size}`;
    const title = await p.until('document.title', (t) => t.startsWith(r.title + ' '));
    if (title === null) {
      console.log(`${name}: no results (title "${await p.read('document.title')}") ${p.errors.join('; ')}`);
      failed = 1;
    } else {
      const lines = (await p.read(`document.getElementById('${r.pre}').textContent`)).split(/\r?\n/);
      if (report(name, lines, ((Date.now() - started) / 1000).toFixed(1), p.errors) || !title.includes(' PASS')) failed = 1;
      const ranAt = await p.read('innerWidth + "x" + innerHeight');
      if (ranAt !== r.size) { console.log(`  ${r.page} ran at ${ranAt}, not ${r.size}`); failed = 1; }
    }
    await p.context.close();
  }

  const started = Date.now();
  const p = await open(new URL('../phone-test.html', import.meta.url).href, '1280x800');
  const results = await p.until("document.getElementById('results')?.textContent", (t) => /ALL PASSED\s*$|\d+ FAILED/.test(t));
  if (results === null) { console.log('Phone page, phone-test.html: no results'); failed = 1; }
  else if (report('Phone page, phone-test.html', results.split(/\r?\n/), ((Date.now() - started) / 1000).toFixed(1), p.errors)
    || !/ALL PASSED\s*$/.test(results)) failed = 1;
  await p.context.close();
} finally {
  await browser.close();
}
process.exit(failed);
