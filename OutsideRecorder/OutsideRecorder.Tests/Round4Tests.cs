using System.Text.Json;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>Round 4 (card OR03 P1): fixes from the 2026-10-27 tests: a reload waits for the game, a rejoin after a restart
/// that went back a turn is a reload, the last lines are sent before the program ends.</summary>
public sealed class Round4Tests(ITestOutputHelper output)
{
    /// <summary>The user's Snowve single-player test with a reload from turn 16 to turn 12 (not in git; skipped when missing).</summary>
    static readonly string SnowveReload = TestData.Saves(@"outside-recorder 2026-10-27 snowve sp stream\session-20261027T152244-19836");

    static PlayerState P(int id) => new() { Id = id, Alive = true, TurnActive = id == 0, Slot = 3, CivType = id };
    static CityState C(int owner, int pop) => new() { Owner = owner, Id = 8192, X = owner, Y = 1, Population = pop, Name = "c" + owner, Damage = 0, TurnFounded = 0, OriginalOwner = owner, Puppet = false, Occupied = false };
    static GameSnapshot S(int turn, bool players = true, params CityState[] cities) => new()
    {
        TimeMs = 0, Turn = turn, ActivePlayer = 0, MapWidth = 40, MapHeight = 24, MapSeed = 1, SyncSeed = 2,
        Players = players ? [P(0), P(1)] : [], Units = players ? [new UnitState { Owner = 0, Id = 8192, Type = 1, X = 1, Y = 1, Damage = 0, Moves = 60, FortifyTurns = 0, Experience = 0, Level = 1, TurnCreated = 0, LastMoveTurn = 0 }] : [], Cities = cities,
    };

    static string[] Run(Action<GamePipeline> feed, int previousTurn = -1)
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-r4-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var rec = new JsonlRecording(dir, "e.jsonl", () => 0, false)) feed(new GamePipeline(rec, null, true, previousTurn));
            return File.ReadAllLines(Path.Combine(dir, "e.jsonl"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The user's vanilla game (2026-10-27): turn 0 had its players before its units; that look is not the game yet.</summary>
    [Fact]
    public void PlayersWithoutUnitsOrCitiesAreStillLoading()
    {
        Assert.False(GamePipeline.IsLoaded(S(0) with { Units = [] }));
        Assert.True(GamePipeline.IsLoaded(S(0)));
        Assert.True(GamePipeline.IsLoaded(S(0) with { Units = [], Cities = [C(0, 1)] }));
    }

    [Fact]
    public void AGameStillLoadingIsNotTheBaseline()
    {
        var lines = Run(p =>
        {
            p.Step(SampleStatus.Ok, S(12, players: false), 0);             // map + turn readable, players not built yet
            p.Step(SampleStatus.Ok, S(12, true, C(0, 1)), 200);            // half there: city size not set yet
            p.Step(SampleStatus.Ok, S(12, true, C(0, 5), C(1, 4)), 400);
            p.Step(SampleStatus.Ok, S(12, true, C(0, 5), C(1, 4)), 600);   // two samples agree: this is the game
            p.Step(SampleStatus.Ok, S(12, true, C(0, 6), C(1, 4)), 800);
        });
        var snaps = lines.Where(l => l.Contains("\"kind\":\"snapshot\"")).ToList();
        Assert.Single(snaps);
        Assert.Contains("\"population\":5", snaps[0]);
        Assert.DoesNotContain(lines, l => l.Contains("PlayerCityFounded"));
        Assert.Contains(lines, l => l.Contains("SetPopulation") && l.Contains("\"population\":6"));
    }

    [Fact]
    public void ARejoinAfterARestartThatWentBackIsAReload()
    {
        var lines = Run(p => { p.Step(SampleStatus.Ok, S(12), 0); p.Step(SampleStatus.Ok, S(12), 200); }, previousTurn: 16);
        Assert.Contains(lines, l => l.Contains("\"game_reloaded\"") && l.Contains("\"fromTurn\":16") && l.Contains("\"toTurn\":12") && l.Contains("restart"));
        Assert.Single(lines, l => l.Contains("\"kind\":\"snapshot\""));
        // the same turn again (Civ restarted, the game loaded where it was): no reload
        var same = Run(p => { p.Step(SampleStatus.Ok, S(16), 0); p.Step(SampleStatus.Ok, S(16), 200); }, previousTurn: 16);
        Assert.DoesNotContain(same, l => l.Contains("game_reloaded"));
        Assert.Single(same, l => l.Contains("\"kind\":\"snapshot\""));
    }

    [Fact]
    public void TheLastTurnOfARecordingIsFound()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-lt-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var r = new JsonlRecording(dir, "events.jsonl", () => 1)) { r.Write("lifecycle", 15, new { a = 1 }); r.Write("lifecycle", 16, new { a = 2 }); r.Write("lifecycle", null, new { a = 3 }); }
            Assert.Equal(16, JsonlRecording.LastTurn(Path.Combine(dir, "events.jsonl")));
            Assert.Null(JsonlRecording.LastTurn(Path.Combine(dir, "none.jsonl")));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The real tape: after the reload to turn 12 the first snapshot has every city at its real size.</summary>
    [Fact]
    public void SnowveReloadSnapshotHasTheRealCitySizes()
    {
        var tape = Path.Combine(SnowveReload, "tape.bin.gz");
        if (!File.Exists(tape)) { output.WriteLine("Snowve tape not on this PC: skipped"); return; }
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-snowve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(tape, Path.Combine(dir, "tape.bin.gz"));
            var (samples, _, outputFile) = TapeReplay.Run(dir);
            var lines = File.ReadAllLines(outputFile);
            int reload = Array.FindIndex(lines, l => l.Contains("\"game_reloaded\""));
            Assert.True(reload > 0, "no reload in the replay");
            var snap = lines.Skip(reload).First(l => l.Contains("\"kind\":\"snapshot\""));
            using var doc = JsonDocument.Parse(snap);
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(12, doc.RootElement.GetProperty("turn").GetInt32());
            var pops = data.GetProperty("cities").EnumerateArray().Select(c => c.GetProperty("population").GetInt32()).ToList();
            output.WriteLine($"{samples} samples; after the reload: {data.GetProperty("players").GetArrayLength()} players, {data.GetProperty("units").GetArrayLength()} units, cities {string.Join(",", pops)}");
            Assert.Equal(6, pops.Count);
            Assert.True(pops.Count(x => x > 1) >= 5, "cities came back at size 1");
            Assert.True(data.GetProperty("players").GetArrayLength() > 0);
            // and nothing was "founded" again between the reload and the next turn
            var next = Array.FindIndex(lines, reload, l => l.Contains("game_turn_changed"));
            Assert.DoesNotContain(lines[reload..(next < 0 ? lines.Length : next)], l => l.Contains("PlayerCityFounded"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TheLastLinesAreSentBeforeTheProgramEnds()
    {
        var root = Path.Combine(Path.GetTempPath(), "outside-recorder-finish-" + Guid.NewGuid().ToString("N"));
        var game = new GameEntry { Identity = "1a2b3c4d-5e6f7a8b", Folder = Path.Combine(root, "s1"), Context = GameIndex.NewContext(), LastSeen = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(game.Folder);
        new GameIndex(root).Save(game);
        var events = Path.Combine(game.Folder, "events.jsonl");
        try
        {
            var site = new FakeSite();
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/", StreamIntervalS = 60 };
            File.WriteAllText(events, "{\"kind\":\"lifecycle\",\"data\":{\"state\":\"recording\"}}\n");
            var s = new Streamer(settings, site);
            s.Start();
            for (int i = 0; i < 100 && site.Chunks.Count == 0; i++) await Task.Delay(50);
            Assert.Single(site.Chunks);
            File.AppendAllText(events, "{\"kind\":\"lifecycle\",\"data\":{\"state\":\"game_left\"}}\n{\"kind\":\"lifecycle\",\"data\":{\"state\":\"recorder_closed\"}}\n");
            s.Dispose();                                                    // the next pass would be a minute away
            Assert.Equal(File.ReadAllBytes(events), site.Joined());
        }
        finally { Directory.Delete(root, true); }
    }
}
