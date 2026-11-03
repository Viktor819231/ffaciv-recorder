using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>
/// The real recorder behind the window (card OR03 P1, the test build): the reader core's <see cref="RecorderSession"/>
/// (what it sees in Civ), the <see cref="Streamer"/> to the local test server, the recordings on this PC (games.json +
/// session folders), the autosave check and fix, start with Windows. What the test build does not do (going public,
/// postpone, watch requests, updates, Steam) is answered with "not in the test build", never made up.
/// </summary>
sealed partial class LiveBackend : IRecorderBackend
{
    readonly RecorderSettings fileSettings;   // settings.txt next to the exe (stream address, live site, folders …)
    readonly string settingsTxt;
    readonly RecorderSession session;
    readonly WorkloadPolicy workload = new();
    readonly ResourceMonitor resourceMonitor = new();
    readonly SessionDiagnostics diagnostics;
    readonly CrashDiagnostics crashDiagnostics = new();
    readonly CancellationTokenSource resourceStop = new();
    Task? resourceTask;
    ResourceSample? latestResources;
    IDisposable? cpuLimit;
    string? cpuLimitError;
    readonly UploadPolicy uploadPolicy;
    volatile bool generousUploads;
    volatile bool civClosedAtLastSample;
    bool FastUploads => generousUploads && (civClosedAtLastSample || !session.Status.Recording);
    int UploadRate => !FastUploads || fileSettings.UploadKilobytesPerSecond == 0 ? fileSettings.UploadKilobytesPerSecond
        : Math.Max(fileSettings.UploadKilobytesPerSecond, Math.Min(4096, fileSettings.UploadKilobytesPerSecond * 4));
    int PrivateSaveLimit => FastUploads ? 4 : 1;
    int PrivateEventLimit => FastUploads ? 16 : 4;
    readonly string settingsFile;
    readonly JsonObject ui;   // the window's own settings + first-start flag
    readonly string version;
    readonly bool preview;     // --ui-shot: never touch Windows' start list
    readonly object streamLock = new();
    Streamer? streamer;
    long nextChecks, nextLibrary;
    bool libraryBusy;
    JsonArray siteGames = [];
    JsonArray checks = [];
    JsonArray games = [];
    readonly RecordingDeletionCache deletedCards = new();
    readonly Dictionary<string, (long Length, DateTime Time, GameFacts Facts)> factsCache = [];
    string? lastAutosave;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> notifiedLiveLinks = new();
    long streamDownSince = -1;
    bool toldStreamDown;

    public event Action<JsonObject>? Toast;

    public LiveBackend(RecorderSettings fileSettings, string settingsTxt, string version, bool preview)
    {
        this.fileSettings = fileSettings;
        this.settingsTxt = settingsTxt;
        this.version = version;
        this.preview = preview;
        delayedUploads = new DelayedUploads(fileSettings.OutputFolder);
        diagnostics = new SessionDiagnostics(fileSettings.OutputFolder, version, enabled: false);
        // The optional job ceiling covers this host only; child programs explicitly break away.
        if (!preview && fileSettings.CpuLimitPercent > 0)
        {
            cpuLimit = CpuLimit.TryApply(fileSettings.CpuLimitPercent, out cpuLimitError);
            diagnostics.Activity(cpuLimit is null ? "CPU ceiling unavailable" : "CPU ceiling enabled");
        }
        session = new RecorderSession(fileSettings) { Workload = workload, Diagnostics = diagnostics };
        uploadPolicy = new UploadPolicy { ExitGrace = TimeSpan.FromSeconds(5),
            IsPlaying = () => UploadPolicy.CivRunning() && !session.Status.HasRecentMenuObservation(Environment.TickCount64) };
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFACiv", "OutsideRecorder", "app");
        Directory.CreateDirectory(dir);
        settingsFile = Path.Combine(dir, "ui-settings.json");
        ui = LoadUi();
        diagnostics.Enabled = Setting("diagnostics", false);
        releases = new ReleaseNotifications(version, fileSettings, preview, () => Setting("notify.updates"));
        releases.CanCheck = () => NormalServerAllowed;
        releases.Toast += t => Toast?.Invoke(t);
        session.RecordingEnabled = !SetupPending && Setting("recording");
        ApplyCaptureSettings();
        session.Notice += OnNotice;
        session.RecordSinglePlayer = Setting("recordSinglePlayer", false);
        session.BackupUnsupportedEnabled = Setting("backupUnsupported");
        session.CaptureMoves = session.TrackLogs;
        session.SampleMs = session.CaptureMoves ? 1000 / LooksPerSecond() : 1000;
        visibility = new Visibility(fileSettings, () => { lock (streamLock) return !NormalServerAllowed ? null : streamer; }, preview);
        slots = new SlotKeeper(() => { lock (streamLock) return streamer; }, () => session.Status, preview)
            { PrepareContext = PrepareLivestreamContext, BindShare = BindLivestreamShare, CheckConsent = CheckLivestreamConsent, CanRunControl = () => BackgroundWorkAllowed };
        oldGames = new OldGamesService(fileSettings, () => { lock (streamLock) return !NormalServerAllowed ? null : streamer; }, preview)
        {
            CanWork = () => BackgroundWorkAllowed, SendingEnabled = () => !SetupPending,
            UploadSettings = () => fileSettings with { SaveUpload = UploadFormat() },
        };
    }

    /// <summary>The live slot of the game being recorded (OR05 "busy line").</summary>
    readonly SlotKeeper slots;
    /// <summary>Block / Unblock of finished games (OR03 "Visibility").</summary>
    readonly Visibility visibility;
    /// <summary>"Upload old games" (OR03 "Bulk upload").</summary>
    readonly OldGamesService oldGames;
    readonly ReleaseNotifications releases;

    bool Setting(string path, bool fallback = true)
    {
        JsonNode? n = ui["settings"];
        foreach (var p in path.Split('.')) n = n?[p];
        return n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    }

    /// <summary>Pop-ups from the recorder, as the player chose them in Settings. Never one per turn.</summary>
    void OnNotice(string title, string text)
    {
        nextChecks = 0;
        bool problem = title.StartsWith("Unsupported") || title.StartsWith("Not") || title.Contains("stopped") || title.StartsWith("In-game") || title.StartsWith("Civ's autosave");
        bool detected = title is "Civ V found" or "Recording game" or "Civ closed" or "Single-player game — not recorded" or "Multiplayer lobby";
        if (problem && !Setting("notify.problems")) return;
        if (detected && !Setting("notify.lobby")) return;
        if (title == "Recording saved" && !Setting("notify.lobby")) return;
        var kind = problem ? "warn" : title.StartsWith("Recording") ? "good" : "info";
        // the core's own wording can name the game's mod folder: the window says it neutrally
        if (title == "Not recording") { title = "This game isn't recorded"; text = "This game version is not supported for realtime recording."; }
        var t = J.Toast(kind, title, text);
        if (title is "Civ V found" or "Multiplayer lobby" or "Recording saved" or "Civ closed") return;
        if (title == "Recording game")
        {
            t["title"] = "Game detected · recording";
            t["text"] = "Your game is being recorded. You can keep playing.";
            t["actions"] = RecordingActions();
        }
        if (title == "Single-player game — not recorded")
        {
            t["actions"] = new JsonArray(J.Obj(("label", "Record it anyway"), ("cmd", "recordAnyway"), ("primary", true)));
            t["timeout"] = 30000;
        }
        Toast?.Invoke(t);
    }

    Mutex? recorderLock;
    bool otherRecorder;
    bool started;
    bool SetupPending => ui["firstRun"]?.GetValue<bool>() == true;

    /// <summary>Starts reading Civ and streaming, unless the other recorder program (FFACivOutsideRecorder.exe) already
    /// runs: two recorders would write into the same game folder. Both hold the same lock, so only one ever records.</summary>
    public void Start()
    {
        if (started || SetupPending) return;
        started = true;
        if (!preview)
        {
            try { using var process = Process.GetCurrentProcess(); process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception e) { diagnostics.Error("background priority", e); }
            resourceTask = Task.Run(MonitorResources);
        }
        oldGames.Scan();   // reading the save folders: works even while another recorder records
        bool civRunning = AutosaveCheck.CivRunning();
        if (!preview || civRunning)
        {
            recorderLock = new Mutex(true, "FFACivOutsideRecorder.Single", out bool first);
            if (!first) { otherRecorder = true; recorderLock.Dispose(); recorderLock = null; return; }
        }
        session.Start();
        RestartStream();
    }

    /// <summary>Order matters (OR03 fix 1): the recorder stops first and writes its last lines (game_left, stopped,
    /// recorder_closed), then the stream sends them in one last pass, then the program may end.</summary>
    public void Dispose()
    {
        StopLivestream();
        resourceStop.Cancel();
        try { resourceTask?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        session.Dispose();
        oldGames.Dispose();
        lock (streamLock) { streamer?.Dispose(); streamer = null; }
        try { recorderLock?.ReleaseMutex(); } catch { }
        recorderLock?.Dispose();
        releases.Dispose();
        visibility.Dispose();
        slots.Dispose();
        diagnostics.Dispose();
        cpuLimit?.Dispose();
        resourceStop.Dispose();
    }

    async Task MonitorResources()
    {
        while (!resourceStop.IsCancellationRequested)
        {
            try
            {
                var sample = resourceMonitor.Sample();
                Volatile.Write(ref latestResources, sample);
                civClosedAtLastSample = !UploadPolicy.CivRunning();
                generousUploads = civClosedAtLastSample || session.Status.HasRecentMenuObservation(Environment.TickCount64);
                workload.GameIdle = FastUploads;
                workload.Observe(sample);
                diagnostics.Observe(sample, workload.Reason == "Ready" ? session.Status.State : workload.Reason);
                if (session.Status.LastError is { } error && error != lastDiagnosticError)
                { lastDiagnosticError = error; diagnostics.Activity("Recorder reported an error"); }
            }
            catch (Exception e) { diagnostics.Error("resource monitor", e); }
            await Task.Delay(2000, resourceStop.Token);
        }
    }
    string? lastDiagnosticError;

    /// <summary>The user build: games go to a site (ffaciv.com or a test environment), not to the test server on this PC.</summary>
    bool Site => fileSettings.Stream == "site";
    bool Production => Uri.TryCreate(fileSettings.StreamAddress, UriKind.Absolute, out var address) && address.Host is "ffaciv.com" or "www.ffaciv.com";
    /// <summary>"ffaciv.com" (the site's host) or "the test server".</summary>
    string ServerShort => Site ? (Uri.TryCreate(fileSettings.StreamAddress, UriKind.Absolute, out var u) ? u.Host : "ffaciv.com") : "the test server";
    /// <summary>For sentences: "ffaciv.com" / "the test server on this PC".</summary>
    string ServerName => Site ? ServerShort : "the test server on this PC";
    /// <summary>What to do when the server does not answer.</summary>
    string WhenDown => Site ? "Check the internet connection. Everything waits on this PC and is sent when it is back." : "Start \"1 Start server\" on the Desktop. Everything waits on this PC and is sent when it is back.";

    /// <summary>The stream mode in force: the window's switch decides on/off; settings.txt only picks where (local in the test build).</summary>
    string StreamMode => SetupPending || !Setting("stream") && !LiveRequested ? "off" : fileSettings.Stream == "site" ? "site" : "local";

    bool LiveRequested => liveShare.Snapshot.Requested;
    bool BackgroundWorkAllowed => !SetupPending && workload.CanStartHeavyWork && !session.TurnTransitionQuiet;
    bool NormalServerAllowed => BackgroundWorkAllowed && (!uploadPolicy.AfterPlaying || uploadPolicy.IsIdle());
    bool LiveControlAllowed => BackgroundWorkAllowed && LiveRequested && session.TrackLogs && Setting("recording") && (!session.Status.Paused || waitingForFirstGame);
    bool LiveServerAllowed => LiveControlAllowed && (liveShare.CanSend(session.Status.Context) || !session.Status.Recording && liveShare.CanSendLobby);
    bool CanContactServerNow => NormalServerAllowed || LiveControlAllowed || BackgroundWorkAllowed && (revokingInvites || slots?.ReleasePending == true || Volatile.Read(ref revealing) > 0);
    string UploadFormat() => CapturePreferences.Format(ui["settings"]!.AsObject());

    void ApplyCaptureSettings()
    {
        CapturePreferences.Apply(session, ui["settings"]!.AsObject(), SetupPending);
        // The per-recording gate separates automatic schedules from an explicit Upload now.
        CapturePreferences.ApplyUploadPolicy(uploadPolicy, ui["settings"]!.AsObject(), SetupPending);
    }

    void RestartStream()
    {
        if (!started || SetupPending || otherRecorder || preview) return;   // setup/picture runs never send
        Streamer? old;
        lock (streamLock)
        {
            old = streamer;
            old?.SuspendUploads();
            // sent in bundles every 1-2 s (the user, 2026-10-27): an older settings.txt with a longer pause is shortened
            var s = fileSettings with { RequireReplayLifecycle = true, RequiredControlsVersion = 2,
                Stream = fileSettings.Stream == "site" ? "site" : "local", StreamUrl = fileSettings.StreamAddress,
                SaveUpload = UploadFormat(), SaveBatchLimit = LiveRequested ? 1 : int.MaxValue, EventChunkLimit = LiveRequested ? 4 : int.MaxValue, StreamIntervalS = Math.Min(fileSettings.StreamIntervalS, 2), KeepCopies = Setting("keepCopies", fileSettings.KeepCopies) };
            streamer = new Streamer(s, version: version) { Workload = workload, Diagnostics = diagnostics,
                CanUpload = () => BackgroundWorkAllowed && (uploadPolicy.CanSend() || LiveServerAllowed),
                CanUploadGame = CanUploadRecording, CanContactServer = () => CanContactServerNow,
                CanUploadSave = (game, file) => !LiveRequested || liveSaveWindow?.Includes(game.Context, file) != false
                    || uploadPolicy.IsIdle() && delayedUploads.CanUpload(game, Setting("stream")),
                ForceFullSave = (game, file) => LiveRequested && liveSaveWindow?.NeedsFullBaseline(game.Context, file) == true,
                UploadRateKilobytes = () => UploadRate,
                SavePassLimit = () => LiveRequested ? PrivateSaveLimit : int.MaxValue,
                EventPassLimit = () => LiveRequested ? PrivateEventLimit : int.MaxValue };
            deletedCards.Remember(streamer.QueuedReplayDeletions());
            streamer.Start();
        }
        if (old is not null) Task.Run(old.Dispose);
    }

    // ---- the window's own settings ---------------------------------------------------------------------------------------
    JsonObject LoadUi()
    {
        JsonObject? saved = null;
        try { if (File.Exists(settingsFile)) saved = JsonNode.Parse(File.ReadAllText(settingsFile)) as JsonObject; } catch { }
        var defaults = J.Obj(
            ("firstRun", true),
            ("settings", J.Obj(("diagnostics", false), ("backupUnsupported", true), ("captureMoves", true), ("recording", true), ("trackLogs", fileSettings.TrackLogs), ("uploadTiming", fileSettings.UploadTiming), ("recordSinglePlayer", false), ("looksPerSecond", 20), ("keepCopies", fileSettings.KeepCopies), ("stream", true), ("upload", false), ("publishDelay", 3),
                ("notify", J.Obj(("lobby", true), ("saved", false), ("reminders", true), ("problems", true), ("requests", true), ("updates", true))),
                ("startWithWindows", true), ("dataFolder", fileSettings.OutputFolder), ("steam", J.Obj(("linked", false))), ("language", "en"))),
            ("autoSmall", true));
        if (saved is not null) Merge(defaults, saved);
        // Older versions only remembered the old introduction. Show this setup once without
        // resetting the user's choices, and do not start capturing/sending until it is finished.
        Onboarding.Migrate(defaults);
        if (saved?["settings"]?["trackLogs"] is null && saved?["settings"]?["captureMoves"] is JsonValue oldCapture)
            defaults["settings"]!["trackLogs"] = oldCapture.DeepClone();
        defaults["settings"]!["experimentalMemoryOptIn"] ??= fileSettings.MemoryTest;
        defaults["settings"]!["saveUpload"] ??= "delta";
        if (saved?["settings"]?["experimentalMemoryOptIn"] is null) defaults["settings"]!["looksPerSecond"] = 4;
        if (fileSettings.MemoryTest) { defaults["settings"]!["experimentalMemoryOptIn"] = true; defaults["settings"]!["trackLogs"] = true; }
        if (defaults["settings"]!["experimentalMemoryOptIn"]?.GetValue<bool>() != true)
        {
            defaults["settings"]!["trackLogs"]=false; defaults["settings"]!["captureMoves"]=false;
        }
        if (fileSettings.SafeCapture) { defaults["settings"]!["uploadTiming"]="after"; defaults["settings"]!["keepCopies"]=true; defaults["settings"]!["backupUnsupported"]=false; }
        // the test build (OR03): stream to the local test server and start with Windows, once, whatever an older preview saved
        if (defaults["testBuild"]?.GetValue<int>() is not >= 1)
        {
            defaults["settings"]!["stream"] = true;
            defaults["settings"]!["upload"] = false;
            defaults["settings"]!["startWithWindows"] = true;
            if (!preview) defaults["testBuild"] = 1;
        }
        // Windows itself is the truth (the player may have switched it off in Task Manager)
        if (saved?["testBuild"] is not null) defaults["settings"]!["startWithWindows"] = StartWithWindows;
        defaults["settings"]!["dataFolder"] = fileSettings.OutputFolder;
        if (!preview) SaveUi(defaults);
        return defaults;
    }

    static void Merge(JsonObject into, JsonObject from)
    {
        foreach (var (k, v) in from)
        {
            if (v is JsonObject o && into[k] is JsonObject t) Merge(t, o);
            else into[k] = v?.DeepClone();
        }
    }

    void SaveUi(JsonObject? o = null) { if (preview) return; try { File.WriteAllText(settingsFile, (o ?? ui).ToJsonString(new JsonSerializerOptions { WriteIndented = true })); } catch { } }

    // ---- state ---------------------------------------------------------------------------------------------------------
    public JsonObject BuildState()
    {
        var s = session.Status;
        UpdateRecordingUx(s);
        RefreshLibrary();
        StreamStatus? st; string mode;
        lock (streamLock) { st = streamer?.Status; mode = StreamMode; }
        if (preview && mode != "off") st = ProbeStatus();
        if (Environment.TickCount64 >= nextChecks)
        {
            checks = BuildChecks(s, st, mode);
            if (BackgroundWorkAllowed) games = ListGames(s, mode);
            nextChecks = Environment.TickCount64 + 4000;
        }
        games = deletedCards.Filter(games);
        WatchForToasts(s, st, mode);
        var (mod, modVersion) = ModOf(s.Layout);
        JsonObject status;
        JsonObject? current = null;
        bool inGame = s.Identity is not null && s.Players.Count > 0 && s.Game is not null;
        if (!Setting("recording")) status = J.Obj(("kind", "paused"), ("title", "Recording is off"),
            ("detail", "No new saves are copied. Existing recordings follow your upload choices in Games."));
        else if (!session.TrackLogs) status = J.Obj(("kind", s.Paused ? "paused" : SaveCaptureActive(s) ? "recording" : "waiting"),
            ("title", s.Paused ? "Recording paused" : SaveCaptureActive(s) ? "Recording your game" : "Waiting for Civ"),
            ("detail", s.Paused ? "Start recording to keep new saves." : SaveCaptureActive(s) ? "Autosaves are being kept on this PC. Keep playing." : "Start Civ V and play. Recording starts with the first autosave."),
            ("note", Setting("stream") ? "After the game, you have 2 hours to upload, postpone or delete." : "Uploads are off. Your recordings stay on this PC."));
        else if (s.Recording && session.TurnTransitionQuiet)
            status = J.Obj(("kind", "recording"), ("title", "Recording · new turn settling"), ("detail", "Detailed reads resume after the 5-second pause."));
        else if (s.Recording && s.State == "Recording")
            status = J.Obj(("kind", "recording"), ("title", $"Recording {mod ?? "Civ V"} game"), ("detail", Join(s.Turn is int t ? $"Turn {t}" : null, s.Mode)));
        else if (s.Recording) status = J.Obj(("kind", "found"), ("title", $"Civ V found · {mod} {modVersion}".TrimEnd()), ("detail", "In the menus or loading. Recording goes on when the game is back."));
        else if (inGame && s.SinglePlayerSkipped) status = J.Obj(("kind", "paused"), ("title", "Single-player game — not recorded"),
            ("detail", "Single-player games are off in Settings (for testing, coming later). Multiplayer and hot seat games are recorded."),
            ("action", J.Obj(("cmd", "recordAnyway"), ("label", "Record it anyway"))));
        else if (inGame && s.Paused) status = J.Obj(("kind", "paused"), ("title", "Not recording this game"), ("detail", Setting("recording") ? "You paused it. Press \"Record this game again\" to go on." : "Recording is off in Settings."));
        else if (s.State.StartsWith("Waiting for Civ")) status = J.Obj(("kind", "waiting"), ("title", "Waiting for Civ"), ("detail", "Start a game and it is recorded by itself."));
        else if (s.Lobby is { } lb) status = J.Obj(("kind", "found"), ("title", "In the multiplayer lobby"),
            ("detail", Join($"{lb.Slots.Count(x => x.Status == 3)} players", lb.MapScript is { } m ? MapName(m) : null, WorldSizeName(lb.WorldSize), lb.TurnTimer?.TurnSeconds is int ts ? $"turn timer {ts} s" : lb.TurnTimer?.Enabled == true ? "turn timer on" : null) + ". Recorded when the game starts."));
        else if (s.State.StartsWith("Civ found")) status = J.Obj(("kind", "found"), ("title", $"Civ V found · {mod} {modVersion}".TrimEnd()), ("detail", "Recording starts when a game is running."));
        else if (s.UnsupportedKek) status = J.Obj(("kind", "paused"), ("title", "KEK Mod is supplied by Oblast"), ("detail", "KEK saves are not copied or uploaded by this recorder."));
        else if (s.UnsupportedBackingUp) status = J.Obj(("kind", "attention"), ("title", "Unsupported build — keeping autosaves"),
            ("detail", $"{s.UnsupportedSaves} autosaves kept on this PC, with a diagnostic log. Upload them after support is fixed. Realtime movement logs are unavailable."),
            ("action", J.Obj(("cmd", "openFolder"), ("label", "Open backup folder"), ("args", J.Obj(("path", s.UnsupportedFolder))))));
        else if (s.State == "Starting") status = J.Obj(("kind", "starting"), ("title", "Starting…"));
        else status = J.Obj(("kind", "unsupported"), ("title", "This game isn't recorded"), ("detail", "This game version is not supported for realtime recording."),
            ("note", "Civ's original autosaves are kept. Manual upload may be possible once the website supports this mod version. KEK Mod games come from Oblast."));
        if (otherRecorder)
            status = J.Obj(("kind", "paused"), ("title", "Another recorder is running"), ("detail", "The old FFACiv Outside Recorder window records right now. Close it (its icon by the clock or its window), then start this one again."));
        else if (!Setting("recording") && !inGame)
            status = J.Obj(("kind", "paused"), ("title", "Recording is off"), ("detail", "Turn the recorder back on here when you want to record."));
        if (!otherRecorder && Setting("recording") && !workload.CanStartHeavyWork)
            status = J.Obj(("kind", "waiting"), ("title", "Waiting for Civ"), ("detail", "The recorder is on. Keep playing; resource details are available in Checks."));

        if (!session.TrackLogs && s.Context is not null && s.Folder is not null && SaveCaptureActive(s))
        {
            current = J.Obj(("id", "live"), ("context", s.Context), ("mod", "Civ V"), ("turn", s.Turn), ("players", new JsonArray()),
                ("paused", s.Paused), ("folder", s.Folder), ("upload", Upload(s.Folder, st, mode)), ("liveLink", LiveLink(s, st, mode)));
            AddSaves(current, s.Folder, s.Context, mode, st);
        }
        else if (inGame && (s.Recording || s.Paused))
        {
            var knownCurrent = new GameIndex(fileSettings.OutputFolder).Load().LastOrDefault(e => e.Identity == s.Identity);
            var currentContext = s.Context ?? knownCurrent?.Context;
            var folder = s.Folder ?? knownCurrent?.Folder;
            current = J.Obj(("id", "live"), ("mod", mod ?? "Civ V"), ("modVersion", modVersion), ("mode", s.Mode ?? "game"), ("turn", s.Turn),
                ("players", Players(s.Players, s.ActivePlayer)), ("cityStates", s.Players.Count(p => !IsMajor(p) && p.Alive)),
                ("startedAt", s.GameStarted is { } gs ? J.Ms(gs) : null), ("lastSaveAt", s.LastSaveAt is { } ls ? J.Ms(ls) : null),
                ("paused", s.Paused), ("ended", !s.Recording), ("context", currentContext), ("upload", Upload(folder, st, mode)), ("publish", J.Obj(("state", "local"))), ("folder", folder),
                ("liveLink", LiveLink(s, st, mode)), ("replay", currentContext is { } context ? ReplayUiState(context) : null));
            AddSaves(current, folder, currentContext, mode, st);
        }
        else if (s.Lobby is { } lobby)
        {
            current = J.Obj(("id", "lobby"), ("lobby", true), ("mod", mod ?? "Civ V"), ("modVersion", modVersion), ("mode", "multiplayer lobby"),
                ("players", J.Arr(lobby.Slots.Where(x => x.InUse).Select(x => (JsonNode?)J.Obj(
                    ("name", x.Status == 3 ? x.Name ?? "Player" : x.Status == 1 ? "AI" : x.Status == 0 ? "Open" : x.Status == 4 ? (x.Name ?? "Observer") + " (watching)" : "–"),
                    ("civ", x.Status is 0 or 4 ? "–" : x.Civ == -1 ? "Random" : CivName(x.CivKey)), ("team", x.Team),
                    ("color", x.Id == lobby.ActivePlayer ? "#f0c860" : x.Status == 3 ? "#5ec8bc" : "#7d8b88"), ("you", x.Id == lobby.ActivePlayer), ("human", x.Status == 3))))),
                ("map", lobby.MapScript is { } m2 ? MapName(m2) : null), ("worldSize", lobby.WorldSize), ("mapSize", WorldSizeName(lobby.WorldSize)),
                ("turnTimerOn", lobby.TurnTimer?.Enabled == true), ("gameSpeed", lobby.GameSpeed), ("era", lobby.Era),
                ("turnTimerSeconds", lobby.TurnTimer?.TurnSeconds), ("options", lobby.Options.Count(o => o.Value != 0)), ("gameName", lobby.GameName),
                ("upload", Upload(null, st, mode)), ("publish", J.Obj(("state", "local"))), ("liveLink", LiveLink(s, st, mode)));
        }

        if (session.IsDeletedIdentity(s.Identity) || current is not null && deletedCards.Hides(current)) { current = null; status = J.Obj(("kind", "paused"), ("title", "This game was deleted"), ("detail", "It will not be recorded again. New games are recorded normally.")); }

        return J.Obj(("live", true), ("production", Production), ("testBuild", !Production), ("site", Site), ("serverShort", ServerShort), ("serverName", Site ? ServerShort : "The test server"), ("version", version), ("firstRun", ui["firstRun"]?.DeepClone()),
            ("status", status), ("civDetected", s.CivDetected || s.Game is not null || s.Lobby is not null), ("current", current), ("checks", checks.DeepClone()), ("games", games.DeepClone()),
            ("deleted", HeldDeletions()),
            ("requests", new JsonArray()), ("permissions", new JsonArray()),
            ("updates", releases.State()),
            ("resources", ResourceState()),
            ("crashDiagnostics", CrashDiagnosticsState()),
            ("memoryLogging", MemoryLoggingState()),
            ("reportBusy", Volatile.Read(ref reportBusy) != 0),
            ("safeCapture", fileSettings.SafeCapture), ("livestream", LivestreamState(s, st, mode)), ("settings", ui["settings"]!.DeepClone()), ("oldGames", oldGames.State()), ("oldHintDismissed", ui["oldHintDismissed"]?.DeepClone() ?? false),
            ("test", J.Obj(("channelUrl", ChannelUrl(s.Channel)), ("streamUrl", fileSettings.StreamAddress), ("liveSiteUrl", fileSettings.LiveSiteAddress), ("settingsFile", settingsTxt), ("streamState", st?.State ?? "off"))));
    }

    JsonObject CrashDiagnosticsState()
    {
        var s = crashDiagnostics.Snapshot;
        return J.Obj(("busy", s.Busy), ("checkedAt", s.CheckedAt is { } at ? J.Ms(at) : null),
            ("status", s.Result?.Status), ("crashes", new JsonArray((s.Result?.Crashes ?? [])
                .Select(c => (JsonNode)J.Obj(("at", J.Ms(c.At)), ("application", c.Application), ("module", c.Module), ("exceptionCode", c.ExceptionCode))).ToArray())));
    }

    JsonObject ResourceState()
    {
        var r = Volatile.Read(ref latestResources);
        return J.Obj(("reason", session.TurnTransitionQuiet ? "Waiting for the new turn to settle" : workload.Reason), ("paused", !workload.CanStartHeavyWork || session.TurnTransitionQuiet),
            ("cpuPercent", r?.RecorderCpuPercent), ("systemCpuPercent", r?.SystemCpuPercent),
            ("memoryBytes", r?.WorkingSetBytes), ("peakMemoryBytes", r?.PeakWorkingSetBytes),
            ("uploadKibPerSecond", UploadRate), ("idleUploadMode", FastUploads), ("cpuBudgetPercent", workload.RecorderCpuBudgetPercent),
            ("cpuLimitPercent", fileSettings.CpuLimitPercent), ("cpuLimitError", cpuLimitError));
    }

    StreamStatus? probe; long nextProbe;
    /// <summary>For the pictures only: whether the test server answers at all (a TCP connect, nothing sent).</summary>
    StreamStatus ProbeStatus()
    {
        if (probe is not null && Environment.TickCount64 < nextProbe) return probe;
        bool up = false;
        try
        {
            var u = new Uri(fileSettings.StreamAddress);
            using var c = new System.Net.Sockets.TcpClient();
            up = c.ConnectAsync(u.Host, u.Port).Wait(800) && c.Connected;
        }
        catch { }
        nextProbe = Environment.TickCount64 + 4000;
        return probe = new StreamStatus { State = up ? "connected" : "server not reachable – queued, retrying" };
    }

    /// <summary>Pop-ups the window makes itself: the test server lost for a while during a game (once per outage), and
    /// "saved" when the player asked for one per saved turn (off by default).</summary>
    void WatchForToasts(RecorderStatus s, StreamStatus? st, string mode)
    {
        if (!uploadPolicy.CanSend()) return;
        if (slots.Enabled && !preview && Setting("notify.lobby") && LiveLink(s, st, mode).Str("url") is { } liveUrl
            && notifiedLiveLinks.TryAdd(liveUrl, 0))
        {
            var ready = J.Toast("good", "Private livestream ready", "Only people with your link can watch. It is not listed publicly.");
            AddNotificationLink(ready, liveUrl);
            Toast?.Invoke(ready);
        }
        if (s.LastAutosave is { } la && la != lastAutosave)
        {
            if (lastAutosave is not null && s.Recording && Setting("notify.saved", false))
                Toast?.Invoke(J.Toast("good", $"Turn {s.Turn} saved", "Kept on this PC" + (mode == "off" ? "." : $" and sent to {ServerName}.")));
            lastAutosave = la;
        }
        // a problem the player must hear about once: cap reached, version too old, blocked / switched off
        if (st?.Problem is { } pr && pr != toldProblem && pr is "cap" or "update" or "blocked" or "off" && Setting("notify.problems"))
        {
            toldProblem = pr;
            var (title, text) = ProblemWords(st);
            var t = J.Toast("warn", title, text);
            if (pr == "update" && st.DownloadUrl is { } dl) t["actions"] = new JsonArray(J.Obj(("label", "Get the new version"), ("cmd", "openSite"), ("args", J.Obj(("url", dl))), ("primary", true)));
            Toast?.Invoke(t);
        }
        if (st?.Problem is null) toldProblem = null;
        bool down = mode != "off" && st is { State: var state } && state.StartsWith("server not reachable");
        if (!down) { streamDownSince = -1; toldStreamDown = false; return; }
        if (streamDownSince < 0) streamDownSince = Environment.TickCount64;
        if (!toldStreamDown && s.Recording && Environment.TickCount64 - streamDownSince > 30_000 && Setting("notify.problems"))
        {
            toldStreamDown = true;
            Toast?.Invoke(J.Toast("warn", $"Can't reach {ServerShort}", "The game is kept on this PC and sent when the server is back. " + WhenDown));
        }
    }

    void AddNotificationLink(JsonObject toast, string url)
    {
        notifiedLiveLinks.TryAdd(url, 0);
        toast["text"] = (toast.Str("text") ?? "") + "\n" + url;
        var actions = toast["actions"] as JsonArray ?? new JsonArray();
        if (toast["actions"] is null) toast["actions"] = actions;
        actions.Add(J.Obj(("label", "Open livestream"), ("cmd", "openSite"), ("args", J.Obj(("url", url)))));
        if (actions.Count == 1) actions.Add(J.Obj(("label", "Copy link"), ("cmd", "copyLiveLink"), ("primary", true)));
        toast["timeout"] = 30000;
    }

    string? toldProblem;

    /// <summary>The stream's problem in plain words (title, text) for the window and the pop-ups.</summary>
    (string Title, string Text) ProblemWords(StreamStatus st) => st.Problem switch
    {
        "cap" => ("Today's upload limit is reached", $"The rest is sent by itself after {(st.Until is { } u ? u.ToLocalTime().ToString("HH:mm") : "midnight UTC")}. Nothing is lost: it waits on this PC."),
        "update" => ("Please update the recorder", $"This version is too old for {ServerShort}{(st.MinVersion is { } v ? $" (it needs {v} or newer)" : "")}. Nothing is lost: your games wait on this PC and go out after the update."),
        "blocked" => ($"This PC can't send to {ServerShort}", "Your games stay on this PC. If you think this is a mistake, write to us on the site."),
        "off" => ($"Sending is paused on {ServerShort}", "Your games wait on this PC and are sent when it opens again."),
        "key" => ("Signing this PC in again", $"{ServerShort} did not know this PC any more; it is registered again by itself."),
        "unreachable" => ($"Can't reach {ServerShort}", WhenDown),
        _ => ("", ""),
    };

    static bool IsMajor(PlayerState p) => p.Id < 22 && p.Civ is not ("CIVILIZATION_MINOR" or "CIVILIZATION_BARBARIAN");

    /// <summary>The major civs of a game for the chips: civ name, human or AI, which one is the player at this PC.</summary>
    /// Only living players: the memory has no "was ever in the game" mark, and unused slots hold a civ too (not alive).
    static JsonArray Players(IEnumerable<PlayerState> players, int? me) => J.Arr(players.Where(p => IsMajor(p) && p.Alive).Select(p => (JsonNode?)J.Obj(
        ("name", p.Slot == 3 ? "Human" : p.Slot == 1 ? "AI" : "–"), ("civ", CivName(p.Civ)),
        ("color", p.Id == me ? "#f0c860" : p.Slot == 3 ? "#5ec8bc" : "#7d8b88"), ("you", p.Id == me), ("human", p.Slot == 3), ("alive", p.Alive))));

    /// <summary>"CIVILIZATION_UC_CUBA_BATISTA" → "Cuba Batista"; mod prefixes (UC_, MC_, JFD_ …) dropped.</summary>
    public static string CivName(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "–";
        var k = Regex.Replace(key, "^CIVILIZATION_", "");
        k = Regex.Replace(k, "^(UC|MC|JFD|TCM|LS|CL|SENSHI|DMS|EW|LITE)_", "");
        return string.Join(' ', k.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    /// <summary>The stream of one game folder: off / queued (server away) / sending / sent, how far, whole saves vs changes.</summary>
    JsonObject? ReplayUiState(string context)
    {
        if (streamer is not { } sender) return null;
        var state = sender.ReadReplay(context);
        var pending = sender.ReadReplay(context, true);
        state["completionQueued"] = pending.Str("action") == "complete";
        state["keepShortRequested"] = pending["keepShort"]?.GetValue<bool>() == true;
        return state;
    }

    JsonObject Upload(string? folder, StreamStatus? st, string mode)
    {
        if (mode == "off") return J.Obj(("state", "off"), ("note", "Kept on this PC. Automatic sending is disabled."), ("target", "test"));
        if (!uploadPolicy.CanSend() && !LiveServerAllowed) return J.Obj(("state", "held"), ("target", "test"), ("note", uploadPolicy.Automatic
            ? "Stored on this PC. Uploading and processing will start after Civ closes." : "Manual upload only. Automatic sending is off."));
        var (queuedBytes, queuedSaves, lastTurn, sentSaves) = folder is null ? (0L, 0, (int?)null, 0) : StreamOf(folder);
        bool down = st?.State is { } sx && (sx.StartsWith("server not reachable") || sx.StartsWith("stalled"));
        var state = down ? "queued" : st?.State is "starting" or null ? "sending" : queuedBytes > 0 || queuedSaves > 0 ? "sending" : "sent";
        var o = J.Obj(("state", state), ("target", "test"), ("server", fileSettings.StreamAddress), ("queued", queuedSaves), ("queuedBytes", queuedBytes),
            ("lastSentTurn", lastTurn), ("sentSaves", sentSaves), ("lastSentAt", st?.LastSent is { } t ? J.Ms(t) : null),
            ("wholeSaves", st?.FullSaves ?? 0), ("deltaSaves", st?.DeltaSaves ?? 0), ("eventBytes", st?.EventWireBytes ?? 0), ("saveBytes", st?.SaveWireBytes ?? 0),
            ("error", down ? st?.LastError : null));
        return o;
    }

    static (long QueuedBytes, int QueuedSaves, int? LastTurn, int SentSaves) StreamOf(string folder)
    {
        try
        {
            var f = Path.Combine(folder, "stream.json");
            var state = File.Exists(f) ? JsonSerializer.Deserialize<StreamState>(SafeFiles.ReadAllText(f)) ?? new() : new();
            var events = Path.Combine(folder, "events.jsonl");
            long queued = File.Exists(events) ? Math.Max(0, new FileInfo(events).Length - state.SentOffset) : 0;
            int queuedSaves = Streamer.SavesOf(folder).Count(x => !state.SentSaves.ContainsKey(Path.GetFileName(x)));
            int? last = state.SentSaves.Keys.Select(RecorderSession.AutosaveTurn).Where(t => t is not null).Max();
            return (queued, queuedSaves, last, state.SentSaves.Count);
        }
        catch { return (0, 0, null, 0); }
    }

    /// <summary>The address where the game can be watched live on the local live site (OR03 "Watch link"), or why not yet.</summary>
    JsonObject LiveLink(RecorderStatus s, StreamStatus? st, string mode)
    {
        var share = liveShare.Snapshot;
        if (!share.Requested) return J.Obj(("disabled", true), ("url", null), ("why", "Get a private livestream link from the active recorder."));
        var slot = slots.Identity == s.Identity ? slots.State : LiveSlotState.Unknown;
        string? why = share.Url is not null ? null : liveShareProblem ?? (slot.State == "busy"
            ? "Waiting for an available livestream slot. Your invite appears once the server confirms it."
            : "Confirming private access and creating your timed invite…");
        return J.Obj(("url", share.Url), ("disabled", share.Url is null), ("why", why), ("private", true),
            ("busy", slot.State == "busy"), ("position", slot.Position), ("requested", slot.Requested),
            ("scope", share.Scope), ("expiresAt", share.ExpiresAt is { } expiry ? J.Ms(expiry) : null), ("channel", share.Scope == "player"));
    }

    /// <summary>The player's own channel on the live site (OR03 "Lobby / channel"): the lobby before the game, the game after.</summary>
    string? ChannelUrl(string? channel)
    {
        if (channel is null) return null;
        var site = fileSettings.LiveSiteAddress.EndsWith('/') ? fileSettings.LiveSiteAddress : fileSettings.LiveSiteAddress + "/";
        return $"{site}live/channel/{channel}";
    }

    /// <summary>"Assets\Maps\Lekmap v6.3\LekmapPangaeaFractalv6.3.lua" → "LekmapPangaeaFractalv6.3".</summary>
    /// <summary>The map size's name: the game's Worlds table keeps Duel, Tiny, Small, Standard, Large, Huge as rows 0-5 (vanilla, Lekmod, Snowve alike).</summary>
    static string? WorldSizeName(int? size) => size switch { 0 => "Duel", 1 => "Tiny", 2 => "Small", 3 => "Standard", 4 => "Large", 5 => "Huge", _ => null };

    static string MapName(string script) => Path.GetFileNameWithoutExtension(script.Replace('\\', '/').Split('/')[^1]);

    string? LiveUrl(RecorderStatus s)
    {
        if (s.Identity is null) return null;
        var site = fileSettings.LiveSiteAddress.EndsWith('/') ? fileSettings.LiveSiteAddress : fileSettings.LiveSiteAddress + "/";
        return $"{site}live/{Uri.EscapeDataString(GameIdOf(s.Identity))}/watch";
    }

    /// <summary>The live site's game id (OR03 "Feed"): the game identity, its two pre-game seeds ("0a41a177-0a41a177"),
    /// the same on every player's PC. The link <c>&lt;live site&gt;/live/&lt;game&gt;/watch</c> is always valid: it waits for the
    /// first converted turn, then goes to the watch page.</summary>
    public static string GameIdOf(string identity) => identity;

    static string? Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>The game core's name → (mod, version): "LEKMOD v35.4.003" → ("Lekmod", "35.4"), "Snowve 8.2.1" → ("Snowve", "8.2.1"), BNW → ("Vanilla", "BNW").</summary>
    public static (string? Mod, string? Version) ModOf(string? layout)
    {
        if (layout is null) return (null, null);
        if (layout.Contains("LEKMOD", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(layout, @"v?(\d+\.\d+)");
            return ("Lekmod", m.Success ? m.Groups[1].Value : null);
        }
        if (layout.Contains("Snowve", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(layout, @"(\d+\.\d+\.\d+)");
            return ("Snowve", m.Success ? m.Groups[1].Value : null);
        }
        if (layout.Contains("BNW", StringComparison.OrdinalIgnoreCase)) return ("Vanilla", "BNW");
        return (null, null);
    }

    bool civWasRunning;

    /// <summary>Looks at the game per second (Settings: 5, 10 or 20; 20 by default).</summary>
    int LooksPerSecond() => CapturePreferences.Rate(ui["settings"]!.AsObject());

    JsonArray BuildChecks(RecorderStatus s, StreamStatus? st, string mode)
    {
        var list = new JsonArray();
        var ini = AutosaveCheck.Read(AutosaveCheck.IniPath);
        if (ini is null) list.Add(J.Obj(("id", "autosave"), ("level", "info"), ("title", "Civ's autosave setting not found yet"), ("text", "Start Civ V once; then we can check that it saves every turn.")));
        else
        {
            var (between, kept) = ini.Value;
            bool everyTurn = between == 1, enough = kept is int k && k >= 10;
            if (everyTurn && enough) list.Add(J.Obj(("id", "autosave"), ("level", "ok"), ("title", "Civ saves every turn"), ("text", "Autosave is set to every turn, so the replay has every turn.")));
            else
            {
                bool running = AutosaveCheck.CivRunning();
                if (civWasRunning && !running && Setting("notify.problems"))
                {
                    var t = J.Toast("warn", "Civ is closed: fix the autosave setting now?", "Civ does not save every turn, so replays would miss turns. We change two numbers and keep a copy of the old file.");
                    t["actions"] = new JsonArray(J.Obj(("label", "Fix it for me"), ("cmd", "fixAutosave"), ("primary", true)));
                    t["timeout"] = 30000;
                    Toast?.Invoke(t);
                }
                civWasRunning = running;
                var text = !everyTurn
                    ? (between is int b ? $"Civ saves only every {b} turns, so the replay would miss turns." : "Civ's autosave is not set to every turn.")
                    : $"Civ keeps only {kept} autosaves; we would like at least 10.";
                list.Add(J.Obj(("id", "autosave"), ("level", "warn"), ("title", everyTurn ? "Civ keeps few autosaves" : "Civ does not save every turn"), ("text", text + (running ? " Civ must be closed to change this." : "")),
                    ("action", J.Obj(("cmd", "fixAutosave"), ("label", "Fix it for me"), ("disabled", running), ("why", running ? "Close Civ first, then press this." : null)))));
            }
        }
        if (s.UnsupportedBackingUp) list.Add(J.Obj(("id", "unsupported"), ("level", "warn"), ("title", "Autosave backup only"),
            ("text", "This DLL has no verified realtime support. Autosaves and a diagnostic log are kept locally for manual upload after support is fixed."),
            ("action", J.Obj(("cmd", "openFolder"), ("label", "Open backup folder"), ("args", J.Obj(("path", s.UnsupportedFolder)))))));
        if (s.InGameRecorderPresent || RecorderSession.InstalledInGameRecorder() is not null)
            list.Add(J.Obj(("id", "ingame"), ("level", "info"), ("title", "The in-game recorder is also installed"),
                ("text", "Found the FFACiv Recorder mod in Civ's DLC folder. Both can run; you only need one. To use only this program, move the folder FFACiv_Recorder out of Assets\\DLC while Civ is closed."),
                ("action", J.Obj(("cmd", "openFolder"), ("label", "Show the folder"), ("args", J.Obj(("path", Path.GetDirectoryName(RecorderSession.InstalledInGameRecorder() ?? ""))))))));
        var (mod, ver) = ModOf(s.Layout);
        if (s.Game is not null && !s.State.StartsWith("Waiting"))
            list.Add(mod is null
                ? J.Obj(("id", "version"), ("level", "warn"), ("title", "This game version is not supported yet"), ("text", "Realtime recording is off for this game version. Civ keeps its own autosaves; unsupported builds keep local autosave backups and a diagnostic log when backup is on. KEK Mod games are supplied by Oblast."))
                : J.Obj(("id", "version"), ("level", "ok"), ("title", $"{mod} {ver} is supported".Replace("  ", " ")), ("text", "This game version can be recorded.")));
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(fileSettings.OutputFolder))!;
            var free = new DriveInfo(root).AvailableFreeSpace / 1e9;
            list.Add(free < 2
                ? J.Obj(("id", "disk"), ("level", "warn"), ("title", "Disk space is low"), ("text", $"Only {free:0.0} GB free on {root.TrimEnd('\\')}. Recording goes on; free some space soon."))
                : J.Obj(("id", "disk"), ("level", "ok"), ("title", "Enough disk space"), ("text", $"{free:0} GB free. A long game needs about 200 MB.")));
        }
        catch { }
        if (s.Game is not null && s.LooksPerSecond > 0)
            list.Add(s.LooksPerSecond < s.LooksPerSecondWanted - 0.5
                ? J.Obj(("id", "pace"), ("level", "info"), ("title", $"Looking {s.LooksPerSecond:0} times a second (slowed down)"),
                    ("text", $"One look takes {s.LookMs:0.0} ms on this PC right now, so the recorder looks less often ({s.LooksPerSecondWanted:0} asked) to stay under a tenth of one processor core."))
                : J.Obj(("id", "pace"), ("level", "ok"), ("title", $"Looking {s.LooksPerSecond:0} times a second"),
                    ("text", $"Units every look, cities 5 times a second, the map's tiles once a second. One look takes {s.LookMs:0.0} ms on average, about {s.LookMs * s.LooksPerSecond / 10:0.0} % of one processor core.")));
        if (!uploadPolicy.CanSend()) list.Add(J.Obj(("id", "stream"), ("level", "info"), ("title", uploadPolicy.Automatic ? "Stored for upload after Civ closes" : "Manual upload only"), ("text", "Automatic uploads are held by the recording controls on Home. Existing local copies stay queued.")));
        else if (mode == "off") list.Add(J.Obj(("id", "stream"), ("level", "info"), ("title", "Uploads are off"), ("text", "Games stay on this PC. Turn on Allow uploads on Home when ready.")));
        else if (st?.Problem is "cap" or "update" or "blocked" or "off" or "key")
        {
            var (title, text) = ProblemWords(st);
            var c = J.Obj(("id", "stream"), ("level", st.Problem is "update" or "blocked" ? "bad" : "warn"), ("title", title), ("text", text));
            if (st.Problem == "update" && st.DownloadUrl is { } dl) c["action"] = J.Obj(("cmd", "openSite"), ("label", "Get the new version"), ("args", J.Obj(("url", dl))));
            list.Add(c);
        }
        else if (st?.State.StartsWith("stalled") == true)
            list.Add(J.Obj(("id", "stream"), ("level", "warn"), ("title", fileSettings.SafeCapture ? "Upload worker stalled — restart the recorder" : "Stream stalled — starting it again"),
                ("text", fileSettings.SafeCapture ? "New uploads are held and local copies remain queued. Restart the recorder to retry." : "The sender stopped making progress, so it was started again. Local copies remain queued. Details in stream.log next to the games.")));
        else if (st?.State.StartsWith("server not reachable") == true)
            list.Add(J.Obj(("id", "stream"), ("level", "warn"), ("title", $"Can't reach {ServerShort}"),
                ("text", $"Nothing answers at {fileSettings.StreamAddress}. {WhenDown}")));
        else if (st?.State.StartsWith("connected") == true)
            list.Add(J.Obj(("id", "stream"), ("level", "ok"), ("title", $"Connected to {ServerShort}"), ("text", LiveRequested ? "Private livestream requested. Recording data sends after private access is confirmed; local copies are retained." : "Queued saves upload after Civ has stayed closed for 30 seconds and their upload countdown permits it. Local save copies are retained.")));
        else list.Add(J.Obj(("id", "stream"), ("level", "info"), ("title", $"Connecting to {ServerShort}…"), ("text", fileSettings.StreamAddress)));
        return list;
    }

    // ---- the games on this PC --------------------------------------------------------------------------------------------
    /// <summary>What the history shows of one recording folder (read once per change of its events.jsonl).</summary>
    sealed record GameFacts(string? Mod, string? ModVersion, int? LastTurn, JsonArray Players, string? Identity, DateTimeOffset Date);

    /// <summary>The recordings on this PC (one folder per game, games.json says which game): mod, turns, civs, stream state.</summary>
    JsonArray ListGames(RecorderStatus s, string mode)
    {
        var list = new JsonArray();
        try
        {
            if (!Directory.Exists(fileSettings.OutputFolder)) return list;
            var blocks = visibility.All();
            var known = new GameIndex(fileSettings.OutputFolder).Load().GroupBy(e => Path.GetFullPath(e.Folder), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(fileSettings.OutputFolder, "session-*").OrderByDescending(d => Path.GetFileName(d)).Take(40))
            {
                var file = Path.Combine(dir, "events.jsonl");
                if (!File.Exists(file) && !Streamer.SavesOf(dir).Any()) continue;
                var facts = File.Exists(file) ? FactsOf(dir, file) : new GameFacts("Civ V · save files only", null,
                    Streamer.SavesOf(dir).Select(RecorderSession.AutosaveTurn).Where(t => t is not null).DefaultIfEmpty().Max(), [], null,
                    RecorderSession.StartedOf(dir) ?? Directory.GetCreationTime(dir));
                known.TryGetValue(Path.GetFullPath(dir), out var entry);
                if (entry is not null && session.IsDeletedIdentity(entry.Identity)) continue;
                bool now = s.Recording && s.Folder is not null && string.Equals(Path.GetFullPath(s.Folder), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)
                    && (entry is null || !Streamer.HasEnded(entry));
                var (qb, qs, lastSent, sentSaves) = StreamOf(dir);
                bool streamed = sentSaves > 0;
                var status = now ? "recording" : !streamed ? "local" : qb > 0 || qs > 0 ? (mode == "off" || entry is not null && !CanUploadRecording(entry) ? "local" : "sending") : "streamed";
                list.Add(J.Obj(("id", Path.GetFileName(dir)), ("mod", facts.Mod ?? "Civ V"), ("modVersion", facts.ModVersion), ("date", J.Ms(facts.Date)),
                    ("turns", now ? s.Turn : facts.LastTurn), ("players", facts.Players.DeepClone()), ("status", status), ("folder", dir),
                    ("identity", entry?.Identity ?? facts.Identity), ("sentSaves", sentSaves), ("lastSentTurn", lastSent), ("queuedSaves", qs)));
                var gid = entry?.Identity ?? facts.Identity;
                var choice = gid is null ? null : blocks.GetValueOrDefault(gid);
                string? why = gid is null || !Regex.IsMatch(gid, "^[0-9a-f]{8}-[0-9a-f]{8}$") ? "This recording has no game id, so the site cannot tell which game it is."
                    : now || gid == s.Identity ? "This game is still being played. Block it after the game." : null;
                var g = (JsonObject)list[^1]!;
                g["uploadWaitReason"] = UploadWaitReason();
                var (firstTurn, lastSaved, savedTurns) = TurnsOf(dir);
                g["firstTurn"] = firstTurn; g["lastTurn"] = now ? s.Turn ?? lastSaved : lastSaved; g["savedTurns"] = savedTurns;
                if (list.Count <= 3 || savesShown.Contains(Path.GetFileName(dir)))
                {
                    g["saves"] = SaveList(dir, entry?.Context);
                    g["savesWait"] = SaveWaitReason(entry, mode, streamer?.Status);
                }
                if (entry is not null) g["uploadSchedule"] = UploadSchedule(entry.Context);
                g["blocked"] = choice?.Blocked == true; g["blockConfirmed"] = choice?.Confirmed; g["blockError"] = choice?.LastError;
                g["canBlock"] = why is null; g["blockWhy"] = why; g["context"] = entry?.Context;
                var replay = entry?.Context is { } cx ? ReplayUiState(cx) : null;
                g["replay"] = replay;
                if (replay?.Str("game") is { } key) { g["siteGame"] = key.Split('/')[^1]; g["siteMod"] = replay.Str("mod"); }
                if (!now && replay?["lastTurn"] is { } lastTurn) g["turns"] = lastTurn.DeepClone();
                if (replay?.Str("state") == "deleted") { list.RemoveAt(list.Count - 1); continue; }
                if (replay?.Str("url") is { } url) g["url"] = url;
            }
        }
        catch { }
        foreach (var remote in siteGames.OfType<JsonObject>())
        {
            var url = remote.Str("pageUrl");
            if (url is null) continue;
            var existing = list.OfType<JsonObject>().FirstOrDefault(g => g.Str("url") == url || (g.Str("siteGame") == remote.Str("game") && g.Str("siteMod") == remote.Str("mod")));
            var g = existing ?? J.Obj(("id", "site-" + remote.Str("mod") + "-" + remote.Str("game")), ("mod", remote.Str("mod")),
                ("turns", remote["lastTurn"]?.DeepClone()), ("date", remote["lastSaveAt"]?.DeepClone()), ("players", new JsonArray()), ("status", "public"));
            g["url"] = url; g["siteGame"] = remote.Str("game"); g["siteMod"] = remote.Str("mod");
            g["private"] = remote["private"]?.DeepClone(); g["restrictedUntil"] = remote["restrictedUntil"]?.DeepClone();
            if (existing is null) list.Add(g);
        }
        return new JsonArray(list.OfType<JsonObject>().OrderByDescending(g => DateValue(g["date"])).Select(g => (JsonNode)g.DeepClone()).ToArray());
    }

    static long DateValue(JsonNode? value) => long.TryParse(value?.ToString(), out var n) ? n : DateTimeOffset.TryParse(value?.ToString(), out var t) ? t.ToUnixTimeMilliseconds() : 0;

    void RefreshLibrary()
    {
        if (!NormalServerAllowed) return;
        if (preview || libraryBusy || Environment.TickCount64 < nextLibrary || streamer is null) return;
        libraryBusy = true; nextLibrary = Environment.TickCount64 + 15000;
        _ = Task.Run(async () =>
        {
            try { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await streamer.FlushReplayDeletionsAsync(ct.Token);
                var answer = await streamer.GetReplayLibraryAsync(ct.Token);
                siteGames = deletedCards.Filter(answer["games"] as JsonArray ?? []); nextChecks = 0;
            } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            finally { libraryBusy = false; }
        });
    }

    GameFacts FactsOf(string dir, string file)
    {
        if (!NormalServerAllowed)
            return factsCache.TryGetValue(dir, out var previous) ? previous.Facts : new GameFacts("Recorded game", null, null, [], null, Directory.GetCreationTime(dir));
        var fi = new FileInfo(file);
        if (factsCache.TryGetValue(dir, out var c) && c.Length == fi.Length && c.Time == fi.LastWriteTimeUtc) return c.Facts;
        string? first = null, lastSnapshot = null;
        try
        {
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using (var r = new StreamReader(f, Encoding.UTF8, false, 8192, leaveOpen: true)) first = r.ReadLine();
            // the newest snapshot (every unit and city at a turn start) says who plays: look in the last 2 MB
            long start = Math.Max(0, f.Length - 2 * 1024 * 1024);
            f.Seek(start, SeekOrigin.Begin);
            var tail = new StreamReader(f, Encoding.UTF8).ReadToEnd();
            int at = tail.LastIndexOf("\"kind\":\"snapshot\"", StringComparison.Ordinal);
            if (at >= 0)
            {
                int ls = tail.LastIndexOf('\n', at) + 1, le = tail.IndexOf('\n', at);
                lastSnapshot = tail[ls..(le < 0 ? tail.Length : le)].TrimEnd('\r');
            }
        }
        catch { }
        string? layout = null, identity = null;
        try { var d = JsonNode.Parse(first ?? "{}")?["data"]; layout = d?["mod"]?.GetValue<string>(); identity = d?["identity"]?.GetValue<string>(); } catch { }
        var players = new JsonArray();
        try
        {
            if (lastSnapshot is not null && JsonNode.Parse(lastSnapshot)?["data"] is JsonObject snap)
            {
                int? me = snap["activePlayer"]?.GetValue<int?>();
                var ps = (snap["players"] as JsonArray ?? []).OfType<JsonObject>().Select(p => new PlayerState
                {
                    Id = p["id"]!.GetValue<int>(), Alive = p["alive"]?.GetValue<bool>() ?? false, TurnActive = false,
                    Slot = p["slot"]?.GetValue<int?>(), CivType = p["civType"]?.GetValue<int?>(), Civ = p["civ"]?.GetValue<string?>(),
                });
                players = Players(ps, me);
            }
        }
        catch { }
        var (mod, ver) = ModOf(layout);
        var facts = new GameFacts(mod, ver, JsonlRecording.LastTurn(file), players, identity, RecorderSession.StartedOf(dir) ?? Directory.GetCreationTime(dir));
        factsCache[dir] = (fi.Length, fi.LastWriteTimeUtc, facts);
        return facts;
    }

    // ---- commands ------------------------------------------------------------------------------------------------------
    JsonObject NotInTestBuild => J.Obj(("toast", J.Toast("info", Site ? "Not in this version yet" : "Not in the test build",
        Site ? $"This part comes later. This version records your games and sends them to {ServerShort}, where they become replays." : "This part comes later. The test build records, keeps every game on this PC and streams it to the test server on this PC.")));

    public JsonObject? Handle(string cmd, JsonObject args)
    {
        if (HandleUx(cmd, args, out var uxResult)) return uxResult;
        switch (cmd)
        {
            case "fixAutosave":
            {
                var (changes, backup, error) = AutosaveCheck.Fix(AutosaveCheck.IniPath);
                nextChecks = 0;
                if (error == "civRunning") return J.Obj(("dialog", J.Obj(("kind", "civRunning"))));
                if (error is not null) return J.Obj(("toast", J.Toast("warn", "Could not fix it", error)));
                if (changes.Count == 0) return J.Obj(("toast", J.Toast("good", "Already fine", "Civ already saves every turn.")));
                return J.Obj(("dialog", J.Obj(("kind", "fixed"), ("file", ShortPath(AutosaveCheck.IniPath)), ("backup", Path.GetFileName(backup)),
                    ("changes", J.Arr(changes.Select(c => (JsonNode?)J.Obj(("key", c.Key), ("was", c.Was), ("now", c.Now))))))));
            }
            case "pauseGame":
                if (session.Status.Paused) { session.ResumeGame(); return J.Obj(("toast", J.Toast("good", "Recording this game again", "It goes on in the same folder."))); }
                StopLivestream();
                session.PauseGame();
                return J.Obj(("toast", PausedRecordingToast()));
            case "dontRecord":
                StopLivestream();
                session.PauseGame();
                return J.Obj(("toast", PausedRecordingToast()));
            case "requestLive":
                return J.Obj(("toast", J.Toast("info", "Get your link on Recorder", "While recording a game, choose Get livestream link on Recorder.")));
            case "copyLiveLink":
            {
                var s = session.Status; StreamStatus? st; string mode;
                lock (streamLock) { st = streamer?.Status; mode = StreamMode; }
                var link = LiveLink(s, st, mode);
                if (link["busy"]?.GetValue<bool>() == true) { slots.Request(); return J.Obj(("toast", J.Toast("info", "Livestream requested", "The link will appear in a notification when a live slot is available."))); }
                if (link["url"]?.GetValue<string>() is not { } url) return J.Obj(("toast", J.Toast("warn", "No livestream link yet", link.Str("why") ?? "")));
                try { Clipboard.SetText(url); }
                catch { return J.Obj(("toast", J.Toast("warn", "Could not copy invite", "Open the stream lobby and copy its address from your browser."))); }
                var t = J.Toast("good", "Invite link copied", "Ready to share. Anyone with this private link can watch; it is not listed publicly.");
                t["actions"] = new JsonArray(J.Obj(("label", "Open in browser"), ("cmd", "openSite"), ("args", J.Obj(("url", url))), ("primary", true)));
                t["timeout"] = 15000;
                return J.Obj(("toast", t), ("copied", url));
            }
            case "setSetting":
            {
                var key = args.Str("key") ?? "";
                if (key is "trackLogs" or "captureMoves") return Handle("setMemoryLogging", J.Obj(("enabled", args["value"]?.DeepClone())));
                if (fileSettings.SafeCapture && key is "backupUnsupported" or "keepCopies") return J.Obj(("toast", J.Toast("info", "Conservative file handling", "This profile keeps local save copies and uses verified capture paths.")));
                if (key == "updates.autoSmall") return NotInTestBuild;
                if (key == "upload") return J.Obj(("toast", J.Toast("info", Site ? "Use the switch above" : "Not in the test build", Site ? $"\"Send my games to {ServerShort}\" turns sending on and off." : "Sending to ffaciv.com comes later. The test build streams to the test server on this PC (\"Live stream to the test server\").")));
                var boundary = StreamingSetupPolicy.ChangeReason(ui["settings"]!.AsObject(), key, args["value"], UploadPolicy.CivRunning(), session.Status.Lobby is not null);
                if (boundary is not null) return J.Obj(("toast", J.Toast("info", "Prepare before your game", boundary)));
                var parts = key.Split('.');
                var node = ui["settings"]!.AsObject();
                foreach (var p in parts[..^1]) node = node[p]!.AsObject();
                node[parts[^1]] = args["value"]?.DeepClone();
                if (key == "startWithWindows") { try { StartWithWindows = args["value"]?.GetValue<bool>() ?? false; } catch (Exception e) { return J.Obj(("toast", J.Toast("warn", "Could not change it", e.Message))); } ui["settings"]!["startWithWindows"] = StartWithWindows; }
                if (key == "recording")
                {
                    session.RecordingEnabled = !SetupPending && Setting("recording");
                    if (session.RecordingEnabled) session.ResumeGame();
                    else StopLivestream();
                }
                if (key is "recording" or "trackLogs" or "uploadTiming" or "stream") { ApplyCaptureSettings(); nextChecks = 0; }
                if (key == "diagnostics") diagnostics.Enabled = Setting("diagnostics", false);
                if (key == "backupUnsupported") session.BackupUnsupportedEnabled = Setting("backupUnsupported");
                if (key == "captureMoves") { ui["settings"]!["trackLogs"] = Setting("captureMoves"); ApplyCaptureSettings(); nextChecks = 0; }
                if (key == "recordSinglePlayer") session.RecordSinglePlayer = Setting("recordSinglePlayer", false);
                if (key == "looksPerSecond") { session.SampleMs = session.CaptureMoves ? 1000 / LooksPerSecond() : 1000; nextChecks = 0; }
                SaveUi();
                if (key is "stream" or "keepCopies") { RestartStream(); nextChecks = 0; }
                return null;
            }
            case "finishFirstRun":
                Onboarding.Complete(ui);
                session.RecordingEnabled = Setting("recording");
                ApplyCaptureSettings();
                try { StartWithWindows = Setting("startWithWindows"); } catch { }
                SaveUi();
                Start();
                return J.Obj(("toast", J.Toast("good", "Setup complete", "Your choices are saved. Revisit setup from Options whenever you like.")));
            case "openFolder":
            {
                var path = args.Str("path") ?? FolderOf(args.Str("game")) ?? fileSettings.OutputFolder;
                if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return null;
            }
            case "openSite":
                WebUi.OpenExternal(args.Str("url") ?? "https://ffaciv.com");
                return null;
            case "editSettings":
                try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{settingsTxt}\"") { UseShellExecute = true }); } catch { }
                return J.Obj(("toast", J.Toast("info", "settings.txt opened", "Changes there are used after the recorder is started again (tray icon → Quit, then start it).")));
            case "sendReport":
                if (Interlocked.Exchange(ref reportBusy, 1) != 0)
                    return J.Obj(("toast", J.Toast("info", "Your report is already being prepared", "The recorder will confirm when the local file is ready.")));
                var reportArgs = (JsonObject)args.DeepClone();
                Task.Run(() =>
                {
                    try { if (SaveReport(reportArgs)["toast"] is JsonObject toast) Toast?.Invoke(toast); }
                    finally { Interlocked.Exchange(ref reportBusy, 0); }
                });
                return J.Obj(("toast", J.Toast("info", "Preparing your local report", "It will be saved on your Desktop. Nothing is uploaded.")));
            case "scanOldGames": oldGames.Scan(); return null;
            case "recordAnyway":
                session.RecordThisSinglePlayerGame();
                return J.Obj(("toast", J.Toast("good", "Recording this single-player game", "Only this one. Single-player games stay off in Settings.")));
            case "checkUpdates": releases.Check(); return null;
            case "updateNow": WebUi.OpenExternal(releases.DownloadUrl); return null;
            case "updateLater": releases.Later(args.Str("when") ?? "tomorrow"); return null;
            case "deleteGame":
            {
                var game = games.OfType<JsonObject>().FirstOrDefault(g => g.Str("id") == args.Str("game"));
                var context = game?.Str("context"); var identity = game?.Str("identity");
                if (context is null && game?.Str("siteGame") is { } siteGame && streamer is { } knownSender)
                {
                    var known = new GameIndex(fileSettings.OutputFolder).Load().LastOrDefault(e => knownSender.ReadReplay(e.Context).Str("game") == game.Str("siteMod") + "/" + siteGame);
                    if (known is not null) { context = known.Context; identity = known.Identity; }
                }
                if (args.Str("game") == "live")
                {
                    context = session.Status.Context ?? args.Str("context");
                    identity = session.Status.Identity ?? new GameIndex(fileSettings.OutputFolder).Load().LastOrDefault(e => e.Context == context)?.Identity;
                    if (identity is not null) session.PauseGame();
                }
                if (game is null && context is null) return J.Obj(("toast", J.Toast("warn", "Game no longer found", "Refresh your games and try again.")));
                // Older recorder builds left session folders without games.json entries or server IDs.
                // They are local copies, so there is no website withdrawal to queue.
                if (context is null && game?.Str("siteGame") is null)
                {
                    var folder = game?.Str("folder");
                    if (folder is null) return J.Obj(("toast", J.Toast("warn", "Game no longer found", "Refresh your games and try again.")));
                    LocalRecordingDeletion.Delete(fileSettings.OutputFolder, folder);
                    if (identity is not null) session.DeleteGameIdentity(identity);
                    factsCache.Remove(folder);
                    deletedCards.Remember(game, context, identity);
                    games = deletedCards.Filter(games); siteGames = deletedCards.Filter(siteGames);
                    nextLibrary = nextChecks = 0;
                    return J.Obj(("toast", J.Toast("good", "Local recording deleted", "Removed this recorder's copy. Civ's original saves were kept.")));
                }
                if (streamer is not { } sender) return J.Obj(("toast", J.Toast("warn", "Recorder is starting", "Try again in a moment.")));
                bool current = identity is not null && identity == session.Status.Identity && session.Status.Recording;
                // held for 7 days (the user, 2026-10-31: "a way to regret deleting replays"): hidden on the site now, Undo
                // brings it back with its upload choice and visibility; the real withdrawal comes when the hold ends
                var card = HeldCard(game, context, identity);
                if (context is not null)
                {
                    var before = delayedUploads.Get(context);
                    card["uploadDecision"] = J.Obj(("state", before.State), ("endedAt", before.EndedAt is { } e ? J.Ms(e) : null),
                        ("dueAt", before.DueAt is { } du ? J.Ms(du) : null), ("postponed", before.Postponed), ("endPrompted", before.EndPrompted), ("hold", before.Hold));
                }
                if (identity is not null) session.DeleteGameIdentity(identity);
                if (context is not null) delayedUploads.KeepLocal(context);
                sender.QueueReplayDeletion(context, game?.Str("siteMod"), game?.Str("siteGame"), waitForEnd: current, hold: Streamer.DeletionHold, card: card,
                    wasPrivate: game?["private"]?.GetValue<bool>() == true || game?["replay"]?["private"]?.GetValue<bool>() == true);
                deletedCards.Remember(game, context, identity);
                games = deletedCards.Filter(games); siteGames = deletedCards.Filter(siteGames);
                nextLibrary = nextChecks = 0;
                _ = Task.Run(async () => { try {
                    using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await sender.FlushReplayDeletionsAsync(ct.Token);
                    nextLibrary = nextChecks = 0; }
                    catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { /* the regular deletion pass hides it later */ } });
                var deleted = J.Toast("info", "Recording deleted · Undo for 7 days", "Hidden from your list and from the website now. It is erased for good in 7 days; until then Undo brings it back (also under Previous games). Civ's original saves are kept.");
                deleted["actions"] = new JsonArray(J.Obj(("label", "Undo"), ("cmd", "undoDelete"), ("args", J.Obj(("key", context is null ? $"game:{game?.Str("siteMod")}/{game?.Str("siteGame")}" : "recording:" + context))), ("primary", true)));
                deleted["timeout"] = 15000;
                return J.Obj(("toast", deleted));
            }
            case "undoDelete": return UndoDelete(args.Str("key"));
            case "showSaves":
                if (args.Str("game") is { } shown) { if (savesShown.Count >= 5) savesShown.Clear(); savesShown.Add(shown); nextChecks = 0; }
                return null;
            case "completeGame":
            case "keepShortGame":
            case "replayChoice":
            {
                var game = games.OfType<JsonObject>().FirstOrDefault(g => g.Str("id") == args.Str("game"));
                var context = args.Str("context") ?? game?.Str("context") ?? session.Status.Context;
                if (streamer is not { } sender) return J.Obj(("toast", J.Toast("warn", "Sending is off", "Switch sending on in Settings first.")));
                var choice = new JsonObject();
                if (cmd == "completeGame") { choice["action"] = "complete"; choice["keepShort"] = true; if (session.Status.Context == context) { choice["waitForEnd"] = true; session.PauseGame(); } }
                // finished and uploaded = public, unless the player chose privacy by hand (the user, 2026-11-01)
                if (cmd == "completeGame" && context is not null && EndPrivateWhilePlaying(context)) choice["private"] = false;
                if (cmd == "keepShortGame") choice["keepShort"] = true;
                if (cmd == "replayChoice") foreach (var key in new[] { "private", "restricted" }) if (args[key] is { } v) choice[key] = v.DeepClone();
                if (cmd == "replayChoice" && args["private"] is not null && context is not null) { EndPrivateWhilePlaying(context); delayedUploads.EndHold(context); }
                if (game?.Str("siteGame") is { } siteGame && cmd == "replayChoice")
                    _ = Task.Run(async () => { try { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await sender.SetReplayAccessAsync(game.Str("siteMod")!, siteGame, choice, ct.Token); nextLibrary = 0; }
                        catch (Exception e) { Toast?.Invoke(J.Toast("warn", "Access change not saved", e.Message)); } });
                else if (context is not null)
                {
                    sender.ChooseReplay(context, choice);
                    if (cmd == "replayChoice") _ = Task.Run(async () => {
                        try { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await sender.ApplyReplayChoiceAsync(context, choice, ct.Token); nextChecks = 0; }
                        catch (Exception e) { Toast?.Invoke(J.Toast("warn", "Access change queued", e.Message)); }
                    });
                }
                else return J.Obj(("toast", J.Toast("warn", "Not received yet", "Wait for the game to reach the server.")));
                nextChecks = 0;
                return J.Obj(("toast", J.Toast("info", cmd == "completeGame" ? "Finishing this game" : "Choice queued", cmd == "completeGame" ? "Recording kept, including games under 50 turns. Capture stops for this recording; finishing waits for upload permission and server acknowledgement. Replay visibility is unchanged." : "The server confirms the change in the game card.")));
            }
            case "setBlocked":
            {
                var g = games.OfType<JsonObject>().FirstOrDefault(x => x.Str("id") == args.Str("game"));
                if (g is null) return null;
                if (g["canBlock"]?.GetValue<bool>() != true) return J.Obj(("toast", J.Toast("info", "Not now", g.Str("blockWhy") ?? "")));
                bool blocked = args["blocked"]?.GetValue<bool>() ?? true;
                visibility.Set(g.Str("identity")!, g.Str("context"), blocked);
                nextChecks = 0;
                return J.Obj(("toast", J.Toast(blocked ? "info" : "good", blocked ? "Game blocked" : "Game unblocked",
                    blocked ? "Hidden from everyone on the site. Unblock brings it back." : "It shows on the site again.")));
            }
            case "addOldFolder":
            {
                using var pick = new FolderBrowserDialog { Description = "A folder with old Civ V saves (its subfolders and zip files are looked at too)", UseDescriptionForTitle = true };
                if (pick.ShowDialog() == DialogResult.OK) oldGames.AddFolder(pick.SelectedPath);
                return null;
            }
            case "uploadOldGames":
            {
                var ids = (args["ids"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>().ToList() ?? [];
                if (ids.Count == 0) return J.Obj(("toast", J.Toast("info", "No game picked", "Tick the games to upload. Only whole games are uploaded.")));
                if (oldGames.Upload(ids) is { } why) return J.Obj(("toast", J.Toast("warn", "Not uploading", why)));
                return J.Obj(("toast", J.Toast("good", ids.Count == 1 ? "Uploading 1 game" : $"Uploading {ids.Count} games", Site ? $"To {ServerShort}, where they become replays." : "To the test server on this PC. Nothing is public in the test build.")));
            }
            case "stopOldUpload": oldGames.Stop(); return null;
            case "dismissOldHint": ui["oldHintDismissed"] = true; SaveUi(); return null;
            case "chooseFolder":
                return J.Obj(("toast", J.Toast("info", "Games folder", "Changing the folder comes later. It is set in settings.txt (output_folder) for now.")));
            default:
                return NotInTestBuild;
        }
    }

    string? FolderOf(string? game) => game is null ? null : game == "live" ? session.Status.Folder : Path.Combine(fileSettings.OutputFolder, game);

    static string ShortPath(string p)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return p.StartsWith(docs, StringComparison.OrdinalIgnoreCase) ? "Documents" + p[docs.Length..] : p;
    }

    /// <summary>The problem report as a zip on the Desktop (sending it comes later): only the parts the player ticked.</summary>
    JsonObject SaveReport(JsonObject args)
    {
        var parts = (args["parts"] as JsonArray)?.Select(p => p?.GetValue<string>()).ToHashSet() ?? [];
        var zipPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"FFACiv Recorder report {DateTime.Now:yyyy-MM-dd HHmmss}-{Guid.NewGuid():N}.zip");
        string? diagnosticPath = null;
        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            var s = session.Status;
            var sb = new StringBuilder();
            sb.AppendLine($"FFACiv Recorder {version}, {DateTime.Now:o}");
            sb.AppendLine($"What happened: {args.Str("text")}");
            sb.AppendLine($"Capture: {(session.TrackLogs ? $"Experimental external memory logs ({LooksPerSecond()}/s)" : "Save files only; memory reading disabled")}; recording: {s.Recording}; paused: {s.Paused}");
            StreamStatus? st; lock (streamLock) st = streamer?.Status;
            sb.AppendLine($"Uploads: {StreamMode}; completed event bytes: {st?.EventWireBytes}; completed save bytes: {st?.SaveWireBytes}");
            sb.AppendLine($"Resource policy: {workload.Reason}; upload limit: {fileSettings.UploadKilobytesPerSecond} KiB/s; requested hard CPU ceiling: {fileSettings.CpuLimitPercent}% (0 = off)");
            sb.AppendLine($"Hard CPU ceiling: {(cpuLimit is not null ? "active" : cpuLimitError is not null ? "unavailable; adaptive controls remain active" : "off")}");
            sb.AppendLine("Crash cause: undetermined. Low recorder resource use does not prove or exclude involvement.");
            if (parts.Contains("pc")) sb.AppendLine($"Windows: {Environment.OSVersion} | 64-bit: {Environment.Is64BitOperatingSystem} | Civ settings file: {File.Exists(AutosaveCheck.IniPath)}");
            bool attachmentsAllowed = workload.CanStartHeavyWork && (!fileSettings.SafeCapture || uploadPolicy.IsIdle());
            if (!attachmentsAllowed) sb.AppendLine("Game/save attachments omitted while Civ or the computer is busy. Resource diagnostics remain available.");
            using (var w = new StreamWriter(zip.CreateEntry("report.txt").Open())) w.Write(sb.ToString());
            if (parts.Contains("log"))
            {
                diagnosticPath = Path.Combine(Path.GetTempPath(), $"ffaciv-diagnostic-{Guid.NewGuid():N}.txt");
                diagnostics.Export(diagnosticPath);
                zip.CreateEntryFromFile(diagnosticPath, "session-health.txt");
            }
            var last = Directory.Exists(fileSettings.OutputFolder) ? Directory.GetDirectories(fileSettings.OutputFolder, "session-*").OrderByDescending(d => d).FirstOrDefault() : null;
            if (attachmentsAllowed && parts.Contains("session") && last is not null && File.Exists(Path.Combine(last, "events.jsonl")))
            {
                using var src = new FileStream(Path.Combine(last, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = zip.CreateEntry("last-game/events.jsonl").Open();
                src.CopyTo(dst);
            }
            var backup = Directory.Exists(fileSettings.OutputFolder) ? Directory.GetDirectories(fileSettings.OutputFolder, "unsupported-*").OrderByDescending(d => d).FirstOrDefault() : null;
            if (attachmentsAllowed && backup is not null && parts.Contains("session"))
                foreach (var name in new[] { "diagnostics.jsonl", "backup.json" })
                {
                    var file = Path.Combine(backup, name);
                    if (File.Exists(file))
                    {
                        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var dst = zip.CreateEntry("unsupported-backup/" + name).Open(); src.CopyTo(dst);
                    }
                }
            if (attachmentsAllowed && parts.Contains("save") && (last ?? backup) is { } saveFolder && Streamer.SavesOf(saveFolder).LastOrDefault() is { } save)
                zip.CreateEntryFromFile(save, "last-game/" + Path.GetFileName(save));
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
            return J.Obj(("toast", J.Toast("good", "Report saved on your Desktop", "Attach this zip when asking for help. It has not been sent automatically.")));
        }
        catch (Exception e) { return J.Obj(("toast", J.Toast("warn", "Could not make the report", e.Message))); }
        finally { if (diagnosticPath is not null) { try { File.Delete(diagnosticPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }
    int reportBusy;

    // ---- start with Windows (HKCU Run, no admin) -------------------------------------------------------------------------
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "FFACiv Recorder";
    /// <summary>Windows' own start list for this user: the program starts hidden by the clock (--tray).</summary>
    static bool StartWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) is string; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray"); else k.DeleteValue(RunName, false);
        }
    }
}
