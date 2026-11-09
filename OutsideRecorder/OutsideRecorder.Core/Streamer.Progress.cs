using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OutsideRecorderCore;

/// <summary>What was sent of each recording (stream.json) and whether its log says it ended.</summary>
public sealed partial class Streamer
{
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

    /// <summary>What was sent of a recording. A file another program holds right now throws (the recording waits for the next
    /// pass): reading it as "nothing sent" made the next write replace the whole list of sent saves with one. A damaged
    /// file is kept beside as stream.json.damaged and the recording starts over, as before.</summary>
    static StreamState Load(string folder)
    {
        var f = Path.Combine(folder, "stream.json");
        if (!File.Exists(f)) return new();
        try { return JsonSerializer.Deserialize<StreamState>(SafeFiles.ReadAllText(f)) ?? new(); }
        catch (JsonException)
        {
            try { File.Copy(f, f + ".damaged", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new();
        }
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
}
