using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OutsideRecorderCore;

/// <summary>Optional Windows-enforced CPU ceiling for this recorder host only; child applications escape its job.</summary>
public sealed class CpuLimit : IDisposable
{
    readonly SafeFileHandle job;
    CpuLimit(SafeFileHandle job) => this.job = job;

    /// <summary>Attaches only when explicitly requested; returns an explanation if a host job or OS refuses it.</summary>
    public static CpuLimit? TryApply(int percent, out string? error)
    {
        if (percent is < 1 or > 100) { error = "CPU ceiling must be between 1 and 100 percent of total machine CPU."; return null; }
        if (!OperatingSystem.IsWindows()) { error = "The CPU ceiling requires Windows."; return null; }
        SafeFileHandle? handle = null;
        try
        {
            handle = CreateJobObject(0, null);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            // A browser/Explorer opened by the recorder, and WebView2 helpers, must never inherit its ceiling.
            // SILENT_BREAKAWAY_OK permits children to leave automatically, without special CreateProcess flags.
            ExtendedLimits limits = HostOnlyLimits();
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // ENABLE | HARD_CAP. No kill-on-close, process suspension, debugger or game handle.
            CpuRate rate = new() { Flags = 0x1 | 0x4, Rate = (uint)percent * 100 };
            if (!SetInformationJobObject(handle, 15, ref rate, (uint)Marshal.SizeOf<CpuRate>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using var process = Process.GetCurrentProcess();
            if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            error = null;
            return new(handle);
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            handle?.Dispose();
            error = "Windows could not apply the recorder CPU ceiling: " + e.Message;
            return null;
        }
    }

    /// <summary>Removes the optional ceiling without terminating the recorder.</summary>
    public void Dispose()
    {
        if (job.IsClosed) return;
        CpuRate disabled = new();
        SetInformationJobObject(job, 15, ref disabled, (uint)Marshal.SizeOf<CpuRate>());
        job.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] struct CpuRate { public uint Flags, Rate; }
    // winnt.h JOBOBJECT_BASIC_LIMIT_INFORMATION / IO_COUNTERS / JOBOBJECT_EXTENDED_LIMIT_INFORMATION.
    // SIZE_T and ULONG_PTR follow host bitness; LARGE_INTEGER and IO counters remain eight bytes.
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    static ExtendedLimits HostOnlyLimits() => new() { Basic = new() { LimitFlags = 0x00001000 } };
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref CpuRate information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}
