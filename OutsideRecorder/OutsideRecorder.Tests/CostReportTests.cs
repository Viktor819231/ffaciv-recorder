using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>
/// Hard numbers for the user (OR03, before choosing the look rate and the tile reads): how many reads one look makes and
/// in which part, what one ReadProcessMemory call costs (measured on this process: one call per object vs one big block),
/// and what the rest of the recorder (differ + lines, tape) costs per look. Prints a report; asserts only loose limits.
/// </summary>
public sealed class CostReportTests(ITestOutputHelper output)
{
    static readonly string Recording = TestData.Saves(@"outside-recorder 2026-10-27 vanilla mp\recording");

    sealed class Counting(IMemory inner) : IMemory
    {
        public string Phase = "globals";
        public readonly Dictionary<string, (long Calls, long Bytes)> ByPhase = [];
        public bool TryRead(uint address, Span<byte> buffer)
        {
            var (c, b) = ByPhase.GetValueOrDefault(Phase);
            ByPhase[Phase] = (c + 1, b + buffer.Length);
            return inner.TryRead(address, buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFree(nint address, nuint size, uint type);

    /// <summary>ReadProcessMemory through the recorder's own read-only handle, on a block of this process below 4 GB.</summary>
    [Fact]
    public void WhatOneReadCosts()
    {
        nint at = 0;
        for (nint hint = 0x30000000; hint < 0x7F000000 && at == 0; hint += 0x01000000) at = VirtualAlloc(hint, 8 << 20, 0x3000, 4);
        if (at == 0) { output.WriteLine("no block below 4 GB: skipped"); return; }
        try
        {
            using var plainMem = new ProcessMemory(Environment.ProcessId) { Guarded = false };
            using var guardedMem = new ProcessMemory(Environment.ProcessId);
            ProcessMemory mem = plainMem;
            double Time(int size, int n)
            {
                var buf = new byte[size];
                for (int i = 0; i < 20; i++) mem.TryRead((uint)at, buf);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++) mem.TryRead((uint)(at + (i * 4096) % (4 << 20)), size > 4096 ? buf : buf.AsSpan(0, size));
                return sw.Elapsed.TotalMilliseconds * 1000 / n;
            }
            foreach (var (size, n) in new[] { (4, 20000), (64, 20000), (0x160, 20000), (4096, 5000), (64 * 1024, 500), (484 * 2772, 50), (484 * 4160, 30), (484 * 10240, 20) })
            {
                mem = plainMem; double p = Time(size, n);
                mem = guardedMem; double g = Time(size, n);
                output.WriteLine($"ReadProcessMemory {size,9:N0} bytes: {p,8:F1} µs a call, with the memory-map check {g,8:F1} µs");
            }
        }
        finally { VirtualFree(at, 0, 0x8000); }
    }

    [Fact]
    public void ReadsPerLookAndTheRestOfTheWork()
    {
        if (!Directory.Exists(Recording)) { output.WriteLine("recording not on this PC: skipped"); return; }
        var session = Directory.GetDirectories(Recording, "session-*").Order().Last();
        var perLook = new List<(int Turn, Dictionary<string, (long Calls, long Bytes)> Reads, int Units, int Cities)>();
        double readerMs = 0; int looks = 0;
        var mem = new ReplayMemory(); Counting? counting = null; StateReader? reader = null;
        foreach (var (h, s) in TapeReader.Read(Path.Combine(session, "tape.bin.gz")))
        {
            if (h is not null)
            {
                mem.Clear(); counting = new Counting(mem);
                reader = TapeReplay.ReaderFor(h, counting);
                var c = counting; reader.Phase = p => c.Phase = p;
                continue;
            }
            mem.Apply(s!);
            counting!.ByPhase.Clear();
            var sw = Stopwatch.StartNew();
            var (st, snap, _) = reader!.Read(0);
            readerMs += sw.Elapsed.TotalMilliseconds; looks++;
            if (st == SampleStatus.Ok && snap is not null)
                perLook.Add((snap.Turn, new(counting.ByPhase), snap.Units.Count, snap.Cities.Count));
        }
        int last = perLook.Max(x => x.Turn);
        var late = perLook.Where(x => x.Turn >= last - 10).ToList();
        output.WriteLine($"{Path.GetFileName(session)}: turns {perLook.Min(x => x.Turn)}-{last}; late looks (turn {last - 10}+): {late.Count}, {late.Average(x => x.Units):F0} units, {late.Average(x => x.Cities):F0} cities");
        foreach (var phase in new[] { "globals", "players", "units", "cities", "timer" })
        {
            var calls = late.Average(x => x.Reads.GetValueOrDefault(phase).Calls);
            var bytes = late.Average(x => x.Reads.GetValueOrDefault(phase).Bytes);
            output.WriteLine($"  {phase,-8} {calls,7:F1} reads a look, {bytes / 1024,7:F1} KB");
        }
        var all = late.Average(x => x.Reads.Values.Sum(v => v.Calls));
        output.WriteLine($"  all      {all,7:F1} reads a look; reader work {readerMs / looks:F3} ms a look (replayed, no system calls)");

        // the rest of a look: the differ + the lines (TapeReplay = reader + pipeline + JSON), and the tape (changes + gzip)
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Path.Combine(session, "tape.bin.gz"), Path.Combine(dir, "tape.bin.gz"));
            TapeReplay.Run(dir);                                   // warm up (JIT)
            var sw = Stopwatch.StartNew();
            var (samples, lines, _) = TapeReplay.Run(dir);
            double whole = sw.Elapsed.TotalMilliseconds / samples;
            // the reader alone, warm, the same way TapeReplay reads (no counting wrapper)
            double plain = 0; int pn = 0;
            for (int round = 0; round < 2; round++)
            {
                plain = 0; pn = 0;
                var m3 = new ReplayMemory(); StateReader? r3 = null;
                foreach (var (h, s) in TapeReader.Read(Path.Combine(session, "tape.bin.gz")))
                {
                    if (h is not null) { m3.Clear(); r3 = TapeReplay.ReaderFor(h, m3); continue; }
                    var t0 = Stopwatch.GetTimestamp(); m3.Apply(s!); r3!.Read(0); plain += Stopwatch.GetElapsedTime(t0).TotalMilliseconds; pn++;
                }
            }
            double pipeline = whole - plain / pn;
            output.WriteLine($"replay: reader {plain / pn:F3} ms a look, reader + differ + lines {whole:F3} ms (whole replay incl. tape unpacking)");
            output.WriteLine($"differ + lines: {pipeline:F3} ms a look ({lines} lines for {samples} looks)");

            var tapeDir = Path.Combine(dir, "t2"); Directory.CreateDirectory(tapeDir);
            var mem2 = new ReplayMemory(); StateReader? r2 = null; TapingMemory? taping = null; TapeWriter? w = null;
            double tapeMs = 0; int n = 0;
            foreach (var (h, s) in TapeReader.Read(Path.Combine(session, "tape.bin.gz")))
            {
                if (h is not null)
                {
                    mem2.Clear(); w?.Dispose();
                    w = new TapeWriter(tapeDir, h); taping = new TapingMemory(mem2) { Tape = w };
                    r2 = TapeReplay.ReaderFor(h, taping);
                    continue;
                }
                mem2.Apply(s!);
                var t = Stopwatch.StartNew();
                taping!.Begin(s!.TimeUs); r2!.Read(0); taping.End(0);
                tapeMs += t.Elapsed.TotalMilliseconds; n++;
            }
            w?.Dispose();
            output.WriteLine($"reader + tape: {tapeMs / n:F3} ms a look -> tape {tapeMs / n - plain / pn:F3} ms a look; tape {new FileInfo(Path.Combine(tapeDir, "tape.bin.gz")).Length / 1024} KB for {n} looks");
            Assert.True(pipeline < 5);
        }
        finally { Directory.Delete(dir, true); }
    }
}
