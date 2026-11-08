namespace OutsideRecorderCore;

/// <summary>Separate automatic capture from when stored data may reach the server for parsing.</summary>
public sealed class UploadPolicy
{
    /// <summary>Manual-only mode leaves automatic senders idle, including old queued data.</summary>
    public volatile bool Automatic = true;
    /// <summary>Wait for Civ to close before any automatic upload or server processing.</summary>
    public volatile bool AfterPlaying;
    /// <summary>Only enumerates process names; never opens process memory.</summary>
    public Func<bool> IsPlaying { get; init; } = CivRunning;
    /// <summary>May automatic senders upload now? Explicit manual batch uploads remain available.</summary>
    public TimeSpan ExitGrace { get; init; } = TimeSpan.Zero;
    public Func<long> Clock { get; init; } = () => Environment.TickCount64;
    long idleSince = -1;
    /// <summary>A short idle grace keeps crash/relaunch cycles from starting uploads.</summary>
    public bool IsIdle()
    {
        if (IsPlaying()) { Interlocked.Exchange(ref idleSince, -1); return false; }
        Interlocked.CompareExchange(ref idleSince, Clock(), -1);
        return Clock() - Interlocked.Read(ref idleSince) >= ExitGrace.TotalMilliseconds;
    }
    public bool CanSend() => Automatic && (!AfterPlaying || IsIdle());

    /// <summary>How long one answer to "is Civ open?" serves everyone.</summary>
    public const int CivCheckMs = 2000;
    static readonly FreshAnswer civOpen = new(CivRunningNow, CivCheckMs);

    /// <summary>Whether Civ is open, without inspecting its modules or reading its memory. The window, the senders and the
    /// monitors ask this many times a second, once per game in places, and each real check makes Windows list every process
    /// and thread on the PC: that was most of the recorder's CPU use (2026-11-07), so one answer serves them all.</summary>
    public static bool CivRunning() => civOpen.Value;

    static bool CivRunningNow()
    {
        var processes = GameFinder.CivProcesses().ToArray();
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

/// <summary>A costly yes/no question asked from many places and threads: the last answer stands for a short time.</summary>
public sealed class FreshAnswer(Func<bool> ask, int keepMs, Func<long>? clock = null)
{
    readonly object gate = new();
    readonly Func<long> clock = clock ?? (() => Environment.TickCount64);
    bool answered, answer;
    long askedAt;

    /// <summary>The answer, asked again only when the last one is older than the keep time.</summary>
    public bool Value
    {
        get
        {
            lock (gate)
            {
                long now = clock();
                if (answered && now - askedAt < keepMs) return answer;
                answer = ask(); answered = true; askedAt = now;
                return answer;
            }
        }
    }
}
