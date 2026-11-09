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
        : this(Path.Combine(root, RecorderSession.SessionFolderName(processId.ToString(System.Globalization.CultureInfo.InvariantCulture))), "events.jsonl", () => clock.Elapsed.Ticks / 10) { }

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
        foreach (var (head, complete) in LinesFromEnd(path))
        {
            var m = System.Text.RegularExpressions.Regex.Match(head, @"""sequence"":(\d+)");
            if (m.Success && complete) return long.Parse(m.Groups[1].Value);
        }
        return 0;
    }

    /// <summary>The last turn written in the file (a line with a turn), or null for a new file or none.</summary>
    public static int? LastTurn(string path)
    {
        foreach (var (head, complete) in LinesFromEnd(path))
        {
            if (!complete) continue;
            var m = System.Text.RegularExpressions.Regex.Match(head, @"""turn"":(\d+),""turnSource""");
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return null;
    }

    /// <summary>Bytes of a line's start that hold its top-level fields (schema, kind, sequence, times, turn and turnSource
    /// all come before "data").</summary>
    const int HeadBytes = 1024;

    /// <summary>
    /// The file's lines from the last to the first, each as its start (<see cref="HeadBytes"/>) and whether it is complete
    /// (ends with "}": the last line of a recorder stopped mid-write does not). Read backwards in blocks, so a line of any
    /// length is found: a turn's snapshot line can be longer than any fixed tail, and the old 64 KB tail then found no line
    /// (the sequence started again at 1). Nothing for a missing file.
    /// </summary>
    static IEnumerable<(string Head, bool Complete)> LinesFromEnd(string path)
    {
        if (!File.Exists(path)) yield break;
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = RandomAccess.GetLength(file);
        var block = new byte[64 * 1024];
        var head = new byte[HeadBytes];
        var last = new byte[2];
        long end = length, searched = length;   // the line ends at `end` (exclusive); bytes before `searched` are not searched yet
        while (true)
        {
            long newline = -1;
            while (newline < 0 && searched > 0)
            {
                int n = (int)Math.Min(block.Length, searched);
                long from = searched - n;
                n = RandomAccess.Read(file, block.AsSpan(0, n), from);
                int i = block.AsSpan(0, n).LastIndexOf((byte)'\n');
                searched = i >= 0 ? from + i : from;
                if (i >= 0) newline = from + i;
            }
            long start = newline + 1;
            int tail = RandomAccess.Read(file, last.AsSpan(0, (int)Math.Min(2, end - start)), Math.Max(start, end - 2));
            bool complete = tail > 0 && (last[tail - 1] == '}' || tail == 2 && last[1] == '\r' && last[0] == '}');
            int got = RandomAccess.Read(file, head.AsSpan(0, (int)Math.Min(head.Length, end - start)), start);
            yield return (System.Text.Encoding.UTF8.GetString(head, 0, got), complete);
            if (newline < 0) yield break;
            end = newline;
        }
    }

    /// <summary>Pushes buffered lines to disk (called after each sample).</summary>
    public void Flush() => writer.Flush();

    /// <inheritdoc/>
    public void Dispose()
    {
        // Dispose flushes and always closes the file. On a full disk the unwritten tail is lost either way, and the
        // recording must still close (a throw here used to keep the file open and end the capture thread).
        try { writer.Dispose(); } catch (IOException) { }
    }
}
