using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using SharpPdb.Windows;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>
/// 0.4 (card OR03 "Tiles"): the tiles in one read, block reads, tiered rates (units every look, cities 5 a second, tiles
/// once a second), the tape keeping only changed ranges of big reads; and what one look costs now, on the vanilla tapes.
/// </summary>
public sealed class TilesTests(ITestOutputHelper output)
{
    const uint Base = 0x10000000;

    // ---- the proofs ----------------------------------------------------------------------------------------------------
    [Fact]
    public void LekmodPlotsMatchItsPdb()
    {
        var path = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Sid Meier's Civilization V\Assets\DLC\LEKMOD_v35.4\CvGameCore_Expansion2.pdb",
            TestData.LekmodPdb,
        }.FirstOrDefault(File.Exists);
        if (path is null) { output.WriteLine("Lekmod PDB not on this PC: skipped"); return; }
        using var pdb = new PdbFile(path);
        Assert.Equal("4d0dd08d-b7a6-46f4-86d3-ae7f45dac65b/26", $"{pdb.InfoStream.Header.Guid}/{pdb.DbiStream.Header.Age}");
        SnowveLayoutTests.AssertPlots(KnownLayouts.Lekmod354, pdb.TpiStream);
    }

    /// <summary>BNW and every Snowve release: each tile value is what that core's own code reads (evidence-plots.txt).</summary>
    [Fact]
    public void PlotsAreInTheCodeEvidence()
    {
        var text = File.ReadAllText(Path.Combine(EvidenceTests.EvidenceDir(), "evidence-plots.txt"));
        var parts = text.Split("## ").Skip(1).ToList();
        Assert.Equal(5, parts.Count);
        foreach (var (part, L) in parts.Zip(new[] { KnownLayouts.Bnw103279, SnowveLayouts.Snowve811, SnowveLayouts.Snowve820, SnowveLayouts.Snowve821, SnowveLayouts.Snowve830 }))
        {
            Assert.Contains(L.DllSha256, part);
            Assert.Contains($"imul eax,{L.PlotSize:X}h", part);
            Assert.Contains($"[ecx+{L.MapPlots:X}h]", part.Replace("0FE4h", "FE4h"));
            Assert.Contains($"GetOwner: wrapper", part); Assert.Contains($"[ECX+0x{L.PlotOwner:X}(Int8)]", part);
            Assert.Contains($"[ECX+0x{L.PlotImprovement:X}(Int8)]", part);
            Assert.Contains($"[ECX+0x{L.PlotRoute:X}(Int8)]", part);
            Assert.Contains($"mov al,[ecx+{L.PlotPillagedByte:X}h]", part);
            Assert.Contains($"byte ptr [edi+{L.PlotResource:X}h]", part);
            Assert.Contains("movsx eax,word ptr [ecx]", part); Assert.Contains("movsx eax,word ptr [ecx+2]", part);
            // improvement pillaged = bit 0 ("and al,1" right after the load), route pillaged = bit 1 ("shr al,1")
            Assert.Equal(0, L.PlotImprovementPillagedBit); Assert.Equal(1, L.PlotRoutePillagedBit);
            Assert.Contains("shr al,1", part);
        }
    }

    // ---- reading (fake memory with the real layout) ------------------------------------------------------------------
    static GameCoreLayout L => KnownLayouts.Lekmod354;

    static (FakeMemory M, byte[] Plots) GameWithTiles(int w = 80, int h = 52)
    {
        var (m, _, _) = ReaderTests.FakeGame(L);
        uint map = 0x21000000, plots = 0x60000000;
        var bytes = m.Block(plots, w * h * L.PlotSize!.Value);
        m.I(map + (uint)L.MapPlots!.Value, (int)plots);
        for (int i = 0; i < w * h; i++)
        {
            int o = i * L.PlotSize.Value;
            BitConverter.GetBytes((short)(i % w)).CopyTo(bytes, o + L.PlotX); BitConverter.GetBytes((short)(i / w)).CopyTo(bytes, o + L.PlotY);
            bytes[o + L.PlotOwner!.Value] = 0xFF; bytes[o + L.PlotResource!.Value] = 0xFF; bytes[o + L.PlotImprovement!.Value] = 0xFF; bytes[o + L.PlotRoute!.Value] = 0xFF;
        }
        return (m, bytes);
    }

    static void Set(byte[] plots, int x, int y, int field, byte v, int w = 80) => plots[(y * w + x) * L.PlotSize!.Value + field] = v;

    [Fact]
    public void TheTilesAreReadOnceASecondAndTheirChangesWritten()
    {
        var (m, plots) = GameWithTiles();
        Set(plots, 10, 5, L.PlotOwner!.Value, 0);
        var r = new StateReader(m, Base, L);
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-tiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reads = new List<bool>();
            using (var rec = new JsonlRecording(dir, "e.jsonl", () => 0, false))
            {
                var pipe = new GamePipeline(rec, null, true);
                for (int t = 0; t <= 2400; t += 200)
                {
                    if (t == 1200)
                    {
                        Set(plots, 11, 5, L.PlotOwner!.Value, 0);                                   // borders grow
                        Set(plots, 10, 5, L.PlotImprovement!.Value, 3);                             // a farm
                        plots[(5 * 80 + 10) * L.PlotSize!.Value + L.PlotPillagedByte!.Value] |= (byte)(1 << L.PlotImprovementPillagedBit);
                    }
                    var (st, s, _) = r.Read(t);
                    reads.Add(s!.Tiles is not null);
                    pipe.Step(st, s, t);
                }
            }
            // 0 ms; 600 ms = the turn's settle look (0.5 s after its first look: tiles read fresh for its snapshot); then once a second
            Assert.Equal([true, false, false, true, false, false, false, false, true, false, false, false, false], reads);
            var lines = File.ReadAllLines(Path.Combine(dir, "e.jsonl"));
            var changed = lines.Where(l => l.Contains("\"plot_changed\"")).ToList();
            Assert.Equal(2, changed.Count);
            Assert.Contains(changed, l => l.Contains("\"x\":11") && l.Contains("\"owner\":[-1,0]"));
            Assert.Contains(changed, l => l.Contains("\"x\":10") && l.Contains("\"improvement\":[-1,3]") && l.Contains("\"improvementPillaged\":[false,true]"));
            // the snapshot holds the whole table: gzip of 5 columns × 4160 tiles
            var snap = JsonDocument.Parse(lines.First(l => l.Contains("\"kind\":\"snapshot\""))).RootElement.GetProperty("data").GetProperty("tiles");
            var raw = Unzip(Convert.FromBase64String(snap.GetProperty("data").GetString()!));
            Assert.Equal(5 * 80 * 52, raw.Length);
            Assert.Equal(0, (sbyte)raw[5 * 80 + 10]);                                           // owner column, tile (10,5)
            Assert.Equal(-1, (sbyte)raw[80 * 52 + 0]);                                          // resource column
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>0.4.3 (the 124-turn live check): a turn's snapshot is the state 0.5 s after the turn change, with cities and tiles read fresh then.</summary>
    [Fact]
    public void TheTurnSnapshotWaitsForCivsTurnWork()
    {
        var (m, plots) = GameWithTiles();
        var (_, _, city) = (0, 0, 0x43000000u);
        var r = new StateReader(m, Base, L);
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-settle-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var rec = new JsonlRecording(dir, "e.jsonl", () => 0, false))
            {
                var pipe = new GamePipeline(rec, null, true);
                for (int t = 0; t <= 2500; t += 100)
                {
                    if (t == 1000) m.I(Base + L.RvaGameTurn, 43);                               // the turn changes ...
                    if (t == 1200) { m.I(city + (uint)L.CityPopulation, 6); Set(plots, 30, 20, L.PlotOwner!.Value, 1); }   // ... and Civ's turn work follows
                    var (st, s, _) = r.Read(t);
                    pipe.Step(st, s, t);
                }
            }
            var snaps = File.ReadAllLines(Path.Combine(dir, "e.jsonl")).Where(l => l.Contains("\"kind\":\"snapshot\"")).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            var t43 = snaps.Single(x => x.GetProperty("turn").GetInt32() == 43);
            Assert.Equal(6, t43.GetProperty("data").GetProperty("cities")[0].GetProperty("population").GetInt32());
            Assert.Equal(1500, t43.GetProperty("data").GetProperty("tiles").GetProperty("readAtMs").GetInt64());
            var raw = Unzip(Convert.FromBase64String(t43.GetProperty("data").GetProperty("tiles").GetProperty("data").GetString()!));
            Assert.Equal(1, (sbyte)raw[20 * 80 + 30]);
            Assert.True(snaps.Single(x => x.GetProperty("turn").GetInt32() == 42).GetProperty("data").GetProperty("units").GetArrayLength() > 0);   // the first snapshot has the units
        }
        finally { Directory.Delete(dir, true); }
    }

    static byte[] Unzip(byte[] z) { using var i = new GZipStream(new MemoryStream(z), CompressionMode.Decompress); using var o = new MemoryStream(); i.CopyTo(o); return o.ToArray(); }

    [Fact]
    public void ATableThatDoesNotLookRightIsNotTrusted()
    {
        var (m, plots) = GameWithTiles();
        BitConverter.GetBytes((short)7).CopyTo(plots, 100 * L.PlotSize!.Value + L.PlotX);  // plot 100 says it is somewhere else
        Assert.Null(new StateReader(m, Base, L).Read(0).Snapshot!.Tiles);
    }

    [Fact]
    public void CitiesAreReadFiveTimesASecondUnitsEveryLook()
    {
        var (m, _, city) = ReaderTests.FakeGame(L);
        var r = new StateReader(m, Base, L);
        var pops = new List<int>();
        for (int t = 0; t <= 400; t += 50)
        {
            if (t == 100) m.I(city + (uint)L.CityPopulation, 6);
            pops.Add(r.Read(t).Snapshot!.Cities.Single().Population);
        }
        Assert.Equal([5, 5, 5, 5, 6, 6, 6, 6, 6], pops);    // the change shows at the next city read (200 ms), the city never "disappears"
    }

    // ---- the tape: big reads go as changed ranges ------------------------------------------------------------------------
    [Fact]
    public void ABigReadThatChangedALittleGoesAsItsChangedRanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-tape-d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var block = new byte[2_000_000]; new Random(1).NextBytes(block);
            var seen = new List<byte[]>();
            using (var w = new TapeWriter(dir, new TapeHeader { Layout = "x", CoreSha256 = "y", ModuleBase = 1, SampleMs = 50, Started = "t", Reads = 2 }))
                for (int i = 0; i < 30; i++)
                {
                    block[(i * 7919) % block.Length] ^= 0x5A; block[(i * 104729) % block.Length]++;
                    w.Begin(i); w.Read(0x60000000, block, true); w.Read(0x1000, BitConverter.GetBytes(i), true); w.End(1);
                    seen.Add((byte[])block.Clone());
                }
            var back = TapeReader.Read(Path.Combine(dir, "tape.bin.gz")).Where(x => x.Sample is not null).Select(x => x.Sample!.Changes.Single(c => c.Address == 0x60000000).Data!).ToList();
            Assert.Equal(30, back.Count);
            for (int i = 0; i < 30; i++) Assert.True(seen[i].AsSpan().SequenceEqual(back[i]), $"sample {i} differs");
            long size = new FileInfo(Path.Combine(dir, "tape.bin.gz")).Length;
            output.WriteLine($"30 looks of a 2 MB block with 2 bytes changed each: tape {size / 1024} KB");
            Assert.True(size < 2_400_000, $"tape {size} bytes");   // the first look whole (random bytes do not pack), then a few bytes each
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- what one look costs now (the vanilla 146-turn tapes, read the 0.4 way) ------------------------------------------
    static readonly string Recording = TestData.Saves(@"outside-recorder 2026-10-27 vanilla mp\recording");

    /// <summary>
    /// The 0.3 tapes hold every field the 0.4 reads need (in smaller reads): laid out byte by byte, they answer the bigger
    /// blocks, so the 0.4 reader runs on them and its calls can be counted.
    /// </summary>
    sealed class Pages : IMemory
    {
        readonly Dictionary<uint, byte[]> pages = [];
        public long Calls, Bytes;
        public void Apply(TapeSample s)
        {
            foreach (var (a, l, d) in s.Changes)
                if (d is not null)
                    for (int i = 0; i < l; i++)
                    {
                        uint at = a + (uint)i;
                        if (!pages.TryGetValue(at >> 12, out var p)) pages[at >> 12] = p = new byte[4096];
                        p[at & 0xFFF] = d[i];
                    }
        }
        public bool TryRead(uint address, Span<byte> buffer)
        {
            Calls++; Bytes += buffer.Length;
            bool any = false;
            for (int i = 0; i < buffer.Length; i++)
            {
                uint at = address + (uint)i;
                if (pages.TryGetValue(at >> 12, out var p)) { buffer[i] = p[at & 0xFFF]; any = true; } else buffer[i] = 0;
            }
            return any;
        }
    }

    [Fact]
    public void WhatOneLookCostsNow()
    {
        if (!Directory.Exists(Recording)) { output.WriteLine("recording not on this PC: skipped"); return; }
        var session = Directory.GetDirectories(Recording, "session-*").Order().Last();
        var oldMem = new ReplayMemory(); var mem = new Pages(); StateReader? oldR = null, newR = null;
        long oldCalls = 0; int looks = 0, unitsOld = 0, unitsNew = 0, citiesNew = 0, w = 0, h = 0;
        var counting = new CountingMemory(oldMem);
        double newMs = 0;
        long t = 0;
        foreach (var (hd, s) in TapeReader.Read(Path.Combine(session, "tape.bin.gz")))
        {
            if (hd is not null)
            {
                var layout = KnownLayouts.All.Single(l => l.Id == hd.Layout);
                oldMem.Clear(); oldR = new StateReader(counting, hd.ModuleBase, layout) { BlockReads = false };
                newR = new StateReader(mem, hd.ModuleBase, layout);
                continue;
            }
            oldMem.Apply(s!); mem.Apply(s!);
            t = s!.TimeUs / 1000;
            long c0 = counting.Calls;
            var (st, old, _) = oldR!.Read(t);
            if (st != SampleStatus.Ok || old is null) continue;
            oldCalls += counting.Calls - c0;
            long n0 = mem.Calls;
            var sw = Stopwatch.GetTimestamp();
            var (_, now, _) = newR!.Read(t);
            newMs += Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            looks++; unitsOld += old.Units.Count; unitsNew += now?.Units.Count ?? 0; citiesNew += now?.Cities.Count ?? 0; w = old.MapWidth; h = old.MapHeight;
        }
        Assert.True(looks > 0);
        double callsNew = (double)mem.Calls / looks, callsOld = (double)oldCalls / looks, kbNew = mem.Bytes / 1024.0 / looks;
        output.WriteLine($"{Path.GetFileName(session)}: {looks} looks, {unitsOld / looks} units, map {w}×{h}");
        output.WriteLine($"  0.3 reads: {callsOld:F0} calls a look");
        output.WriteLine($"  0.4 reads: {callsNew:F0} calls a look, {kbNew:F0} KB a look (units every look, cities 5/s; tiles not on this tape)");
        output.WriteLine($"  same units seen: {unitsNew == unitsOld} ({unitsNew / looks} vs {unitsOld / looks}), cities {citiesNew / looks}");
        // live estimate with the 0.3 tape's own measure: 4.4 ms for 921 calls = 4.8 µs a call, plus ~0.6 µs a KB copied
        double estimate = callsNew * 4.8 / 1000 + kbNew * 0.6 / 1000;
        output.WriteLine($"  estimated live time a look: {estimate:F2} ms (was 4.4 ms) → {estimate * 20 / 10:F1} % of one core at 20 looks a second");
        int plots = w * h; double tilesMs = 1.5 * plots / 4160.0 + 0.0048;
        output.WriteLine($"  tiles: one {plots * 0x1E4 / 1024} KB read once a second ≈ {tilesMs:F1} ms → {tilesMs / 10:F2} % of one core");
        Assert.Equal(unitsOld, unitsNew);
        Assert.True(callsNew * 2 <= callsOld, $"{callsNew:F0} calls vs {callsOld:F0}");
    }

    sealed class CountingMemory(IMemory inner) : IMemory
    {
        public long Calls;
        public bool TryRead(uint address, Span<byte> buffer) { Calls++; return inner.TryRead(address, buffer); }
    }

    /// <summary>The tile diff and table on a Huge map (10,240 tiles): what the once-a-second part costs besides the read.</summary>
    [Fact]
    public void TheTileWorkIsSmall()
    {
        int w = 128, h = 80, n = w * h;
        var rnd = new Random(3);
        TileTable T() { var t = new TileTable(w, h, new sbyte[n], new sbyte[n], new sbyte[n], new sbyte[n], new bool[n], new bool[n], 0); rnd.NextBytes((byte[])(Array)t.Owner); return t; }
        var a = T(); var b = a with { Owner = (sbyte[])a.Owner.Clone() };
        for (int i = 0; i < 40; i++) b.Owner[rnd.Next(n)]++;
        GamePipeline.TileChanges(a, b, 1).ToList(); GamePipeline.TileTableData(b);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) GamePipeline.TileChanges(a, b, 1).ToList();
        double diff = sw.Elapsed.TotalMilliseconds / 20; sw.Restart();
        var data = GamePipeline.TileTableData(b);
        double table = sw.Elapsed.TotalMilliseconds;
        output.WriteLine($"Huge map: diff {diff:F2} ms a second; whole table for the turn snapshot {table:F1} ms, {((string)data["data"]!).Length / 1024} KB base64");
        Assert.True(diff < 100, $"{diff:F1} ms");   // a loose limit: this PC is often busy with other builds
    }
}
