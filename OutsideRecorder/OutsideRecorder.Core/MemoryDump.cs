using System.Text;
using System.Text.Json;

namespace OutsideRecorderCore;

/// <summary>
/// The "Snapshot" button: copies Civ's readable memory to one file for later field discovery.
/// Uses the same read-only handle (no MiniDumpWriteDump: that needs PROCESS_QUERY_INFORMATION and pauses Civ's threads).
/// Civ keeps running, so the copy is not from one single instant (best taken while the game waits, e.g. between turns).
/// File: "ORDUMP1\n" + int32 header length + header JSON, then per region: uint32 base, uint32 size, uint32 protect, uint32 type, bytes
/// (unreadable pages are zero-filled).
/// </summary>
public static class MemoryDump
{
    /// <summary>Writes the dump; returns bytes written. Only private (heap) memory and the game core DLL image are copied.</summary>
    public static long Write(int processId, string path, ModuleInfo? gameCore, object header, Action<long>? progress = null)
    {
        using var mem = new ProcessMemory(processId);
        var regions = mem.Regions().Where(r => r.Type == 0x20000 /*MEM_PRIVATE*/
            || gameCore is not null && r.Base >= gameCore.Base && r.Base < gameCore.Base + gameCore.Size).ToList();
        using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(f);
        w.Write(Encoding.ASCII.GetBytes("ORDUMP1\n"));
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { header, regions = regions.Count, gameCore }));
        w.Write(json.Length); w.Write(json);
        var buf = new byte[1 << 16];
        long total = 0;
        foreach (var (b, size, protect, type) in regions)
        {
            w.Write(b); w.Write(size); w.Write(protect); w.Write(type);
            for (uint off = 0; off < size; off += (uint)buf.Length)
            {
                int n = (int)Math.Min(buf.Length, size - off);
                var span = buf.AsSpan(0, n);
                if (!mem.TryRead(b + off, span))
                    for (int p = 0; p < n; p += 4096) // page by page, zero what cannot be read
                        if (!mem.TryRead(b + off + (uint)p, span.Slice(p, Math.Min(4096, n - p)))) span.Slice(p, Math.Min(4096, n - p)).Clear();
                w.Write(span);
                total += n;
            }
            progress?.Invoke(total);
        }
        return total;
    }
}
