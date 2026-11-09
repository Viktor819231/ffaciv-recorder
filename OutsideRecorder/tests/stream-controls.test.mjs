import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
// The window's scripts in the order index.html loads them. Icons, the made-up recorder and the bridge stay out: each test
// sets the state itself, and the window's drawing functions are plain globals of the scripts.
const read = (path) => readFileSync(new URL('../ui/' + path, import.meta.url), 'utf8');
const scripts = [...read('index.html').matchAll(/<script src="(js\/[^"]+)"><\/script>/g)].map(([, f]) => f)
  .filter((f) => !['js/icons.js', 'js/mock.js', 'js/bridge.js'].includes(f));
function ui(state) {
  const context = vm.createContext({ location:{hash:'',search:''}, URLSearchParams,
    document:{addEventListener(){}}, window:{addEventListener(){}},
    Bridge:{on(){},start(){}}, icon:()=>'', setInterval(){}, setTimeout(){}, clearTimeout(){}, Date });
  for (const f of scripts) vm.runInContext(read(f), context, { filename: f });
  const shared = vm.runInContext('ui', context), fn = (name) => vm.runInContext(name, context);
  shared.S = state;
  return { replayControls: fn('replayControls'), streamingScreen: fn('streaming'), homeScreen: fn('home'), streamControls: fn('liveSessionControl'),
    gameStreamActions: fn('gameStreamActions'), crashDiagnosticsCard: fn('crashDiagnosticsCard'),
    streamDialog: (options={}) => { shared.dialog = {kind:'livestream', ...options}; return fn('dialogHtml')(); } };
}
test('private livestream section remains visible with no game and explains blocked start',()=>{
  const app=ui({livestream:{active:false,canStart:false,reason:'Join a game first.'}});
  const html=app.streamControls();
  assert.match(html,/Private livestream/); assert.match(html,/Prepare your invite now/);
  assert.doesNotMatch(html,/data-k="get-livestream-link"[^>]*disabled/); assert.match(html,/normal uploads wait until after the game/);
  assert.match(html,/Anyone with the link can watch/); assert.match(html,/Resource protection/);
});
test('current game offers creation with consent and never starts streaming from a state refresh',()=>{
  const app=ui({current:{context:'now'},livestream:{active:false,canStart:true}});
  assert.match(app.gameStreamActions({context:'now'}),/data-dialog="livestream"/);
  assert.doesNotMatch(app.gameStreamActions({context:'now'}),/startLivestream/);
  assert.equal(app.gameStreamActions({context:'old'}),'');
});
test('only the current game exposes an existing stream link, with copy and open controls',()=>{
  const app=ui({current:{context:'now'},livestream:{active:true,url:'https://example.test/invite'}});
  const html=app.gameStreamActions({context:'now'});
  assert.match(html,/Copy stream link/); assert.match(html,/Open livestream/);
  assert.match(app.streamControls(),/Disable livestream/);
  assert.equal(app.gameStreamActions({context:'old'}),'');
});
test('pending invite cannot copy a missing URL and ended games cannot reuse it',()=>{
  const state={current:{context:'now'},livestream:{active:true,url:null}};
  const app=ui(state);
  assert.match(app.gameStreamActions({context:'now'}),/Preparing stream link/);
  state.current.ended=true;
  assert.equal(app.gameStreamActions({context:'now'}),'');
});



test('crash diagnostics offers explicit local lookup and report without claiming a cause',()=>{
  const app=ui({});
  const html=app.crashDiagnosticsCard();
  assert.match(html,/Check crash records/); assert.match(html,/checkCrashRecords/);
  assert.match(html,/Save diagnostic report/); assert.match(html,/Reports stay on this PC/);
  assert.match(html,/do not establish the cause/);
});
test('crash evidence escapes fields and shows unavailable evidence without inventing faults',()=>{
  const app=ui({settings:{diagnostics:true},crashDiagnostics:{busy:true,status:'Windows crash records unavailable',checkedAt:0,crashes:[]}});
  assert.match(app.crashDiagnosticsCard(),/Checking…/); assert.match(app.crashDiagnosticsCard(),/disabled/);
  const evidence=ui({settings:{diagnostics:true},crashDiagnostics:{checkedAt:0,status:'Matching events',crashes:[{at:0,application:'<script>',module:'x.dll',exceptionCode:'0xc0000005'}]}}).crashDiagnosticsCard();
  assert.match(evidence,/&lt;script&gt;/); assert.doesNotMatch(evidence,/<script>/);
  assert.match(evidence,/x.dll/); assert.match(evidence,/0xc0000005/);
});


test('diagnostic collection is off by default and crash lookup is disabled until enabled',()=>{
  const html=ui({}).crashDiagnosticsCard();
  assert.match(html,/Collect diagnostic history/);
  assert.match(html,/Off — no new diagnostic history/);
  assert.match(html,/data-set="diagnostics"/);
  assert.match(html,/Resource protection still runs/);
  assert.match(html,/data-cmd="checkCrashRecords"[^>]*disabled/);
});


test('idle Enable opens setup and prepares a timed player invite before a game exists',()=>{
  const app=ui({livestream:{needsSetup:true},settings:{recording:false}});
  assert.doesNotMatch(app.streamControls(),/data-k="get-livestream-link"[^>]*disabled/);
  const html=app.streamDialog();
  assert.match(html,/Enable settings &amp; prepare link|Enable settings & prepare link/);
  assert.match(html,/automatic recording and experimental player-action logs/);
  assert.match(html,/Steam can stay open/);
  assert.doesNotMatch(html,/This game only|data-stream-scope/); assert.match(html,/Follow me/);
  assert.match(html,/24 hours/);
  assert.doesNotMatch(html,/data-k="stream-confirm" disabled/);
});
test('an active-game safety reason explains a blocked confirmation while leaving setup openable',()=>{
  const app=ui({current:{context:'game'},livestream:{setupReason:'Close Civ V first.'}});
  assert.match(app.streamDialog(),/Close Civ V first/);
  assert.match(app.streamDialog(),/data-k="stream-confirm" disabled/);
});

test('follow link remains preparable for a future game when current backlog blocks streaming',()=>{
  const app=ui({current:{context:'game'},livestream:{needsSetup:false,setupReason:'Unsent backlog',followSetupReason:null}});
  const html=app.streamDialog({startCurrentSave:false});
  assert.match(html,/Prepare future-game link/);
  assert.match(html,/current recording and its backlog stay excluded/);
  assert.doesNotMatch(html,/data-k="stream-confirm" disabled/);
});

test('future invite still cannot turn on missing capture settings during a game',()=>{
  const html=ui({current:{context:'game'},livestream:{needsSetup:true,setupReason:'Close Civ',followSetupReason:'Close Civ before enabling capture'}}).streamDialog();
  assert.match(html,/Close Civ before enabling capture/);
  assert.match(html,/data-k="stream-confirm" disabled/);
});

 test('current-save activation offers a baseline without releasing older unsent saves',()=>{
  const app=ui({current:{context:'game'},livestream:{active:true,waitingForGame:true,currentSaveReason:null,setupReason:'Backlog',followSetupReason:null}});
  assert.match(app.streamControls(),/Stream current save/);
  assert.match(app.gameStreamActions({context:'game'}),/Stream current save/);
  const html=app.streamDialog();
  assert.match(html,/Start livestream from current save/);
  assert.match(html,/Previously uploaded data remains available/);
  assert.doesNotMatch(html,/data-k="stream-confirm" disabled/);
});

 test('Streaming screen shares the current game and keeps warnings above controls',()=>{
  const app=ui({status:{kind:'waiting'},settings:{},checks:[{level:'warn',title:'Unsupported version'}],current:{context:'now',turn:48,mode:'Multiplayer',mod:'Vanilla',players:[],upload:{state:'held'},paused:false},livestream:{active:false}});
  const html=app.streamingScreen();
  assert.match(html,/Ongoing game/);assert.match(html,/>48</);
  assert.match(html,/Transport status/); assert.match(html,/Transfer counters/); assert.match(html,/Stream protocol/);
  assert.match(html,/More viewers do not add uploads/);
  assert.ok(html.indexOf('Unsupported version')<html.indexOf('Enable livestream'));
  const home=app.homeScreen();
  assert.ok(home.indexOf('Unsupported version')<home.indexOf('Recording options'));
});

test('technical stream status shows measured counters and server rejection without inventing missing readings',()=>{
  const html=ui({livestream:{technical:{server:'https://example.test/api/',lastError:'HTTP 410: expired <channel>',fullSaves:2,deltaSaves:7,saveBytes:2048,controlAllowed:true,payloadAllowed:false,slot:'unknown',eventChunkBytes:262144}}}).streamingScreen();
  assert.match(html,/HTTP 410: expired &lt;channel&gt;/);
  assert.match(html,/Acknowledged save data/);assert.match(html,/2 KB/);
  assert.match(html,/Delta saves sent<\/dt><dd>7/);
  assert.match(html,/Recorder CPU sample<\/dt><dd>–/);
  assert.match(html,/Counters reset when the sender restarts/);
});

test('short active game can finish directly and access/deletion controls are collapsed',()=>{
 const app=ui({current:{context:'game'}});
 const html=app.replayControls({id:'g',context:'game',turn:37,status:'recording',replay:{state:'recording',private:true}});
 assert.match(html,/Mark as done/);
 assert.doesNotMatch(html,/disabled|Store this game/);
 assert.match(html,/stops this recording and keeps it/);
 assert.match(html,/<details class="replay-settings"/);
 assert.doesNotMatch(html,/<details[^>]* open/);
 const options=html.indexOf('<details');
 assert.ok(html.indexOf('Mark as done')<options);
 assert.ok(html.indexOf('Make public')>options);
 assert.ok(html.indexOf('Restrict viewing')>options);
 assert.ok(html.indexOf('Delete recording')>options);
 const menu=app.replayControls({id:'g',context:'game',turn:37,ended:true,replay:{state:'recording',private:true}});
 assert.match(menu,/Mark as done/);assert.doesNotMatch(menu,/disabled/);
});
test('finish queue is visible without claiming completion or public access',()=>{
 const html=ui({}).replayControls({id:'g',context:'game',turn:37,replay:{state:'recording',private:true,completionQueued:true,keepShortRequested:true}});
 assert.match(html,/Finish requested/);assert.match(html,/Private: hidden/);
 assert.doesNotMatch(html,/Mark as done|Deleted |Replay generated and stored/);
 const local=ui({}).replayControls({id:'g',context:'game',turn:37});
 assert.match(local,/Mark as done/);assert.doesNotMatch(local,/Make public|Restrict viewing/);
});
