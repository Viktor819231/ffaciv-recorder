using System.Diagnostics;
using System.Security.Cryptography;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>Round 3: game identity, the 2026-10-27 recording, save deltas, autosave settings, continuing a recording.</summary>
public sealed class Round3Tests(ITestOutputHelper output)
{
    /// <summary>The user's 146-turn vanilla multiplayer recording (not in git; the tests pass with a note when it is missing).</summary>
    static readonly string Recorded = TestData.Saves(@"outside-recorder 2026-10-27 vanilla mp");

    static GameSnapshot S(int turn, uint? map, uint? sync, int[] civs, params PlayerState[] players) => new()
    {
        TimeMs = 0, Turn = turn, ActivePlayer = 0, MapWidth = 66, MapHeight = 42, MapSeed = map, SyncSeed = sync, SlotCivs = civs,
        Players = players, Units = [], Cities = [],
    };
    static PlayerState P(int id, int civ, bool alive = true, int slot = 3) => new() { Id = id, Alive = alive, TurnActive = false, Slot = slot, CivType = civ };

    [Fact]
    public void IdentityIsTheGameSeeds()
    {
        int[] civs = [1, 21, 22, 32, 17, 28];
        var game = GamePipeline.IdentityOf(S(80, 0x1a2b3c4d, 0x5e6f7a8b, civs, P(0, 1), P(4, 17)));
        Assert.Equal("1a2b3c4d-5e6f7a8b", game); // the converter's own key for joining saves (IntakeUploads.SeedsOf)
        // player 4 died, a city-state came back, a human dropped to AI: the same game
        Assert.Equal(game, GamePipeline.IdentityOf(S(81, 0x1a2b3c4d, 0x5e6f7a8b, civs, P(0, 1, slot: 1))));
        // reloaded an older save / rejoined after a crash: still the same seeds
        Assert.Equal(game, GamePipeline.IdentityOf(S(60, 0x1a2b3c4d, 0x5e6f7a8b, civs, P(0, 1), P(4, 17))));
        // another game with the same map size and the same civs in the same slots: different seeds
        Assert.NotEqual(game, GamePipeline.IdentityOf(S(80, 0x11111111, 0x22222222, civs, P(0, 1), P(4, 17))));
    }

    [Fact]
    public void FallbackIdentityIgnoresWhoIsAlive()
    {
        int[] civs = [1, 21, 22, 32, 17, 28];
        var a = GamePipeline.IdentityOf(S(80, null, null, civs, P(0, 1), P(4, 17)));
        var b = GamePipeline.IdentityOf(S(81, null, null, civs, P(0, 1)));          // player 4 died
        Assert.Equal(a, b);
    }

    [Fact]
    public void TheRecordedGameIsOneGame()
    {
        var dir = Path.Combine(Recorded, "recording");
        if (!Directory.Exists(dir)) { output.WriteLine("recording not on this PC: skipped"); return; }
        var ids = new List<string>();
        foreach (var session in Directory.GetDirectories(dir, "session-*").Order())
        {
            var mem = new ReplayMemory();
            StateReader? reader = null;
            foreach (var (h, s) in TapeReader.Read(Path.Combine(session, "tape.bin.gz")))
            {
                if (h is not null) { reader = TapeReplay.ReaderFor(h, mem); continue; }
                mem.Apply(s!);
                var (st, snap, _) = reader!.Read(0);
                if (st == SampleStatus.Ok && snap is not null) { ids.Add(GamePipeline.IdentityOf(snap)); break; }
            }
        }
        output.WriteLine(string.Join("\n", ids));
        Assert.Equal(8, ids.Count);
        Assert.Single(ids.Distinct()); // the old rule (living players) gave 8 different ones
    }

    [Fact]
    public void SaveDeltaRebuildsTheSave()
    {
        var dir = Path.Combine(Recorded, "saves");
        if (!Directory.Exists(dir)) { output.WriteLine("saves not on this PC: skipped"); return; }
        var files = Directory.GetFiles(dir, "*.Civ5Save").OrderBy(f => f).ToList();
        foreach (int i in new[] { 5, 30, 70, 120 })
        {
            var a = File.ReadAllBytes(files[i]); var b = File.ReadAllBytes(files[i + 1]);
            var sw = Stopwatch.StartNew();
            var pa = SaveParts.Split(a)!; var pb = SaveParts.Split(b)!;
            var split = sw.Elapsed.TotalMilliseconds; sw.Restart();
            var delta = SaveDelta.Make(pa, pb, b);
            var make = sw.Elapsed.TotalMilliseconds; sw.Restart();
            var (file, parts, header) = SaveDelta.Apply(pa, delta);
            var apply = sw.Elapsed.TotalMilliseconds;
            Assert.Equal(pb.ContentSha256(), parts.ContentSha256());
            Assert.Equal(Convert.ToHexStringLower(MD5.HashData(b)), header.FileMd5);
            var again = SaveParts.Split(file)!;          // the rebuilt file reads back to the same content
            Assert.Equal(pb.ContentSha256(), again.ContentSha256());
            output.WriteLine($"{Path.GetFileName(files[i + 1])}: save {b.Length / 1024} KB, payload {pb.Payload.Length / 1024} KB, delta {delta.Length / 1024} KB " +
                $"({100.0 * delta.Length / b.Length:F0} %), split {split:F0} ms, make {make:F0} ms, apply {apply:F0} ms, rebuilt file {file.Length / 1024} KB");
        }
    }

    [Fact]
    public void AutosaveSettingsAreChecked()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "[AutoSave]\n\nTurnsBetweenAutosave = 1\n\nNumAutosavesKept = 200\n");
            Assert.Null(RecorderSession.CheckAutosaveSettings(f));
            File.WriteAllText(f, "[AutoSave]\nTurnsBetweenAutosave = 5\nNumAutosavesKept = 200\n");
            Assert.Contains("every 5 turns", RecorderSession.CheckAutosaveSettings(f));
            Assert.Null(RecorderSession.CheckAutosaveSettings(f + ".missing"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void ARecordingContinuesItsNumbers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-cont-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var r = new JsonlRecording(dir, "events.jsonl", () => 1)) { r.Write("lifecycle", 1, new { a = 1 }); r.Write("lifecycle", 1, new { a = 2 }); }
            using (var r = new JsonlRecording(dir, "events.jsonl", () => 2)) { r.Write("lifecycle", 2, new { a = 3 }); Assert.Equal(3, r.Count); }
            Assert.Contains("\"sequence\":3", File.ReadAllLines(Path.Combine(dir, "events.jsonl"))[2]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GameIndexFindsTheSameGameAgain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "s1"));
        try
        {
            var index = new GameIndex(dir);
            var e = new GameEntry { Identity = "1a2b3c4d-5e6f7a8b", Folder = Path.Combine(dir, "s1"), Context = GameIndex.NewContext(), ProcessId = 1, LastSeen = DateTimeOffset.UtcNow };
            index.Save(e);
            Assert.Equal(e.Context, new GameIndex(dir).Find("1a2b3c4d-5e6f7a8b", DateTimeOffset.UtcNow)!.Context);
            Assert.Null(index.Find("other", DateTimeOffset.UtcNow));
            Assert.Null(index.Find("1a2b3c4d-5e6f7a8b", DateTimeOffset.UtcNow.AddDays(30)));
            Assert.Matches("^[0-9a-f]{32}$", e.Context);
        }
        finally { Directory.Delete(dir, true); }
    }
}
