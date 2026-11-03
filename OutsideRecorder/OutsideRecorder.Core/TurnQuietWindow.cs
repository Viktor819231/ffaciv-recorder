namespace OutsideRecorderCore;

/// <summary>Defers detailed capture and new upload work after an observed turn change.</summary>
public sealed class TurnQuietWindow(int durationMs, Func<long> clock)
{
    readonly object gate = new();
    int? lastTurn;
    long untilMs;

    /// <summary>Header-only observations do not extend the pause unless the turn changes again.</summary>
    public bool Observe(int turn)
    {
        lock (gate)
        {
            if (lastTurn == turn) return false;
            lastTurn = turn;
            Interlocked.Exchange(ref untilMs, clock() + Math.Max(0, durationMs));
            return durationMs > 0;
        }
    }

    /// <summary>Read by capture and upload workers; already-running work may finish.</summary>
    public bool IsQuiet => clock() < Interlocked.Read(ref untilMs);

    /// <summary>Detaching or disabling capture discards the old game's turn and outstanding pause.</summary>
    public void Reset() { lock (gate) { lastTurn = null; Interlocked.Exchange(ref untilMs, 0); } }
}
