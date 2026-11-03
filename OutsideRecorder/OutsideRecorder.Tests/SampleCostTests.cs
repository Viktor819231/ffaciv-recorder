using System.Diagnostics;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>
/// What one look at Civ costs (OR03, the user's "20 looks a second"): the tapes keep the live time of every sample
/// (ReadMs, Civ's memory read through the system); replaying them gives the reader's own work. From the user's
/// 146-turn vanilla multiplayer recording (not in git; skipped when missing).
/// </summary>
public sealed class SampleCostTests(ITestOutputHelper output)
{
    static readonly string Recording = TestData.Saves(@"outside-recorder 2026-10-27 vanilla mp\recording");

    [Theory]
    [InlineData(50, 4.4, 50)]     // the late vanilla game: 20 a second stays 20 a second
    [InlineData(50, 8.0, 80)]     // a busy PC: 8 ms a look -> 12.5 a second (10 % of one core)
    [InlineData(100, 30.0, 300)]
    [InlineData(200, 1.0, 200)]
    public void ThePaceSlowsDownOnlyWhenALookIsExpensive(int wanted, double lookMs, int interval) =>
        Assert.Equal(interval, RecorderSession.IntervalFor(wanted, lookMs));

    [Fact]
    public void TwentyLooksASecondByDefault()
    {
        Assert.Equal(50, new RecorderSettings().SampleMs);
        Assert.Equal(2, new RecorderSettings().StreamIntervalS);
        var f = Path.GetTempFileName();
        try { File.WriteAllText(f, RecorderSettings.DefaultFileText()); Assert.Equal(50, RecorderSettings.Load(f).SampleMs); Assert.Equal(2, RecorderSettings.Load(f).StreamIntervalS); }
        finally { File.Delete(f); }
    }

    [Fact]
    public void ALookAtALateBigGameIsCheap()
    {
        if (!Directory.Exists(Recording)) { output.WriteLine("recording not on this PC: skipped"); return; }
        var live = new List<(int Turn, double Ms, int Units)>();
        var replayMs = new List<(int Turn, double Ms)>();
        foreach (var session in Directory.GetDirectories(Recording, "session-*").Order())
        {
            var mem = new ReplayMemory(); StateReader? reader = null;
            foreach (var (h, s) in TapeReader.Read(Path.Combine(session, "tape.bin.gz")))
            {
                if (h is not null) { mem.Clear(); reader = new StateReader(mem, h.ModuleBase, KnownLayouts.All.Single(l => l.Id == h.Layout)) { BlockReads = h.Reads >= 2 }; continue; }
                mem.Apply(s!);
                var sw = Stopwatch.StartNew();
                var (st, snap, _) = reader!.Read(0);
                var ms = sw.Elapsed.TotalMilliseconds;
                if (st != SampleStatus.Ok || snap is null) continue;
                live.Add((snap.Turn, s!.ReadMs, snap.Units.Count));
                replayMs.Add((snap.Turn, ms));
            }
        }
        Assert.NotEmpty(live);
        int lastTurn = live.Max(x => x.Turn);
        string Line(string what, IEnumerable<double> v)
        {
            var a = v.Order().ToArray();
            return a.Length == 0 ? $"{what}: none" : $"{what}: n={a.Length} median {a[a.Length / 2]:F2} ms, p95 {a[(int)(a.Length * 0.95)]:F2} ms, max {a[^1]:F2} ms";
        }
        foreach (var (from, to) in new[] { (0, 30), (60, 90), (lastTurn - 20, lastTurn) })
        {
            var l = live.Where(x => x.Turn >= from && x.Turn <= to).ToList();
            output.WriteLine($"turns {from}-{to} ({(l.Count > 0 ? l.Average(x => x.Units) : 0):F0} units per look on average)");
            output.WriteLine("  " + Line("live (Civ's memory read)", l.Select(x => x.Ms)));
            output.WriteLine("  " + Line("reader work (replayed)", replayMs.Where(x => x.Turn >= from && x.Turn <= to).Select(x => x.Ms)));
        }
        var late = live.Where(x => x.Turn >= lastTurn - 20).Select(x => x.Ms).Order().ToArray();
        double p95 = late[(int)(late.Length * 0.95)];
        output.WriteLine($"20 looks a second at the late game's p95: {p95 * 20 / 10:F1} % of one core");
        Assert.True(p95 < 50, $"a late look took {p95:F1} ms (p95)");
    }
}
