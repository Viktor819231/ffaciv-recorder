using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OutsideRecorderCore;

/// <summary>
/// A Civ V save split the generic way (no save reader): the bytes before the compressed body ("head": file header and
/// pre-game, ~10 KB), the body (int32 length + up to 64 KiB of one zlib stream, block after block) unpacked to its
/// payload (~14 MB), and anything after the body.
/// </summary>
public sealed record SaveParts(byte[] Head, byte[] Payload, byte[] Tail)
{
    /// <summary>Block size of the compressed body.</summary>
    public const int BlockSize = 65536;

    /// <summary>Splits a save; null when no body is found.</summary>
    public static SaveParts? Split(byte[] file)
    {
        int body = -1;
        for (int o = 4; o + 6 <= file.Length; o++)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(o)), rest = file.Length - o - 4;
            if ((len == BlockSize || len == rest) && len > 0 && len <= rest && file[o + 4] == 0x78 && ((0x78 << 8) | file[o + 5]) % 31 == 0) { body = o; break; }
        }
        if (body < 0) return null;
        using var compressed = new MemoryStream();
        int p = body;
        while (p + 4 <= file.Length)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(p)); p += 4;
            if (len <= 0 || p + len > file.Length) return null;
            compressed.Write(file, p, len); p += len;
            if (len != BlockSize) break;
        }
        compressed.Position = 2; // the zlib header; Civ's stream is flushed, not finished, so read it as raw deflate
        using var inflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var payload = new MemoryStream();
        try { inflate.CopyTo(payload); } catch (InvalidDataException) { if (payload.Length == 0) return null; }
        return new SaveParts(file[..body], payload.ToArray(), file[p..]);
    }

    /// <summary>A save again from parts: head + payload re-compressed (standard zlib) in 64 KiB blocks + tail. Not the
    /// original compressed bytes (Civ's compressor cannot be reproduced), but the same head and the same payload.</summary>
    public byte[] Join()
    {
        byte[] Compress(CompressionLevel level)
        {
            using var z = new MemoryStream();
            using (var c = new ZLibStream(z, level, leaveOpen: true)) c.Write(Payload);
            return z.ToArray();
        }
        var comp = Compress(CompressionLevel.Optimal);
        // the body ends at the first block shorter than 64 KiB: an exact multiple would not end (1 in 65536)
        if (comp.Length % BlockSize == 0) comp = Compress(CompressionLevel.SmallestSize);
        using var o = new MemoryStream();
        o.Write(Head);
        Span<byte> len = stackalloc byte[4];
        for (int at = 0; at < comp.Length; at += BlockSize)
        {
            int n = Math.Min(BlockSize, comp.Length - at);
            BinaryPrimitives.WriteInt32LittleEndian(len, n);
            o.Write(len); o.Write(comp, at, n);
        }
        o.Write(Tail);
        return o.ToArray();
    }

    /// <summary>SHA-256 (hex) of head + payload + tail: what stays equal when a save is rebuilt from a delta.</summary>
    public string ContentSha256()
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Head); h.AppendData(Payload); h.AppendData(Tail);
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }
}

/// <summary>
/// "save_upload = delta": a save sent as its change from the previous save of the same game. Format ORSD1 (gzip):
/// "ORSD1\n", int32 + header JSON, the head, the tail, then ops on the payload: 'C' varint offset varint length (copy from
/// the base payload) / 'L' varint length + bytes (new bytes) / 'E'. The server rebuilds head + payload + tail, checks the
/// SHA-256s, and asks for the full save when anything does not match.
/// </summary>
public static class SaveDelta
{
    /// <summary>Header of a delta.</summary>
    public sealed record Header(string BaseContentSha256, string ContentSha256, string FileMd5, long FileLength, int PayloadLength);

    const int Min = 32; // shortest copy worth an op

    /// <summary>Makes the delta of <paramref name="target"/> against <paramref name="baseParts"/>.</summary>
    public static byte[] Make(SaveParts baseParts, SaveParts target, byte[] targetFile)
    {
        var header = new Header(baseParts.ContentSha256(), target.ContentSha256(), Convert.ToHexStringLower(MD5.HashData(targetFile)), targetFile.LongLength, target.Payload.Length);
        using var o = new MemoryStream();
        using (var gz = new GZipStream(o, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new BinaryWriter(gz))
        {
            w.Write(Encoding.ASCII.GetBytes("ORSD1\n"));
            var json = JsonSerializer.SerializeToUtf8Bytes(header);
            w.Write(json.Length); w.Write(json);
            w.Write(target.Head.Length); w.Write(target.Head);
            w.Write(target.Tail.Length); w.Write(target.Tail);
            WriteOps(w, baseParts.Payload, target.Payload);
        }
        return o.ToArray();
    }

    static void WriteOps(BinaryWriter w, byte[] old, byte[] now)
    {
        // index every 16th position of the old payload by its first 8 bytes (a flat table: fast, no allocations per entry)
        const int Bits = 21;
        var table = new int[1 << Bits];
        Array.Fill(table, -1);
        static int Slot(ulong k) => (int)((k * 0x9E3779B97F4A7C15UL) >> (64 - Bits));
        for (int i = 0; i + 16 <= old.Length; i += 16)
        {
            int slot = Slot(BinaryPrimitives.ReadUInt64LittleEndian(old.AsSpan(i)));
            if (table[slot] < 0) table[slot] = i;
        }
        int lit = 0, at = 0, nextOld = 0;
        void Literal(int end) { if (end > lit) { w.Write((byte)'L'); Varint(w, end - lit); w.Write(now, lit, end - lit); } }
        while (at + 8 <= now.Length)
        {
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(now.AsSpan(at));
            // the old payload usually goes on where the last copy stopped: try that first, then the index (checked: slots collide)
            int cand = nextOld + 8 <= old.Length && BinaryPrimitives.ReadUInt64LittleEndian(old.AsSpan(nextOld)) == key ? nextOld : table[Slot(key)];
            if (cand >= 0 && (cand + 8 > old.Length || BinaryPrimitives.ReadUInt64LittleEndian(old.AsSpan(cand)) != key)) cand = -1;
            if (cand < 0) { at++; continue; }
            int len = now.AsSpan(at).CommonPrefixLength(old.AsSpan(cand));
            int back = 0;
            while (at - back > lit && cand - back > 0 && now[at - back - 1] == old[cand - back - 1]) back++;
            if (len + back < Min) { at++; continue; }
            Literal(at - back);
            w.Write((byte)'C'); Varint(w, cand - back); Varint(w, len + back);
            at += len; lit = at; nextOld = cand + len;
        }
        Literal(now.Length);
        w.Write((byte)'E');
    }

    static void Varint(BinaryWriter w, int v) { uint u = (uint)v; while (u >= 0x80) { w.Write((byte)(u | 0x80)); u >>= 7; } w.Write((byte)u); }
    static int Varint(BinaryReader r) { int v = 0, shift = 0; byte b; do { b = r.ReadByte(); v |= (b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0); return v; }

    /// <summary>Rebuilds a save from its base and a delta (what the server does); throws when a SHA-256 does not match.</summary>
    public static (byte[] File, SaveParts Parts, Header Header) Apply(SaveParts baseParts, byte[] delta)
    {
        using var r = new BinaryReader(new GZipStream(new MemoryStream(delta), CompressionMode.Decompress));
        if (Encoding.ASCII.GetString(r.ReadBytes(6)) != "ORSD1\n") throw new InvalidDataException("not an ORSD1 delta");
        var header = JsonSerializer.Deserialize<Header>(r.ReadBytes(r.ReadInt32()))!;
        if (header.BaseContentSha256 != baseParts.ContentSha256()) throw new InvalidDataException("the base save is not the one the delta was made from");
        var head = r.ReadBytes(r.ReadInt32());
        var tail = r.ReadBytes(r.ReadInt32());
        var payload = new byte[header.PayloadLength];
        int at = 0;
        for (int op = r.ReadByte(); op != 'E'; op = r.ReadByte())
        {
            if (op == 'C') { int from = Varint(r), n = Varint(r); Buffer.BlockCopy(baseParts.Payload, from, payload, at, n); at += n; }
            else if (op == 'L') { int n = Varint(r); r.BaseStream.ReadExactly(payload, at, n); at += n; }
            else throw new InvalidDataException($"bad op {op}");
        }
        if (at != payload.Length) throw new InvalidDataException("payload length mismatch");
        var parts = new SaveParts(head, payload, tail);
        if (parts.ContentSha256() != header.ContentSha256) throw new InvalidDataException("rebuilt save content SHA-256 mismatch");
        return (parts.Join(), parts, header);
    }
}
