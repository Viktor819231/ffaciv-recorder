// Made-up data for the preview (no Civ, no internet needed). The window says "Preview · made-up data" while this runs.
// Each scenario is the same state shape the real program sends (LiveBackend.BuildState, see bridge.js), so the screens are
// the real ones.
'use strict';
const Mock = (() => {
  const H = 3600e3, M = 60e3, D = 24 * H;
  const now = Date.now();
  const P = (name, civ, color, k, you) => ({ name, civ, color, k, you: !!you });
  const lobby = [
    P('You', 'Poland', '#e8463c', '#f4e9c9', true), P('Mansa_Moose', 'Babylon', '#2f61b8', '#e6d27a'),
    P('Kharn', 'Inca', '#e3c84a', '#2a6a39'), P('Ilyich', 'Korea', '#7d1f2e', '#d7d2c4'),
    P('Nordwind', 'Morocco', '#a3301f', '#5b9a4d'), P('tinyTurtle', 'Maya', '#2b8f8a', '#e0b54d'),
  ];
  const ctx = (c) => c.repeat(32);   // a recording's context (32 hex digits in the real program)
  const SERVER = 'https://ffaciv.com/replay/api/uploader/';
  const FOLDER = 'C:\\Users\\You\\AppData\\Local\\FFACiv\\Recorder';
  // A recording's saves as LiveBackend.SaveList sends them: turns first..last, a save every `step` ms from at0, the turns
  // up to `sent` on the site (an id), the rest waiting on this PC.
  const saves = (first, last, sent, at0, step) => Array.from({ length: last - first + 1 }, (_, i) => ({ file: `AutoSave_Post_${String(first + i).padStart(4, '0')} AD-${2000 + first + i}.Civ5Save`,
    turn: first + i, at: at0 + i * step, id: first + i <= sent ? `2026103110${String(first + i).padStart(4, '0')}-0000000${(first + i) % 10}` : null, beforeStream: false, refused: null }));
  // what the site lists of them (the recording's replay.files): converted up to a turn, the rest kept for the replay
  const files = (list, converted) => list.filter((x) => x.id).map((x) => ({ id: x.id, file: x.file, turn: x.turn, at: x.at, state: x.turn <= converted ? 'converted' : 'waiting', note: null }));
  // the upload plan of a recording (LiveBackend.UploadSchedule)
  const plan = (state, extra = {}) => ({ state, held: state === 'recording' || state === 'local', ended: state !== 'recording', warning: false, enabled: true, endPrompted: state !== 'recording', hidden: false, dueAt: null, ...extra });
  // the games list (LiveBackend.ListGames): one recording now, a public replay, a private one, one kept on this PC, a locked one
  const games = [
    { id: 'g5', context: ctx('5'), mod: 'Lekmod', modVersion: '35.4', date: now - 2 * H - 12 * M, turns: 34, firstTurn: 1, lastTurn: 34, savedTurns: 34, players: lobby, status: 'recording', folder: FOLDER + '\\session-g5',
      sentSaves: 34, lastSentTurn: 34, queuedSaves: 0, waitsForMenu: false, uploadSchedule: plan('recording'), replay: { state: 'recording', lastTurn: 34, saves: 34, processed: 0, keepShort: false } },
    { id: 'g4', context: ctx('4'), mod: 'Lekmod', modVersion: '35.4', date: now - 1 * D - 5 * H, turns: 186, firstTurn: 1, lastTurn: 186, savedTurns: 186, players: lobby.slice(0, 5), status: 'streamed', folder: FOLDER + '\\session-g4',
      sentSaves: 186, lastSentTurn: 186, queuedSaves: 0, uploadSchedule: plan('upload'), siteGame: 'u8c21a4f0', url: 'https://ffaciv.com/replay/game/lekmod-8c21', replay: { state: 'ready', saves: 186, processed: 186 } },
    { id: 'g3', context: ctx('3'), mod: 'Vanilla', modVersion: 'BNW', date: now - 3 * D, turns: 147, firstTurn: 1, lastTurn: 147, savedTurns: 147, players: lobby.slice(0, 6).map((p, i) => i === 3 ? { ...p, civ: 'Venice' } : p), status: 'streamed', folder: FOLDER + '\\session-g3',
      sentSaves: 147, lastSentTurn: 147, queuedSaves: 0, uploadSchedule: plan('upload'), private: true, siteGame: 'u3f9a77c1', url: 'https://ffaciv.com/replay/game/vanilla-3f9a', replay: { state: 'ready', saves: 147, processed: 147, private: true } },
    { id: 'g2', context: ctx('2'), mod: 'Lekmod', modVersion: '35.4', date: now - 6 * D, turns: 92, firstTurn: 1, lastTurn: 92, savedTurns: 92, players: lobby.slice(0, 4), status: 'local', folder: FOLDER + '\\session-g2',
      sentSaves: 0, queuedSaves: 92, uploadSchedule: plan('local'), replay: {} },
    { id: 'g1', context: ctx('1'), mod: 'Snowve', modVersion: '8.2', date: now - 9 * D, turns: 211, firstTurn: 1, lastTurn: 211, savedTurns: 211, players: lobby.slice(1, 6).concat([P('You', 'Arabia', '#4b8f39', '#f0e3ad', true)]), status: 'streamed', folder: FOLDER + '\\session-g1',
      sentSaves: 211, lastSentTurn: 211, queuedSaves: 0, uploadSchedule: plan('upload'), restrictedUntil: now + 12 * D, siteGame: 'u1b0d93e2', url: 'https://ffaciv.com/replay/game/snowve-1b0d', replay: { state: 'ready', saves: 211, processed: 211 } },
  ];
  const okChecks = [
    { id: 'autosave', level: 'ok', title: 'Civ saves every turn', text: 'Autosave is set to every turn, so the replay has every turn.' },
    { id: 'version', level: 'ok', title: 'Lekmod 35.4 is supported', text: 'This game version can be recorded.' },
    { id: 'stream', level: 'ok', title: 'Connected to ffaciv.com', text: 'Queued saves upload after Civ has stayed closed for 30 seconds and their upload countdown permits it. Local save copies are retained.' },
    { id: 'disk', level: 'ok', title: 'Enough disk space', text: '48 GB free. A long game needs about 200 MB.' },
  ];
  // old Civ autosaves found on this PC (OldGamesService.State)
  const O = (id, mod, from, mins, count, t0, t1, status, extra = {}) => ({ id, mod, from, to: from + mins * M, saves: count, firstTurn: t0, lastTurn: t1, mapSize: 'Small', speed: 'Quick', map: 'LekmapPangaeaFractalv6.3',
    folder: 'C:\\Users\\You\\Documents\\My Games\\Sid Meier\'s Civilization 5\\ModdedSaves\\multi\\auto', megabytes: +(count * 1.1).toFixed(1), already: 0, status, done: 0, total: count, ...extra });
  const oldFolders = ['…\\Saves\\multi\\auto', '…\\ModdedSaves\\multi\\auto'];
  // the multiplayer staging room (LiveBackend: current with lobby: true)
  const L = (name, civ, you, human, team) => ({ name, civ, color: you ? '#f0c860' : human ? '#5ec8bc' : '#7d8b88', you: !!you, human: !!human, team });
  const lobbyRoom = () => ({ id: 'lobby', lobby: true, mod: 'Lekmod', modVersion: '35.4', mode: 'multiplayer lobby', gameName: 'LEKMOD v35.4.003 FFA',
    players: [L('vik', 'Armenia', true, true, 0), L('raffyG', 'Random', false, true, 1), L('Mansa_Moose', 'Babylon', false, true, 2), L('Kharn', 'Random', false, true, 3), L('Open', '–', false, false, 4), L('AI', 'Inca', false, false, 5)],
    map: 'LekmapPangaeaFractalv6.3', worldSize: 2, mapSize: 'Small', turnTimerSeconds: 90, turnTimerOn: true, options: 7,
    upload: { state: 'sent', target: 'test' }, publish: { state: 'local' }, liveLink: { disabled: true, url: null, why: 'Get a private livestream link from the active recorder.' } });

  const base = () => ({
    live: true, production: true, testBuild: false, site: true, serverShort: 'ffaciv.com', serverName: 'ffaciv.com',
    version: '0.3.0', firstRun: false, reportBusy: false, safeCapture: false, civDetected: true, oldHintDismissed: false,
    status: { kind: 'recording', title: 'Recording Lekmod game', detail: 'Turn 34 · 6 players · multiplayer' },
    current: {
      id: 'live', context: ctx('5'), mod: 'Lekmod', modVersion: '35.4', mode: 'multiplayer', turn: 34, players: lobby,
      startedAt: now - 2 * H - 12 * M, lastSaveAt: now - 2 * M, paused: false, ended: false, folder: FOLDER + '\\session-g5',
      upload: { state: 'sent', target: 'test', server: SERVER, queued: 0, queuedBytes: 0, lastSentTurn: 34, sentSaves: 34, lastSentAt: now - 2 * M,
        wholeSaves: 2, deltaSaves: 32, eventBytes: 412 * 1024, saveBytes: 9.6 * 1048576, error: null },
      publish: { state: 'local' },
      liveLink: { disabled: true, url: null, why: 'Get a private livestream link from the active recorder.' },
      replay: { state: 'recording', lastTurn: 34, saves: 34, processed: 0, keepShort: false, completionQueued: false, keepShortRequested: false, publicRequested: false },
      firstTurn: 1, lastTurn: 34, savedTurns: 34, saves: saves(1, 34, 34, now - 2 * H, 3.5 * M), savesWait: null,
    },
    checks: structuredClone(okChecks),
    games: structuredClone(games),
    deleted: [],
    updates: {
      current: '0.3.0', checking: false, checkedAt: now - 40 * M, error: null, waited: false,
      available: { version: '0.3.1', required: false, url: 'https://ffaciv.com/replay/recorder/', notes: ['Snowve 8.2 games are recorded', 'Fewer pop-ups at the start of a game'], later: null },
      changelog: [],
    },
    resources: { reason: 'Ready for background work', paused: false, cpuPercent: 0.3, systemCpuPercent: 24, memoryBytes: 68157440, peakMemoryBytes: 83886080,
      uploadKibPerSecond: 256, idleUploadMode: false, cpuBudgetPercent: 5, cpuLimitPercent: 0, cpuLimitError: null },
    crashDiagnostics: { busy: false, checkedAt: null, status: null, crashes: [] },
    memoryLogging: { enabled: true, canEnable: true, reason: null, readsPerSecond: 4, cityManagementIntervalMs: 1000, turnQuietMs: 5000 },
    livestream: {
      active: false, starting: false, canStart: true, reason: null, problem: null, url: null, scope: 'player', expiresAt: null, status: 'unknown', private: true,
      needsSetup: false, waitingForGame: false, currentSaveReason: null, followSetupReason: null, setupReason: null,
      technical: { server: SERVER, transportState: 'connected', lastError: null, slot: 'unknown', controlAllowed: true, payloadAllowed: false, waitReason: null,
        queuedSaves: 0, queuedEventBytes: 0, fullSaves: 2, deltaSaves: 32, saveBytes: 9.6 * 1048576, eventBytes: 412 * 1024, requests: 81, chunks: 37,
        deltaCpuMs: 5400, chunkCpuMs: 610, lastSentAt: now - 2 * M, saveBatchLimit: 2, eventChunkLimit: 4, idleUploadMode: false, uploadKibPerSecond: 256,
        eventChunkBytes: 262144, saveUpload: 'delta' },
    },
    settings: {
      recording: true, trackLogs: true, captureMoves: true, experimentalMemoryOptIn: true, saveUpload: 'delta', uploadTiming: 'during', stream: true,
      recordSinglePlayer: false, looksPerSecond: 4, diagnostics: false, backupUnsupported: true,
      notify: { lobby: true, reminders: true, problems: true, updates: true },
      startWithWindows: true, dataFolder: FOLDER,
    },
    oldGames: { scanning: false, scanned: true, scannedAt: now - 10 * M, uploading: false, folders: oldFolders, newCount: 1, games: [
      O('o1', 'Lekmod 35.4', now - 14 * D, 150, 96, 0, 95, 'new'),
    ] },
    test: { channelUrl: null, streamUrl: SERVER, liveSiteUrl: 'https://ffaciv.com/replay/', settingsFile: 'C:\\Users\\You\\AppData\\Local\\Programs\\FFACiv Recorder\\settings.txt', streamState: 'connected' },
  });

  // No game running: the current recording is gone and the livestream cannot start.
  const noGame = (s, detected = false) => {
    s.current = null; s.civDetected = detected;
    s.livestream = { ...s.livestream, canStart: false, reason: 'Join a supported lobby or start a game first.' };
  };
  const WAITING = { kind: 'waiting', title: 'Waiting for Civ', detail: 'Start a game and it is recorded by itself.' };

  // Each scenario: a change on top of the base. Names are what the preview strip lists.
  const scenarios = {
    resourcepaused: {label:'Resources paused · example data',apply:s=>{scenarios.safeCapture.apply(s);s.resources={...s.resources,paused:true,reason:'Waiting for computer load to ease',cpuPercent:0.4,systemCpuPercent:91.2};}},
    onboarding: {label:'First launch · setup choices',apply:s=>{scenarios.safeCapture.apply(s);s.firstRun=true;s.games=[];s.oldGames=null;s.status={kind:'waiting',title:'Finish setup to start',detail:'Choose your recording and upload preferences.'};}},
    scheduled: {label:'Finished game · hidden 2 hours',apply:s=>{scenarios.safeCapture.apply(s);s.games[0].uploadSchedule=plan('waiting',{dueAt:Date.now()+2*H-5*60e3,hidden:true});s.games[0].status='local';s.games[0].replay={};}},
    finishwaits: {label:'Livestreamed game · finish waits for the main menu',apply:s=>{scenarios.replaycontrols.apply(s);Object.assign(s.games[0],{turns:46,firstTurn:1,lastTurn:46,status:'streamed',ended:true,sentSaves:47,queuedSaves:0,waitsForMenu:true,
      uploadWaitReason:'Civ is still open. The replay is made by itself once Civ is closed.',uploadSchedule:plan('upload'),
      replay:{state:'recording',lastTurn:46,saves:47,processed:0,finishAt:Date.now()+55*60e3,keepShort:false,private:true,completionQueued:true,publicRequested:true}});}},
    updatewait: {label:'Updates · automatic check waits for Civ',apply:s=>{scenarios.safeCapture.apply(s);s.updates={...s.updates,available:null,checking:false,checkedAt:Date.now()-5*H,waited:true};}},
    streamready: {label:'Experimental stream · ready',apply:s=>{scenarios.recording.apply(s);s.livestream={...s.livestream,active:false,canStart:true};}},
    streaming: {label:'Private livestream · active',apply:s=>{scenarios.streamready.apply(s);s.livestream={...s.livestream,active:true,canStart:false,status:'granted',expiresAt:Date.now()+3*H,url:'https://ffaciv.com/replay/live/private-example'};s.livestream.technical={...s.livestream.technical,slot:'granted',payloadAllowed:true};}},
    unsupportedbackup: { label: 'Unsupported build backup · example data', apply: s => {
      scenarios.replaycontrols.apply(s);
      s.status = {kind:'attention',title:'Unsupported build — keeping autosaves',detail:'12 autosaves kept on this PC, with a diagnostic log. Upload them after support is fixed. Realtime movement logs are unavailable.',action:{cmd:'openFolder',label:'Open backup folder',args:{path:'example'}}};
      s.checks.unshift({id:'unsupported',level:'warn',title:'Autosave backup only',text:'The game DLL is not recognized. Original save bytes and diagnostics are kept locally; no movement data is guessed.'});
    } },
    safeCapture: {label:'Save-file recorder · example data',apply:s=>{scenarios.replaycontrols.apply(s);s.safeCapture=true;s.version='0.7.6';s.checks=s.checks.filter(c=>c.id!=='version').map(c=>c.title.startsWith('Connected')?{...c,level:'info',title:'Uploads wait until after the game',text:'Automatic uploads start once Civ is closed. The replay stays hidden for 2 hours.'}:c);Object.assign(s.settings,{trackLogs:false,captureMoves:false,experimentalMemoryOptIn:false,looksPerSecond:4,uploadTiming:'after'});s.memoryLogging={enabled:false,canEnable:true,reason:'Needs a supported Civ build. Resource controls still apply.',readsPerSecond:4,cityManagementIntervalMs:1000,turnQuietMs:5000};s.livestream={...s.livestream,needsSetup:true,reason:'Enable experimental memory logs first.'};s.status={kind:'waiting',title:'Ready for your next game',detail:'Start Civ V. Save copies stay on this PC while you play.'};}},
    releaseupdate: { label: 'Update notice · example data', apply: s => {
      scenarios.replaycontrols.apply(s);
      s.updates={current:'0.7.6',checking:false,checkedAt:Date.now(),error:null,waited:false,available:{version:'0.7.6',required:false,url:'https://ffaciv.com/replay/recorder/',notes:['Example: support for a new mod build','Example: recording fixes'],later:null},changelog:[]};
    } },
    replaycontrols: { label: 'Replay controls · example data', apply: s => {
      scenarios.testbuild.apply(s); s.production = true; s.testBuild = false; s.site = true; s.serverShort = 'ffaciv.com'; s.serverName = 'ffaciv.com'; s.firstRun = false; s.version = '0.7.6';
      noGame(s);
      s.checks = s.checks.map(c => c.title.startsWith('Connected to') ? {...c,title:'Connected to ffaciv.com'} : c);
      s.updates.available = null;
      s.status = {kind:'waiting',title:'Waiting for Civ V',detail:'Start a game and it is recorded by itself.'};
      s.games = [
        {id:'short',context:'a'.repeat(32),mod:'Vanilla',modVersion:'BNW',date:now,turns:42,players:lobby,status:'streamed',folder:'example',
          replay:{state:'recording',lastTurn:42,saves:42,processed:0,finishAt:now+44*M,keepShort:false}},
        {id:'ready',context:'b'.repeat(32),mod:'Lekmod',modVersion:'35.4',date:now-2*H,turns:186,players:lobby,status:'public',
          siteGame:'u12345678',url:'https://ffaciv.com/replay/',restrictedUntil:now+14*24*H,
          replay:{state:'ready',saves:186,processed:186}},
        {id:'processing',context:'c'.repeat(32),mod:'Snowve',modVersion:'8.3.0',date:now-24*H,turns:72,players:lobby,status:'sending',
          replay:{state:'processing',saves:72,processed:38,keepShort:false,private:true}},
      ];
      if(s.oldGames?.games?.length) {
        s.oldGames.uploading = false;
        s.settings.notify.updates = true;
        s.updates = {current:'0.6.0',checking:false,checkedAt:Date.now(),error:null,waited:false,available:null,changelog:[]};
        s.oldGames.games[0] = {...s.oldGames.games[0],saves:72,firstTurn:0,lastTurn:71,status:'processing',done:38,total:72,note:'Generating replay… 38 of 72 saves processed.'};
        if(s.oldGames.games[1]) s.oldGames.games[1] = {...s.oldGames.games[1],status:'done',done:31,total:31,note:'Replay generated and stored.',links:[{url:'https://ffaciv.com/replay/'}]};
      }
    } },
    // the Saves list, its waiting reason and a refused save; a deletion held 7 days with Undo (2026-10-31)
    savesfeedback: { label: 'Saves list · waiting, refused, deleted', apply: s => {
      scenarios.replaycontrols.apply(s);
      const cur = saves(30, 47, 43, now-40*M, 2*M);
      cur.find(x=>x.turn===44).refused = {at:now-6*M,status:413,why:'a save is at most 25 MB'};
      s.current = {id:'live',context:'d'.repeat(32),mod:'Lekmod',modVersion:'35.4',mode:'multiplayer',turn:47,players:lobby,startedAt:now-2*H,lastSaveAt:now-1*M,paused:false,ended:false,
        upload:{state:'sending',lastSentTurn:43,queued:4},publish:{state:'local'},firstTurn:30,lastTurn:47,savedTurns:18,saves:cur,
        savesWait:'Saves are sent once Civ is closed. The replay then stays hidden for 2 hours. To send while you play, close Civ and choose While playing.',
        replay:{state:'recording',lastTurn:43,saves:14,processed:10,files:files(cur,40)}};
      const g0 = s.games[0];
      Object.assign(g0, {firstTurn:1,lastTurn:42,savedTurns:40,saves:saves(1,42,42,now-3*H,4*M)});
      g0.saves.splice(10,2);
      g0.replay = {...g0.replay, files: files(g0.saves, 42)};
      Object.assign(s.games[1], {firstTurn:30,lastTurn:186,savedTurns:157});
      Object.assign(s.games[2], {firstTurn:1,lastTurn:72,savedTurns:72});
      s.deleted = [{key:'recording:'+'e'.repeat(32),deletedAt:now-26*H,due:now+6*24*H-2*H,card:{id:'gone',context:'e'.repeat(32),mod:'Snowve',modVersion:'8.3.0',date:now-30*H,firstTurn:12,lastTurn:96,savedTurns:85,players:lobby}}];
    } },
    recording: { label: 'Recording a game', apply: () => {} },
    testbuildlobby: { label: 'Test build · multiplayer lobby', apply: (s) => {
      scenarios.testbuild.apply(s);
      s.status = { kind: 'found', title: 'In the multiplayer lobby', detail: '4 players · LekmapPangaeaFractalv6.3 · Small · turn timer 90 s. Recorded when the game starts.' };
      s.current = lobbyRoom();
      s.livestream = { ...s.livestream, canStart: false, reason: 'Enable experimental memory logs first.' };
      s.games.shift();
    } },
    testbuildsp: { label: 'Test build · single player not recorded', apply: (s) => {
      scenarios.testbuild.apply(s);
      s.status = { kind: 'paused', title: 'Single-player game — not recorded', detail: 'Single-player games are off in Settings (for testing, coming later). Multiplayer and hot seat games are recorded.', action: { cmd: 'recordAnyway', label: 'Record it anyway' } };
      noGame(s, true); s.games.shift();
    } },
    // the test build (OR03): the state LiveBackend sends while a game is recorded and streamed to the local test server
    testbuild: { label: 'Test build · recording + stream', apply: (s) => {
      const T = (civ, color, you, human) => ({ name: human ? 'Human' : 'AI', civ, color, you: !!you, human: !!human, alive: true });
      const civs = [T('Argentina', '#f0c860', true, true), T('Morocco', '#7d8b88'), T('Cuba Batista', '#7d8b88'), T('Burma', '#7d8b88'), T('Zimbabwe', '#7d8b88'), T('India', '#7d8b88')];
      const local = 'http://127.0.0.1:5631/replay/api/uploader/';
      Object.assign(s, { testBuild: true, production: false, site: false, serverShort: 'the test server', serverName: 'The test server', version: '0.3.1' });
      s.updates.available = null;
      s.test = { channelUrl: null, streamUrl: local, liveSiteUrl: 'http://127.0.0.1:5632/replay/', settingsFile: 'C:\\Users\\You\\AppData\\Local\\Programs\\FFACiv Recorder\\settings.txt', streamState: 'connected' };
      s.livestream.technical = { ...s.livestream.technical, server: local };
      s.settings.looksPerSecond = 20;
      s.status = { kind: 'recording', title: 'Recording Snowve game', detail: 'Turn 12 · single player' };
      s.current = { id: 'live', context: ctx('f'), mod: 'Snowve', modVersion: '8.3.0', mode: 'single player', turn: 12, players: civs, cityStates: 16,
        startedAt: now - 12 * M, lastSaveAt: now - 40e3, paused: false, ended: false, folder: FOLDER + '\\session-s3', publish: { state: 'local' },
        upload: { state: 'sent', target: 'test', server: local, queued: 0, queuedBytes: 0, lastSentTurn: 11, sentSaves: 24,
          lastSentAt: now - 3e3, wholeSaves: 3, deltaSaves: 21, eventBytes: 96 * 1024, saveBytes: 2.4 * 1048576, error: null },
        liveLink: { disabled: true, url: null, why: 'Get a private livestream link from the active recorder.' },
        replay: { state: 'recording', lastTurn: 11, saves: 11, processed: 0, keepShort: false, completionQueued: false, keepShortRequested: false, publicRequested: false },
        firstTurn: 1, lastTurn: 12, savedTurns: 12, saves: saves(1, 12, 11, now - 12 * M, 60e3), savesWait: 'Finishing the copy of the newest save.' };
      s.checks = [okChecks[0], { id: 'version', level: 'ok', title: 'Snowve 8.3.0 is supported', text: 'This game version can be recorded.' }, okChecks[3],
        { id: 'stream', level: 'ok', title: 'Connected to the test server', text: 'Moves go out every 3 s, saves when Civ writes them.' }];
      s.games = [{ id: 's3', context: ctx('f'), mod: 'Snowve', modVersion: '8.3.0', date: now - 12 * M, turns: 12, players: civs, status: 'recording', folder: 'example', sentSaves: 11, lastSentTurn: 11, queuedSaves: 1,
          canBlock: false, blockWhy: 'This game is still being played. Block it after the game.', uploadSchedule: plan('recording') },
        { id: 's2', context: ctx('e'), mod: 'Lekmod', modVersion: '35.4', date: now - 3 * H, turns: 14, players: civs.slice(0, 5), status: 'streamed', folder: 'example', sentSaves: 14, lastSentTurn: 13, queuedSaves: 0,
          canBlock: true, blocked: true, blockConfirmed: true, uploadSchedule: plan('upload'), replay: { state: 'recording', lastTurn: 13, saves: 14, processed: 0 } },
        { id: 's1', context: ctx('d'), mod: 'Vanilla', modVersion: 'BNW', date: now - 9 * H, turns: 146, players: civs.slice(0, 4), status: 'streamed', folder: 'example', sentSaves: 147, lastSentTurn: 146, queuedSaves: 0,
          canBlock: true, blocked: false, uploadSchedule: plan('upload'), replay: { state: 'ready', saves: 147, processed: 147 }, siteGame: 'u5d2c0a11', url: 'http://127.0.0.1:5632/replay/' }];
      s.settings.recordSinglePlayer = false;
      s.oldGames = { scanning: false, scanned: true, scannedAt: now - 10 * M, uploading: true, folders: oldFolders, newCount: 3, games: [
        O('a1', 'Lekmod 35.4', now - 1 * D, 190, 120, 0, 118, 'uploading', { done: 46 }),
        O('a2', 'Civ V (no mods)', now - 4 * D, 75, 31, 0, 30, 'new', { map: 'Pangaea', mapSize: 'Standard', speed: 'Standard', folder: 'C:\\Users\\You\\Documents\\My Games\\Sid Meier\'s Civilization 5\\Saves\\single\\auto' }),
        O('a3', 'Lekmod 35.4', now - 6 * D, 240, 155, 0, 152, 'partial', { already: 20 }),
        O('a4', 'Snowve 8.3', now - 12 * D, 30, 16, 0, 15, 'uploaded', { already: 16 }),
      ] };
    } },
    waiting: { label: 'Waiting for Civ V', apply: (s) => {
      s.status = { ...WAITING }; noGame(s); s.games.shift(); s.updates.available = null;
    } },
    lobby: { label: 'In the game lobby', apply: (s) => {
      s.status = { kind: 'found', title: 'In the multiplayer lobby', detail: '4 players · LekmapPangaeaFractalv6.3 · Small · turn timer 90 s. Recorded when the game starts.' };
      s.current = lobbyRoom(); s.updates.available = null;
      s.games.shift();
    } },
    unsupported: { label: 'Version not supported', apply: (s) => {
      s.status = { kind: 'unsupported', title: 'Civ V found — this version is not supported yet', detail: 'Lekmod 35.5 is new. We are adding it; you do not need to do anything.', note: 'Your autosaves are still kept, so this game can be replayed once Lekmod 35.5 is supported.' };
      noGame(s, true); s.games.shift();
      s.checks[1] = { id: 'version', level: 'warn', title: 'Lekmod 35.5 is not supported yet', text: 'Moves between turns are not recorded for this version. The autosaves are kept and sent.' };
    } },
    // the game is over: uploaded at once, hidden on the site, public 2 hours after the game
    countdown: { label: 'Game over · public in 2 hours', apply: (s) => {
      s.status = { ...WAITING }; noGame(s); s.updates.available = null;
      Object.assign(s.games[0], { status: 'streamed', ended: true, lastSentTurn: 34, uploadSchedule: plan('waiting', { dueAt: now + 2 * H - 5 * M, hidden: true }),
        replay: { state: 'recording', lastTurn: 34, saves: 34, processed: 0, keepShort: false, private: true } });
    } },
    // the game is over and its saves are going up
    sending: { label: 'Uploading a finished game', apply: (s) => {
      s.status = { ...WAITING }; noGame(s);
      Object.assign(s.games[0], { status: 'sending', ended: true, sentSaves: 27, lastSentTurn: 27, queuedSaves: 7, uploadSchedule: plan('upload'),
        replay: { state: 'recording', lastTurn: 27, saves: 27, processed: 0, keepShort: false } });
    } },
    attention: { label: 'Needs attention', apply: (s) => {
      s.status = { kind: 'recording', title: 'Recording Lekmod game', detail: 'Turn 34 · 6 players · multiplayer' };
      s.current.upload = { ...s.current.upload, state: 'queued', queued: 3, lastSentTurn: 31, sentSaves: 31, error: 'No connection could be made.' };
      s.current.saves = saves(1, 34, 31, now - 2 * H - 1 * M, 3.5 * M);
      s.current.savesWait = 'Can\'t reach ffaciv.com. Saves are sent when the connection is back; nothing is lost.';
      s.checks[0] = { id: 'autosave', level: 'warn', title: 'Civ does not save every turn', text: 'Civ saves only every 5 turns, so the replay would miss turns. Civ must be closed to change this.',
        action: { cmd: 'fixAutosave', label: 'Fix it for me', disabled: true, why: 'Close Civ first, then press this.' } };
      s.checks[2] = { id: 'stream', level: 'warn', title: 'Can\'t reach ffaciv.com', text: `Nothing answers at ${SERVER}. 3 saves wait on this PC and are sent when the connection is back; nothing is lost.` };
      s.checks.push({ id: 'ingame', level: 'info', title: 'The in-game recorder is also installed', text: 'Found the FFACiv Recorder mod in Civ\'s DLC folder. Both can run; you only need one. To use only this program, move the folder FFACiv_Recorder out of Assets\\DLC while Civ is closed.',
        action: { cmd: 'openFolder', label: 'Show the folder', args: { path: 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\Sid Meier\'s Civilization V\\Assets\\DLC' } } });
      s.checks.push({ id: 'disk', level: 'warn', title: 'Disk space is low', text: 'Only 1.1 GB free on C:. Recording goes on; free some space soon.' });
      s.checks.splice(3, 1);
    } },
    fixable: { label: 'Autosave fix (Civ closed)', apply: (s) => {
      s.status = { ...WAITING }; noGame(s); s.games.shift(); s.updates.available = null;
      s.checks[0] = { id: 'autosave', level: 'warn', title: 'Civ does not save every turn', text: 'Civ saves only every 5 turns and keeps 10 saves. The replay needs a save every turn.',
        action: { cmd: 'fixAutosave', label: 'Fix it for me' } };
    } },
    required: { label: 'Required update', apply: (s) => {
      s.status = { kind: 'attention', title: 'Please update to keep recording', detail: 'This version can no longer send games.' };
      noGame(s); s.games.shift();
      s.updates.available = { version: '0.4.0', required: true, url: 'https://ffaciv.com/replay/recorder/', notes: ['ffaciv.com changed how games are sent', 'Your saved games are kept and sent after the update'], later: null };
    } },
    firstrun: { label: 'First start', apply: (s) => {
      s.firstRun = true; noGame(s); s.games = []; s.updates.available = null;
      s.status = { ...WAITING };
      s.checks[0] = { id: 'autosave', level: 'warn', title: 'Civ does not save every turn', text: 'Civ saves only every 5 turns.', action: { cmd: 'fixAutosave', label: 'Fix it for me' } };
    } },
  };

  // Pop-ups the preview can show (the toast window and the pictures use these); the same ones the real program sends.
  const toasts = {
    liveReady: { kind:'good', title:'Livestream ready', text:'Share this link so others can watch.\nhttps://ffaciv-test.pages.dev/replay/live/12345678-12345678/watch', actions:[{label:'Open livestream',cmd:'openSite',args:{url:'https://ffaciv-test.pages.dev/replay/live/12345678-12345678/watch'}},{label:'Copy link',cmd:'copyLiveLink',primary:true}] },
    lobby: { kind: 'good', title: 'Game detected · recording', text: 'Civ V · save copies stay on this PC while you play.', actions: [{ label: 'Stop recording', cmd: 'pauseGame' }, {label:'Options',cmd:'showOptions'}] },
    started: { kind: 'good', title: 'Game detected · recording', text: 'Lekmod game · turn 1 · 6 players', actions:[{label:'Stop recording',cmd:'pauseGame'},{label:'Options',cmd:'showOptions'}] },
    ended: {kind:'info',title:'Finished playing?',text:'It uploads now, hidden, and goes public on ffaciv.com at 21:40. Still playing? Keep it on this PC.',actions:[{label:'Make public now',cmd:'uploadGameNow',args:{game:'g5'},primary:true},{label:'Still playing',cmd:'continueGame',args:{game:'g5'}},{label:'Options',cmd:'showOptions'}]},
    reminder: {kind:'warn',title:'Public in 5 minutes',text:'Civ V game · turn 186 goes public on ffaciv.com. Keep it hidden longer, or delete it.',actions:[{label:'Hide 2 more hours',cmd:'postponeUpload',args:{game:'g5',hours:2},primary:true},{label:'Delete…',cmd:'reviewDelete',args:{game:'g5'}}]},
    saved: { kind: 'good', title: 'Turn 34 saved', text: 'Sent to ffaciv.com. Private until the game is over.' },
    problem: { kind: 'warn', title: 'Not recording this game', text: 'Lekmod 35.5 is not supported yet. Your autosaves are kept.' },
    offline: { kind: 'warn', title: 'No internet', text: '3 turns waiting. They are sent when you are back online.' },
    update: { kind: 'info', title: 'Update ready', text: 'FFACiv Recorder 0.3.1 · Snowve 8.2 games are recorded', actions: [{ label: 'Later', cmd: 'updateLater', args: { when: 'next' } }, { label: 'Update now', cmd: 'updateNow', primary: true }] },
  };

  let state = null, scenario = 'recording';
  function load(name) {
    scenario = scenarios[name] ? name : 'recording';
    state = base(); scenarios[scenario].apply(state);
    return state;
  }
  const find = (id) => state.games.find((g) => g.id === id);
  const preview = (text) => ({ toast: { kind: 'info', title: 'Preview', text } });

  // The same commands the real program takes (LiveBackend.Handle / HandleUx). Returns { toast?, dialog? } for the window.
  function handle(cmd, a = {}) {
    const s = state, cur = s.current;
    switch (cmd) {
      case 'simpleSetup': Object.assign(s.settings,{recording:true,experimentalMemoryOptIn:false,trackLogs:false,captureMoves:false,saveUpload:'full',uploadTiming:'after',stream:true});s.memoryLogging={...s.memoryLogging,enabled:false};return {toast:{kind:'good',title:'Simple setup chosen',text:'The recorder copies Civ’s autosaves and uploads them after the game. Nothing reads Civ’s memory.'}};
      case 'setUploadFormat': if(!['delta','full'].includes(a.format))return {};s.settings.saveUpload=a.format;return {toast:{kind:'good',title:'Upload method saved',text:a.format==='delta'?'Save changes will be prepared locally. The site still generates the replay.':'Complete save files will be sent for the site to process.'}};
      case 'setMemoryLogging': {if(a.enabled && s.settings.recording===false)return {toast:{kind:'warn',title:'Automatic recording is off',text:'Turn it on before adding memory logs.'}};s.settings.trackLogs=!!a.enabled;s.memoryLogging={enabled:!!a.enabled,canEnable:s.settings.recording!==false,reason:'Needs a supported Civ build. Resource controls still apply.',readsPerSecond:4,cityManagementIntervalMs:1000,turnQuietMs:5000};return {toast:{kind:'good',title:a.enabled?'Memory logs enabled':'Memory logs off',text:a.enabled?'Read-only observations are enabled for supported games. Upload and livestream preferences are unchanged.':'Your save files are still recorded.'}};}
      case 'setCaptureMethod': {
        s.settings.recording=a.method!=='manual'; if(a.method==='memory')s.settings.trackLogs=true;s.memoryLogging={enabled:s.settings.recording && !!s.settings.trackLogs,canEnable:s.settings.recording,reason:s.settings.recording?'Needs a supported Civ build. Resource controls still apply.':'Turn automatic recording on first.',readsPerSecond:4,cityManagementIntervalMs:1000,turnQuietMs:5000};s.settings.uploadTiming='after';
        return {toast:{kind:'good',title:'Recording method saved',text:a.method==='manual'?'Keep your own saves and choose Manual upload later.':a.method==='memory'?'Experimental observations are enabled. Livestreaming remains off.':'Automatic save copies are enabled. Your memory logging choice is kept.'}};
      }
      case 'startLivestream': s.livestream={...s.livestream,active:true,canStart:false,scope:a.scope??'player',expiresAt:Date.now()+(a.durationHours??4)*H,url:'https://ffaciv.com/replay/live/share/private-example'};return {toast:{kind:'good',title:'Private livestream started',text:'Copy your invite link to share it.'}};
      case 'stopLivestream': s.livestream={...s.livestream,active:false,canStart:true,url:null,expiresAt:null};return {toast:{kind:'info',title:'Livestream stopped',text:'The private link no longer shows this game live.'}};
      case 'uploadGameNow': case 'confirmGameFinished': {const g=find(a.game);if(g){g.uploadSchedule=plan('upload');g.status='sending';}return {toast:{kind:'good',title:'Upload queued',text:'Your game will upload when Civ is closed and the server is available.'}};}
      case 'postponeUpload': {const g=find(a.game);if(g)g.uploadSchedule=a.hours?plan('waiting',{dueAt:Date.now()+a.hours*H,hidden:g.uploadSchedule?.hidden??false}):plan('local');return {toast:{kind:'info',title:'Upload postponed',text:a.hours?'The new countdown is shown on your game.':'Your game stays on this PC until you upload it.'}};}
      case 'continueGame': {const g=find(a.game);if(g)g.uploadSchedule=plan('local');return {toast:{kind:'info',title:'Game kept on this PC',text:'Automatic upload is paused for this game. Choose Upload now when you finish.'}};}
      case 'dontRecord':
        s.status = { kind: 'paused', title: 'Not recording this game', detail: 'You chose not to record it. The next game is recorded again.' };
        if (cur) cur.paused = true;
        return { toast: { kind: 'info', title: 'This game is not recorded', text: 'Nothing from it is sent. The next game is recorded again.' } };
      case 'recordAnyway':
        s.status = { kind: 'recording', title: 'Recording your game', detail: 'Autosaves are being kept on this PC. Keep playing.' };
        return { toast: { kind: 'good', title: 'Recording this single-player game', text: 'Only this one. Single-player games stay off in Settings.' } };
      case 'deleteGame': { const g = find(a.game) ?? (s.current?.id === a.game ? s.current : null); s.games = s.games.filter(x => x.id !== a.game); if(s.current?.id === a.game)s.current=null;
        const key = 'recording:' + (g?.context ?? a.game); if (g) (s.deleted ??= []).unshift({key, deletedAt: Date.now(), due: Date.now() + 7*24*H, card: {...g, saves: undefined}});
        return {toast:{kind:'info',title:'Recording deleted · Undo for 7 days',text:'Hidden from your list and from the website now. It is erased for good in 7 days; until then Undo brings it back.',actions:[{label:'Undo',cmd:'undoDelete',args:{key},primary:true}]}}; }
      case 'reviewDelete': return { dialog: { kind: 'deleteGame', game: a.game } };   // the window frame answers this one in the real program
      case 'undoDelete': { const d = (s.deleted ?? []).find(x => x.key === a.key); if (!d) return {toast:{kind:'warn',title:'Too late to undo',text:'The hold had ended: the recording was deleted.'}};
        s.deleted = s.deleted.filter(x => x !== d); s.games.unshift(d.card); s.games.sort((x, y) => (y.date ?? 0) - (x.date ?? 0));
        return {toast:{kind:'good',title:'Recording brought back',text:'It is in your list again, with the visibility it had before.'}}; }
      case 'showSaves': return {};
      case 'checkUpdates': s.updates.checkedAt=Date.now(); s.updates.waited=false; return {};
      case 'keepShortGame': {const g=find(a.game);if(g) g.replay={...g.replay,keepShort:true};return {};}
      case 'completeGame': {const g=find(a.game);if(g) g.replay={...g.replay,state:'processing',processed:0};return {};}
      case 'replayChoice': {const g=find(a.game);if(g) {if('private' in a) g.private=a.private;if('restricted' in a) g.restrictedUntil=a.restricted?Date.now()+14*24*H:null;}return {toast:{kind:'good',title:'Replay access updated',text:'Your game card now shows its visibility.'}};}
      case 'pauseGame':
        if (!cur) return {};
        cur.paused = !cur.paused;
        s.status = cur.paused ? { kind: 'paused', title: 'Recording paused', detail: 'For this game only. Nothing is sent while paused.' } : { kind: 'recording', title: 'Recording Lekmod game', detail: `Turn ${cur.turn} · ${cur.players.length} players · ${cur.mode}` };
        return {};
      case 'setSetting': {
        const path = a.key.split('.'); let o = s.settings; while (path.length > 1) o = o[path.shift()]; o[path[0]] = a.value;
        if(a.key==='recording' && !a.value)s.livestream={...s.livestream,active:false,canStart:false,url:null};
        if (a.key === 'recording' && cur) s.status = a.value ? { kind: 'recording', title: 'Recording Lekmod game', detail: `Turn ${cur.turn} · 6 players · multiplayer` } : { kind: 'paused', title: 'Recording is off', detail: 'Turn it on in Settings.' };
        return {};
      }
      case 'fixAutosave': {
        const c = s.checks.find((x) => x.id === 'autosave');
        if (c?.action?.disabled) return { dialog: { kind: 'civRunning' } };
        Object.assign(c, { level: 'ok', title: 'Civ saves every turn', text: 'Fixed. Autosave is now every turn.', action: undefined });
        return { dialog: { kind: 'fixed', file: 'Documents\\My Games\\Sid Meier\'s Civilization 5\\UserSettings.ini', backup: 'UserSettings.ini.before-ffaciv',
          changes: [{ key: 'TurnsBetweenAutosave', was: '5', now: '1' }, { key: 'NumAutosavesKept', was: '10', now: '100' }] } };
      }
      case 'checkCrashRecords':
        if (!s.settings.diagnostics) return { toast: { kind: 'info', title: 'Recorder diagnostics are off', text: 'Enable diagnostic collection in Recorder to check crash records.' } };
        s.crashDiagnostics = { busy: false, checkedAt: Date.now(), status: 'No Civ V crash records in the last 24 hours.', crashes: [] };
        return { toast: { kind: 'info', title: 'Checking local crash records', text: 'Looking for Civ V faults reported by Windows in the last 24 hours. Nothing is uploaded.' } };
      case 'scanOldGames': if (s.oldGames) Object.assign(s.oldGames, { scanning: false, scanned: true, scannedAt: Date.now() }); return {};
      case 'addOldFolder': return preview('This opens a folder picker; the games in that folder (and its zip files) are listed here.');
      case 'stopOldUpload': if (s.oldGames) { s.oldGames.uploading = false; s.oldGames.games.forEach((g) => { if (['uploading', 'queued'].includes(g.status)) g.status = 'stopped'; }); } return {};
      case 'dismissOldHint': s.oldHintDismissed = true; return {};
      case 'setOldGamesUpload': s.settings.oldGamesUpload = a.format; return {toast:{kind:'good',title:'Upload size saved',text:a.format==='delta'?'Old games upload smaller: this PC sends only what changed from save to save. The replay is the same.':'Old games upload as whole saves: the website does all the work. The replay is the same.'}};
      case 'uploadOldGames':
        if (a.format) s.settings.oldGamesUpload = a.format;
        if (s.oldGames) { s.oldGames.uploading = true; s.oldGames.games.forEach((g) => { if (a.ids?.includes(g.id)) g.status = 'queued'; }); }
        return {toast:{kind:'good',title:(a.ids?.length??0)===1?'Uploading 1 game':`Uploading ${a.ids?.length??0} games`,text:'To ffaciv.com, where they become replays.'}};
      case 'refreshLobby': return {toast:{kind:'info',title:'Lobby refresh requested',text:'Checking the lobby again.'}};
      case 'updateNow': return {toast:{kind:'info',title:'Download preview',text:'The real recorder opens the official release page. It never installs automatically.'}};
      case 'updateLater': if (s.updates.available) s.updates.available.later = a.when; return { toast: { kind: 'info', title: 'Update later', text: { tonight: 'We will ask again tonight at 22:00.', tomorrow: 'We will ask again tomorrow.', next: 'We will ask the next time the recorder starts.' }[a.when] } };
      case 'sendReport': return { toast: { kind: 'info', title: 'Diagnostic report preview', text: 'The real recorder prepares a report zip on your Desktop and confirms when it is saved. Nothing is sent automatically.' } };
      case 'editSettings': return { toast: { kind: 'info', title: 'settings.txt opened', text: 'Changes there are used after the recorder is started again (tray icon → Quit, then start it).' } };
      case 'finishFirstRun': s.firstRun = false; return {toast:{kind:'good',title:'Setup complete',text:'Start Civ V when you’re ready.'}};
      case 'openSite': return preview('This opens ffaciv.com in your browser.');
      case 'openFolder': return preview('This opens the folder in Explorer.');
      case 'chooseFolder': return preview('Changing the folder comes later. It is set in settings.txt (output_folder) for now.');
      case 'copyLiveLink': return { toast: { kind: 'good', title: 'Invite link copied', text: s.livestream?.url ?? cur?.liveLink?.url ?? '', actions: [{ label: 'Open stream lobby', cmd: 'openSite', args: { url: s.livestream?.url ?? cur?.liveLink?.url }, primary: true }] } };
      default: return {};
    }
  }
  return { load, handle, scenarios, toasts, get state() { return state; }, get scenario() { return scenario; } };
})();
