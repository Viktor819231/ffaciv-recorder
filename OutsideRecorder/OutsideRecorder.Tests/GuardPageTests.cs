using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>
/// Can a read from outside change the game? The one known way: ReadProcessMemory on a PAGE_GUARD page (thread stacks grow
/// through guard pages). Measured here on this PC's Windows, on a page of this process made a guard page.
/// </summary>
public sealed class GuardPageTests(ITestOutputHelper output)
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFree(nint address, nuint size, uint type);
    [DllImport("kernel32.dll")] static extern nint VirtualQuery(nint address, out MBI info, nint length);
    [StructLayout(LayoutKind.Sequential)] struct MBI { public nint BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId; public nint RegionSize; public uint State, Protect, Type; }
    const uint PAGE_READWRITE = 4, PAGE_GUARD = 0x100;

    nint LowGuardPage()
    {
        for (nint hint = 0x31000000; hint < 0x7F000000; hint += 0x01000000)
        {
            var at = VirtualAlloc(hint, 4096 * 3, 0x3000, PAGE_READWRITE);
            if (at == 0) continue;
            // the middle page becomes a guard page, like the one under a thread's stack
            var g = VirtualAlloc(at + 4096, 4096, 0x1000, PAGE_READWRITE | PAGE_GUARD);
            Assert.Equal(at + 4096, g);
            return at;
        }
        return 0;
    }

    uint ProtectOf(nint a) { VirtualQuery(a, out var m, Marshal.SizeOf<MBI>()); return m.Protect; }

    /// <summary>What plain ReadProcessMemory does to a guard page (the 0.4.1 reader did no check): reported, not asserted.</summary>
    [Fact]
    public void WhatAPlainReadDoesToAGuardPage()
    {
        var at = LowGuardPage();
        if (at == 0) { output.WriteLine("no low block: skipped"); return; }
        try
        {
            using var mem = new ProcessMemory(Environment.ProcessId) { Guarded = false };
            bool before = (ProtectOf(at + 4096) & PAGE_GUARD) != 0;
            bool ok = mem.TryRead((uint)(at + 4096), new byte[16]);
            bool after = (ProtectOf(at + 4096) & PAGE_GUARD) != 0;
            output.WriteLine($"plain ReadProcessMemory on a guard page: read {(ok ? "succeeded" : "failed")}; guard before {before}, after {after}");
            bool okAcross = mem.TryRead((uint)(at + 4096 - 8), new byte[64]);
            output.WriteLine($"a read across into the guard page: {(okAcross ? "succeeded" : "failed")}; guard after {(ProtectOf(at + 4096) & PAGE_GUARD) != 0}");
        }
        finally { VirtualFree(at, 0, 0x8000); }
    }

    /// <summary>While Civ loads (0.4.2), only the globals are read: the header says whether a game runs, nothing else is touched.</summary>
    [Fact]
    public void TheHeaderReadTouchesOnlyTheGlobals()
    {
        var L = KnownLayouts.Lekmod354;
        var (m, _, _) = ReaderTests.FakeGame(L);
        var counting = new CountingFake(m);
        Assert.Equal((SampleStatus.Ok, 42, 80, 52), new StateReader(counting, 0x10000000, L).ReadHeader());
        Assert.True(counting.Calls <= 9, $"{counting.Calls} reads");
        Assert.DoesNotContain(counting.Addresses, a => a >= 0x30000000);           // no player, unit or city memory
        m.B(0x10000000 + L.RvaGameStarted!.Value, false);
        Assert.Equal(SampleStatus.NoGame, new StateReader(m, 0x10000000, L).ReadHeader().Status);
    }

    sealed class CountingFake(IMemory inner) : IMemory
    {
        public int Calls; public readonly List<uint> Addresses = [];
        public bool TryRead(uint address, Span<byte> buffer) { Calls++; Addresses.Add(address); return inner.TryRead(address, buffer); }
    }

    /// <summary>0.4.2: the reader never touches a guard page, nor anything in an allocation that holds one (a thread stack).</summary>
    [Fact]
    public void TheGuardedReaderNeverTouchesAGuardPage()
    {
        var at = LowGuardPage();
        if (at == 0) { output.WriteLine("no low block: skipped"); return; }
        try
        {
            using var mem = new ProcessMemory(Environment.ProcessId);
            Assert.False(mem.TryRead((uint)(at + 4096), new byte[16]));          // the guard page itself
            Assert.False(mem.TryRead((uint)(at + 4096 - 8), new byte[64]));      // across into it
            Assert.False(mem.TryRead((uint)at, new byte[16]));                   // a normal page of the same allocation (stack-like): not read either
            Assert.True((ProtectOf(at + 4096) & PAGE_GUARD) != 0, "the guard page lost its guard");
            var plain = VirtualAlloc(0, 4096, 0x3000, PAGE_READWRITE);
            if ((ulong)plain < uint.MaxValue) Assert.True(mem.TryRead((uint)plain, new byte[16]));   // ordinary memory still reads
            Assert.False(mem.TryRead(0x7F000000u, new byte[4]));                                   // (nothing committed there): no read
        }
        finally { VirtualFree(at, 0, 0x8000); }
    }
}
