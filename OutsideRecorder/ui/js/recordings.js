// Recordings: the current game (or the lobby), each recording's upload plan, progress, Saves list and replay controls, and
// the Previous games screen with its recently deleted ones.
// Reads the shared window state `ui` (js/app.js); the files load in the order of index.html.
'use strict';
// the multiplayer staging room (OR03 "Lobby / channel"): who is in, their civs, the map and the timer; the channel link
function lobbyCard(c) {
  const humans = c.players.filter((p) => p.human).length;
  const seated = c.players.filter((p) => p.team != null && p.name !== 'Open'), teams = new Set(seated.map((p) => p.team));
  let h = `<section class="card"><div class="game-head"><div style="flex:1;min-width:0"><h3>${modTag(c)} Multiplayer lobby</h3><div class="sub">${esc([c.gameName, `${humans} ${humans === 1 ? 'player' : 'players'} in`].filter(Boolean).join(' · '))}</div></div><span class="badge countdown">Lobby</span></div>`;
  const share=ui.S.livestream??{};
  h += `<div class="row actions">${btn('Refresh lobby','refreshLobby',{},'quiet')}${share.active?`${btn('Copy invite link','copyLiveLink',{},'primary')}${btn('Stop livestream','stopLivestream',{},'danger')}`:`<button class="btn primary" data-dialog="livestream" data-k="get-livestream-link" ${share.canStart===true?'':'disabled'}>${icon('link')} Get livestream link</button>`}</div>`;
  h += `<div class="players">${c.players.map((p) => `<span class="player${p.you ? ' you' : ''}"><i style="--c:${esc(p.color)}"></i>${esc(p.you ? 'You' : p.name)} · ${esc(p.civ)}${teams.size > 1 && teams.size < seated.length && p.name !== 'Open' ? ` · team ${p.team + 1}` : ''}</span>`).join('')}</div>`;
  h += `<div class="facts"><div class="fact"><b>${esc(c.map ?? '–')}</b><span>Map${c.mapSize ? ` · ${esc(c.mapSize)}` : ''}</span></div><div class="fact"><b>${c.turnTimerSeconds != null ? `${Math.floor(c.turnTimerSeconds / 60)}:${String(c.turnTimerSeconds % 60).padStart(2, '0')}` : c.turnTimerOn ? 'On' : 'Off'}</b><span>Turn timer</span></div><div class="fact"><b>${c.options ?? '–'}</b><span>Options on</span></div></div>`;
  h += `<div class="publish"><div class="publish-line">${icon('record')}<span>Get a private follow link now. Viewers wait here until your game starts.</span></div>${share.active?liveLinkRow():share.canStart===true?'':`<p class="small">${esc(share.reason??'Enable memory logs for a supported lobby first.')}</p>`}</div>`;
  return h + '</section>';
}
function currentGame(c) {
  if (c.lobby) return lobbyCard(c);
  const upl = c.upload ?? {};
  const status = c.ended ? (upl.state === 'sending' ? 'sending' : 'locked') : 'recording';
  const sub = c.ended ? `Ended ${c.lastSaveAt ? ago(c.lastSaveAt) : ''}` : `${c.startedAt ? `Started ${clock(c.startedAt)}` : 'Game detected'}${c.lastSaveAt ? ` · last save ${ago(c.lastSaveAt)}` : ''}`;
  let h = `<section class="card"><div class="game-head"><div style="flex:1;min-width:0"><h3>${modTag(c)} ${esc(c.ended ? 'Your last game' : cap(c.mode ?? 'game'))}</h3><div class="sub">${esc(sub)}</div></div>${c.paused ? '<span class="badge withdrawn">Paused</span>' : badge(status)}</div>`;
  if (c.players?.length) h += playerChips(c.players);
  const sent = upl.state === 'off' ? 'Disabled' : upl.state === 'held' ? 'Waiting' : upl.state === 'queued' ? `${upl.queued} waiting` : upl.lastSentTurn != null ? `Turn ${upl.lastSentTurn}` : '–';
  h += `<div class="facts"><div class="fact"><b>${c.turn ?? '–'}</b><span>Turn</span></div><div class="fact"><b>${c.players?.length ?? '–'}</b><span>Players${c.cityStates ? ` · ${c.cityStates} city-states` : ''}</span></div><div class="fact" title="${esc(upl.note??'')}"><b>${esc(sent)}</b><span>${upl.state==='held'?'Upload timing':upl.state==='off'?'Uploads':upl.state === 'queued' ? 'Saves to send' : 'Sent up to'}</span></div></div>`;
  h += replayControls({...c, turns:c.turn});
  if (!c.ended) h += `<div class="row actions">${gameStreamActions(c)}${btn(`${icon('folder')} Open save folder`, 'openFolder', { game: c.id })}</div>`;
  return h + '</section>';
}
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
  let line = r.completionQueued ? 'Finish requested' :!acknowledged ? 'Saved on this PC' : r.state === 'deleted' ? 'Short game deleted' : r.state === 'processing' ? '' : r.state === 'ready' || g.url ? 'Replay generated and stored' : r.finishAt ? `${short && !kept ? 'Deleted' : 'Finished'} ${untilSpan(r.finishAt)} without a new save or log` : g.uploadSchedule?.state === 'upload' ? '' : 'Waiting for the server to acknowledge this recording';
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
  else if (restricted) line += ` · Page listed; replay locked until ${esc(new Date(restrictedUntil).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }))}`;
  options += `<button class="btn quiet danger" data-delete="${esc(g.id)}" data-context="${esc(context ?? '')}" data-k="delete-${esc(g.id)}">${icon('x')} Delete recording</button>`;
  const active = !g.ended && (g.status === 'recording' || g.context === ui.S.current?.context && !ui.S.current?.ended);
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
    // A save sent after the site's list was read is simply not on that list yet (the user's T1, 2026-11-05: "Sent, but the
    // site no longer lists it" for a save sent seconds before); only an older save missing from it is really gone.
    const sentAt = /^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})-/.exec(s.id), sent = sentAt ? Date.UTC(+sentAt[1], sentAt[2] - 1, +sentAt[3], +sentAt[4], +sentAt[5], +sentAt[6]) : NaN;
    const newer = listed && Number.isFinite(sent) && !(sent <= (g.replay?.lastActivity ?? -Infinity));
    return { ...s, kind: listed && !newer ? 'muted' : 'ok', words: newer ? 'Sent · the site lists it at its next check' : listed ? 'Sent, but the site no longer lists it' : s.removed ? 'On the site · copy removed from this PC' : 'On the site' };
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
const heldCards = () => (ui.S.deleted ?? []).map((d) => ({ ...(d.card ?? {}), held: d }));
function deletedCard(g) {
  const d = g.held;
  return `<article class="card game-row deleted-row"><div class="gr-main">${gameTop(g, '', '<span class="badge withdrawn">Deleted</span>')}<div class="small" style="margin-top:6px">Hidden from you and the website. Erased for good ${untilSpan(d.due)} unless you undo it.</div></div><div class="row gr-acts">${btn(`${icon('refresh')} Undo delete`, 'undoDelete', { key: d.key }, 'primary')}</div></article>`;
}
// a finished game uploads once Civ is closed, hidden, and goes public 2 hours later (the user, 2026-11-02 and 2026-11-09)
function uploadSchedule(g) {
  const q = g.uploadSchedule;
  if (!q || !q.ended) return '';
  const a = {game:g.id};
  // what happens and when, the one button that does it now, a menu to wait longer (2 hours, a day, or the last choice)
  const plan = (sign, title, text, primary, menuKey, labels, last) => `<div class="upload-plan ${q.warning ? 'urgent' : ''}"><div class="row">${icon(sign)}<b>${title}</b></div><p class="small">${text}</p><div class="row">${btn(primary, 'uploadGameNow', a, 'primary')}${dropdown(menuKey, labels[0], [{label:labels[1],cmd:'postponeUpload',args:{...a,hours:2}},{label:labels[2],cmd:'postponeUpload',args:{...a,hours:24}},last])}</div></div>`;
  if (q.state === 'waiting' && q.hidden && q.dueAt) return plan('lock', `Hidden · public on ffaciv.com ${untilSpan(q.dueAt)}`, 'Only you can see it until then. Keep it hidden longer, or delete it under Replay settings.',
    `${icon('globe')} Make public now`, 'hide-'+g.id, ['Keep hidden', '2 more hours', '1 more day'], {label:'Until I make it public',cmd:'replayChoice',args:{...a,context:g.context,private:true}});
  if (g.url || ['ready','processing'].includes(g.replay?.state)) return '';
  if (q.state === 'upload') return recordingProgress(g);
  const held = q.held || q.enabled === false;
  return plan(held ? 'pause' : 'clock', held ? q.enabled === false ? 'Automatic uploads are off' : 'Upload postponed' : q.dueAt ? 'Uploads once Civ is closed · public 2 hours later' : 'Ready to upload',
    held ? 'Your recording stays on this PC until you choose to upload.' : 'It stays hidden on ffaciv.com for 2 hours after the upload, so you can keep it hidden or delete it.',
    held ? `${icon('up')} Upload now` : `${icon('globe')} Make public now`, 'upload-'+g.id,
    held ? ['Postpone', 'For 2 hours', 'For 1 day'] : ['Keep hidden', '2 more hours', '1 more day'], {label:held ? 'Until I upload' : 'Keep it on this PC',cmd:'postponeUpload',args:{...a,hours:0}});
}
function gameCard(g) {
  const you = g.players.find((p) => p.you);
  const civs = g.players.map((p) => `<span style="white-space:nowrap"><span class="civdot" style="background:${esc(p.color)}"></span>${esc(p.civ)}${p.you ? ' (you)' : ''}</span>`).join(' · ');
  const line = g.status === 'recording' ? 'Recording now' : g.replay?.state === 'ready' ? 'Replay ready' : (g.sentSaves ?? 0) > 0 ? `${g.sentSaves} saves uploaded · ${g.waitsForMenu ? 'replay made after you close Civ' : 'replay not made yet'}` : g.uploadSchedule?.ended ? 'Game ended · saved on this PC' : 'Local recording';
  const acts = (g.folder ? btn(`${icon('folder')} Open folder`, 'openFolder', {game:g.id}, 'quiet') : '') + gameStreamActions(g) + replayControls(g);
  return `<article class="card game-row"><div class="gr-main">${gameTop(g, g.players.length ? ` · ${g.players.length} players` : '', (g.blocked ? '<span class="badge queued">Blocked</span>' : '') + badge(g.private || g.replay?.private ? 'locked' : g.restrictedUntil > Date.now() ? 'restricted' : g.replay?.state === 'processing' ? 'processing' : g.replay?.state === 'ready' ? 'public' : g.status))}${civs ? `<div class="civs">${civs}</div>` : ''}<div class="small" style="margin-top:6px">${line}</div></div><div class="row gr-acts">${acts}</div></article>`;
}
function games() {
  const all = ui.S.games ?? [];
  const pick = { all: () => true, private: (g) => g.private || g.replay?.private, public: (g) => g.url && !g.private && !g.replay?.private };
  const shown = all.filter(pick[ui.gamesFilter]);
  const chip = (k, t) => `<button class="chip" data-filter="${k}" aria-pressed="${ui.gamesFilter === k}" data-k="f-${k}">${t}</button>`;
  let h = `<h2 class="screen-title">Previous games${ui.S.oldGames ? `<span style="flex:1"></span>${go(`${icon('up')} Manual upload${ui.S.oldGames.newCount ? ` · ${ui.S.oldGames.newCount}` : ''}`, 'old', 'primary')}` : ''}</h2><div class="filters">${chip('all', `All · ${all.length}`)}${chip('private', 'Private')}${chip('public', 'Public')}</div>`;
  h += shown.length ? `<div class="list">${shown.map(gameCard).join('')}</div>` : `<div class="empty">${icon('games')}No games here yet. Play a game of Civ V and it shows up here.</div>`;
  const held = heldCards();
  if (held.length) h += `<h3 class="group-label deleted-heading">Recently deleted · ${held.length}</h3><p class="small">Hidden from you and the website. Each is erased for good 7 days after you deleted it; until then Undo brings it back as it was.</p><div class="list">${held.map(deletedCard).join('')}</div>`;
  return h;
}
