namespace OutsideRecorderCore;

/// <summary>An explicit mid-game stream starts at one settled copy, retaining older copies and upload receipts.</summary>
public sealed class LiveSaveWindow
{
    readonly string context;
    readonly HashSet<string> excluded;
    public string Baseline { get; }

    public LiveSaveWindow(string context, IReadOnlyList<string> oldestFirst)
    {
        if (oldestFirst.Count == 0) throw new InvalidOperationException("Wait for the first settled autosave before streaming this game.");
        this.context = context;
        Baseline = Path.GetFileName(oldestFirst[^1]);
        excluded = oldestFirst.Take(oldestFirst.Count - 1).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool Includes(string recording, string file) => recording != context || !excluded.Contains(Path.GetFileName(file));
    public bool NeedsFullBaseline(string recording, string file) => recording == context && string.Equals(Path.GetFileName(file), Baseline, StringComparison.OrdinalIgnoreCase);
}
