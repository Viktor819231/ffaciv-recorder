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
        while (!stop.Token.WaitHandle.WaitOne(WatchEvery))
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

    /// <summary>The events of every recording (what is there now), chunk by chunk.</summary>
    public async Task EventsPassAsync(CancellationToken ct)
    {
        if (!MayUpload) return;
        using var work = await EnterHeavyWorkAsync(ct);
        if (!MayUpload) return;
        Diagnostics?.Activity("Uploading recorder events");
        await EnsureRegisteredAsync(ct);
        long queued = 0;
        foreach (var game in new GameIndex(settings.OutputFolder).Load())
        {
            if (!MayUpload || !CanContactServer()) break;
            if (!CanUploadGame(game) || !Directory.Exists(game.Folder) || DeletionPending(game)) continue;
            var state = await SendEventsAsync(game, Load(game.Folder), ct);
            var file = Path.Combine(game.Folder, "events.jsonl");
            queued += File.Exists(file) ? Math.Max(0, new FileInfo(file).Length - state.SentOffset) : 0;
        }
        queuedEventBytes = queued;
    }

    /// <summary>The saves of every recording not sent yet, oldest first (each as a delta of the one before when that is smaller).</summary>
    public async Task SavesPassAsync(CancellationToken ct)
    {
        if (!MayUpload) return;
        using var work = await EnterHeavyWorkAsync(ct);
        if (!MayUpload) return;
        Diagnostics?.Activity("Processing queued saves");
        await EnsureRegisteredAsync(ct);
        await FlushReplayDeletionsAsync(ct);
        int queued = 0;
        foreach (var game in new GameIndex(settings.OutputFolder).Load())
        {
            if (!MayUpload || !CanContactServer()) break;
            if (!CanUploadGame(game) || DeletionPending(game)) continue;
            if (settings.SafeCapture && game.Identity.StartsWith("files:") && ReadReplay(game.Context)["state"]?.GetValue<string>() is not "ready" and not "processing"
                && ReadReplay(game.Context,true)["action"] is null)
                ChooseReplay(game.Context,new System.Text.Json.Nodes.JsonObject { ["action"]="complete",["keepShort"]=true,["waitForEnd"]=true });
            var state = Directory.Exists(game.Folder) ? await SendSavesAsync(game, Load(game.Folder), ct, SavePassLimit?.Invoke() ?? settings.SaveBatchLimit) : Load(game.Folder);
            await RefreshReplayAsync(game, state, ct);
            queued += SavesOf(game.Folder).Count(f => CanUploadSave(game, f) && !state.SentSaves.ContainsKey(Path.GetFileName(f)));
            if (!settings.KeepCopies && MayUpload && CanContactServer() && CanUploadGame(game)) CleanUp(game, state);
        }
        queuedSaves = queued;
    }

    /// <summary>A recording quiet this long, fully sent and ended, is removed from this PC (keep_copies = off).</summary>
    public static readonly TimeSpan CleanupAfter = TimeSpan.FromMinutes(60);

    /// <summary>
    /// keep_copies = off: a save copy goes once the server confirmed it (the last one sent stays: the next delta is made
    /// against it); a whole recording folder goes once everything in it was confirmed, it ended (its last line says so)
    /// and nothing was written to it for <see cref="CleanupAfter"/>. Channels (the lobby) are kept.
    /// </summary>
    void CleanUp(GameEntry game, StreamState state)
    {
        int n = 0;
        foreach (var f in SavesOf(game.Folder))
        {
            var name = Path.GetFileName(f);
            if (state.SentSaves.ContainsKey(name) && name != state.BaseFile) { try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        var events = Path.Combine(game.Folder, "events.jsonl");
        if (!game.Identity.StartsWith("channel:") && File.Exists(events) && Load(game.Folder).SentOffset >= new FileInfo(events).Length
            && SavesOf(game.Folder).All(f => state.SentSaves.ContainsKey(Path.GetFileName(f)))
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(events) > CleanupAfter && Ended(events))
        {
            try { if (LocalRecordingDeletion.Delete(settings.OutputFolder, game.Folder)) n++; Log($"cleanup: {Path.GetFileName(game.Folder)} sent and removed from this PC"); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (n > 0) Status = Status with { CleanedFiles = Status.CleanedFiles + n };
    }

    /// <summary>The recording's last line says it stopped (Civ closed or crashed, the recorder closed, another game, left).</summary>
    static bool Ended(string events)
    {
        if (!File.Exists(events)) return File.Exists(Path.Combine(Path.GetDirectoryName(events)!, "capture-ended"));
        try
        {
            using var f = new FileStream(events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            f.Seek(Math.Max(0, f.Length - 4096), SeekOrigin.Begin);
            var tail = new StreamReader(f).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            return tail.Contains("\"kind\":\"lifecycle\"") && System.Text.RegularExpressions.Regex.IsMatch(tail, "\"state\":\"(stopped|civ_closed|civ_crashed|recorder_closed|paused|game_left)\"");
        }
        catch { return false; }
    }

    /// <summary>Whether local capture wrote a completion marker or a final lifecycle event.</summary>
    public static bool HasEnded(GameEntry game) => Ended(Path.Combine(game.Folder, "events.jsonl"));

    async Task EnsureRegisteredAsync(CancellationToken ct)
    {
        if (install is not null && (!settings.RequireReplayLifecycle || replayLifecycleChecked)) return;
        await registering.WaitAsync(ct);
        try
        {
            if (settings.RequireReplayLifecycle && !replayLifecycleChecked)
            {
                using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "config"), ct, auth: false);
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                if (r.IsSuccessStatusCode) Status = Status with { TimedShares = SupportsTimedShares(doc.RootElement) };
                if (!r.IsSuccessStatusCode || !doc.RootElement.TryGetProperty("recorder", out var recorder)
                    || !recorder.TryGetProperty("lifecycle", out var lifecycle) || lifecycle.ValueKind != JsonValueKind.True
                    || (settings.RequiredControlsVersion > 1 && (!recorder.TryGetProperty("controlsVersion", out var controls)
                        || !controls.TryGetInt32(out var controlVersion) || controlVersion < settings.RequiredControlsVersion)))
                    throw new HttpRequestException("The website needs the recorder update. Saves wait on this PC until it is ready.");
                replayLifecycleChecked = true;
            }
            install ??= await RegisterAsync(ct);
        }
        finally { registering.Release(); }
    }

    bool replayLifecycleChecked;

    /// <summary>A successfully fetched config must explicitly advertise the timed-share capability.</summary>
    public static bool SupportsTimedShares(JsonElement config) => config.ValueKind == JsonValueKind.Object
        && config.TryGetProperty("recorder", out var recorder) && recorder.ValueKind == JsonValueKind.Object
        && recorder.TryGetProperty("timedShares", out var enabled) && enabled.ValueKind == JsonValueKind.True;

    string InstallFile => Path.Combine(settings.OutputFolder, $"stream-install-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(http.BaseAddress!.ToString())))[..12]}.json");

    /// <summary>
    /// The install's id and key for this server: registered once (POST register, the uploader's install role) and kept
    /// next to the recordings with the key protected for this Windows user (<see cref="KeyStore"/>; an older plain file
    /// is protected on first use).
    /// </summary>
    async Task<(string, string)> RegisterAsync(CancellationToken ct)
    {
        var file = InstallFile;
        if (File.Exists(file))
        {
            var known = JsonSerializer.Deserialize<Dictionary<string, string>>(SafeFiles.ReadAllText(file))!;
            if (known.TryGetValue("keyProtected", out var p) && KeyStore.Unprotect(p) is { } key) return (known["id"], key);
            if (known.TryGetValue("key", out var plain))
            {
                SafeFiles.Replace(file, JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = known["id"], ["keyProtected"] = KeyStore.Protect(plain) }));
                return (known["id"], plain);
            }
            // not readable (another Windows user, another PC): register anew
        }
        var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "register")
        { Content = new StringContent(JsonSerializer.Serialize(new { version }), Encoding.UTF8, "application/json") }, ct, auth: false);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"register: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync(ct)}");
        var answer = JsonSerializer.Deserialize<Dictionary<string, string>>(await r.Content.ReadAsStringAsync(ct))!;
        Directory.CreateDirectory(settings.OutputFolder);
        SafeFiles.Replace(file, JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = answer["id"], ["keyProtected"] = KeyStore.Protect(answer["key"]) }));
        return (answer["id"], answer["key"]);
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, bool auth = true)
    {
        try { return await SendCoreAsync(make, ct, auth); }
        catch (Exception e) when (ResourceDeferred(e))
        { Diagnostics?.UploadDeferred(); throw new WorkDeferredException("Upload deferred while the game or computer is busy."); }
    }

    static bool ResourceDeferred(Exception e)
    {
        for (Exception? current = e; current is not null; current = current.InnerException)
            if (current is WorkDeferredException) return true;
        return false;
    }

    async Task<HttpResponseMessage> SendCoreAsync(Func<HttpRequestMessage> make, CancellationToken ct, bool auth)
    {
        if (Workload?.CanStartHeavyWork == false) throw new WorkDeferredException("Server work paused while the computer is busy.");
        if (!CanContactServer()) throw new HttpRequestException("Server work waits for game/menu, resource and upload permission.");
        using var request = make();
        if (request.Content is { } content) request.Content = uploadBudget.Wrap(content);
        if (auth) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", install!.Value.Key);
        request.Headers.TryAddWithoutValidation("X-Program-Version", version);
        Status = Status with { Requests = Status.Requests + 1 };
        Diagnostics?.RequestStarted();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Deliberate bandwidth pacing is not a network stall.
        double pacedSeconds = settings.UploadKilobytesPerSecond > 0 ? (request.Content?.Headers.ContentLength ?? 0) / (settings.UploadKilobytesPerSecond * 1024d) : 0;
        limit.CancelAfter(RequestTimeout + TimeSpan.FromSeconds(pacedSeconds));
        HttpResponseMessage r;
        try
        {
            r = await http.SendAsync(request, limit.Token);
            await r.Content.LoadIntoBufferAsync(limit.Token);   // the answer's body too, within the same time
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Diagnostics?.UploadFailed();
            throw new TimeoutException($"no answer from the server in {RequestTimeout.TotalSeconds:F0} s ({request.Method} {request.RequestUri})");
        }
        catch (Exception e) when (!ResourceDeferred(e)) { Diagnostics?.UploadFailed(); throw; }
        if (!r.IsSuccessStatusCode) Diagnostics?.UploadFailed();
        if (r.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var after = r.Headers.RetryAfter?.Delta ?? (r.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(10));
            retryAfter = DateTime.UtcNow + after;
            Status = Status with { Problem = "cap", Until = DateTimeOffset.UtcNow + after };
            throw new HttpRequestException($"server says wait {after.TotalMinutes:F0} min (daily allowance)");
        }
        if ((int)r.StatusCode == 426)
        {
            // this version is too old for the site: stop sending until the program is updated (everything waits on this PC)
            string? min = null, url = null;
            try { using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)); min = doc.RootElement.TryGetProperty("minVersion", out var m) ? m.GetString() : null; url = doc.RootElement.TryGetProperty("downloadUrl", out var u) ? u.GetString() : null; } catch { }
            retryAfter = DateTime.UtcNow + TimeSpan.FromHours(1);
            Status = Status with { Problem = "update", MinVersion = min, DownloadUrl = url };
            throw new HttpRequestException($"this version is too old for the site (needs {min})");
        }
        if (r.StatusCode == HttpStatusCode.Forbidden && auth && await InstallRefusal(r, ct) is var (blocked, enabled, whole) && whole)
        {
            retryAfter = DateTime.UtcNow + TimeSpan.FromMinutes(30);
            Status = Status with { Problem = blocked ? "blocked" : !enabled ? "off" : "blocked" };
            throw new HttpRequestException(blocked ? "this install is blocked on the site" : "sending is switched off on the site");
        }
        if (r.StatusCode == HttpStatusCode.Unauthorized && auth)
        {
            // the site does not know this key (a new test environment, a reset): register again on the next pass
            try { File.Delete(InstallFile); } catch { }
            install = null;
            Status = Status with { Problem = "key" };
            throw new HttpRequestException("the site did not know this install's key; registering again");
        }
        if (r.IsSuccessStatusCode && Status.Problem is not null and not "unreachable") Status = Status with { Problem = null, Until = null };
        return r;
    }

    /// <summary>
    /// A 403 that refuses this whole install (the uploader's answer carries "blocked" / "enabled": switched off or blocked)
    /// — not a 403 of one route about one game (e.g. Block of a game this install sent nothing of), which the caller handles.
    /// </summary>
    static async Task<(bool Blocked, bool Enabled, bool Whole)> InstallRefusal(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var e = doc.RootElement;
            bool hasB = e.TryGetProperty("blocked", out var b), hasE = e.TryGetProperty("enabled", out var en);
            return (hasB && b.ValueKind == JsonValueKind.True, !hasE || en.ValueKind != JsonValueKind.False, hasB || hasE);
        }
        catch { return (false, true, false); }
    }

    async Task<StreamState> SendEventsAsync(GameEntry game, StreamState state, CancellationToken ct)
    {
        if (!MayUpload || !CanContactServer() || !CanUploadGame(game)) return state;
        var path = Path.Combine(game.Folder, "events.jsonl");
        if (!File.Exists(path)) return state;
        // only what is there now: a game writing faster than we send (a fast replay, a busy turn) must not keep this
        // loop chasing the end of the file while the saves behind it wait
        int sentChunks = 0;
        long until = new FileInfo(path).Length;
        while (!ct.IsCancellationRequested)
        {
            if (!MayUpload || !CanContactServer() || !CanUploadGame(game)) return state;
            byte[] chunk;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (Math.Min(f.Length, until) <= state.SentOffset && state.PendingLength is null) return state;
                if (f.Length <= state.SentOffset) return state;
                f.Seek(state.SentOffset, SeekOrigin.Begin);
                if (state.PendingLength is int pending)
                {
                    // the chunk that was on its way when the program stopped: the very same bytes again ("duplicate" if it arrived)
                    chunk = new byte[pending];
                    f.ReadExactly(chunk);
                }
                else
                {
                    var buf = new byte[(int)Math.Min(ChunkBytes, Math.Min(f.Length, until) - state.SentOffset)];
                    f.ReadExactly(buf);
                    int end = Array.LastIndexOf(buf, (byte)'\n') + 1; // whole lines only
                    if (end == 0)
                    {
                        if (buf.Length == ChunkBytes) throw new InvalidDataException($"a line longer than {ChunkBytes} bytes in {path}");
                        return state; // the writer is in the middle of a line
                    }
                    chunk = buf[..end];
                    // written before sending: never a longer chunk at an offset the server may already hold
                    state = state with { PendingLength = chunk.Length };
                    SaveEvents(game.Folder, state);
                }
            }
            if (!MayUpload || !CanContactServer() || !CanUploadGame(game)) return state;
            var sw = Stopwatch.StartNew();
            var hash = Convert.ToHexStringLower(SHA256.HashData(chunk));
            using var gz = new MemoryStream();
            if (!MayUpload || !CanContactServer() || !CanUploadGame(game)) return state;
            using (var z = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true)) z.Write(chunk);
            var wire = gz.ToArray();
            if (!MayUpload || !CanContactServer() || !CanUploadGame(game)) return state;
            var cpu = sw.Elapsed.TotalMilliseconds; // one thread, busy the whole time: wall time = CPU time of packing
            long offset = state.SentOffset;
            var r = await SendAsync(() =>
            {
                var m = new HttpRequestMessage(HttpMethod.Post, $"recordings/{game.Context}/chunks/{offset}") { Content = new ByteArrayContent(wire) };
                m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                m.Headers.TryAddWithoutValidation("X-Recording-Encoding", "gzip");
                m.Headers.TryAddWithoutValidation("X-Content-SHA256", hash);
                m.Headers.TryAddWithoutValidation("X-Game-Identity", game.Identity);
                if (!game.Identity.StartsWith("channel:")) m.Headers.TryAddWithoutValidation("X-Recorder-Lifecycle", "1");
                return m;
            }, ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode) throw new HttpRequestException($"chunk {offset}: {(int)r.StatusCode} {body}");
            state = state with { SentOffset = offset + chunk.Length, PendingLength = null };
            SaveEvents(game.Folder, state);
            Status = Status with
            {
                EventWireBytes = Status.EventWireBytes + wire.Length, Chunks = Status.Chunks + 1, LastSent = DateTimeOffset.Now,
                ChunkCpuMs = Status.ChunkCpuMs + Math.Max(cpu, 0), LastError = Status.LastError,
            };
            if (++sentChunks >= (EventPassLimit?.Invoke() ?? settings.EventChunkLimit)) return state;
        }
        return state;
    }

    /// <summary>The save copies of a recording, oldest first.</summary>
    public static List<string> SavesOf(string folder)
    {
        var dir = Path.Combine(folder, "saves");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.Civ5Save").OrderBy(File.GetLastWriteTimeUtc).ThenBy(f => f).ToList() : [];
    }

    readonly Dictionary<string, int> deltaTries = [];

    async Task<StreamState> SendSavesAsync(GameEntry game, StreamState state, CancellationToken ct, int max = int.MaxValue)
    {
        if (!MayUpload || !CanContactServer() || !CanUploadGame(game)) return state;
        int sentNow = 0;
        foreach (var file in SavesOf(game.Folder))
        {
            if (!MayUpload || !CanContactServer() || !CanUploadGame(game)) break;
            if (sentNow >= Math.Min(max, SavePassLimit?.Invoke() ?? int.MaxValue)) break;
            var name = Path.GetFileName(file);
            if (state.SentSaves.ContainsKey(name) || !CanUploadSave(game, file)) continue;
            // copies are written as .part and renamed (SafeFiles.CopySave), so a listed save is whole; one another program
            // holds right now waits for the next pass instead of failing the pass (and the events behind it)
            byte[] bytes;
            try { bytes = SafeFiles.ReadAllBytes(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = Status with { LastError = $"{DateTime.Now:HH:mm:ss} {name}: {e.Message} (next pass)" }; break; }
            string? id = null; bool asDelta = false; int wireBytes = 0;
            var baseFile = state.BaseFile is null ? null : Path.Combine(game.Folder, "saves", state.BaseFile);
            // a delta the server failed on twice (no answer, an error) goes whole: one bad delta never holds the saves up
            bool full = ForceFullSave(game, file) || settings.SaveUpload != "delta" || state.BaseId is null || baseFile is null || !File.Exists(baseFile)
                || state.SinceFull + 1 >= settings.FullSaveEvery || deltaTries.GetValueOrDefault(name) >= 2;
            if (!full)
            {
                if (!MayUpload || !CanContactServer() || !CanUploadGame(game)) return state;
                var sw = Stopwatch.StartNew();
                // the base was the last save sent: its unpacked parts are usually still here
                var baseParts = lastParts is { } lp && lp.File == state.BaseFile ? lp.Parts : SaveParts.Split(SafeFiles.ReadAllBytes(baseFile!));
                var parts = SaveParts.Split(bytes);
                if (parts is not null) lastParts = (name, parts);
                byte[]? delta = baseParts is null || parts is null ? null : SaveDelta.Make(baseParts, parts, bytes);
                Status = Status with { DeltaCpuMs = Status.DeltaCpuMs + sw.Elapsed.TotalMilliseconds };
                if (delta is not null && delta.Length < bytes.Length * 0.8)
                {
                    deltaTries[name] = deltaTries.GetValueOrDefault(name) + 1;
                    var (ok, answerId, needFull) = await PostSaveAsync(game, name, file, delta, state.BaseId, ct);
                    if (ok) { id = answerId; asDelta = true; wireBytes = delta.Length; }
                    else if (!needFull) return state;
                }
            }
            if (id is null)
            {
                var (ok, answerId, _) = await PostSaveAsync(game, name, file, bytes, null, ct);
                if (!ok) return state;
                id = answerId; wireBytes = bytes.Length;
            }
            sentNow++;
            var sent = new Dictionary<string, string>(state.SentSaves) { [name] = id! };
            state = state with { SentSaves = sent, BaseFile = name, BaseId = id, SinceFull = asDelta ? state.SinceFull + 1 : 0 };
            SaveSaves(game.Folder, state);
            Status = Status with
            {
                SaveWireBytes = Status.SaveWireBytes + wireBytes, LastSent = DateTimeOffset.Now,
                FullSaves = Status.FullSaves + (asDelta ? 0 : 1), DeltaSaves = Status.DeltaSaves + (asDelta ? 1 : 0),
            };
        }
        return state;
    }

    async Task<(bool Ok, string? Id, bool NeedFull)> PostSaveAsync(GameEntry game, string name, string file, byte[] body, string? deltaBase, CancellationToken ct)
    {
        if (!MayUpload || !CanContactServer() || !CanUploadGame(game) || !CanUploadSave(game, file)) return (false, null, false);
        var r = await SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "saves") { Content = new ByteArrayContent(body) };
            m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            m.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString(OriginalName(name)));
            m.Headers.TryAddWithoutValidation("X-Folder", "outside-recorder");
            m.Headers.TryAddWithoutValidation("X-File-Time", new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds().ToString());
            m.Headers.TryAddWithoutValidation("X-Recording-Id", game.Context);
            if (!game.Identity.StartsWith("files:", StringComparison.Ordinal)) m.Headers.TryAddWithoutValidation("X-Game-Identity", game.Identity);
            if (!game.Identity.StartsWith("channel:")) m.Headers.TryAddWithoutValidation("X-Recorder-Lifecycle", "1");
            if (deltaBase is not null)
            {
                m.Headers.TryAddWithoutValidation("X-Save-Encoding", "orsd1");
                m.Headers.TryAddWithoutValidation("X-Delta-Base", deltaBase);
            }
            return m;
        }, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (r.StatusCode == HttpStatusCode.Conflict && deltaBase is not null && text.Contains("needFull")) return (false, null, true);
        if (!r.IsSuccessStatusCode)
        {
            refusals[Path.GetFullPath(file)] = new SaveRefusal(DateTimeOffset.UtcNow, (int)r.StatusCode, SiteWords(text));
            throw new HttpRequestException($"save {name}: {(int)r.StatusCode} {text}");
        }
        refusals.TryRemove(Path.GetFullPath(file), out _);
        using var doc = JsonDocument.Parse(text);
        return (true, doc.RootElement.GetProperty("id").GetString(), false);
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, SaveRefusal> refusals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the site last refused this save copy (null: never, or it took it since). Kept while the recorder runs.</summary>
    public SaveRefusal? RefusalOf(string file) => refusals.TryGetValue(Path.GetFullPath(file), out var r) ? r : null;

    /// <summary>The site's own words from an error answer ({"error": "…"}), else the start of the answer.</summary>
    static string SiteWords(string text)
    {
        try { if (System.Text.Json.Nodes.JsonNode.Parse(text)?["error"]?.GetValue<string>() is { Length: > 0 } words) return words; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        text = text.Trim();
        return text.Length == 0 ? "no reason given" : text.Length > 200 ? text[..200] + "…" : text;
    }

    /// <summary>"AutoSave_0013 BC-3220 (114502).Civ5Save" (a second copy of one name) -> "AutoSave_0013 BC-3220.Civ5Save".</summary>
    public static string OriginalName(string name) => Regex.Replace(name, @" \(\d{6}\)(?=\.Civ5Save$)", "", RegexOptions.IgnoreCase);

    static StreamState Load(string folder)
    {
        var f = Path.Combine(folder, "stream.json");
        try { return File.Exists(f) ? JsonSerializer.Deserialize<StreamState>(SafeFiles.ReadAllText(f)) ?? new() : new(); }
        catch { return new(); }
    }

    static readonly object stateLock = new();

    /// <summary>The events sender's part of stream.json (how far the events got); the saves' part stays as it is on disk.</summary>
    static void SaveEvents(string folder, StreamState mine)
    {
        lock (stateLock) Save(folder, Load(folder) with { SentOffset = mine.SentOffset, PendingLength = mine.PendingLength });
    }

    /// <summary>The saves sender's part of stream.json (which saves, the delta base).</summary>
    static void SaveSaves(string folder, StreamState mine)
    {
        lock (stateLock) Save(folder, Load(folder) with { SentSaves = mine.SentSaves, BaseFile = mine.BaseFile, BaseId = mine.BaseId, SinceFull = mine.SinceFull });
    }

    static void Save(string folder, StreamState state)
    {
        var f = Path.Combine(folder, "stream.json");
        SafeFiles.Replace(f, JsonSerializer.Serialize(state));
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
