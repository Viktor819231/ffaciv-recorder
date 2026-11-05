// The FFACiv Recorder window: Home, Games, Requests, Settings, Help (+ Updates, Checks, first start, dialogs).
// It only draws the state the program sends (bridge.js) and sends commands back; it decides nothing itself.
'use strict';
const App = (() => {
  let S = null;              // the latest state
  let route = (location.hash.slice(1) || 'home');
  let dialog = null;         // { kind, ... } shown over the screen
  let menu = null;           // the open drop-down: its key
  let setupOpen = new URLSearchParams(location.search).has('setup');
  let feedback = null;
  let pending = null;
  let pendingDeletion = null;
  let pendingTimer = null;
  let step = 0;              // first start: 0..2
  let gamesFilter = 'all';
  const picked = new Set();  // "Upload old games": the games ticked
  const $ = (sel) => document.querySelector(sel);
  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  const attr = (o) => esc(JSON.stringify(o ?? {}));

  // ---- time in plain words ------------------------------------------------------------------------------------------
  function span(ms) {
    const m = Math.max(0, Math.round(ms / 60e3));
    if (m < 1) return 'less than a minute';
    if (m < 60) return `${m} min`;
    const h = Math.floor(m / 60), r = m % 60;
    if (h < 24) return r ? `${h} h ${r} min` : `${h} h`;
    const d = Math.round(h / 24); return d === 1 ? '1 day' : `${d} days`;
  }
  const until = (t) => t - Date.now() <= 0 ? 'any moment now' : 'in ' + span(t - Date.now());
  const ago = (t) => Date.now() - t < 60e3 ? 'just now' : span(Date.now() - t) + ' ago';
  const day = (t) => {
    const d = new Date(t), today = new Date(); const y = new Date(); y.setDate(today.getDate() - 1);
    if (d.toDateString() === today.toDateString()) return 'Today';
    if (d.toDateString() === y.toDateString()) return 'Yesterday';
    return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
  };
  const clock = (t) => new Date(t).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  function recordingTime(g) {
    const m=/^session-(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})/.exec(g.id??'');
    return m ? new Date(+m[1],+m[2]-1,+m[3],+m[4],+m[5],+m[6]).getTime() : g.date??g.startedAt??g.lastSaveAt;
  }
  const recordingLabel = g => Number.isFinite(new Date(recordingTime(g)).getTime()) ? `${g.mod??'Recording'} · ${day(recordingTime(g))} · ${clock(recordingTime(g))}` : `${g.mod??'Current'} recording`;
  // "T30–144": the turns a recording holds saves of (the user, 2026-10-31: "say what turns we have recordings of")
  const turnsLabel = (g) => g.firstTurn != null && g.lastTurn != null ? (g.firstTurn === g.lastTurn ? `T${g.lastTurn}` : `T${g.firstTurn}–${g.lastTurn}`)
    : g.turns != null ? `${g.turns} ${g.turns === 1 ? 'turn' : 'turns'}` : '';
  const turnsGap = (g) => g.firstTurn != null && g.savedTurns && g.lastTurn - g.firstTurn + 1 > g.savedTurns ? g.lastTurn - g.firstTurn + 1 - g.savedTurns : 0;

  // ---- small pieces --------------------------------------------------------------------------------------------------
  const btn = (label, cmd, args, cls = '', extra = '') =>
    `<button class="btn ${cls}" data-cmd="${cmd}" data-args="${attr(args)}" data-k="${esc(cmd + JSON.stringify(args ?? {}))}" ${pending === cmd ? 'disabled aria-busy="true"' : ''} ${extra}>${label}</button>`;
  const go = (label, to, cls = '') => `<button class="btn ${cls}" data-go="${to}" data-k="go-${to}">${label}</button>`;
  function dropdown(key, label, items, cls = '', chevron = true) {
    const open = menu === key;
    return `<span class="menu-wrap"><button class="btn ${cls}" data-menu="${key}" data-k="menu-${key}" aria-haspopup="menu" aria-expanded="${open}" ${chevron ? '' : 'aria-label="More"'}>${label}${chevron ? ' ' + icon('chevronDown') : ''}</button>${open ? `<div class="menu" role="menu">${items.map((it) =>
      `<button role="menuitem" data-cmd="${it.cmd}" data-args="${attr(it.args)}">${esc(it.label)}${it.hint ? `<span>${esc(it.hint)}</span>` : ''}</button>`).join('')}</div>` : ''}</span>`;
  }
  const modTag = (g) => `<span class="mod">${esc(g.mod)}${g.modVersion ? ' ' + esc(g.modVersion) : ''}</span>`;
  const playerChips = (ps) => `<div class="players">${ps.map((p) => `<span class="player${p.you ? ' you' : ''}" title="${esc(p.civ)}"><i style="--c:${esc(p.color)};--k:${esc(p.k ?? '#000')}"></i>${esc(p.you ? 'You' : p.name)} · ${esc(p.civ)}</span>`).join('')}</div>`;
  function liveLinkRow() {
    const l = S.livestream;
    if (!l?.active) return '';
    let address = '';
    if (l.url) {try {const u = new URL(l.url); address = u.host + u.pathname;} catch {address = l.url;}}
    const expires = l.expiresAt ? new Date(l.expiresAt).getTime() : null;
    const label = l.problem ? 'Needs attention' : l.starting || !l.url ? 'Preparing' : l.status==='waiting' ? 'Waiting' : 'Ready';
    return `<div class="stream-active"><div class="stream-lobby-heading">${icon('requests')}<div><div class="eyebrow">PRIVATE LIVESTREAM</div><h3>${l.scope==='player'?'Your player link':'This game’s stream'}</h3></div><span class="badge ${l.url ? 'public' : 'countdown'}">${label}</span></div><p class="stream-privacy">${icon('lock')}Anyone with the invite can watch · Not publicly listed</p><p class="small">${l.scope==='player'?'One link follows your recordings through rehosts and new games. Viewers choose when to switch.':'This link stays with this game.'}${expires && Number.isFinite(expires)?` Expires <span data-until="${expires}">${until(expires)}</span>.`:''}</p>${l.url ? `<button class="stream-lobby-address" data-cmd="openSite" data-args="${attr({url:l.url})}" data-k="stream-lobby-address"><span>${esc(address)}</span>${icon('external')}</button><div class="row actions">${btn(`${icon('external')} Open livestream`, 'openSite', {url:l.url}, 'primary')}${btn(`${icon('link')} Copy invite link`, 'copyLiveLink', {})}</div>` : '<p class="small">Confirming private access. Your invite appears when ready.</p>'}${l.problem ? `<p class="stream-problem" role="status">${esc(l.problem)}</p>` : ''}${l.viewers != null ? `<p class="small">${l.viewers} watching</p>` : ''}</div>`;
  }
  const STATUS = {
    restricted: ['locked', 'Replay locked'], processing: ['sending', 'Generating replay'], recording: ['recording', 'Recording'], sending: ['sending', 'Sending'], queued: ['queued', 'Waiting for internet'],
    locked: ['locked', 'Private'], countdown: ['countdown', 'Going public'], public: ['public', 'Public'],
    streamed: ['public', 'Saves uploaded'], withdrawn: ['withdrawn', 'Withdrawn'], local: ['local', 'Saved on this PC'],

  };
  const badge = (st) => { const [c, t] = (S?.testBuild && st === 'sending') ? ['sending', `Sending to ${S.serverShort ?? 'the server'}`] : STATUS[st] ?? ['', st]; return `<span class="badge ${c}">${t}</span>`; };
  // the test build (card OR03): parts that come later keep their screens, with this note
  const notInTest = (what) => S?.testBuild ? `<div class="banner">${icon('info')}<div><h3>${S.site ? 'Not in this version yet' : 'Not in the test build'}</h3><p>${what}</p></div></div>` : '';
  const kb = (n) => n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(0, Math.round(n / 1024))} KB`;
  const postponeMenu = (key, game) => dropdown(key, 'Postpone', [
    { label: 'By 1 day', cmd: 'postpone', args: { game, hours: 24 } },
    { label: 'By 1 week', cmd: 'postpone', args: { game, hours: 168 } },
    { label: 'Until I make it public', cmd: 'postpone', args: { game, hours: 0 } },
  ]);
  const problems = () => (S.checks ?? []).filter((c) => c.level === 'warn' || c.level === 'bad');
  const delayWords = (h) => h === 0 ? 'only when you make it public' : h >= 24 ? `${h / 24 === 1 ? '1 day' : h / 24 + ' days'} after the game ends` : `${h} h after the game ends`;

  // ---- Home -----------------------------------------------------------------------------------------------------------
  const KIND_ICON = { recording: 'record', sending: 'up', waiting: 'wait', found: 'checkCircle', unsupported: 'alert', attention: 'alert', paused: 'pause', starting: 'wait' };
  function uploadTimingChoice() {
    const during=S.settings?.uploadTiming==='during';
    return `<div class="set"><div class="set-text"><b id="l-upload-timing">Upload and process saves</b><span>Replay visibility and private livestreams are separate choices.</span></div><select class="pick" data-set-select="uploadTiming" aria-labelledby="l-upload-timing" data-k="upload-timing"><option value="after" ${during?'':'selected'}>After the game</option><option value="during" ${during?'selected':''}>While playing</option></select></div><p class="small">${during?'Upload your current recording as saves arrive. Resource limits and per-game upload holds still apply.':'Saves upload when you leave the game: Civ\'s main menu or Civ closed, not a staging lobby. The replay stays hidden on ffaciv.com for 2 hours, so you can keep it hidden or delete it.'}</p>`;
  }

  function inlineUploadMethod() {
    const selected=S.settings?.saveUpload==='full'?'full':'delta';
    return `<fieldset class="inline-upload-method"><legend>Upload method</legend><div role="radiogroup" aria-label="Upload method">${[['delta','Local preparation','Smaller uploads · recommended'],['full','Whole save files','Processed on the website']].map(([format,title,detail])=>`<button class="inline-upload-choice ${selected===format?'selected':''}" role="radio" aria-checked="${selected===format}" data-cmd="setUploadFormat" data-args="${attr({format})}" data-k="inline-format-${format}" ${pending==='setUploadFormat'?'disabled':''}><span class="inline-choice-dot" aria-hidden="true">${selected===format?icon('check'):''}</span><span><b>${title}</b><small>${detail}</small></span></button>`).join('')}</div></fieldset>`;
  }
  function sessionControls() {
    const s = S.settings ?? {}, on = s.recording !== false, memory = memoryLoggingState();
    return `<section class="card settings session-controls"><h3>Recording options</h3>${sw('trackLogs',memory.enabled,'Player-action logs','Experimental · capture detail between saves',!on || (!memory.enabled && !memory.canEnable))}${sw('stream',s.stream!==false,'Automatic uploads',s.uploadTiming==='during'?'Upload and process while playing':'Upload after your game · public 2 hours later')}${sw('recordSinglePlayer',s.recordSinglePlayer,'Include single-player games','',!on)}${uploadTimingChoice()}${inlineUploadMethod()}${streamingShortcut()}<div class="row actions">${go('All options','settings','quiet')}</div></section>`;
  }
  function currentSaveStreamAction() {
    const l=S.livestream??{};
    return l.active && l.waitingForGame && S.current?.context && !S.current?.lobby && !S.current?.ended
      ? '<button class="btn primary" data-dialog="livestream" data-k="start-current-save">Stream current save</button>' : '';
  }
  function gameStreamActions(g) {
    if (!g.context || g.context !== S.current?.context || S.current?.ended || S.current?.lobby) return '';
    const l = S.livestream ?? {};
    if (l.active) return currentSaveStreamAction() + (l.url
      ? `${btn(`${icon('external')} Open livestream`, 'openSite', {url:l.url})}${btn(`${icon('link')} Copy stream link`, 'copyLiveLink', {}, 'primary')}`
      : '<button class="btn quiet" disabled>Preparing stream link…</button>');
    return l.canStart === true ? '<button class="btn" data-dialog="livestream" data-k="game-stream-link">Get livestream link</button>' : '';
  }
  function liveSessionControl() {
    const l = S.livestream ?? {};
    return `<div class="recording-stream-options"><h3>Private livestream <span class="badge plain">Experimental</span></h3><p class="small">You choose when to enable sharing with a private invite. Anyone with the link can watch; it is not publicly listed.</p>${l.active ? liveLinkRow()+currentSaveStreamAction()+`<div class="row actions">${btn('Disable livestream','stopLivestream',{},'danger')}</div>` : `<div class="row actions"><button class="btn primary" data-dialog="livestream" data-k="get-livestream-link" >${icon('link')} Enable livestream</button></div>${l.canStart===true?'':`<p class="small" role="status">${esc(l.setupReason??(l.needsSetup?'Enable to review the required recording settings and prepare your invite.':'Prepare your invite now. Viewers can wait for your next game.'))}</p>`}`}<details class="stream-explainer"><summary>How it works &amp; safety</summary><p class="small">Starting a private stream sends this game while you play, even if normal uploads wait until after the game. Viewers download from the website; more viewers do not add uploads from your PC.</p><p class="small">Experimental player-action logs use read-only access outside Civ. They do not inject code or change game memory. Resource protection can pause work, so the stream may be delayed.</p><p class="small">Invite links expire and can be forwarded. Disable livestream ends the invite. Streaming stays off after Recorder restarts until you start it again.</p></details></div>`;
  }
  function crashDiagnosticsCard() {
    const enabled = S.settings?.diagnostics === true, d = enabled ? S.crashDiagnostics ?? {} : {}, crashes = d.crashes ?? [];
    return `<section class="card crash-diagnostics"><h3>Crash reports &amp; diagnostics</h3>${sw('diagnostics',enabled,'Collect diagnostic history','Optional · local reports only')}<p class="small">${enabled?'Collecting resource history.':'Off — no new diagnostic history is collected.'} Resource protection still runs to keep recording considerate.</p><p class="small">Game crashed? Check recent Windows crash records and save a local report with Recorder activity and resource usage.</p><div class="row actions">${btn(d.busy?'Checking…':'Check crash records','checkCrashRecords',{},'',d.busy||!enabled?'disabled':'')}<button class="btn quiet" data-dialog="report" data-k="crash-report" ${S.reportBusy?'disabled':''}>${S.reportBusy?'Preparing report…':'Save diagnostic report'}</button></div>${d.status?`<p class="small" role="status">${esc(d.status)}</p><p class="small">Checked ${esc(new Date(d.checkedAt).toLocaleString())} · last 24 hours</p>`:''}${crashes.length?`<details><summary>${crashes.length} Civ crash record${crashes.length===1?'':'s'}</summary>${crashes.map(c=>`<p class="small"><b>${esc(new Date(c.at).toLocaleString())}</b><br>${esc(c.application)}<br>Fault module: ${esc(c.module??'Unknown')}<br>Exception: ${esc(c.exceptionCode??'Unknown')}</p>`).join('')}</details>`:''}<p class="small">Reports stay on this PC until you share them. Crash records can provide clues; they do not establish the cause.</p>${go('Resource usage','checks','quiet')}</section>`;
  }
  function home() {
    const st = S.status ?? { kind: 'starting', title: 'Starting…' };
    const p = problems(), up = S.updates?.available;
    let left = '', right = '', recordings = '';
    const settings = S.settings ?? {};
    if (up?.required) left += `<div class="banner bad">${icon('download')}<div><h3>Please update to keep recording</h3><p>This version can no longer send games to ffaciv.com. Your games are kept on this PC and sent after the update.</p><div class="row">${btn(`${icon('download')} Download update`, 'updateNow', {}, 'primary')}${go('What changes', 'updates', 'quiet')}</div></div></div>`;
    const on = settings.recording !== false;
    const active = S.current && !S.current.ended && !S.current.lobby;
    const detected = S.civDetected === true || S.current?.lobby === true;
    const idleTitle = detected ? S.current?.lobby ? 'In the multiplayer lobby' : ['unsupported','attention','paused'].includes(st.kind) ? st.title : 'Civ detected · waiting for a game' : 'Waiting for Civ';
    const kind = !on ? 'paused' : !active ? detected ? 'found' : 'waiting' : S.current.paused ? 'paused' : st.kind;
    const title = !on ? 'Recorder is off' : !active ? idleTitle : S.current.paused ? 'Recording paused' : S.resources?.paused ? 'Recording paused · checking resources' : st.title;
    const detail = !on ? 'Turn it on to detect and record your next game.' : !active ? detected ? S.current?.lobby ? 'Create a private invite now. Recording follows when the game starts.' : st.detail ?? 'Start or load a game. Save-only recording begins with the first autosave.' : 'Keep the recorder on. Your next game is detected automatically.' : st.detail;
    left += `<section class="frame recording-hero" aria-live="polite"><div class="eyebrow">${active && on && !S.current.paused && !S.resources?.paused ? 'YOUR GAME IS BEING SAVED' : on ? 'RECORDER ON' : 'RECORDER OFF'}</div><div class="hero"><div class="ring ${esc(kind)}">${icon(KIND_ICON[kind] ?? 'wait')}</div><div class="hero-text"><h1 class="hero-title">${esc(title)}</h1><p class="hero-detail">${esc(detail)}</p></div></div><div class="row actions">${btn(on ? `${icon('pause')} Turn recorder off` : `${icon('record')} Turn recorder on`, 'setSetting', {key:'recording',value:!on}, on ? '' : 'primary')}${active && on ? btn(S.current.paused ? 'Resume this game' : 'Pause this game','pauseGame',{},'quiet') : ''}${btn('Minimize to tray', 'hideWindow', {}, 'quiet')}</div>${!on ? '<p class="recorder-idle-note">Leaving it on is lightweight: it waits for Civ between checks, then records when a game is detected.</p>' : ''}<div class="recording-summary">${icon('save')}<span>${on ? 'Temporary save copies on this PC' : 'No new save copies'} <span class="summary-sep">·</span> ${settings.stream === false ? 'Upload when you choose' : settings.uploadTiming==='during' ? 'Upload while playing' : 'Upload after your game'}${S.livestream?.active ? ' · Private stream on' : ''}</span></div>${st.action ? `<div class="row actions">${btn(esc(st.action.label), st.action.cmd, st.action.args ?? {}, 'primary')}</div>` : ''}</section>`;
    for (const r of S.requests ?? []) left += `<div class="banner gold">${icon('requests')}<div><h3>${esc(r.who)} wants to watch you live</h3><p>${esc(r.role === 'caster' ? 'A caster' : 'Someone')} asks to see this game from your side, as you see it.</p><div class="row">${btn('Allow for this game', 'allowRequest', { id: r.id, scope: 'game' }, 'primary')}${btn('Decline', 'declineRequest', { id: r.id })}${go('More…', 'requests', 'quiet')}</div></div></div>`;
    if (S.oldGames?.newCount && !S.oldHintDismissed && !S.current && !(S.games?.length)) left += `<div class="banner">${icon('games')}<div><h3>${S.oldGames.newCount === 1 ? '1 older game' : `${S.oldGames.newCount} older games`} on this PC</h3><p>Civ's autosaves of games you played before. You can upload them whole, so they can be watched too.</p><div class="row">${go('Look at them', 'old', 'primary')}${btn('Not now', 'dismissOldHint', {}, 'quiet')}</div></div></div>`;
    if (S.current) recordings += currentGame(S.current);
    const available = (S.games ?? []).filter(g => !S.current || (g.context !== S.current.context && g.status !== 'recording'));
    const newest = [...available, ...heldCards().filter(g => !S.current || g.context !== S.current.context)].sort((a, b) => (recordingTime(b) ?? 0) - (recordingTime(a) ?? 0));
    const recent = newest.slice(0, S.current && !S.current.lobby ? 1 : 2);
    if (recent.length) recordings += `<div class="recordings-list-heading"><div><div class="group-label">Recent recordings</div><span class="small">The newest ${recent.length === 1 ? 'one' : 'two'} · the rest are under Previous games</span></div>${go(`Previous games (${available.length})`,'games','quiet')}</div>${recent.map(g => g.held ? deletedCard(g) : gameCard(g)).join('')}`;
    right += setupChecksCard() + sessionControls() + crashDiagnosticsCard();
    if (up && !up.required && !up.later) right += `<div class="banner">${icon('sparkle')}<div><h3>Update available · ${esc(up.version)}</h3><p>${esc(up.notes?.[0] ?? 'A new recorder version is available')}${up.notes?.length > 1 ? ' and more' : ''}.</p><div class="row">${btn('Download update', 'updateNow', {}, 'primary')}${laterMenu('later-home')}</div></div></div>`;
    return `<div class="recorder-layout"><div class="col recorder-main">${left}</div><div class="col recorder-controls">${right}</div><div class="col recorder-recordings">${recordings}</div></div>`;
  }
  function setupChecksCard() {
    const p=problems();
    const order = { bad: 0, warn: 1, info: 2, ok: 3 }, ic = { ok: 'checkCircle', warn: 'alert', bad: 'xCircle', info: 'info' };
    return `<section class="card glance"><button class="glance-head" data-go="checks" data-k="health"><h3>${p.length ? (p.length === 1 ? '1 thing needs attention' : `${p.length} things need attention`) : 'Everything is fine'}</h3>${icon('chevron', 'chev')}</button>${[...(S.checks ?? [])].sort((a, b) => order[a.level] - order[b.level]).map((c) => `<button class="glance-row" data-go="checks">${icon(ic[c.level], 'state-ic ' + c.level)}<span>${esc(c.title)}</span></button>`).join('')}</section>`;
  }
  function streamingShortcut() {
    const l=S.livestream??{};
    return `<div class="recording-stream-options"><h3>Private livestream <span class="badge plain">${l.active?'Enabled':'Off'}</span></h3><p class="small">${l.active?'Manage your follow link and current stream.':'Prepare a private follow link before your next game, or start from the current save.'}</p>${go('Open Streaming','streaming','quiet')}</div>`;
  }
  function streamTechnicalCard() {
    const l=S.livestream??{}, t=l.technical??{}, r=S.resources??{};
    const value=v=>v==null?'–':esc(v), bytes=v=>v==null?'–':kb(v);
    const flag=v=>v==null?'–':v?'Allowed':'Waiting';
    const metric=(label,v)=>`<div><dt>${label}</dt><dd>${v}</dd></div>`;
    return `<section class="card stream-technical"><h3>Transport status</h3><p class="small path">${value(t.server)}</p><dl class="stream-metrics">${metric('Sender',value(t.transportState))}${metric('Live slot',value(t.slot))}${metric('Local control gate',flag(t.controlAllowed))}${metric('Game payload',flag(t.payloadAllowed))}${metric('Save upload mode',value(t.saveUpload))}${metric('Latest recorded turn',value(S.current?.turn))}${metric('Saves queued',value(t.queuedSaves))}${metric('Event bytes queued',bytes(t.queuedEventBytes))}${metric('Last acknowledgement',t.lastSentAt?esc(new Date(t.lastSentAt).toLocaleTimeString()):'–')}</dl>${t.lastError?`<p class="stream-problem" role="status">${esc(t.lastError)}</p>`:''}${t.waitReason?`<p class="small" role="status">Work paused: ${esc(t.waitReason)}</p>`:''}<h3>Transfer counters</h3><p class="small">This sender session. Counters reset when the sender restarts.</p><dl class="stream-metrics">${metric('Full saves sent',value(t.fullSaves))}${metric('Delta saves sent',value(t.deltaSaves))}${metric('Acknowledged save data',bytes(t.saveBytes))}${metric('Acknowledged gzip events',bytes(t.eventBytes))}${metric('HTTP requests',value(t.requests))}${metric('Event chunks accepted',value(t.chunks))}${metric('Delta preparation',t.deltaCpuMs==null?'–':`${Math.round(t.deltaCpuMs)} ms`)}${metric('Log preparation',t.chunkCpuMs==null?'–':`${Math.round(t.chunkCpuMs)} ms`)}</dl><p class="small">Preparation figures are accumulated packing/diff elapsed time, not a CPU-utilization percentage. Transfer counters exclude HTTP/TLS overhead.</p><h3>Work limits</h3><dl class="stream-metrics">${metric('Upload pacing',t.uploadKibPerSecond==null?'–':t.uploadKibPerSecond===0?'Unpaced':`${t.uploadKibPerSecond} KiB/s`)}${metric('Upload mode',t.idleUploadMode?'Menu / closed · faster':'Gameplay · conservative')}${metric('Saves per private pass',value(t.saveBatchLimit))}${metric('Event chunks per pass',value(t.eventChunkLimit))}${metric('Max decoded chunk',bytes(t.eventChunkBytes))}${metric('Recorder CPU budget',typeof r.cpuBudgetPercent==='number'?`${r.cpuBudgetPercent}%`:'–')}${metric('Recorder CPU sample',typeof r.cpuPercent==='number'?`${r.cpuPercent.toFixed(1)}%`:'–')}${metric('Recorder host memory',bytes(r.memoryBytes))}${metric('Resource gate',value(r.reason))}</dl>${go('Resource details','checks','quiet')}</section>`;
  }
  function streaming() {
    const current=S.current;
    const ongoing=current && !current.ended ? currentGame(current) : `<section class="card prose"><h3>No active recording</h3><p>The follow invite can be prepared with Civ closed. Game transfer waits for an authorized recording.</p></section>`;
    return `<h2 class="screen-title">Streaming</h2>${problems().length?setupChecksCard():''}<div class="cols"><div class="col"><section class="card streaming-controls">${liveSessionControl()}</section><div><h3 class="group-label">${current?.lobby?'Current lobby':'Ongoing game'}</h3>${ongoing}</div><section class="card prose"><h3>Stream protocol</h3><p><b>Starting point:</b> the latest settled autosave is sent whole if needed. Older unsent saves stay local; existing server data and upload receipts are preserved. New saves use the selected full/delta mode.</p><p><b>Events:</b> JSONL observations are sent in gzip chunks with byte offsets and acknowledgements. Existing event progress is retained; retries reuse the same pending chunk.</p><p><b>Gates:</b> private-access acknowledgement and a matching live slot precede game payloads. Preparation passes are serialized, with resource and turn-quiet gates checked during work.</p><details><summary>Privacy and viewer behavior</summary><p>The invite follows your recordings until expiry. Anyone with it can watch or forward it; the stream is unlisted. Disable ends sharing without changing ordinary upload preferences. Recorder restarts require sharing to be enabled again.</p><p>The viewer draws recorded game data locally. It can lag behind capture. More viewers do not add uploads from your PC.</p></details></section></div><div class="col">${streamTechnicalCard()}</div></div>`;
  }
  const laterMenu = (key) => dropdown(key, 'Later', [
    { label: 'Tonight', hint: '22:00', cmd: 'updateLater', args: { when: 'tonight' } },
    { label: 'Tomorrow', cmd: 'updateLater', args: { when: 'tomorrow' } },
    { label: 'Next time it starts', cmd: 'updateLater', args: { when: 'next' } },
  ]);

  // the multiplayer staging room (OR03 "Lobby / channel"): who is in, their civs, the map and the timer; the channel link
  function lobbyCard(c) {
    const humans = c.players.filter((p) => p.human).length;
    const seated = c.players.filter((p) => p.team != null && p.name !== 'Open'), teams = new Set(seated.map((p) => p.team));
    let h = `<section class="card"><div class="game-head"><div style="flex:1;min-width:0"><h3>${modTag(c)} Multiplayer lobby</h3><div class="sub">${esc([c.gameName, `${humans} ${humans === 1 ? 'player' : 'players'} in`].filter(Boolean).join(' · '))}</div></div><span class="badge countdown">Lobby</span></div>`;
    const share=S.livestream??{};
    h += `<div class="row actions">${btn('Refresh lobby','refreshLobby',{},'quiet')}${share.active?`${btn('Copy invite link','copyLiveLink',{},'primary')}${btn('Stop livestream','stopLivestream',{},'danger')}`:`<button class="btn primary" data-dialog="livestream" data-k="get-livestream-link" ${share.canStart===true?'':'disabled'}>${icon('link')} Get livestream link</button>`}</div>`;
    h += `<div class="players">${c.players.map((p) => `<span class="player${p.you ? ' you' : ''}"><i style="--c:${esc(p.color)}"></i>${esc(p.you ? 'You' : p.name)} · ${esc(p.civ)}${teams.size > 1 && teams.size < seated.length && p.name !== 'Open' ? ` · team ${p.team + 1}` : ''}</span>`).join('')}</div>`;
    h += `<div class="facts"><div class="fact"><b>${esc(c.map ?? '–')}</b><span>Map${c.mapSize ? ` · ${esc(c.mapSize)}` : ''}</span></div><div class="fact"><b>${c.turnTimerSeconds != null ? `${Math.floor(c.turnTimerSeconds / 60)}:${String(c.turnTimerSeconds % 60).padStart(2, '0')}` : c.turnTimerOn ? 'On' : 'Off'}</b><span>Turn timer</span></div><div class="fact"><b>${c.options ?? '–'}</b><span>Options on</span></div></div>`;
    h += `<div class="publish"><div class="publish-line">${icon('record')}<span>Get a private follow link now. Viewers wait here until your game starts.</span></div>${share.active?liveLinkRow():share.canStart===true?'':`<p class="small">${esc(share.reason??'Enable memory logs for a supported lobby first.')}</p>`}</div>`;
    return h + '</section>';
  }

  function currentGame(c) {
    if (c.lobby) return lobbyCard(c);
    const pub = c.publish ?? {}, upl = c.upload ?? {};
    const status = c.paused ? 'local' : c.ended ? (upl.state === 'sending' ? 'sending' : pub.state === 'public' ? 'public' : pub.state === 'countdown' ? 'countdown' : 'locked') : 'recording';
    const sub = c.ended ? `Ended ${c.lastSaveAt ? ago(c.lastSaveAt) : ''}` : `${c.startedAt ? `Started ${clock(c.startedAt)}` : 'Game detected'}${c.lastSaveAt ? ` · last save ${ago(c.lastSaveAt)}` : ''}`;
    let h = `<section class="card"><div class="game-head"><div style="flex:1;min-width:0"><h3>${modTag(c)} ${esc(c.ended ? 'Your last game' : cap(c.mode ?? 'game'))}</h3><div class="sub">${esc(sub)}</div></div>${c.paused ? '<span class="badge withdrawn">Paused</span>' : badge(status)}</div>`;
    if (c.players?.length) h += playerChips(c.players);
    const sent = upl.state === 'off' ? 'Disabled' : upl.state === 'held' ? 'Waiting' : upl.state === 'queued' ? `${upl.queued} waiting` : upl.lastSentTurn != null ? `Turn ${upl.lastSentTurn}` : '–';
    h += `<div class="facts"><div class="fact"><b>${c.turn ?? '–'}</b><span>Turn</span></div><div class="fact"><b>${c.players?.length ?? '–'}</b><span>Players${c.cityStates ? ` · ${c.cityStates} city-states` : ''}</span></div><div class="fact" title="${esc(upl.note??'')}"><b>${esc(sent)}</b><span>${upl.state==='held'?'Upload timing':upl.state==='off'?'Uploads':upl.state === 'queued' ? 'Saves to send' : 'Sent up to'}</span></div></div>`;
    if (!S.live) h += `<div class="publish">${upl.target === 'test' ? streamBlock(upl) : publishBlock(c.id, pub, upl, c.ended)}</div>`;
    if (S.live) h += replayControls({...c, turns:c.turn});
    if (!c.ended) h += `<div class="row actions">${gameStreamActions(c)}${btn(`${icon('folder')} Open save folder`, 'openFolder', { game: c.id })}</div>`;
    return h + '</section>';
  }
  // the test build: the stream to the test server on this PC (connected / queued / sent; whole saves vs changes)
  function streamBlock(u) {
    if (u.state === 'off' || u.state === 'held') return `<div class="publish-line">${icon('save')}<span>${esc(u.note)}</span></div>`;
    const where = `<span class="small path">${esc(u.server ?? '')}</span>`;
    const saves = `${u.sentSaves ?? 0} saves sent${(u.wholeSaves || u.deltaSaves) ? ` (this run: ${u.wholeSaves} whole, ${u.deltaSaves} as changes, ${kb(u.saveBytes)})` : ''}`;
    const moves = `moves ${kb(u.eventBytes)} sent this run`;
    let line;
    if (u.state === 'queued') line = `${icon('wifiOff')}<span><b>${esc(S.serverName ?? 'The server')} not reachable.</b> ${u.queued ? `${u.queued} saves and ` : ''}${kb(u.queuedBytes)} of moves wait on this PC. Nothing is lost.</span>`;
    else if (u.state === 'sending') line = `${icon('up')}<span><b>Connected</b> · sending${u.queued ? ` · ${u.queued} saves to go` : ''}</span>`;
    else line = `${icon('checkCircle')}<span><b>Connected</b> · everything sent${u.lastSentAt ? ` · last ${ago(u.lastSentAt)}` : ''}</span>`;
    return `<div class="publish-line">${line}</div><div class="small">${esc(saves)} · ${esc(moves)}</div><div class="small">Server: ${where}</div><div class="small">Going public, postpone: not in the test build.</div>`;
  }
  function publishBlock(id, pub, upl, ended) {
    if (upl.state === 'sending') return `<div class="publish-line">${icon('up')}<span>Sending to ffaciv.com · <b>${upl.percent ?? 0} %</b></span></div><div class="meter teal"><i style="--p:${upl.percent ?? 0}%"></i></div><div class="small">It goes public ${delayWords(S.settings?.publishDelay ?? 3)}.</div>`;
    if (upl.state === 'off' || upl.state === 'held') return `<div class="publish-line">${icon('save')}<span>${esc(upl.note ?? 'Kept on this PC only (sending is off in Settings).')}</span></div>`;
    if (pub.state === 'public') return `<div class="publish-line">${icon('globe')}<span><b>Public</b> on ffaciv.com</span></div><div class="row actions">${btn(`${icon('external')} Open on ffaciv.com`, 'openSite', { url: pub.url }, 'primary')}${btn('Withdraw', 'withdraw', { game: id }, 'quiet')}</div>`;
    if (pub.state === 'countdown') {
      const total = (pub.delayHours ?? 3) * 3600e3, left = pub.publicAt - Date.now();
      const pct = pub.postponed ? 0 : Math.max(0, Math.min(100, 100 - left / total * 100));
      return `<div class="publish-line">${icon('clock')}<span>Public <b data-until="${pub.publicAt}">${until(pub.publicAt)}</b></span></div>${pub.postponed ? '' : `<div class="meter"><i style="--p:${pct}%"></i></div>`}<div class="small">Until then only you see it.</div><div class="row actions">${postponeMenu('pp-' + id, id)}${btn(`${icon('globe')} Make public now`, 'publishNow', { game: id }, 'primary')}</div>`;
    }
    if (pub.manual) return `<div class="publish-line">${icon('lock')}<span><b>Private</b> until you make it public.</span></div><div class="row actions">${btn(`${icon('globe')} Make public now`, 'publishNow', { game: id }, 'primary')}</div>`;
    if (upl.state === 'queued') return `<div class="publish-line">${icon('wifiOff')}<span>No internet: <b>${upl.queued} turns</b> wait on this PC. Nothing is lost.</span></div>`;
    return `<div class="publish-line">${icon('lock')}<span><b>Private</b> while you play. Public ${delayWords(S.settings?.publishDelay ?? 3)}.</span></div>${ended ? `<div class="row actions">${btn(`${icon('globe')} Make public now`, 'publishNow', { game: id }, 'primary')}</div>` : ''}`;
  }
  const cap = (s) => s.charAt(0).toUpperCase() + s.slice(1);

  // ---- Games ---------------------------------------------------------------------------------------------------------
  function uploadProgress(p) {
    const phase = p.phase, uploaded = p.uploaded ?? 0, total = p.total ?? 0, parsed = p.parsed ?? 0, parseTotal = p.parseTotal ?? 0;
    const index = ['ready'].includes(phase) ? 3 : ['preview'].includes(phase) ? 2 : ['parsing','held','unsupported','sent'].includes(phase) ? 1 : phase === 'error' && parseTotal ? 1 : 0;
    const title = {queued:'Waiting to upload',uploading:'Uploading save files',sent:'All saves uploaded',parsing:'Parsing save files',preview:'Creating game preview',held:'Parsing paused by server',unsupported:'Waiting for mod support',ready:'Game preview ready',stopped:'Upload stopped',error:'Upload needs attention'}[phase] ?? 'Preparing upload';
    const n = index === 1 ? parsed : uploaded, d = index === 1 ? parseTotal : total;
    const measured = d > 0 && ['uploading','parsing','queued','held','stopped'].includes(phase);
    const percent = measured ? Math.max(0,Math.min(100,Math.floor(100*n/d))) : null;
    const count = phase === 'sent' ? `${uploaded} of ${total} saves on the site` : index === 0 && total ? `${uploaded} of ${total} saves received` : index === 1 && parseTotal ? `${parsed} of ${parseTotal} saves parsed` : phase === 'preview' ? 'All saves parsed · waiting for the page' : '';
    return `<div class="upload-progress" aria-live="polite"><div class="upload-stages">${['Upload','Parse','Preview'].map((s,i)=>`<span class="${i < index ? 'complete' : i === index ? 'active' : ''}">${i < index ? '✓ ' : `${i+1} · `}${s}</span>`).join('')}</div><b>${title}</b>${!['ready','sent'].includes(phase) ? `<div class="meter teal ${percent == null ? 'indeterminate' : ''}" role="progressbar" aria-label="${title}"${percent == null ? '' : ` aria-valuemin="0" aria-valuemax="${d}" aria-valuenow="${Math.min(n,d)}"`}><i style="--p:${percent ?? 35}%"></i></div>` : ''}${count ? `<div class="small">${count}</div>` : ''}${p.note ? `<div class="small">${esc(p.note)}</div>` : ''}</div>`;
  }
  function recordingProgress(g) {
    const r = g.replay ?? {}, sent = g.sentSaves ?? g.upload?.sentSaves ?? 0, queued = g.queuedSaves ?? g.upload?.queuedSaves ?? 0;
    if (r.state === 'processing') return uploadProgress({phase:r.saves > 0 && r.processed >= r.saves ? 'preview' : 'parsing', uploaded:sent,total:sent+queued,parsed:r.processed,parseTotal:r.saves,note:r.failed ? `${r.failed} saves could not be parsed.` : ''});
    // every save already on the site (a livestreamed game): only the finish is left, so say that and why it waits
    if (g.uploadSchedule?.state === 'upload' && !g.url && r.state !== 'ready' && queued === 0 && sent > 0) return uploadProgress({phase:'sent',uploaded:sent,total:sent,note:g.uploadWaitReason || 'Waiting for the site to start the replay.'});
    if (g.uploadSchedule?.state === 'upload' && !g.url && r.state !== 'ready') return uploadProgress({phase:queued > 0 && sent > 0 ? 'uploading' : 'queued',uploaded:sent,total:sent+queued,note:g.uploadWaitReason || (queued > 0 ? 'Uploads resume automatically when the game and computer are ready.' : 'Waiting for the server to acknowledge the recording.')});
    return '';
  }
  function replayControls(g) {
    const r = g.replay ?? {}, context = g.context, args = {game:g.id,context};
    const acknowledged = !!(g.url || g.siteGame || r.state || (g.sentSaves ?? 0) > 0 || g.lastSentTurn != null || g.upload?.lastSentTurn != null);
    const kept = r.keepShort || r.keepShortRequested, short = (g.turns ?? g.turn ?? 0) < 50;
    let line = r.completionQueued ? 'Finish requested' :!acknowledged ? 'Saved on this PC' : r.state === 'deleted' ? 'Short game deleted' : r.state === 'processing' ? '' : r.state === 'ready' || g.url ? 'Replay generated and stored' : r.finishAt ? `${short && !kept ? 'Deleted' : 'Finished'} <span data-until="${r.finishAt}">${until(r.finishAt)}</span> without a new save or log` : g.uploadSchedule?.state === 'upload' ? '' : 'Waiting for the server to acknowledge this recording';
    let acts = '', options = '';
    if (g.url || r.url) acts += btn(`${icon('external')} Game page`, 'openSite', {url:g.url || r.url}, 'primary');
    if (!['processing','ready','deleted'].includes(r.state) && !g.siteGame && context && !r.completionQueued) {
      acts += btn('Mark as done', 'completeGame', args, 'primary');
      if (short && !kept) options += btn('Keep short game', 'keepShortGame', args);
    }
    const priv = g.private ?? r.private ?? false, restrictedUntil = g.restrictedUntil ?? r.restrictedUntil, restricted = restrictedUntil > Date.now();
    if (acknowledged && (context || g.siteGame)) {
      options += btn(`${icon(priv ? 'globe' : 'lock')} ${priv ? 'Make public' : 'Make private'}`, 'replayChoice', {...args,private:!priv});
      options += btn(restricted ? 'Allow replay viewing' : 'Restrict viewing for 14 days', 'replayChoice', {...args,restricted:!restricted});
    }
    if (priv && r.completionQueued && r.publicRequested) line += ' · Hidden on the website until it is finished, then public';
    else if (priv && !(g.uploadSchedule?.state === 'waiting' && g.uploadSchedule?.hidden)) line += ' · Private: hidden from the website';
    else if (restricted) line += ` · Page listed; replay locked until ${esc(new Date(restrictedUntil).toLocaleDateString())}`;
    options += `<button class="btn quiet danger" data-delete="${esc(g.id)}" data-context="${esc(context ?? '')}" data-k="delete-${esc(g.id)}">${icon('x')} Delete recording</button>`;
    const active = !g.ended && (g.status === 'recording' || g.context === S.current?.context && !S.current?.ended);
    return `<div class="replay-state">${uploadSchedule(g)}${r.state === 'processing' ? recordingProgress(g) : ''}<div class="small">${line}</div>${savesBlock(g)}${acts?`<div class="row actions">${acts}</div>`:''}${active && !r.completionQueued?'<p class="small">Mark as done stops this recording and keeps it, even under 50 turns. Its replay goes public unless you chose Make private.</p>':''}<details class="replay-settings" data-details-key="replay-${esc(context??g.id)}"><summary data-k="replay-settings-${esc(g.id)}">Replay settings</summary><div class="row actions">${options}</div></details></div>`;
  }

  // ---- the Saves list (the user, 2026-10-31: "more feedback on when things go wrong … see what savefiles the server has") --
  // Each save of the recording: on the site (and what the site made of it: the recording's state from the site lists them),
  // refused by the site (its words), or waiting on this PC (the one reason everything waits). Newest first when opened.
  const SITE_SAVE = { converted: ['good', 'On the site · in the replay'], converting: ['busy', 'On the site · being converted'],
    queued: ['ok', 'On the site · in line to be converted'], waiting: ['ok', 'On the site · kept for the replay'], failed: ['bad', 'On the site · could not be read'],
    unsupported: ['bad', 'On the site · this mod version cannot be read yet'], cleared: ['muted', 'On the site · cleared by the site'] };
  function saveRows(g) {
    const listed = g.replay?.files, server = new Map((listed ?? []).map((f) => [f.id, f]));
    return (g.saves ?? []).map((s) => {
      if (s.refused) return { ...s, kind: 'bad', words: `Refused by the site (HTTP ${s.refused.status}): ${s.refused.why}` };
      if (!s.id) return { ...s, kind: s.beforeStream ? 'muted' : 'wait', words: s.beforeStream ? 'On this PC · older than the livestream: sent after the game' : 'On this PC · not sent yet' };
      const f = server.get(s.id);
      if (f) { const [kind, words] = SITE_SAVE[f.state] ?? ['ok', 'On the site']; return { ...s, kind, words: f.note && kind === 'bad' ? `${words}: ${f.note}` : words }; }
      return { ...s, kind: listed ? 'muted' : 'ok', words: listed ? 'Sent, but the site no longer lists it' : s.removed ? 'On the site · copy removed from this PC' : 'On the site' };
    }).sort((a, b) => (a.turn ?? -1) - (b.turn ?? -1) || (a.at ?? 0) - (b.at ?? 0));
  }
  function savesBlock(g) {
    if (!g.saves) return g.context && g.id !== 'live' ? `<div class="row actions">${btn(`${icon('save')} Show saves`, 'showSaves', { game: g.id }, 'quiet')}</div>` : '';
    const rows = saveRows(g);
    if (!rows.length) return '';
    const on = rows.filter((r) => r.id).length, waiting = rows.filter((r) => !r.id && !r.refused && !r.beforeStream), refused = rows.filter((r) => r.refused);
    const unreadable = rows.filter((r) => r.id && r.kind === 'bad');
    const oldest = Math.min(...waiting.map((r) => r.at ?? Date.now()));
    const level = refused.length || unreadable.length ? 'bad' : waiting.length && Date.now() - oldest > 120e3 ? 'warn' : '';
    const summary = [`${on} of ${rows.length} on the site`, waiting.length ? `${waiting.length} waiting on this PC` : '', refused.length ? `${refused.length} refused` : '',
      unreadable.length ? `${unreadable.length} could not be read` : ''].filter(Boolean).join(' · ');
    const said = refused.length ? `The site refused ${refused.length === 1 ? 'a save' : `${refused.length} saves`}: ${refused.at(-1).refused.why}` : '';
    const why = waiting.length ? g.savesWait ?? 'Sending in a moment.' : '';
    const gap = turnsGap(g);
    return `<div class="saves-block ${level}"><div class="saves-head">${icon(level ? 'alert' : waiting.length ? 'up' : 'checkCircle')}<b>Saves${turnsLabel(g) ? ` · ${esc(turnsLabel(g))}` : ''}</b><span>${esc(summary)}</span></div>`
      + `${said ? `<p class="small saves-why refused" role="status">${esc(said)}</p>` : ''}${why ? `<p class="small saves-why" role="status">${waiting.length} waiting: ${esc(why)}</p>` : ''}${gap ? `<p class="small">${gap} ${gap === 1 ? 'turn has' : 'turns have'} no save in this range (Civ did not autosave them, or the recorder was off).</p>` : ''}`
      + `<details class="saves-list" data-details-key="saves-${esc(g.context ?? g.id)}"><summary data-k="saves-${esc(g.id)}">Each save</summary><ol>${rows.slice().reverse().map((r) =>
        `<li class="${r.kind}" title="${esc(r.file ?? '')}"><b>${r.turn != null ? `T${r.turn}` : '–'}</b><span class="saves-at">${r.at ? esc(clock(r.at)) : ''}</span><span class="saves-state">${esc(r.words)}</span></li>`).join('')}</ol></details></div>`;
  }

  // ---- deleted, held 7 days (the user, 2026-10-31: "a way to regret deleting replays … hide it for like 1 week") ------
  const heldCards = () => (S.deleted ?? []).map((d) => ({ ...(d.card ?? {}), held: d }));
  function deletedCard(g) {
    const d = g.held;
    return `<article class="card game-row deleted-row"><div class="gr-main"><div class="game-top">${modTag(g)}<h3>${esc(day(recordingTime(g)))} · ${esc(clock(recordingTime(g)))}${turnsLabel(g) ? ` · ${esc(turnsLabel(g))}` : ''}</h3><span class="badge withdrawn">Deleted</span></div><div class="small" style="margin-top:6px">Hidden from you and the website. Erased for good <span data-until="${d.due}">${until(d.due)}</span> unless you undo it.</div></div><div class="row gr-acts">${btn(`${icon('refresh')} Undo delete`, 'undoDelete', { key: d.key }, 'primary')}</div></article>`;
  }

  // a finished game uploads at once, hidden, and goes public 2 hours after the game (the user, 2026-11-02)
  function uploadSchedule(g) {
    const q = g.uploadSchedule;
    if (!q || !q.ended) return '';
    const a = {game:g.id};
    if (q.state === 'waiting' && q.hidden && q.dueAt) return `<div class="upload-plan ${q.warning ? 'urgent' : ''}"><div class="row">${icon('lock')}<b>Hidden · public on ffaciv.com <span data-until="${q.dueAt}">${until(q.dueAt)}</span></b></div><p class="small">Only you can see it until then. Keep it hidden longer, or delete it under Replay settings.</p><div class="row">${btn(`${icon('globe')} Make public now`, 'uploadGameNow', a, 'primary')}${dropdown('hide-'+g.id, 'Keep hidden', [{label:'2 more hours',cmd:'postponeUpload',args:{...a,hours:2}},{label:'1 more day',cmd:'postponeUpload',args:{...a,hours:24}},{label:'Until I make it public',cmd:'replayChoice',args:{...a,context:g.context,private:true}}])}</div></div>`;
    if (g.url || ['ready','processing'].includes(g.replay?.state)) return '';
    if (q.state === 'upload') return recordingProgress(g);
    const held = q.held || q.enabled === false;
    return `<div class="upload-plan ${q.warning ? 'urgent' : ''}"><div class="row">${icon(held ? 'pause' : 'clock')}<b>${held ? q.enabled === false ? 'Automatic uploads are off' : 'Upload postponed' : q.dueAt ? `Uploads now · public <span data-until="${q.dueAt}">${until(q.dueAt)}</span>` : 'Ready to upload'}</b></div><p class="small">${held ? 'Your recording stays on this PC until you choose to upload.' : 'It uploads once Civ is in its main menu or closed (not a staging lobby) and stays hidden on ffaciv.com until then.'}</p><div class="row">${btn(held ? `${icon('up')} Upload now` : `${icon('globe')} Make public now`, 'uploadGameNow', a, 'primary')}${dropdown('upload-'+g.id, held ? 'Postpone' : 'Keep hidden', [{label:held ? 'For 2 hours' : '2 more hours',cmd:'postponeUpload',args:{...a,hours:2}},{label:held ? 'For 1 day' : '1 more day',cmd:'postponeUpload',args:{...a,hours:24}},{label:held ? 'Until I upload' : 'Keep it on this PC',cmd:'postponeUpload',args:{...a,hours:0}}])}</div></div>`;
  }

  function gameCard(g) {
    const you = g.players.find((p) => p.you);
    const civs = g.players.map((p) => `<span style="white-space:nowrap"><span class="civdot" style="background:${esc(p.color)}"></span>${esc(p.civ)}${p.you ? ' (you)' : ''}</span>`).join(' · ');
    let line = '', acts = '';
    const more = (items) => dropdown('more-' + g.id, icon('more'), items, 'quiet more', false);
    if (S.live) { line = g.status === 'recording' ? 'Recording now' : g.replay?.state === 'ready' ? 'Replay ready' : (g.sentSaves ?? 0) > 0 ? `${g.sentSaves} saves uploaded · ${g.waitsForMenu ? 'replay made after you leave the game' : 'replay not made yet'}` : g.uploadSchedule?.ended ? 'Game ended · saved on this PC' : 'Local recording'; if (g.folder) acts = btn(`${icon('folder')} Open folder`, 'openFolder', {game:g.id}, 'quiet'); }
    else switch (g.status) {
      case 'recording': line = 'Recording now'; acts = btn(`${icon('folder')} Open folder`, 'openFolder', { game: g.id }, 'quiet'); break;
      case 'sending': line = S.testBuild ? `Sending to the test server · ${g.queuedSaves ?? 0} saves to go` : `Sending · ${g.percent ?? 0} %`; acts = btn(`${icon('folder')} Open folder`, 'openFolder', { game: g.id }, 'quiet'); break;
      case 'countdown': line = `Public <b data-until="${g.publicAt}">${until(g.publicAt)}</b>`; acts = postponeMenu('ppg-' + g.id, g.id) + btn('Make public now', 'publishNow', { game: g.id }, 'primary') + more([{ label: "Don't upload", cmd: 'withdraw', args: { game: g.id } }, { label: 'Open folder', cmd: 'openFolder', args: { game: g.id } }]); break;
      case 'locked': line = 'Private until you make it public'; acts = btn('Make public now', 'publishNow', { game: g.id }, 'primary') + more([{ label: "Don't upload", cmd: 'withdraw', args: { game: g.id } }, { label: 'Open folder', cmd: 'openFolder', args: { game: g.id } }]); break;
      case 'public': line = 'Public on ffaciv.com'; acts = btn(`${icon('external')} Open on ffaciv.com`, 'openSite', { url: g.url }) + more([{ label: 'Withdraw from ffaciv.com', cmd: 'withdraw', args: { game: g.id } }, { label: 'Open folder', cmd: 'openFolder', args: { game: g.id } }]); break;
      case 'withdrawn': line = 'Withdrawn · kept on this PC'; acts = btn('Upload again', 'upload', { game: g.id }) + btn(`${icon('folder')} Open folder`, 'openFolder', { game: g.id }, 'quiet'); break;
      case 'streamed': line = `Sent to the test server${g.lastSentTurn != null ? ` · saves up to turn ${g.lastSentTurn}` : ''}`; acts = btn(`${icon('folder')} Open folder`, 'openFolder', { game: g.id }, 'quiet'); break;
      case 'local': line = 'Recording saved on this PC'; acts = (S.settings?.upload ? btn('Upload', 'upload', { game: g.id }) : '') + btn(`${icon('folder')} Open folder`, 'openFolder', { game: g.id }, 'quiet'); break;
    }
    acts += gameStreamActions(g);
    if (S.live) { acts += replayControls(g); }
    else if (g.canBlock !== undefined) {
      acts += g.canBlock ? btn(g.blocked ? `${icon('globe')} Unblock` : `${icon('eyeOff')} Block`, 'setBlocked', { game: g.id, blocked: !g.blocked }, g.blocked ? 'primary' : 'quiet')
        : `<button class="btn quiet" disabled title="${esc(g.blockWhy)}">${icon('eyeOff')} Block</button>`;
      if (g.blocked) line += ` · <b>Blocked:</b> hidden from everyone on the site${g.blockConfirmed ? '' : g.blockError ? ` (not taken yet: ${esc(g.blockError)}; tried again by itself)` : ' (sending…)'}`;
      else if (!g.canBlock && g.status === 'recording') line += ` · ${esc(g.blockWhy)}`;
      else if (g.blockConfirmed === false) line += ` · unblocking${g.blockError ? ` (not taken yet: ${esc(g.blockError)})` : '…'}`;
    }
    return `<article class="card game-row"><div class="gr-main"><div class="game-top">${modTag(g)}<h3>${esc(day(recordingTime(g)))} · ${esc(clock(recordingTime(g)))}${turnsLabel(g) ? ` · ${esc(turnsLabel(g))}` : ''}${g.players.length ? ` · ${g.players.length} players` : ''}</h3>${g.blocked ? '<span class="badge queued">Blocked</span>' : ''}${badge(g.private || g.replay?.private ? 'locked' : g.restrictedUntil > Date.now() ? 'restricted' : g.replay?.state === 'processing' ? 'processing' : g.replay?.state === 'ready' ? 'public' : g.status)}</div>${civs ? `<div class="civs">${civs}</div>` : ''}${!S.live && g.status === 'sending' && !S.testBuild ? `<div class="meter teal"><i style="--p:${g.percent ?? 0}%"></i></div>` : ''}<div class="small" style="margin-top:6px">${line}</div></div><div class="row gr-acts">${acts}</div></article>`;
  }
  function games() {
    const all = S.games ?? [];
    const pick = { all: () => true, private: (g) => g.private || g.replay?.private, public: (g) => g.url && !g.private && !g.replay?.private };
    const shown = all.filter(pick[gamesFilter]);
    const chip = (k, t) => `<button class="chip" data-filter="${k}" aria-pressed="${gamesFilter === k}" data-k="f-${k}">${t}</button>`;
    let h = `<h2 class="screen-title">Previous games${S.oldGames ? `<span style="flex:1"></span>${go(`${icon('up')} Manual upload${S.oldGames.newCount ? ` · ${S.oldGames.newCount}` : ''}`, 'old', 'primary')}` : ''}</h2><div class="filters">${chip('all', `All · ${all.length}`)}${chip('private', 'Private')}${chip('public', 'Public')}</div>`;
    h += shown.length ? `<div class="list">${shown.map(gameCard).join('')}</div>` : `<div class="empty">${icon('games')}No games here yet. Play a game of Civ V and it shows up here.</div>`;
    const held = heldCards();
    if (held.length) h += `<h3 class="group-label deleted-heading">Recently deleted · ${held.length}</h3><p class="small">Hidden from you and the website. Each is erased for good 7 days after you deleted it; until then Undo brings it back as it was.</p><div class="list">${held.map(deletedCard).join('')}</div>`;
    return h;
  }

  // ---- Upload old games (OR03 "Bulk upload") -------------------------------------------------------------------------
  const OLD = { new: ['', 'Not uploaded'], partial: ['countdown', 'Partly uploaded'], uploaded: ['public', 'Already uploaded'], queued: ['locked', 'Waiting'],
    unsupported: ['locked', 'Waiting for support'], uploading: ['sending', 'Uploading'], processing: ['sending', 'Generating replay'], done: ['public', 'Replay ready'], stopped: ['queued', 'Stopped'], error: ['queued', 'Problem'] };
  const pickable = (g) => !['uploading', 'queued', 'processing', 'done'].includes(g.status);
  const range = (a, b) => { const d1 = `${day(a)} ${clock(a)}`; return day(a) === day(b) ? `${d1}–${clock(b)}` : `${d1} – ${day(b)} ${clock(b)}`; };
  function oldGames() {
    const O = S.oldGames ?? { games: [], folders: [] }, list = O.games ?? [];
    for (const id of [...picked]) if (!list.some((g) => g.id === id && pickable(g))) picked.delete(id);
    const can = list.filter(pickable), all = can.length > 0 && can.every((g) => picked.has(g.id));
    let h = `<h2 class="screen-title">Manual upload</h2>`;
    h += `<p class="lead">Choose a folder, then select all the full games you want to keep. Generate and store replay explicitly keeps selected games, including games under 50 turns.</p>`;
    if (S.testBuild) h += `<div class="banner">${icon('lock')}<div><h3>${S.site ? 'Kept for the replays' : 'Nothing is public'}</h3><p>${S.site ? `The games go to ${esc(S.serverShort)}, where they become replays. Use Make private in Games to hide a game, or Restrict viewing to keep its page visible with a locked replay.` : 'Test build: the games go only to the test server on this PC.'}</p></div></div>`;
    h += `<div class="row old-bar">${btn(`${icon('check')} ${all ? 'Select none' : 'Select all'}`, 'ui.pickAll', {}, 'quiet', can.length ? '' : 'disabled')}${btn(`${icon('folder')} Add folder…`, 'addOldFolder', {}, 'quiet')}${btn(`${icon('refresh')} ${O.scanning ? 'Looking…' : 'Look again'}`, 'scanOldGames', {}, 'quiet', O.scanning ? 'disabled' : '')}<span style="flex:1"></span>${O.uploading ? btn('Stop', 'stopOldUpload', {}, '') : ''}${btn(`${icon('up')} Generate and store replay${picked.size > 1 ? ` (${picked.size} games)` : ''}`, 'uploadOldGames', { ids: [...picked] }, 'primary', picked.size ? '' : 'disabled')}</div>`;
    if (!list.length) return h + `<div class="empty">${icon('games')}${O.scanning || !O.scanned ? 'Looking through the save folders…' : 'No old games found. Civ keeps its autosaves in Documents\\My Games\\Sid Meier\'s Civilization 5. "Add folder…" looks in any other folder (zip files too).'}</div>`;
    h += `<div class="list">${list.map((g) => {
      const [c, t] = OLD[g.status] ?? ['', g.status], on = picked.has(g.id), p = pickable(g);
      const turns = g.firstTurn != null ? `turns ${g.firstTurn}–${g.lastTurn}` : '';
      const facts = [g.mapSize && `${g.mapSize} map`, g.speed, g.map].filter(Boolean).join(' · ');
      return `<article class="card game-row old${on ? ' on' : ''}"><button class="tick" role="checkbox" aria-checked="${on}" data-pick="${esc(g.id)}" data-k="pick-${esc(g.id)}" ${p ? '' : 'disabled'} aria-label="Upload this game"></button><div class="gr-main"><div class="game-top"><span class="mod">${esc(g.mod)}</span><h3>${esc(range(g.from, g.to))}</h3><span class="badge ${c}">${t}</span></div>`
        + `<div class="small">${g.saves} ${g.saves === 1 ? 'save' : 'saves'}${turns ? ' · ' + turns : ''} · ${g.megabytes} MB${g.already && g.status === 'partial' ? ` · ${g.already} sent already` : ''}</div>${facts ? `<div class="small">${esc(facts)}</div>` : ''}<div class="small path">${esc(g.folder)}</div>`
        + `${['uploading','queued','processing','done','unsupported','stopped','error'].includes(g.status) ? uploadProgress({phase:g.phase ?? (g.status === 'processing' ? 'parsing' : g.status === 'done' ? 'ready' : g.status),uploaded:g.uploaded ?? (g.status === 'processing' ? g.saves : g.done),total:g.saves,parsed:g.parsed ?? (g.status === 'processing' ? g.done : 0),parseTotal:g.parseTotal ?? (g.status === 'processing' ? g.total : 0),note:g.note}) : g.note ? `<div class="small">${esc(g.note)}</div>` : ''}${g.status === 'done' ? (g.links ?? []).filter(link=>link.url).map(link => btn(`${icon('external')} Open game preview`, 'openSite', {url:link.url}, 'primary')).join('') : ''}</div></article>`;
    }).join('')}</div>`;
    h += `<p class="small" style="margin-top:10px">Games are grouped by their own identity; the server checks each save before making the replay.${O.folders?.length ? ` Looked in: ${O.folders.map(esc).join(' · ')}` : ''}</p>`;
    return h;
  }

  // ---- Requests ------------------------------------------------------------------------------------------------------
  function requests() {
    const rq = S.requests ?? [], pm = S.permissions ?? [];
    let h = `<h2 class="screen-title">Watch requests</h2><p class="lead">Casters and organisers can ask to watch a game live from your side. They only see what you see, and nobody else sees it.</p>`;
    h += notInTest('Watch requests come later. ' + (S.site ? 'Today anyone with the livestream link can watch while a live slot is yours.' : 'In the test build everything goes to the test server on this PC, and anyone with the livestream link can watch.'));
    if (S.testBuild) {} else if (!S.settings?.stream) h += `<div class="banner warn">${icon('info')}<div><h3>Live view is off</h3><p>Nobody can watch you live. Turn it on in Settings to get requests.</p></div></div>`;
    h += rq.length ? rq.map((r) => `<article class="card"><div class="item">${icon('user')}<div class="item-body"><h3>${esc(r.who)} <span class="small">· ${esc(r.role)} · ${ago(r.at)}</span></h3><p>wants to watch <b style="color:var(--text)">${esc(r.game)}</b> live, as you see it.</p><div class="row actions">${btn('Allow for this game', 'allowRequest', { id: r.id, scope: 'game' }, 'primary')}${btn('Decline', 'declineRequest', { id: r.id })}</div><div class="row" style="margin-top:6px">${btn('Always allow this person', 'allowRequest', { id: r.id, scope: 'always' }, 'quiet')}</div></div></div></article>`).join('')
      : `<div class="card flat empty" style="padding:18px">${icon('requests')}No requests right now.</div>`;
    h = h.replace('</p>', '</p><div class="cols even"><div class="col">') + `</div><div class="col"><div class="group-label" style="margin-top:0">Who can watch you</div>`;
    h += pm.length ? `<div class="card settings">${pm.map((p) => `<div class="set"><div class="set-text"><b>${esc(p.who)}</b><span>${p.scope === 'always' ? 'Always' : `This game only · ${esc(p.game ?? '')}`} · ${esc(p.role)}</span></div>${btn('Take back', 'revoke', { id: p.id }, 'danger')}</div>`).join('')}</div>`
      : `<div class="card flat small" style="padding:12px 14px">Nobody. Allowed people show up here, and you can take it back any time.</div>`;
    return h + '</div></div>';
  }

  // ---- Settings ------------------------------------------------------------------------------------------------------
  const sw = (key, on, label, sub, disabled = false) => `<div class="set"><div class="set-text"><b id="l-${key}">${label}</b>${sub ? `<span>${sub}</span>` : ''}</div><button class="switch" role="switch" aria-checked="${!!on}" aria-labelledby="l-${key}" data-set="${key}" data-k="sw-${key}" ${disabled || pending === 'setSetting' ? 'disabled' : ''}></button></div>`;
  function settings() {
    const s = S.settings ?? {}, n = s.notify ?? {}, m = memoryLoggingState();
    return `<h2 class="screen-title">Options</h2><p class="lead">Set up how you record and upload.</p><div class="cols even"><div class="col">
      <section class="card settings option-section"><div class="option-heading"><span class="step-number">1</span><div><h3>Record on this PC</h3><p>Local recording does not mean uploading.</p></div></div>
      ${sw('recording', s.recording !== false, 'Automatically save my games', 'Keep copies of Civ’s autosaves while you play.')}
      ${sw('recordSinglePlayer', s.recordSinglePlayer, 'Include single-player games', 'Multiplayer games are recorded by default.')}
      ${!S.safeCapture ? sw('backupUnsupported', s.backupUnsupported !== false, 'Back up unsupported versions', 'Keep save files locally until replay support is available.') : ''}
      </section><section class="card settings option-section"><div class="option-heading"><span class="step-number">2</span><div><h3>Memory logs <span class="badge plain">Experimental</span></h3><p>Optional detail between turn saves.</p></div></div><div class="set"><div class="set-text"><b id="l-memory-logging">Add memory logs</b><span>Moves and city changes, read from outside Civ.</span></div><button class="switch" role="switch" aria-checked="${m.enabled}" aria-labelledby="l-memory-logging" data-cmd="setMemoryLogging" data-args="${attr({enabled:!m.enabled})}" data-k="options-memory" ${pending === 'setMemoryLogging' || (!m.enabled && !m.canEnable) ? 'disabled' : ''}></button></div><p class="option-note">${icon('info')}${esc(m.reason)}</p><details class="option-details"><summary>How memory logging works</summary><p class="small">A separate process reads supported Civ memory. No injection, game-memory writes or pausing Civ. Sampling can miss short actions; read-only access does not guarantee stability. Resource controls apply. Livestreaming stays off until separately started.</p>${memoryCadenceDetails()}<div class="set"><div class="set-text"><b id="l-looks">Memory reads per second</b><span>Only applies while memory logging is on.</span></div><select class="pick" data-set-select="looksPerSecond" aria-labelledby="l-looks" data-k="sel-looks" ${!m.enabled || s.recording === false ? 'disabled' : ''}>${[1,2,4,5,10,20].map(v=>`<option value="${v}" ${(s.looksPerSecond ?? 4) === v ? 'selected' : ''}>${v} / second</option>`).join('')}</select></div></details></section>
      <section class="card settings option-section"><div class="option-heading"><span class="step-number">3</span><div><h3>Upload timing</h3><p>Turn local saves into a replay on ffaciv.com.</p></div></div>
      ${sw('stream', s.stream !== false, 'Allow automatic uploads', s.uploadTiming==='during'?'Send the current recording as saves arrive.':'Upload after your game; the replay goes public 2 hours later. Until then you can keep it hidden or delete it.')}
      ${uploadTimingChoice()}${inlineUploadMethod()}
      <p class="option-note">${icon('clock')}${s.uploadTiming==='during'?'While-playing uploads use available computer resources. Explicit per-game holds still apply.':'A reminder appears 5 minutes before a replay goes public. Uploads start once Civ is in its main menu or closed (not a staging lobby) and computer resources are available.'} Leave the recorder running; postponed games stay here.</p>
      <div class="row actions">${go(`${icon('up')} Manual upload games…`, 'old')}</div></section>
      </div><div class="col"><section class="card getting-started"><h3>Choose how to record</h3><p>Save files, experimental memory recording or manual saves — see what each method enables.</p><button class="btn" data-setup data-setup-step="1" data-k="setup-options">${icon('settings')} Compare recording methods</button>${go('How recording works', 'help', 'quiet')}</section>
      <section class="card settings"><h3 class="section-label">Quiet while you play</h3>${sw('notify.lobby', n.lobby !== false, 'Game detected', 'One recording notification with start / stop controls.')}${sw('notify.problems', n.problems !== false, 'Recording problems')}${sw('notify.updates', n.updates !== false, 'Recorder updates')}${sw('startWithWindows', s.startWithWindows, 'Start with Windows', 'Wait quietly in the tray until Civ starts.')}</section>
      <section class="card settings"><h3 class="section-label">This PC</h3><div class="set"><div class="set-text"><b>Recordings folder</b><span class="path">${esc(s.dataFolder ?? '')}</span></div>${btn('Open', 'openFolder', {path:s.dataFolder})}</div><div class="set"><div class="set-text"><b>FFACiv Recorder ${esc(S.version)}</b><span>Updates download only when you choose.</span></div>${go('Updates', 'updates')}</div></section>
      ${S.test ? `<details class="card option-details"><summary>Advanced connection settings</summary><p class="small path">${esc(S.test.streamUrl ?? '')}</p><p class="small path">${esc(S.test.settingsFile ?? '')}</p>${btn('Edit settings file', 'editSettings', {})}</details>` : ''}
      </div></div>`;
  }

  // ---- Updates ---------------------------------------------------------------------------------------------------------
  function updates() {
    const u = S.updates ?? {}, a = u.available;
    let h = `<h2 class="screen-title">Updates</h2><div class="cols even"><div class="col">`;
    if (a) h += `<section class="card"><h3>FFACiv Recorder ${esc(a.version)} ${a.required ? '<span class="badge queued">Required</span>' : '<span class="badge sending">Available</span>'}</h3><p class="small">You have ${esc(S.version)}.${a.required ? ' Update to continue sending games; your recordings wait on this PC.' : ''}</p>${a.notes?.length ? `<ul class="points">${a.notes.map(note => `<li>${icon('check')}<div>${esc(note)}</div></li>`).join('')}</ul>` : ''}<div class="row actions">${btn(`${icon('download')} Download update`, 'updateNow', {}, 'primary')}${a.required ? '' : laterMenu('later-update')}</div>${a.later ? `<p class="small">Reminder postponed ${a.later === 'next' ? 'until the next start' : a.later === 'tonight' ? 'until tonight' : 'until tomorrow'}.</p>` : ''}</section>`;
    else h += `<section class="card"><h3>${u.checking ? 'Checking for updates…' : u.error ? 'Update check unavailable' : u.checkedAt && u.waited ? 'No newer version at the last check' : u.checkedAt ? 'You have the newest version' : 'Updates have not been checked yet'}</h3><p class="small">FFACiv Recorder ${esc(S.version)}</p>${u.error ? `<p class="small">${esc(u.error)}</p>` : ''}${u.waited && !u.checking ? '<p class="small">Automatic checks wait while Civ is open. Check for updates asks the website now.</p>' : ''}</section>`;
    h += `<div class="row actions">${btn(`${icon('refresh')} ${u.checking ? 'Checking…' : 'Check for updates'}`, 'checkUpdates', {}, '', u.checking ? 'disabled' : '')}</div>${u.checkedAt ? `<p class="small">Last checked ${esc(new Date(u.checkedAt).toLocaleString())}.</p>` : ''}</div><div class="col"><div class="card prose"><h3>Updates stay in your control</h3><p>The recorder checks when it starts and every hour while it runs, including in the tray. While Civ is open it waits; Check for updates always asks at once.</p><p>New versions can show a notification. Download opens the official release page in your browser; you choose when to install.</p><p>There are no automatic program installations.</p></div></div></div>`;
    return h;
  }

  // ---- Checks ----------------------------------------------------------------------------------------------------------
  function resourceCard() {
    const r = S.resources;
    if (!r) return '';
    const percent = v => typeof v === 'number' && Number.isFinite(v) ? `${v.toFixed(1)}%` : '–';
    const memory = v => typeof v === 'number' && Number.isFinite(v) ? `${(v/1048576).toFixed(0)} MiB` : '–';
    const pacing = r.uploadKibPerSecond == null ? 'Upload pacing: unavailable.' : r.uploadKibPerSecond > 0 ? `Uploads paced at ${esc(r.uploadKibPerSecond)} KiB/s of request data.` : 'Upload bandwidth limit is off.';
    const ceiling = r.cpuLimitPercent == null ? 'CPU ceiling status unavailable.' : r.cpuLimitPercent > 0 ? `Requested recorder-host CPU ceiling: ${esc(r.cpuLimitPercent)}%.` : 'Adaptive CPU budget is active; hard CPU ceiling is off.';
    return `<article class="card resource-card"><div class="resource-card-head"><h3>Recorder resources</h3><span class="badge ${r.paused ? 'queued' : 'plain'}">${r.paused ? 'Work paused' : 'Monitoring'}</span></div><p class="resource-reason">${esc(r.reason ?? 'Waiting for resource samples…')}</p><dl class="resource-metrics"><div><dt>Recorder CPU</dt><dd>${percent(r.cpuPercent)}</dd></div><div><dt>Computer CPU</dt><dd>${percent(r.systemCpuPercent)}</dd></div><div><dt>Recorder memory</dt><dd>${memory(r.memoryBytes)}</dd></div><div><dt>Peak host memory</dt><dd>${memory(r.peakMemoryBytes)}</dd></div></dl><p class="small">Sampled every 2 seconds. CPU uses total computer capacity. Recorder figures cover the host process; separate WebView2 window helpers are excluded. Brief spikes can be missed.</p><p class="small">${pacing} ${ceiling}</p>${r.cpuLimitError ? `<p class="resource-limit-error">${icon('alert')}${esc(r.cpuLimitError)}</p>` : ''}<p class="small">Sustained high load or low available memory pauses new save copies and heavy work. Work resumes gradually after recovery. Uploads also wait until Civ has stayed closed or in a confirmed menu for 5 seconds.</p><div class="row actions"><button class="btn" data-dialog="report" data-k="health-report" ${S.reportBusy ? 'disabled' : ''}>${icon('save')} ${S.reportBusy ? 'Preparing report…' : 'Save diagnostic report'}</button></div></article>`;
  }

  function checks() {
    const list = S.checks ?? [];
    const order = { bad: 0, warn: 1, info: 2, ok: 3 };
    const ic = { ok: 'checkCircle', warn: 'alert', bad: 'xCircle', info: 'info' };
    let h = `<h2 class="screen-title"><button class="icon-btn back" data-go="home" aria-label="Back" data-k="back">${icon('back')}</button>Checks</h2><p class="lead">What the recorder needs to make a full replay.</p><div class="list" style="max-width:760px">`;
    h += resourceCard();
    for (const c of [...list].sort((a, b) => order[a.level] - order[b.level])) {
      const a = c.action;
      h += `<article class="card"><div class="item">${icon(ic[c.level], 'state-ic ' + c.level)}<div class="item-body"><h3>${esc(c.title)}</h3><p>${esc(c.text)}</p>${a ? `<div class="row actions">${btn(esc(a.label), a.cmd, a.args, c.level === 'info' ? '' : 'primary', a.disabled ? 'disabled' : '')}${a.disabled && a.why ? `<span class="small">${esc(a.why)}</span>` : ''}</div>` : ''}</div></div></article>`;
    }
    return h + '</div>';
  }

  // ---- Help & privacy --------------------------------------------------------------------------------------------------
  function help() {
    if (S.safeCapture) return `<h2 class="screen-title">How recording works</h2><div class="cols even"><div class="col"><section class="card prose"><h3>Save first, share later</h3><p>The recorder keeps local copies of Civ V's autosaves after their size and modification time have stopped changing. Memory logs are ${memoryLoggingState().enabled ? 'on' : 'off'} and can be changed separately in Options. The conservative upload policy still waits until after the game.</p><p>In Civ: Options → Game options → Turns between autosaves = 1.</p><div class="row actions">${go('Check my setup', 'checks')}<button class="btn quiet" data-setup data-setup-step="1" data-k="help-methods">Compare recording methods</button></div></section><section class="card prose"><h3>Recording and uploading are separate</h3><p>Recording keeps save copies on this PC. Uploading sends them to ffaciv.com to generate a replay.</p><p>After your game, automatic uploads start right away and the replay stays hidden for 2 hours, with a warning 5 minutes before it goes public. You can make it public now, keep it hidden longer or delete it. Turn automatic uploads off in Options to choose every upload yourself.</p><p>Every upload still waits until Civ has been in its main menu (not a staging lobby) or closed for 5 seconds and computer resources are available. Upload now does not bypass those checks. Keep the recorder open, or reopen it later.</p></section><section class="card prose"><h3>What a save includes</h3><p>Save files contain the game state, player names and potentially in-game chat. Uploading a game generates a replay; Games lets you control replay visibility.</p><p>KEK games are supplied by Oblast. Unsupported versions may need website conversion support.</p></section></div><div class="col"><section class="card prose"><h3>Leaves room for your game</h3><p>The recorder runs at lower priority. Sustained high CPU use or low available memory pauses save copying and new processing. Queued work resumes gradually when resources recover.</p><p>A deferred save can be missed if Civ overwrites or removes it before it can be copied. Keep enough autosaves in Civ.</p><p>The recorder does not change Civ's memory or original saves. Crash causality has not been established; resource reports help investigate problems.</p></section><section class="card prose"><h3>Local diagnostic reports</h3><p>Small, capped local notes record CPU, memory, upload activity and errors. A diagnostic report can include matching Windows crash records when available. Reports stay on this PC until you choose to share them.</p><p>The figures cover the recorder host; separate WebView2 window helper processes are excluded, and brief spikes can be missed. No game-memory dump or authentication settings are included. Nothing is sent automatically. Upload activity counts request data and retries, excludes HTTP/TLS overhead, and does not prove the server received a save.</p><div class="row actions"><button class="btn" data-dialog="report" data-k="report">Save diagnostic report</button>${go('Resource usage', 'checks', 'quiet')}</div></section><section class="card prose"><h3>Memory logs</h3><p>Optional game-memory observations capture moves and city changes between saves. A separate process reads supported Civ memory without injection, writes or pausing the game. Sampling can miss short actions, and compatibility or stability is not guaranteed.</p><p>Resource controls and bounded sampling apply. Turning memory logs on does not turn uploads or livestreaming on.</p><button class="btn" data-setup data-setup-step="2" data-k="help-memory">Choose memory logging</button></section><section class="card prose"><h3>Private livestreams</h3><p>${esc(S.livestream?.reason ?? 'Livestreaming needs compatible memory logging, upload policy, server support and a separate Start action on Recorder.')}</p></section></div></div>`;
    return `<h2 class="screen-title">How recording works</h2><div class="cols even"><div class="col"><section class="card prose"><h3>Save first, share later</h3><p>Civ saves each turn. The recorder keeps its own copies after the files have settled. Your original Civ saves stay where they are.</p><p>Make sure Civ autosaves every turn: Options → Game options → Turns between autosaves = 1.</p>${go('Check my setup', 'checks')}</section><section class="card prose"><h3>Recording and uploading are separate</h3><p><b>Recording</b> keeps save copies on this PC. <b>Uploading</b> sends them to ffaciv.com to generate a replay.</p><p>Choose uploads while playing in Options, or upload after the game: the replay then stays hidden for 2 hours, with a warning 5 minutes before it goes public. You can keep it hidden longer or delete it.</p><p>With automatic uploads off, games stay here until you upload them yourself. Keep the recorder open for reminders and uploads.</p></section><section class="card prose"><h3>What a save includes</h3><p>Civ saves contain the map, player names and in-game chat. Check what you want to share before publishing a replay.</p><p>KEK games are supplied by Oblast. Unsupported versions may need website conversion support.</p></section></div><div class="col"><section class="card prose"><h3>Movement recording is experimental</h3><p>Save files capture each turn. Optional movement logs fill in changes between saves by reading supported game memory from a separate process.</p><p>The reader does not write to Civ, inject code or pause the game. Read-only access reduces interference, but does not guarantee compatibility or stability.</p><p>Memory logs are ${memoryLoggingState().enabled ? 'on' : 'off'}. Choose them separately in Options. The read rate controls how often the game is sampled.</p></section><section class="card prose"><h3>Private livestreams</h3><p>Get a livestream link on Recorder while recording. It requires compatible capture settings and an available live slot.</p><p>Your private lobby is not publicly listed. Anyone with the link can watch, so share it carefully. When your game finishes and uploads, its replay goes public on ffaciv.com, with its game page.</p><p>Use Make private in Games to hide a replay, or restrict replay viewing for 14 days.</p></section><section class="card"><h3>Something went wrong?</h3><p class="small">Save a report zip and attach it when asking for help. Nothing is sent automatically.</p><button class="btn" data-dialog="report" data-k="report">Save a problem report</button></section></div></div>`;
  }

  // ---- dialogs ---------------------------------------------------------------------------------------------------------
  function dialogHtml() {
    if (!dialog) return '';
    let body = '';
    if (dialog.kind === 'livestream') {
      const l = S.livestream ?? {}, pregame = !S.current || S.current.lobby || S.current.ended;
      const hours=dialog.hours??4;
      const latestOnly=!pregame && (dialog.startCurrentSave??true);
      const blocked=latestOnly && Object.hasOwn(l,'currentSaveReason') ? l.currentSaveReason : Object.hasOwn(l,'followSetupReason') ? l.followSetupReason : l.setupReason;
      const futureOnly=!latestOnly && l.setupReason && !blocked;
      body=`<h2>Enable private livestream</h2><p>Prepare your invite before starting Civ. Anyone with the link can watch; it is not publicly listed.</p>${l.needsSetup?'<div class="banner"><div><h3>Streaming setup</h3><p>Confirming enables automatic recording and experimental player-action logs at 4 samples/second. Normal upload timing and diagnostics stay as you chose them.</p><p>Capture settings must be prepared with Civ closed or in a supported lobby. Steam can stay open.</p></div></div>':'<p class="small">Your recording settings are already prepared. Enabling this invite does not release old games for upload.</p>'}${blocked?`<p class="stream-problem" role="status">${esc(blocked)}</p>`:futureOnly?'<p class="stream-problem" role="status">This game cannot be streamed now. This link will wait for a future game; the current recording and its backlog stay excluded.</p>':''}<section class="stream-follow-summary"><h3>Follow me</h3><p class="small">One private link follows your lobbies, games and rejoins until it expires. Viewers choose when to enter a game.</p></section>${!pregame?`<ul class="checks"><li><label><input type="checkbox" data-start-current-save data-k="start-current-save-choice" ${latestOnly?'checked':''}><span>Start from current save<small>Use the latest settled autosave and continue from there. Older unsent saves stay on this PC. Previously uploaded data remains available. One full starting save may be needed; later saves follow your chosen upload method.</small></span></label></li></ul>`:''}${l.active?`<p class="small">Your existing invite link and expiry are kept.</p>`:`<label class="stream-duration">Invite expires after<select class="pick" data-stream-duration data-k="stream-duration">${[1,4,8,24].map(h=>`<option value="${h}" ${h===hours?'selected':''}>${h} hour${h===1?'':'s'}</option>`).join('')}</select></label>`}<p class="small">Private streaming only sends the authorized current game, a small save batch and a bounded batch of logs at a time. Starting from the current save skips older unsent saves without deleting them. Disabling sharing leaves your other preferences alone.</p><div class="row end"><button class="btn quiet" data-close data-k="stream-cancel">Cancel</button><button class="btn primary" data-start-private-stream data-k="stream-confirm" ${blocked?'disabled':''}>${l.needsSetup?'Enable settings & prepare link':latestOnly?'Start livestream from current save':futureOnly?'Prepare future-game link':'Enable & prepare link'}</button></div>`;
    }
    else if (dialog.kind === 'deleteGame') body = `<h2>Delete this recording?</h2>${dialog.label?`<p class="delete-recording-label">${esc(dialog.label)}</p>`:''}<p>It disappears from your list and from the website right away. For 7 days you can bring it back with Undo (under Previous games, Recently deleted); then the recorder's copy and the uploaded recording are erased for good.</p><p>If another player kept this game, their replay remains. Civ's original save files are kept.</p><div class="row end"><button class="btn quiet" data-close data-k="delete-cancel">Cancel</button><button class="btn danger" data-confirm-delete data-k="delete-confirm">Delete recording</button></div>`;
    else if (dialog.kind === 'fixed') body = `<h2>Fixed</h2><p>Civ now saves every turn. We changed two lines in <b style="color:var(--text)">${esc(dialog.file)}</b>, nothing else:</p><div class="diff">${dialog.changes.map((c) => `${esc(c.key)} = <span class="was">${esc(c.was)}</span> <span class="now">${esc(c.now)}</span>`).join('<br>')}</div><p class="small">A copy of the old file is next to it: ${esc(dialog.backup)}</p><div class="row end"><button class="btn primary" data-close data-k="close">Done</button></div>`;
    else if (dialog.kind === 'civRunning') body = `<h2>Close Civ first</h2><p>Civ writes this setting itself when it closes, so a change now would be undone. Close Civ V, then press <b style="color:var(--text)">Fix it for me</b> again.</p><p class="small">Or in Civ: Options → Game options → Turns between autosaves = 1.</p><div class="row end"><button class="btn primary" data-close data-k="close">OK</button></div>`;
    else if (dialog.kind === 'report') body = `<h2>Save a diagnostic report</h2><p>Create a report zip on your Desktop. Pick what it includes:</p><ul class="checks">
      <li><label><input type="checkbox" ${(dialog.parts ?? ['log','session','pc']).includes('log') ? 'checked' : ''} data-part="log" data-k="report-part-log"><span>Health report<small>CPU, memory, upload totals and recent activity. Matching Windows crash records when available. Host-process metrics only; WebView2 helpers are excluded. No game-memory dump.</small></span></label></li>
      <li><label><input type="checkbox" ${(dialog.parts ?? ['log','session','pc']).includes('session') ? 'checked' : ''} data-part="session" data-k="report-part-session"><span>About your last game<small>Game notes, when available; may include player names and recorded events. No saves. Omitted while Civ or the computer is busy.</small></span></label></li>
      <li><label><input type="checkbox" ${(dialog.parts ?? ['log','session','pc']).includes('pc') ? 'checked' : ''} data-part="pc" data-k="report-part-pc"><span>About this PC<small>Windows version and whether Civ's settings file exists.</small></span></label></li>
      <li><label><input type="checkbox" ${(dialog.parts ?? ['log','session','pc']).includes('save') ? 'checked' : ''} data-part="save" data-k="report-part-save"><span>The last autosave<small>The whole game at that turn. Omitted while Civ or the computer is busy.</small></span></label></li></ul>
      <label class="small" for="rep-text">What happened? (you can leave it empty)</label><textarea id="rep-text" data-k="report-text" rows="3" style="width:100%;margin:4px 0 12px;border-radius:8px;border:1px solid var(--line);background:var(--bg);color:var(--text);padding:8px;resize:vertical">${esc(dialog.text ?? '')}</textarea>
      <div class="row end"><button class="btn quiet" data-close data-k="close">Cancel</button><button class="btn primary" data-send-report data-k="send" ${S.reportBusy ? 'disabled aria-busy="true"' : ''}>${icon('save')} ${S.reportBusy ? 'Preparing report…' : 'Save report zip'}</button></div><p class="small" style="margin-top:8px">Saved locally. Nothing is sent automatically. Authentication settings are excluded.</p>${S.reportBusy ? '<p class="small" role="status">A report is being prepared. The recorder will confirm when it is saved.</p>' : ''}`;
    return `<div class="scrim" data-scrim><div class="dialog frame" role="dialog" aria-modal="true">${body}</div></div>`;
  }

  // ---- first start -----------------------------------------------------------------------------------------------------
  const SETUP_STEPS = ['Welcome', 'Recording', 'Memory', 'Uploads', 'Format', 'Autosaves', 'Review'];
  let renderedSetupStep = -1;
  let reviewReturn = false;
  function recorderAssurance() {
    return `<section class="recorder-assurance" aria-label="Outside the game, quiet in the background"><div class="assurance-points"><div>${icon('shield')}<div><h3>Reads from outside Civ</h3><p>${memoryLoggingState().enabled ? 'Memory logs are on. A separate process reads supported Civ memory without code injection, writes or pausing Civ.' : 'Memory logs are off. The recorder keeps settled save copies from outside Civ.'}</p><span>This separation reduces interference. Experimental compatibility still depends on the supported game build.</span></div></div><div>${icon('checkCircle')}<div><h3>Set up once. Then just play.</h3><p>Automatic recording keeps save copies for you and stays quietly in the tray. You choose when to share.</p><span>Resource controls pause heavy work under sustained pressure. Optional memory logging uses bounded sampling.</span></div></div></div><details class="setup-details resource-example"><summary data-k="resource-example">See an example of light background activity</summary><p>A Task Manager snapshot from a recording session:</p><img src="img/resource-example.png" width="1661" height="110" alt="Task Manager snapshot showing FFACiv Recorder with low activity readings; column headings are outside the crop." loading="lazy"><p class="small">One observed session, not a benchmark or a usage guarantee. Usage varies with your PC, game and recording mode. Disk and network activity can rise when copying or uploading saves.</p></details></section>`;
  }

  function memoryLoggingState() {
    const s = S.settings ?? {}, m = S.memoryLogging ?? {};
    return {enabled:m.enabled ?? (s.trackLogs === true),canEnable:m.canEnable ?? (s.recording !== false),reason:m.reason ?? (s.recording === false ? 'Turn automatic recording on first.' : 'Needs a supported Civ build. Resource controls still apply.'),readsPerSecond:m.readsPerSecond ?? s.looksPerSecond ?? 4,cityManagementIntervalMs:m.cityManagementIntervalMs ?? 1000,turnQuietMs:m.turnQuietMs ?? 5000};
  }

  function memoryCadenceDetails() {
    const m = memoryLoggingState();
    return `<p class="small">Movement reads: ${esc(m.readsPerSecond)} per second. City management: every ${esc(m.cityManagementIntervalMs/1000)} second(s). Reads pause for ${esc(m.turnQuietMs/1000)} seconds after an observed turn change; this reacts to a change already observed and does not predict the start of a transition.</p>`;
  }

  function memoryChoices() {
    const m = memoryLoggingState();
    const card = (enabled,title,text,tag='') => `<button class="setup-choice simple-choice ${m.enabled === enabled ? 'selected' : ''}" role="radio" aria-checked="${m.enabled === enabled}" data-memory-choice="${enabled}" data-cmd="setMemoryLogging" data-args="${attr({enabled})}" data-k="memory-${enabled}" ${pending === 'setMemoryLogging' || (enabled && !m.canEnable && !m.enabled) ? 'disabled' : ''}><span class="choice-radio">${m.enabled === enabled ? icon('check') : ''}</span><span class="choice-body"><b>${title}${tag ? `<span class="method-tag">${tag}</span>` : ''}</b><small>${text}</small></span>${m.enabled === enabled ? '<span class="choice-selected">Selected</span>' : ''}</button>`;
    return `<div class="setup-choices" role="radiogroup" aria-label="Memory logging">${card(false,'Save files only','Replay each saved turn.')}${card(true,'Add memory logs','Observe moves and city changes to help recreate play.','Recommended · Experimental')}</div>`;
  }

  function uploadDataDetails() {
    return `<details class="setup-details upload-data-details"><summary data-k="setup-data-details">What gets uploaded?</summary><p><b>Game saves:</b> map, units, cities and other saved game state. Saves may include player names and in-game chat.</p><p><b>Recording details:</b> save names/times, game identifiers, recorder version and an installation identifier for authentication and upload progress. When experimental memory logs are enabled in a supported build, selected game observations are sent too—not a raw game-memory dump.</p><p><b>Capture safety:</b> the recorder copies saves without editing the originals. The optional memory reader works outside Civ with read-only access. This reduces interference; experimental compatibility is not guaranteed. Local diagnostic reports are not automatically uploaded.</p><p><b>Sharing:</b> normal uploads create public replays on ffaciv.com. You can manage replay access in Games. A private stream is unlisted, but anyone with its invite link can watch.</p><div class="upload-size-example"><b>How smaller-upload mode can work</b><div><span>200 MB<small>local turn saves</small></span><span aria-hidden="true">→</span><span>15 MB<small>transferred data</small></span></div><p>Illustrative delta-mode example, not a measured result or a guaranteed ratio. Complete saves plus changes can avoid sending repeated data; actual sizes depend on the game.</p><p class="current-transfer-mode">${S.settings?.saveUpload === 'full' ? '<b>Whole-save mode is selected.</b> This example does not describe your current upload size.' : '<b>Local preparation is selected.</b> Actual savings vary; the site still generates the final replay.'}</p></div></details>`;
  }

  function uploadFormatChoices() {
    const format = S.settings?.saveUpload === 'full' ? 'full' : 'delta';
    const card = (key,title,text,tag='') => `<button class="setup-choice simple-choice ${format===key?'selected':''}" role="radio" aria-checked="${format===key}" data-upload-format="${key}" data-cmd="setUploadFormat" data-args="${attr({format:key})}" data-k="upload-format-${key}" ${pending==='setUploadFormat'?'disabled':''}><span class="choice-radio">${format===key?icon('check'):''}</span><span class="choice-body"><b>${title}${tag?`<span class="method-tag">${tag}</span>`:''}</b><small>${text}</small></span>${format===key?'<span class="choice-selected">Selected</span>':''}</button>`;
    return `<div class="setup-choices" role="radiogroup" aria-label="Save upload format">${card('delta','Prepare smaller uploads on this PC','Prepare save changes locally to send less data.','Recommended')}${card('full','Upload whole save files','Send complete saves; automatic recording and logs still work.')}</div>`;
  }

  function setupReview() {
    const s=S.settings??{}, m=memoryLoggingState(), n=s.notify??{};
    const memory=m.enabled ? `On <small>${esc(m.readsPerSecond)}/s · cities ${esc(m.cityManagementIntervalMs/1000)}s · quiet ${esc(m.turnQuietMs/1000)}s after turn change</small>` : 'Off · save files only';
    const item=(label,value,target)=>`<div><dt>${label}<button class="review-change" data-setup data-setup-step="${target}" data-return-review data-k="review-change-${target}" aria-label="Change ${label.toLowerCase()}">Change</button></dt><dd>${value}</dd></div>`;
    const preference=(key,on,label)=>`<button class="review-toggle" role="switch" aria-checked="${on}" aria-label="${label}" data-set="${key}" data-k="review-pref-${key}" ${pending==='setSetting'?'disabled':''}>${icon(on?'check':'x')}${on?'On':'Off'}</button>`;
    return `<div class="setup-summary setup-review"><dl>${item('Recording',s.recording!==false ? s.recordSinglePlayer?'Automatic · multiplayer + single-player':'Automatic · multiplayer' : 'Manual saves',1)}${item('Memory logs',memory,2)}${item('Upload timing',s.stream!==false?(s.uploadTiming==='during'?'Automatic · while playing':'Automatic · after the game, public 2 h later'):'Only when you choose',3)}${item('Upload method',s.saveUpload==='full'?'Whole saves · site processing':'Local preparation · smaller uploads',4)}<div><dt>Start with Windows</dt><dd>${preference('startWithWindows',!!s.startWithWindows,'Start with Windows')}</dd></div><div><dt>Recording popup</dt><dd>${preference('notify.lobby',n.lobby!==false,'Recording popup')}</dd></div></dl></div>`;
  }

  function welcome() {
    if (!S.firstRun && !setupOpen) return '';
    const s = S.settings ?? {}, n = s.notify ?? {};
    const auto = (S.checks ?? []).find(c => c.id === 'autosave');
    const recording = s.recording !== false, uploading = s.stream !== false;
    const captureMethod = !recording ? 'manual' : s.trackLogs === true ? 'memory' : 'saves';
    const choice = (value, title, text) => `<button class="setup-choice simple-choice ${uploading === value ? 'selected' : ''}" role="radio" aria-checked="${uploading === value}" data-set-choice="stream" data-choice="${value}" data-k="setup-share-${value}" ${pending === 'setSetting' ? 'disabled' : ''}><span class="choice-radio">${uploading === value ? icon('check') : ''}</span><span class="choice-body"><b>${title}</b><small>${text}</small></span>${uploading === value ? '<span class="choice-selected">Selected</span>' : ''}</button>`;
    const method = (key, title, text, tag='') => {
      const selected = key === 'saves' ? recording : !recording;
      return `<button class="setup-choice simple-choice capture-choice ${selected ? 'selected' : ''}" role="radio" aria-checked="${selected}" data-capture-method="${key}" data-k="capture-${key}" ${pending === 'setCaptureMethod' ? 'disabled' : ''}><span class="choice-radio">${selected ? icon('check') : ''}</span><span class="choice-body"><b>${title}${tag ? `<span class="method-tag">${tag}</span>` : ''}</b><small>${text}</small></span>${selected ? '<span class="choice-selected">Selected</span>' : ''}</button>`;
    };
    let body = '';
    if (step === 0) body = `<h1 id="setup-title" data-k="setup-title" tabindex="-1">Welcome to FFACiv Recorder</h1><p class="intro">Choose how to save your games and when to share them.</p>`;
    else if (step === 1) body = `<h1 id="setup-title" data-k="setup-title" tabindex="-1">How should your games be saved?</h1><p class="intro">Local copies first; uploads follow your choice.</p><div class="setup-choices" role="radiogroup" aria-label="Recording method">${method('saves','Save games automatically','Stage local save copies for your chosen upload.','Recommended')}${method('manual','Keep my own saves','Keep saves yourself, then choose what to upload.')}</div><section class="setup-secondary">${sw('recordSinglePlayer',s.recordSinglePlayer,'Include single-player games','',!recording)}</section><details class="setup-details"><summary data-k="setup-capture-details">How recording works</summary><p><b>Automatic:</b> settled autosave copies are staged locally while waiting for your chosen upload. Copying a save is not uploading it, and it is not a promise of permanent archival. Retained copies remain until they are removed or deleted.</p><p><b>Manual:</b> you collect the saves yourself and choose a game to upload later. Upload timing and upload format are separate choices.</p>${recorderAssurance()}</details>`;
    else if (step === 2) body = `<h1 id="setup-title" data-k="setup-title" tabindex="-1">Recreate more of the play?</h1><p class="intro">${recording ? 'Lightweight action logs for richer replays. Optional and experimental.' : 'Memory logs need automatic recording.'}</p>${memoryChoices()}<p class="memory-readonly">Read-only, from outside Civ. No code injection or game-memory writes.</p><p class="memory-requirement">${esc(memoryLoggingState().reason)}</p><details class="setup-details"><summary data-k="setup-memory-details">How memory logs work</summary><p>A separate process samples known fields in a supported Civ build. Comparing observations helps recreate moves and city choices between saves; it does not capture every click or action, and short changes can be missed.</p><p>This separation reduces interference: no game-memory writes, injected code or pausing Civ. It does not guarantee compatibility or stability. Resource controls and bounded sampling still apply.</p>${memoryCadenceDetails()}<p>Logs are lightweight sampled observations rather than a raw memory dump. <b>20 KB per turn is an illustrative estimate only</b>, not a measurement or limit; actual sizes depend on activity and recording data.</p><p>Logs stay local while you play, then follow your chosen upload timing. Both whole saves and smaller uploads support memory logs and automatic recording. This switch never starts a livestream.</p>${recorderAssurance()}</details>`;
    else if (step === 3) body = `<h1 id="setup-title" data-k="setup-title" tabindex="-1">When should games upload?</h1>${uploadTimingChoice()}<p class="intro setup-public-notice">Choose WHEN. Uploaded games become <b>public replays</b>.</p><div class="setup-choices" role="radiogroup" aria-label="Automatic upload preference">${choice(true,'Automatically',s.uploadTiming==='during'?'Upload as saves arrive while playing.':'Upload after the game; public 2 hours later.')}${choice(false,'Only when I choose','Keep games here until I choose Upload now.')}</div><p class="setup-selected-summary" role="status">${uploading ? 'Selected: automatic uploads.' : 'Selected: manual uploads only.'}</p><details class="setup-details"><summary data-k="setup-upload-details">Upload timing and publication</summary><p>${s.uploadTiming==='during'?'Automatic uploads send the current recording while playing.':'Automatic uploads start when you leave the game; the replay stays hidden for 2 hours, with a warning 5 minutes before it goes public.'} You can upload now, postpone or delete the recorder’s copy; Civ’s originals are kept.</p><p>Manual means you choose Upload now. ${s.uploadTiming==='during'?'While-playing uploads send the current recording as saves arrive, subject to resource controls and per-game holds.':'Uploads wait until Civ is in its main menu (not a staging lobby) or closed, and computer resources are available.'} Keep the recorder running or reopen it later.</p><p>This choice changes timing only. It does not change automatic recording, memory logging or upload format. Games lets you make a replay private or restrict viewing for 14 days. Turning uploads off does not remove existing uploads.</p></details>`;
    else if (step === 4) body = `<h1 id="setup-title" data-k="setup-title" tabindex="-1">How should saves be uploaded?</h1><p class="intro">Choose HOW files transfer; recording and timing stay independent.</p>${uploadFormatChoices()}<p class="setup-format-scale">Illustrative only: 200 MB whole → 15 MB prepared. Actual sizes vary.</p><details class="setup-details"><summary data-k="setup-format-details">What changes between these methods?</summary><p><b>Local preparation</b> compares save files on this PC, sends changes and includes complete saves when needed. It can transfer less repeated data, at the cost of some local processing. Resource controls still apply.</p><p><b>Whole saves</b> sends each complete save and leaves save processing to the site. It avoids preparing local changes but can transfer more data. It supports automatic recording and memory logs too.</p><p>Both methods leave Civ’s originals untouched and let the site generate the final replay. Neither changes your automatic or manual upload timing.</p></details>`;
    else if (step === 5) body = `<h1 id="setup-title" data-k="setup-title" tabindex="-1">Check your autosaves</h1><p class="intro">A complete replay needs a save for every turn.</p><section class="card setup-autosave"><div class="item">${icon(auto?.level === 'ok' ? 'checkCircle' : 'alert','state-ic '+(auto?.level ?? 'info'))}<div class="item-body"><h3>${esc(auto?.title ?? 'Checking Civ’s autosave settings…')}</h3>${auto?.action ? `<div class="row actions">${btn(esc(auto.action.label),auto.action.cmd,auto.action.args,'primary',auto.action.disabled ? 'disabled' : '')}</div>${auto.action.disabled ? `<p class="small">${esc(auto.action.why ?? 'Close Civ to fix this.')}</p>` : ''}` : ''}</div></div></section><details class="setup-details"><summary data-k="setup-autosave-details">Fix manually or see what changes</summary><p>${esc(auto?.text ?? 'Start Civ V once so the recorder can find its settings file.')}</p><p>In Civ: Options → Game options → Turns between autosaves = <b>1</b>. Keep at least 10 autosaves.</p><p>The automatic fix changes only the autosave settings and keeps a backup. Civ must be closed first. You can continue setup and fix this before your next game.</p></details>`;
    else body = `<h1 id="setup-title" data-k="setup-title" tabindex="-1">Review your setup</h1><p class="intro">Change any choice below, then finish.</p>${setupReview()}`;
    if (step === 4) body = body.replace('<details class="setup-details">', uploadDataDetails() + '<details class="setup-details">');
    body = body.replaceAll('id="l-', 'id="welcome-l-').replaceAll('aria-labelledby="l-', 'aria-labelledby="welcome-l-').replaceAll('data-k="sw-', 'data-k="welcome-sw-');
    const saved = feedback && ['warn','bad'].includes(feedback.kind) && !dialog ? `<div class="setup-feedback ${esc(feedback.kind)}" role="status">${icon(feedback.kind === 'warn' || feedback.kind === 'bad' ? 'alert' : 'checkCircle')}<span>${esc(feedback.title)}</span></div>` : '';
    const nextLabels = ['Next', 'Next', 'Next', 'Next', 'Next', 'Next'];
    const next = reviewReturn && step < SETUP_STEPS.length-1 ? `<button class="btn primary" data-step="${SETUP_STEPS.length-1-step}" data-k="return-review">Return to review</button>` : step < SETUP_STEPS.length-1 ? `<button class="btn primary" data-step="1" data-k="next">${nextLabels[step]} ${icon('chevron')}</button>` : btn('Finish setup', 'finishFirstRun', {}, 'primary');
    const foot = `${setupOpen && !S.firstRun ? '<button class="btn quiet" data-exit-setup data-k="exit-setup">Close walkthrough</button>' : ''}${step > 0 ? '<button class="btn" data-step="-1" data-k="prev">Back</button>' : ''}<span class="setup-step-count">Step ${step+1} of ${SETUP_STEPS.length}<small class="setup-scroll-cue" hidden>Scroll for more details</small></span>${next}`;
    return `<div class="welcome onboarding" role="dialog" aria-modal="true" aria-labelledby="setup-title"><div class="onboarding-top"><span class="onboarding-brand">FFACiv Recorder <span>GET STARTED</span></span><ol class="setup-progress" aria-label="Setup progress">${SETUP_STEPS.map((label,i)=>`<li class="${i===step?'current':i<step?'complete':''}" ${i===step?'aria-current="step"':''}><span>${i<step?icon('check'):i+1}</span><b>${label}</b></li>`).join('')}</ol></div><div class="welcome-body"><div class="welcome-content">${body}${saved}</div></div><div class="welcome-foot">${foot}</div></div>`;
  }

  // ---- drawing -----------------------------------------------------------------------------------------------------------
  const SCREENS = { home, streaming, games, requests, settings, help, updates, checks, old: oldGames };
  const TABS = [['home', 'Recorder', 'record'], ['streaming', 'Streaming', 'requests'], ['games', 'Previous games', 'games'], ['old', 'Manual upload', 'up'], ['requests', 'Requests', 'requests'], ['updates', 'Updates', 'download'], ['settings', 'Options', 'settings'], ['help', 'Help', 'help']];
  const tabOf = (r) => r === 'checks' ? 'home' : r;
  let lastScreen = null;
  let lastOverlayHtml = null;
  function draw() {
    if (!S) return;
    const focusKey = document.activeElement?.dataset?.k;
    const reportText = document.querySelector('#rep-text');
    const reportSelection = reportText ? [reportText.selectionStart,reportText.selectionEnd] : null;
    if(dialog?.kind === 'report' && reportText) {dialog.text=reportText.value;dialog.parts=[...document.querySelectorAll('[data-part]:checked')].map(el=>el.dataset.part);}
    const replayDetails = new Set([...document.querySelectorAll(".replay-settings[open], .saves-list[open]")].map(d=>d.dataset.detailsKey));
    const setupScroll = document.querySelector('.welcome-body')?.scrollTop ?? 0;
    const setupDetails = [...document.querySelectorAll('.welcome details')].map(d => d.open);
    const sameSetupStep = renderedSetupStep === step;
    const kind = S.status?.kind ?? 'waiting';
    $('#mark').className = 'mark ' + (problems().length && kind === 'recording' ? 'attention' : kind);
    $('#nav').innerHTML = TABS.filter(([r]) => !S.production || r !== 'requests').map(([r, t, ic]) => {
      const n = r === 'requests' ? (S.requests?.length ?? 0) : 0;
      const upd = r === 'updates' && S.updates?.available;
      return `<button class="tab${r === 'updates' ? ' wide-only' : ''}" data-go="${r}" data-k="tab-${r}" ${tabOf(route) === r ? 'aria-current="page"' : ''}>${icon(ic)}<span>${t}</span>${n ? `<span class="dot" aria-label="${n} new">${n}</span>` : upd ? `<span class="dot${S.updates.available.required ? '' : ' new'}">1</span>` : ''}</button>`;
    }).join('');
    const sidebarTitle = S.settings?.recording === false ? 'Recorder is off' : S.current?.lobby ? 'In the multiplayer lobby' : S.status?.kind === 'waiting' ? S.civDetected ? 'Civ detected' : 'Waiting for Civ' : S.status?.title ?? '';
    $('#sidestate').innerHTML = `<span class="mark ${trayKind()}"></span><div><b>${esc(sidebarTitle)}</b>${esc('Version ' + S.version)}</div>`;
    const view = $('#view');
    const scroll = lastScreen === route ? view.scrollTop : 0;
    view.innerHTML = `<div class="screen${lastScreen === route ? '' : ' enter'}" style="${lastScreen === route ? 'animation:none' : ''}">${feedback ? `<div class="action-feedback ${esc(feedback.kind)}" role="status" aria-live="polite">${icon(feedback.kind === 'pending' ? 'refresh' : feedback.kind === 'info' ? 'info' : feedback.kind === 'warn' || feedback.kind === 'bad' ? 'alert' : 'checkCircle')}<div><b>${esc(feedback.title)}</b>${feedback.text ? `<span>${esc(feedback.text)}</span>` : ''}${feedback.actions?.length ? `<div class="row">${feedback.actions.map(a => btn(esc(a.label), a.cmd, a.args ?? {}, a.primary ? 'primary' : 'quiet')).join('')}</div>` : ''}</div><button class="icon-btn" data-dismiss-feedback aria-label="Dismiss">${icon('x')}</button></div>` : ''}${(SCREENS[route] ?? home)()}</div>`;
    if(lastScreen === route) view.querySelectorAll(".replay-settings, .saves-list").forEach(d=>d.open=replayDetails.has(d.dataset.detailsKey));
    view.scrollTop = scroll; lastScreen = route;
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
      onboarding.inert = !!dialog;
      if (sameSetupStep) onboarding.querySelectorAll('details').forEach((d,i) => d.open = setupDetails[i] ?? false);
      document.querySelector('.welcome-body').scrollTop = sameSetupStep ? setupScroll : 0;
      updateSetupScrollCue();
    }
    $('#app').inert = !!onboarding || !!dialog;
    renderedSetupStep = onboarding ? step : -1;
    document.body.classList.toggle('hide-preview', !Bridge.demo);
    if (Bridge.demo) {
      $('#preview').innerHTML = `<span>Preview · made-up data</span><select aria-label="Preview situation" data-scenario>${Object.entries(Mock.scenarios).map(([k, v]) => `<option value="${k}" ${Mock.scenario === k ? 'selected' : ''}>${v.label}</option>`).join('')}</select>`;
      Bridge.toHost('ui.tray', { kind: trayKind(), tip: trayTip() });
    }
    const focusScope = document.querySelector('.dialog') ?? onboarding ?? document;
    const f = focusKey && focusScope.querySelector(`[data-k="${CSS.escape(focusKey)}"]`);
    if (f && !f.disabled && (!onboarding || sameSetupStep || dialog)) {if(document.activeElement !== f)f.focus({ preventScroll: true });if(f.id === 'rep-text' && reportSelection)f.setSelectionRange(...reportSelection);}
    else if (onboarding && !dialog) onboarding.querySelector('#setup-title').focus({preventScroll:true});
    else if (menu) document.querySelector('.menu button')?.focus();
    else if (dialog) document.querySelector('.dialog button:not(:disabled), .dialog input:not(:disabled), .dialog select:not(:disabled)')?.focus({preventScroll:true});
  }
  function updateSetupScrollCue() {
    const body = document.querySelector('.welcome-body'), cue = document.querySelector('.setup-scroll-cue');
    if(body && cue) cue.hidden = body.scrollHeight - body.clientHeight - body.scrollTop < 5;
  }
  document.addEventListener('scroll', e => {if(e.target.matches?.('.welcome-body'))updateSetupScrollCue();}, true);
  document.addEventListener('toggle', e => {if(e.target.closest?.('.welcome'))updateSetupScrollCue();}, true);
  function trayKind() {
    const k = S.status?.kind;
    if (k === 'attention' || k === 'unsupported' || (S.updates?.available?.required)) return 'attention';
    if (k === 'recording') return problems().length ? 'attention' : 'recording';
    if (k === 'sending') return 'sending';
    if (k === 'paused') return 'paused';
    return 'waiting';
  }
  const trayTip = () => `FFACiv Recorder · ${S.status?.title ?? ''}`.slice(0, 120);
  // countdowns tick without redrawing the screen
  setInterval(() => document.querySelectorAll('[data-until]').forEach((e) => { e.textContent = until(+e.dataset.until); }), 15e3);

  function navigate(r) { route = r; menu = null; location.hash = r; draw(); $('#view').focus({ preventScroll: true }); }

  function send(cmd, args = {}) {
    pending = cmd;
    feedback = cmd === 'sendReport' ? {kind:'pending',title:'Preparing your local report…',text:'The recorder will confirm when the zip is saved. Nothing is uploaded.'} : {kind:'pending',title:'Working…',text:'Waiting for the recorder to confirm.'};
    clearTimeout(pendingTimer);
    pendingTimer = setTimeout(() => { if (pending === cmd) {pending = null;feedback = {kind:'warn',title:'Still waiting for confirmation',text:'Check your connection and try again if needed.'};draw();} }, 20000);
    draw();
    Bridge.send(cmd, args);
  }

  // ---- input ---------------------------------------------------------------------------------------------------------------
  document.addEventListener('click', (e) => {
    const t = e.target.closest('button, [data-scrim]');
    if (!t) { if (menu) { menu = null; draw(); } return; }
    if (t.matches('[data-scrim]') && e.target === t) { dialog = null; draw(); return; }
    if (t.disabled) return;
    if(t.hasAttribute('data-start-private-stream') && dialog?.kind==='livestream') {const scope='player',durationHours=dialog.hours??4,configure=S.livestream?.needsSetup===true,startCurrentSave=!!S.current&&!S.current.lobby&&!S.current.ended&&(dialog.startCurrentSave??true);dialog=null;send('startLivestream',{scope,durationHours,configure,startCurrentSave});draw();return;}
    if (t.hasAttribute('data-dismiss-feedback')) {feedback = null; draw(); return;}
    if (t.hasAttribute('data-exit-setup')) {setupOpen = false; draw(); return;}
    if (t.hasAttribute('data-setup')) {setupOpen = true; reviewReturn = t.hasAttribute('data-return-review'); step = Number(t.dataset.setupStep ?? 0); draw(); return;}
    if (t.dataset.menu) { menu = menu === t.dataset.menu ? null : t.dataset.menu; draw(); return; }
    if (menu && !t.closest('.menu')) menu = null;
    if (t.dataset.go) { navigate(t.dataset.go); return; }
    if (t.dataset.filter) { gamesFilter = t.dataset.filter; draw(); return; }
    if (t.dataset.step) { step = Math.max(0, Math.min(SETUP_STEPS.length-1, step + +t.dataset.step)); draw(); return; }
    if (t.dataset.delete) { const game=(S.games??[]).find(g=>g.id===t.dataset.delete)??S.current;dialog = {kind:'deleteGame', game:t.dataset.delete, context:t.dataset.context || null,label:game?recordingLabel(game):null}; draw(); return; }
    if (t.hasAttribute('data-confirm-delete') && dialog?.kind === 'deleteGame') { const args = {game:dialog.game,context:dialog.context};pendingDeletion={id:dialog.game,label:dialog.label};dialog = null; send('deleteGame',args); draw(); return; }
    if (t.dataset.dialog) { dialog = { kind: t.dataset.dialog }; draw(); return; }
    if (t.hasAttribute('data-close')) { dialog = null; draw(); return; }
    if (t.hasAttribute('data-send-report')) {
      const parts = [...document.querySelectorAll('[data-part]')].filter((c) => c.checked).map((c) => c.dataset.part);
      dialog = null; send('sendReport', { parts, text: $('#rep-text')?.value ?? '' }); return;
    }
    if (t.dataset.pick) { const id = t.dataset.pick; picked.has(id) ? picked.delete(id) : picked.add(id); draw(); return; }
    if (t.dataset.cmd === 'ui.pickAll') { const can = (S.oldGames?.games ?? []).filter(pickable); const all = can.every((g) => picked.has(g.id)); can.forEach((g) => all ? picked.delete(g.id) : picked.add(g.id)); draw(); return; }
    if (t.dataset.cmd === 'uploadOldGames') { const ids = [...picked]; picked.clear(); send('uploadOldGames', { ids }); draw(); return; }
    if (t.dataset.captureMethod) { send('setCaptureMethod', {method:t.dataset.captureMethod}); return; }
    if (t.dataset.setChoice) { send('setSetting', {key:t.dataset.setChoice,value:t.dataset.choice === 'true'}); return; }
    if (t.dataset.set) { send('setSetting', { key: t.dataset.set, value: t.getAttribute('aria-checked') !== 'true' }); return; }
    if (t.dataset.cmd) { menu = null; if(t.dataset.cmd === 'finishFirstRun') {setupOpen=false;route='home';location.hash='home';} send(t.dataset.cmd, JSON.parse(t.dataset.args || '{}')); draw(); }
  });
  document.addEventListener('change', (e) => {
    const t = e.target;
    if(t.hasAttribute('data-start-current-save') && dialog?.kind==='livestream') {dialog.startCurrentSave=t.checked;draw();return;}
    if(t.hasAttribute('data-stream-duration') && dialog?.kind==='livestream') {dialog.hours=+t.value;lastOverlayHtml=welcome()+dialogHtml();return;}
    if (t.dataset.setSelect) send('setSetting', { key: t.dataset.setSelect, value: t.dataset.setSelect === 'uploadTiming' ? t.value : +t.value });
    if (t.dataset.scenario !== undefined) { step = 0; dialog = null; Bridge.scenario(t.value); }
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
    if ((document.activeElement?.dataset.setChoice || document.activeElement?.dataset.captureMethod || document.activeElement?.dataset.memoryChoice !== undefined || document.activeElement?.dataset.uploadFormat !== undefined) && ['ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(e.key)) {
      const choices = [...document.activeElement.closest('[role=radiogroup]').querySelectorAll('[role=radio]:not(:disabled)')];
      const direction = ['ArrowUp','ArrowLeft'].includes(e.key) ? -1 : 1;
      const other = choices[(choices.indexOf(document.activeElement)+direction+choices.length)%choices.length];
      if (other) {other.focus();other.click();e.preventDefault();}
    }
    if (e.key === 'Escape') { if (menu) { const k = menu; menu = null; draw(); document.querySelector(`[data-menu="${k}"]`)?.focus(); } else if (dialog) { dialog = null; draw(); } else if (route === 'checks') navigate('home'); }
    if (menu && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
      const items = [...document.querySelectorAll('.menu button')], i = items.indexOf(document.activeElement);
      items[(i + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus(); e.preventDefault();
    }
  });
  window.addEventListener('hashchange', () => { const r = location.hash.slice(1) || 'home'; if (r !== route) { route = r; draw(); } });

  Bridge.on((msg) => {
    if (msg.type === 'state') { S = msg.state; draw(); window.__ready = true; }
    else if (msg.type === 'result') {
      if (msg.cmd === pending) {pending=null;clearTimeout(pendingTimer);}
      if(msg.toast) feedback=msg.toast;
      else if(feedback?.kind === 'pending' && !pending) {
        const labels = {setSetting:'Option saved',setCaptureMethod:'Recording method saved',setMemoryLogging:'Memory logging preference saved',setUploadFormat:'Upload method saved',pauseGame:'Recording control applied',openFolder:'Opened recordings folder',openSite:'Opened in your browser',finishFirstRun:'Setup complete',checkUpdates:'Checking for updates',scanOldGames:'Looking for games',updateNow:'Opened download page',updateLater:'Update reminder postponed'};
        feedback = msg.dialog ? null : {kind:'good',title:labels[msg.cmd] ?? 'Request received',text:'The recorder has confirmed your action.'};
      }
      if(msg.cmd==='deleteGame' && pendingDeletion) {
        if(feedback && ['good','info'].includes(feedback.kind)) {
          const remaining=S.games?.length??0;
          feedback={...feedback,text:`${pendingDeletion.label?pendingDeletion.label+'. ':''}${feedback.text??''} ${remaining} recording${remaining===1?'':'s'} remain in your list.`};
        }
        pendingDeletion=null;
      }
      if(msg.dialog) dialog=msg.dialog;
      draw();
    }
    else if(msg.type === 'command' && msg.cmd === 'showOptions') navigate('settings');
  });
  // for the pictures: open a dialog / a first-start step / a menu
  return { start: () => Bridge.start(), open: (kind) => { dialog = { kind }; draw(); }, result: (d) => { dialog = d; draw(); }, step: (n) => { step = Math.max(0,Math.min(SETUP_STEPS.length-1,n)); draw(); }, setup: (n=0) => { setupOpen=true;reviewReturn=false;dialog=null;step=Math.max(0,Math.min(SETUP_STEPS.length-1,n));draw(); }, menu: (k) => { menu = k; draw(); }, go: navigate, get state() { return S; } };
})();
App.start();
