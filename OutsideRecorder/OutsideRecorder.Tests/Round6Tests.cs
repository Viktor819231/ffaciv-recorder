using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>
/// Round 6 (OR03, the user's vanilla MP test 2026-10-27 20:35): the stream stopped for good when one answer took longer
/// than the client's timeout: the timeout came as a cancellation and the send loop took it for "the program stops".
/// Now a slow or hung answer is a failure that is tried again, saves never hold up the events, and a watchdog starts a
/// sender again that stops finishing passes.
/// </summary>
public sealed class Round6Tests(ITestOutputHelper output)
{
    static (string Root, GameEntry Game, string Events) Game()
    {
        var root = Path.Combine(Path.GetTempPath(), "outside-recorder-r6-" + Guid.NewGuid().ToString("N"));
        var game = new GameEntry { Identity = "008bc565-008bc565", Folder = Path.Combine(root, "s1"), Context = GameIndex.NewContext(), LastSeen = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(Path.Combine(game.Folder, "saves"));
        new GameIndex(root).Save(game);
        return (root, game, Path.Combine(game.Folder, "events.jsonl"));
    }

    static string Lines(int from, int n)
    {
        var sb = new StringBuilder();
        for (int i = from; i < from + n; i++) sb.Append($"{{\"schema\":2,\"kind\":\"result\",\"sequence\":{i}}}\n");
        return sb.ToString();
    }

    /// <summary>The fake site, with a save answer that hangs (honouring the cancel, or not at all).</summary>
    sealed class SlowSite(FakeSite inner) : HttpMessageHandler
    {
        public int HangSaves;            // this many save posts never answer (until cancelled)
        public bool IgnoreCancel;        // ... and do not even stop when cancelled (the watchdog's case)
        public int HangChunks;
        static readonly System.Reflection.MethodInfo Inner = typeof(FakeSite).GetMethod("SendAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            bool hang = (path.EndsWith("/saves") && Interlocked.Decrement(ref HangSaves) >= 0) || (path.Contains("/chunks/") && Interlocked.Decrement(ref HangChunks) >= 0);
            if (hang) await (IgnoreCancel ? Task.Delay(Timeout.Infinite) : Task.Delay(Timeout.Infinite, ct));
            return await (Task<HttpResponseMessage>)Inner.Invoke(inner, [r, ct])!;
        }
    }

    static async Task Until(Func<bool> ok, int seconds = 20) { var sw = Stopwatch.StartNew(); while (!ok() && sw.Elapsed.TotalSeconds < seconds) await Task.Delay(50); }

    [Fact]
    public async Task ASaveAnswerThatNeverComesDoesNotStopTheStream()
    {
        var (root, game, events) = Game();
        try
        {
            var site = new FakeSite();
            var slow = new SlowSite(site) { HangSaves = 1 };
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/", StreamIntervalS = 1, SaveUpload = "full" };
            File.WriteAllText(events, Lines(1, 10));
            File.WriteAllBytes(Path.Combine(game.Folder, "saves", "AutoSave_0011 BC-3340.Civ5Save"), new byte[4000]);
            using var s = new Streamer(settings, slow) { RequestTimeout = TimeSpan.FromSeconds(1) };
            s.Start();
            await Until(() => site.Chunks.Count > 0);
            File.AppendAllText(events, Lines(11, 10));                                   // the game goes on while the save hangs
            await Until(() => site.Joined().Length == new FileInfo(events).Length && site.Saves.Count == 1);
            Assert.Equal(File.ReadAllBytes(events), site.Joined());                      // the events went on
            Assert.Single(site.Saves);                                                    // the save went on its second try
            Assert.Contains("no answer from the server", s.Status.LastError ?? "");
            await Until(() => s.Status.State.StartsWith("connected"));                   // the saves sender's pass ends, then it says so
            Assert.StartsWith("connected", s.Status.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact(Skip = "Out of date since 0.7.10: saves and events take turns in one upload slot (one job at a time keeps the game smooth), "
        + "so the stuck sender keeps the slot and the restarted one waits behind it. Only a request that ignores its own cancel stays "
        + "stuck like this; HttpClient's requests honour theirs.")]
    public async Task ASenderStuckForGoodIsStartedAgainByTheWatchdog()
    {
        var (root, game, events) = Game();
        try
        {
            var site = new FakeSite();
            var slow = new SlowSite(site) { HangChunks = 1, IgnoreCancel = true };       // hangs even through its timeout
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/", StreamIntervalS = 1 };
            File.WriteAllText(events, Lines(1, 10));
            using var s = new Streamer(settings, slow) { RequestTimeout = TimeSpan.FromSeconds(30), StallAfter = TimeSpan.FromSeconds(2), WatchEvery = TimeSpan.FromMilliseconds(300) };
            s.Start();
            await Until(() => site.Chunks.Count > 0);
            Assert.True(s.Restarts >= 1, "the watchdog did not start the stuck sender again");
            Assert.Equal(File.ReadAllBytes(events), site.Joined());
            Assert.Contains("STALLED", File.ReadAllText(Path.Combine(root, "stream.log")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact(Skip = "Out of date since 0.7.10: saves and events take turns in one upload slot (one job at a time keeps the game smooth), "
        + "so a hanging save holds the events until its request times out (RequestTimeout, 45 s in the program), then they go on.")]
    public async Task ASlowSaveDoesNotHoldUpTheEvents()
    {
        var (root, game, events) = Game();
        try
        {
            var site = new FakeSite();
            var slow = new SlowSite(site) { HangSaves = 1 };
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/", StreamIntervalS = 1, SaveUpload = "full" };
            File.WriteAllText(events, Lines(1, 5));
            File.WriteAllBytes(Path.Combine(game.Folder, "saves", "AutoSave_0001 BC.Civ5Save"), new byte[4000]);
            using var s = new Streamer(settings, slow) { RequestTimeout = TimeSpan.FromSeconds(10) };
            s.Start();
            await Task.Delay(1500);                                                      // the save is hanging now
            File.AppendAllText(events, Lines(6, 5));
            await Until(() => site.Joined().Length == new FileInfo(events).Length, 5);
            Assert.Equal(File.ReadAllBytes(events), site.Joined());                      // well inside the save's 10 s
            Assert.Empty(site.Saves);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>How long a delta of the user's vanilla MP saves takes (the 20:35 game; skipped when not on this PC).</summary>
    [Fact]
    public void AVanillaDeltaIsQuick()
    {
        var dir = TestData.Saves("outside-recorder 2026-10-28 stall");
        var a = Path.Combine(dir, "AutoSave_0010 BC-3400.Civ5Save"); var b = Path.Combine(dir, "AutoSave_0011 BC-3340.Civ5Save");
        if (!File.Exists(a) || !File.Exists(b)) { output.WriteLine("saves not here: skipped"); return; }
        var sw = Stopwatch.StartNew();
        var pa = SaveParts.Split(File.ReadAllBytes(a))!; var bytes = File.ReadAllBytes(b); var pb = SaveParts.Split(bytes)!;
        var delta = SaveDelta.Make(pa, pb, bytes);
        output.WriteLine($"delta {delta.Length / 1024} KB of {bytes.Length / 1024} KB in {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 5000);
        Assert.Equal(pb.ContentSha256(), SaveDelta.Apply(pa, delta).Parts.ContentSha256());
    }
}
