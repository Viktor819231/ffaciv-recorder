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

    /// <summary>Whether Civ is open, without inspecting its modules or reading its memory.</summary>
    public static bool CivRunning()
    {
        var processes = GameFinder.CivProcesses().ToArray();
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
