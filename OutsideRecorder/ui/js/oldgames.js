// Manual upload: old Civ autosaves found on this PC, ticked and uploaded whole as replays.
// Reads the shared window state `ui` (js/app.js); the files load in the order of index.html.
'use strict';
// (card OR03 "Bulk upload")
const OLD = { new: ['', 'Not uploaded'], partial: ['countdown', 'Partly uploaded'], uploaded: ['public', 'Already uploaded'], queued: ['locked', 'Waiting'],
  unsupported: ['locked', 'Waiting for support'], uploading: ['sending', 'Uploading'], processing: ['sending', 'Generating replay'], done: ['public', 'Replay ready'], stopped: ['queued', 'Stopped'], error: ['queued', 'Problem'] };
const pickable = (g) => !['uploading', 'queued', 'processing', 'done'].includes(g.status);
// How old games upload (the user, 2026-11-08: ask once on the first upload, remember it, a switch by the button changes it).
const OLD_UPLOAD = {
  delta: ['Smaller upload', 'This PC sends only what changed from one save to the next: about a tenth of the size, so it finishes sooner.'],
  full: ['Whole saves', 'Every save goes up complete and the website does all the work. A bigger upload.'] };
function oldUploadSwitch() {
  const chosen = ui.S.settings?.oldGamesUpload;
  if (chosen !== 'delta' && chosen !== 'full') return '';   // not answered yet: the first upload asks
  const chip = (key) => `<button class="chip" aria-pressed="${chosen === key}" data-cmd="setOldGamesUpload" data-args="${attr({ format: key })}" data-k="old-upload-${key}" title="${esc(OLD_UPLOAD[key][1])}">${OLD_UPLOAD[key][0]}</button>`;
  return `<div class="filters old-upload-switch" role="group" aria-label="Upload size"><span class="small">Upload size</span>${chip('delta')}${chip('full')}</div>`;
}
function oldGames() {
  const O = ui.S.oldGames ?? { games: [], folders: [] }, list = O.games ?? [];
  for (const id of [...ui.picked]) if (!list.some((g) => g.id === id && pickable(g))) ui.picked.delete(id);
  const can = list.filter(pickable), all = can.length > 0 && can.every((g) => ui.picked.has(g.id));
  let h = `<h2 class="screen-title">Manual upload</h2>`;
  h += `<p class="lead">Choose a folder, then select all the full games you want to keep. Generate and store replay explicitly keeps selected games, including games under 50 turns.</p>`;
  if (ui.S.testBuild) h += `<div class="banner">${icon('lock')}<div><h3>${ui.S.site ? 'Kept for the replays' : 'Nothing is public'}</h3><p>${ui.S.site ? `The games go to ${esc(ui.S.serverShort)}, where they become replays. Use Make private in Games to hide a game, or Restrict viewing to keep its page visible with a locked replay.` : 'Test build: the games go only to the test server on this PC.'}</p></div></div>`;
  h += `<div class="row old-bar">${btn(`${icon('check')} ${all ? 'Select none' : 'Select all'}`, 'ui.pickAll', {}, 'quiet', can.length ? '' : 'disabled')}${btn(`${icon('folder')} Add folder…`, 'addOldFolder', {}, 'quiet')}${btn(`${icon('refresh')} ${O.scanning ? 'Looking…' : 'Look again'}`, 'scanOldGames', {}, 'quiet', O.scanning ? 'disabled' : '')}<span style="flex:1"></span>${oldUploadSwitch()}${O.uploading ? btn('Stop', 'stopOldUpload', {}, '') : ''}${btn(`${icon('up')} Generate and store replay${ui.picked.size > 1 ? ` (${ui.picked.size} games)` : ''}`, 'uploadOldGames', { ids: [...ui.picked] }, 'primary', ui.picked.size ? '' : 'disabled')}</div>`;
  if (!list.length) return h + `<div class="empty">${icon('games')}${O.scanning || !O.scanned ? 'Looking through the save folders…' : 'No old games found. Civ keeps its autosaves in Documents\\My Games\\Sid Meier\'s Civilization 5. "Add folder…" looks in any other folder (zip files too).'}</div>`;
  h += `<div class="list">${list.map((g) => {
    const [c, t] = OLD[g.status] ?? ['', g.status], on = ui.picked.has(g.id), p = pickable(g);
    const turns = g.firstTurn != null ? `turns ${g.firstTurn}–${g.lastTurn}` : '';
    const facts = [g.mapSize && `${g.mapSize} map`, g.speed, g.map].filter(Boolean).join(' · ');
    return `<article class="card game-row old${on ? ' on' : ''}"><button class="tick" role="checkbox" aria-checked="${on}" data-pick="${esc(g.id)}" data-k="pick-${esc(g.id)}" ${p ? '' : 'disabled'} aria-label="Upload this game"></button><div class="gr-main"><div class="game-top"><span class="mod">${esc(g.mod)}</span><h3>${esc(range(g.from, g.to))}</h3><span class="badge ${c}">${t}</span></div>`
      + `<div class="small">${g.saves} ${g.saves === 1 ? 'save' : 'saves'}${turns ? ' · ' + turns : ''} · ${g.megabytes} MB${g.already && g.status === 'partial' ? ` · ${g.already} sent already` : ''}</div>${facts ? `<div class="small">${esc(facts)}</div>` : ''}<div class="small path">${esc(g.folder)}</div>`
      + `${['uploading','queued','processing','done','unsupported','stopped','error'].includes(g.status) ? uploadProgress({phase:g.phase ?? (g.status === 'processing' ? 'parsing' : g.status === 'done' ? 'ready' : g.status),uploaded:g.uploaded ?? (g.status === 'processing' ? g.saves : g.done),total:g.saves,parsed:g.parsed ?? (g.status === 'processing' ? g.done : 0),parseTotal:g.parseTotal ?? (g.status === 'processing' ? g.total : 0),note:g.note}) : g.note ? `<div class="small">${esc(g.note)}</div>` : ''}${g.status === 'done' ? (g.links ?? []).filter(link=>link.url).map(link => btn(`${icon('external')} Open game preview`, 'openSite', {url:link.url}, 'primary')).join('') : ''}</div></article>`;
  }).join('')}</div>`;
  h += `<p class="small" style="margin-top:10px">Games are grouped by their own identity; the server checks each save before making the replay.${O.folders?.length ? ` Looked in: ${O.folders.map(esc).join(' · ')}` : ''}</p>`;
  return h;
}
