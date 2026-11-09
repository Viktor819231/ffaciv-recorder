using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace OutsideRecorderCore;

/// <summary>What a tape was recorded from: needed to read it again.</summary>
public sealed record TapeHeader
{
    /// <summary>Tape format version.</summary>
    public int Format { get; init; } = 1;
    /// <summary>How the reader read (1 = a call per field; 2 = blocks + tiers + tiles; 3 = also city micro once a second;
    /// 4 = also unreadable names asked again and a new turn's timer start read at once). A replay reads the same way as the
    /// live run (<see cref="TapeReplay.ReaderFor"/>), so it asks for the same blocks the tape holds.</summary>
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
        // Appending writes a second gzip member; TapeReader reads each member on its own, so a member cut off by a killed
        // recorder (no gzip end) does not hide the parts after it.
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
    public void Dispose()
    {
        // the file is closed even when the disk is full (gzip closes its file in its own finally)
        try { w.Dispose(); } catch (IOException) { }
        try { gz.Dispose(); } catch (IOException) { }
    }
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
    /// <summary>
    /// Header(s) and samples in order. A continued tape has a new header before its samples. Each part (a gzip member: the
    /// first recording, then one per continuation) is read on its own: a part that was cut off (the recorder was killed, so
    /// its member never got its end) is read up to the cut, then the next part follows. Read as one stream, the cut part ran
    /// into the next part's bytes and everything from there on was lost.
    /// </summary>
    public static IEnumerable<(TapeHeader? Header, TapeSample? Sample)> Read(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var starts = PartStarts(file);
        if (starts.Count == 0 || starts[0] != 0) starts.Insert(0, 0);   // not a gzip file: the first read below says so
        long length = RandomAccess.GetLength(file);
        var last = new Dictionary<(uint, int), byte[]?>();   // for 'D' records (changed ranges only)
        for (int part = 0; part < starts.Count; part++)
        {
            long end = part + 1 < starts.Count ? starts[part + 1] : length;
            using var r = new BinaryReader(new BufferedStream(new GZipStream(new Part(file, starts[part], end - starts[part]), CompressionMode.Decompress), 1 << 16));
            if (part == 0)
            {
                var magic = r.ReadBytes(8);
                if (Encoding.ASCII.GetString(magic) != "ORTAPE1\n") throw new InvalidDataException("not an Outside Recorder tape");
                yield return (ReadHeader(r), null);
            }
            bool header = part > 0;   // a continued part starts with its header
            while (true)
            {
                (TapeHeader? H, TapeSample? S) item;
                // a cut-off end (or damage): the rest of this part is lost, the next part is read
                try { item = Next(r, last, header); }
                catch (Exception e) when (e is EndOfStreamException or InvalidDataException or JsonException) { break; }
                if (item.H is null && item.S is null) break;
                header = false;
                yield return item;
            }
        }
    }

    /// <summary>How each gzip member this program writes starts: deflate, no flags, no time stamp.</summary>
    static readonly byte[] PartMagic = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Where each part of the tape starts (the first at 0). Compressed bytes holding these 8 bytes by chance (one in
    /// 2^64 a position) would split a part: its second piece is not a gzip member and is skipped.</summary>
    static List<long> PartStarts(Microsoft.Win32.SafeHandles.SafeFileHandle file)
    {
        var starts = new List<long>();
        var block = new byte[1 << 20];
        long blockStart = 0; int kept = 0;
        while (true)
        {
            int n = RandomAccess.Read(file, block.AsSpan(kept), blockStart + kept);
            var span = block.AsSpan(0, kept + n);
            for (int from = 0, i; (i = span[from..].IndexOf(PartMagic)) >= 0; from += i + 1) starts.Add(blockStart + from + i);
            if (n == 0) return starts;
            // keep the last 7 bytes: a start may span two blocks (one found whole is never inside them)
            kept = Math.Min(PartMagic.Length - 1, span.Length);
            span[^kept..].CopyTo(block);
            blockStart += span.Length - kept;
        }
    }

    /// <summary>One part's bytes of the tape file, read where they lie (its end is the end of the stream).</summary>
    sealed class Part(Microsoft.Win32.SafeHandles.SafeFileHandle file, long start, long length) : Stream
    {
        long at;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => at; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, length - at);
            if (n <= 0) return 0;
            n = RandomAccess.Read(file, buffer[..n], start + at);
            at += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>The longest single read a tape holds is the tile table (~5 MB on a huge map); a length past this is damage.</summary>
    const int MaxRecordBytes = 64 << 20;

    static int Length(BinaryReader r)
    {
        int n = r.ReadInt32();
        return n is >= 0 and <= MaxRecordBytes ? n : throw new InvalidDataException($"bad length {n}");
    }

    static TapeHeader ReadHeader(BinaryReader r) => JsonSerializer.Deserialize<TapeHeader>(r.ReadBytes(Length(r)))!;

    static (TapeHeader?, TapeSample?) Next(BinaryReader r, Dictionary<(uint, int), byte[]?> last, bool header)
    {
        int kind = r.BaseStream.ReadByte();
        if (kind < 0) return (null, null);
        if (kind == 'H') { last.Clear(); return (ReadHeader(r), null); }
        if (header || kind != 'S') throw new InvalidDataException($"bad frame {kind}");
        long t = r.ReadInt64();
        var changes = new List<(uint, int, byte[]?)>();
        while (true)
        {
            int f = r.ReadByte();
            if (f == 'E') return (null, new TapeSample(t, r.ReadDouble(), changes));
            if (f == 'D')
            {
                // only the changed ranges of a read whose whole bytes came earlier: patched onto a copy of those
                uint da = r.ReadUInt32(); int dlen = Length(r); int n = r.ReadInt32();
                var full = last.TryGetValue((da, dlen), out var before) && before is not null ? (byte[])before.Clone() : new byte[dlen];
                for (int k = 0; k < n; k++)
                {
                    int at = r.ReadInt32(), cl = Length(r);
                    if (at < 0 || at > dlen - cl) throw new InvalidDataException($"bad range {at}+{cl} of {dlen}");
                    r.ReadBytes(cl).CopyTo(full, at);
                }
                last[(da, dlen)] = full;
                changes.Add((da, dlen, full));
                continue;
            }
            if (f != 'R') throw new InvalidDataException($"bad record {f}");
            uint a = r.ReadUInt32(); int len = Length(r); bool ok = r.ReadByte() != 0;
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
