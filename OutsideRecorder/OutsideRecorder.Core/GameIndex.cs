using System.Security.Cryptography;
using System.Text.Json;

namespace OutsideRecorderCore;

/// <summary>One recorded game: its folder, its stream id, when it was last seen.</summary>
public sealed record GameEntry
{
    /// <summary>The game's identity (<see cref="GamePipeline.IdentityOf"/>).</summary>
    public required string Identity { get; init; }
    /// <summary>The session folder.</summary>
    public required string Folder { get; init; }
    /// <summary>32 hex digits: the recording id the site knows the stream and its saves by (X-Recording-Id).</summary>
    public required string Context { get; init; }
    /// <summary>The Civ process that last played it.</summary>
    public int ProcessId { get; init; }
    /// <summary>When the recorder last wrote to it (UTC).</summary>
    public DateTimeOffset LastSeen { get; init; }
}

/// <summary>
/// games.json in the output folder: identity → recording folder. A game that comes back (Civ restarted, recorder restarted,
/// a multiplayer rejoin after a crash) continues its own folder and stream instead of starting a new one.
/// </summary>
public sealed class GameIndex(string root)
{
    /// <summary>How long a game may be away and still continue its folder.</summary>
    public static readonly TimeSpan KeepOpen = TimeSpan.FromDays(14);
    string Path => System.IO.Path.Combine(root, "games.json");

    /// <summary>All known games.</summary>
    public List<GameEntry> Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<List<GameEntry>>(SafeFiles.ReadAllText(Path)) ?? [] : []; }
        catch { return []; }
    }

    /// <summary>The game's entry when it was seen lately and its folder still exists.</summary>
    public GameEntry? Find(string identity, DateTimeOffset now) =>
        Load().LastOrDefault(e => e.Identity == identity && now - e.LastSeen < KeepOpen && Directory.Exists(e.Folder));

    /// <summary>Adds or updates an entry.</summary>
    public void Save(GameEntry entry)
    {
        var all = Load().Where(e => e.Folder != entry.Folder).Append(entry).ToList();
        Directory.CreateDirectory(root);
        SafeFiles.Replace(Path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>A new random recording id (32 hex digits).</summary>
    public static string NewContext() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
