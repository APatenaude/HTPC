'use strict';
// Self-test: a release's notes. Run by selftest.js, in its order.
selftestGroup(async ({ check, sent, lastSent, heard }) => {
  // ---- A release's notes: line by line, the row's first 4, the question scrolls ------------------
  // The question shows them all (to 20 000 characters) in a box Up and Down scroll, the focus left
  // on its buttons. Other screen sizes: the audit's pages.
  {
    const lines = UPD_DEMO_NOTES.split('\n').map((l) => l.replace(/^- /, ''));
    const row = () => $('settings').querySelector('[data-id="upd-launcher"]');
    const noteLines = (el) => [...el.querySelectorAll('.upd-note')];
    const box = () => askNotes();
    const bar = () => $('ask').querySelector('.dialog > .hints');
    const focusId = () => focusedEl() && focusedEl().dataset.id;
    const edges = () => ['more-up', 'more-down'].filter((c) => box().classList.contains(c)).join(' ') || 'none';
    const openRow = (kind, notes) => {
      updDemo(kind);
      if (notes !== undefined) upd.s.launcher.notes = notes;
      state.section = 'updates';
      reset('settings');
      setFocus(row());
    };

    // The row: the first 4 lines, each on a line of its own ("- " and "* " a bullet), "…" after.
    openRow('ready', '- One thing\r\n\r\n- Two things\n* Three\nFour, with no bullet.\n- Five');
    const rowLines = noteLines(row());
    check('Notes: the row keeps their line breaks, a bullet for each "- " line', rowLines.map((e) => `${e.classList.contains('bullet') ? '• ' : ''}${e.textContent}`).join('|')
      === '• One thing|• Two things|• Three|Four, with no bullet…', rowLines.map((e) => e.textContent).join('|'));
    check('Notes: ... the 5th left out', !row().textContent.includes('Five'));
    // 1.0.7's: their first line wraps in the row's width (it is 4 lines on its own). Heights in
    // the page's own pixels: the section is still coming in (its entrance scales it).
    openRow('ready');
    const clampOf = () => row().querySelector('.notes');
    const lineH = parseFloat(getComputedStyle(clampOf()).lineHeight);
    check('Notes: the row shows 4 lines at most, a line that wraps clamped', noteLines(row())[0].textContent === lines[0] && clampOf().clientHeight <= 4 * lineH + 1
      && clampOf().scrollHeight > clampOf().clientHeight, `${clampOf().clientHeight} px, a line ${lineH} px`);
    openRow('longnotes');
    const r = row().getBoundingClientRect(), n = clampOf().getBoundingClientRect();
    check('Notes: the longest in the row: 4 lines at most, 300 characters, no wider than the row', noteLines(row()).length <= 4 && clampOf().textContent.length <= 301
      && clampOf().clientHeight <= 4 * lineH + 1 && n.right <= r.right + 1 && row().scrollWidth <= row().clientWidth,
      `${noteLines(row()).length} lines, ${clampOf().textContent.length} characters, ${clampOf().clientHeight} px, right ${n.right} / ${r.right}`);

    // The question, short notes: in their box, line by line; nothing to scroll.
    openRow('ready', '- One thing\n- And another');
    sent.length = 0;
    press('a');
    check('Notes: the question has them line by line, in a box of their own', state.view === 'ask' && box() && noteLines(box()).map((e) => e.textContent).join('|') === 'One thing|And another',
      box() ? box().textContent : state.view);
    check('Notes: short, they do not scroll: no Scroll hint, their ends not faded', box().scrollHeight <= box().clientHeight && !/Scroll/.test(bar().textContent) && edges() === 'none',
      `${bar().textContent}, ${edges()}`);
    press('down'); press('up');
    check('Notes: ... Up and Down leave the focus on Cancel', focusId() === 'ask-no' && box().scrollTop === 0, focusId());
    press('b');

    // 1.0.7's: all six, line by line; the Scroll hint if (and only if) they are longer than the box.
    openRow('ready');
    press('a');
    check('Notes: the question has all of them, line by line (1.0.7\'s six)', noteLines(box()).map((e) => e.textContent).join('\n') === lines.join('\n'),
      noteLines(box()).map((e) => e.textContent).join(' | '));
    check('Notes: ... a Scroll hint only if they are longer than their box', /Scroll/.test(bar().textContent) === box().scrollHeight > box().clientHeight + 2,
      `${box().scrollHeight} in ${box().clientHeight}: ${bar().textContent}`);
    press('b');

    // The longest: 20 000 characters of them, in a box that scrolls.
    openRow('longnotes');
    press('a');
    const all = noteLines(box()), length = box().textContent.length;
    check('Notes: the longest in the question: to 20 000 characters, line by line, "…" after', length <= 20001 && length > 19000 && all.length > 100 && /…$/.test(all[all.length - 1].textContent),
      `${length} characters, ${all.length} lines`);
    // In the stage's pixels (1080 high), not the screen's: the dialog is still popping in.
    const dialog = $('ask').querySelector('.dialog');
    check('Notes: ... their box at most 45 % of the screen\'s height, the question on the screen', box().offsetHeight <= 0.45 * 1080 && dialog.offsetTop >= 0
      && dialog.offsetTop + dialog.offsetHeight <= 1080, `${box().offsetHeight} of 1080; the dialog ${dialog.offsetTop} to ${dialog.offsetTop + dialog.offsetHeight}`);
    check('Notes: ... it scrolls: the Scroll hint, only the bottom end faded', box().scrollHeight > box().clientHeight && /↑↓\s*Scroll/.test(bar().textContent) && edges() === 'more-down',
      `${bar().textContent}, ${edges()}`);
    press('down'); press('down');
    const down = box().scrollTop;
    check('Notes: Down scrolls them, the focus stays on Cancel, both ends faded', down > 0 && focusId() === 'ask-no' && edges() === 'more-up more-down', `${down}, ${focusId()}, ${edges()}`);
    press('up');
    check('Notes: Up scrolls them back', box().scrollTop > 0 && box().scrollTop < down && focusId() === 'ask-no', `${box().scrollTop}, ${focusId()}`);
    press('left');
    check('Notes: left and right still go between the buttons', focusId() === 'ask-yes', focusId());
    const was = box().scrollTop;
    press('down');
    check('Notes: ... Down scrolls from Update too, the focus left on it', box().scrollTop > was && focusId() === 'ask-yes', `${box().scrollTop}, ${focusId()}`);
    box().scrollTop = box().scrollHeight;
    press('down');
    const end = box().scrollTop;
    press('down');
    check('Notes: at the end Down stops there, only the top end faded', box().scrollTop === end && end + box().clientHeight >= box().scrollHeight - 2 && edges() === 'more-up',
      `${box().scrollTop} / ${end}, ${edges()}`);
    check('Notes: Down at their end thuds, Up scrolling ticks', await heard(() => press('down')) === 'bump' && await heard(() => press('up')) === 'move');
    press('b');
    check('Notes: B leaves the question, nothing sent', state.view === 'settings' && !lastSent('updates.app'), state.view);
    press('a');
    check('Notes: asked again, they start at the top', state.view === 'ask' && box().scrollTop === 0 && edges() === 'more-down', `${box().scrollTop}, ${edges()}`);
    press('b');
    ask({ title: 'Restart the box?', text: 'Apps close and the box starts again.', yes: 'Restart' });
    check('Notes: a question with none: no notes box, no Scroll hint', !box() && !/Scroll/.test(bar().textContent), bar().textContent);
    press('down');
    check('Notes: ... Down there leaves the focus on Cancel', focusId() === 'ask-no', focusId());
    press('b');
    reset('home');
  }
});
