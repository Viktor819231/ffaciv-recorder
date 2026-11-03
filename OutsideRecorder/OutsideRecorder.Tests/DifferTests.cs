using System.Diagnostics;

namespace OutsideRecorderTests;

public sealed class DifferTests
{
    static UnitState U(int owner, int id, int x, int y, int moves = 120, int fort = 0, int dmg = 0) => new()
    {
        Owner = owner, Id = id, Type = 1, X = x, Y = y, Damage = dmg, Moves = moves, FortifyTurns = fort,
        Experience = 0, Level = 1, TurnCreated = 0, LastMoveTurn = 0,
    };
    static CityState C(int owner, int id, int x, int y, int pop = 1) => new()
    {
        Owner = owner, Id = id, X = x, Y = y, Population = pop, Damage = 0, TurnFounded = 1, OriginalOwner = owner, Puppet = false, Occupied = false, Name = "A",
    };
    static GameSnapshot S(int turn, UnitState[] units, CityState[]? cities = null) => new()
    {
        TimeMs = 0, Turn = turn, ActivePlayer = 0, MapWidth = 80, MapHeight = 52, Players = [], Units = units, Cities = cities ?? [],
    };

    [Fact]
    public void FirstSnapshotIsSilentThenMovesAndDamageShow()
    {
        var d = new Differ();
        Assert.Empty(d.Next(S(1, [U(0, 8192, 5, 5)])));
        var ev = d.Next(S(1, [U(0, 8192, 7, 5, moves: 0, dmg: 20)]));
        var mv = Assert.Single(ev, e => e.Method == "UnitSetXY");
        Assert.Equal((7, 5, 5, 5), ((int)mv.Args["x"]!, (int)mv.Args["y"]!, (int)mv.Args["fromX"]!, (int)mv.Args["fromY"]!));
        Assert.Single(ev, e => e.Method == "unit_damage");
    }

    [Fact]
    public void ExchangeOfTwoUnitsWithMovesIsASwap()
    {
        var d = new Differ();
        d.Next(S(1, [U(0, 1, 5, 5), U(0, 2, 6, 5)]));
        var ev = d.Next(S(1, [U(0, 1, 6, 5, moves: 60), U(0, 2, 5, 5, moves: 60)]));
        Assert.Equal(2, ev.Count(e => e.Method == "UnitSetXY" && e.Args.ContainsKey("swapWithUnitId")));
    }

    [Fact]
    public void FortifiedNowNeverSwapsButWokenUpDoes()
    {
        var d = new Differ();
        d.Next(S(1, [U(0, 1, 5, 5), U(0, 2, 7, 5, fort: 1)]));           // 2 tiles apart, unit 2 fortified at the last sample
        var ev = d.Next(S(1, [U(0, 1, 7, 5), U(0, 2, 5, 5)]));           // it woke up and swapped in between (Lekmod T9)
        Assert.Equal(2, ev.Count(e => e.Method == "UnitSetXY" && e.Args.ContainsKey("swapWithUnitId")));
        Assert.Contains(ev, e => e.Method == "unit_activity" && (bool)e.Args["previousFortified"]! && !(bool)e.Args["fortified"]!);
        Assert.False(Differ.CanSwap(U(0, 1, 5, 5), U(0, 2, 7, 5), U(0, 1, 7, 5), U(0, 2, 5, 5, fort: 1)));
    }

    [Fact]
    public void FortifyAndSleepAreEvents()
    {
        var d = new Differ();
        d.Next(S(1, [U(0, 1, 5, 5)]));
        var ev = d.Next(S(1, [U(0, 1, 5, 5, fort: 1)]));
        Assert.Single(ev, e => e.Method == "unit_activity" && (bool)e.Args["fortified"]!);
        Assert.DoesNotContain(d.Next(S(2, [U(0, 1, 5, 5, fort: 2)])), e => e.Method == "unit_activity"); // counting up is no change
    }

    [Fact]
    public void UnitWithoutMovesNeverSwaps()
    {
        Assert.False(Differ.CanSwap(U(0, 1, 0, 0, moves: 0), U(0, 2, 0, 0), U(0, 1, 1, 0), U(0, 2, 0, 0)));
        Assert.True(Differ.CanSwap(U(0, 1, 0, 0), U(0, 2, 0, 0), U(0, 1, 1, 0), U(0, 2, 0, 0)));
    }

    [Fact]
    public void RemovalNeedsTwoMissingSamples()
    {
        var d = new Differ();
        d.Next(S(1, [U(0, 1, 5, 5)]));
        Assert.Empty(d.Next(S(1, [])));                     // one bad read: nothing
        Assert.Empty(d.Next(S(1, [U(0, 1, 5, 5)])));        // it is back: nothing
        d.Next(S(1, []));
        Assert.Single(d.Next(S(1, [])), e => e.Method == "UnitRemoved");
    }

    [Fact]
    public void CitiesFoundCaptureAndGrow()
    {
        var d = new Differ();
        d.Next(S(1, [], [C(0, 8192, 10, 10)]));
        var found = d.Next(S(2, [], [C(0, 8192, 10, 10, pop: 2), C(1, 8192, 30, 30)]));
        Assert.Contains(found, e => e.Method == "game_turn_changed");
        Assert.Contains(found, e => e.Method == "SetPopulation");
        Assert.Contains(found, e => e.Method == "PlayerCityFounded" && (int)e.Args["owner"]! == 1);
        var cap = d.Next(S(3, [], [C(1, 16385, 10, 10), C(1, 8192, 30, 30)]));
        Assert.Single(cap, e => e.Method == "CityCaptured" && (int)e.Args["from"]! == 0);
        Assert.DoesNotContain(cap, e => e.Method == "CityRemoved");
    }

    [Fact]
    public void JsonlLinesHaveTheRecorderShape()
    {
        var root = Path.Combine(Path.GetTempPath(), "outside-recorder-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file;
            using (var rec = new JsonlRecording(root, 1, Stopwatch.StartNew()))
            {
                rec.Write(new GameEvent("UnitSetXY", 3, new Dictionary<string, object?> { ["owner"] = 0, ["unitId"] = 8192, ["x"] = 1, ["y"] = 2 }));
                file = Path.Combine(rec.Folder, "events.jsonl");
            }
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllLines(file).Single());
            var r = doc.RootElement;
            Assert.Equal(2, r.GetProperty("schema").GetInt32());
            Assert.Equal("result", r.GetProperty("kind").GetString());
            Assert.Equal(3, r.GetProperty("turn").GetInt32());
            Assert.Equal("UnitSetXY", r.GetProperty("data").GetProperty("method").GetString());
            Assert.Equal("memory_sample", r.GetProperty("data").GetProperty("source").GetString());
            Assert.Equal(8192, r.GetProperty("data").GetProperty("args").GetProperty("unitId").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }
}
