using System.Net;
using System.Text;
using System.Text.Json;

namespace OutsideRecorderTests;

/// <summary>0.5.0, the user build (card OR05): ffaciv.com by default, the install key protected, plain-words problems,
/// copies removed once the server has them, the live slot ("busy line").</summary>
public sealed class UserBuildTests
{
    static string NewDir() { var d = Path.Combine(Path.GetTempPath(), "outside-recorder-user-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void TheUserBuildDefaults()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, RecorderSettings.DefaultFileText("site"));
            var s = RecorderSettings.Load(f);
            Assert.Equal("site", s.Stream);
            Assert.False(s.Tape);
            Assert.False(s.KeepCopies);
            Assert.Equal(50, s.SampleMs);
            Assert.Equal("https://ffaciv.com/replay/api/uploader/", s.StreamAddress);
            Assert.Equal("https://ffaciv.com/replay/", s.LiveSiteAddress);
            Assert.Equal("https://test.example.pages.dev/replay/", (s with { StreamUrl = "https://test.example.pages.dev/replay/api/uploader/" }).LiveSiteAddress);
            File.WriteAllText(f, RecorderSettings.DefaultFileText("local"));
            var l = RecorderSettings.Load(f);
            Assert.True(l.Tape); Assert.True(l.KeepCopies); Assert.Equal("http://127.0.0.1:5632/replay/", l.LiveSiteAddress);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void TheKeyIsProtectedForThisWindowsUser()
    {
        var p = KeyStore.Protect("u_0123456789abcdef.k");
        Assert.DoesNotContain("u_0123456789abcdef", Encoding.UTF8.GetString(Convert.FromBase64String(p)));
        Assert.Equal("u_0123456789abcdef.k", KeyStore.Unprotect(p));
        Assert.Null(KeyStore.Unprotect("bm90IGEga2V5"));
    }

    /// <summary>The site's answers for the problems; everything else as the uploader API says.</summary>
    sealed class Site(HttpStatusCode code, string body = "{}", TimeSpan? retry = null) : HttpMessageHandler
    {
        public int Registers;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            HttpResponseMessage J(HttpStatusCode c, string b) => new(c) { Content = new StringContent(b) };
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/register")) { Registers++; return Task.FromResult(J(HttpStatusCode.OK, "{\"id\":\"u_0123456789abcdef\",\"key\":\"u_0123456789abcdef.k\"}")); }
            var m = J(code, body);
            if (retry is { } t) m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(t);
            return Task.FromResult(m);
        }
    }

    static (string Root, GameEntry Game) Game(string root)
    {
        // Cleanup accepts only recorder-owned session folders; use the production naming contract.
        var game = new GameEntry { Identity = "1a2b3c4d-5e6f7a8b", Folder = Path.Combine(root, "session-test"), Context = GameIndex.NewContext(), LastSeen = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(Path.Combine(game.Folder, "saves"));
        new GameIndex(root).Save(game);
        File.WriteAllText(Path.Combine(game.Folder, "events.jsonl"), "{\"kind\":\"lifecycle\"}\n");
        return (root, game);
    }

    [Theory]
    [InlineData(426, "{\"error\":\"old\",\"minVersion\":\"0.6.0\",\"downloadUrl\":\"https://ffaciv.com/recorder\"}", "update")]
    [InlineData(403, "{\"error\":\"blocked\",\"blocked\":true,\"enabled\":true}", "blocked")]
    [InlineData(403, "{\"error\":\"off\",\"blocked\":false,\"enabled\":false}", "off")]
    [InlineData(429, "{}", "cap")]
    [InlineData(401, "{}", "key")]
    public async Task ProblemsGetAPlainWord(int code, string body, string problem)
    {
        var (root, _) = Game(NewDir());
        try
        {
            var site = new Site((HttpStatusCode)code, body, code == 429 ? TimeSpan.FromHours(3) : null);
            using var s = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "site", StreamUrl = "http://test/replay/api/uploader/" }, site);
            await Assert.ThrowsAnyAsync<Exception>(() => s.PassAsync(default));
            Assert.Equal(problem, s.Status.Problem);
            if (problem == "update") { Assert.Equal("0.6.0", s.Status.MinVersion); Assert.Equal("https://ffaciv.com/recorder", s.Status.DownloadUrl); }
            if (problem == "cap") Assert.InRange(s.Status.Until!.Value, DateTimeOffset.UtcNow.AddHours(2.9), DateTimeOffset.UtcNow.AddHours(3.1));
            // the key on disk is protected; 401 throws it away so the next pass registers again
            var file = Directory.GetFiles(root, "stream-install-*.json").SingleOrDefault();
            if (problem == "key") Assert.Null(file);
            else Assert.Contains("keyProtected", File.ReadAllText(file!));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CopiesGoOnceTheServerHasThem()
    {
        var (root, game) = Game(NewDir());
        try
        {
            for (int t = 1; t <= 3; t++) { var f = Path.Combine(game.Folder, "saves", $"AutoSave_000{t} BC.Civ5Save"); File.WriteAllBytes(f, new byte[3000 + t]); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(t - 10)); }
            File.AppendAllText(Path.Combine(game.Folder, "events.jsonl"), "{\"kind\":\"lifecycle\",\"data\":{\"state\":\"civ_closed\"}}\n");
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://test/replay/api/uploader/", SaveUpload = "full", KeepCopies = false };
            using (var s = new Streamer(settings, new FakeSite())) await s.PassAsync(default);
            var left = Directory.GetFiles(Path.Combine(game.Folder, "saves")).Select(Path.GetFileName).ToList();
            Assert.Equal(["AutoSave_0003 BC.Civ5Save"], left);                         // the last one stays: the next delta is made against it
            Assert.True(Directory.Exists(game.Folder));                                  // the game was written to just now: kept
            File.SetLastWriteTimeUtc(Path.Combine(game.Folder, "events.jsonl"), DateTime.UtcNow.AddHours(-1));
            using (var s = new Streamer(settings, new FakeSite())) await s.PassAsync(default);
            Assert.False(Directory.Exists(game.Folder));                                 // ended, sent, quiet for an hour: removed
            // with keep_copies on nothing is removed
            var (root2, game2) = Game(NewDir());
            File.WriteAllBytes(Path.Combine(game2.Folder, "saves", "AutoSave_0001 BC.Civ5Save"), new byte[3000]);
            File.SetLastWriteTimeUtc(Path.Combine(game2.Folder, "saves", "AutoSave_0001 BC.Civ5Save"), DateTime.UtcNow.AddMinutes(-5));
            using (var s = new Streamer(settings with { OutputFolder = root2, KeepCopies = true }, new FakeSite())) await s.PassAsync(default);
            Assert.Single(Directory.GetFiles(Path.Combine(game2.Folder, "saves")));
            Directory.Delete(root2, true);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>The busy line against the site's route (cloud-feed lib/live-slots.js): one POST live/slot with an action.</summary>
    sealed class Slots(string? answer) : HttpMessageHandler
    {
        public string? LastPath, LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/register")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"u_1\",\"key\":\"u_1.k\"}") };
            LastPath = path; LastBody = await r.Content!.ReadAsStringAsync(ct);
            return answer is null ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) };
        }
    }

    [Fact]
    public async Task TheBusyLine()
    {
        var root = NewDir();
        try
        {
            async Task<(LiveSlotState, Slots)> Ask(string? answer, string stream, string action = "take")
            {
                var h = new Slots(answer);
                using var s = new Streamer(new RecorderSettings { OutputFolder = root, Stream = stream, StreamUrl = "http://test/replay/api/uploader/" }, h);
                return (await s.LiveSlotAsync("1a2b3c4d-5e6f7a8b", "c", action, default), h);
            }
            var (g, h1) = await Ask("{\"status\":\"granted\",\"granted\":true,\"slots\":{\"total\":2,\"used\":1,\"free\":1},\"renewEverySeconds\":60,\"retryAfterSeconds\":30,\"watch\":\"https://ffaciv.com/replay/live/1a2b3c4d-5e6f7a8b/watch\"}", "site");
            Assert.Equal(("granted", 60, "https://ffaciv.com/replay/live/1a2b3c4d-5e6f7a8b/watch"), (g.State, g.AskAgainSeconds, g.Watch));
            Assert.EndsWith("/live/slot", h1.LastPath); Assert.Contains("\"action\":\"take\"", h1.LastBody);
            var (b, _) = await Ask("{\"status\":\"busy\",\"slots\":{\"total\":2,\"used\":2,\"free\":0},\"queue\":{\"position\":3,\"waiting\":4},\"requested\":false,\"retryAfterSeconds\":30}", "site");
            Assert.Equal(("busy", 3, false, 30), (b.State, b.Position, b.Requested, b.AskAgainSeconds));
            var (r, h3) = await Ask("{\"status\":\"requested\",\"requested\":true,\"queue\":{\"position\":2,\"waiting\":3}}", "site", "request");
            Assert.True(r.Requested); Assert.Equal(2, r.Position); Assert.Contains("\"action\":\"request\"", h3.LastBody);
            Assert.Equal("off", (await Ask("{\"status\":\"off\"}", "site")).Item1.State);              // LIVE_SLOTS=0
            Assert.Equal("unavailable", (await Ask(null, "site")).Item1.State);                        // no route on this server
            Assert.Equal("granted", (await Ask(null, "local")).Item1.State);                           // the local test server has no line
            var (_, h4) = await Ask("{\"status\":\"released\"}", "site", "release");
            Assert.Contains("\"action\":\"release\"", h4.LastBody);
        }
        finally { Directory.Delete(root, true); }
    }
}
