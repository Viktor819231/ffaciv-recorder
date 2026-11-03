using System.Diagnostics;
using System.Text.Json;

namespace OutsideRecorderCore;

/// <summary>
/// One recording folder: events.jsonl in the in-game recorder's shape
/// ({"schema":2,"kind":"result","sequence","timeMs","timeUs","turn","turnSource","data":{"method","source","args"}}),
/// with source "memory_sample" so nobody mistakes a sample for an exact game event.
/// </summary>
public sealed class JsonlRecording : IDisposable
{
    readonly StreamWriter writer;
    readonly Func<long> nowUs;
    readonly bool wallClock;
    long sequence;
    static readonly JsonSerializerOptions json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    /// <summary>The session folder.</summary>
    public string Folder { get; }
    /// <summary>Lines written so far.</summary>
    public long Count => sequence;

    /// <summary>Creates session-yyyyMMddTHHmmss-pid under <paramref name="root"/>, times from the stopwatch.</summary>
    public JsonlRecording(string root, int processId, Stopwatch clock)
        : this(Path.Combine(root, $"session-{DateTime.Now:yyyyMMddTHHmmss}-{processId}"), "events.jsonl", () => clock.Elapsed.Ticks / 10) { }

    /// <summary>Writes <paramref name="fileName"/> in <paramref name="folder"/>; <paramref name="nowUs"/> gives each line's time.</summary>
    public JsonlRecording(string folder, string fileName, Func<long> nowUs, bool wallClock = true)
    {
        this.nowUs = nowUs;
        this.wallClock = wallClock;
        Folder = folder;
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, fileName);
        sequence = LastSequence(path); // continuing a game's file (rejoin after a Civ or recorder restart): numbers go on
        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
    }

    /// <summary>Writes one line of kind <paramref name="kind"/>; <paramref name="data"/> is any JSON-able object.</summary>
    public void Write(string kind, int? turn, object data)
    {
        long us = nowUs();
        var line = new Dictionary<string, object?>
        {
            ["schema"] = 2, ["kind"] = kind, ["sequence"] = ++sequence, ["timeMs"] = us / 1000, ["timeUs"] = us,
            ["wallClock"] = wallClock ? DateTimeOffset.Now.ToString("o") : null, ["turn"] = turn, ["turnSource"] = turn is null ? "none" : "memory_sample",
            ["data"] = data,
        };
        writer.WriteLine(JsonSerializer.Serialize(line, json));
    }

    /// <summary>Writes a game event as kind "result" with source "memory_sample".</summary>
    public void Write(GameEvent e) =>
        Write("result", e.Turn, new Dictionary<string, object?> { ["method"] = e.Method, ["source"] = "memory_sample", ["args"] = e.Args });

    /// <summary>The sequence of the file's last complete line (0 for a new file).</summary>
    public static long LastSequence(string path)
    {
        if (!File.Exists(path)) return 0;
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long start = Math.Max(0, f.Length - 64 * 1024);
        f.Seek(start, SeekOrigin.Begin);
        var tail = new StreamReader(f).ReadToEnd();
        foreach (var line in tail.Split('\n').Select(x => x.TrimEnd('\r')).Reverse())
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"""sequence"":(\d+)");
            if (m.Success && line.EndsWith('}')) return long.Parse(m.Groups[1].Value);
        }
        return 0;
    }

    /// <summary>The last turn written in the file (a line with a turn), or null for a new file or none.</summary>
    public static int? LastTurn(string path)
    {
        if (!File.Exists(path)) return null;
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long start = Math.Max(0, f.Length - 256 * 1024);
        f.Seek(start, SeekOrigin.Begin);
        var tail = new StreamReader(f).ReadToEnd();
        foreach (var line in tail.Split('\n').Select(x => x.TrimEnd('\r')).Reverse())
        {
            if (!line.EndsWith('}')) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"""turn"":(\d+),""turnSource""");
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return null;
    }

    /// <summary>Pushes buffered lines to disk (called after each sample).</summary>
    public void Flush() => writer.Flush();

    /// <inheritdoc/>
    public void Dispose() { writer.Flush(); writer.Dispose(); }
}
