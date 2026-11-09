// The FFACiv Recorder window: Recorder, Streaming, Previous games, Manual upload, Updates, Options, Help (+ Checks, first start, dialogs).
// It only draws the state the program sends (bridge.js) and sends commands back; it decides nothing itself.
// The screens are drawn by the other js/*.js files (loaded before this one, see index.html); this file keeps the shared state,
// draws it, takes the clicks and keys, and talks to the program (bridge.js).
'use strict';
/** The window's own state, shared by every js/*.js file. S is the latest state from the program; the rest is the window's. */
const ui = {
  S: null,               // the latest state
  route: location.hash.slice(1) || 'home',
  dialog: null,          // { kind, ... } shown over the screen
  menu: null,            // the open drop-down: its key
  feedback: null,        // the answer of the last command, on top of the screen
  pending: null,         // the command waiting for its answer
  setupOpen: new URLSearchParams(location.search).has('setup'),
  step: 0,               // first start: the walkthrough's step, 0..6
  simple: false,         // the one-page Simple setup instead of the step-by-step walkthrough
  reviewReturn: false,   // a walkthrough step opened from the review goes back to it
  gamesFilter: 'all',    // Previous games: all, private or public
  picked: new Set(),     // Manual upload: the games ticked
};
const App = (() => {
  const $ = (sel) => document.querySelector(sel);
  let pendingDeletion = null;   // the recording being deleted: its name goes into the answer
  let pendingTimer = null;      // gives up waiting for a command's answer after 20 s

  // ---- drawing -----------------------------------------------------------------------------------------------------------
  let renderedSetupStep = -1;   // the setup step (or 'simple') drawn last: a redraw of the same step keeps its scroll and sections
  const setupKey = () => ui.simple ? 'simple' : ui.step;
  const SCREENS = { home, streaming, games, settings, help, updates, checks, old: oldGames };
  const TABS = [['home', 'Recorder', 'record'], ['streaming', 'Streaming', 'requests'], ['games', 'Previous games', 'games'], ['old', 'Manual upload', 'up'], ['updates', 'Updates', 'download'], ['settings', 'Options', 'settings'], ['help', 'Help', 'help']];
  const tabOf = (r) => r === 'checks' ? 'home' : r;
  let lastScreen = null;
  let lastOverlayHtml = null;
  // A section is known by its own key or its heading: the state arrives every second or two, and a redraw used to close
  // every section the player had opened.
  const detailsKey = (d) => d.dataset.detailsKey ?? d.querySelector(':scope > summary')?.textContent.trim();
  // A drop-down list being chosen from is not replaced under the mouse: the state waits until it loses focus.
  const choosing = () => document.activeElement?.tagName === 'SELECT' && !!document.activeElement.closest('#view');
  let stateWaiting = false;
  document.addEventListener('focusout', (e) => { if (e.target.tagName === 'SELECT' && stateWaiting) { stateWaiting = false; setTimeout(draw); } });
  function draw() {
    if (!ui.S) return;
    const focusKey = document.activeElement?.dataset?.k;
    const reportText = document.querySelector('#rep-text');
    const reportSelection = reportText ? [reportText.selectionStart,reportText.selectionEnd] : null;
    if(ui.dialog?.kind === 'report' && reportText) {ui.dialog.text=reportText.value;ui.dialog.parts=[...document.querySelectorAll('[data-part]:checked')].map(el=>el.dataset.part);}
    const openDetails = new Set([...document.querySelectorAll('#view details[open]')].map(detailsKey));
    const setupScroll = document.querySelector('.welcome-body')?.scrollTop ?? 0;
    const setupDetails = [...document.querySelectorAll('.welcome details')].map(d => d.open);
    const sameSetupStep = renderedSetupStep === setupKey();
    $('#mark').className = 'mark ' + trayKind();   // the same five looks as the tray icon and the side bar
    $('#nav').innerHTML = TABS.map(([r, t, ic]) => {
      const upd = r === 'updates' && ui.S.updates?.available;
      return `<button class="tab${r === 'updates' ? ' wide-only' : ''}" data-go="${r}" data-k="tab-${r}" ${tabOf(ui.route) === r ? 'aria-current="page"' : ''}>${icon(ic)}<span>${t}</span>${upd ? `<span class="dot${ui.S.updates.available.required ? '' : ' new'}">1</span>` : ''}</button>`;
    }).join('');
    const sidebarTitle = ui.S.settings?.recording === false ? 'Recorder is off' : ui.S.current?.lobby ? 'In the multiplayer lobby' : ui.S.status?.kind === 'waiting' ? ui.S.civDetected ? 'Civ detected' : 'Waiting for Civ' : ui.S.status?.title ?? '';
    $('#sidestate').innerHTML = `<span class="mark ${trayKind()}"></span><div><b>${esc(sidebarTitle)}</b>${esc('Version ' + ui.S.version)}</div>`;
    const view = $('#view');
    const scroll = lastScreen === ui.route ? view.scrollTop : 0;
    view.innerHTML = `<div class="screen${lastScreen === ui.route ? '' : ' enter'}" style="${lastScreen === ui.route ? 'animation:none' : ''}">${ui.feedback ? `<div class="action-feedback ${esc(ui.feedback.kind)}" role="status" aria-live="polite">${icon(ui.feedback.kind === 'pending' ? 'refresh' : ui.feedback.kind === 'info' ? 'info' : ui.feedback.kind === 'warn' || ui.feedback.kind === 'bad' ? 'alert' : 'checkCircle')}<div><b>${esc(ui.feedback.title)}</b>${ui.feedback.text ? `<span>${esc(ui.feedback.text)}</span>` : ''}${ui.feedback.actions?.length ? `<div class="row">${ui.feedback.actions.map(a => btn(esc(a.label), a.cmd, a.args ?? {}, a.primary ? 'primary' : 'quiet')).join('')}</div>` : ''}</div><button class="icon-btn" data-dismiss-feedback aria-label="Dismiss">${icon('x')}</button></div>` : ''}${(SCREENS[ui.route] ?? home)()}</div>`;
    if(lastScreen === ui.route) view.querySelectorAll('details').forEach(d => { const k = detailsKey(d); if (k) d.open = openDetails.has(k); });
    view.scrollTop = scroll; lastScreen = ui.route;
    // Status arrives frequently. Keep open controls (including native select menus)
    // alive unless the actual overlay contents changed.
    const overlay = $('#overlay'), overlayHtml = welcome() + dialogHtml();
    if (overlayHtml !== lastOverlayHtml) {
      const previousDialog = overlay.querySelector('.dialog');
      const dialogScroll = previousDialog?.scrollTop ?? 0;
      overlay.innerHTML = overlayHtml;
      lastOverlayHtml = overlayHtml;
      const nextDialog = overlay.querySelector('.dialog');
      if (previousDialog && nextDialog) {
        nextDialog.scrollTop = dialogScroll;
        nextDialog.closest('.scrim').style.animation = 'none';
      }
    }
    const onboarding = document.querySelector('.welcome');
    if (onboarding) {
      onboarding.inert = !!ui.dialog;
      if (sameSetupStep) onboarding.querySelectorAll('details').forEach((d,i) => d.open = setupDetails[i] ?? false);
      document.querySelector('.welcome-body').scrollTop = sameSetupStep ? setupScroll : 0;
      updateSetupScrollCue();
    }
    $('#app').inert = !!onboarding || !!ui.dialog;
    renderedSetupStep = onboarding ? setupKey() : -1;
    document.body.classList.toggle('hide-preview', !Bridge.demo);
    if (Bridge.demo) {
      $('#preview').innerHTML = `<span>Preview · made-up data</span><select aria-label="Preview situation" data-scenario>${Object.entries(Mock.scenarios).map(([k, v]) => `<option value="${k}" ${Mock.scenario === k ? 'selected' : ''}>${v.label}</option>`).join('')}</select>`;
      Bridge.toHost('ui.tray', { kind: trayKind(), tip: trayTip() });
    }
    const focusScope = document.querySelector('.dialog') ?? onboarding ?? document;
    const f = focusKey && focusScope.querySelector(`[data-k="${CSS.escape(focusKey)}"]`);
    if (f && !f.disabled && (!onboarding || sameSetupStep || ui.dialog)) {if(document.activeElement !== f)f.focus({ preventScroll: true });if(f.id === 'rep-text' && reportSelection)f.setSelectionRange(...reportSelection);}
    else if (onboarding && !ui.dialog) onboarding.querySelector('#setup-title').focus({preventScroll:true});
    else if (ui.menu) document.querySelector('.menu button')?.focus();
    else if (ui.dialog) document.querySelector('.dialog button:not(:disabled), .dialog input:not(:disabled), .dialog select:not(:disabled)')?.focus({preventScroll:true});
  }
  function updateSetupScrollCue() {
    const body = document.querySelector('.welcome-body'), cue = document.querySelector('.setup-scroll-cue');
    if(body && cue) cue.hidden = body.scrollHeight - body.clientHeight - body.scrollTop < 5;
  }
  document.addEventListener('scroll', e => {if(e.target.matches?.('.welcome-body'))updateSetupScrollCue();}, true);
  document.addEventListener('toggle', e => {if(e.target.closest?.('.welcome'))updateSetupScrollCue();}, true);
  function trayKind() {
    const k = ui.S.status?.kind;
    if (k === 'attention' || k === 'unsupported' || (ui.S.updates?.available?.required)) return 'attention';
    if (k === 'recording') return problems().length ? 'attention' : 'recording';
    if (k === 'sending') return 'sending';
    if (k === 'paused') return 'paused';
    return 'waiting';
  }
  const trayTip = () => `FFACiv Recorder · ${ui.S.status?.title ?? ''}`.slice(0, 120);
  // countdowns tick without redrawing the screen
  setInterval(() => document.querySelectorAll('[data-until]').forEach((e) => { e.textContent = until(+e.dataset.until); }), 15e3);
  function navigate(r) { ui.route = r; ui.menu = null; location.hash = r; draw(); $('#view').focus({ preventScroll: true }); }

  // ---- commands and input ---------------------------------------------------------------------------------------------------
  /** Commands the window frame does itself, without an answer to wait for. */
  const WINDOW_ONLY = new Set(['showWindow', 'hideWindow', 'showOptions', 'reviewDelete']);
  function send(cmd, args = {}) {
    ui.pending = cmd;
    ui.feedback = cmd === 'sendReport' ? {kind:'pending',title:'Preparing your local report…',text:'The recorder will confirm when the zip is saved. Nothing is uploaded.'} : {kind:'pending',title:'Working…',text:'Waiting for the recorder to confirm.'};
    clearTimeout(pendingTimer);
    pendingTimer = setTimeout(() => { if (ui.pending === cmd) {ui.pending = null;ui.feedback = {kind:'warn',title:'Still waiting for confirmation',text:'Check your connection and try again if needed.'};draw();} }, 20000);
    draw();
    Bridge.send(cmd, args);
  }
  document.addEventListener('click', (e) => {
    const t = e.target.closest('button, [data-scrim]');
    if (!t) { if (ui.menu) { ui.menu = null; draw(); } return; }
    if (t.matches('[data-scrim]') && e.target === t) { ui.dialog = null; draw(); return; }
    if (t.disabled || t.getAttribute('aria-disabled') === 'true') return;   // pending: its answer is awaited, the focus stays on it
    if(t.hasAttribute('data-start-private-stream') && ui.dialog?.kind==='livestream') {const scope='player',durationHours=ui.dialog.hours??4,configure=ui.S.livestream?.needsSetup===true,startCurrentSave=!!ui.S.current&&!ui.S.current.lobby&&!ui.S.current.ended&&(ui.dialog.startCurrentSave??true);ui.dialog=null;send('startLivestream',{scope,durationHours,configure,startCurrentSave});draw();return;}
    if (t.hasAttribute('data-dismiss-feedback')) {ui.feedback = null; draw(); return;}
    if (t.hasAttribute('data-exit-setup')) {ui.setupOpen = false; ui.simple = false; draw(); return;}
    if (t.hasAttribute('data-simple-setup')) {ui.reviewReturn = false; send('simpleSetup', {}); draw(); return;}
    if (t.hasAttribute('data-simple-back')) {ui.simple = false; ui.step = 0; draw(); return;}
    if (t.hasAttribute('data-setup')) {ui.setupOpen = true; ui.simple = false; ui.reviewReturn = t.hasAttribute('data-return-review'); ui.step = Number(t.dataset.setupStep ?? 0); draw(); return;}
    if (t.dataset.menu) { ui.menu = ui.menu === t.dataset.menu ? null : t.dataset.menu; draw(); return; }
    if (ui.menu && !t.closest('.menu')) ui.menu = null;
    if (t.dataset.go) { navigate(t.dataset.go); return; }
    if (t.dataset.filter) { ui.gamesFilter = t.dataset.filter; draw(); return; }
    if (t.dataset.step) { ui.step = Math.max(0, Math.min(SETUP_STEPS.length-1, ui.step + +t.dataset.step)); draw(); return; }
    if (t.dataset.delete) { const game=(ui.S.games??[]).find(g=>g.id===t.dataset.delete)??ui.S.current;ui.dialog = {kind:'deleteGame', game:t.dataset.delete, context:t.dataset.context || null,label:game?recordingLabel(game):null}; draw(); return; }
    if (t.hasAttribute('data-confirm-delete') && ui.dialog?.kind === 'deleteGame') { const args = {game:ui.dialog.game,context:ui.dialog.context};pendingDeletion={id:ui.dialog.game,label:ui.dialog.label};ui.dialog = null; send('deleteGame',args); draw(); return; }
    if (t.dataset.dialog) { ui.dialog = { kind: t.dataset.dialog }; draw(); return; }
    if (t.hasAttribute('data-close')) { ui.dialog = null; draw(); return; }
    if (t.hasAttribute('data-send-report')) {
      const parts = [...document.querySelectorAll('[data-part]')].filter((c) => c.checked).map((c) => c.dataset.part);
      ui.dialog = null; send('sendReport', { parts, text: $('#rep-text')?.value ?? '' }); return;
    }
    if (t.dataset.pick) { const id = t.dataset.pick; ui.picked.has(id) ? ui.picked.delete(id) : ui.picked.add(id); draw(); return; }
    if (t.dataset.cmd === 'ui.pickAll') { const can = (ui.S.oldGames?.games ?? []).filter(pickable); const all = can.every((g) => ui.picked.has(g.id)); can.forEach((g) => all ? ui.picked.delete(g.id) : ui.picked.add(g.id)); draw(); return; }
    if (t.dataset.cmd === 'uploadOldGames') {
      const ids = [...ui.picked];
      if (ids.length && !['delta', 'full'].includes(ui.S.settings?.oldGamesUpload)) { ui.dialog = { kind: 'oldUpload', ids, format: 'delta' }; draw(); return; }   // first upload: ask once
      ui.picked.clear(); send('uploadOldGames', { ids }); draw(); return;
    }
    if (t.dataset.oldFormat && ui.dialog?.kind === 'oldUpload') { ui.dialog.format = t.dataset.oldFormat; draw(); return; }
    if (t.hasAttribute('data-confirm-old-upload') && ui.dialog?.kind === 'oldUpload') { const { ids, format } = ui.dialog; ui.dialog = null; ui.picked.clear(); send('uploadOldGames', { ids, format }); draw(); return; }
    if (t.dataset.captureMethod) { send('setCaptureMethod', {method:t.dataset.captureMethod}); return; }
    if (t.dataset.setChoice) { send('setSetting', {key:t.dataset.setChoice,value:t.dataset.choice === 'true'}); return; }
    if (t.dataset.set) { send('setSetting', { key: t.dataset.set, value: t.getAttribute('aria-checked') !== 'true' }); return; }
    if (t.dataset.cmd) { ui.menu = null; if(t.dataset.cmd === 'finishFirstRun') {ui.setupOpen=false;ui.simple=false;ui.route='home';location.hash='home';} (WINDOW_ONLY.has(t.dataset.cmd) ? Bridge.send : send)(t.dataset.cmd, JSON.parse(t.dataset.args || '{}')); draw(); }
  });
  document.addEventListener('change', (e) => {
    const t = e.target;
    if(t.hasAttribute('data-start-current-save') && ui.dialog?.kind==='livestream') {ui.dialog.startCurrentSave=t.checked;draw();return;}
    if(t.hasAttribute('data-stream-duration') && ui.dialog?.kind==='livestream') {ui.dialog.hours=+t.value;lastOverlayHtml=welcome()+dialogHtml();return;}
    if (t.dataset.setSelect) send('setSetting', { key: t.dataset.setSelect, value: t.dataset.setSelect === 'uploadTiming' ? t.value : +t.value });
    if (t.dataset.scenario !== undefined) { ui.step = 0; ui.dialog = null; Bridge.scenario(t.value); }
  });
  document.addEventListener('keydown', (e) => {
    const modal = document.querySelector('.dialog') ?? document.querySelector('.welcome');
    if (modal && e.key === 'Tab') {
      const focusable = [...modal.querySelectorAll('button:not(:disabled),select:not(:disabled),input:not(:disabled),textarea:not(:disabled),summary')].filter(el => el.getClientRects().length);
      const first = focusable[0], last = focusable.at(-1);
      if (!modal.contains(document.activeElement) || document.activeElement.id === 'setup-title' || (e.shiftKey && document.activeElement === first) || (!e.shiftKey && document.activeElement === last)) {
        (e.shiftKey ? last : first)?.focus(); e.preventDefault();
      }
    }
    if ((document.activeElement?.dataset.setChoice || document.activeElement?.dataset.captureMethod || document.activeElement?.dataset.memoryChoice !== undefined || document.activeElement?.dataset.uploadFormat !== undefined || document.activeElement?.dataset.oldFormat !== undefined) && ['ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(e.key)) {
      if (document.activeElement.getAttribute('aria-disabled') === 'true') e.preventDefault();   // its choice is still being saved
      else {
        const choices = [...document.activeElement.closest('[role=radiogroup]').querySelectorAll('[role=radio]:not(:disabled):not([aria-disabled="true"])')];
        const direction = ['ArrowUp','ArrowLeft'].includes(e.key) ? -1 : 1;
        const other = choices[(choices.indexOf(document.activeElement)+direction+choices.length)%choices.length];
        if (other) {other.focus();other.click();e.preventDefault();}
      }
    }
    if (e.key === 'Escape') { if (ui.menu) { const k = ui.menu; ui.menu = null; draw(); document.querySelector(`[data-menu="${k}"]`)?.focus(); } else if (ui.dialog) { ui.dialog = null; draw(); } else if (ui.route === 'checks') navigate('home'); }
    if (ui.menu && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
      const items = [...document.querySelectorAll('.menu button')], i = items.indexOf(document.activeElement);
      items[(i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus(); e.preventDefault();
    }
  });

  // ---- the program's messages -----------------------------------------------------------------------------------------------
  window.addEventListener('hashchange', () => { const r = location.hash.slice(1) || 'home'; if (r !== ui.route) { ui.route = r; draw(); } });
  Bridge.on((msg) => {
    if (msg.type === 'state') { ui.S = msg.state; if (choosing()) stateWaiting = true; else draw(); window.__ready = true; }
    else if (msg.type === 'result') {
      if (msg.cmd === ui.pending) {ui.pending=null;clearTimeout(pendingTimer);}
      if (msg.cmd === 'simpleSetup' && msg.toast?.kind === 'good') ui.simple = true;
      if(msg.toast) ui.feedback=msg.toast;
      else if(ui.feedback?.kind === 'pending' && !ui.pending) {
        const labels = {setSetting:'Option saved',setCaptureMethod:'Recording method saved',setMemoryLogging:'Memory logging preference saved',setUploadFormat:'Upload method saved',simpleSetup:'Simple setup chosen',pauseGame:'Recording control applied',openFolder:'Opened recordings folder',openSite:'Opened in your browser',finishFirstRun:'Setup complete',checkUpdates:'Checking for updates',scanOldGames:'Looking for games',updateNow:'Opened download page',updateLater:'Update reminder postponed'};
        ui.feedback = msg.dialog ? null : {kind:'good',title:labels[msg.cmd] ?? 'Request received',text:'The recorder has confirmed your action.'};
      }
      if(msg.cmd==='deleteGame' && pendingDeletion) {
        if(ui.feedback && ['good','info'].includes(ui.feedback.kind)) {
          const remaining=ui.S.games?.length??0;
          ui.feedback={...ui.feedback,text:`${pendingDeletion.label?pendingDeletion.label+'. ':''}${ui.feedback.text??''} ${remaining} recording${remaining===1?'':'s'} remain in your list.`};
        }
        pendingDeletion=null;
      }
      if(msg.dialog) ui.dialog=msg.dialog;
      draw();
    }
  });
  // for the pictures and the program (ShellForm, Program): open a dialog / a first-start step / a menu / a screen
  return {
    start: () => Bridge.start(),
    open: (kind) => { ui.dialog = { kind }; draw(); },
    result: (d) => { ui.dialog = d; draw(); },
    simple: () => { ui.setupOpen = true; ui.simple = true; ui.reviewReturn = false; ui.dialog = null; draw(); },
    step: (n) => { ui.simple = false; ui.step = Math.max(0, Math.min(SETUP_STEPS.length - 1, n)); draw(); },
    setup: (n = 0) => { ui.setupOpen = true; ui.simple = false; ui.reviewReturn = false; ui.dialog = null; ui.step = Math.max(0, Math.min(SETUP_STEPS.length - 1, n)); draw(); },
    menu: (k) => { ui.menu = k; draw(); },
    go: navigate,
    get state() { return ui.S; },
  };
})();
App.start();
