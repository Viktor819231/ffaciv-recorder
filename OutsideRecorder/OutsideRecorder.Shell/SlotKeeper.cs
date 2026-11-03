using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>
/// Keeps the live slot of the game being recorded (OR05 "busy line"): "take" when a game is recorded, again every
/// renewEverySeconds (granted) or retryAfterSeconds (waiting, to keep the place); "request" when the player presses
/// "Request a livestream"; "release" when the game ends. The recording and the upload never wait for any of this.
/// </summary>
sealed class SlotKeeper : IDisposable
{
    readonly Func<Streamer?> streamer;
    readonly Func<RecorderStatus> status;
    readonly System.Threading.Timer timer;
    readonly object gate = new();
    volatile string? game;
    string? channel;
    bool busy, requestNow;
    long nextAt;
    volatile bool enabled;
    /// <summary>A live slot is requested only after the player explicitly starts a stream on Recorder.</summary>
    public bool Enabled => enabled;
    public bool ReleasePending => !enabled && game is not null;
    public Func<RecorderStatus, Task<bool>> PrepareContext { get; init; } = _ => Task.FromResult(true);
    public Func<RecorderStatus, Task<bool>> BindShare { get; init; } = _ => Task.FromResult(true);
    public Action? CheckConsent { get; init; }
    public Func<bool> CanRunControl { get; init; } = () => true;
    public void Start() { State = LiveSlotState.Unknown; enabled = true; Request(); }
    public void Stop() { enabled = false; lock (gate) { requestNow = false; nextAt = 0; } _ = Tick(); }

    /// <summary>The slot of the game being recorded now.</summary>
    public LiveSlotState State { get; private set; } = LiveSlotState.Unknown;
    /// <summary>The game this slot state belongs to; a rehost must not reuse its previous link.</summary>
    public string? Identity => game;

    public SlotKeeper(Func<Streamer?> streamer, Func<RecorderStatus> status, bool preview)
    {
        this.streamer = streamer; this.status = status;
        timer = new System.Threading.Timer(_ => _ = Tick(), null, preview ? Timeout.Infinite : 3_000, preview ? Timeout.Infinite : 5_000);
    }

    /// <summary>"Request a livestream": asked at once.</summary>
    public void Request() { lock (gate) { requestNow = true; nextAt = 0; } _ = Tick(); }

    async Task Tick()
    {
        lock (gate) { if (busy) return; busy = true; }
        try
        {
            CheckConsent?.Invoke();
            var s = status();
            var st = streamer();
            string? now = enabled && s.Recording && s.Identity is { } id && System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{8}-[0-9a-f]{8}$") ? id : null;
            if (game is not null && game != now)
            {
                if (st is null || !CanRunControl()) return; // retain the release until resource/quiet gates reopen
                if (st is not null) await st.ReleaseLiveSlotAsync(game, channel, default);   // the game ended: slot and place given back
                State = LiveSlotState.Unknown; nextAt = 0;
            }
            game = now; channel = s.Channel;
            if (now is null || st is null) { State = LiveSlotState.Unknown; return; }
            if (!await PrepareContext(s)) { State = LiveSlotState.Unknown; nextAt = 0; return; }
            bool ask;
            lock (gate) { ask = requestNow; if (!ask && Environment.TickCount64 < nextAt) return; requestNow = false; }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            State = await st.LiveSlotAsync(now, s.Channel, ask ? "request" : "take", cts.Token);
            if (State.State == "granted" && !await BindShare(s)) { nextAt = 0; return; }
            nextAt = Environment.TickCount64 + Math.Clamp(State.AskAgainSeconds, 10, 600) * 1000L;
        }
        catch { nextAt = Environment.TickCount64 + 30_000; /* no answer: the state stays; asked again in 30 s */ }
        finally { lock (gate) busy = false; }
    }

    public void Dispose() => timer.Dispose();
}
