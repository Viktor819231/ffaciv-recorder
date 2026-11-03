namespace OutsideRecorderCore;

/// <summary>A cached, explicitly requested local crash lookup; never samples game memory or sends a report.</summary>
public sealed class CrashDiagnostics(Func<DateTimeOffset, DateTimeOffset, CrashEvidenceResult>? lookup = null,
    Func<DateTimeOffset>? clock = null)
{
    readonly object gate = new();
    int busy;
    DateTimeOffset? checkedAt;
    CrashEvidenceResult? result;

    public (bool Busy, DateTimeOffset? CheckedAt, CrashEvidenceResult? Result) Snapshot
    {
        get { lock (gate) return (Volatile.Read(ref busy) != 0, checkedAt, result); }
    }

    /// <summary>Checks at most the last day off the UI thread; concurrent clicks share the current lookup.</summary>
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref busy, 1) != 0) return;
        try
        {
            var until = clock?.Invoke() ?? DateTimeOffset.UtcNow;
            CrashEvidenceResult read;
            try { read = await Task.Run(() => (lookup ?? WindowsCrashEvidence.Read)(until.AddHours(-24), until)); }
            catch { read = new("Windows crash records unavailable", []); }
            lock (gate) { checkedAt = until; result = read with { Crashes = read.Crashes.Take(32).ToArray() }; }
        }
        finally { Volatile.Write(ref busy, 0); }
    }
}
