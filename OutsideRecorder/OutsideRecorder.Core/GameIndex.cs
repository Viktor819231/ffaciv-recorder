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

    /// <summary>Adds or updates an entry. A games.json that cannot be read right now (another program holds it) throws instead
    /// of being replaced by this one entry, which would cut every earlier recording off from its upload id; a damaged one
    /// is kept beside as games.json.damaged-&lt;time&gt; before a new list starts.</summary>
    public void Save(GameEntry entry)
    {
        var all = LoadForChange().Where(e => e.Folder != entry.Folder).Append(entry).ToList();
        Directory.CreateDirectory(root);
        SafeFiles.Replace(Path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    List<GameEntry> LoadForChange()
    {
        for (int i = 0; ; i++)
        {
            try { return File.Exists(Path) ? JsonSerializer.Deserialize<List<GameEntry>>(SafeFiles.ReadAllText(Path)) ?? [] : []; }
            catch (JsonException)
            {
                try { File.Copy(Path, $"{Path}.damaged-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && i < 40) { Thread.Sleep(50); }
        }
    }

    /// <summary>True for a recording id as <see cref="NewContext"/> makes them (32 lower-case hex digits): ids from files and
    /// the window are checked before they become file names or site addresses.</summary>
    public static bool IsContext(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A new random recording id (32 hex digits).</summary>
    public static string NewContext() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
