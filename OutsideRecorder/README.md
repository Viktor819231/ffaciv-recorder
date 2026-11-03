# FFACiv Recorder

## 0.7.24 — open source on GitHub

The recorder's code is public at github.com/Viktor819231/ffaciv-recorder so players can see what they install (the
rest of this project stays private). `OutsideRecorder/public-repo/export.sh <clone>` copies this folder there with the
public README, MIT licence and `.github/workflows/release.yml`, leaves out AGENTS.md, installer/RELEASE.md and
tools/ui-shots.sh, and refuses on personal data (this PC's paths, e-mail, real Steam ids). Pushing a `v<version>` tag
there makes GitHub run the tests, build the installer (`build-installer.sh --github`: unsigned, plain file name) and
publish it with its SHA-256 and a build attestation. Tests that use the developer's recordings find them through
`TestData` (`RECORDER_TEST_DATA` or a `test saves` folder above the binaries) and skip elsewhere. No program change
from 0.7.23; READ-ME.txt / PRIVACY.txt now describe 0.7.23's upload timing.

## 0.7.19 — Saves list, undo for deleted replays, turn ranges

Every recording's controls show a Saves block: how many saves are on the site, how many wait on this PC and the one
reason they wait (setup, automatic uploads off, upload timing "After Civ closes", resource pause, turn settling, no
connection, livestream access or slot, a per-game hold), and any save the site refused with its HTTP status and words
(kept in memory per file, `Streamer.RefusalOf`). Each save, newest first, under "Each save"; with the website's
recording state listing `files`, each also shows what the site made of it. The two newest recordings carry their list;
older ones load it with Show saves. Recordings show "T30–144" (first–last saved turn) and how many turns in that range
have no save.

Delete is held 7 days (`Streamer.DeletionHold`): the replay is made private on the site at once and hidden locally; the
real withdrawal and the local copy's removal happen when the hold ends. Undo (`undoDelete`) restores the game's
identity, its upload choice and its earlier visibility. Home keeps two recordings; a deleted one stays there as an Undo
card, so an older recording never moves up. The Games tab is now Previous games, with Recently deleted at the end. UI28.

## 0.7.18 — send to ffaciv.com

The installer moves an install that a test build pointed at ffaciv-test.pages.dev to ffaciv.com: it empties the kept
settings.txt's `stream_url` and `live_site_url` lines when they name the test site (empty = ffaciv.com). Every other
setting, the recordings folder and the queue stay. No recorder code change from 0.7.17.

## 0.7.17 — finish recordings and drain uploads after play

Mark as done works at any turn, including from the main menu. It explicitly keeps short games and stops that recording without changing visibility. Pending completion is shown until the server acknowledges it. Replay settings now collapses storage, visibility, viewing restrictions and deletion; expanded settings survive background state refreshes.

An enabled follow invite no longer blocks trailing authorized saves or an idle finished recording with normal Upload now permission. The 40-of-41 resync case is covered without resending acknowledged files. Confirmed menus end capture using an actual successful read of the existing game-started flag as false; loading/unknown/stale observations do not grant menu permission. Menu proof expires after 2.5 seconds and loaded headers clear it. Missing memory readers require Civ to close.

After a five-second closed/menu grace, approved uploads can proceed. Idle mode permits four times the configured transfer rate (boost capped at 4 MiB/s, never reducing a higher configured rate; zero stays unpaced), up to four saves/sixteen log chunks per private pass, and a 10% recorder CPU scheduling budget instead of 2%. One worker, system-load/low-memory gates and explicit hard CPU ceilings remain. Gameplay returns to one save/four chunks and the normal CPU budget. Current limits/mode are shown in Streaming. The ordinary two-hour schedule, local/postponed choices, privacy and diagnostics preferences are preserved.

Validation: 59 focused recorder tests and sixteen UI checks passed; enabled turn-37/menu completion, collapsed controls and expanded-state preservation were checked in a real browser. Local player package out/recorder-0717 / UI27; no service deployment or user process restart.

## 0.7.16 — technical streaming status and visible access failures

Streaming now prioritizes live technical details: endpoint, sender/slot state, control and game-payload gates, queue sizes, full/delta save counts, acknowledged data bytes, requests, chunks, last acknowledgement, preparation elapsed times, pass limits and recorder-host resource samples. Missing measurements stay unknown. Counters are labelled as the current sender session and exclude transport overhead. Protocol details replace the larger how-it-works cards; privacy/viewer notes are collapsed. No diagnostic history is enabled by these in-memory counters.

Failed privacy acknowledgement now exposes the HTTP status, including an expired-channel explanation for HTTP 410. The associated website fix excludes owned lobby transport channels from short-game expiry and safely repairs old inactivity expiry during explicit privacy initialization. That server repair must be deployed separately; the local Recorder build alone cannot repair server state. Local package out/recorder-0716 / UI26.

## 0.7.15 — Streaming tab and visible setup warnings

Streaming now has its own sidebar tab with follow-link controls, the same ongoing game/lobby card as Recorder, and explanations of invite expiry, game-data playback, privacy, current-save activation and resource/upload behavior. New invites use Follow me only; the setup no longer offers This game only. Recorder keeps an Open Streaming shortcut, and current-game actions remain available. The setup-status card now precedes Recording options; Streaming shows active warnings above its controls. UI25.

Thirteen UI render tests and browser dialog-refresh/confirmation checks passed. Wide and narrow Streaming layouts, current-game reuse, sidebar navigation and top-of-options warning placement were rendered and inspected. Self-contained local package out/recorder-0715; no website deployment or capture-policy change.

## 0.7.14 — start streaming from the current save

During an already-prepared recording, explicit Start from current save selects the latest settled staged copy, sends it whole if it has not already been acknowledged, then continues with new saves using the chosen upload method. Older unsent copies are excluded from this private session without being deleted or marked sent; existing server data, event offsets and save receipts remain intact. Logs retain their existing bounded, resumable upload protocol. Missing capture settings, paused recordings and unreadable upload progress still block activation. Resource/turn quiet, private acknowledgement and live-slot gates remain.

A waiting player invite exposes Stream current save in Recorder and the current Games card. Confirmation updates that existing invite without replacing its URL or expiry. Disabling sharing leaves ordinary upload preferences unchanged; their normal rules still apply to retained copies. New recording contexts retain the existing consent checks. UI24. Local build: out/recorder-0714; no service deployment.

## 0.7.13 — separate invite preparation from current-game streaming

Prepared private capture no longer requires normal automatic uploads or normal while-playing upload timing. The private stream retains its own explicit permission and bounded work limits. An existing backlog still blocks this-game streaming; Follow me can prepare a future-game link during play if capture is already configured, explicitly excluding all existing contexts even if their backlog later shrinks. Missing capture settings still require Civ closed or a recognized lobby. UI23 distinguishes the two choices and labels future-only confirmation.

## 0.7.12 — stable streaming setup dialog

Unrelated status updates preserve the existing overlay DOM instead of rebuilding it and restarting its fade. The expiry dropdown retains its native control, selection and focus; dialog scroll is retained when safety information actually changes. Focus fallback no longer scrolls the dialog on opening. Browser regression covers twenty status updates, expiry selection, focus/scroll/position, safety updates, Cancel and Escape at normal and short window heights. UI22.

## 0.7.11 — notification crash repair

WebView2 environments are cached per owning STA/UI thread, with a separate notification browser profile. Popups initialize only after Show has finished; closing or replacing a popup during startup cancels quietly, and notification callbacks check for disposed windows. Posted creation/message failures are contained and logged instead of escaping the notification loop. Main-window dispatch also tolerates shutdown. The native smoke harness now warms the main WebView before notification creation, tests a blocked main UI, timeout without web timer messages, a deliberately hung notification renderer, continued main-renderer responsiveness, and rapid close/replacement during initialization. No capture/upload or diagnostics preference changes.


## 0.7.10 — pre-game streaming setup

Enable livestream always opens setup, including before Civ starts. Explicit confirmation can enable automatic recording and experimental action logs; normal upload timing, automatic upload preference, upload format and diagnostics are preserved. An idle/lobby player invite uses the existing private channel API, so its timed link can be copied before the first game or save. Each game still requires private acknowledgement and slot/share binding before payloads send. Disabling stops only the invite.

Enabling new memory capture, delta preparation or while-playing sending is refused while Civ is open outside a recognized lobby. Starting a stream in an active game requires already-configured capture/while-playing uploads and at most two unsent saves. Unknown progress fails closed. A pre-game invite cannot release an old recording backlog; new recording contexts from the prepared game may stream incrementally. All sender preparation shares one work lease; private streams send at most one save or four event chunks per pass, retaining progress for later passes. No Steam shutdown is required. Resource and turn-transition protections remain. This is scheduling protection, not a guarantee against game crashes. UI21.


## 0.7.9 — optional diagnostic history

Collect diagnostic history is now an explicit, persistent switch in Recorder, off by default. Disabled collection does not record new diagnostic resource/activity/upload history, write buffered history on shutdown, or query Windows crash evidence for diagnostic reports. Earlier diagnostic files remain on this PC. Lightweight resource sampling and workload protection continue independently. Enable collection before reproducing an incident if you want historical evidence. UI20.


## 0.7.8 — crash records and diagnostic reports

Recorder now has a permanent Crash reports & diagnostics card. Check crash records performs a bounded local Windows Civ Application Error lookup for the last 24 hours on a background thread; repeated clicks do not start parallel lookups. Results show time, application, fault module and exception code when available, with an explicit unknown-cause message. Save diagnostic report reuses the existing local zip export with selected resource/history/Windows evidence and user notes; nothing is uploaded automatically. Earlier sessions retained by the capped history can help after restarting Recorder. UI19.

Follow me retains its existing 1/4/8/24-hour choices and link through Civ crash/rejoin while Recorder remains running, until the original expiry. A Recorder restart still requires a new explicit Start. The proposed next-real-game threshold is deferred at the owner’s request.


## 0.7.7 — dismissible notifications and stream controls

Recording options permanently includes Private livestream controls and concise privacy, upload-timing and resource-safety information. The current game card in Recorder and Games offers Get livestream link, or Open/Copy stream link while an invite is active; historical games never borrow the current player link. The permanent section uses Enable livestream / Disable livestream labels; enable still opens the explicit confirmation. UI18.

Corner notifications now run on a separate STA UI thread. A native close button and bounded native expiry remain responsive if the main Recorder UI is busy or the popup JavaScript stops responding. Actions dismiss first, then dispatch to the main window. Closing notifications does not stop recording or change sharing. The native regression harness is `./dn.sh run --project tools/recorder-toast-check` (no backend, capture or network uploads).


## 0.7.6 - Restore upload timing

Options and setup again offer while-playing upload/processing or after-Civ
upload timing. The choice controls both server contact and the current game's
upload gate; automatic uploads do not release explicit keep-local/postponed
recordings or bypass private livestream consent. Waiting for Civ to close is
shown as Waiting / Upload timing rather than Off / Sent up to. Local test
packages use UI16; public recorder downloads remain hidden.

## 0.7.5 - Lobby invitations and refresh

The Recorder heading now distinguishes a detected Civ process and multiplayer
lobby from waiting for Civ to start. Lobby cards put **Refresh lobby** and **Get
livestream link** beside their player list; an active invite has Copy and Stop.
Refresh respects existing read permissions and resource pauses. Player-scope
invitations can be made before the first game; viewers wait in the lobby and the
same token follows into a separately authorized private game. A pending lobby
invite never grants a gameplay slot or another player's game data. Matching
website API and migration 0021 are required. Public recorder downloads/setup
pages remain withdrawn; local installer only. See OR10 for release checks.

## Release packaging and publisher trust

`installer/build-installer.sh --scan` prepares a local `UNSIGNED-TEST` installer,
bundled getting-started/privacy/release notes, exact dependency licenses/notices,
SHA-256 checksums, a full app-file inventory and Defender results in `out/or04`.
`--release` additionally requires the owner's verified signing certificate and
Windows SDK signing tools; it signs the app, Setup, temporary Setup copies and
uninstaller, then requires signature/timestamp verification and Defender scans.
Unsigned builds are always marked `releaseReady: false`.

Installer builds exclude developer memory-dump and stream/site-test types/CLI
commands and ignore loose UI override folders. Developer publishes retain those
tools. Update announcements only link to HTTPS on ffaciv.com/www.ffaciv.com.
No publishing or installation is performed. See
[the release procedure](installer/RELEASE.md) for commands, remaining owner setup,
false-positive reporting and clean-PC/real-game checks. The signing identity is
not configured yet; no claim of antivirus or SmartScreen immunity is made.


## 0.7.4 — simpler Recorder and timed follow links

Recorder has global on/off, quick session choices and a separate Manual upload
tab. Waiting for Civ stays the idle heading; resource details live in Checks.
Deleted cards disappear immediately even while background work is paused. Setup
explains local staging, recommends optional experimental action logs, separates
upload timing from format and lets every review choice be changed.

Livestreaming is started only on Recorder during a supported recording. Get
livestream link offers a fixed game invite or Follow me for 1/4/8/24 hours. A player
invite follows this recorder through lobbies, rehosts and new games. Viewers choose
Open new game or Stay here. Rejoining the same game does not force a switch. Invites
are unlisted, server-expiring and revocable; each new recording first confirms
private access. Stopping or memory-off blocks new live data, and revocation retries.
Sharing does not automatically resume when the program restarts.

This website feature requires migration 0021_live_shares.sql and the matching
Functions/web deployment. Neither has been applied to production here. Older
servers show a clear unavailable message instead of issuing an unsafe fallback.
Whole saves still support automatic recording and memory logs; local save-change
preparation remains recommended for smaller uploads. Size examples are illustrative.

New portable app: `out/recorder-074/app/FFACivRecorder.exe`. Close the older recorder
before launching it. Nothing was installed or deployed. See OR09 for validation.
## 0.7.3 — recording choices and setup review

Setup now has seven short pages and ends with a summary of all choices. Memory
logs have their own optional page, with external read-only observation explained
briefly. The restored experiment defaults to 4 movement reads/second, city
management every 1 second and a 5-second pause in detailed reads after an observed
turn change. It cannot anticipate a transition. Save-only remains the default.

Choose recommended local preparation for smaller save-change uploads, or send
whole saves for site processing. Both generate the final replay on the website;
the 200 MB → 15 MB example is illustrative. Manual uploads expose upload/parse/
preview progress and only show a replay link after confirmation. Private streaming
still needs an explicit Start in Options and server support; game data waits for
private-access acknowledgement. Resource controls and local diagnostics remain.

New portable app: `out/recorder-073/app/FFACivRecorder.exe`. Existing programs,
recordings and installed files are untouched. Reopen setup through Options. The
new onboarding revision also appears once for existing users. No deployment.

The following 0.7.2 notes describe the previous build; its enforced save-only
restriction is superseded by the explicit experimental opt-in in 0.7.3.
## 0.7.2 — combined UX and resource-controls test build

Setup now puts the choices first: short questions, large selectable cards and a
clear selected state. How-it-works explanations remain available on demand;
technical details and the resource screenshot are collapsed by default. UI
cache version 10; recording/upload preferences and safety controls are unchanged.
Memory logs have a brief visible description beside the recording choices. What
gets uploaded explains saves/metadata, read-only capture and privacy, with an
explicitly illustrative 200 MB → 15 MB delta-mode example. Production still sends
whole saves; the example is neither a measurement nor a promise of current size.

The authoritative combined source is the `recorder-ux` worktree. It retains the
latest five-step method guide, outside-reader explanation, Task Manager example,
minimal Home/Options, action feedback, deletion fixes and startup recovery.
Resource controls are integrated into capture, upload and manual-upload work.
Checks shows host CPU/memory and resource pauses; diagnostic reports are local
exports with explicit contents and no authentication settings. Automatic uploads
off still permits explicit manual uploads. Production uploads obey resource/idle
gates and wait for pending save copies.

SafeCapture remains enforced: memory observation and livestreaming are unavailable
in this desktop build. No installed program, user preference or public service is
replaced by building it. See [combined validation](../docs/spec/OR09-recorder-resource-safety.md).

## Recorder UX update — local build

The introduction now highlights external read-only observations (separate process,
no injection or game-memory writes) and set-up-once tray operation. An expandable
resource-use example includes the supplied Task Manager screenshot, clearly marked
as one observation rather than a performance guarantee. No benchmark was run.

The WebView2 startup fix shares in-flight environment creation, retries transient
controller aborts twice, and cancels safely when a notification closes during
initialization. Persistent main-window failures offer a native Try again/Quit
screen; diagnostics go to `app/display-errors.log`. Demo/screenshot runs use a
separate browser profile. Recordings and settings are never reset for recovery.

Home shows recording status, Start/Stop recording, Options, and recent games.
The introduction now explains recording methods in five steps: welcome, capture,
uploads, autosaves, and ready. Compare automatic save copies, experimental memory
observations, and manually kept saves; then choose automatic after-game uploads
or manual sharing. The guide explains during-play private streaming separately,
including what memory sampling enables and the limits of this save-only build.
Older first-run flags no longer skip the new guide: versioned setup opens once
without resetting existing preferences. Capture/sending wait until Finish setup.
Reopen it from Home, Options, or the tray's Recording methods & setup entry.
Older unindexed session folders can now be deleted locally without requiring a
server recording ID; only the recorder's own validated session folders qualify.
Options separates saving on this PC from automatic uploads; stopping capture
does not cancel an existing game's upload choice. Get started walks through
sharing preferences, Civ's autosave setup, and tray operation. Game actions show
pending/result feedback inside the app, including queued and failed changes.

After capture ends, a persistent per-recording choice offers upload now or a
two-hour countdown. Postpone by two hours/a day or keep the game local until
explicit upload. A five-minute warning offers postpone or a confirmed delete.
Restarting after an unwarned deadline grants five fresh minutes. The recorder
must be running to show warnings and upload. Uploads-off holds all automatic
sending. An in-flight request may finish. Original Civ saves remain untouched.

Private livestreams require an explicit Start in Options, compatible movement
capture, uploads while playing, a connected server, and a granted live slot.
The server confirms an unlisted recording before any stream data leaves this
PC. The lobby card shows Preparing/Ready, Open stream lobby and Copy invite link;
anyone with the invite can watch. Streams never start from a tray/recording
notification. Ending capture releases the live slot and offers finish/make public.

The desktop build **still enforces save-only capture**: experimental memory
recording and livestreaming remain unavailable. It detects game completion when
Civ closes; main-menu detection requires the experimental reader. Read-only
memory access avoids writing/injecting into Civ but is not a guarantee against
crashes. Viewer-join notifications are not available because the server does not
track viewer presence. No installed program or hosted service is replaced by
these source changes. Private-stream preflight requires the accompanying server
update, with no migration.

Validation: recorder core suite, Shell build/publish, recorder lifecycle/upload
HTTP tests, and targeted Edge/WebView2 screenshots/interactions. See
[OR09](../docs/spec/OR09-recorder-ux.md) for current state.

## 0.7.1 — resource controls and local health reports

The desktop build retains 0.7.0's enforced save-only capture: no game-memory
reads, realtime logs or livestream. Saves must settle for five seconds. All
server work still waits until Civ has stayed closed for 30 seconds.

The recorder runs below normal priority and measures host-process CPU/memory,
system CPU and available physical memory every two seconds. Four seconds of
observed high load (system CPU >=85%, recorder host CPU >=2% of total machine
capacity, or available memory below max(256 MiB,5%)) pauses new copies and heavy
work. Recovery requires eight seconds below the lower thresholds (65% system,
1.5% recorder, memory >=max(512 MiB,8%)). These are adaptive targets, not promises
that brief spikes cannot happen. Unknown counters are shown as unknown; unavailable
recovery counters cannot clear the relevant pause.

Pending copies retain their retry budget during a pause and drain one per poll.
The metadata queue is bounded; uncopied files remain Civ's property and can be
lost if Civ rotates them before copying. Existing local copies remain the durable
upload queue. A small bounded metadata manifest also recovers previously observed
pending sources after a recorder restart, checks that size/time still match and
waits a fresh five seconds before copying. It does not import unrelated existing
files. Manifest writes are coalesced for ten seconds; an abrupt stop can lose the
newest pending observations. The save-only release serializes automatic/manual upload batches;
body writes are capped to 32 KiB chunks and a shared 256 KiB/s budget. A newly
busy game can interrupt body sending; unacknowledged files remain queued. A worker
that ignores its timeout is held for a recorder restart rather than duplicated.

Advanced settings in `settings.txt`: `upload_kib_per_second = 256` (0 disables
pacing), `cpu_limit_percent = 0` (0 means adaptive controls only; 1–100 requests
an optional Windows job-object CPU ceiling for the recorder host). Child processes
break away: window helpers and external programs opened by the recorder are not
capped. Failure is shown in Checks. No game process is assigned to that job.
Legacy developer realtime mode retains independent event/save senders.

Checks shows resource samples and **Save diagnostic report**. Reports contain
sampled averages/maxima, peak memory, upload body totals/rates including retries,
request/failure/defer counters, bounded recent activity and available matching
Windows crash events. CPU/memory figures cover the recorder host, excluding
separate WebView2 helpers. Upload figures exclude HTTP/TLS overhead and are not
proof of server receipt. Local history is capped at two 1 MiB files, buffered for
15 seconds; recent prior-session evidence survives a recorder restart. Crash
lookup occurs only on explicit export. No dumps, registry changes, admin access,
authentication settings or automatic diagnostic uploads are introduced.

No verified transition-start/end signal exists in the current supported layouts;
autosaves occur at different phases by mod. We do not guess offsets or enable a
save-triggered memory reader. Optional developer memory capture obeys resource
pauses, settles and ramps afterward, with a fresh baseline and explicit gap.
Crash causality remains undetermined. See [OR09](../docs/spec/OR09-recorder-resource-safety.md).

## 0.7.0 — conservative save-file capture

After repeated reported Civ crashes, the desktop host now enforces save-only
capture and uploads after Civ closes. Memory reading, raw tapes, realtime logs and
livestream capture are disabled regardless of old settings. Autosaves must remain
unchanged for five seconds before a bounded shared-read copy. All server work waits
for 30 seconds of observed idle after Civ exits; relaunching resets the wait. Local
copies are retained. See [OR08](../docs/spec/OR08-safe-save-capture.md). Crash
causality has not been established; this is a conservative test configuration.


## 0.6.0 — public release preparation

The normal Windows launch uses the real recorder and defaults to
`https://ffaciv.com/replay/api/uploader/`; `--demo` is explicitly example data.
Existing custom/test server settings are preserved. The public build hides
unfinished account/watch-request controls and checks the website's lifecycle
capability before uploading.

Recent game cards have **Delete**, with confirmation. Deletion withdraws this
PC's contribution, removes its recorder copy, survives offline retries, and
prevents that identified game from being recorded again after restart. Other
players' retained copies and Civ's original saves are preserved.

**Back up unsupported builds** is on by default: autosaves are copied byte for
byte into `unsupported-*/saves`, with `diagnostics.jsonl` and `backup.json`.
No movement events are guessed and these folders are not automatically uploaded
or expired as short games. Manual upload includes the backup folders, so players
can generate replays after website support is fixed. KEK saves are excluded:
Oblast supplies those games. Turn off the recorder or unsupported backup to stop
collecting new copies.

New-release checks run on startup and hourly while the recorder runs, including
in the tray. Notifications can be turned off or postponed. **Download update**
opens the configured HTTPS release page; it does not install anything. Website
operators announce a release with `UPLOADER_LATEST_VERSION`,
`UPLOADER_RELEASE_NOTES`, and `UPLOADER_DOWNLOAD_URL`; raise
`UPLOADER_MIN_VERSION` only when older clients must stop uploading.

Deploy both `0019_recorder_lifecycle.sql` and `0020_recorder_deletions.sql` with
the website before shipping. The download page only exposes a published release
of 0.6 or newer through the existing opt-in download gate. The local installer
and manifest are staged in `out/recorder-release/`; no release has been published
by this implementation.

## 0.5.1 — replay controls

Home has a recorder on/off button, manual upload and the two most recent games.
Manual upload selects whole games from a folder; **Generate and store replay**
explicitly keeps them, shows uploaded/processed saves, then links to each game
page after packaging. Games offers private (unlisted) and fourteen-day replay
restriction (listed page, locked replay) choices; either can be changed later.

Mark a recording done, or let one hour without a received save or log finish it.
Games below turn 50 are deleted after that hour unless **Store this game** was
chosen. Original Civ save files are untouched. In Settings, turn off **Record
realtime moves** for savefiles plus basic session details without movement logs;
realtime playback and live view need it on.

The website must include migration `0019_recorder_lifecycle.sql` and the OR06 API
before running 0.5.1 against it. See [OR06](../docs/spec/OR06-replay-controls.md)
for the integration state, contract and local checks. No deployment is implied
by building the recorder. `--demo` and `?demo=1&scenario=replaycontrols` show
example data without recording or uploading a game.

## Older core/developer reference

## City micro (2026-10-28)

City management is sampled **once per second**, independently of units (50 ms), city basics (200 ms) and tiles
(1000 ms). The turn-settle read does not trigger extra micro scans. Each supported hash-matched BNW, LEKMOD and
Snowve layout has verified offsets in `layouts/evidence-city-micro.txt`; mod offsets are also checked against their PDBs.

`city_micro_baseline` establishes each city's first observed state; `city_micro_changed` writes only changed fields
as `[before, after]`: focus, avoid-growth and manual-specialist settings, worked/forced city plots, default specialists,
specialist counts by type/building, forced specialists by building, production queue, food in hundredths, and raw stored
production overflow. Plot arrays use the game's city-relative indices 0–36, specialist/building arrays use database IDs,
and queue entries retain order type, both data values and the repeat flag. Overflow is stored raw without guessing units.
Current-item production progress and yield breakdowns are not included.

Turn snapshots include `cityMicro` with each observation's own `readAtMs`; these values may be up to one sampling interval
older than the turn snapshot. Unreadable optional fields are null. Loss/recovery of a field is recorded explicitly, and a
failed micro read does not discard city health/population. Reloads reset the micro baseline; quick changes reversed between
samples can be missed. This is state sampling, not a click log. The raw events use the existing upload path. The live feed
and saved-replay importer now retain this state for the shared realtime viewer's city panel, following pause and rewind.
Only observers and the owning team receive city internals. Existing logs without these observations remain unknown.
New diagnostic tapes use reads version 3; versions 1/2 keep their original collection behavior.

This webport-based workspace includes the recorder baseline from `outside-recorder` at `4f95e317`, since that standalone
project was not yet on webport. The installed recorder is not replaced by merging these source changes.

Records a Civilization V game **from outside the game**: stock Brave New World 1.0.3.279 (no mods), LEKMOD v35.4.003 and Snowve 8.1.1, 8.2.0, 8.2.1, 8.3.0
(each by its DLL's SHA-256; Snowve's in-game version text always says 8.0.1).
It reads Civ's memory read-only a few times a second and notes each new autosave. The reader stays outside Civ: it never writes to
Civ, inject code or pause it; this does not establish crash-free operation. Nothing is sent unless `stream` is switched on (off by default; `local` = a test
server on this PC). Self-contained (no link to the save parser).
Research and results: [docs/research/OUTSIDE-RECORDER.md](../docs/research/OUTSIDE-RECORDER.md).

## Use

Start `FFACivOutsideRecorder.exe` (Desktop: "Outside Recorder.cmd"), then play. Close its window to stop.
Recordings: `%LOCALAPPDATA%\FFACiv\OutsideRecorder\session-<time>-<pid>\` with `events.jsonl`, `tape.bin.gz`, `saves\` (a copy of
each autosave) and `stream.json` (how far the stream got). `games.json` maps each game (its seeds) to its folder, so the same
game coming back (reload, rejoin, Civ or recorder restarted) continues its folder and its stream.

**Stream** (`settings.txt`): `stream = off | local | site`, `stream_url`, `stream_interval_s = 3`, `save_upload = full | delta`,
`full_save_every = 10`. Events go in gzip chunks to the site's recorder chunk API (`POST recordings/<id>/chunks/<offset>`),
saves to `POST saves` (whole, or as an ORSD1 delta: `X-Save-Encoding: orsd1`, `X-Delta-Base`). The files are the queue:
a server outage or a restart just continues. Local test server: `tools/site/pages/tests/dev-stream.mjs` on branch
`outside-stream-site` (Desktop: "Outside Recorder local server.cmd", then "Outside Recorder (stream local).cmd").
Settings: `settings.txt` next to the exe (made on first start; sample time, output folder, pop-ups, tape, autosave folders).

* `--check` prints one look at the game (your own units and cities only) and exits; redirect it: `... --check > check.txt`.
* `--stream local` overrides settings.txt for one run. `--stream-test <recording> <saves> <out>`, `--stream-only <out>`,
  `--stream-verify <out>`: the stream checked end to end without Civ (see `StreamTest.cs`).
* `--replay-tape <session folder>` re-reads a tape and writes `events.replay.jsonl` there (`> replay.txt` for the summary).
* `--ui-shot <prefix> [ms]` saves pictures of the window and the newest pop-up.
* The **Snapshot** button copies Civ's memory (1–2 GB) to `memory-snapshots\` for finding more fields later.

## 0.5.0 — the user build (card OR05)

`stream = site` by default (ffaciv.com, or a test environment's `stream_url`; `local` stays for our tests). The install
registers once through the uploader's install role; its key is kept protected for the Windows user (`KeyStore`, DPAPI).
Defaults: tape off, `keep_copies = off` (copies and finished recordings removed once the server confirmed them), single
player off, 20 looks/s, start with Windows. Problems in plain words (unreachable, daily cap, too old → update, blocked,
switched off). The "busy line" (`LiveSlot.cs`, `Shell/SlotKeeper.cs`): the livestream link needs a live slot; busy →
"Request a livestream"; the recording and upload never wait. Contract: OR05 "The busy line".

## The program: FFACiv Recorder (test build 0.3.1, card OR03 P1)

`FFACivRecorder.exe` (`OutsideRecorder.Shell`) is the recorder for players: one process = the reader core + the stream + the
window (`ui/`, HTML in WebView2, card OR02) + the tray icon (menu: Open, Get livestream link, Pause for this game, Quit) +
corner pop-ups (Civ found / recording / saved / problems; none per turn). Start with Windows is on (per-user Run key,
`--tray`; switch in Settings). Test build: stream = local by default (`settings.txt` next to the exe; the window's
"Live stream to the test server" switch turns it on/off), every recording kept on this PC, "Get livestream link" =
`<live_site_url>live/<game>/watch` (OR03 "Feed"; `live_site_url = http://127.0.0.1:5632/replay/`). Going public, postpone,
watch requests, updates and Steam keep their screens with "Not in the test build". The old `FFACivOutsideRecorder.exe`
window stays as a check/debug tool (`--check`, `--replay-tape`, `--stream-test`).
Published to `out/recorder-test/` (old tool in `check-tool/`); Desktop "2 Start recorder.cmd" starts it.

```bash
./dn.sh publish OutsideRecorder/OutsideRecorder.Shell -c Release -o out/recorder-test
out/recorder-test/FFACivRecorder.exe --ui-shot out/recorder-shots --live   # pictures of the real window (sends nothing)
OutsideRecorder/tools/ui-shots.sh out/recorder-ui-shots                     # every screen on made-up data (scenario testbuild too)
```

Also: **Upload old games** (Games page; `OldGames.cs` finds and groups Civ's autosaves and zips, `OldGameUpload.cs` sends
whole games under one batch, OR03 "Bulk upload"), **Block / Unblock** of finished games (`Shell/Visibility.cs`, OR03
"Visibility"), single-player games off by default (Settings, "Record it anyway"), autosave fix offered when Civ closes.

0.3.8: the multiplayer **lobby** (pre-game: slots, Steam names, civs, teams, map, speed, options, turn timer) goes out on the
player's own channel (`Channel.cs`, OR03 "Lobby / channel"); "Get my channel link" in the lobby; the turn timer in the
recording (`turn_timer`). New offsets: `layouts/evidence-bnw-1.0.3.279-pregame.txt` (BNW), PDB tests (Lekmod, Snowve).

Fixes from the 2026-10-27 tests (`Round4Tests`): the last lines (game_left, stopped, recorder_closed) are sent before the
program ends (`Streamer.Finish`, one last pass); a game rejoined after a Civ/recorder restart at an older turn is marked
`game_reloaded` (`GamePipeline` gets the folder's last turn); after a load the pipeline waits until the game is fully there
(a living player, two samples in a row agree) before the new baseline, so the Snowve T12 reload no longer starts from an
empty sample (cities "founded" again at size 1).

## Installer (card OR04)

`OutsideRecorder/installer/build-installer.sh` → `out/or04/FFACiv-Recorder-Setup-<version>-UNSIGNED-TEST.exe` (~35 MB): the program as a
plain self-contained folder (no .NET needed, no single-file packing; 111 MB installed), Inno Setup 7 used portable from
`out/tools` (downloaded and checked by the script). Per-user, no admin: Start menu, optional desktop shortcut, start with
Windows (opt-in), Apps entry, upgrade in place, uninstall keeps recordings unless asked. Use --release after verified signing is configured.
Details, tests, AOT estimate: [docs/spec/OR04-installer.md](../docs/spec/OR04-installer.md).

## Projects

* `OutsideRecorder.Core` — find Civ (`GameFinder`), read it (`ProcessMemory`, `StateReader`, offsets in `Layouts.cs`),
  turn samples into events (`Differ`, incl. the swap rule; `GamePipeline`, reloads/menus), JSONL (`JsonlRecording`),
  tape (`Tape.cs`, `TapeReplay`), memory copy (`MemoryDump`), autosaves (`AutosaveWatcher`), the loop (`RecorderSession`).
* `OutsideRecorder.App` — the window and pop-ups (WinForms, .NET 10).
* `OutsideRecorder.Tests` — xunit; `EvidenceTests` ties every layout value to `layouts/evidence-*.txt`,
  `LayoutPdbTests` re-checks Lekmod against its PDB, `SnowveLayoutTests` each Snowve release against its PDB (DLL + PDB from the
  Snowve git tag in `out/snowve-bin/<version>/` or `SNOWVE_BIN`; every PDB record of a class must agree), `Round2Tests` covers tape/replay/reloads/drops.
* `layouts/` — the machine-code evidence for each supported game core DLL.
* `tools/BinaryVerify` — developer tool: finds field offsets in a DLL without a PDB (Lua method → getter, RTTI vtables,
  GET_PLAYER, CvGlobals constructor); `verify.sh` writes an evidence file, `verify-extra.sh` (DLLs with a PDB) adds the
  unit/civ name tables. `tools/LayoutDump` — reads a PDB.

## Build (repo root, Git Bash)

```bash
./dn.sh test OutsideRecorder/OutsideRecorder.Tests
./dn.sh publish OutsideRecorder/OutsideRecorder.App -c Release -o out/outside-recorder
```

## Adding another game version

Only with evidence from its own code: build `tools/BinaryVerify`, run
`OutsideRecorder/tools/BinaryVerify/verify.sh <BinaryVerify.exe> <CvGameCore_Expansion2.dll> > OutsideRecorder/layouts/evidence-<id>.txt`,
add a `GameCoreLayout` in `Layouts.cs` (with the DLL's SHA-256 and whether it autosaves at the start or end of a turn),
and add the id to the tests. A field without evidence stays null (not read). Never guess offsets.

## Capture and upload controls (OR07)

Home now has **Automatically keep save files**, **Track logs**, **Parse and upload**
(while playing / store now, upload after), **Allow uploads**, and manual game upload.
Turning automatic saving off is manual-only mode: the recorder does not open Civ's
memory, record realtime logs, copy autosaves or automatically drain old queues.
Keep each turn's save yourself and use Manual upload games.

With automatic saving on and Track logs off, stable autosaves are copied without
memory reads or event/tape logs. The server parses those files later. With
Store now, upload after, automatic uploads wait until Civ closes; leave the
recorder running or reopen it later. Turning Allow uploads off keeps everything
local until it is enabled again. Already-running uploads may finish.
Settings persist in the app; settings.txt defaults are track_logs and upload_timing.
