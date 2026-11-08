// Made-up data for the preview (no Civ, no internet needed). The window says "Preview · made-up data" while this runs.
// Each scenario is the same state shape the real program sends (see bridge.js), so the screens are the real ones.
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
  const games = [
    { id: 'g5', mod: 'Lekmod', modVersion: '35.4', date: now - 2 * H, turns: 34, players: lobby, status: 'recording' },
    { id: 'g4', mod: 'Lekmod', modVersion: '35.4', date: now - 1 * D - 5 * H, turns: 186, players: lobby.slice(0, 5), status: 'countdown', publicAt: now + 2 * H + 40 * M },
    { id: 'g3', mod: 'Vanilla', modVersion: 'BNW', date: now - 3 * D, turns: 147, players: lobby.slice(0, 6).map((p, i) => i === 3 ? { ...p, civ: 'Venice' } : p), status: 'public', url: 'https://ffaciv.com/replay/game/vanilla-3f9a' },
    { id: 'g2', mod: 'Lekmod', modVersion: '35.4', date: now - 6 * D, turns: 92, players: lobby.slice(0, 4), status: 'withdrawn' },
    { id: 'g1', mod: 'Snowve', modVersion: '8.2', date: now - 9 * D, turns: 211, players: lobby.slice(1, 6).concat([P('You', 'Arabia', '#4b8f39', '#f0e3ad', true)]), status: 'local' },
  ];
  const okChecks = [
    { id: 'autosave', level: 'ok', title: 'Civ saves every turn', text: 'Autosave is set to every turn, so the replay has every turn.' },
    { id: 'version', level: 'ok', title: 'Lekmod 35.4 is supported', text: 'This game version can be recorded.' },
    { id: 'internet', level: 'ok', title: 'Connected to ffaciv.com', text: 'Turns are sent as they are saved.' },
    { id: 'disk', level: 'ok', title: 'Enough disk space', text: '48 GB free. A long game needs about 200 MB.' },
  ];
  const base = () => ({
    reportBusy: false,
    livestream: {active:false,canStart:false,reason:'Enable movement recording and uploads, then start a supported game.'},
    live: false, version: '0.3.0', firstRun: false,
    status: { kind: 'recording', title: 'Recording Lekmod game', detail: 'Turn 34 · 6 players · multiplayer' },
    current: {
      id: 'g5', mod: 'Lekmod', modVersion: '35.4', mode: 'multiplayer', turn: 34, players: lobby,
      startedAt: now - 2 * H - 12 * M, lastSaveAt: now - 2 * M, paused: false,
      upload: { state: 'sent', lastSentTurn: 34, queued: 0 },
      publish: { state: 'locked' },
      liveLink: { url: 'http://127.0.0.1:5632/replay/live/1a2b3c4d-5e6f7a8b/watch', disabled: false },
    },
    checks: structuredClone(okChecks),
    games: structuredClone(games),
    requests: [{ id: 'r1', who: 'CasterCiv', role: 'caster', game: 'Lekmod game · turn 34', at: now - 3 * M }],
    permissions: [
      { id: 'p1', who: 'FFA League stream', role: 'organiser', scope: 'always' },
      { id: 'p2', who: 'Kharn', role: 'player', scope: 'game', game: 'Lekmod · 26 Oct' },
    ],
    updates: {
      current: '0.3.0', autoSmall: true,
      available: { version: '0.3.1', size: '1.2 MB', required: false, notes: ['Snowve 8.2 games are recorded', 'Fewer pop-ups at the start of a game'] },
      changelog: [
        { version: 'map 2026.10.26', date: now - 1 * D, kind: 'map', auto: true, text: 'Lekmod 35.4: city names read for every city' },
        { version: 'look 7', date: now - 2 * D, kind: 'look', auto: true, text: 'Clearer game list; bigger buttons' },
        { version: '0.3.0', date: now - 5 * D, kind: 'program', auto: false, text: 'Sends turns as they are saved; publish countdown' },
      ],
    },
    settings: {
      recording: true, trackLogs: true, saveUpload: 'delta', uploadTiming: 'during', stream: true, upload: true, publishDelay: 3,
      notify: { lobby: true, saved: false, reminders: true, problems: true, requests: true },
      startWithWindows: true, dataFolder: 'C:\\Users\\You\\AppData\\Local\\FFACiv\\Recorder', steam: { linked: false }, language: 'en',
    },
  });

  // Each scenario: a change on top of the base. Names are what the preview strip lists.
  const scenarios = {
    resourcepaused: {label:'Resources paused · example data',apply:s=>{scenarios.safeCapture.apply(s);s.resources={...s.resources,paused:true,reason:'Waiting for computer load to ease',cpuPercent:0.4,systemCpuPercent:91.2};}},
    onboarding: {label:'First launch · setup choices',apply:s=>{scenarios.safeCapture.apply(s);s.firstRun=true;s.games=[];s.oldGames=null;s.status={kind:'waiting',title:'Finish setup to start',detail:'Choose your recording and upload preferences.'};}},
    scheduled: {label:'Finished game · hidden 2 hours',apply:s=>{scenarios.safeCapture.apply(s);s.games[0].uploadSchedule={state:'waiting',enabled:true,ended:true,dueAt:Date.now()+2*H-5*60e3,held:false,hidden:true};s.games[0].status='local';s.games[0].replay={};}},
    finishwaits: {label:'Livestreamed game · finish waits for the main menu',apply:s=>{scenarios.replaycontrols.apply(s);Object.assign(s.games[0],{turns:46,firstTurn:1,lastTurn:46,status:'streamed',ended:true,sentSaves:47,queuedSaves:0,waitsForMenu:true,
      uploadWaitReason:'Civ is still open in a game or a lobby. The replay is made by itself once Civ is at its main menu or closed.',uploadSchedule:{state:'upload',enabled:true,ended:true,held:false,hidden:false},
      replay:{state:'recording',lastTurn:46,saves:47,processed:0,finishAt:Date.now()+55*60e3,keepShort:false,private:true,completionQueued:true,publicRequested:true}});}},
    updatewait: {label:'Updates · automatic check waits for Civ',apply:s=>{scenarios.safeCapture.apply(s);s.updates={...s.updates,available:null,checking:false,checkedAt:Date.now()-5*H,waited:true};}},
    streamready: {label:'Experimental stream · ready',apply:s=>{scenarios.recording.apply(s);s.livestream={active:false,canStart:true};s.live=true;s.production=true;}},
    streaming: {label:'Private livestream · active',apply:s=>{scenarios.streamready.apply(s);s.livestream={active:true,canStart:false,url:'https://ffaciv.com/replay/live/private-example',viewers:2};}},
    unsupportedbackup: { label: 'Unsupported build backup · example data', apply: s => {
      scenarios.replaycontrols.apply(s);
      s.status = {kind:'attention',title:'Unsupported build — keeping autosaves',detail:'12 autosaves kept on this PC, with a diagnostic log. Upload them after support is fixed. Realtime movement logs are unavailable.',action:{cmd:'openFolder',label:'Open backup folder',args:{path:'example'}}};
      s.checks.unshift({id:'unsupported',level:'warn',title:'Autosave backup only',text:'The game DLL is not recognized. Original save bytes and diagnostics are kept locally; no movement data is guessed.'});
    } },
    safeCapture: {label:'Save-file recorder · example data',apply:s=>{scenarios.replaycontrols.apply(s);s.safeCapture=true;s.version='0.7.6';s.resources={reason:'Ready for background work',paused:false,cpuPercent:0.3,systemCpuPercent:24,memoryBytes:68157440,peakMemoryBytes:83886080,uploadKibPerSecond:256,cpuLimitPercent:0,cpuLimitError:null};s.checks=s.checks.filter(c=>c.id!=='version').map(c=>c.title.startsWith('Connected')?{...c,level:'info',title:'Uploads wait until after the game',text:'Automatic uploads start once Civ is in its main menu or closed (not a staging lobby). The replay stays hidden for 2 hours.'}:c);s.settings.trackLogs=false;s.settings.looksPerSecond=4;s.memoryLogging={enabled:false,canEnable:true,reason:'Needs a supported Civ build. Resource controls still apply.',scope:'game capture',readsPerSecond:4,cityManagementIntervalMs:1000,turnQuietMs:5000};s.settings.uploadTiming='after';s.current=null;s.status={kind:'waiting',title:'Ready for your next game',detail:'Start Civ V. Save copies stay on this PC while you play.'};}},
    releaseupdate: { label: 'Update notice · example data', apply: s => {
      scenarios.replaycontrols.apply(s);
      s.updates={current:'0.7.6',checkedAt:Date.now(),available:{version:'0.7.6',required:false,url:'https://ffaciv.com/replay/recorder/',notes:['Example: support for a new mod build','Example: recording fixes']},changelog:[]};
    } },
    replaycontrols: { label: 'Replay controls · example data', apply: s => {
      scenarios.testbuild.apply(s); s.live = true; s.production = true; s.site = true; s.serverShort = 'ffaciv.com'; s.serverName = 'ffaciv.com'; s.firstRun = false; s.version = '0.7.6'; s.current = null;
      s.checks = s.checks.map(c => c.title.startsWith('Connected to') ? {...c,title:'Connected to ffaciv.com'} : c);
      s.settings.captureMoves = true; s.requests = []; s.updates.available = null;
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
        s.updates = {current:'0.6.0',checkedAt:Date.now(),available:null,changelog:[]};
        s.oldGames.games[0] = {...s.oldGames.games[0],saves:72,firstTurn:0,lastTurn:71,status:'processing',done:38,total:72,note:'Generating replay… 38 of 72 saves processed.'};
        if(s.oldGames.games[1]) s.oldGames.games[1] = {...s.oldGames.games[1],status:'done',done:31,total:31,note:'Replay generated and stored.',links:[{url:'https://ffaciv.com/replay/'}]};
      }
    } },
    // the Saves list, its waiting reason and a refused save; a deletion held 7 days with Undo (2026-10-31)
    savesfeedback: { label: 'Saves list · waiting, refused, deleted', apply: s => {
      scenarios.replaycontrols.apply(s);
      const saves = (first, last, sent, at0, step) => Array.from({length:last-first+1},(_,i)=>({file:`AutoSave_Post_${String(first+i).padStart(4,'0')} AD-${2000+first+i}.Civ5Save`,turn:first+i,at:at0+i*step,id:first+i<=sent?`2026103110${String(first+i).padStart(4,'0')}-0000000${(first+i)%10}`:null,beforeStream:false,refused:null}));
      const files = (list, converted) => list.filter(x=>x.id).map(x=>({id:x.id,file:x.file,turn:x.turn,at:x.at,state:x.turn<=converted?'converted':'waiting',note:null}));
      const cur = saves(30, 47, 43, now-40*M, 2*M);
      cur[cur.length-1].refused = null;
      cur.find(x=>x.turn===44).refused = {at:now-6*M,status:413,why:'a save is at most 25 MB'};
      s.current = {id:'live',context:'d'.repeat(32),mod:'Lekmod',modVersion:'35.4',mode:'multiplayer',turn:47,players:lobby,startedAt:now-2*H,lastSaveAt:now-1*M,paused:false,
        upload:{state:'sending',lastSentTurn:43,queued:4},publish:{state:'local'},firstTurn:30,lastTurn:47,savedTurns:18,saves:cur,
        savesWait:'Saves are sent when you leave the game: the main menu or Civ closed, not a staging lobby. The replay then stays hidden for 2 hours. To send while you play, close Civ and choose While playing.',
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
      s.status = { kind: 'found', title: 'In the multiplayer lobby', detail: '4 players · LekmapPangaeaFractalv6.3 · turn timer 90 s. Recorded when the game starts.' };
      const L = (name, civ, you, human, team) => ({ name, civ, color: you ? '#f0c860' : human ? '#5ec8bc' : '#7d8b88', you: !!you, human: !!human, team });
      s.current = { id: 'lobby', lobby: true, mod: 'Lekmod', modVersion: '35.4', mode: 'multiplayer lobby', gameName: 'LEKMOD v35.4.003 FFA',
        players: [L('vik', 'Armenia', true, true, 0), L('raffyG', 'Random', false, true, 1), L('Mansa_Moose', 'Babylon', false, true, 2), L('Kharn', 'Random', false, true, 3), L('Open', '–', false, false, 4), L('AI', 'Inca', false, false, 5)],
        map: 'LekmapPangaeaFractalv6.3', mapSize: 'Small', turnTimerSeconds: 90, turnTimerOn: true, options: 7,
        upload: { state: 'sent', target: 'test' }, publish: { state: 'local' },
        liveLink: { url: 'http://127.0.0.1:5632/replay/live/channel/3f9a0c7e5b1d4e2f8a6c9b0d1e2f3a4b', disabled: false, channel: true } };
      s.games.shift();
    } },
    testbuildsp: { label: 'Test build · single player not recorded', apply: (s) => {
      scenarios.testbuild.apply(s);
      s.status = { kind: 'paused', title: 'Single-player game — not recorded', detail: 'Single-player games are off in Settings (for testing, coming later). Multiplayer and hot seat games are recorded.', action: { cmd: 'recordAnyway', label: 'Record it anyway' } };
      s.current = null; s.games.shift();
    } },
    // the test build (OR03): the state shape LiveBackend sends while a game is recorded and streamed to the local test server
    testbuild: { label: 'Test build · recording + stream', apply: (s) => {
      const T = (civ, color, you, human) => ({ name: human ? 'Human' : 'AI', civ, color, you: !!you, human: !!human, alive: true });
      const civs = [T('Argentina', '#f0c860', true, true), T('Morocco', '#7d8b88'), T('Cuba Batista', '#7d8b88'), T('Burma', '#7d8b88'), T('Zimbabwe', '#7d8b88'), T('India', '#7d8b88')];
      s.testBuild = true; s.version = '0.3.1'; s.requests = []; s.permissions = []; s.updates.available = null;
      s.test = { streamUrl: 'http://127.0.0.1:5631/replay/api/uploader/', liveSiteUrl: 'http://127.0.0.1:5632/replay/', settingsFile: 'C:\\Users\\You\\AppData\\Local\\Programs\\FFACiv Recorder\\settings.txt', streamState: 'connected' };
      s.settings.stream = true; s.settings.upload = false; s.settings.looksPerSecond = 20;
      s.checks = [...s.checks];
      s.status = { kind: 'recording', title: 'Recording Snowve game', detail: 'Turn 12 · single player' };
      s.current = { id: 'live', mod: 'Snowve', modVersion: '8.3.0', mode: 'single player', turn: 12, players: civs, cityStates: 16,
        startedAt: now - 12 * M, lastSaveAt: now - 40e3, paused: false, publish: { state: 'local' },
        upload: { state: 'sent', target: 'test', server: 'http://127.0.0.1:5631/replay/api/uploader/', queued: 0, queuedBytes: 0, lastSentTurn: 11, sentSaves: 24,
          lastSentAt: now - 3e3, wholeSaves: 3, deltaSaves: 21, eventBytes: 96 * 1024, saveBytes: 2.4 * 1048576 },
        liveLink: { url: 'http://127.0.0.1:5632/replay/live/1a2b3c4d-5e6f7a8b/watch', disabled: false } };
      s.checks = [okChecks[0], { id: 'version', level: 'ok', title: 'Snowve 8.3.0 is supported', text: 'This game version can be recorded.' }, okChecks[3],
        { id: 'stream', level: 'ok', title: 'Connected to the test server', text: 'Moves go out every 3 s, saves when Civ writes them.' }];
      s.games = [{ id: 's3', mod: 'Snowve', modVersion: '8.3.0', date: now - 12 * M, turns: 12, players: civs, status: 'recording' },
        { id: 's2', mod: 'Lekmod', modVersion: '35.4', date: now - 3 * H, turns: 14, players: civs.slice(0, 5), status: 'streamed', lastSentTurn: 13, canBlock: true, blocked: true, blockConfirmed: true },
        { id: 's1', mod: 'Vanilla', modVersion: 'BNW', date: now - 9 * H, turns: 146, players: civs.slice(0, 4), status: 'streamed', lastSentTurn: 146, canBlock: true, blocked: false }];
      s.games[0] = { ...s.games[0], canBlock: false, blockWhy: 'This game is still being played. Block it after the game.' };
      s.settings.recordSinglePlayer = false;
      const O = (id, mod, from, mins, saves, t0, t1, status, extra = {}) => ({ id, mod, from, to: from + mins * M, saves, firstTurn: t0, lastTurn: t1, mapSize: 'Small', speed: 'Quick', map: 'LekmapPangaeaFractalv6.3', folder: 'C:\\Users\\You\\Documents\\My Games\\Sid Meier\'s Civilization 5\\ModdedSaves\\multi\\auto', megabytes: +(saves * 1.1).toFixed(1), already: 0, status, done: 0, total: saves, ...extra });
      s.oldGames = { scanning: false, scanned: true, uploading: true, folders: ['…\\Saves\\multi\\auto', '…\\ModdedSaves\\multi\\auto'], newCount: 3, games: [
        O('a1', 'Lekmod 35.4', now - 1 * D, 190, 120, 0, 118, 'uploading', { done: 46 }),
        O('a2', 'Civ V (no mods)', now - 4 * D, 75, 31, 0, 30, 'new', { map: 'Pangaea', mapSize: 'Standard', speed: 'Standard', folder: 'C:\\Users\\You\\Documents\\My Games\\Sid Meier\'s Civilization 5\\Saves\\single\\auto' }),
        O('a3', 'Lekmod 35.4', now - 6 * D, 240, 155, 0, 152, 'partial', { already: 20 }),
        O('a4', 'Snowve 8.3', now - 12 * D, 30, 16, 0, 15, 'uploaded', { already: 16 }),
      ] };
    } },
    waiting: { label: 'Waiting for Civ V', apply: (s) => {
      s.status = { kind: 'waiting', title: 'Waiting for Civ V', detail: 'Start a game and it is recorded by itself.' };
      s.current = null; s.requests = []; s.games[0].status = 'sending'; s.games[0].percent = 80; s.updates.available = null;
    } },
    lobby: { label: 'In the game lobby', apply: (s) => {
      s.status = { kind: 'found', title: 'Civ V found · Lekmod 35.4', detail: 'In the multiplayer lobby. Recording starts with the game.' };
      s.current = null; s.requests = []; s.updates.available = null;
      s.games.shift();
    } },
    unsupported: { label: 'Version not supported', apply: (s) => {
      s.status = { kind: 'unsupported', title: 'Civ V found — this version is not supported yet', detail: 'Lekmod 35.5 is new. We are adding it; you do not need to do anything.', note: 'Your autosaves are still kept, so this game can be replayed once Lekmod 35.5 is supported.' };
      s.current = null; s.requests = []; s.games.shift();
      s.checks[1] = { id: 'version', level: 'warn', title: 'Lekmod 35.5 is not supported yet', text: 'Moves between turns are not recorded for this version. The autosaves are kept and sent.' };
    } },
    countdown: { label: 'Game over, countdown', apply: (s) => {
      s.status = { kind: 'waiting', title: 'Game saved', detail: 'Your last game is safe on ffaciv.com. Waiting for the next one.' };
      s.games.shift(); s.requests = []; s.updates.available = null;
      s.current = { ...s.current, id: 'g4', turn: 186, players: lobby.slice(0, 5), ended: true, startedAt: now - 9 * H, lastSaveAt: now - 20 * M,
        publish: { state: 'countdown', publicAt: now + 2 * H + 40 * M, delayHours: 3 }, upload: { state: 'sent', lastSentTurn: 186, queued: 0 } };
    } },
    sending: { label: 'Sending a game', apply: (s) => {
      s.status = { kind: 'sending', title: 'Sending your game', detail: '80 % · turn 150 of 186' };
      s.games.shift(); s.games[0].status = 'sending'; s.games[0].percent = 80; delete s.games[0].publicAt;
      s.current = { ...s.current, id: 'g4', turn: 186, players: lobby.slice(0, 5), ended: true, upload: { state: 'sending', percent: 80, lastSentTurn: 150 }, publish: { state: 'locked' } };
      s.requests = [];
    } },
    attention: { label: 'Needs attention', apply: (s) => {
      s.status = { kind: 'recording', title: 'Recording Lekmod game', detail: 'Turn 34 · 6 players · multiplayer' };
      s.current.upload = { state: 'queued', queued: 3, lastSentTurn: 31 };
      s.checks[0] = { id: 'autosave', level: 'warn', title: 'Civ does not save every turn', text: 'Civ saves only every 5 turns, so the replay would miss turns. Civ must be closed to change this.',
        action: { cmd: 'fixAutosave', label: 'Fix it for me', disabled: true, why: 'Close Civ first, then press this.' } };
      s.checks[2] = { id: 'internet', level: 'warn', title: 'No internet right now', text: '3 turns are waiting. They are sent when you are back online; nothing is lost.', action: { cmd: 'retry', label: 'Try again' } };
      s.checks.push({ id: 'ingame', level: 'info', title: 'The in-game recorder is also installed', text: 'Found the FFACiv Recorder mod in Civ\'s DLC folder. Both can run; you only need one. To use only this program, move the folder FFACiv_Recorder out of Assets\\DLC while Civ is closed.',
        action: { cmd: 'openFolder', label: 'Show the folder', args: { path: 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\Sid Meier\'s Civilization V\\Assets\\DLC' } } });
      s.checks.push({ id: 'disk', level: 'warn', title: 'Disk space is low', text: 'Only 1.1 GB free on C:. Recording goes on; free some space soon.' });
      s.checks.splice(3, 1);
    } },
    fixable: { label: 'Autosave fix (Civ closed)', apply: (s) => {
      s.status = { kind: 'waiting', title: 'Waiting for Civ V', detail: 'Start a game and it is recorded by itself.' };
      s.current = null; s.requests = []; s.games.shift(); s.updates.available = null;
      s.checks[0] = { id: 'autosave', level: 'warn', title: 'Civ does not save every turn', text: 'Civ saves only every 5 turns and keeps 10 saves. The replay needs a save every turn.',
        action: { cmd: 'fixAutosave', label: 'Fix it for me' } };
    } },
    required: { label: 'Required update', apply: (s) => {
      s.status = { kind: 'attention', title: 'Please update to keep recording', detail: 'This version can no longer send games.' };
      s.current = null; s.requests = []; s.games.shift();
      s.updates.available = { version: '0.4.0', size: '3.8 MB', required: true, notes: ['ffaciv.com changed how games are sent', 'Your saved games are kept and sent after the update'] };
    } },
    firstrun: { label: 'First start', apply: (s) => {
      s.firstRun = true; s.current = null; s.requests = []; s.games = []; s.permissions = []; s.updates.available = null;
      s.status = { kind: 'waiting', title: 'Waiting for Civ V', detail: 'Start a game and it is recorded by itself.' };
      s.checks[0] = { id: 'autosave', level: 'warn', title: 'Civ does not save every turn', text: 'Civ saves only every 5 turns.', action: { cmd: 'fixAutosave', label: 'Fix it for me' } };
    } },
  };

  // Pop-ups the preview can show (the toast window and the pictures use these).
  const toasts = {
    liveReady: { kind:'good', title:'Livestream ready', text:'Share this link so others can watch.\nhttps://ffaciv-test.pages.dev/replay/live/12345678-12345678/watch', actions:[{label:'Open livestream',cmd:'openSite',args:{url:'https://ffaciv-test.pages.dev/replay/live/12345678-12345678/watch'}},{label:'Copy link',cmd:'copyLiveLink',primary:true}] },
    lobby: { kind: 'good', title: 'Game detected · recording', text: 'Civ V · save copies stay on this PC while you play.', actions: [{ label: 'Stop recording', cmd: 'pauseGame' }, {label:'Options',cmd:'showOptions'}] },
    started: { kind: 'good', title: 'Game detected · recording', text: 'Lekmod game · turn 1 · 6 players', actions:[{label:'Stop recording',cmd:'pauseGame'},{label:'Options',cmd:'showOptions'}] },
    ended: {kind:'info',title:'Finished playing?',text:'Civ is closed. Upload now or wait 2 hours. Your recording stays on this PC until upload.',actions:[{label:'Upload now',cmd:'uploadGameNow',args:{game:'short'},primary:true},{label:'Keep playing',cmd:'continueGame',args:{game:'short'}}]},
    uploadWarning: {kind:'warn',title:'Your Civ V game uploads in 5 min',text:'The game will be uploaded to ffaciv.com. Postpone or delete if you do not want to share it yet.',actions:[{label:'Postpone 2 hours',cmd:'postponeUpload',args:{game:'short',hours:2}},{label:'Delete recording',cmd:'reviewDelete',args:{game:'short'}}]},
    saved: { kind: 'good', title: 'Turn 34 saved', text: 'Sent to ffaciv.com. Private until the game is over.' },
    reminder: { kind: 'info', title: 'Your game goes public in 15 min', text: 'Lekmod · 186 turns · 5 players', actions: [{ label: 'Postpone', cmd: 'postpone', args: { game: 'g4', hours: 24 } }, { label: 'Make public now', cmd: 'publishNow', args: { game: 'g4' }, primary: true }] },
    request: { kind: 'request', title: 'CasterCiv wants to watch you live', text: 'They would see the game as you see it, for this game only.', actions: [{ label: 'Decline', cmd: 'declineRequest', args: { id: 'r1' } }, { label: 'Allow', cmd: 'allowRequest', args: { id: 'r1', scope: 'game' }, primary: true }] },
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
  const fmtDay = (t) => new Date(t).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });

  // The same commands the real program takes. Returns { toast?, dialog? } for the window to show.
  function handle(cmd, a = {}) {
    const s = state, cur = s.current;
    switch (cmd) {
      case 'simpleSetup': Object.assign(s.settings,{recording:true,experimentalMemoryOptIn:false,trackLogs:false,captureMoves:false,saveUpload:'full',uploadTiming:'after',stream:true});return {toast:{kind:'good',title:'Simple setup chosen',text:'The recorder copies Civ’s autosaves and uploads them after the game. Nothing reads Civ’s memory.'}};
      case 'setUploadFormat': if(!['delta','full'].includes(a.format))return {};s.settings.saveUpload=a.format;return {toast:{kind:'good',title:'Upload method saved',text:a.format==='delta'?'Save changes will be prepared locally. The site still generates the replay.':'Complete save files will be sent for the site to process.'}};
      case 'setMemoryLogging': {if(a.enabled && s.settings.recording===false)return {toast:{kind:'warn',title:'Automatic recording is off',text:'Turn it on before adding memory logs.'}};s.settings.trackLogs=!!a.enabled;s.memoryLogging={enabled:!!a.enabled,canEnable:s.settings.recording!==false,reason:'Needs a supported Civ build. Resource controls still apply.',scope:'game capture',readsPerSecond:4,cityManagementIntervalMs:1000,turnQuietMs:5000};return {toast:{kind:'good',title:a.enabled?'Memory logs enabled':'Memory logs off',text:a.enabled?'Read-only observations are enabled for supported games. Upload and livestream preferences are unchanged.':'Your save files are still recorded.'}};}
      case 'setCaptureMethod': {
        s.settings.recording=a.method!=='manual'; if(a.method==='memory')s.settings.trackLogs=true;s.memoryLogging={enabled:s.settings.recording && !!s.settings.trackLogs,canEnable:s.settings.recording,reason:s.settings.recording?'Needs a supported Civ build. Resource controls still apply.':'Turn automatic recording on first.',scope:'game capture',readsPerSecond:4,cityManagementIntervalMs:1000,turnQuietMs:5000};s.settings.uploadTiming='after';
        return {toast:{kind:'good',title:'Recording method saved',text:a.method==='manual'?'Keep your own saves and choose Manual upload later.':a.method==='memory'?'Experimental observations are enabled. Livestreaming remains off.':'Automatic save copies are enabled. Your memory logging choice is kept.'}};
      }
      case 'startLivestream': s.livestream={...s.livestream,active:true,canStart:false,scope:a.scope??'game',expiresAt:Date.now()+(a.durationHours??4)*H,url:'https://ffaciv.com/replay/live/share/private-example'};return {toast:{kind:'good',title:'Private livestream started',text:'Copy your invite link to share it.'}};
      case 'stopLivestream': s.livestream={active:false,canStart:true};return {toast:{kind:'info',title:'Livestream stopped',text:'The private link no longer shows this game live.'}};
      case 'uploadGameNow': {const g=find(a.game);if(g){delete g.uploadSchedule;g.status='sending';}return {toast:{kind:'good',title:'Upload queued',text:'Your game will upload when Civ is closed and the server is available.'}};}
      case 'postponeUpload': {const g=find(a.game);if(g)g.uploadSchedule={state:a.hours?'waiting':'local',enabled:true,ended:true,held:a.hours===0,dueAt:a.hours?Date.now()+a.hours*H:null};return {toast:{kind:'info',title:'Upload postponed',text:a.hours?'The new countdown is shown on your game.':'Your game stays on this PC until you upload it.'}};}
      case 'publishNow': {
        const g = find(a.game ?? cur?.id); if (g) { g.status = 'public'; g.url = 'https://ffaciv.com/replay/game/lekmod-8c21'; delete g.publicAt; }
        if (cur && cur.id === (a.game ?? cur.id)) cur.publish = { state: 'public', url: 'https://ffaciv.com/replay/game/lekmod-8c21' };
        return { toast: { kind: 'good', title: 'Your game is public', text: 'Anyone with the link can watch it on ffaciv.com.' } };
      }
      case 'postpone': {
        const g = find(a.game ?? cur?.id), hours = a.hours ?? 24;
        const until = hours === 0 ? null : (g?.publicAt ?? now) + hours * H;
        if (g) { g.status = hours === 0 ? 'locked' : 'countdown'; g.publicAt = until ?? undefined; }
        if (cur && cur.id === g?.id) cur.publish = hours === 0 ? { state: 'locked', manual: true } : { state: 'countdown', publicAt: until, postponed: true };
        return { toast: { kind: 'info', title: 'Postponed', text: hours === 0 ? 'It stays private until you make it public.' : `It goes public on ${new Date(until).toLocaleString('en-GB', { weekday: 'long', hour: '2-digit', minute: '2-digit' })}.` } };
      }
      case 'dontRecord':
        s.status = { kind: 'paused', title: 'Not recording this game', detail: 'You chose not to record it. The next game is recorded again.' };
        if (cur) cur.paused = true;
        return { toast: { kind: 'info', title: 'This game is not recorded', text: 'Nothing from it is sent. The next game is recorded again.' } };
      case 'deleteGame': { const g = find(a.game) ?? (s.current?.id === a.game ? s.current : null); s.games = s.games.filter(x => x.id !== a.game); if(s.current?.id === a.game)s.current=null;
        const key = 'recording:' + (g?.context ?? a.game); if (g) (s.deleted ??= []).unshift({key, deletedAt: Date.now(), due: Date.now() + 7*24*H, card: {...g, saves: undefined}});
        return {toast:{kind:'info',title:'Recording deleted · Undo for 7 days',text:'Hidden from your list and from the website now. It is erased for good in 7 days; until then Undo brings it back.',actions:[{label:'Undo',cmd:'undoDelete',args:{key},primary:true}]}}; }
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
      case 'withdraw': { const g = find(a.game); if (g) { g.status = 'withdrawn'; delete g.url; delete g.publicAt; } return { toast: { kind: 'info', title: 'Game withdrawn', text: 'It is no longer on ffaciv.com. Your own copy is kept.' } }; }
      case 'upload': { const g = find(a.game); if (g) { g.status = 'sending'; g.percent = 5; } return {}; }
      case 'allowRequest': {
        const r = s.requests.find((x) => x.id === a.id); s.requests = s.requests.filter((x) => x.id !== a.id);
        if (r) s.permissions.unshift({ id: 'p' + Date.now(), who: r.who, role: r.role, scope: a.scope ?? 'game', game: a.scope === 'always' ? undefined : r.game });
        return { toast: { kind: 'good', title: `${r?.who ?? 'They'} can watch`, text: a.scope === 'always' ? 'Always, until you take it back in Requests.' : 'For this game only.' } };
      }
      case 'declineRequest': s.requests = s.requests.filter((x) => x.id !== a.id); return {};
      case 'revoke': s.permissions = s.permissions.filter((x) => x.id !== a.id); return {};
      case 'setSetting': {
        const path = a.key.split('.'); let o = s.settings; while (path.length > 1) o = o[path.shift()]; o[path[0]] = a.value;
        if(a.key==='recording' && !a.value)s.livestream={active:false,canStart:false};
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
      case 'refreshLobby': return {toast:{kind:'info',title:'Lobby refresh requested',text:'Checking the lobby again.'}};
      case 'retry': return { toast: { kind: 'warn', title: 'Still offline', text: 'We try again by ourselves every minute.' } };
      case 'updateNow':
        if(s.production) return {toast:{kind:'info',title:'Download preview',text:'The real recorder opens the official release page. It never installs automatically.'}};
        s.updates.changelog.unshift({ version: s.updates.available?.version ?? '?', date: Date.now(), kind: 'program', auto: false, text: (s.updates.available?.notes ?? []).join('; ') });
        s.updates.current = s.updates.available?.version ?? s.updates.current; s.version = s.updates.current; s.updates.available = null;
        if (s.status.title.startsWith('Please update')) s.status = { kind: 'waiting', title: 'Waiting for Civ V', detail: 'Start a game and it is recorded by itself.' };
        return { toast: { kind: 'good', title: `Updated to ${s.version}`, text: 'The recorder restarted in a second. Nothing was lost.' } };
      case 'updateLater': if (s.updates.available) s.updates.available.later = a.when; return { toast: { kind: 'info', title: 'Update later', text: { tonight: 'We will ask again tonight at 22:00.', tomorrow: 'We will ask again tomorrow.', next: 'We will ask the next time the recorder starts.' }[a.when] } };
      case 'sendReport': return { toast: { kind: 'info', title: 'Diagnostic report preview', text: 'The real recorder prepares a report zip on your Desktop and confirms when it is saved. Nothing is sent automatically.' } };
      case 'linkSteam': s.settings.steam = { linked: true, name: 'You (Steam)' }; return {};
      case 'unlinkSteam': s.settings.steam = { linked: false }; return {};
      case 'finishFirstRun': s.firstRun = false; return {toast:{kind:'good',title:'Setup complete',text:'Start Civ V when you’re ready.'}};
      case 'openSite': case 'openFolder': case 'chooseFolder': return { toast: { kind: 'info', title: 'Preview', text: cmd === 'openSite' ? 'This opens ffaciv.com in your browser.' : 'This opens the folder in Explorer.' } };
      case 'copyLiveLink': return { toast: { kind: 'good', title: 'Invite link copied', text: s.livestream?.url ?? cur?.liveLink?.url ?? '', actions: [{ label: 'Open stream lobby', cmd: 'openSite', args: { url: s.livestream?.url ?? cur?.liveLink?.url }, primary: true }] } };
      default: return {};
    }
  }
  return { load, handle, scenarios, toasts, get state() { return state; }, get scenario() { return scenario; }, fmtDay };
})();
