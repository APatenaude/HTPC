'use strict';
// UI audit pages: the on-screen keyboard (keyboard.html). Registered with auditPage() (audit.js).

// Its rows and columns wrap round on purpose: the walker lets them.
if (AUDIT_PAGE === 'keyboard') {
  auditPage('keyboard: letters', { view: 'keyboard', back: 0, wraps: true, open() { onHost({ type: 'open', field: `A field ${AUDIT_LONG}`, password: false }); } });
  auditPage('keyboard: symbols, shift locked', { view: 'keyboard', covers: [], back: 0, wraps: true, open() {
    onHost({ type: 'open', field: 'Search', password: false });
    symbols = true; shift = 'lock'; render();   // eslint-disable-line no-global-assign
  } });
  auditPage('keyboard: a password', { view: 'keyboard', covers: [], back: 0, wraps: true, open() {
    onHost({ type: 'open', field: 'Password', password: true });
    typed = 'x'.repeat(48); render();   // eslint-disable-line no-global-assign
  } });
}
