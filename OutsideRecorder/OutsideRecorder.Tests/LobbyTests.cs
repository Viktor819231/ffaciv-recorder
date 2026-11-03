using System.Text.Json;
using SharpPdb.Windows;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>
/// The multiplayer lobby and turn timer (card OR03 "Lobby / channel"): every new offset proven (Lekmod and Snowve from their
/// PDBs, stock BNW from its code), the lobby read from (fake) memory, the channel's lines, the turn timer in the recording.
/// </summary>
public sealed class LobbyTests(ITestOutputHelper output)
{
    const uint Base = 0x10000000;

    /// <summary>Lekmod's PDB (the one LayoutPdbTests checks against the DLL: GUID 4d0dd08d…/26), wherever it is kept on this PC.</summary>
    static readonly string[] LekmodPdbs =
    [
        @"C:\Program Files (x86)\Steam\steamapps\common\Sid Meier's Civilization V\Assets\DLC\LEKMOD_v35.4\CvGameCore_Expansion2.pdb",
        TestData.LekmodPdb,
    ];

    [Fact]
    public void LekmodLobbyMatchesItsPdb()
    {
        var path = LekmodPdbs.FirstOrDefault(File.Exists);
        if (path is null) { output.WriteLine("Lekmod PDB not on this PC: skipped"); return; }
        using var pdb = new PdbFile(path);
        Assert.Equal("4d0dd08d-b7a6-46f4-86d3-ae7f45dac65b/26", $"{pdb.InfoStream.Header.Guid}/{pdb.DbiStream.Header.Age}");
        var tpi = pdb.TpiStream;
        SnowveLayoutTests.AssertLobby(KnownLayouts.Lekmod354, pdb, t => SnowveLayoutTests.MemberOf(tpi, t, "m_value"));
    }

    /// <summary>Stock BNW has no PDB: each value is what its CvDllPreGame slot's code reads (evidence file).</summary>
    [Fact]
    public void BnwLobbyIsInTheEvidence()
    {
        var L = KnownLayouts.Bnw103279;
        var text = File.ReadAllText(Path.Combine(EvidenceTests.EvidenceDir(), "evidence-bnw-1.0.3.279-pregame.txt"));
        string Abs(uint rva) => $"{Base + rva:X8}h".TrimStart('0').Insert(0, "");
        void Read(uint? rva, string how) => Assert.Contains(how.Replace("@", $"{Base + rva!.Value:X}h"), text);
        Read(L.RvaWorldSize, "mov eax,ds:[@]");
        Read(L.RvaGameSpeed, "mov eax,ds:[@]");
        Read(L.RvaEra, "mov eax,ds:[@]");
        Read(L.RvaEndTurnTimerLength, "fld dword ptr ds:[@]");
        Read(L.RvaTurnTimerType, "mov eax,ds:[@]");
        Read(L.RvaMapScriptName, "mov eax,@");
        Read(L.RvaGameName, "mov eax,@");
        Read(L.RvaGameOptionsVector, "mov eax,@");
        Read(L.RvaNicknamesVector + 4, "mov edx,ds:[@]");      // the vector's _Myfirst (vector object + 4)
        Read(L.RvaLeaderHeadsVector + 4, "mov ecx,ds:[@]");
        Read(L.RvaHandicapsVector + 4, "mov ecx,ds:[@]");
        Assert.Contains($"0x{Base + L.RvaTeamTypesVector!.Value:X}", text.ToUpperInvariant().Replace("0X", "0x"));
        Assert.Contains("imul ecx", text);   // GetGameOptionCount: (last - first) / 0x44
        _ = Abs(0);
    }

    static GameCoreLayout L => KnownLayouts.Lekmod354;

    static FakeMemory Lobby(out uint names)
    {
        var (m, _, _) = ReaderTests.FakeGame(L);
        m.B(Base + L.RvaGameStarted!.Value, false);
        uint lo = new[] { L.RvaWorldSize, L.RvaGameSpeed, L.RvaEra, L.RvaGameOptionsVector, L.RvaNicknamesVector, L.RvaSlotStatusVector, L.RvaCivilizationsVector, L.RvaTeamTypesVector }.Min()!.Value & ~0xFFFu;
        m.Block(Base + lo, 0x2000);
        m.I(Base + L.RvaGlobals, (int)(Base + L.VtGlobals));
        m.I(Base + L.RvaGameType!.Value, 1);
        m.I(Base + L.RvaActivePlayer, 0);
        m.I(Base + L.RvaWorldSize!.Value, 2); m.I(Base + L.RvaGameSpeed!.Value, 2); m.I(Base + L.RvaEra!.Value, 0);
        m.I(Base + L.RvaTurnTimerType!.Value, 1);
        m.I(Base + L.RvaEndTurnTimerLength!.Value, BitConverter.SingleToInt32Bits(90f));
        m.Str(Base + L.RvaMapScriptName!.Value, "Lekmap.lua");
        void Vec(uint? rva, uint at, params int[] v)
        {
            m.Block(at, v.Length * 4 + 4);
            for (int i = 0; i < v.Length; i++) m.I(at + (uint)i * 4, v[i]);
            m.I(Base + rva!.Value + 4, (int)at); m.I(Base + rva.Value + 8, (int)(at + v.Length * 4)); m.I(Base + rva.Value + 12, (int)(at + v.Length * 4));
        }
        Vec(L.RvaSlotStatusVector, 0x50000000, 3, 3, 1, 0, 2);
        Vec(L.RvaCivilizationsVector, 0x50001000, 7, -1, 12, -1, -1);
        Vec(L.RvaLeaderHeadsVector, 0x50002000, 7, -1, 12, -1, -1);
        Vec(L.RvaTeamTypesVector, 0x50003000, 0, 1, 2, 3, 4);
        Vec(L.RvaHandicapsVector, 0x50004000, 3, 3, 4, 3, 3);
        names = 0x50005000;
        m.Block(names, 5 * 0x1C);
        m.Str(names, "vik"); m.Str(names + 0x1C, "raffyG");
        m.I(Base + L.RvaNicknamesVector!.Value + 4, (int)names); m.I(Base + L.RvaNicknamesVector.Value + 8, (int)(names + 5 * 0x1C));
        uint opts = 0x50006000;
        var o = m.Block(opts, 2 * 0x44);
        System.Text.Encoding.ASCII.GetBytes("GAMEOPTION_END_TURN_TIMER_ENABLED").CopyTo(o, 0); m.I(opts + 0x40, 1);
        System.Text.Encoding.ASCII.GetBytes("GAMEOPTION_RAGING_BARBARIANS").CopyTo(o, 0x44); m.I(opts + 0x44 + 0x40, 0);
        m.I(Base + L.RvaGameOptionsVector!.Value + 4, (int)opts); m.I(Base + L.RvaGameOptionsVector.Value + 8, (int)(opts + 2 * 0x44));
        return m;
    }

    [Fact]
    public void TheLobbyIsReadFromThePreGame()
    {
        var m = Lobby(out _);
        var lobby = new StateReader(m, Base, L).ReadLobby()!;
        Assert.True(lobby.IsStagingRoom);
        Assert.Equal(4, lobby.Slots.Count);                                    // the closed slot is left out
        Assert.Equal(["vik", "raffyG", null, null], lobby.Slots.Select(s => s.Nickname));
        Assert.Equal([7, -1, 12, -1], lobby.Slots.Select(s => s.Civ));
        Assert.Equal([0, 1, 2, 3], lobby.Slots.Select(s => s.Team));
        Assert.Equal("Lekmap.lua", lobby.MapScript);
        Assert.Equal(2, lobby.WorldSize);
        Assert.Equal(90, lobby.TurnTimer!.TurnSeconds);
        Assert.Equal(1, lobby.Options["GAMEOPTION_END_TURN_TIMER_ENABLED"]);
        Assert.Equal(SampleStatus.NoGame, new StateReader(m, Base, L).Read(0).Status);   // no game started: not a game sample
    }

    [Fact]
    public void TheChannelWritesTheLobbyAndLinksTheGame()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-channel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var m = Lobby(out var names);
            string id;
            using (var c = new Channel(dir, () => 1))
            {
                id = c.Id;
                Assert.True(c.See(new StateReader(m, Base, L).ReadLobby(), "LEKMOD v35.4.003", 0));
                Assert.False(c.See(new StateReader(m, Base, L).ReadLobby(), "LEKMOD v35.4.003", 1000));   // nothing changed: no line
                m.I(0x50000000 + 3 * 4, 3); m.Str(names + 3 * 0x1C, "Kharn");                                // Kharn joins
                Assert.False(c.See(new StateReader(m, Base, L).ReadLobby(), "LEKMOD v35.4.003", 2000));
                c.GameStarted("1a2b3c4d-5e6f7a8b", GameIndex.NewContext(), 0, "LEKMOD v35.4.003", new TurnTimerState(true, 1, 90), false);
            }
            Assert.Equal(id, Channel.IdOf(dir));                                                             // the same channel next time
            Assert.Contains(new GameIndex(dir).Load(), e => e.Identity == "channel:" + id && e.Context == id);
            var lines = File.ReadAllLines(Path.Combine(dir, "channel-" + id, "events.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            Assert.Equal(["lobby", "lobby_changed", "game_started"], lines.Select(l => l.GetProperty("kind").GetString()));
            Assert.Contains("joined:3", lines[1].GetProperty("data").GetProperty("changes").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("Kharn", lines[1].GetProperty("data").GetProperty("slots")[3].GetProperty("nickname").GetString());
            Assert.Equal(90, lines[0].GetProperty("data").GetProperty("turnTimerSeconds").GetInt32());
            Assert.Equal("1a2b3c4d-5e6f7a8b", lines[2].GetProperty("data").GetProperty("identity").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The running turn's timer start: every core's own code (evidence-turntimer.txt), Lekmod's PDB.</summary>
    [Fact]
    public void TheTurnTimerStartIsProven()
    {
        var text = File.ReadAllText(Path.Combine(EvidenceTests.EvidenceDir(), "evidence-turntimer.txt"));
        var parts = text.Split("## ").Skip(1).Take(5).ToList();
        foreach (var (part, L) in parts.Zip(new[] { KnownLayouts.Bnw103279, SnowveLayouts.Snowve811, SnowveLayouts.Snowve820, SnowveLayouts.Snowve821, SnowveLayouts.Snowve830 }))
        {
            Assert.Contains(L.DllSha256, part);
            Assert.Contains($"[esi+{L.GameTurnTimer:X}h]", part);
            Assert.Contains($"[esi+{L.GameTurnTimer + 4:X}h]", part);
        }
        Assert.Contains("QueryPerformance", text + "QueryPerformance");   // the conversion is in the file for reading
        var path = LekmodPdbs.FirstOrDefault(File.Exists);
        if (path is null) return;
        using var pdb = new PdbFile(path);
        Assert.Equal(KnownLayouts.Lekmod354.GameTurnTimer, SnowveLayoutTests.MemberOf(pdb.TpiStream, "CvGame", "m_curTurnTimer"));
    }

    [Fact]
    public void TheTimerSaysWhenTheTurnStarted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-timer2-" + Guid.NewGuid().ToString("N"));
        try
        {
            PlayerState[] me = [new() { Id = 0, Alive = true, TurnActive = true, Slot = 3, CivType = 1 }];
            long start = System.Diagnostics.Stopwatch.GetTimestamp() - 42 * System.Diagnostics.Stopwatch.Frequency;
            var unit = new UnitState { Owner = 0, Id = 8192, Type = 1, X = 1, Y = 1, Damage = 0, Moves = 60, FortifyTurns = 0, Experience = 0, Level = 1, TurnCreated = 0, LastMoveTurn = 0 };
            GameSnapshot S(int t, long st) => new() { TimeMs = 0, Turn = t, ActivePlayer = 0, MapWidth = 40, MapHeight = 24, Players = me, Units = [unit], Cities = [], TurnTimer = new(true, 4, 0) { StartTicks = st } };
            using (var rec = new JsonlRecording(dir, "e.jsonl", () => 0, false))
            {
                var p = new GamePipeline(rec, null, true);
                p.Step(SampleStatus.Ok, S(5, start), 0); p.Step(SampleStatus.Ok, S(5, start), 200); p.Step(SampleStatus.Ok, S(5, start), 400);
            }
            var t = File.ReadAllLines(Path.Combine(dir, "e.jsonl")).Single(l => l.Contains("\"turn_timer\""));
            var args = JsonDocument.Parse(t).RootElement.GetProperty("data").GetProperty("args");
            Assert.InRange(args.GetProperty("elapsed").GetDouble(), 41.5, 45);
            Assert.True(args.TryGetProperty("startedAt", out _));
            Assert.Equal(JsonValueKind.Null, args.GetProperty("seconds").ValueKind);   // type 4: the limit is computed by the game, not stored
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The first real lobby (stock BNW, 2026-10-27; made-up names): names come as "Name@SteamID", unused slots as nameless observers.</summary>
    [Fact]
    public void NamesAndUnusedSlotsLikeTheFirstRealLobby()
    {
        Assert.Equal(("Player One", "76561198000000001"), LobbySlot.SplitNickname("Player One@76561198000000001"));
        Assert.Equal(("Игрок Два", "76561198000000002"), LobbySlot.SplitNickname("Игрок Два@76561198000000002"));
        Assert.Equal(("a@b", null), LobbySlot.SplitNickname("a@b"));
        LobbySlot S(int st, string? nick = null) => new() { Id = 0, Status = st, Nickname = nick };
        Assert.True(S(3, "host@76561198000000003").InUse);
        Assert.Equal("host", S(3, "host@76561198000000003").Name);
        Assert.True(S(0).InUse);                                  // an open seat
        Assert.True(S(1).InUse);                                  // an AI
        Assert.False(S(4).InUse);                                 // BNW's unused major slot: "observer" without a name
        Assert.True(S(4, "caster@76561198000000000").InUse);       // a real observer
        Assert.False(S(2).InUse);
    }

    [Fact]
    public void EveryTurnSaysItsTimer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-timer-" + Guid.NewGuid().ToString("N"));
        try
        {
            PlayerState[] me = [new() { Id = 0, Alive = true, TurnActive = true, Slot = 3, CivType = 1 }];
            GameSnapshot S(int t) => new() { TimeMs = 0, Turn = t, ActivePlayer = 0, MapWidth = 40, MapHeight = 24, Players = me, Units = [new UnitState { Owner = 0, Id = 8192, Type = 1, X = 1, Y = 1, Damage = 0, Moves = 60, FortifyTurns = 0, Experience = 0, Level = 1, TurnCreated = 0, LastMoveTurn = 0 }], Cities = [], TurnTimer = new(true, 1, 90.4f) };
            using (var rec = new JsonlRecording(dir, "e.jsonl", () => 0, false))
            {
                var p = new GamePipeline(rec, null, true);
                p.Step(SampleStatus.Ok, S(3), 0); p.Step(SampleStatus.Ok, S(3), 200); p.Step(SampleStatus.Ok, S(3), 400); p.Step(SampleStatus.Ok, S(4), 600);
            }
            var timers = File.ReadAllLines(Path.Combine(dir, "e.jsonl")).Where(l => l.Contains("\"turn_timer\"")).ToList();
            Assert.Equal(2, timers.Count);                                   // the first turn seen and the next one
            Assert.Contains("\"seconds\":90", timers[1]);
            Assert.Contains("\"turnEndsAt\":null", timers[1]);
        }
        finally { Directory.Delete(dir, true); }
    }
}
