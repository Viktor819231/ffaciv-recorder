using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OutsideRecorderCore;

/// <summary>Measured recorder and computer resources; missing counters stay unknown, never zero.</summary>
public sealed record ResourceSample
{
    public DateTimeOffset At { get; init; }
    public double? SystemCpuPercent { get; init; }
    /// <summary>CPU as a percentage of the whole machine, like Task Manager; not a percentage of one core.</summary>
    public double? RecorderCpuPercent { get; init; }
    public long? AvailableMemoryBytes { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public long? WorkingSetBytes { get; init; }
    public long? PrivateBytes { get; init; }
    public long? PeakWorkingSetBytes { get; init; }
}

/// <summary>Cumulative OS counters, also injectable without opening or inspecting a game process.</summary>
public sealed record ResourceCounters
{
    public ulong? SystemIdleTicks { get; init; }
    public ulong? SystemKernelTicks { get; init; }
    public ulong? SystemUserTicks { get; init; }
    public long? RecorderCpuTicks { get; init; }
    public long? AvailableMemoryBytes { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public long? WorkingSetBytes { get; init; }
    public long? PrivateBytes { get; init; }
    public long? PeakWorkingSetBytes { get; init; }
}

/// <summary>Samples this process and OS counters at most once every two seconds. Never opens Civ.</summary>
public sealed class ResourceMonitor
{
    readonly object sync = new();
    readonly Func<ResourceCounters> read;
    readonly Func<long> clock;
    readonly Func<DateTimeOffset> utcNow;
    readonly int processors;
    ResourceCounters? previous;
    ResourceSample? latest;
    long sampledAt;

    /// <summary>Counter and clock injection keeps scheduling tests independent of machine load.</summary>
    public ResourceMonitor(Func<ResourceCounters>? read = null, Func<long>? clock = null,
        Func<DateTimeOffset>? utcNow = null, int? processorCount = null)
    {
        this.read = read ?? ReadCounters;
        this.clock = clock ?? (() => Environment.TickCount64);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        processors = Math.Max(1, processorCount ?? MachineProcessorCount());
    }

    /// <summary>Returns a cached sample between intervals; failed counters are reported as unknown.</summary>
    public ResourceSample Sample()
    {
        lock (sync)
        {
            long now = clock();
            if (latest is not null && now >= sampledAt && now - sampledAt < 2000) return latest;
            ResourceCounters current;
            try { current = read(); }
            catch { current = new(); }
            long elapsed = now - sampledAt;
            double? systemCpu = null, recorderCpu = null;
            if (previous is not null && elapsed > 0)
            {
                if (Delta(current.SystemIdleTicks, previous.SystemIdleTicks) is double idle &&
                    Delta(current.SystemKernelTicks, previous.SystemKernelTicks) is double kernel &&
                    Delta(current.SystemUserTicks, previous.SystemUserTicks) is double user && kernel + user > 0 && idle <= kernel + user)
                    systemCpu = Math.Clamp(100 * (kernel + user - idle) / (kernel + user), 0, 100);
                if (current.RecorderCpuTicks is long used && previous.RecorderCpuTicks is long before && used >= before)
                    recorderCpu = Math.Clamp((used - before) / (double)TimeSpan.TicksPerMillisecond / elapsed / processors * 100, 0, 100);
            }
            latest = new()
            {
                At = utcNow(), SystemCpuPercent = systemCpu, RecorderCpuPercent = recorderCpu,
                AvailableMemoryBytes = current.AvailableMemoryBytes, TotalMemoryBytes = current.TotalMemoryBytes,
                WorkingSetBytes = current.WorkingSetBytes, PrivateBytes = current.PrivateBytes,
                PeakWorkingSetBytes = current.PeakWorkingSetBytes,
            };
            previous = current; sampledAt = now;
            return latest;
        }
    }

    static double? Delta(ulong? after, ulong? before) => after is ulong a && before is ulong b && a >= b ? a - b : null;

    static int MachineProcessorCount()
    {
        // Environment.ProcessorCount can reflect this host's affinity or CPU job quota. The displayed metric
        // is a share of the whole computer, so use all active Windows processor groups instead.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                uint count = GetActiveProcessorCount(0xffff);
                if (count is > 0 and <= int.MaxValue) return (int)count;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        }
        return Environment.ProcessorCount;
    }

    static ResourceCounters ReadCounters()
    {
        ResourceCounters result = new();
        if (OperatingSystem.IsWindows())
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
                result = result with { SystemIdleTicks = idle.Value, SystemKernelTicks = kernel.Value, SystemUserTicks = user.Value };
            MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status))
                result = result with { AvailableMemoryBytes = Bytes(status.AvailablePhysical), TotalMemoryBytes = Bytes(status.TotalPhysical) };
        }
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            result = result with
            {
                RecorderCpuTicks = process.TotalProcessorTime.Ticks,
                WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64,
                PeakWorkingSetBytes = process.PeakWorkingSet64,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return result;
    }

    static long? Bytes(ulong value) => value <= long.MaxValue ? (long)value : null;
    [StructLayout(LayoutKind.Sequential)]
    struct FileTime { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")]
    static extern uint GetActiveProcessorCount(ushort groupNumber);
}
