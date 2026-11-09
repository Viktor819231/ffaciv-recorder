using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OutsideRecorderCore;

/// <summary>What the window shows; replaced as a whole after each sample.</summary>
public sealed record RecorderStatus
{
    /// <summary>One plain line: what the recorder is doing.</summary>
    public string State { get; init; } = "Starting";
    /// <summary>The save-only poll detected Civ without opening its memory.</summary>
    public bool CivDetected { get; init; }
    /// <summary>Fresh verified main-menu observation (game not started, no staging room: a lobby counts as playing, the user
    /// 2026-11-02); no-game/loading text alone is not proof of a menu.</summary>
    public long? MenuObservedAt { get; init; }
    public bool HasRecentMenuObservation(long now) => MenuObservedAt is long seen && now >= seen && now - seen <= 2500;
    /// <summary>Civ process + game core, or null.</summary>
    public string? Game { get; init; }
    /// <summary>Current turn, or null.</summary>
    public int? Turn { get; init; }
    /// <summary>The local player's own units in the last sample (never other players': no help for cheating).</summary>
    public int MyUnits { get; init; }
    /// <summary>The local player's own cities in the last sample.</summary>
    public int MyCities { get; init; }
    /// <summary>"single player", "multiplayer", "hot seat" or null.</summary>
    public string? Mode { get; init; }
    /// <summary>Lines written to the recording.</summary>
    public long Events { get; init; }
    /// <summary>Tape size so far (compressed bytes on disk), or null when tape mode is off.</summary>
    public long? TapeBytes { get; init; }
    /// <summary>Recording folder, or null.</summary>
    public string? Folder { get; init; }
    /// <summary>Last autosave noticed.</summary>
    public string? LastAutosave { get; init; }
    /// <summary>Last sample took this long (ms).</summary>
    public double LastSampleMs { get; init; }
    /// <summary>Average time of one look (ms, the last few seconds).</summary>
    public double LookMs { get; init; }
    /// <summary>Looks per second asked for (Settings).</summary>
    public double LooksPerSecondWanted { get; init; }
    /// <summary>Looks per second really made: fewer when one look takes long, so the recorder stays under ~10 % of one core.</summary>
    public double LooksPerSecond { get; init; }
    /// <summary>True while writing a recording.</summary>
    public bool Recording { get; init; }
    /// <summary>A confirmed staging room replaces the previous game's menu state during a rehost.</summary>
    public RecorderStatus InLobby(LobbySnapshot lobby) => this with
    {
        Recording=false, Identity=null, Context=null, Folder=null, Turn=null, Players=[], ActivePlayer=null,
        GameStarted=null, LastSaveAt=null, LastAutosave=null, MyUnits=0, MyCities=0, Events=0,
        TapeBytes=null, Paused=false, SinglePlayerSkipped=false, Mode=null, Lobby=lobby,
        State="Civ found; waiting for a game",
    };

    /// <summary>Civ is gone (closed, crashed or let go): nothing it showed stays on screen, the staging room included
    /// (a game that ended in the lobby and then Civ closed kept a "Multiplayer lobby" card for hours, 2026-11-08).</summary>
    public RecorderStatus CivGone() => this with
    {
        CivDetected=false, State="Waiting for Civilization V to start", Recording=false, Game=null, MyUnits=0, MyCities=0,
        Layout=null, Identity=null, Players=[], ActivePlayer=null, Lobby=null, MenuObservedAt=null,
    };

    /// <summary>The in-game FFACiv Recorder is installed or loaded (a warning is shown).</summary>
    public bool InGameRecorderPresent { get; init; }
    /// <summary>Last unexpected error (the recorder keeps going), or null.</summary>
    public string? LastError { get; init; }
    /// <summary>A problem with Civ's autosave settings (not every turn), or null.</summary>
    public string? AutosaveWarning { get; init; }
    /// <summary>The recording id (32 hex) of the game being recorded, or null.</summary>
    public string? Context { get; init; }
    /// <summary>The game's identity (its seeds, <see cref="GamePipeline.IdentityOf"/>) while one is open, or null.</summary>
    public string? Identity { get; init; }
    /// <summary>The game core's name ("Snowve 8.2.1", "LEKMOD v35.4.003" …) while Civ is read, or null.</summary>
    public string? Layout { get; init; }
    /// <summary>The players of the open game as last read (major civs and city-states, not the barbarians); empty when none.</summary>
    public IReadOnlyList<PlayerState> Players { get; init; } = [];
    /// <summary>The player sitting at this PC (the game's active player), or null.</summary>
    public int? ActivePlayer { get; init; }
    /// <summary>When this game's recording folder was made (its first recording), or null.</summary>
    public DateTimeOffset? GameStarted { get; init; }
    /// <summary>When the last autosave of this game was copied, or null.</summary>
    public DateTimeOffset? LastSaveAt { get; init; }
    /// <summary>The player switched recording off for this game (or for all games).</summary>
    public bool Paused { get; init; }
    /// <summary>The multiplayer staging room open now (before the game starts), or null.</summary>
    public LobbySnapshot? Lobby { get; init; }
    /// <summary>This install's channel id (OR03 "Lobby / channel"), or null before the recorder started.</summary>
    public string? Channel { get; init; }
    /// <summary>A single-player game is open and not recorded (single player is off in Settings; "Record it anyway" records it).</summary>
    public bool SinglePlayerSkipped { get; init; }
    /// <summary>Unsupported builds retain local autosaves/diagnostics, never guessed realtime events.</summary>
    public bool UnsupportedBackingUp { get; init; }
    public bool UnsupportedKek { get; init; }
    public string? UnsupportedFolder { get; init; }
    public int UnsupportedSaves { get; init; }
}

/// <summary>
/// The recorder loop: looks for Civ, reads it read-only every few hundred ms, writes events (+ tape), notices autosaves.
/// One recording folder per game (identity = the game's own seeds); the same game coming back (reload, rejoin, Civ or
/// recorder restarted) continues its folder, its tape and its stream.
/// Runs on its own background thread; never throws into the caller.
/// </summary>
public sealed class RecorderSession : IDisposable
{
    readonly RecorderSettings settings;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly TurnQuietWindow turnQuiet;
    /// <summary>Only verified lightweight headers are read during the experimental five-second new-turn pause.</summary>
    public bool TurnTransitionQuiet => ExperimentalMemoryCapture && turnQuiet.IsQuiet;
    readonly AutosaveWatcher autosaves;
    readonly SaveOnlyCapture saveOnly;
    volatile bool trackLogs;
    volatile bool allowExperimentalMemoryLogging;
    bool ExperimentalMemoryCapture => settings.MemoryTest || allowExperimentalMemoryLogging;
    /// <summary>Explicit runtime opt-in. Permission alone does not start memory logging; TrackLogs must also be enabled.</summary>
    public bool AllowExperimentalMemoryLogging
    {
        get => allowExperimentalMemoryLogging;
        set
        {
            if (value && !allowExperimentalMemoryLogging) { SampleMs = 250; turnQuiet.Reset(); }
            allowExperimentalMemoryLogging = value;
            if (!value && settings.SafeCapture) trackLogs = false;
            if (!value) turnQuiet.Reset();
        }
    }
    bool filesRunning;
    volatile bool filesPaused;
    /// <summary>Off never opens or reads process memory, including lobby and layout checks.</summary>
    public bool TrackLogs
    {
        get => trackLogs && (!settings.SafeCapture || allowExperimentalMemoryLogging);
        set
        {
            trackLogs = value && (!settings.SafeCapture || allowExperimentalMemoryLogging);
            if (!trackLogs) turnQuiet.Reset();
        }
    }
    /// <summary>Shared resource budget; a pause never opens Civ or starts a new save copy.</summary>
    public WorkloadPolicy? Workload { get; set; }
    /// <summary>Local-only activity and error history; it never enables capture or uploads.</summary>
    public SessionDiagnostics? Diagnostics { get; set; }
    /// <summary>Stable files waiting for another copy attempt; uncopied source files remain owned by Civ.</summary>
    public int PendingAutosaves => copyLater.Count;
    volatile bool hasPendingAutosaves;
    /// <summary>Capture-thread snapshot covering settling, held/retrying saves and a copy in progress.</summary>
    public bool HasPendingAutosaves => hasPendingAutosaves;
    readonly CancellationTokenSource stop = new();
    Thread? thread;

    ProcessMemory? mem;
    TapingMemory? taping;
    StateReader? reader;
    FoundGame? found;
    JsonlRecording? rec;
    UnsupportedBackup? unsupported;
    int unsupportedProcess;
    public bool BackupUnsupportedEnabled { get; set; } = true;
    TapeWriter? tape;
    GamePipeline? pipe;
    string? identity;
    GameEntry? entry;
    readonly GameIndex index;
    readonly DateTimeOffset recorderStarted = DateTimeOffset.Now;
    long sampleUs, lastIndexWriteMs;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> deletedIdentities = new();
    string DeletedFile => Path.Combine(settings.OutputFolder, "deleted-games.json");
    volatile string? pausedIdentity;
    volatile bool recordingOff;
    /// <summary>Savefile-only mode keeps session identity and autosaves without a movement log.</summary>
    public bool CaptureMoves { get; set; } = true;
    volatile bool pauseCurrent;
    volatile bool recordSinglePlayer = true;
    volatile string? singlePlayerAllowed, singlePlayerTold;
    int lastRefusedPid = -1;
    string? refusedHash, refusedReason;
    bool warnedInGameRecorder;
    Channel? channel;
    long nextLobbyLookMs;
    /// <summary>Requests the next permitted lobby observation without enabling memory access or bypassing resource limits.</summary>
    public void RequestLobbyRefresh() => Interlocked.Exchange(ref nextLobbyLookMs, 0);
    /// <summary>
    /// Until this time only the globals are read (no players, units, cities, tiles): right after Civ is found and after any
    /// look that found no running game (menus, loading), the game must look the same for <see cref="QuietMs"/> first.
    /// </summary>
    long quietUntilMs, nextHeartbeatMs;
    (int Turn, int W, int H) quietSeen = (-1, -1, -1);
    /// <summary>How long a (re)loaded game must look the same before its objects are read.</summary>
    public const int QuietMs = 3000;

    /// <summary>Latest status for the window.</summary>
    public RecorderStatus Status { get; private set; } = new();
    /// <summary>Raised (on the recorder thread) for things worth a pop-up: title, text.</summary>
    public event Action<string, string>? Notice;
    /// <summary>The Civ process being read, for the Snapshot button (null when none).</summary>
    public FoundGame? Attached => found;

    /// <summary>
    /// "Pause for this game": stops writing the game that is open now (its folder is closed with state "paused") until
    /// <see cref="ResumeGame"/>; another game is recorded as usual. Any thread.
    /// </summary>
    public void PauseGame() => pauseCurrent = true;
    /// <summary>Deleted games stay stopped across restarts; another game is recorded normally.</summary>
    public void DeleteGameIdentity(string id)
    {
        deletedIdentities[id] = true;
        PersistDeletedIdentities();
        if (Status.Identity == id) PauseGame();
    }
    /// <summary>Undo of a held deletion: the game is recorded and uploaded again like any other.</summary>
    public void RestoreGameIdentity(string id)
    {
        if (!deletedIdentities.TryRemove(id, out _)) return;
        PersistDeletedIdentities();
    }
    readonly object deletedFileLock = new();
    /// <summary>Writes deleted-games.json. Deleting and restoring come from different threads: the list is taken inside the
    /// lock, so an older list can never replace a newer one.</summary>
    void PersistDeletedIdentities()
    {
        lock (deletedFileLock)
        {
            Directory.CreateDirectory(settings.OutputFolder);
            SafeFiles.Replace(DeletedFile, System.Text.Json.JsonSerializer.Serialize(deletedIdentities.Keys.ToArray()));
        }
    }
    /// <summary>Whether the player deliberately deleted this game's recording.</summary>
    public bool IsDeletedIdentity(string? id) => id is not null && deletedIdentities.ContainsKey(id);
    double lookAvg;
    volatile int sampleMs;
    /// <summary>The share of one core the looks may use before the recorder slows down by itself.</summary>
    public const double MaxBusy = 0.10;
    /// <summary>The longest wait between looks: slowing down must never mean not looking, Civ is found within this time.</summary>
    public const int MaxIntervalMs = 30_000;
    /// <summary>A "look" longer than this was no look: the PC slept during it and the clock kept counting.</summary>
    public const double LongestLookMs = 10_000;
    /// <summary>Milliseconds between looks asked for (50 = 20 a second; 50..5000). Any thread.</summary>
    public int SampleMs { get => sampleMs; set => sampleMs = Math.Clamp(value, 50, 5000); }

    /// <summary>The pace a look of <paramref name="lookMs"/> allows: as asked, or slower so looking stays under <see cref="MaxBusy"/>,
    /// up to <see cref="MaxIntervalMs"/>.</summary>
    public static int IntervalFor(int wantedMs, double lookMs) => Math.Max(wantedMs, (int)Math.Ceiling(Math.Min(lookMs / MaxBusy, MaxIntervalMs)));

    /// <summary>The running average of what a look costs, leaving out anything over <see cref="LongestLookMs"/>: one "look" that
    /// lasted a 9-hour PC sleep made the recorder wait 4 hours between looks and miss a whole game (2026-11-07).</summary>
    public static double NextLookAverage(double average, double tookMs) =>
        tookMs > LongestLookMs ? average : average <= 0 ? tookMs : average * 0.95 + tookMs * 0.05;

    /// <summary>Records the paused game again (it continues its own folder).</summary>
    public void ResumeGame() { pausedIdentity = null; pauseCurrent = false; filesPaused = false; }
    /// <summary>Single-player games are recorded (the window's "Record single-player games"; multiplayer and hot seat always are).</summary>
    public bool RecordSinglePlayer { get => recordSinglePlayer; set => recordSinglePlayer = value; }
    /// <summary>Records the single-player game open now although single player is off ("Record it anyway").</summary>
    public void RecordThisSinglePlayerGame() => singlePlayerAllowed = Status.Identity;
    /// <summary>Off = record no game at all (the window's "Record my games" switch). Any thread.</summary>
    public bool RecordingEnabled { get => !recordingOff; set => recordingOff = !value; }

    /// <summary>The in-game recorder's folder, if it is installed next to the game (checked at start).</summary>
    public static string? InstalledInGameRecorder(string? civFolder = null)
    {
        civFolder ??= @"C:\Program Files (x86)\Steam\steamapps\common\Sid Meier's Civilization V";
        var dir = Path.Combine(civFolder, "Assets", "DLC", "FFACiv_Recorder");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Prepares the recorder; call <see cref="Start"/> to run it.</summary>
    public RecorderSession(RecorderSettings settings, Func<DateTime>? utcNow = null)
    {
        this.settings = settings;
        turnQuiet = new TurnQuietWindow(5000, () => clock.ElapsedMilliseconds);
        trackLogs = settings.TrackLogs && !settings.SafeCapture;
        saveOnly = new SaveOnlyCapture(settings.OutputFolder);
        SampleMs = settings.SampleMs;
        autosaves = new AutosaveWatcher(settings.SaveFolders, settleFor:settings.SafeCapture || settings.MemoryTest ? TimeSpan.FromSeconds(5):TimeSpan.Zero, utcNow:utcNow);
        index = new GameIndex(settings.OutputFolder);
        RestorePendingCopies();
        UpdatePendingAutosaves();
        try { if (File.Exists(DeletedFile)) foreach (var id in System.Text.Json.JsonSerializer.Deserialize<string[]>(SafeFiles.ReadAllText(DeletedFile)) ?? []) deletedIdentities[id] = true; } catch { }
    }

    /// <summary>False = never look for Civ (tests: a test must not read a Civ that happens to run on the PC).</summary>
    public bool FindCiv { get; init; } = true;

    /// <summary>Starts the background thread.</summary>
    public void Start()
    {
        if (InstalledInGameRecorder() is { } dir)
        {
            Status = Status with { InGameRecorderPresent = true };
            warnedInGameRecorder = true;
            Notice?.Invoke("In-game recorder is installed", InGameRecorderAdvice(dir));
        }
        if (CheckAutosaveSettings(UserSettingsPath()) is { } warning)
        {
            Status = Status with { AutosaveWarning = warning };
            Notice?.Invoke("Civ's autosave setting", warning);
        }
        thread = new Thread(Loop) { IsBackground = true, Name = "OutsideRecorder" };
        thread.Start();
    }

    /// <summary>Civ's UserSettings.ini (read only).</summary>
    public static string UserSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sid Meier's Civilization 5", "UserSettings.ini");

    /// <summary>
    /// Reads [AutoSave] TurnsBetweenAutosave / NumAutosavesKept (never writes). A warning when not every turn is saved or
    /// too few are kept; null when fine or when the file cannot be read.
    /// </summary>
    public static string? CheckAutosaveSettings(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            int? between = null, kept = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var m = Regex.Match(raw, @"^\s*(TurnsBetweenAutosave|NumAutosavesKept)\s*=\s*(-?\d+)");
                if (!m.Success) continue;
                if (m.Groups[1].Value == "TurnsBetweenAutosave") between = int.Parse(m.Groups[2].Value); else kept = int.Parse(m.Groups[2].Value);
            }
            if (between is int b && b != 1)
                return $"Civ saves only every {b} turns (TurnsBetweenAutosave = {b} in UserSettings.ini). For a full replay set it to 1 " +
                       "(in Civ: Options > Game > Turns between autosaves), with Civ closed.";
            if (kept is int k && k < 10)
                return $"Civ keeps only {k} autosaves (NumAutosavesKept = {k}). The recorder copies each one as it appears, so this is fine while it runs.";
            return null;
        }
        catch { return null; }
    }

    static string InGameRecorderAdvice(string dir) =>
        $"For this test please close Civ and move the folder\n{dir}\nout of Assets\\DLC (for example to the Desktop). " +
        "Then Civ runs without anything inside it. The Outside Recorder works either way.";

    void Loop()
    {
        long nextAutosavePoll = 0, nextSearch = 0;
        while (!stop.IsCancellationRequested)
        {
            var t0 = clock.Elapsed;
            try
            {
                if (!TrackLogs || recordingOff)
                {
                    CloseUnsupported();
                    if (mem is not null || rec is not null) Detach("memory logging switched off");
                    channel?.Dispose(); channel = null;
                    if (clock.ElapsedMilliseconds >= nextAutosavePoll)
                    {
                        var running = FindCiv && UploadPolicy.CivRunning();
                        if (running != filesRunning)
                            Diagnostics?.Activity(running ? "Civ process detected" : "Civ process no longer detected", running ? "Memory capture remains disabled" : "Exit reason unknown; Windows crash evidence is checked when a report is saved");
                        if (running && !filesRunning) { saveOnly.Close(); filesPaused = false; }
                        if (recordingOff) saveOnly.Close();
                        filesRunning = running;
                        PollSaveOnly();
                        if (!running) saveOnly.End();
                        nextAutosavePoll = clock.ElapsedMilliseconds + 1000;
                    }
                    stop.Token.WaitHandle.WaitOne(100);
                    continue;
                }
                // a memory recording took over: the files-only capture of saves that came before it is complete
                if (rec is not null) saveOnly.Close();
                if (Workload is { CanReadMemory: false } held)
                {
                    // asking Windows whether Civ still runs reads none of its memory: a busy PC must not keep a closed Civ on screen
                    if (mem is { IsAlive: false } gone) { Detach("Civ closed", gone.ExitCode); continue; }
                    NoteMemoryHeld(held);
                    // saves are still noticed while reading waits: copies wait for the budget, the files stay queued
                    if (clock.ElapsedMilliseconds >= nextAutosavePoll) { PollAutosaves(); nextAutosavePoll = clock.ElapsedMilliseconds + 1000; }
                    stop.Token.WaitHandle.WaitOne(250);
                    continue;
                }
                channel ??= new Channel(settings.OutputFolder, () => sampleUs);
                Status = Status with { Channel = channel.Id };
                if (!ExperimentalMemoryCapture && clock.ElapsedMilliseconds >= nextAutosavePoll)
                { PollAutosaves(); nextAutosavePoll = clock.ElapsedMilliseconds + 1000; }
                if (mem is null)
                {
                    if (FindCiv && clock.ElapsedMilliseconds >= nextSearch) { Search(); nextSearch = clock.ElapsedMilliseconds + 2000; }
                }
                else Sample();
                // Observe the current turn before file work. Quiet-period saves remain queued (only noticed, PollAutosavesCore).
                if (ExperimentalMemoryCapture && clock.ElapsedMilliseconds >= nextAutosavePoll)
                { PollAutosaves(); nextAutosavePoll = clock.ElapsedMilliseconds + 1000; }
            }
            catch (Exception e)
            {
                Diagnostics?.Error("Capture loop", e);
                // a full disk or a locked file can make the clean-up fail too: that must not end this thread (and the program)
                try { Detach("error"); } catch (Exception again) { Diagnostics?.Error("Capture loop clean-up", again); }
                Status = Status with { LastError = $"{DateTime.Now:HH:mm:ss} {e.GetType().Name}: {e.Message}" };
            }
            var took = (clock.Elapsed - t0).TotalMilliseconds;
            // the pace: as asked, unless a look takes so long that it would use more than MaxBusy of one core
            lookAvg = NextLookAverage(lookAvg, took);
            int target = Math.Max(Math.Max(SampleMs, Workload?.MinimumSampleMs ?? 0), RecoveryInterval());
            int interval = IntervalFor(target, lookAvg);
            Status = Status with
            {
                LastSampleMs = Math.Round(took, 1), LookMs = Math.Round(lookAvg, 2),
                LooksPerSecondWanted = Math.Round(1000.0 / target, 1), LooksPerSecond = Math.Round(1000.0 / interval, 1),
            };
            stop.Token.WaitHandle.WaitOne(Math.Max(5, interval - (int)took));
        }
        try { PersistPendingCopies(force: true); } catch (Exception e) { Diagnostics?.Error("Pending copies at close", e); }
        try { saveOnly.Close(); } catch (Exception e) { Diagnostics?.Error("Save copies at close", e); }
        try { Detach("recorder closed"); } catch (Exception e) { Diagnostics?.Error("Recording at close", e); }
        CloseOwnedFiles();
    }

    void Search()
    {
        // Check before Inspect: even build detection must not open/read the game while held.
        if (!FindCiv || !TrackLogs || recordingOff || Workload is { CanReadMemory: false }) return;
        var procs = GameFinder.CivProcesses().ToList();
        try { SearchAmong(procs); }
        finally { foreach (var process in procs) process.Dispose(); }   // every 2 s while Civ runs and no game is attached
    }

    void SearchAmong(List<Process> procs)
    {
        if (procs.Count == 0) { CloseUnsupported(); saveOnly.Close(); Status = Status.CivGone(); return; }
        Status = Status with { CivDetected = true };
        var p = procs[0];
        var f = GameFinder.Inspect(p);
        if (f.InGameRecorderLoaded && !warnedInGameRecorder)
        {
            warnedInGameRecorder = true;
            Status = Status with { InGameRecorderPresent = true };
            // <civ>\Assets\DLC\<mod>\CvGameCore_Expansion2.dll -> <civ> (no extra process rights needed)
            var civ = f.GameCore is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f.GameCore.Path)!, "..", "..", ".."));
            Notice?.Invoke("In-game recorder is running too", InGameRecorderAdvice(InstalledInGameRecorder(civ) ?? @"Assets\DLC\FFACiv_Recorder"));
        }
        if (f.Layout is null)
        {
            if (f.GameCore is not null && RecordingEnabled && BackupUnsupportedEnabled) StartUnsupported(p.Id, f.GameCoreSha256, f.Status);
            else CloseUnsupported();
            Status = Status with { State = f.Status, Game = f.ExeName, Recording = false };
            ShowUnsupported();
            if (f.GameCore is not null && lastRefusedPid != p.Id)
            {
                lastRefusedPid = p.Id;
                Notice?.Invoke(unsupported is not null ? "Unsupported build — autosave backup" : "Not recording", unsupported is not null ? "Autosaves and a diagnostic log stay on this PC for later manual upload. Realtime moves cannot be captured for this build." : f.Status);
            }
            return;
        }
        if (refusedHash == f.GameCoreSha256 && lastRefusedPid == p.Id)
        {
            if (RecordingEnabled && BackupUnsupportedEnabled) StartUnsupported(p.Id, refusedHash, refusedReason ?? "Layout verification failed");
            else CloseUnsupported();
            return;
        }
        CloseUnsupported();
        mem = new ProcessMemory(p.Id);
        taping = new TapingMemory(mem);
        reader = new StateReader(taping, f.GameCore!.Base, f.Layout);
        if (!reader.CheckGlobals(out var why))
        {
            Status = Status with { State = $"Refusing to read: {why}" };
            Notice?.Invoke("Not recording", $"The game core looks different from what we know ({why}).");
            mem.Dispose(); mem = null; reader = null; taping = null; lastRefusedPid = p.Id; refusedHash = f.GameCoreSha256; refusedReason = why;
            if (RecordingEnabled && BackupUnsupportedEnabled) StartUnsupported(p.Id, f.GameCoreSha256, why ?? "Layout verification failed");
            return;
        }
        found = f;
        quietUntilMs = clock.ElapsedMilliseconds + QuietMs;   // Civ may be starting or loading: let it settle first
        Status = Status with { State = "Civ found; waiting for a game", Game = $"{f.ExeName} – {f.Layout.Name}", Layout = f.Layout.Name };
        Notice?.Invoke("Civ V found", $"{f.Layout.Name}. Recording starts when a game is running.");
    }

    bool memoryWasHeld;
    string? memoryHeldWhy;
    long recoveryStartedMs = -1;

    int RecoveryInterval()
    {
        if (recoveryStartedMs < 0) return 0;
        long elapsed = clock.ElapsedMilliseconds - recoveryStartedMs;
        return elapsed < QuietMs ? 1000 : elapsed < QuietMs + 2000 ? 500 : elapsed < QuietMs + 4000 ? 200 : 0;
    }

    /// <summary>One look at Civ: the quiet-window header look, the turn-transition gate, or a taped read of the game.</summary>
    void Sample()
    {
        if (!TrackLogs || recordingOff) return;
        if (Workload is { CanReadMemory: false } held) { NoteMemoryHeld(held); return; }
        if (!mem!.IsAlive) { Detach("Civ closed", mem.ExitCode); return; }
        if (memoryWasHeld) SettleAfterHold();
        sampleUs = clock.Elapsed.Ticks / 10;
        var t0 = clock.Elapsed;
        // loading or just found: only the globals until the game has looked the same for QuietMs (nothing in flux is followed)
        if (clock.ElapsedMilliseconds < quietUntilMs) { QuietLook(); return; }
        if (ExperimentalMemoryCapture && InTurnTransition()) return;
        if (!TrackLogs || recordingOff) return;
        taping!.Tape = CaptureMoves ? tape : null;
        taping.Begin(sampleUs);
        try { ReadAndRecord(t0); }
        finally { taping?.End((clock.Elapsed - t0).TotalMilliseconds); }
    }

    /// <summary>The resource budget holds memory reading: remembered, so the next look starts a fresh baseline.</summary>
    void NoteMemoryHeld(WorkloadPolicy held)
    {
        memoryWasHeld = true; memoryHeldWhy = held.Reason;
        Diagnostics?.Activity("Memory capture deferred", held.Reason);
        Status = Status with { State = held.Reason, LooksPerSecond = 0 };
    }

    /// <summary>A workload pause is an observation gap, not game actions that all happened at resume time: a fresh reader and
    /// baseline after a quiet window, then a gradual return to the requested pace.</summary>
    void SettleAfterHold()
    {
        pipe?.ResetAfterCaptureGap(memoryHeldWhy);
        reader = new StateReader(taping!, found!.GameCore!.Base, found.Layout!);
        quietSeen = (-1, -1, -1);
        quietUntilMs = clock.ElapsedMilliseconds + QuietMs;
        recoveryStartedMs = clock.ElapsedMilliseconds;
        Diagnostics?.Activity("Memory capture settling", "Basic checks precede a gradual return to the requested sample rate");
        memoryWasHeld = false;
    }

    /// <summary>The quiet window: only the globals, until the turn and map size stayed the same for <see cref="QuietMs"/>.</summary>
    void QuietLook()
    {
        var (hs, ht, hw, hh) = reader!.ReadHeader();
        if (hs != SampleStatus.NoGame)
        {
            Status = Status with { MenuObservedAt = null };
            if (Workload is { } activeWorkload) activeWorkload.GameIdle = false;
        }
        if (hs == SampleStatus.LayoutMismatch || hs == SampleStatus.Unreadable) { Detach(hs == SampleStatus.Unreadable ? "game core unloaded" : "layout mismatch"); return; }
        if (hs != SampleStatus.Ok || (ht, hw, hh) != quietSeen) { quietSeen = (ht, hw, hh); quietUntilMs = clock.ElapsedMilliseconds + QuietMs; }
        pipe?.Step(SampleStatus.NoGame, null, sampleUs / 1000);
        rec?.Flush();
        var lobbyNow = hs == SampleStatus.NoGame ? LookAtLobby() : Status.Lobby;
        Status = Status with { State = rec is null ? "Civ found; waiting for a game" : "Game paused / in menus / loading", Lobby = lobbyNow };
    }

    /// <summary>The experimental capture's new-turn pause: true while detailed reads wait (only the verified globals are read).</summary>
    bool InTurnTransition()
    {
        var header = reader!.ReadHeader();
        if (header.Status != SampleStatus.Ok) { turnQuiet.Reset(); return false; }
        Status = Status with { MenuObservedAt = null };
        if (Workload is { } activeWorkload) activeWorkload.GameIdle = false;
        if (turnQuiet.Observe(header.Turn))
        {
            rec?.Write("sampling_paused", header.Turn, new Dictionary<string, object?>
            {
                ["reason"] = "turn_transition", ["durationMs"] = 5000,
                ["note"] = "Detailed reads paused; actions during this interval may be missed.",
            });
            rec?.Flush();
        }
        if (!TurnTransitionQuiet) return false;
        Status = Status with { State = "Giving Civ time to start the turn" };
        return true;
    }

    /// <summary>One taped read of the game, fed to the recording (opening or closing it as the game asks), then the window.</summary>
    void ReadAndRecord(TimeSpan t0)
    {
        var (status, snap, why) = reader!.Read(sampleUs / 1000);
        if (status != SampleStatus.NoGame) Status = Status with { MenuObservedAt = null };
        if (status == SampleStatus.NoGame) { quietUntilMs = clock.ElapsedMilliseconds + QuietMs; quietSeen = (-1, -1, -1); }
        if (status == SampleStatus.LayoutMismatch) { RefuseAfterMismatch(why, t0); return; }
        if (status == SampleStatus.Unreadable) { taping!.End(0); Detach("game core unloaded"); return; } // mod switched: look again
        string? seenId = null;
        // a game still loading (no players built yet) says nothing about which game it is: the pipeline waits it out
        if (status == SampleStatus.Ok && snap is not null && GamePipeline.IsLoaded(snap)) (seenId, snap) = OpenOrCloseGame(snap);
        if (pipe is not null) pipe.CaptureMoves = CaptureMoves;
        pipe?.Step(status, snap, sampleUs / 1000);
        // a heartbeat every ~10 s while recording: quiet stretches are not a stalled recorder, and the real cost is in the data
        if (CaptureMoves && rec is not null && clock.ElapsedMilliseconds >= nextHeartbeatMs)
        {
            nextHeartbeatMs = clock.ElapsedMilliseconds + 10_000;
            rec.Write("heartbeat", snap?.Turn ?? Status.Turn, new Dictionary<string, object?>
            {
                ["state"] = "alive", ["looksPerSecond"] = Status.LooksPerSecond, ["looksPerSecondWanted"] = Status.LooksPerSecondWanted,
                ["msPerLook"] = Status.LookMs, ["corePercent"] = Math.Round(Status.LookMs * Status.LooksPerSecond / 10, 2),
                ["refusedReads"] = mem?.Refused, ["units"] = snap?.Units.Count, ["cities"] = snap?.Cities.Count,
            });
        }
        rec?.Flush();
        ShowSample(status, snap, seenId);
    }

    /// <summary>Civ's memory stopped matching the layout: the recording ends, this Civ is not read again until it restarts,
    /// and its autosaves are kept locally instead.</summary>
    void RefuseAfterMismatch(string why, TimeSpan t0)
    {
        Notice?.Invoke("Recording stopped", $"Civ's memory no longer looks as expected ({why}).");
        taping!.End((clock.Elapsed - t0).TotalMilliseconds);
        var refused = found;
        Detach("layout mismatch: " + why);
        lastRefusedPid = refused?.ProcessId ?? -1; refusedHash = refused?.GameCoreSha256; refusedReason = why;
        if (refused is not null && RecordingEnabled && BackupUnsupportedEnabled) StartUnsupported(refused.ProcessId, refused.GameCoreSha256, why);
    }

    /// <summary>
    /// A loaded game was seen: pauses, ends or starts its recording as the player chose (paused, deleted, single player off,
    /// recording off). Returns its identity and the sample to feed on (read again inside the tape when a recording began).
    /// </summary>
    (string Id, GameSnapshot Snap) OpenOrCloseGame(GameSnapshot snap)
    {
        var id = GamePipeline.IdentityOf(snap);
        if (pauseCurrent && rec is not null) { pausedIdentity = identity; pauseCurrent = false; EndGame("paused by the player", "paused"); }
        else if (pauseCurrent) { pausedIdentity = id; pauseCurrent = false; }
        if (rec is not null && id != identity) EndGame("another game was started");
        if (recordingOff && rec is not null) EndGame("recording switched off", "paused");
        // single player off: such a game is not started (one that is being recorded already goes on)
        bool spSkip = rec is null && snap.GameType == 0 && !recordSinglePlayer && id != singlePlayerAllowed;
        if (spSkip && singlePlayerTold != id)
        {
            singlePlayerTold = id;
            Notice?.Invoke("Single-player game — not recorded", $"Turn {snap.Turn}. Single-player games are off in Settings (for testing, coming later).");
        }
        bool skip = recordingOff || id == pausedIdentity || IsDeletedIdentity(id) || spSkip;
        if (rec is null && !skip)
        {
            BeginGame(snap, id); taping!.Begin(sampleUs);
            // Only a raw tape needs a repeated baseline; the lightweight experiment keeps one sample.
            if (tape is not null) snap = reader!.Read(sampleUs / 1000).Snapshot ?? snap;
        }
        if (entry is not null && clock.ElapsedMilliseconds - lastIndexWriteMs > 30_000) { index.Save(entry with { LastSeen = DateTimeOffset.UtcNow }); lastIndexWriteMs = clock.ElapsedMilliseconds; }
        return (id, snap);
    }

    /// <summary>The window after a look (and, with no game running, a look at the pre-game for a staging room).</summary>
    void ShowSample(SampleStatus status, GameSnapshot? snap, string? seenId)
    {
        // no game running: look at the pre-game once a second for a multiplayer staging room (read-only, not on the tape)
        LobbySnapshot? lobby = status == SampleStatus.NoGame ? LookAtLobby() : rec is not null ? null : Status.Lobby;
        var me = snap?.ActivePlayer ?? -1;
        bool loaded = snap is not null && GamePipeline.IsLoaded(snap);
        Status = Status with
        {
            Lobby = lobby,
            Identity = identity ?? (loaded ? seenId : Status.Identity),
            Players = loaded ? snap!.Players.Where(p => p.Id != BarbarianId).ToList() : Status.Players,
            ActivePlayer = loaded ? snap!.ActivePlayer : Status.ActivePlayer,
            Paused = recordingOff || ((seenId ?? Status.Identity) is { } sid && (sid == pausedIdentity || IsDeletedIdentity(sid))),
            SinglePlayerSkipped = rec is null && loaded && snap!.GameType == 0 && !recordSinglePlayer && seenId != singlePlayerAllowed,
            State = rec is null ? (loaded ? "Civ found; game not recorded" : "Civ found; waiting for a game") : !loaded ? "Game paused / in menus / loading" : "Recording",
            Recording = rec is not null, Turn = snap?.Turn ?? Status.Turn,
            MyUnits = snap?.Units.Count(u => u.Owner == me) ?? 0, MyCities = snap?.Cities.Count(c => c.Owner == me) ?? 0,
            Mode = snap?.GameType switch { 0 => "single player", 1 => "multiplayer", 2 => "hot seat", 3 => "e-mail", _ => Status.Mode },
            Events = rec?.Count ?? 0, Folder = rec?.Folder ?? Status.Folder,
            TapeBytes = tape is null ? null : new FileInfo(tape.Path).Length, Context = entry?.Context,
        };
    }

    /// <summary>
    /// No game running: once a second a look at the pre-game for a multiplayer staging room (its statics in the game core's
    /// image and a few small vectors; read-only, guarded like every read, not on the tape).
    /// </summary>
    LobbySnapshot? LookAtLobby()
    {
        if (channel is null || mem is null || found is null) return null;
        if (clock.ElapsedMilliseconds < Interlocked.Read(ref nextLobbyLookMs)) return Status.Lobby;
        Interlocked.Exchange(ref nextLobbyLookMs, clock.ElapsedMilliseconds + 1000);
        var lobby = new StateReader(mem, found.GameCore!.Base, found.Layout!).ReadLobby();
        bool menu = lobby is { GameStarted: false } && found.Layout!.RvaGameStarted is uint startedRva
            && mem.Bool(found.GameCore!.Base + startedRva) == false;
        Status = Status with { MenuObservedAt = menu && lobby is not { IsStagingRoom: true } ? Environment.TickCount64 : null };
        if (menu && lobby is not { IsStagingRoom: true }) EndGame("returned to the main menu");
        if (lobby is { IsStagingRoom: true })
        {
            EndGame("returned to the multiplayer lobby");
            Status = Status.InLobby(lobby);
        }
        if (channel.See(lobby, found.Layout!.Name, sampleUs / 1000))
            Notice?.Invoke("Multiplayer lobby", $"{lobby!.Slots.Count(x => x.Status == 3)} players so far. The game is recorded when it starts.");
        return lobby is { IsStagingRoom: true } ? lobby : null;
    }

    void BeginGame(GameSnapshot s, string id)
    {
        // the first sample of a game was read before the tape existed: the caller reads it again inside the tape
        taping!.End(0);
        identity = id;
        // a fresh reader: its name caches must start empty so every read it makes is on the tape (replay = live)
        reader = new StateReader(taping, found!.GameCore!.Base, found.Layout!);
        var known = index.Find(id, DateTimeOffset.UtcNow);
        var folder = known?.Folder ?? Path.Combine(settings.OutputFolder, SessionFolderName(found!.ProcessId.ToString(CultureInfo.InvariantCulture)));
        entry = new GameEntry { Identity = id, Folder = folder, Context = known?.Context ?? GameIndex.NewContext(), ProcessId = found!.ProcessId, LastSeen = DateTimeOffset.UtcNow };
        index.Save(entry); lastIndexWriteMs = clock.ElapsedMilliseconds;
        int previousTurn = known is null ? -1 : JsonlRecording.LastTurn(Path.Combine(folder, "events.jsonl")) ?? -1;
        rec = new JsonlRecording(folder, "events.jsonl", () => sampleUs);
        Status = Status with { GameStarted = StartedOf(folder) ?? DateTimeOffset.Now, LastSaveAt = LastSaveOf(folder) };
        if (settings.Tape && !ExperimentalMemoryCapture)
        {
            tape = new TapeWriter(folder, new TapeHeader
            {
                Layout = found.Layout!.Id, CoreSha256 = found.GameCoreSha256!, ModuleBase = found.GameCore!.Base,
                SampleMs = SampleMs, Reads = TapeReplay.CurrentReads, Started = DateTimeOffset.Now.ToString("o"),
            });
            taping.Tape = tape;
        }
        pipe = new GamePipeline(rec, reader!.UnitTypeKey, settings.SnapshotEachTurn || ExperimentalMemoryCapture, previousTurn);
        var profile = ProfileData(s, found.Layout!, found.GameCoreSha256, "read-only memory sampling", SampleMs, id, entry.Context,
            tape is not null, found.ProcessId, recorderStarted.ToString("o"));
        profile["cityMicroMs"] = reader.CityMicroEveryMs; profile["cityMicroAvailable"] = found.Layout!.CityMicro is not null;
        profile["corePath"] = found.GameCore!.Path; profile["exe"] = found.ExeName; profile["inGameRecorderAlsoLoaded"] = found.InGameRecorderLoaded;
        profile["turnTransitionQuietMs"] = ExperimentalMemoryCapture ? 5000 : 0;
        profile["autosaveWarning"] = Status.AutosaveWarning; profile["channel"] = channel?.Id;
        profile["turnTimerSeconds"] = s.TurnTimer?.TurnSeconds; profile["turnTimer"] = s.TurnTimer?.Data();
        rec.Write("recording_profile", s.Turn, profile);
        // why this recording (re)starts: a new game, or the same game back after Civ or the recorder restarted
        rec.Write("lifecycle", s.Turn, new Dictionary<string, object?>
        {
            ["state"] = known is null ? "recording" : "game_rejoined",
            ["reason"] = known is null ? "new game" : known.ProcessId != found.ProcessId ? "civ restarted" : "recorder restarted or game reopened",
            ["previousProcessId"] = known?.ProcessId, ["lastSeen"] = known?.LastSeen.ToString("o"),
            ["previousTurn"] = previousTurn < 0 ? null : previousTurn,
        });
        try { channel?.GameStarted(id, entry.Context, s.Turn, found.Layout!.Name, s.TurnTimer, known is not null); } catch { }
        Notice?.Invoke("Recording game", $"Turn {s.Turn}, {s.Players.Count(p => p.Alive && p.Id != 63)} players, {ModeName(s.GameType)}.");
    }

    /// <summary>
    /// The fields every recording's first line ("recording_profile") has: who wrote it and how, the DLL that was read, and
    /// the game's identity, map and seeds (the converter needs them). The live recorder and the stream test add their own.
    /// </summary>
    internal static Dictionary<string, object?> ProfileData(GameSnapshot s, GameCoreLayout layout, string? coreSha256, string method,
        int sampleMs, string identity, string context, bool tape, int processId, string recorderStarted) => new()
    {
        ["recorder"] = "FFACiv Outside Recorder", ["method"] = method, ["sampleMs"] = sampleMs,
        ["layout"] = layout.Id, ["mod"] = layout.Name, ["coreSha256"] = coreSha256,
        ["autosaveTiming"] = layout.AutosaveAtTurnStart ? "start_of_turn" : "end_of_turn",
        ["gameType"] = s.GameType, ["activePlayer"] = s.ActivePlayer, ["mapWidth"] = s.MapWidth, ["mapHeight"] = s.MapHeight,
        ["identity"] = identity, ["context"] = context, ["mapSeed"] = s.MapSeed, ["syncSeed"] = s.SyncSeed, ["tape"] = tape,
        ["processId"] = processId, ["recorderStarted"] = recorderStarted,
    };

    /// <summary>The players' number of the barbarians (not shown as a player).</summary>
    public const int BarbarianId = 63;

    const string SessionTimeFormat = "yyyyMMdd'T'HHmmss";

    /// <summary>"session-20261027T152244-<paramref name="suffix"/>" for a recording starting now: local time in Western digits
    /// and calendar on every Windows language, so <see cref="StartedOf"/> reads it back.</summary>
    public static string SessionFolderName(string suffix) => $"session-{DateTime.Now.ToString(SessionTimeFormat, CultureInfo.InvariantCulture)}-{suffix}";

    /// <summary>"session-20261027T152244-19836" -> that local time.</summary>
    public static DateTimeOffset? StartedOf(string? folder)
    {
        var m = folder is null ? null : Regex.Match(Path.GetFileName(folder), @"session-(\d{8}T\d{6})");
        return m is { Success: true } && DateTime.TryParseExact(m.Groups[1].Value, SessionTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
    }

    /// <summary>Civ keeps single-player autosaves in ...\Saves\single\auto (also under ModdedSaves). Only the save's own two
    /// folders count, so a Windows user or OneDrive folder called "single" does not hide multiplayer saves.</summary>
    public static bool InSinglePlayerFolder(string savePath)
    {
        var folder = Path.GetDirectoryName(savePath);
        for (int i = 0; i < 2 && !string.IsNullOrEmpty(folder); i++, folder = Path.GetDirectoryName(folder))
            if (Path.GetFileName(folder).Equals("single", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>When the newest save copy of a recording was written, or null.</summary>
    public static DateTimeOffset? LastSaveOf(string folder)
    {
        var dir = Path.Combine(folder, "saves");
        try
        {
            if (!Directory.Exists(dir)) return null;
            var t = Directory.GetFiles(dir, "*.Civ5Save").Select(File.GetLastWriteTime).DefaultIfEmpty().Max();
            return t > DateTime.MinValue ? t : null;
        }
        catch { return null; }
    }

    static string ModeName(int? t) => t switch { 0 => "single player", 1 => "multiplayer", 2 => "hot seat", _ => "game" };

    /// <summary>Autosave file name turn: "AutoSave_0013 BC-3220.Civ5Save" -> 13.</summary>
    public static int? AutosaveTurn(string path) =>
        Regex.Match(Path.GetFileName(path), @"AutoSave_(?:Post_)?(\d+)", RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    /// <summary>Autosaves noticed but not copied yet (Civ still had them open), tried again each second for a while.</summary>
    readonly Dictionary<string, (AutosaveSeen Save, int Tries)> copyLater = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Maximum retained copy retries; ready source files beyond this stay in the metadata watcher.</summary>
    public const int MaxPendingCopies = 256;

    List<(AutosaveSeen Save, int Tries)> PendingCopies()
    {
        foreach (var save in autosaves.Poll(maxReports: Math.Max(0, MaxPendingCopies - copyLater.Count)))
        {
            int tries = restoredCopies.Remove(save.Path, out var restored) && restored.Save == save ? restored.Tries : 0;
            copyLater[save.Path] = (save, tries); // rewritten metadata supersedes a stale, uncopiable version
            Status = Status with { LastAutosave = $"{Path.GetFileName(save.Path)} ({save.Size / 1024} KB, {save.WriteTimeUtc.ToLocalTime():HH:mm:ss})" };
        }
        var todo = copyLater.Values.OrderBy(x => x.Tries).ToList();
        copyLater.Clear();
        return todo;
    }

    sealed record PendingSaveState(AutosaveSeen Save, int Tries);
    readonly Dictionary<string, PendingSaveState> restoredCopies = new(StringComparer.OrdinalIgnoreCase);
    const int MaxRecoverableCopies = 512, MaxPendingManifestBytes = 1024 * 1024;
    string PendingManifest => Path.Combine(settings.OutputFolder, "pending-autosaves.json");
    string? lastPendingManifest;
    bool pendingCompleted;
    long lastPendingWriteMs = -10000;

    void RestorePendingCopies()
    {
        if (!settings.SafeCapture) return;
        try
        {
            if (!File.Exists(PendingManifest) || new FileInfo(PendingManifest).Length > MaxPendingManifestBytes) return;
            var records = System.Text.Json.JsonSerializer.Deserialize<PendingSaveState[]>(SafeFiles.ReadAllText(PendingManifest)) ?? [];
            var candidates = records.Take(MaxRecoverableCopies).Where(r => r?.Save is not null)
                .GroupBy(r => r.Save.Path ?? "", StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToArray();
            var accepted = autosaves.RestorePending(candidates.Select(r => r.Save), MaxRecoverableCopies)
                .ToHashSet();
            foreach (var record in candidates)
                if (accepted.Contains(record.Save)) restoredCopies[record.Save.Path] = record with { Tries = Math.Clamp(record.Tries, 0, CopyTries - 1) };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        { Status = Status with { LastError = "Pending autosave recovery metadata was unavailable or invalid; recording continues." }; }
    }

    void PersistPendingCopies(bool force = false)
    {
        if (!settings.SafeCapture || !force && clock.ElapsedMilliseconds - lastPendingWriteMs < 10000) return;
        try
        {
            var records = copyLater.Values.Select(p => new PendingSaveState(p.Save, p.Tries))
                .ToDictionary(p => p.Save.Path, StringComparer.OrdinalIgnoreCase);
            // A changed source being observed by the watcher replaces an older retry for the same path.
            foreach (var save in autosaves.PendingFiles(MaxRecoverableCopies))
            {
                int tries = restoredCopies.TryGetValue(save.Path, out var recovered) && recovered.Save == save ? recovered.Tries : 0;
                records[save.Path] = new(save, tries);
            }
            var bounded = records.Values.OrderByDescending(p => p.Save.WriteTimeUtc).Take(MaxRecoverableCopies).ToArray();
            var text = System.Text.Json.JsonSerializer.Serialize(bounded);
            lastPendingWriteMs = clock.ElapsedMilliseconds;
            if (text == lastPendingManifest) return;
            if (text.Length > MaxPendingManifestBytes / 2) return;
            if (bounded.Length == 0 && !File.Exists(PendingManifest)) { lastPendingManifest = text; return; }
            Directory.CreateDirectory(settings.OutputFolder);
            SafeFiles.Replace(PendingManifest, text);
            lastPendingManifest = text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Diagnostics?.Error("Pending autosave recovery", e); }
    }

    bool CopiesHeld()
    {
        if (Workload is not { CanCopySaves: false }) return false;
        autosaves.Poll(reportReady: false); // update stability, but do not consume any ready notification
        Diagnostics?.Activity("Autosave copying deferred", Workload.Reason);
        return true;
    }

    void RetryCopy(AutosaveSeen save, int tries) => copyLater[save.Path] = (save, tries);
    /// <summary>Actual failed IO attempts before giving up; resource and queue waits do not spend this budget.</summary>
    public const int CopyTries = 30;

    void PollAutosaves()
    {
        hasPendingAutosaves = !recordingOff && !filesPaused;
        try { PollAutosavesCore(); }
        finally
        {
            UpdatePendingAutosaves();
            PersistPendingCopies(force: pendingCompleted);
            pendingCompleted = false;
        }
    }

    /// <summary>Memory mode's save copies: into the open recording, an unsupported build's backup, or (no recording open) the
    /// files-only capture. Saves are noticed in every state; during the new-turn pause they only queue.</summary>
    void PollAutosavesCore()
    {
        if (TurnTransitionQuiet) { autosaves.Poll(reportReady: false); return; }
        if (CopiesHeld()) return;
        var todo = PendingCopies();
        if (rec is not null) CopyOneSave(todo, "Copying a realtime-session autosave", CopyIntoRecording, WriteAutosaveLine);
        else if (unsupported is not null && RecordingEnabled && BackupUnsupportedEnabled)
        {
            CopyOneSave(todo, "Copying an unsupported-build autosave", BackUpUnsupported);
            ShowUnsupported();
        }
        else CopyWithoutRecording(todo);
    }

    /// <summary>What one attempt to copy a ready save came to.</summary>
    enum CopyResult
    {
        /// <summary>The copy is made.</summary>
        Copied,
        /// <summary>Not now (Civ still has it, it changed): tried again on a later poll, up to <see cref="CopyTries"/> attempts.</summary>
        RetryLater,
        /// <summary>Never copied (a KEK save: Oblast supplies those).</summary>
        Excluded,
        /// <summary>The source is gone, or the copy failed for good.</summary>
        Gone,
    }

    /// <summary>
    /// The one copy a poll makes: the first ready save (fewest attempts first) when heavy work may start; every other save
    /// waits with its attempt count (resource and queue waits never spend the retry budget). <paramref name="leaveOut"/>
    /// saves are dropped. <paramref name="finished"/> hears of an attempt that is not retried. Saves not dealt with go back to
    /// the queue even when something throws: one error must not lose the saves after it.
    /// </summary>
    void CopyOneSave(List<(AutosaveSeen Save, int Tries)> todo, string activity, Func<AutosaveSeen, (CopyResult Result, string? Copy, string? Error)> copy,
        Action<AutosaveSeen, int, CopyResult, string?, string?>? finished = null, Func<AutosaveSeen, bool>? leaveOut = null)
    {
        int next = 0;   // todo[next..] are not dealt with yet
        try
        {
            bool attempted = false;
            while (next < todo.Count)
            {
                var (save, tries) = todo[next];
                if (leaveOut?.Invoke(save) == true) { next++; continue; }
                if (attempted || Workload is { CanCopySaves: false }) { RetryCopy(save, tries); next++; continue; }
                attempted = true;
                Diagnostics?.Activity(activity);
                (CopyResult Result, string? Copy, string? Error) outcome;
                try { outcome = copy(save); }
                catch { if (tries + 1 < CopyTries) RetryCopy(save, tries + 1); next++; throw; }   // an unexpected error is a failed attempt
                next++;
                if (outcome.Result is CopyResult.Copied or CopyResult.RetryLater) CopyOutcome(save, tries, outcome.Copy);
                if (outcome.Result == CopyResult.RetryLater && tries + 1 < CopyTries) RetryCopy(save, tries + 1);
                else finished?.Invoke(save, tries, outcome.Result, outcome.Copy, outcome.Error);
            }
        }
        finally
        {
            for (; next < todo.Count; next++) RetryCopy(todo[next].Save, todo[next].Tries);
        }
    }

    /// <summary>A copy in the recording's folder: Civ deletes old autosaves, and the stream sends from here. Never ends the
    /// recording: an error is written on the save's line.</summary>
    (CopyResult, string?, string?) CopyIntoRecording(AutosaveSeen save)
    {
        try
        {
            var dir = Path.Combine(rec!.Folder, "saves");
            var copy = settings.SafeCapture || ExperimentalMemoryCapture ? SafeFiles.CopyStableSave(save, dir) : SafeFiles.CopySave(save.Path, dir, save.WriteTimeUtc);
            return copy is not null ? (CopyResult.Copied, copy, null)
                : File.Exists(save.Path) ? (CopyResult.RetryLater, null, null) : (CopyResult.Gone, null, "gone before it could be copied");
        }
        catch (Exception e) { Diagnostics?.Error("Autosave copy", e); return (CopyResult.Gone, null, $"{e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>The recording's "autosave" line for a save copied or given up (and a "resync" line for a resync save).</summary>
    void WriteAutosaveLine(AutosaveSeen a, int tries, CopyResult result, string? copy, string? error)
    {
        var r = rec!;
        if (result == CopyResult.RetryLater) error = $"could not be read after {CopyTries} attempts";
        if (copy is not null) Status = Status with { LastSaveAt = a.WriteTimeUtc.ToLocalTime() };
        int? fileTurn = AutosaveTurn(a.Path);
        bool? atStart = found?.Layout?.AutosaveAtTurnStart;
        // a resync (multiplayer: the host's state replaced this PC's) — Civ writes AutoSave_Resync…; marked so the turn's
        // jumps (a unit changing type, moves, the map id in the save) are known not to be play
        if (Path.GetFileName(a.Path).Contains("Resync", StringComparison.OrdinalIgnoreCase))
            r.Write("lifecycle", Status.Turn, new Dictionary<string, object?>
            {
                ["state"] = "resync", ["file"] = Path.GetFileName(a.Path), ["copy"] = copy is null ? null : Path.GetRelativePath(r.Folder, copy),
                ["note"] = "the host's game state replaced this PC's; changes around it can come from the resync",
            });
        r.Write("autosave", Status.Turn, new Dictionary<string, object?>
        {
            ["path"] = a.Path, ["bytes"] = a.Size, ["writeTimeUtc"] = a.WriteTimeUtc.ToString("o"), ["folder"] = Path.GetDirectoryName(a.Path),
            ["fileTurn"] = fileTurn, ["liveTurn"] = Status.Turn, ["copy"] = copy is null ? null : Path.GetRelativePath(r.Folder, copy),
            ["copyTries"] = tries + 1, ["copyError"] = error,
            // which samples the save should equal: stock BNW saves at the start of a turn, Lekmod/KEK at the end
            ["timing"] = atStart switch { true => "start_of_turn", false => "end_of_turn", null => "unknown" },
            ["compareWith"] = atStart switch
            {
                true => $"first sample of turn {fileTurn}",
                false => $"last sample of turn {fileTurn} (before turn {fileTurn + 1} starts)",
                null => null,
            },
        });
        r.Flush();
    }

    /// <summary>An unsupported build's save, kept locally exactly as written (no memory read, no upload).</summary>
    (CopyResult, string?, string?) BackUpUnsupported(AutosaveSeen save)
    {
        try
        {
            switch (unsupported!.Capture(save))
            {
                case true: return (CopyResult.Copied, "copied", null);
                case false: Diagnostics?.Activity("Autosave excluded", "KEK games are supplied separately"); return (CopyResult.Excluded, null, null);
                default: return (CopyResult.RetryLater, null, null);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics?.Error("Autosave backup", e);
            Status = Status with { LastError = "Autosave backup: " + e.Message };
            return (CopyResult.RetryLater, null, e.Message);
        }
    }

    /// <summary>
    /// Memory mode with no memory recording open (not begun yet, reading held, after an error closed it, after Civ closed):
    /// the save is kept the way save-files-only capture keeps it (a "files:" recording; the server groups saves by their
    /// contents), never dropped. A paused or deleted game, and a single-player save while single player is off, keep none.
    /// </summary>
    void CopyWithoutRecording(List<(AutosaveSeen Save, int Tries)> todo)
    {
        // the player deleted the files-only capture itself: like save-files-only mode, its copying pauses (until Civ closes)
        if (IsDeletedIdentity(saveOnly.Entry?.Identity)) { filesPaused = true; saveOnly.End(); }
        bool notRecorded = pauseCurrent || filesPaused || Status.Identity is { } id && (id == pausedIdentity || IsDeletedIdentity(id));
        if (notRecorded)
        {
            if (todo.Count > 0) Diagnostics?.Activity("Autosave not copied", "This game's recording is paused or deleted");
            return;
        }
        CopyOneSave(todo, "Copying an autosave without a realtime session", CopySaveOnly, ShowSaveWithoutRecording,
            save => !recordSinglePlayer && InSinglePlayerFolder(save.Path) && !(singlePlayerAllowed is { } allowed && allowed == Status.Identity));
    }

    void ShowSaveWithoutRecording(AutosaveSeen save, int tries, CopyResult result, string? copy, string? error)
    {
        if (result == CopyResult.Copied) Status = Status with { LastSaveAt = save.WriteTimeUtc.ToLocalTime() };
    }

    void CopyOutcome(AutosaveSeen save, int tries, string? copy)
    {
        if (copy is not null)
        {
            pendingCompleted = true;
            Diagnostics?.Activity("Autosave copy completed", $"{save.Size} bytes preserved locally");
        }
        else if (tries + 1 < CopyTries) Diagnostics?.Activity("Autosave copy deferred", "Source unavailable, excluded or changed; retry queued");
        else
        {
            Diagnostics?.Error("Autosave copy retries exhausted", new IOException());
            Status = Status with { LastError = "An autosave could not be copied before its retry limit; its source may have changed or been removed." };
        }
    }

    void UpdatePendingAutosaves() => hasPendingAutosaves = !recordingOff && !filesPaused &&
        (copyLater.Count > 0 || autosaves.PendingFiles(1).Count > 0);

    void PollSaveOnly()
    {
        // Publish before consuming watcher notifications: uploads must not see an empty queue during a copy.
        hasPendingAutosaves = !recordingOff && !filesPaused;
        try
        {
            if (pauseCurrent || IsDeletedIdentity(saveOnly.Entry?.Identity)) { filesPaused = true; pauseCurrent = false; saveOnly.End(); }
            bool held = !recordingOff && !filesPaused && CopiesHeld();
            var todo = held ? [] : PendingCopies();
            // Multiplayer and single-player folders are distinguishable without parsing the save.
            if (!recordingOff && !filesPaused)
                CopyOneSave(todo, "Copying a settled autosave", CopySaveOnly, ShowSavedFile, save => !recordSinglePlayer && InSinglePlayerFolder(save.Path));
            Status = Status with { CivDetected = filesRunning, State = recordingOff ? "Manual upload only — automatic capture is off" : filesPaused ? "Save copying paused" : held ? Workload!.Reason + " — save copies waiting" : "Save files only — memory reading and logs are off",
                Recording = !recordingOff && !filesPaused && saveOnly.Entry is not null, Paused = recordingOff || filesPaused, Folder = saveOnly.Entry?.Folder,
                Context = saveOnly.Entry?.Context, Identity = null, Game = null, Players = [], ActivePlayer = null,
                Lobby = null, Channel = null, MyUnits = 0, MyCities = 0, Events = 0, TapeBytes = null,
                LooksPerSecond = 0, LookMs = 0, LastSampleMs = 0 };
        }
        finally
        {
            PersistPendingCopies(force: pendingCompleted || recordingOff || filesPaused);
            pendingCompleted = false;
            UpdatePendingAutosaves();
        }
    }

    /// <summary>Save-only capture: one settled save into the files-only recording.</summary>
    (CopyResult, string?, string?) CopySaveOnly(AutosaveSeen save)
    {
        string? copy; bool excluded;
        try { copy = saveOnly.Copy(save, out excluded); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics?.Error("Autosave copy", e);
            Status = Status with { LastError = "Autosave copy: " + e.GetType().Name };
            return (CopyResult.RetryLater, null, null);
        }
        // a KEK save is left out for good: no 30 retries, no false "could not be copied" error
        if (excluded) { Diagnostics?.Activity("Autosave not copied", "KEK Mod saves are supplied by Oblast"); return (CopyResult.Excluded, null, null); }
        return copy is not null ? (CopyResult.Copied, copy, null) : (CopyResult.RetryLater, null, null);
    }

    void ShowSavedFile(AutosaveSeen save, int tries, CopyResult result, string? copy, string? error)
    {
        if (result == CopyResult.Copied) Status = Status with { LastAutosave = Path.GetFileName(save.Path), LastSaveAt = save.WriteTimeUtc, Turn = AutosaveTurn(save.Path) };
    }

    void EndGame(string why, string state = "stopped", Dictionary<string, object?>? extra = null)
    {
        if (rec is null) return;
        pauseCurrent = false;
        var args = new Dictionary<string, object?> { ["state"] = state, ["reason"] = why };
        if (extra is not null) foreach (var (k, v) in extra) args[k] = v;
        var n = rec.Count; var folder = rec.Folder;
        try
        {
            rec.Write("lifecycle", pipe is { LastTurn: >= 0 } ? pipe.LastTurn : null, args);
            if (entry is not null) index.Save(entry with { LastSeen = DateTimeOffset.UtcNow });
        }
        finally
        {
            // closed and forgotten even when the last line or games.json could not be written (a full disk): the next
            // EndGame must not meet the same broken recording again
            entry = null;
            if (taping is not null) taping.Tape = null;
            try { tape?.Dispose(); } finally { tape = null; }
            try { rec.Dispose(); } finally { rec = null; pipe = null; identity = null; Status = Status with { Recording = false, Context = null }; }
        }
        Notice?.Invoke("Recording saved", $"{n} lines ({why}).\n{folder}");
    }

    void CloseUnsupported()
    {
        unsupported?.Dispose(); unsupported = null;
        Status = Status with { UnsupportedBackingUp = false, UnsupportedKek = false };
    }

    /// <summary>Starts keeping this Civ's autosaves locally (no memory read, no upload; a backup already kept for the same
    /// process and DLL goes on) and shows it in the window.</summary>
    void StartUnsupported(int processId, string? coreHash, string reason)
    {
        if (unsupported is null || unsupported.CoreHash != coreHash || unsupportedProcess != processId)
        {
            CloseUnsupported();
            unsupportedProcess = processId;
            unsupported = new UnsupportedBackup(settings.OutputFolder, processId, coreHash, reason);
        }
        ShowUnsupported();
    }

    void ShowUnsupported() => Status = Status with
    {
        UnsupportedBackingUp = unsupported is not null && !unsupported.KekExcluded, UnsupportedKek = unsupported?.KekExcluded == true,
        UnsupportedFolder = unsupported?.Folder, UnsupportedSaves = unsupported?.Saves ?? 0,
    };

    void Detach(string why, uint? exitCode = null)
    {
        turnQuiet.Reset();
        bool civClosed = why == "Civ closed";
        if (civClosed)
        {
            // NTSTATUS error codes (0xC0000005 access violation ...) mean Civ crashed; 0 or a small code is a normal close
            bool crashed = exitCode is uint c && c >= 0xC0000000;
            EndGame(crashed ? "Civ crashed" : "Civ closed", crashed ? "civ_crashed" : "civ_closed",
                new() { ["exitCode"] = exitCode is uint e ? $"0x{e:X8}" : null });
        }
        else if (why == "recorder closed") EndGame(why, "recorder_closed");
        else EndGame(why);
        if (mem is not null && civClosed) Notice?.Invoke("Civ closed", "The Outside Recorder waits for the next game.");
        // the files-only capture of saves that came without a memory recording ends with Civ (the next Civ starts afresh)
        if (civClosed) { saveOnly.Close(); filesPaused = false; }
        mem?.Dispose(); mem = null; reader = null; taping = null; found = null;
        Status = Status.CivGone();
    }

    /// <summary>What the capture thread closes when it ends, and <see cref="Dispose"/> when there never was one.</summary>
    void CloseOwnedFiles()
    {
        try { CloseUnsupported(); } catch (Exception e) { Diagnostics?.Error("Unsupported backup at close", e); }
        try { channel?.Dispose(); } catch (Exception e) { Diagnostics?.Error("Channel at close", e); }
        channel = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        stop.Cancel();
        // a capture thread that does not end in time still uses the backup, the channel and the pending list: it closes
        // them itself when it ends (the end of Loop), never here under its feet
        if (thread is not null && !thread.Join(3000)) return;
        if (thread is null) PersistPendingCopies(force: true);
        CloseOwnedFiles();
    }
}
