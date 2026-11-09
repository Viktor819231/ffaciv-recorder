// Preview-only browser check, evaluated by tools/site/shot.mjs: a control whose command waits for its answer keeps the
// keyboard focus (it is aria-disabled, not disabled), ignores clicks meanwhile, and setup's radio groups keep the focus on
// the arrowed-to choice instead of jumping to the step's title.
(() => {
  const check = (ok, message) => { if (!ok) throw Error(message); };
  const k = (key) => document.querySelector(`[data-k="${key}"]`);
  Mock.state.firstRun = false;
  App.go('settings');
  // a switch: Space presses it; the answer is held back to look at the waiting state
  const original = Bridge.send, sent = [];
  Bridge.send = (cmd, args) => { sent.push(cmd); };
  k('sw-startWithWindows').focus();
  k('sw-startWithWindows').click();
  check(document.activeElement === k('sw-startWithWindows'), 'Focus left the switch while its command waits');
  check(k('sw-startWithWindows').getAttribute('aria-disabled') === 'true' && k('sw-startWithWindows').getAttribute('aria-busy') === 'true', 'Waiting switch is not marked busy');
  check(!k('sw-startWithWindows').disabled, 'Waiting switch is disabled (drops the focus)');
  k('sw-startWithWindows').click();
  check(sent.length === 1, 'A waiting switch took a second click');
  Bridge.send = original;
  Bridge.send('setSetting', { key: 'startWithWindows', value: false });   // the answer
  check(document.activeElement === k('sw-startWithWindows'), 'Focus left the switch after the answer');
  check(k('sw-startWithWindows').getAttribute('aria-disabled') === null, 'Switch still busy after the answer');
  // setup: arrow keys move between the choices of a radio group, and the focus follows
  App.setup(3);
  k('setup-share-true').focus();
  document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
  check(document.activeElement === k('setup-share-false'), `Arrow key focus went to ${document.activeElement?.id || document.activeElement?.dataset?.k || document.activeElement?.tagName}`);
  check(k('setup-share-false').getAttribute('aria-checked') === 'true', 'Arrow key did not choose the next option');
  App.setup(0); document.querySelector('[data-exit-setup]')?.click();
  console.log('PASS: waiting controls keep the focus, ignore clicks, and setup radio groups keep it on the chosen option.');
})();
