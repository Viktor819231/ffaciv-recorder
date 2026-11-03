using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace OutsideRecorderCore;

/// <summary>What a tape was recorded from: needed to read it again.</summary>
public sealed record TapeHeader
{
    /// <summary>Tape format version.</summary>
    public int Format { get; init; } = 1;
    /// <summary>How the reader read (1 = a call per field; 2 = blocks + tiers + tiles; 3 = also city micro once a second).
    /// A replay reads the same way as the live run, so it asks for the same blocks the tape holds.</summary>
    public int Reads { get; init; } = 1;
    /// <summary>GameCoreLayout.Id of the DLL that was read.</summary>
    public required string Layout { get; init; }
    /// <summary>SHA-256 of that DLL.</summary>
    public required string CoreSha256 { get; init; }
    /// <summary>Where the DLL was loaded in Civ (all addresses on the tape are absolute).</summary>
    public required uint ModuleBase { get; init; }
    /// <summary>Sample interval asked for.</summary>
    public required int SampleMs { get; init; }
    /// <summary>When the tape started (local time).</summary>
    public required string Started { get; init; }
}

/// <summary>One sample on a tape: the reads whose bytes changed since the last time the same read was made.</summary>
public sealed record TapeSample(long TimeUs, double ReadMs, List<(uint Address, int Length, byte[]? Data)> Changes);

/// <summary>
/// Tape mode: every byte range the reader read, per sample, written only when it changed, gzip-compressed,
/// with the time each sample took. A later version can re-run its reader on the tape (<see cref="TapeReplay"/>)
/// and compare against the autosaves. Local only.
/// File: tape.bin.gz = "ORTAPE1\n" + int32 header length + header JSON, then frames:
/// 'S' int64 timeUs, then 'R' uint32 address, int32 length, byte ok, [length bytes if ok] ..., then 'E' float64 readMs.
/// </summary>
public sealed class TapeWriter : IDisposable
{
    readonly GZipStream gz;
    readonly BinaryWriter w;
    readonly Dictionary<(uint, int), byte[]?> last = new();
    /// <summary>Bytes of read data written so far (before compression).</summary>
    public long RawBytes { get; private set; }
    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary>Starts (or continues, after a reload of the same game) tape.bin.gz in <paramref name="folder"/>.</summary>
    public TapeWriter(string folder, TapeHeader header)
    {
        Path = System.IO.Path.Combine(folder, "tape.bin.gz");
        bool append = File.Exists(Path);
        // Appending writes a second gzip member; GZipStream reads concatenated members as one stream.
        gz = new GZipStream(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read), CompressionLevel.Fastest);
        w = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: true);
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header));
        if (!append) w.Write(Encoding.ASCII.GetBytes("ORTAPE1\n"));
        else w.Write((byte)'H'); // header frame for the continued part (module base may differ after a restart)
        w.Write(json.Length); w.Write(json);
    }

    /// <summary>Begins a sample.</summary>
    public void Begin(long timeUs) { w.Write((byte)'S'); w.Write(timeUs); }

    /// <summary>
    /// Notes one read; written only if it differs from the last result of the same read. A big read that changed in a few
    /// places (a unit's block, the tile table) goes as a 'D' record: only the changed ranges.
    /// </summary>
    public void Read(uint address, ReadOnlySpan<byte> data, bool ok)
    {
        var key = (address, data.Length);
        bool had = last.TryGetValue(key, out var prev);
        if (had && (ok ? prev is not null && data.SequenceEqual(prev) : prev is null)) return;
        if (ok && prev is not null && data.Length >= 64 && Ranges(prev, data) is { } ranges)
        {
            w.Write((byte)'D'); w.Write(address); w.Write(data.Length); w.Write(ranges.Count);
            foreach (var (at, len) in ranges) { w.Write(at); w.Write(len); w.Write(data.Slice(at, len)); RawBytes += len; }
            data.CopyTo(prev);
            return;
        }
        last[key] = ok ? data.ToArray() : null;
        w.Write((byte)'R'); w.Write(address); w.Write(data.Length); w.Write((byte)(ok ? 1 : 0));
        if (ok) { w.Write(data); RawBytes += data.Length; }
    }

    /// <summary>The changed byte ranges (gaps under 16 bytes joined), or null when more than half changed (then the whole read goes).</summary>
    static List<(int At, int Len)>? Ranges(byte[] a, ReadOnlySpan<byte> b)
    {
        var r = new List<(int, int)>();
        int n = b.Length, i = 0, changed = 0;
        while (i < n)
        {
            i += a.AsSpan(i, n - i).CommonPrefixLength(b[i..]);
            if (i >= n) break;
            int start = i, j = i;
            while (j < n)
            {
                while (j < n && a[j] != b[j]) j++;
                if (j >= n) break;
                int same = a.AsSpan(j, n - j).CommonPrefixLength(b[j..]);
                if (same >= 16 || j + same >= n) break;
                j += same;
            }
            r.Add((start, j - start)); changed += j - start;
            if (changed * 2 > n) return null;
            i = j;
        }
        return r;
    }

    /// <summary>Ends a sample and pushes it to disk.</summary>
    public void End(double readMs) { w.Write((byte)'E'); w.Write(readMs); w.Flush(); gz.Flush(); }

    /// <inheritdoc/>
    public void Dispose() { w.Flush(); w.Dispose(); gz.Dispose(); }
}

/// <summary>An <see cref="IMemory"/> that passes reads through and, while a sample is open, puts each one on the tape.</summary>
public sealed class TapingMemory(IMemory inner) : IMemory
{
    /// <summary>The tape to write to (null = tape mode off).</summary>
    public TapeWriter? Tape { get; set; }
    bool open;

    /// <summary>Opens a sample frame (no-op without a tape).</summary>
    public void Begin(long timeUs) { if (Tape is null) return; Tape.Begin(timeUs); open = true; }

    /// <summary>Closes the sample frame.</summary>
    public void End(double readMs) { if (!open) return; Tape!.End(readMs); open = false; }

    /// <inheritdoc/>
    public bool TryRead(uint address, Span<byte> buffer)
    {
        bool ok = inner.TryRead(address, buffer);
        if (open) Tape!.Read(address, buffer, ok);
        return ok;
    }
}

/// <summary>Reads a tape back (stops quietly at a cut-off end, e.g. if the PC lost power).</summary>
public static class TapeReader
{
    /// <summary>Header(s) and samples in order. A continued tape has a new header before its samples.</summary>
    public static IEnumerable<(TapeHeader? Header, TapeSample? Sample)> Read(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var r = new BinaryReader(new BufferedStream(gz, 1 << 16));
        var magic = r.ReadBytes(8);
        if (Encoding.ASCII.GetString(magic) != "ORTAPE1\n") throw new InvalidDataException("not an Outside Recorder tape");
        yield return (ReadHeader(r), null);
        var last = new Dictionary<(uint, int), byte[]?>();   // for 'D' records (changed ranges only)
        while (true)
        {
            (TapeHeader? H, TapeSample? S) item;
            try { item = Next(r, last); }
            catch (EndOfStreamException) { yield break; }
            catch (InvalidDataException) { yield break; }
            if (item.H is null && item.S is null) yield break;
            yield return item;
        }
    }

    static TapeHeader ReadHeader(BinaryReader r) => JsonSerializer.Deserialize<TapeHeader>(r.ReadBytes(r.ReadInt32()))!;

    static (TapeHeader?, TapeSample?) Next(BinaryReader r, Dictionary<(uint, int), byte[]?> last)
    {
        int kind = r.BaseStream.ReadByte();
        if (kind < 0) return (null, null);
        if (kind == 'H') { last.Clear(); return (ReadHeader(r), null); }
        if (kind != 'S') throw new InvalidDataException($"bad frame {kind}");
        long t = r.ReadInt64();
        var changes = new List<(uint, int, byte[]?)>();
        while (true)
        {
            int f = r.ReadByte();
            if (f == 'E') return (null, new TapeSample(t, r.ReadDouble(), changes));
            if (f == 'D')
            {
                // only the changed ranges of a read whose whole bytes came earlier: patched onto a copy of those
                uint da = r.ReadUInt32(); int dlen = r.ReadInt32(); int n = r.ReadInt32();
                var full = last.TryGetValue((da, dlen), out var before) && before is not null ? (byte[])before.Clone() : new byte[dlen];
                for (int k = 0; k < n; k++) { int at = r.ReadInt32(), cl = r.ReadInt32(); r.ReadBytes(cl).CopyTo(full, at); }
                last[(da, dlen)] = full;
                changes.Add((da, dlen, full));
                continue;
            }
            if (f != 'R') throw new InvalidDataException($"bad record {f}");
            uint a = r.ReadUInt32(); int len = r.ReadInt32(); bool ok = r.ReadByte() != 0;
            var data = ok ? r.ReadBytes(len) : null;
            last[(a, len)] = data;
            changes.Add((a, len, data));
        }
    }
}

/// <summary>
/// Civ's memory as the tape remembers it at one sample. A read made exactly as recorded is answered exactly;
/// a different read (a newer reader version) is answered from a recorded range that contains it, else it fails.
/// </summary>
public sealed class ReplayMemory : IMemory
{
    readonly Dictionary<(uint, int), byte[]?> state = new();

    /// <summary>Applies one sample's changes.</summary>
    public void Apply(TapeSample s) { foreach (var (a, l, d) in s.Changes) state[(a, l)] = d; }

    /// <summary>Forgets everything (new tape part after a restart).</summary>
    public void Clear() => state.Clear();

    /// <inheritdoc/>
    public bool TryRead(uint address, Span<byte> buffer)
    {
        if (state.TryGetValue((address, buffer.Length), out var exact))
        {
            if (exact is null) return false;
            exact.CopyTo(buffer); return true;
        }
        foreach (var ((a, l), d) in state)
            if (d is not null && address >= a && address + (uint)buffer.Length <= a + (uint)l)
            { d.AsSpan((int)(address - a), buffer.Length).CopyTo(buffer); return true; }
        return false;
    }
}
