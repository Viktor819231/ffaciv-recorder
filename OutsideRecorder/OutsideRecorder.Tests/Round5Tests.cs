using System.Text;

namespace OutsideRecorderTests;

/// <summary>Round 5 (OR03, track B's stream tests): saves late or missing, "Access to the path is denied". Files shared
/// between Civ, the recorder, the stream and the window never fail a step; fast-growing events never starve the saves.</summary>
public sealed class Round5Tests
{
    static string NewDir(string tag) { var d = Path.Combine(Path.GetTempPath(), $"outside-recorder-{tag}-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void ASaveCopyIsWholeAndNeverOverwritten()
    {
        var dir = NewDir("copy");
        try
        {
            var src = Path.Combine(dir, "AutoSave_0013 BC-3220.Civ5Save");
            File.WriteAllBytes(src, [1, 2, 3, 4]);
            var saves = Path.Combine(dir, "saves");
            var a = SafeFiles.CopySave(src, saves, DateTime.UtcNow.AddMinutes(-1))!;
            Assert.Equal(a, SafeFiles.CopySave(src, saves, DateTime.UtcNow));            // the same bytes: the same copy
            File.WriteAllBytes(src, [9, 9, 9, 9]);                                       // a reload wrote turn 13 again, same size
            var b = SafeFiles.CopySave(src, saves, DateTime.UtcNow)!;
            Assert.NotEqual(a, b);
            Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(a));
            Assert.Equal([9, 9, 9, 9], File.ReadAllBytes(b));
            Assert.Empty(Directory.GetFiles(saves, "*.part"));
            using (new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // Civ still writing it
                Assert.Null(SafeFiles.CopySave(src, Path.Combine(dir, "saves2"), DateTime.UtcNow));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ReplacingAFileSomeoneReadsWaitsInsteadOfFailing()
    {
        var dir = NewDir("replace");
        try
        {
            var f = Path.Combine(dir, "stream.json");
            File.WriteAllText(f, "old");
            var reader = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read);   // an old-style reader (File.ReadAllText)
            var done = new TaskCompletionSource();
            new Thread(() => { Thread.Sleep(300); reader.Dispose(); done.SetResult(); }) { IsBackground = true }.Start();   // its own thread: no wait for a busy pool
            var t = done.Task;
            SafeFiles.Replace(f, "new2");
            await t;
            Assert.Equal("new2", File.ReadAllText(f));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    static (string Root, GameEntry Game) Game()
    {
        var root = NewDir("stream5");
        var game = new GameEntry { Identity = "1a2b3c4d-5e6f7a8b", Folder = Path.Combine(root, "s1"), Context = GameIndex.NewContext(), LastSeen = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(Path.Combine(game.Folder, "saves"));
        new GameIndex(root).Save(game);
        return (root, game);
    }

    static string Lines(int from, int n)
    {
        var sb = new StringBuilder();
        for (int i = from; i < from + n; i++) sb.Append($"{{\"schema\":2,\"kind\":\"result\",\"sequence\":{i},\"pad\":\"{new string('x', 300)}\"}}\n");
        return sb.ToString();
    }

    [Fact]
    public async Task ALockedSaveWaitsAndTheEventsStillGo()
    {
        var (root, game) = Game();
        try
        {
            var site = new FakeSite();
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/", SaveUpload = "full" };
            var events = Path.Combine(game.Folder, "events.jsonl");
            File.WriteAllText(events, Lines(1, 10));
            var save = Path.Combine(game.Folder, "saves", "AutoSave_0001 BC-3940.Civ5Save");
            File.WriteAllBytes(save, new byte[5000]);
            using var s = new Streamer(settings, site);
            using (new FileStream(save, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await s.PassAsync(default);                                              // no exception: the pass goes on
            Assert.Equal(File.ReadAllBytes(events), site.Joined());
            Assert.Empty(site.Saves);
            await s.PassAsync(default);                                                  // free again: sent
            Assert.Single(site.Saves);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Events written faster than they are sent (track B's 150× replay) used to keep the event loop busy forever:
    /// the saves behind it waited minutes. Now each pass sends the events there at its start, then a save.</summary>
    [Fact]
    public async Task FastGrowingEventsDoNotStarveTheSaves()
    {
        var (root, game) = Game();
        try
        {
            var events = Path.Combine(game.Folder, "events.jsonl");
            File.WriteAllText(events, Lines(1, 10));
            int next = 11;
            var site = new GrowingSite(() => { File.AppendAllText(events, Lines(next, 5)); next += 5; });
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/", SaveUpload = "full" };
            for (int t = 1; t <= 3; t++) File.WriteAllBytes(Path.Combine(game.Folder, "saves", $"AutoSave_000{t} BC-4000.Civ5Save"), new byte[3000 + t]);
            using var s = new Streamer(settings, site);
            var pass = s.PassAsync(default);
            Assert.Same(pass, await Task.WhenAny(pass, Task.Delay(10_000)));             // the pass ends although the file never stops growing
            await pass;
            Assert.Equal(3, site.Saves.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>The fake site, and the game writes more events each time a chunk arrives.</summary>
    sealed class GrowingSite(Action onChunk) : HttpMessageHandler
    {
        readonly FakeSite inner = new();
        public List<(string Name, bool Delta, int Bytes)> Saves => inner.Saves;
        static readonly System.Reflection.MethodInfo Inner = typeof(FakeSite).GetMethod("SendAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = await (Task<HttpResponseMessage>)Inner.Invoke(inner, [request, ct])!;
            if (request.RequestUri!.AbsolutePath.Contains("/chunks/")) onChunk();
            return r;
        }
    }
}
