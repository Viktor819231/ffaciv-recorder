using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OutsideRecorderTests;

/// <summary>The 2026-11-09 tidy of the capture engine: the reader's checks, the recording's tail, the lobby channel, cut-off
/// tapes, the workload clock and the save copies while no recording is open.</summary>
public sealed class CaptureTidyTests
{
    const uint Base = 0x10000000;
    static readonly GameCoreLayout L = KnownLayouts.Lekmod354;

    static string NewRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"recorder-tidy-{name}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void AnUnreadableMapIsNoGameAndOnlyAReadableWrongOneIsAMismatch()
    {
        var (m, _, _) = ReaderTests.FakeGame(L);
        m.I(Base + L.RvaGlobals + (uint)L.GlobalsMap, 0x7E000000);   // the map pointer leads nowhere: Civ is freeing the map
        var r = new StateReader(m, Base, L);
        Assert.Equal(SampleStatus.NoGame, r.Read(0).Status);          // was a mismatch: the Civ process was refused until restart
        Assert.Equal(SampleStatus.NoGame, r.ReadHeader().Status);
        m.I(Base + L.RvaGlobals + (uint)L.GlobalsMap, 0x21000000);
        m.I(0x21000000, 0x1234);                                       // a readable map object with another vtable
        Assert.Equal(SampleStatus.LayoutMismatch, r.Read(0).Status);
        Assert.Equal(SampleStatus.NoGame, r.ReadHeader().Status);      // the header look runs while memory is in flux
    }

    [Fact]
    public void AUnitNameThatCouldNotBeReadIsAskedAgain()
    {
        var (m, _, _) = ReaderTests.FakeGame(L);
        uint entries = 0x51000000, table = 0x51001000, info = 0x51002000;
        m.I(Base + L.RvaGlobals + (uint)L.GlobalsUnitInfos!.Value, (int)entries);
        var live = new StateReader(m, Base, L);
        var replayOfOldTape = new StateReader(m, Base, L) { RetryUnreadNames = false };
        Assert.Null(live.UnitTypeKey(3));                              // the unit table is not readable yet
        Assert.Null(replayOfOldTape.UnitTypeKey(3));
        m.Block(entries, 12); m.I(entries + 4, (int)table); m.I(entries + 8, (int)(table + 16));
        m.Block(table, 16); m.I(table + 12, (int)info);
        m.Block(info, 0x100); m.Str(info + (uint)L.InfoType!.Value, "UNIT_WARRIOR");
        Assert.Equal("UNIT_WARRIOR", live.UnitTypeKey(3));             // it used to stay unknown for the rest of the game
        Assert.Null(replayOfOldTape.UnitTypeKey(3));                   // an old tape is read the way it was made
    }

    [Fact]
    public void ANewTurnsTimerStartIsReadAtItsFirstLook()
    {
        var (m, _, _) = ReaderTests.FakeGame(L);
        m.I(Base + L.RvaTurnTimerType!.Value, 1);
        m.I(Base + L.RvaEndTurnTimerLength!.Value, BitConverter.SingleToInt32Bits(90f));
        uint opts = 0x50006000;
        var o = m.Block(opts, 0x44);
        Encoding.ASCII.GetBytes("GAMEOPTION_END_TURN_TIMER_ENABLED").CopyTo(o, 0); m.I(opts + 0x40, 1);
        m.I(Base + L.RvaGameOptionsVector!.Value + 4, (int)opts); m.I(Base + L.RvaGameOptionsVector.Value + 8, (int)(opts + 0x44));
        uint game = 0x20000000;
        void Start(int ticks) { m.I(game + (uint)L.GameTurnTimer!.Value, ticks); m.I(game + (uint)L.GameTurnTimer.Value + 4, 0); }
        Start(1000);
        var live = new StateReader(m, Base, L);
        var replayOfOldTape = new StateReader(m, Base, L) { TimerStartOnNewTurn = false };
        Assert.Equal(1000, live.Read(0).Snapshot!.TurnTimer!.StartTicks);
        Assert.Equal(1000, replayOfOldTape.Read(0).Snapshot!.TurnTimer!.StartTicks);
        m.I(Base + L.RvaGameTurn, 43); Start(2000);                    // the next turn, 100 ms later
        Assert.Equal(2000, live.Read(100).Snapshot!.TurnTimer!.StartTicks);
        Assert.Null(replayOfOldTape.Read(100).Snapshot!.TurnTimer!.StartTicks);   // the old way: the start came up to 200 ms later
    }

    [Fact]
    public void TheRecordingsTailIsFoundBehindALongLastLine()
    {
        var dir = NewRoot("tail");
        try
        {
            var path = Path.Combine(dir, "events.jsonl");
            using (var rec = new JsonlRecording(dir, "events.jsonl", () => 0, wallClock: false))
            {
                rec.Write("lifecycle", 7, new { state = "recording" });
                rec.Write("snapshot", 9, new { tiles = new string('x', 300_000) });   // longer than both old tails (64 / 256 KB)
                rec.Write("lobby", null, new { channel = "c" });                       // a line without a turn
            }
            Assert.Equal(3, JsonlRecording.LastSequence(path));
            Assert.Equal(9, JsonlRecording.LastTurn(path));
            using (var rec = new JsonlRecording(dir, "events.jsonl", () => 0, wallClock: false))
                rec.Write("snapshot", 10, new { tiles = new string('y', 100_000) });
            Assert.Equal(4, JsonlRecording.LastSequence(path));                      // the old 64 KB tail started again at 1
            Assert.Equal(10, JsonlRecording.LastTurn(path));
            // a recorder stopped mid-write: the cut line does not count
            File.AppendAllText(path, "{\"schema\":2,\"kind\":\"result\",\"sequence\":5,\"timeMs\":0,\"timeUs\":0,\"wallClock\":null,\"turn\":11,\"turnSource\":\"memory_sample\",\"data\":{\"x\":");
            Assert.Equal(4, JsonlRecording.LastSequence(path));
            Assert.Equal(10, JsonlRecording.LastTurn(path));
            Assert.Equal(0, JsonlRecording.LastSequence(Path.Combine(dir, "missing.jsonl")));
            Assert.Null(JsonlRecording.LastTurn(Path.Combine(dir, "missing.jsonl")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheSameLobbySeenAgainAfterAnUnreadableLookIsTheLobbyAgain()
    {
        var dir = NewRoot("channel");
        try
        {
            string id;
            var lobby = new LobbySnapshot { GameStarted = false, GameType = 1, Slots = [new LobbySlot { Id = 0, Status = 3, Nickname = "vik" }] };
            using (var c = new Channel(dir, () => 1))
            {
                id = c.Id;
                Assert.True(c.See(lobby, "LEKMOD", 0));
                Assert.False(c.See(null, "LEKMOD", 1000));        // one look that could not be read
                Assert.Null(c.Lobby);
                c.See(lobby, "LEKMOD", 2000);
                Assert.NotNull(c.Lobby);                          // it stayed empty: the same text counted as nothing new
                c.See(lobby with { Slots = [.. lobby.Slots, new LobbySlot { Id = 1, Status = 3, Nickname = "raffyG" }] }, "LEKMOD", 3000);
                c.See(new LobbySnapshot { GameStarted = false, GameType = 1 }, "LEKMOD", 4000);   // everyone left
            }
            var kinds = File.ReadAllLines(Path.Combine(dir, "channel-" + id, "events.jsonl")).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("kind").GetString());
            Assert.Equal(["lobby", "lobby", "lobby_changed", "lobby_closed"], kinds);
        }
        finally { Directory.Delete(dir, true); }
    }

    static byte[] ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        f.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void ATapeContinuedAfterACutOffPartIsReadToTheEnd()
    {
        var killed = NewRoot("tape-killed");
        var continued = NewRoot("tape-continued");
        try
        {
            var header = new TapeHeader { Layout = "x", CoreSha256 = "y", ModuleBase = 1, SampleMs = 250, Started = "2026-11-09", Reads = TapeReplay.CurrentReads };
            var first = new TapeWriter(killed, header);
            for (int i = 0; i < 3; i++) { first.Begin(i); first.Read(0x1000, BitConverter.GetBytes(i), true); first.End(0.5); }
            // the recorder is killed here: what is on disk has no gzip end
            var cut = ReadShared(Path.Combine(killed, "tape.bin.gz"));
            first.Dispose();
            var tape = Path.Combine(continued, "tape.bin.gz");
            File.WriteAllBytes(tape, cut);
            Assert.Equal([0L, 1, 2], TapeReader.Read(tape).Where(x => x.Sample is not null).Select(x => x.Sample!.TimeUs));   // a cut end: read up to it
            using (var again = new TapeWriter(continued, header with { ModuleBase = 2 }))   // the same game rejoined: a new part
                for (int i = 10; i < 13; i++) { again.Begin(i); again.Read(0x1000, BitConverter.GetBytes(i), true); again.End(0.5); }
            var items = TapeReader.Read(tape).ToList();
            Assert.Equal([1u, 2u], items.Where(x => x.Header is not null).Select(x => x.Header!.ModuleBase));
            Assert.Equal([0L, 1, 2, 10, 11, 12], items.Where(x => x.Sample is not null).Select(x => x.Sample!.TimeUs));   // it used to stop at 2
            Assert.Equal(12, BitConverter.ToInt32(items[^1].Sample!.Changes.Single().Data!));
        }
        finally { Directory.Delete(killed, true); Directory.Delete(continued, true); }
    }

    [Fact]
    public void TheWallClockSteppingBackDoesNotFreezeTheWorkloadPolicy()
    {
        var start = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
        ResourceSample S(int seconds, int wallSeconds, double cpu) => new()
        {
            At = start.AddSeconds(wallSeconds), TickMs = seconds * 1000L, SystemCpuPercent = cpu, RecorderCpuPercent = .1,
            AvailableMemoryBytes = 4L << 30, TotalMemoryBytes = 16L << 30,
        };
        var policy = new WorkloadPolicy();
        foreach (int t in new[] { 0, 2, 4 }) policy.Observe(S(t, t, 90));
        Assert.False(policy.CanStartHeavyWork);
        for (int t = 6; t <= 16; t += 2) policy.Observe(S(t, t - 3600, 20));   // the PC's clock set an hour back
        Assert.True(policy.CanStartHeavyWork);                                // it used to hold the work until the clock caught up
        Assert.Equal(5000, new ResourceMonitor(() => new(), () => 5000, () => start).Sample().TickMs);
    }

    static void Invoke(RecorderSession session, string method) =>
        typeof(RecorderSession).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(session, null);
    static void SetRecording(RecorderSession session, JsonlRecording? rec) =>
        typeof(RecorderSession).GetField("rec", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, rec);

    [Fact]
    public void AnErrorWhileCopyingDoesNotLoseTheSavesAfterIt()
    {
        var root = NewRoot("copy-error");
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        try
        {
            using var session = new RecorderSession(new RecorderSettings { OutputFolder = Path.Combine(root, "output"), SaveFolders = [source], TrackLogs = false }) { FindCiv = false };
            var recording = new JsonlRecording(Path.Combine(root, "session"), "events.jsonl", () => 0);
            recording.Dispose();   // the copy works, writing its line throws
            SetRecording(session, recording);
            foreach (var n in new[] { 1, 2 }) File.WriteAllBytes(Path.Combine(source, $"AutoSave_{n:0000}.Civ5Save"), new byte[32]);
            Invoke(session, "PollAutosaves");
            Assert.Throws<TargetInvocationException>(() => Invoke(session, "PollAutosaves"));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "session", "saves")));
            Assert.Equal(1, session.PendingAutosaves);   // the other save is still queued: it used to be lost
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ABusyComputerSlowsReadingCivButNeverStopsIt()
    {
        var start = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        var policy = new WorkloadPolicy();
        foreach (int t in new[] { 0, 2, 4, 6 })   // Civ itself keeps three cores busy: system CPU 95 %
            policy.Observe(new ResourceSample { At = start.AddSeconds(t), TickMs = t * 1000L, SystemCpuPercent = 95, RecorderCpuPercent = .1, AvailableMemoryBytes = 1L << 30, TotalMemoryBytes = 16L << 30 });
        Assert.True(policy.CanReadMemory);            // it used to stop: no recording began, no save was copied (2026-11-09)
        Assert.Equal(1000, policy.MinimumSampleMs);   // one look a second instead
        Assert.False(policy.CanStartHeavyWork);       // uploads and packing still wait
        policy.SetQuiet(true, "Giving Civ time to start the turn");
        Assert.False(policy.CanReadMemory);           // only a quiet period stops reading
    }

    static bool Eventually(Func<bool> done, int ms = 8000)
    {
        var until = Environment.TickCount64 + ms;
        while (!done()) { if (Environment.TickCount64 > until) return false; Thread.Sleep(50); }
        return true;
    }

    static int SaveCopies(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*.Civ5Save", SearchOption.AllDirectories).Length : 0;

    [Fact]
    public void SavesAreNoticedWhileMemoryReadingWaitsAndCopiedWhenItMayGoOn()
    {
        var root = NewRoot("copy-held");
        var source = Directory.CreateDirectory(Path.Combine(root, "Saves", "multi", "auto")).FullName;
        var output = Path.Combine(root, "output");
        try
        {
            var workload = new WorkloadPolicy();
            workload.SetQuiet(true, "Giving Civ time to start the turn");
            using var session = new RecorderSession(new RecorderSettings { OutputFolder = output, SaveFolders = [source] }) { FindCiv = false, Workload = workload };
            Assert.True(session.TrackLogs);
            session.Start();
            File.WriteAllBytes(Path.Combine(source, "AutoSave_0016.Civ5Save"), new byte[32]);
            Assert.True(Eventually(() => session.HasPendingAutosaves));   // noticed while reading waits (the held loop used to look at nothing)
            Assert.Equal(0, SaveCopies(output));                          // the copy itself waits for the budget
            workload.SetQuiet(false);
            Assert.True(Eventually(() => SaveCopies(output) == 1));        // no memory recording open: kept as a files-only capture
            // games.json is written just after the copy: wait for it too
            Assert.True(Eventually(() => new GameIndex(output).Load().Any(g => g.Identity.StartsWith("files:", StringComparison.Ordinal))));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ASaveWithoutAMemoryRecordingIsKeptUnlessTheGameIsNotRecorded()
    {
        var root = NewRoot("copy-files");
        var multi = Directory.CreateDirectory(Path.Combine(root, "Saves", "multi", "auto")).FullName;
        var single = Directory.CreateDirectory(Path.Combine(root, "Saves", "single", "auto")).FullName;
        var output = Path.Combine(root, "output");
        try
        {
            using var session = new RecorderSession(new RecorderSettings { OutputFolder = output, SaveFolders = [multi, single], TrackLogs = false })
                { FindCiv = false, RecordSinglePlayer = false };
            void Arrives(string folder, string name) { File.WriteAllBytes(Path.Combine(folder, name), new byte[32]); Invoke(session, "PollAutosaves"); Invoke(session, "PollAutosaves"); }
            Arrives(multi, "AutoSave_0016.Civ5Save");                     // not begun yet / reading held / after a detach
            Assert.Equal(1, SaveCopies(output));                          // it used to be dropped
            var files = Assert.Single(new GameIndex(output).Load(), g => g.Identity.StartsWith("files:", StringComparison.Ordinal));
            Arrives(single, "AutoSave_0003.Civ5Save");                    // single player is off: left out
            Assert.Equal(1, SaveCopies(output));
            Assert.Equal(0, session.PendingAutosaves);
            using (var recording = new JsonlRecording(Path.Combine(root, "session"), "events.jsonl", () => 0))
            {
                SetRecording(session, recording);                         // a memory recording is open: the save goes there
                Arrives(multi, "AutoSave_0017.Civ5Save");
                Assert.Single(Directory.GetFiles(Path.Combine(root, "session", "saves")));
            }
            SetRecording(session, null);
            const string deleted = "00000001-00000002";                   // a game the player deleted keeps no saves
            session.DeleteGameIdentity(deleted);
            typeof(RecorderSession).GetProperty("Status")!.SetValue(session, session.Status with { Identity = deleted });
            Arrives(multi, "AutoSave_0018.Civ5Save");
            typeof(RecorderSession).GetProperty("Status")!.SetValue(session, session.Status with { Identity = null });
            session.PauseGame();                                          // nor does a paused one
            Arrives(multi, "AutoSave_0019.Civ5Save");
            Assert.Equal(1, SaveCopies(files.Folder));
            Assert.Equal(1, SaveCopies(Path.Combine(root, "session")));
            Assert.Equal(0, session.PendingAutosaves);
        }
        finally { Directory.Delete(root, true); }
    }
}
