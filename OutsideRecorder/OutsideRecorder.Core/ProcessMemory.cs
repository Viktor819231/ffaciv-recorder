using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OutsideRecorderCore;

/// <summary>Read-only view of another process's memory (32-bit addresses). Tests use a fake.</summary>
public interface IMemory
{
    /// <summary>Copies bytes at <paramref name="address"/>; false when the memory is not readable (freed, unmapped). After
    /// false the buffer's contents are undefined (a read may stop part-way).</summary>
    bool TryRead(uint address, Span<byte> buffer);
}

/// <summary>Little helpers on top of <see cref="IMemory"/>.</summary>
public static class MemoryExtensions
{
    /// <summary>Reads a 32-bit int, or null when unreadable.</summary>
    public static int? Int(this IMemory m, uint address)
    {
        Span<byte> b = stackalloc byte[4];
        return m.TryRead(address, b) ? BinaryPrimitives.ReadInt32LittleEndian(b) : null;
    }

    /// <summary>Reads a 32-bit pointer, or null when unreadable.</summary>
    public static uint? Ptr(this IMemory m, uint address) => m.Int(address) is int v ? (uint)v : null;

    /// <summary>Reads one byte as a bool, or null when unreadable.</summary>
    public static bool? Bool(this IMemory m, uint address)
    {
        Span<byte> b = stackalloc byte[1];
        return m.TryRead(address, b) ? b[0] != 0 : null;
    }

    /// <summary>
    /// Reads an MSVC 2008 std::string / CvString (28 bytes: 4 allocator, 16 inline buffer or pointer, size, capacity).
    /// Null when unreadable or implausible.
    /// </summary>
    public static string? CvString(this IMemory m, uint address, int maxLength = 256)
    {
        Span<byte> s = stackalloc byte[0x1C];
        if (!m.TryRead(address, s)) return null;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(s[0x14..]);
        uint cap = BinaryPrimitives.ReadUInt32LittleEndian(s[0x18..]);
        if (size > cap || size > (uint)maxLength) return null;
        if (cap < 16) return Encoding.UTF8.GetString(s.Slice(4, (int)size));
        uint ptr = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        var buf = new byte[size];
        return size == 0 || m.TryRead(ptr, buf) ? Encoding.UTF8.GetString(buf) : null;
    }

    /// <summary>Reads std::vector's first/last pointers (MSVC 2008: +4 first, +8 last); null when unreadable.</summary>
    public static (uint First, uint Last)? Vector(this IMemory m, uint address)
    {
        Span<byte> v = stackalloc byte[12];
        if (!m.TryRead(address, v)) return null;
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(v[4..]), last = BinaryPrimitives.ReadUInt32LittleEndian(v[8..]);
        return last < first || last - first > 1 << 20 ? null : (first, last);
    }
}

/// <summary>
/// The real thing: a handle opened with PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION only.
/// No write, no injection, no thread suspend, no debugger attach: those rights are never asked for.
/// </summary>
public sealed class ProcessMemory : IMemory, IDisposable
{
    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    /// <summary>Closed once, by <see cref="Dispose"/>; a call still running on it keeps it open until that call returns.</summary>
    readonly SafeProcessHandle handle;

    /// <summary>Opens the process read-only; throws when Windows refuses (e.g. Civ runs as administrator and we do not).</summary>
    public ProcessMemory(int processId)
    {
        handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"cannot open process {processId} for reading");
        }
    }

    /// <summary>True while the process has not exited.</summary>
    public bool IsAlive => GetExitCodeProcess(handle, out uint code) && code == 259; // STILL_ACTIVE

    /// <summary>The exit code once the process ended (GetExitCodeProcess works with the limited query right); null while running.</summary>
    public uint? ExitCode => GetExitCodeProcess(handle, out uint code) && code != 259 ? code : null;

    /// <summary>
    /// On (the default since 0.4.2): every read is checked against the process's memory map first, and only made when all
    /// its pages are committed, readable, ordinary heap or module memory (MEM_PRIVATE / MEM_IMAGE), not a guard page, and
    /// not in an allocation that holds a guard page (a thread's stack). ReadProcessMemory on a guard page can take the guard
    /// away, and a thread whose stack guard is gone crashes when its stack grows: this makes that impossible.
    /// </summary>
    public bool Guarded { get; init; } = true;
    /// <summary>Reads refused by the memory-map check (for the window and the tests).</summary>
    public long Refused { get; private set; }

    const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, MEM_IMAGE = 0x1000000;
    const uint PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100, READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;   // R, RW, WC, XR, XRW, XWC
    readonly List<(uint Start, uint End, bool Ok, long At)> regions = [];   // sorted by Start, no overlaps
    readonly Dictionary<nint, (bool Guard, long At)> allocations = [];
    /// <summary>How long a region's answer is trusted (ms); then it is asked again.</summary>
    public const int MapRefreshMs = 2000;

    bool Allowed(uint address, int length)
    {
        uint end = address + (uint)length;
        if (end < address) return false;
        for (uint at = address; at < end;)
        {
            var (start, stop, ok) = RegionOf(at);
            if (!ok) return false;
            if (stop <= at) return false;
            at = stop;
        }
        return true;
    }

    (uint Start, uint End, bool Ok) RegionOf(uint address)
    {
        long now = Environment.TickCount64;
        int lo = 0, hi = regions.Count - 1, found = -1;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (regions[mid].Start <= address) { found = mid; lo = mid + 1; } else hi = mid - 1; }
        if (found >= 0 && address < regions[found].End && now - regions[found].At < MapRefreshMs) return (regions[found].Start, regions[found].End, regions[found].Ok);
        if (VirtualQueryEx(handle, (nint)address, out var mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) return (address, address + 1, false);
        ulong b = (ulong)mbi.BaseAddress, e = Math.Min(b + (ulong)mbi.RegionSize, uint.MaxValue);
        bool ok = mbi.State == MEM_COMMIT && (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) == 0 && (mbi.Protect & READABLE) != 0
            && (mbi.Type == MEM_PRIVATE || mbi.Type == MEM_IMAGE) && !HasGuard(mbi.AllocationBase, now);
        regions.RemoveAll(r => r.Start < e && r.End > b);
        int i = regions.FindIndex(r => r.Start > b);
        regions.Insert(i < 0 ? regions.Count : i, ((uint)b, (uint)e, ok, now));
        return ((uint)b, (uint)e, ok);
    }

    /// <summary>True when the allocation holds a guard page anywhere (a thread stack): none of it is read.</summary>
    bool HasGuard(nint allocationBase, long now)
    {
        if (allocationBase == 0) return false;
        if (allocations.TryGetValue(allocationBase, out var known) && now - known.At < 10_000) return known.Guard;
        bool guard = false;
        ulong at = (ulong)allocationBase;
        for (int n = 0; n < 4096; n++)
        {
            if (VirtualQueryEx(handle, (nint)at, out var m, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0 || m.AllocationBase != allocationBase) break;
            if ((m.Protect & PAGE_GUARD) != 0) { guard = true; break; }
            if (m.RegionSize == 0) break;
            at = (ulong)m.BaseAddress + (ulong)m.RegionSize;
        }
        allocations[allocationBase] = (guard, now);
        return guard;
    }

    /// <inheritdoc/>
    /// <remarks>Reads straight into <paramref name="buffer"/>: no second buffer (the tile table is ~5 MB on a huge map).</remarks>
    public bool TryRead(uint address, Span<byte> buffer)
    {
        if (address < 0x10000) return false;
        if (Guarded && !Allowed(address, buffer.Length)) { Refused++; return false; }
        // an empty read still asks Windows (as it always did), with a real buffer address
        ref byte target = ref buffer.IsEmpty ? ref none[0] : ref MemoryMarshal.GetReference(buffer);
        return ReadProcessMemory(handle, (nint)address, ref target, buffer.Length, out nint read) && read == buffer.Length;
    }

    static readonly byte[] none = new byte[1];

    /// <summary>
    /// Committed, readable memory regions (VirtualQueryEx with the same read-only handle).
    /// Throws when Windows wants more rights than we asked for.
    /// </summary>
    public IEnumerable<(uint Base, uint Size, uint Protect, uint Type)> Regions()
    {
        ulong addr = 0x10000;
        int size = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < 0xFFFF0000)
        {
            if (VirtualQueryEx(handle, (nint)addr, out var mbi, size) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (addr == 0x10000) throw new Win32Exception(err, "VirtualQueryEx refused");
                yield break;
            }
            ulong b = (ulong)mbi.BaseAddress, len = (ulong)mbi.RegionSize;
            if (mbi.State == MEM_COMMIT && (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) == 0 && b + len <= uint.MaxValue)
                yield return ((uint)b, (uint)len, mbi.Protect, mbi.Type);
            if (len == 0) yield break;
            addr = b + len;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint VirtualQueryEx(SafeProcessHandle process, nint address, out MEMORY_BASIC_INFORMATION info, int length);

    /// <inheritdoc/>
    public void Dispose() => handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, ref byte buffer, nint size, out nint read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
