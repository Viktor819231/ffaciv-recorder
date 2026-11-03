using System.Text.Json;

namespace OutsideRecorderTests;

public sealed class CityMicroTests
{
    const uint Base = 0x10000000, Citizens = 0x44000000, Specialists = 0x45000000,
        Buildings = 0x45100000, Forced = 0x45200000, Queue = 0x45300000;
    public static TheoryData<string> Layouts => new(KnownLayouts.All.Select(l => l.Id));

    static (FakeMemory Mem, uint City, GameCoreLayout Layout) Game(string id)
    {
        var l = KnownLayouts.All.Single(l => l.Id == id);
        var (m, _, city) = ReaderTests.FakeGame(l);
        var offsets = l.CityMicro!;
        m.Block(Citizens, 0x84); m.I(Citizens, (int)city); m.B(Citizens + 0x80, true);
        m.I(city + (uint)offsets.Citizens, (int)Citizens);
        m.I(Citizens + 0x14, 2); m.B(Citizens + 0x18, true); m.B(Citizens + 5, true);
        m.B(Citizens + 0x19, true); m.B(Citizens + 0x1B, true); m.B(Citizens + 0x40, true);
        m.I(Citizens + 0x64, 1); m.I(Citizens + 0x68, 1);
        m.Block(Specialists, 12); m.Block(Buildings, 16); m.Block(Forced, 16);
        m.I(Citizens + 0x6C, (int)Specialists); m.I(Citizens + 0x74, (int)Buildings); m.I(Citizens + 0x78, (int)Forced);
        m.I(Specialists + 4, 2); m.I(Buildings + 8, 2); m.I(Forced + 8, 1);
        void Vector(uint address, uint data, int count)
        {
            m.I(address + 4, (int)data); m.I(address + 8, (int)(data + count * 4)); m.I(address + 12, (int)(data + count * 4));
        }
        Vector(Base + l.RvaGlobals + (uint)offsets.SpecialistInfos, 0x46000000, 3);
        m.Block(0x46100000, 16); m.I(Base + l.RvaGlobals + (uint)offsets.BuildingInfos, 0x46100000);
        Vector(0x46100000, 0x46200000, 4);
        m.Block(Queue, 32); m.I(city + (uint)offsets.Queue, (int)Queue); m.I(city + (uint)offsets.Queue + 4, 2); m.I(city + (uint)offsets.Queue + 8, 25);
        m.I(Queue, 0); m.I(Queue + 4, 82); m.I(Queue + 8, 3); m.B(Queue + 12, true);
        m.I(Queue + 16, 1); m.I(Queue + 20, 12);
        m.I(city + (uint)offsets.Food100, 1250); m.I(city + (uint)offsets.OverflowRaw, 70);
        return (m, city, l);
    }

    [Theory, MemberData(nameof(Layouts))]
    public void ReadsVerifiedCityManagementForEveryLayout(string id)
    {
        var (m, _, l) = Game(id);
        var s = Assert.Single(new StateReader(m, Base, l).Read(0).Snapshot!.CityMicro!);
        Assert.Equal(2, s.Focus); Assert.True(s.AvoidGrowth); Assert.True(s.NoAutoAssignSpecialists);
        Assert.Equal([0, 2], s.WorkedPlots!); Assert.Equal([2], s.ForcedPlots!);
        Assert.Equal([0, 2, 0], s.Specialists!); Assert.Equal([0, 0, 2, 0], s.SpecialistsByBuilding!);
        Assert.Equal([0, 0, 1, 0], s.ForcedSpecialistsByBuilding!);
        Assert.Equal(1, s.DefaultSpecialists); Assert.Equal(1, s.ForcedDefaultSpecialists);
        Assert.Equal(1250, s.Food100); Assert.Equal(70, s.OverflowProductionRaw);
        Assert.Equal(new CityOrder(0, 82, 3, true), s.Queue![0]); Assert.Equal(2, s.Queue.Length);
    }

    [Fact]
    public void MicroIsOnceASecondWhileHealthStillChangesEveryTwoHundredMilliseconds()
    {
        var (m, city, l) = Game(KnownLayouts.Bnw103279.Id);
        var r = new StateReader(m, Base, l);
        var times = new List<long>();
        for (int t = 0; t <= 2000; t += 50)
        {
            if (t == 200) { m.I(city + (uint)l.CityDamage, 35); m.I(Citizens + 0x14, 3); }
            var s = r.Read(t).Snapshot!;
            if (t == 200) Assert.Equal(35, s.Cities.Single().Damage);
            if (s.CityMicro is { } micro)
            {
                times.Add(t); Assert.Equal(t, micro.Single().ReadAtMs);
                Assert.Equal(t == 0 ? 2 : 3, micro.Single().Focus);
            }
        }
        Assert.Equal([0L, 1000L, 2000L], times); // the turn-settle read at 500 ms does not resample micro
    }

    [Fact]
    public void MissingAndMalformedOptionalReadsDoNotLoseCityBasicsOrInventZeros()
    {
        var (m, city, l) = Game(KnownLayouts.Bnw103279.Id);
        m.I(Citizens + 0x6C, 0x77700000); // unreadable specialists
        m.I(city + (uint)l.CityMicro!.Queue + 4, 100000); // corrupt queue length
        var r = new StateReader(m, Base, l);
        var s = r.Read(0).Snapshot!;
        Assert.Equal(12, s.Cities.Single().Damage);
        Assert.Null(s.CityMicro!.Single().Specialists); Assert.Null(s.CityMicro!.Single().Queue);
        Assert.Equal([0, 2], s.CityMicro!.Single().WorkedPlots!);
        m.I(Citizens, 0x12345678); // stale citizen object belongs to another city
        var next = r.Read(1000).Snapshot!;
        Assert.Null(next.CityMicro!.Single().Focus); Assert.Single(next.Cities);
    }

    [Fact]
    public void ReloadAndClockRewindResampleButNormalTurnsKeepTheOneSecondRate()
    {
        var (m, _, l) = Game(KnownLayouts.Bnw103279.Id);
        var r = new StateReader(m, Base, l);
        Assert.NotNull(r.Read(100).Snapshot!.CityMicro);
        m.I(Base + l.RvaGameTurn, 43);
        Assert.Null(r.Read(150).Snapshot!.CityMicro);
        m.I(Base + l.RvaGameTurn, 41);
        Assert.NotNull(r.Read(200).Snapshot!.CityMicro);
        Assert.NotNull(r.Read(0).Snapshot!.CityMicro);
        m.B(Base + l.RvaGameStarted!.Value, false); Assert.Equal(SampleStatus.NoGame, r.Read(50).Status);
        m.B(Base + l.RvaGameStarted.Value, true); Assert.NotNull(r.Read(100).Snapshot!.CityMicro);
    }

    [Fact]
    public void LogsBaselineThenOnlyChangedFieldsIncludingUnknownAndRecovery()
    {
        var (m, _, l) = Game(KnownLayouts.Bnw103279.Id);
        var r = new StateReader(m, Base, l); var d = new CityMicroDiffer();
        var a = r.Read(0).Snapshot!.CityMicro!;
        Assert.Equal("city_micro_baseline", Assert.Single(d.Next(a, 42)).Method);
        Assert.Empty(d.Next(r.Read(1000).Snapshot!.CityMicro, 42));
        m.I(Citizens + 0x14, 3); m.I(Queue + 4, 83);
        var e = Assert.Single(d.Next(r.Read(2000).Snapshot!.CityMicro, 42));
        var changes = Assert.IsType<Dictionary<string, object?>>(e.Args["changes"]);
        Assert.Equal(2, changes.Count); Assert.True(changes.ContainsKey("focus")); Assert.True(changes.ContainsKey("queue"));
        m.I(Citizens + 0x6C, 0); e = Assert.Single(d.Next(r.Read(3000).Snapshot!.CityMicro, 42));
        changes = Assert.IsType<Dictionary<string, object?>>(e.Args["changes"]);
        Assert.Null(Assert.IsType<object?[]>(changes["specialists"])[1]);
        m.I(Citizens + 0x6C, (int)Specialists); Assert.Single(d.Next(r.Read(4000).Snapshot!.CityMicro, 42));
        d.RemoveAt(27, 30); Assert.Equal("city_micro_baseline", Assert.Single(d.Next(a, 42)).Method);
    }

    [Fact]
    public void NewTapeReplaysMicroAndTurnSnapshotsPreserveItsObservationTime()
    {
        var (m, _, l) = Game(KnownLayouts.Bnw103279.Id);
        var dir = Path.Combine(Path.GetTempPath(), "city-micro-" + Guid.NewGuid().ToString("N"));
        try
        {
            long now = 0;
            using (var rec = new JsonlRecording(dir, "events.jsonl", () => now * 1000, false))
            using (var tape = new TapeWriter(dir, new TapeHeader { Layout = l.Id, CoreSha256 = l.DllSha256, ModuleBase = Base, SampleMs = 50, Started = "test", Reads = 3 }))
            {
                var mem = new TapingMemory(m) { Tape = tape }; var reader = new StateReader(mem, Base, l);
                var pipe = new GamePipeline(rec, null, true);
                for (now = 0; now <= 2500; now += 50)
                {
                    if (now == 800) m.I(Citizens + 0x14, 3);
                    if (now == 1100) m.I(Base + l.RvaGameTurn, 43);
                    mem.Begin(now * 1000); var (status, sample, _) = reader.Read(now);
                    pipe.Step(status, sample, now); mem.End(0);
                }
            }
            var original = File.ReadAllLines(Path.Combine(dir, "events.jsonl"));
            Assert.Equal(original, File.ReadAllLines(TapeReplay.Run(dir).Output));
            Assert.Single(original, s => s.Contains("\"city_micro_changed\""));
            var snapshot = original.Select(s => JsonDocument.Parse(s).RootElement).Single(e => e.GetProperty("kind").GetString() == "snapshot" && e.GetProperty("turn").GetInt32() == 43);
            Assert.Equal(1000, snapshot.GetProperty("data").GetProperty("cityMicro")[0].GetProperty("readAtMs").GetInt64());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
