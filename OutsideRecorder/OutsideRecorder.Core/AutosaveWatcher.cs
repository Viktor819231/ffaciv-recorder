namespace OutsideRecorderCore;

/// <summary>A new or rewritten autosave file that has stopped growing.</summary>
public sealed record AutosaveSeen(string Path, long Size, DateTime WriteTimeUtc);

/// <summary>
/// Notices new autosaves in a few folders. Polls (no FileSystemWatcher: it misses events on some drives
/// and the game writes the file in several steps); a file is reported once its size and time stayed the same
/// for one poll. Only reads directory listings; never opens, copies or changes a save.
/// </summary>
public sealed class AutosaveWatcher
{
    readonly IReadOnlyList<string> folders;
    readonly TimeSpan settleFor;
    readonly Func<DateTime> utcNow;
    readonly Dictionary<string,DateTime> stableSince=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (long Size, DateTime Time)> reported = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (long Size, DateTime Time)> pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Watches these folders (missing ones are fine and checked again each poll).</summary>
    public AutosaveWatcher(IReadOnlyList<string> folders, bool ignoreExisting = true, TimeSpan settleFor = default, Func<DateTime>? utcNow = null)
    {
        this.folders = folders; this.settleFor=settleFor; this.utcNow=utcNow??(()=>DateTime.UtcNow);
        if (ignoreExisting) foreach (var f in List()) reported[f.FullName] = (f.Length, f.LastWriteTimeUtc);
    }

    /// <summary>The folders being watched.</summary>
    public IReadOnlyList<string> Folders => folders;

    /// <summary>A bounded metadata snapshot for shutdown recovery; no save contents are read.</summary>
    public IReadOnlyList<AutosaveSeen> PendingFiles(int maximum) => pending
        .OrderByDescending(p => p.Value.Time).Take(Math.Max(0, maximum))
        .Select(p => new AutosaveSeen(p.Key, p.Value.Size, p.Value.Time)).ToArray();

    /// <summary>
    /// Reobserves only previously pending files in the watched folders whose metadata still matches.
    /// Even a matching file starts its full settling period again; unrelated existing saves remain ignored.
    /// </summary>
    public IReadOnlyList<AutosaveSeen> RestorePending(IEnumerable<AutosaveSeen> saves, int maximum = 512)
    {
        var accepted = new List<AutosaveSeen>();
        var allowed = folders.Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var save in saves.Take(Math.Max(0, maximum)))
        {
            try
            {
                if (save is null || save.Size <= 0 || string.IsNullOrWhiteSpace(save.Path)) continue;
                var full = Path.GetFullPath(save.Path);
                if (!allowed.Contains(Path.GetDirectoryName(full) ?? "") || !Path.GetExtension(full).Equals(".Civ5Save", StringComparison.OrdinalIgnoreCase)) continue;
                var current = new FileInfo(full);
                if (!current.Exists || current.Length != save.Size || current.LastWriteTimeUtc != save.WriteTimeUtc) continue;
                reported.Remove(full);
                pending[full] = (save.Size, save.WriteTimeUtc);
                stableSince[full] = utcNow();
                accepted.Add(save with { Path = full });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return accepted;
    }

    IEnumerable<FileInfo> List() => folders.Where(Directory.Exists)
        .SelectMany(d => { try { return new DirectoryInfo(d).GetFiles("*.Civ5Save"); } catch { return []; } });

    /// <summary>
    /// Observes metadata without opening saves. Deferred or excess ready files remain pending for a later poll;
    /// this lets a busy recorder postpone copies without consuming the notification or its retry budget.
    /// </summary>
    public List<AutosaveSeen> Poll(bool reportReady = true, int maxReports = int.MaxValue)
    {
        var done = new List<AutosaveSeen>();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in List())
        {
            present.Add(f.FullName);
            var now = (f.Length, f.LastWriteTimeUtc);
            if (reported.TryGetValue(f.FullName, out var r) && r == now) continue;
            if (pending.TryGetValue(f.FullName, out var p) && p == now && f.Length > 0)
            {
                if (utcNow()-stableSince.GetValueOrDefault(f.FullName,utcNow()) < settleFor) continue;
                if (!reportReady || done.Count >= maxReports) continue;
                pending.Remove(f.FullName); stableSince.Remove(f.FullName);
                reported[f.FullName] = now;
                done.Add(new AutosaveSeen(f.FullName, f.Length, f.LastWriteTimeUtc));
            }
            else { pending[f.FullName] = now; stableSince[f.FullName]=utcNow(); }
        }
        // Civ rotates autosaves. Do not retain metadata for every file ever seen, and allow a deleted name to reappear.
        foreach (var path in reported.Keys.Where(p => !present.Contains(p)).ToArray()) reported.Remove(path);
        foreach (var path in pending.Keys.Where(p => !present.Contains(p)).ToArray())
        { pending.Remove(path); stableSince.Remove(path); }
        return done;
    }
}
