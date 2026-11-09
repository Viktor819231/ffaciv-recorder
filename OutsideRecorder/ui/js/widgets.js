// The small pieces every screen of the recorder window is built from: buttons, menus, switches, badges, choice cards, and
// the readers of the state several screens share.
// Reads the shared window state `ui` (js/app.js); the files load in the order of index.html.
'use strict';
// A control whose command waits for its answer: shown busy but kept focusable (a disabled control drops the keyboard focus);
// the click handler ignores it.
const busy = (on) => on ? 'aria-disabled="true" aria-busy="true"' : '';
const btn = (label, cmd, args, cls = '', extra = '') =>
  `<button class="btn ${cls}" data-cmd="${cmd}" data-args="${attr(args)}" data-k="${esc(cmd + JSON.stringify(args ?? {}))}" ${busy(ui.pending === cmd)} ${extra}>${label}</button>`;
const go = (label, to, cls = '') => `<button class="btn ${cls}" data-go="${to}" data-k="go-${to}">${label}</button>`;
function dropdown(key, label, items, cls = '', chevron = true) {
  const open = ui.menu === key;
  return `<span class="menu-wrap"><button class="btn ${cls}" data-menu="${key}" data-k="menu-${key}" aria-haspopup="menu" aria-expanded="${open}" ${chevron ? '' : 'aria-label="More"'}>${label}${chevron ? ' ' + icon('chevronDown') : ''}</button>${open ? `<div class="menu" role="menu">${items.map((it) =>
    `<button role="menuitem" data-cmd="${it.cmd}" data-args="${attr(it.args)}" data-k="${esc('menu-' + it.cmd + JSON.stringify(it.args ?? {}))}">${esc(it.label)}${it.hint ? `<span>${esc(it.hint)}</span>` : ''}</button>`).join('')}</div>` : ''}</span>`;
}
// One big choice of the setup and its dialogs: a radio (selected true/false) with a tick and "Selected", or (selected left
// out) a plain button with a chevron. attrs: its data-* attributes, data-k and busy()/disabled.
function choiceCard({ selected = null, title, text, tag = '', attrs = '', cls = '', label = true }) {
  const radio = selected !== null;
  return `<button class="setup-choice simple-choice${cls ? ' ' + cls : ''}${selected ? ' selected' : ''}"${radio ? ` role="radio" aria-checked="${selected}"` : ''} ${attrs}><span class="choice-radio">${radio ? selected ? icon('check') : '' : icon('chevron')}</span><span class="choice-body"><b>${title}${tag ? `<span class="method-tag">${tag}</span>` : ''}</b><small>${text}</small></span>${selected && label ? '<span class="choice-selected">Selected</span>' : ''}</button>`;
}
// a ticked or empty box with its words and a smaller line under them (the dialogs)
const checkRow = (attrs, on, title, text) => `<li><label><input type="checkbox" ${on ? 'checked' : ''} ${attrs}><span>${title}<small>${text}</small></span></label></li>`;
// opens the diagnostic report dialog; waits while the recorder still prepares the last report
const reportButton = (key, cls = '', label = 'Save diagnostic report', sign = '') => `<button class="btn ${cls}" data-dialog="report" data-k="${key}" ${ui.S.reportBusy ? 'disabled' : ''}>${sign ? icon(sign) + ' ' : ''}${ui.S.reportBusy ? 'Preparing report…' : label}</button>`;
// a recording's title line: its mod, when it was recorded, its turns (and more), then its badges
const gameTop = (g, more, badges) => `<div class="game-top">${modTag(g)}<h3>${esc(day(recordingTime(g)))} · ${esc(clock(recordingTime(g)))}${turnsLabel(g) ? ` · ${esc(turnsLabel(g))}` : ''}${more}</h3>${badges}</div>`;
const modTag = (g) => `<span class="mod">${esc(g.mod)}${g.modVersion ? ' ' + esc(g.modVersion) : ''}</span>`;
const playerChips = (ps) => `<div class="players">${ps.map((p) => `<span class="player${p.you ? ' you' : ''}" title="${esc(p.civ)}"><i style="--c:${esc(p.color)};--k:${esc(p.k ?? '#000')}"></i>${esc(p.you ? 'You' : p.name)} · ${esc(p.civ)}</span>`).join('')}</div>`;
// a recording's badge: the game list's status (a recording on this PC, or a replay on the site) in plain words
const STATUS = {
  restricted: ['locked', 'Replay locked'], processing: ['sending', 'Generating replay'], recording: ['recording', 'Recording'], sending: ['sending', 'Sending'],
  locked: ['locked', 'Private'], public: ['public', 'Public'], streamed: ['public', 'Saves uploaded'], local: ['local', 'Saved on this PC'],
};
const badge = (st) => { const [c, t] = (ui.S?.testBuild && st === 'sending') ? ['sending', `Sending to ${ui.S.serverShort ?? 'the server'}`] : STATUS[st] ?? ['', st]; return `<span class="badge ${c}">${t}</span>`; };
const problems = () => (ui.S.checks ?? []).filter((c) => c.level === 'warn' || c.level === 'bad');
// the checks, worst first, each with its sign (Home's glance card and the Checks screen)
const CHECK_ORDER = { bad: 0, warn: 1, info: 2, ok: 3 };
const sortedChecks = () => [...(ui.S.checks ?? [])].sort((a, b) => CHECK_ORDER[a.level] - CHECK_ORDER[b.level]);
const checkIcon = (c) => icon({ ok: 'checkCircle', warn: 'alert', bad: 'xCircle', info: 'info' }[c.level], 'state-ic ' + c.level);
function uploadTimingChoice() {
  const during=ui.S.settings?.uploadTiming==='during';
  return `<div class="set"><div class="set-text"><b id="l-upload-timing">Upload and process saves</b><span>Replay visibility and private livestreams are separate choices.</span></div><select class="pick" data-set-select="uploadTiming" aria-labelledby="l-upload-timing" data-k="upload-timing"><option value="after" ${during?'':'selected'}>After the game</option><option value="during" ${during?'selected':''}>While playing</option></select></div><p class="small">${during?'Upload your current recording as saves arrive. Resource limits and per-game upload holds still apply.':'Saves upload once Civ is closed. The replay then stays hidden on ffaciv.com for 2 hours, so you can keep it hidden or delete it.'}</p>`;
}
function inlineUploadMethod() {
  const selected=ui.S.settings?.saveUpload==='full'?'full':'delta';
  return `<fieldset class="inline-upload-method"><legend>Upload method</legend><div role="radiogroup" aria-label="Upload method">${[['delta','Local preparation','Smaller uploads · recommended'],['full','Whole save files','Processed on the website']].map(([format,title,detail])=>`<button class="inline-upload-choice ${selected===format?'selected':''}" role="radio" aria-checked="${selected===format}" data-cmd="setUploadFormat" data-args="${attr({format})}" data-k="inline-format-${format}" ${busy(ui.pending==='setUploadFormat')}><span class="inline-choice-dot" aria-hidden="true">${selected===format?icon('check'):''}</span><span><b>${title}</b><small>${detail}</small></span></button>`).join('')}</div></fieldset>`;
}
const laterMenu = (key) => dropdown(key, 'Later', [
  { label: 'Tonight', hint: '22:00', cmd: 'updateLater', args: { when: 'tonight' } },
  { label: 'Tomorrow', cmd: 'updateLater', args: { when: 'tomorrow' } },
  { label: 'Next time it starts', cmd: 'updateLater', args: { when: 'next' } },
]);
const sw = (key, on, label, sub, disabled = false) => `<div class="set"><div class="set-text"><b id="l-${key}">${label}</b>${sub ? `<span>${sub}</span>` : ''}</div><button class="switch" role="switch" aria-checked="${!!on}" aria-labelledby="l-${key}" data-set="${key}" data-k="sw-${key}" ${disabled ? 'disabled' : busy(ui.pending === 'setSetting')}></button></div>`;
function memoryLoggingState() {
  const s = ui.S.settings ?? {}, m = ui.S.memoryLogging ?? {};
  return {enabled:m.enabled ?? (s.trackLogs === true),canEnable:m.canEnable ?? (s.recording !== false),reason:m.reason ?? (s.recording === false ? 'Turn automatic recording on first.' : 'Needs a supported Civ build. Resource controls still apply.'),readsPerSecond:m.readsPerSecond ?? s.looksPerSecond ?? 4,cityManagementIntervalMs:m.cityManagementIntervalMs ?? 1000,turnQuietMs:m.turnQuietMs ?? 5000};
}
function memoryCadenceDetails() {
  const m = memoryLoggingState();
  return `<p class="small">Movement reads: ${esc(m.readsPerSecond)} per second. City management: every ${esc(m.cityManagementIntervalMs/1000)} second(s). Reads pause for ${esc(m.turnQuietMs/1000)} seconds after an observed turn change; this reacts to a change already observed and does not predict the start of a transition.</p>`;
}
