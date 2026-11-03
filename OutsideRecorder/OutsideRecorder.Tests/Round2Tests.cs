using System.Diagnostics;
using System.Text.Json;

namespace OutsideRecorderTests;

/// <summary>
/// Every layout value must be backed by the DLL's own machine code: layouts/evidence-*.txt (made by tools/BinaryVerify/verify.sh).
/// </summary>
public sealed class EvidenceTests
{
    internal static string EvidenceDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "layouts")) && File.Exists(Path.Combine(d.FullName, "layouts", "evidence-bnw-1.0.3.279.txt")))
                return Path.Combine(d.FullName, "layouts");
        throw new DirectoryNotFoundException("OutsideRecorder/layouts");
    }

    public static TheoryData<string> Layouts => new(KnownLayouts.All.Select(l => l.Id));

    [Theory, MemberData(nameof(Layouts))]
    public void EveryOffsetIsInTheEvidence(string id)
    {
        var L = KnownLayouts.All.Single(l => l.Id == id);
        var text = File.ReadAllText(Path.Combine(EvidenceDir(), $"evidence-{id}.txt"));
        var sec = text.Split("\n## ").ToDictionary(s => s.Split('\n')[0].Trim(), s => s);
        string S(string start) => sec.Single(kv => kv.Key.StartsWith(start)).Value;
        string Line(string section, string method) =>
            S(section).Split('\n').Single(l => l.TrimStart().StartsWith(method + ":"));
        void Has(string hay, string needle) => Assert.True(hay.Contains(needle), $"{id}: '{needle}' not in: {hay.Trim()}");
        string Hex(int v) => $"0x{v:X}";

        Has(text, "# sha256 " + L.DllSha256);
        var vt = S("vtables");
        Has(vt, $".?AVCvGlobals@@ vtable RVA={Hex((int)L.VtGlobals)}");
        Has(vt, $".?AVCvPlayerAI@@ vtable RVA={Hex((int)L.VtPlayerAI)}");
        Has(vt, $".?AVCvCityAI@@ vtable RVA={Hex((int)L.VtCityAI)}");
        Has(vt, $".?AVCvMap@@ vtable RVA={Hex((int)L.VtMap)}");
        Has(vt, $"FAutoArchiveClassContainer@VCvUnit@@@@ vtable RVA={Hex((int)L.VtUnitArchive)}");
        Has(S("gGlobals"), $"object RVA {Hex((int)L.RvaGlobals)}");
        Assert.StartsWith($"  size {Hex(L.PlayerSize)} global RVA {Hex((int)L.RvaPlayers)}:", S("player array").Split('\n')[1]);

        Has(Line("unit (", "GetX"), $"+{Hex(L.UnitX)}(");
        Has(Line("unit (", "GetY"), $"+{Hex(L.UnitY)}(");
        Has(Line("unit (", "GetID"), $"+{Hex(L.UnitId)}(");
        Has(Line("unit (", "GetOwner"), $"+{Hex(L.UnitOwner)}(");
        Has(Line("unit (", "GetDamage"), $"+{Hex(L.UnitDamage)}(");
        Has(Line("unit (", "GetMoves"), $"+{Hex(L.UnitMoves)}(");
        Has(Line("unit (", "GetUnitType"), $"+{Hex(L.UnitType)}(");
        Has(Line("unit (", "GetFortifyTurns"), $"+{Hex(L.UnitFortifyTurns)}(");
        Has(Line("unit (", "GetExperience"), $"+{Hex(L.UnitExperience)}(");
        Has(Line("unit (", "GetLevel"), $"+{Hex(L.UnitLevel)}(");
        Has(Line("unit (", "IsEmbarked"), $"+{Hex(L.UnitEmbarked!.Value)}(UInt8)");
        Has(Line("unit (", "GetActivityType"), $"+{Hex(L.UnitActivity!.Value)}(");
        Has(Line("unit (", "GetGameTurnCreated"), $"+{Hex(L.UnitTurnCreated)}(");
        Has(Line("unit (", "GetLastMoveTurn"), $"+{Hex(L.UnitLastMoveTurn)}(");

        Has(Line("player (", "GetUnitByID"), $"this+={Hex(L.PlayerUnits)}");
        Has(Line("player (", "GetCityByID"), $"this+={Hex(L.PlayerCities)}");
        Has(Line("player (", "IsAlive"), $"+{Hex(L.PlayerAlive)}(UInt8)");
        Has(Line("player (", "IsTurnActive"), $"+{Hex(L.PlayerTurnActive)}(UInt8)");
        Has(Line("player (", "GetID"), $"+{Hex(L.PlayerId)}(");
        Has(Line("player (", "GetCivilizationType"), $"abs:{Hex((int)L.RvaCivilizationsVector!.Value + 4)}");
        Has(Line("player (", "IsHuman"), $"abs:{Hex((int)L.RvaSlotStatusVector!.Value + 4)}");
        Has(Line("player (", "IsHuman"), $"abs:{Hex((int)L.RvaGameType!.Value)}");

        Has(Line("city (", "GetX"), $"+{Hex(L.CityX)}(");
        Has(Line("city (", "GetY"), $"+{Hex(L.CityY)}(");
        Has(Line("city (", "GetID"), $"+{Hex(L.CityId)}(");
        Has(Line("city (", "GetOwner"), $"+{Hex(L.CityOwner)}(");
        Has(Line("city (", "GetPopulation"), $"+{Hex(L.CityPopulation)}(");
        Has(Line("city (", "GetDamage"), $"+{Hex(L.CityDamage)}(");
        Has(Line("city (", "IsPuppet"), $"+{Hex(L.CityPuppet)}(UInt8)");
        Has(Line("city (", "IsOccupied"), $"+{Hex(L.CityOccupied)}(UInt8)");
        Has(Line("city (", "GetOriginalOwner"), $"+{Hex(L.CityOriginalOwner)}(");
        Has(Line("city (", "GetGameTurnFounded"), $"+{Hex(L.CityTurnFounded)}(");
        Has(Line("city (", "GetName"), $"this+={Hex(L.CityName!.Value)}");

        Has(Line("game +", "GetGameTurn"), $"abs:{Hex((int)L.RvaGlobals + L.GlobalsGame)}");
        Has(Line("game +", "GetGameTurn"), $"abs:{Hex((int)L.RvaGameTurn)}");
        Has(Line("game +", "GetActivePlayer"), $"abs:{Hex((int)L.RvaActivePlayer)}");
        Has(Line("game +", "GetGridSize"), $"abs:{Hex((int)L.RvaGlobals + L.GlobalsMap)},EAX+{Hex(L.MapHeight)}(UInt32),EAX+{Hex(L.MapWidth)}(UInt32)");
        Has(S("gameStarted"), $"mov al,ds:[{0x10000000 + L.RvaGameStarted!.Value:X}h]");
        Has(S("game seeds"), $"mov eax,ds:[{0x10000000 + L.RvaMapSeed!.Value:X}h]");
        Has(S("game seeds"), $"mov eax,ds:[{0x10000000 + L.RvaSyncSeed!.Value:X}h]");
        var lists = S("unit / city lists");
        Has(lists, $"[ecx+{L.ListArray:X}h]"); Has(lists, $"[ecx+0{L.ListLastIndex:X}h]"); Has(lists, "[edx+eax*8+4]");
        Has(lists, $"[eax+{L.UnitId:X}h]"); Has(lists, $"[eax+{L.CityId:X}h]");
    }
}

/// <summary>Tape mode, replay, reloads, slot changes, autosave turns.</summary>
public sealed class Round2Tests
{
    const uint Base = 0x10000000;

    /// <summary>A running game has at least one unit or city (GamePipeline.IsLoaded).</summary>
    static readonly UnitState Settler = new UnitState { Owner = 0, Id = 8192, Type = 1, X = 1, Y = 1, Damage = 0, Moves = 60, FortifyTurns = 0, Experience = 0, Level = 1, TurnCreated = 0, LastMoveTurn = 0 };

    static GameSnapshot S(int turn, IReadOnlyList<PlayerState>? players = null, IReadOnlyList<UnitState>? units = null) => new()
    {
        TimeMs = 0, Turn = turn, ActivePlayer = 0, MapWidth = 80, MapHeight = 52, Players = players ?? [], Units = units ?? (players is { Count: > 0 } ? [Settler] : []), Cities = [],
    };

    [Fact]
    public void ReplayOfTapeGivesTheSameEvents()
    {
        var L = KnownLayouts.Bnw103279;
        var (m, unit, city) = ReaderTests.FakeGame(L);
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-tape-" + Guid.NewGuid().ToString("N"));
        try
        {
            long now = 0;
            using (var rec = new JsonlRecording(dir, "events.jsonl", () => now, wallClock: false))
            using (var tape = new TapeWriter(dir, new TapeHeader { Layout = L.Id, CoreSha256 = L.DllSha256, ModuleBase = Base, SampleMs = 200, Started = "t", Reads = 2 }))
            {
                var taping = new TapingMemory(m) { Tape = tape };
                var reader = new StateReader(taping, Base, L) { ReadCityMicro = false }; // version-2 tapes predate city micro
                var pipe = new GamePipeline(rec, reader.UnitTypeKey, true);
                for (int i = 0; i < 6; i++)
                {
                    now = i * 200_000L;
                    if (i == 2) m.I(unit + (uint)L.UnitX, 42);                // a move
                    if (i == 3) m.I(city + (uint)L.CityDamage, 40);            // the city is hit
                    if (i == 4) m.I(Base + L.RvaGameTurn, 43);                 // next turn
                    taping.Begin(now);
                    var (st, snap, _) = reader.Read(now / 1000);
                    pipe.Step(st, snap, now / 1000);
                    taping.End(0.5);
                }
                Assert.True(tape.RawBytes > 0);
            }
            var (samples, _, output) = TapeReplay.Run(dir);
            Assert.Equal(6, samples);
            var live = File.ReadAllLines(Path.Combine(dir, "events.jsonl"));
            var again = File.ReadAllLines(output);
            Assert.Equal(live, again);
            Assert.Contains(live, l => l.Contains("\"UnitSetXY\""));
            Assert.Contains(live, l => l.Contains("\"city_damage\""));
            Assert.Contains(live, l => l.Contains("\"game_turn_changed\""));
            // only changes are stored: samples without a change are tiny
            var parts = TapeReader.Read(Path.Combine(dir, "tape.bin.gz")).Where(x => x.Sample is not null).Select(x => x.Sample!.Changes.Count).ToList();
            Assert.True(parts[1] == 0, $"unchanged sample stored {parts[1]} reads");
            Assert.True(parts[2] >= 1 && parts[2] <= 2, $"a move stored {parts[2]} reads");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TapeSurvivesACutOffEnd()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-cut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var t = new TapeWriter(dir, new TapeHeader { Layout = "x", CoreSha256 = "y", ModuleBase = 1, SampleMs = 200, Started = "t" }))
                for (int i = 0; i < 50; i++) { t.Begin(i); t.Read(0x1000 + (uint)i, BitConverter.GetBytes(i), true); t.End(1); }
            var path = Path.Combine(dir, "tape.bin.gz");
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..(bytes.Length - 30)]); // power cut: the end is missing
            int n = TapeReader.Read(path).Count(x => x.Sample is not null);
            Assert.InRange(n, 1, 50);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReloadAndMenuGapAreMarked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-pipe-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] lines;
            using (var rec = new JsonlRecording(dir, "e.jsonl", () => 0, false))
            {
                var p = new GamePipeline(rec, null, true);
                PlayerState[] me = [new() { Id = 0, Alive = true, TurnActive = true, Slot = 3, CivType = 1 }];
                p.Step(SampleStatus.Ok, S(10, me), 0);
                p.Step(SampleStatus.Ok, S(10, me), 200);                   // the first baseline: two samples that agree
                p.Step(SampleStatus.Ok, S(11, me), 1000);
                p.Step(SampleStatus.Ok, S(9, me), 2000);                   // an older save loaded
                p.Step(SampleStatus.NoGame, null, 3000);                    // back to the menu ...
                p.Step(SampleStatus.NoGame, null, 9000);
                p.Step(SampleStatus.Ok, S(9), 11800);                      // loading: map there, players not yet
                p.Step(SampleStatus.Ok, S(9, me), 12000);                  // ... and the same game again
                p.Step(SampleStatus.Ok, S(9, me), 12200);
            }
            lines = File.ReadAllLines(Path.Combine(dir, "e.jsonl"));
            Assert.Contains(lines, l => l.Contains("\"game_reloaded\"") && l.Contains("\"fromTurn\":11"));
            Assert.Contains(lines, l => l.Contains("\"game_left\""));
            Assert.Contains(lines, l => l.Contains("\"game_resumed\""));
            Assert.Equal(4, lines.Count(l => l.Contains("\"kind\":\"snapshot\""))); // turn 10, 11, after reload, after resume
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IdentityIgnoresDropsButNotOtherGames()
    {
        PlayerState P(int id, int civ, int slot) => new() { Id = id, Alive = true, TurnActive = false, Slot = slot, CivType = civ };
        var a = GamePipeline.IdentityOf(S(5, [P(0, 7, 3), P(1, 12, 3)]));
        var dropped = GamePipeline.IdentityOf(S(6, [P(0, 7, 3), P(1, 12, 1)]));   // player 1 dropped, AI took over
        var other = GamePipeline.IdentityOf(S(5, [P(0, 7, 3), P(1, 13, 3)]));
        Assert.Equal(a, dropped);
        Assert.NotEqual(a, other);
    }

    [Fact]
    public void DropAndRejoinAreRecorded()
    {
        PlayerState P(int slot) => new() { Id = 1, Alive = true, TurnActive = false, Slot = slot, CivType = 3 };
        var d = new Differ();
        d.Next(S(5, [P(3)]));
        var drop = Assert.Single(d.Next(S(5, [P(1)])));
        Assert.Equal("player_slot_changed", drop.Method);
        Assert.Equal("computer", drop.Args["toName"]);
        Assert.Equal("human", Assert.Single(d.Next(S(5, [P(3)]))).Args["toName"]);
    }

    [Theory]
    [InlineData("AutoSave_0013 BC-3220.Civ5Save", 13)]
    [InlineData("AutoSave_Post_0012 BC-3280.Civ5Save", 12)]
    [InlineData("My game.Civ5Save", null)]
    public void AutosaveTurnFromName(string name, int? turn) => Assert.Equal(turn, RecorderSession.AutosaveTurn(name));

    [Fact]
    public void BothLayoutsKnownAndTimed()
    {
        Assert.True(KnownLayouts.Bnw103279.AutosaveAtTurnStart);
        Assert.False(KnownLayouts.Lekmod354.AutosaveAtTurnStart);
        Assert.Same(KnownLayouts.Bnw103279, KnownLayouts.BySha256("c5483b5857a6fb7ef107af93c5d34282eb713472e2a84d0eb9d3fdc104e155a5"));
        var stock = @"C:\Program Files (x86)\Steam\steamapps\common\Sid Meier's Civilization V\Assets\DLC\Expansion2\CvGameCore_Expansion2.dll";
        if (File.Exists(stock)) Assert.Equal(KnownLayouts.Bnw103279.DllSha256, GameFinder.Sha256Of(stock));
    }

    [Fact]
    public void MemoryRegionsReadableWithReadOnlyRights()
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "cmd.exe");
        if (!File.Exists(cmd)) return;
        using var p = Process.Start(new ProcessStartInfo(cmd, "/c ping -n 6 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Thread.Sleep(300);
            using var mem = new ProcessMemory(p.Id);
            var regions = mem.Regions().ToList();
            Assert.Contains(regions, r => r.Type == 0x20000);   // private memory (the heap) is listed
            var dumpPath = Path.Combine(Path.GetTempPath(), $"outside-recorder-{Guid.NewGuid():N}.ordump");
            try
            {
                long n = MemoryDump.Write(p.Id, dumpPath, null, new { test = true });
                Assert.True(n > 0 && new FileInfo(dumpPath).Length > n);
            }
            finally { File.Delete(dumpPath); }
        }
        finally { try { p.Kill(); } catch { } }
    }
}
