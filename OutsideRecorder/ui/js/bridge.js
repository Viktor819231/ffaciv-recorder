// The one door between the window and the recorder (docs/spec/OR02-recorder-ui.md "The contract").
//
// Program → window (chrome.webview.postMessage, JSON):
//   { type: 'state', state }   the whole state, whenever it changed (LiveBackend.BuildState; shape: mock.js base(); null = not known)
//   { type: 'result', cmd, dialog?, toast? }   the answer to a command (e.g. the autosave fix's changes, a toast to show)
//   { type: 'command', cmd, args }   a button pressed in a pop-up (the main window runs it like its own button)
// Window → program (LiveBackend.Handle / HandleUx; the window frame itself: ShellForm):
//   { cmd, args }   setup and options: setSetting, setMemoryLogging, setCaptureMethod, setUploadFormat, simpleSetup, finishFirstRun
//                   recording: pauseGame, dontRecord, recordAnyway, fixAutosave, refreshLobby
//                   livestream: startLivestream, stopLivestream, copyLiveLink
//                   a recording: uploadGameNow, postponeUpload, confirmGameFinished, continueGame, completeGame, keepShortGame,
//                     replayChoice, deleteGame, undoDelete, showSaves
//                   old games: scanOldGames, addOldFolder, uploadOldGames, setOldGamesUpload, stopOldUpload, dismissOldHint
//                   the rest: checkCrashRecords, sendReport, checkUpdates, updateNow, updateLater, openSite, openFolder,
//                     chooseFolder, editSettings
//                   the window frame, no answer: ready, showWindow, hideWindow, showOptions, reviewDelete
//   { cmd: 'ui.tray', args: { kind, tip } } and { cmd: 'ui.toast', args: toast }   preview only: the made-up state's
//                   tray icon and pop-ups (the real program makes these itself from its own state)
//
// Without the program (a plain browser, the pictures) or with ?demo, the window runs on mock.js.
'use strict';
const Bridge = (() => {
  const params = new URLSearchParams(location.search);
  const web = window.chrome?.webview;
  const demo = !web || params.has('demo');
  const listeners = [];
  const emit = (msg) => listeners.forEach((f) => f(msg));

  if (web) web.addEventListener('message', (e) => {
    const msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
    if (msg?.type === 'command') { send(msg.cmd, msg.args); return; }
    if (!demo) emit(msg);
  });

  function send(cmd, args = {}) {
    if (!demo) { web.postMessage({ cmd, args }); return; }
    if (['showWindow', 'hideWindow'].includes(cmd)) { web?.postMessage({ cmd, args }); return; }
    const out = Mock.handle(cmd, args);
    emit({ type: 'state', state: Mock.state });
    emit({ type: 'result', cmd, ...out });
    if (out.toast) toHost('ui.toast', out.toast);
  }
  function toHost(cmd, args) { if (demo && web) web.postMessage({ cmd, args }); }

  function start() {
    if (demo) {
      Mock.load(params.get('scenario') ?? 'replaycontrols');
      emit({ type: 'state', state: Mock.state });
    } else web.postMessage({ cmd: 'ready', args: {} });
  }
  function scenario(name) { Mock.load(name); emit({ type: 'state', state: Mock.state }); }

  return { demo, on: (f) => listeners.push(f), send, start, scenario, toHost };
})();
