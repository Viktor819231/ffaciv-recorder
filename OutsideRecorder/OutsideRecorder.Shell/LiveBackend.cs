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
    JsonArray checks = [];
    JsonArray games = [];
    readonly RecordingDeletionCache deletedCards = new();
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
        // After the game means Civ closed (the user, 2026-11-09: "upload when civ is not running"); Civ's main menu still
        // counts as not playing for uploads while playing, which only hold older saves back for the livestream.
        uploadPolicy = new UploadPolicy { ExitGrace = TimeSpan.FromSeconds(5),
            IsPlaying = () => UploadPolicy.CivRunning() && (uploadPolicy!.AfterPlaying || !session.Status.HasRecentMenuObservation(Environment.TickCount64)) };
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
            UploadSettings = () => fileSettings with { SaveUpload = CapturePreferences.OldGamesFormat(ui["settings"]!.AsObject()) },
        };
    }

    /// <summary>The live slot of the game being recorded (OR05 "busy line").</summary>
    readonly SlotKeeper slots;
    /// <summary>Block / Unblock of finished games (OR03 "Visibility").</summary>
    readonly Visibility visibility;
    /// <summary>"Upload old games" (OR03 "Bulk upload").</summary>
    readonly OldGamesService oldGames;
    readonly ReleaseNotifications releases;

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

}
