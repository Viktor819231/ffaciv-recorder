using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OutsideRecorderCore;

/// <summary>One module (DLL/EXE) loaded in another process.</summary>
public sealed record ModuleInfo(string Name, string Path, uint Base, uint Size);

/// <summary>What <see cref="GameFinder"/> found: a Civ process and, if it runs a game core we know, its layout.</summary>
public sealed record FoundGame
{
    /// <summary>Process id of CivilizationV*.exe.</summary>
    public required int ProcessId { get; init; }
    /// <summary>Executable name, e.g. CivilizationV_DX11.exe.</summary>
    public required string ExeName { get; init; }
    /// <summary>The game core DLL Civ loaded (null before a game/mod is loaded).</summary>
    public ModuleInfo? GameCore { get; init; }
    /// <summary>SHA-256 of that DLL file.</summary>
    public string? GameCoreSha256 { get; init; }
    /// <summary>The known layout for it, or null = refuse to read.</summary>
    public GameCoreLayout? Layout { get; init; }
    /// <summary>True when the FFACiv_Recorder DLL (in-game recorder) is also loaded.</summary>
    public bool InGameRecorderLoaded { get; init; }
    /// <summary>Plain words for the window: why we read or why not.</summary>
    public required string Status { get; init; }
}

/// <summary>Finds a running Civ V and decides whether its game core is one we can read. Only looks, never touches.</summary>
public static class GameFinder
{
    static readonly string[] ExeNames = ["CivilizationV", "CivilizationV_DX11", "CivilizationV_Tablet"];
    static readonly Dictionary<(string Path, long Size, DateTime Time), string> hashCache = new();

    /// <summary>The running Civ V processes (normally zero or one). One pass over the process list, not one per exe name:
    /// every pass makes Windows list all processes and threads on the PC.</summary>
    public static IEnumerable<Process> CivProcesses()
    {
        var civ = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            if (ExeNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) civ.Add(p);
            else p.Dispose();
        }
        return civ;
    }

    /// <summary>Looks at one Civ process: its modules, the game core's file hash and loaded PE header.</summary>
    public static FoundGame Inspect(Process p)
    {
        var exe = p.ProcessName + ".exe";
        List<ModuleInfo> modules;
        try { modules = Modules(p.Id); }
        catch (Exception e) { return new() { ProcessId = p.Id, ExeName = exe, Status = $"Civ is running but Windows did not let us list its parts ({e.Message})." }; }
        bool inGameRecorder = modules.Any(m => m.Name.Equals("CvGameCore_FFACivRecorder.dll", StringComparison.OrdinalIgnoreCase));
        var cores = modules.Where(m => m.Name.Equals("CvGameCore_Expansion2.dll", StringComparison.OrdinalIgnoreCase)).ToList();
        if (cores.Count == 0)
            return new() { ProcessId = p.Id, ExeName = exe, InGameRecorderLoaded = inGameRecorder, Status = "Civ is running; no game loaded yet." };
        foreach (var core in cores)
        {
            string sha;
            try { sha = Sha256Of(core.Path); }
            catch (Exception e) { return new() { ProcessId = p.Id, ExeName = exe, GameCore = core, Status = $"Cannot read the game core file ({e.Message})." }; }
            var layout = KnownLayouts.BySha256(sha);
            if (layout is null) continue;
            using var mem = new ProcessMemory(p.Id);
            if (!PeHeaderMatches(mem, core.Base, layout))
                return new() { ProcessId = p.Id, ExeName = exe, GameCore = core, GameCoreSha256 = sha, InGameRecorderLoaded = inGameRecorder,
                    Status = "The game core in memory does not match its file; not recording." };
            return new() { ProcessId = p.Id, ExeName = exe, GameCore = core, GameCoreSha256 = sha, Layout = layout, InGameRecorderLoaded = inGameRecorder,
                Status = $"{layout.Name} found." };
        }
        var first = cores[0];
        string? firstSha = null;
        try { firstSha = Sha256Of(first.Path); } catch { }
        return new() { ProcessId = p.Id, ExeName = exe, GameCore = first, GameCoreSha256 = firstSha, InGameRecorderLoaded = inGameRecorder,
            Status = $"This game core is not one we know ({System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(first.Path))}); not recording." };
    }

    /// <summary>
    /// Checks the loaded image's PE header (TimeDateStamp, SizeOfImage) against the layout,
    /// so the bytes in memory are really the DLL whose hash we checked.
    /// </summary>
    public static bool PeHeaderMatches(IMemory mem, uint moduleBase, GameCoreLayout layout)
    {
        Span<byte> head = stackalloc byte[0x400];
        if (!mem.TryRead(moduleBase, head) || head[0] != 'M' || head[1] != 'Z') return false;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(head[0x3C..]);
        if (pe < 0 || pe + 24 + 60 > head.Length || head[pe] != 'P' || head[pe + 1] != 'E') return false;
        uint stamp = BinaryPrimitives.ReadUInt32LittleEndian(head[(pe + 8)..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(head[(pe + 24 + 56)..]);
        return stamp == layout.PeTimeDateStamp && size == layout.PeSizeOfImage;
    }

    /// <summary>SHA-256 of a file (cached by path, size and time so a big DLL is hashed once).</summary>
    public static string Sha256Of(string path)
    {
        var fi = new FileInfo(path);
        var key = (fi.FullName.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc);
        lock (hashCache)
            if (hashCache.TryGetValue(key, out var cached)) return cached;
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hex = Convert.ToHexStringLower(SHA256.HashData(s));
        lock (hashCache) hashCache[key] = hex;
        return hex;
    }

    /// <summary>Lists a process's modules with a Toolhelp snapshot (32-bit modules of a WOW64 process included).</summary>
    public static List<ModuleInfo> Modules(int pid)
    {
        const uint TH32CS_SNAPMODULE = 0x8, TH32CS_SNAPMODULE32 = 0x10;
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
        if (snap == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var list = new List<ModuleInfo>();
        try
        {
            var me = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            for (bool ok = Module32FirstW(snap, ref me); ok; ok = Module32NextW(snap, ref me))
            {
                ulong b = (ulong)me.modBaseAddr;
                if (b <= uint.MaxValue) list.Add(new ModuleInfo(me.szModule, me.szExePath, (uint)b, me.modBaseSize));
            }
        }
        finally { CloseHandle(snap); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MODULEENTRY32W
    {
        public uint dwSize, th32ModuleID, th32ProcessID, GlblcntUsage, ProccntUsage;
        public nint modBaseAddr;
        public uint modBaseSize;
        public nint hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Module32FirstW(nint snap, ref MODULEENTRY32W me);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Module32NextW(nint snap, ref MODULEENTRY32W me);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
}
