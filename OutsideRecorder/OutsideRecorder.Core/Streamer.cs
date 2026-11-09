using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OutsideRecorderCore;

/// <summary>What the window shows about the stream.</summary>
public sealed record StreamStatus
{
    /// <summary>Null before config is known; false for older sites or missing timed-share schema.</summary>
    public bool? TimedShares { get; init; }
    /// <summary>"off", "connected", "server not reachable", "queued" …</summary>
    public string State { get; init; } = "off";
    /// <summary>Bytes of events.jsonl / saves still waiting to be sent.</summary>
    public long QueuedEventBytes { get; init; }
    /// <summary>Saves still waiting.</summary>
    public int QueuedSaves { get; init; }
    /// <summary>When something was last accepted by the server.</summary>
    public DateTimeOffset? LastSent { get; init; }
    /// <summary>Bytes on the wire so far: event chunks (gzip).</summary>
    public long EventWireBytes { get; init; }
    /// <summary>Bytes on the wire so far: saves (full + delta).</summary>
    public long SaveWireBytes { get; init; }
    /// <summary>Saves sent whole / as a delta.</summary>
    public int FullSaves { get; init; }
    /// <summary>Saves sent as a delta.</summary>
    public int DeltaSaves { get; init; }
    /// <summary>HTTP requests made (including retries).</summary>
    public long Requests { get; init; }
    /// <summary>Event chunks accepted.</summary>
    public long Chunks { get; init; }
    /// <summary>Time spent packing chunks (gzip + SHA-256, one thread), ms in total.</summary>
    public double ChunkCpuMs { get; init; }
    /// <summary>Time spent making save deltas (unpack both saves + diff, one thread), ms in total.</summary>
    public double DeltaCpuMs { get; init; }
    /// <summary>The last problem, or null.</summary>
    public string? LastError { get; init; }
    /// <summary>
    /// What stops sending, in a word the window turns into plain words: null (none), "unreachable", "cap" (the daily
    /// allowance; <see cref="Until"/>), "update" (this version is too old; <see cref="DownloadUrl"/>), "blocked", "off"
    /// (sending switched off on the site), "key" (the site does not know this install's key).
    /// </summary>
    public string? Problem { get; init; }
    /// <summary>When sending may go on (cap), or null.</summary>
    public DateTimeOffset? Until { get; init; }
    /// <summary>Where the new version is (update), or null.</summary>
    public string? DownloadUrl { get; init; }
    /// <summary>The oldest version the site takes (update), or null.</summary>
    public string? MinVersion { get; init; }
    /// <summary>Save copies and recordings removed after the server confirmed them (keep_copies = off).</summary>
    public int CleanedFiles { get; init; }
}

/// <summary>The site's answer when it refused a save copy: when, the HTTP status, and its own words (the Saves list shows it).</summary>
public sealed record SaveRefusal(DateTimeOffset At, int Status, string Why);

/// <summary>Per recording folder: how far the stream got (stream.json). The files themselves are the queue.</summary>
public sealed record StreamState
{
    /// <summary>Bytes of events.jsonl the server confirmed.</summary>
    public long SentOffset { get; init; }
    /// <summary>Save copies (file names under saves/) the server confirmed, with the id it gave.</summary>
    public Dictionary<string, string> SentSaves { get; init; } = new();
    /// <summary>The last save sent (the base for the next delta), and its server id.</summary>
    public string? BaseFile { get; init; }
    /// <summary>The server's id of <see cref="BaseFile"/>.</summary>
    public string? BaseId { get; init; }
    /// <summary>Saves sent since the last full one.</summary>
    public int SinceFull { get; init; }
    /// <summary>Length of the chunk on its way at <see cref="SentOffset"/> (written before sending; null when none).</summary>
    public int? PendingLength { get; init; }
}

/// <summary>
/// "stream = local | site": sends each recording's events.jsonl in gzip chunks (the site's recorder chunk API:
/// POST recordings/&lt;context&gt;/chunks/&lt;offset&gt;) every few seconds and its autosave copies (POST saves, whole or
/// as a delta). Nothing is kept in memory only: what was confirmed is written to stream.json, so a server outage, a
/// recorder restart or a Civ restart just continues where it stopped. Every chunk carries its byte offset, so a resend is
/// answered "duplicate" and nothing is counted twice.
/// </summary>
public sealed partial class Streamer : IDisposable
{
    /// <summary>The site's chunk limit (decoded bytes).</summary>
    public const int ChunkBytes = 256 * 1024;
    readonly RecorderSettings settings;
    readonly HttpClient http;
    readonly CancellationTokenSource stop = new();
    readonly string version;
    readonly SemaphoreSlim registering = new(1, 1);
    (string Id, string Key)? install;
    DateTime retryAfter = DateTime.MinValue;
    (string File, SaveParts Parts)? lastParts;
    readonly Sender events, saves;
    Thread? watchdog;
    bool started;
    /// <summary>Automatic upload gate, checked before each event chunk/save and on the final shutdown pass.</summary>
    public Func<bool> CanUpload { get; set; } = () => true;
    /// <summary>Per-game local consent/countdown gate, checked before reads, packing and replay changes.</summary>
    public Func<GameEntry, bool> CanUploadGame { get; set; } = _ => true;
    /// <summary>Consent may exclude old save copies from a private stream without marking them sent.</summary>
    public Func<GameEntry, string, bool> CanUploadSave { get; set; } = (_, _) => true;
    /// <summary>A mid-game starting save is self-contained even if an older delta base exists.</summary>
    public Func<GameEntry, string, bool> ForceFullSave { get; set; } = (_, _) => false;
    public Func<int>? UploadRateKilobytes { get; set; }
    public Func<int>? SavePassLimit { get; set; }
    public Func<int>? EventPassLimit { get; set; }
    /// <summary>Applies to manual actions, status checks and registration as well as automatic senders.</summary>
    public Func<bool> CanContactServer { get; set; } = () => true;
    /// <summary>Shared resource gate checked before packing or reading queued uploads.</summary>
    public WorkloadPolicy? Workload { get; set; }
    /// <summary>Local, bounded diagnostics; never uploaded automatically.</summary>
    public SessionDiagnostics? Diagnostics { get; set; }
    readonly SemaphoreSlim heavyWork = new(1, 1);
    readonly UploadBudget uploadBudget = new();
    readonly AsyncLocal<Sender?> activeSender = new();
    /// <summary>Preparation/upload passes share one work lease; a private stream bounds each pass to keep the game responsive.</summary>
    internal async Task<IDisposable> EnterHeavyWorkAsync(CancellationToken ct)
    {
        while (!await heavyWork.WaitAsync(1000, ct))
            if (activeSender.Value is { } waiting) waiting.Heartbeat = Environment.TickCount64;
        if (!CanContactServer() || Workload?.CanStartHeavyWork == false)
        { heavyWork.Release(); throw new WorkDeferredException("Processing waits until the game and computer are ready."); }
        return new WorkLease(heavyWork);
    }
    sealed class WorkLease(SemaphoreSlim? gate) : IDisposable
    {
        int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate?.Release(); }
    }
    volatile bool uploadsSuspended;
    /// <summary>Stops new automatic sends before this sender is replaced; no shutdown flush can override it.</summary>
    public void SuspendUploads() => uploadsSuspended = true;
    bool MayUpload => !uploadsSuspended && CanUpload() && Workload?.CanStartHeavyWork != false;
    /// <summary>Asked before each piece of work for one recording (and again during it): permission can close at any time.</summary>
    bool MayUploadGame(GameEntry game) => MayUpload && CanContactServer() && CanUploadGame(game);

    /// <summary>Latest status.</summary>
    public StreamStatus Status { get; private set; } = new();

    /// <summary>Longest wait for one answer from the server (a chunk, a save, a register): then it counts as a failure and is tried again.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>A sender that has not finished a pass for this long is stalled: the watchdog starts it again.</summary>
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>How often the watchdog looks.</summary>
    public TimeSpan WatchEvery { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Times the watchdog had to start a sender again.</summary>
    public int Restarts { get; private set; }

    /// <summary>One send loop (events or saves): its thread, when it last finished a pass, how many passes failed in a row.</summary>
    sealed class Sender(string name)
    {
        public readonly string Name = name;
        public int Generation, Failures;
        public long Heartbeat = Environment.TickCount64;
        public Thread? Thread;
        public bool Stalled;
    }

    /// <summary>Prepares the stream for <paramref name="settings"/> (does nothing when the stream is off).</summary>
    public Streamer(RecorderSettings settings, HttpMessageHandler? handler = null, string version = "0.3.0")
    {
        this.settings = settings;
        this.version = version;
        uploadBudget.BytesPerSecond = settings.UploadKilobytesPerSecond * 1024;
        uploadBudget.CurrentBytesPerSecond = () => (UploadRateKilobytes?.Invoke() ?? settings.UploadKilobytesPerSecond) * 1024;
        uploadBudget.CanContinue = () => CanContactServer() && Workload?.CanStartHeavyWork != false;
        uploadBudget.Written = bytes =>
        {
            Diagnostics?.AddUpload(bytes);
            if (activeSender.Value is { } sender) sender.Heartbeat = Environment.TickCount64;
        };
        CanUpload = new UploadPolicy { AfterPlaying = settings.UploadTiming == "after" }.CanSend;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;   // each request has its own timeout (RequestTimeout)
        var url = settings.StreamAddress;
        http.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
        events = new Sender("events");
        saves = new Sender("saves");
    }

    /// <summary>
    /// Starts the senders (no-op when stream = off): one for the events (every few seconds), one for the saves (the delta
    /// is made there), so a slow save never holds up the moves; and a watchdog that starts a sender again when it stops
    /// finishing passes (a hang the timeouts did not catch, a thread that died): "stalled" shows and a line goes to stream.log.
    /// </summary>
    public void Start()
    {
        if (settings.Stream == "off") return;
        started = true;
        Status = Status with { State = "starting" };
        Log($"start: {settings.StreamAddress} (version {version})");
        Launch(events);
        Launch(saves);
        watchdog = new Thread(Watch) { IsBackground = true, Name = "OutsideRecorder stream watchdog" };
        watchdog.Start();
    }

    void Launch(Sender s)
    {
        int gen = Interlocked.Increment(ref s.Generation);
        s.Heartbeat = Environment.TickCount64;
        s.Thread = new Thread(() => Loop(s, gen)) { IsBackground = true, Name = $"OutsideRecorder stream {s.Name}" };
        s.Thread.Start();
    }

    void Loop(Sender s, int gen)
    {
        while (!stop.IsCancellationRequested && s.Generation == gen)
        {
            activeSender.Value = s;
            try
            {
                if (s == events) EventsPassAsync(stop.Token).GetAwaiter().GetResult();
                else SavesPassAsync(stop.Token).GetAwaiter().GetResult();
                if (s.Failures > 0) Log($"{s.Name}: sending again after {s.Failures} failed tries");
                s.Failures = 0;
                if (Status.Problem == "unreachable") Status = Status with { Problem = null };
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }   // only the program's own stop ends a sender
            catch (Exception e) when (ResourceDeferred(e)) { s.Failures = 0; }
            catch (Exception e) { Diagnostics?.Error("upload", e); Fail(s, e is OperationCanceledException ? "no answer in time" : e.Message, e); }
            s.Heartbeat = Environment.TickCount64;
            s.Stalled = false;
            UpdateState();
            int wait = s.Failures == 0 ? settings.StreamIntervalS * 1000 : (int)Math.Min(60_000, 1000 * Math.Pow(2, Math.Min(s.Failures, 6)));
            if (DateTime.UtcNow < retryAfter) wait = Math.Max(wait, (int)(retryAfter - DateTime.UtcNow).TotalMilliseconds);
            // waiting is not stalling: the heartbeat goes on while the sender waits for its next pass
            for (long until = Environment.TickCount64 + wait; Environment.TickCount64 < until && !stop.IsCancellationRequested && s.Generation == gen;)
            {
                stop.Token.WaitHandle.WaitOne((int)Math.Min(1000, Math.Max(1, until - Environment.TickCount64)));
                s.Heartbeat = Environment.TickCount64;
            }
        }
    }

    void Watch()
    {
        long lastLook = Environment.TickCount64;
        while (!stop.Token.WaitHandle.WaitOne(WatchEvery))
        {
            // the clock counts a PC sleep, a sender's request does not move during it: after a sleep the senders get a
            // fresh start instead of "stalled – restart recorder" (with safe capture that held every upload until a restart)
            long now = Environment.TickCount64;
            bool slept = now - lastLook > WatchEvery.TotalMilliseconds + SleepGapMs;
            lastLook = now;
            if (slept) { events.Heartbeat = saves.Heartbeat = now; continue; }
            foreach (var s in new[] { events, saves })
            {
                if (settings.SafeCapture && s.Stalled) continue;
                bool dead = s.Thread is { IsAlive: false };
                long quiet = Environment.TickCount64 - s.Heartbeat;
                if (!dead && quiet < StallAfter.TotalMilliseconds) continue;
                s.Stalled = true;
                if (settings.SafeCapture)
                {
                    uploadsSuspended = true;
                    Diagnostics?.Activity("Upload sender stalled", "Queued files retained; restart the recorder to retry");
                    Status = Status with { State = "stalled – restart recorder", LastError = "An upload worker stopped responding. Queued files are retained. Restart the recorder to retry." };
                    continue;
                }
                Restarts++;
                Log($"{s.Name}: STALLED ({(dead ? "the sender stopped" : $"no pass finished for {quiet / 1000} s")}); starting it again");
                Status = Status with { LastError = $"{DateTime.Now:HH:mm:ss} {s.Name} sender stalled, started again" };
                UpdateState();
                Launch(s);
            }
        }
    }

    /// <summary>A gap this much longer than <see cref="WatchEvery"/> between two watchdog looks means the PC slept (or the
    /// program was frozen), not that a sender hung.</summary>
    const int SleepGapMs = 60_000;

    void Fail(Sender s, string why, Exception? e = null)
    {
        s.Failures++;
        if (e is TimeoutException || e is HttpRequestException { StatusCode: null } && Status.Problem is null or "unreachable") Status = Status with { Problem = "unreachable" };
        if (s.Failures == 1 || s.Failures % 10 == 0) Log($"{s.Name}: {why} (try {s.Failures})");
        Status = Status with { LastError = $"{DateTime.Now:HH:mm:ss} {why}" };
    }

    long queuedEventBytes; int queuedSaves;

    void UpdateState()
    {
        string state = events.Stalled || saves.Stalled ? settings.SafeCapture ? "stalled – restart recorder" : "stalled – restarting the sender"
            : events.Failures > 0 || saves.Failures > 0 ? "server not reachable – queued, retrying"
            : queuedEventBytes > 0 || queuedSaves > 0 ? "connected – sending" : "connected";
        Status = Status with { State = state, QueuedEventBytes = queuedEventBytes, QueuedSaves = queuedSaves };
    }

    /// <summary>A line in stream.log next to the recordings (kept small: the older half goes when it passes 1 MB).</summary>
    void Log(string line)
    {
        try
        {
            var f = Path.Combine(settings.OutputFolder, "stream.log");
            lock (logLock)
            {
                Directory.CreateDirectory(settings.OutputFolder);
                if (File.Exists(f) && new FileInfo(f).Length > 1024 * 1024) File.Move(f, f + ".old", overwrite: true);
                File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
    static readonly object logLock = new();

    /// <summary>One pass over every recording folder: events, then saves, then the events written meanwhile. Public for the tests and the stream test.</summary>
    public async Task PassAsync(CancellationToken ct)
    {
        await EventsPassAsync(ct);
        await SavesPassAsync(ct);
        await EventsPassAsync(ct);
        events.Failures = saves.Failures = 0;
        UpdateState();
    }

    /// <summary>
    /// Stops the stream after one last pass (at most <paramref name="finalPass"/>), so the lines written while closing
    /// (game_left, stopped, recorder_closed) reach the server before the program ends. Call after the recorder stopped.
    /// </summary>
    public void Finish(TimeSpan finalPass)
    {
        stop.Cancel();
        foreach (var t in new[] { events.Thread, saves.Thread, watchdog }) t?.Join(5000);
        if (!started || finished) return;
        finished = true;
        using var cts = new CancellationTokenSource(finalPass);
        try { PassAsync(cts.Token).GetAwaiter().GetResult(); Log("stop: last lines sent"); }
        catch (Exception e) { Status = Status with { LastError = $"{DateTime.Now:HH:mm:ss} last send: {e.Message}" }; Log($"stop: last send failed: {e.Message}"); }
    }
    bool finished;

    /// <inheritdoc/>
    public void Dispose() { Finish(TimeSpan.FromSeconds(6)); http.Dispose(); }
}
