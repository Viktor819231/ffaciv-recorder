using System.Net;
using System.Text.Json.Nodes;

namespace OutsideRecorderTests;

public sealed class ReleaseAndDeletionTests
{
    sealed class Receiver(string config = "{}") : HttpMessageHandler
    {
        public bool Down;
        public readonly List<string> Calls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("Offline");
            Calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            string text = request.RequestUri.AbsolutePath.EndsWith("/config") ? config
                : request.RequestUri.AbsolutePath.EndsWith("/register") ? "{\"id\":\"u_0123456789abcdef\",\"key\":\"u_0123456789abcdef.key\"}"
                : "{\"deleted\":true}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
        }
    }

    [Theory]
    [InlineData("0.6.0", "0.6.0", null, false)]
    [InlineData("0.6", "0.6.0", null, false)]
    [InlineData("0.7.0", "0.6.0", "0.7.0", false)]
    [InlineData("0.7.0", "0.8.0", "0.8.0", true)]
    public async Task ReleaseAnnouncementsCompareVersionsWithoutDownloading(string latest, string minimum, string? expected, bool required)
    {
        var receiver = new Receiver(new JsonObject { ["latestVersion"] = latest, ["minVersion"] = minimum,
            ["downloadUrl"] = "javascript:alert(1)", ["releaseNotes"] = new JsonArray("New mod support") }.ToJsonString());
        using var http = new HttpClient(receiver);
        var release = await new ReleaseChecker(http, "0.6.0").CheckAsync(new Uri("https://ffaciv.com/replay/api/uploader/config"), default);
        Assert.Equal(expected, release?.Version);
        if (release is not null)
        {
            Assert.Equal(required, release.Required);
            Assert.Equal("https://ffaciv.com/replay/recorder/", release.DownloadUrl);
            Assert.Equal(["New mod support"], release.Notes);
        }
        Assert.Equal(["GET /replay/api/uploader/config"], receiver.Calls);
    }

    [Fact]
    public async Task UpdateLinksStayOnTheOfficialHttpsSite()
    {
        foreach (var link in new[] { "https://example.com/setup.exe", "https://ffaciv.com.evil.example/setup.exe",
            "https://ffaciv.com@evil.example/setup.exe", "https://user@ffaciv.com/setup.exe", "https://ffaciv.com:8443/setup.exe",
            "http://ffaciv.com/setup.exe", "file:///C:/setup.exe", "//ffaciv.com/setup.exe" })
        {
            using var http = new HttpClient(new Receiver(new JsonObject { ["latestVersion"] = "1.0.0", ["downloadUrl"] = link }.ToJsonString()));
            var release = await new ReleaseChecker(http, "0.7.0").CheckAsync(new Uri("https://ffaciv.com/replay/api/uploader/config"), default);
            Assert.Equal("https://ffaciv.com/replay/recorder/", release!.DownloadUrl);
        }
        foreach (var link in new[] { "https://ffaciv.com/replay/recorder/", "https://www.ffaciv.com/replay/download/recorder.exe" })
        {
            using var http = new HttpClient(new Receiver(new JsonObject { ["latestVersion"] = "1.0.0", ["downloadUrl"] = link }.ToJsonString()));
            var release = await new ReleaseChecker(http, "0.7.0").CheckAsync(new Uri("https://ffaciv.com/replay/api/uploader/config"), default);
            Assert.Equal(link, release!.DownloadUrl);
        }
    }

    [Fact]
    public async Task DeletionSurvivesAnOutageAndDoesNotUploadAnyMoreSaves()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-delete-" + Guid.NewGuid().ToString("N"));
        var context = new string('a', 32);
        var entry = new GameEntry { Identity = "00000001-00000002", Context = context, Folder = Path.Combine(root, "session-delete"), LastSeen = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(Path.Combine(entry.Folder, "saves"));
        File.WriteAllText(Path.Combine(entry.Folder, "events.jsonl"), "{\"kind\":\"lifecycle\",\"sequence\":1,\"data\":{\"state\":\"paused\"}}\n");
        File.WriteAllText(Path.Combine(entry.Folder, "saves", "AutoSave_0050.Civ5Save"), "Never upload this deleted save");
        new GameIndex(root).Save(entry);
        var original = Path.Combine(root, "original.Civ5Save"); File.WriteAllText(original, "Civ's original save");
        var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://localhost/replay/api/uploader/" };
        try
        {
            using (var offline = new Streamer(settings, new Receiver { Down = true }))
            {
                offline.QueueReplayDeletion(context, waitForEnd: true);
                await Assert.ThrowsAsync<HttpRequestException>(() => offline.FlushReplayDeletionsAsync(default));
                Assert.True(Directory.Exists(entry.Folder));
            }
            var receiver = new Receiver();
            using (var online = new Streamer(settings, receiver))
            {
                await online.SavesPassAsync(default); await online.EventsPassAsync(default);
                Assert.Equal("deleted", online.ReadReplay(context)["state"]!.GetValue<string>());
                Assert.False(Directory.Exists(entry.Folder));
                Assert.True(File.Exists(original));
                Assert.Contains(receiver.Calls, c => c.EndsWith($"recordings/{context}/delete"));
                Assert.DoesNotContain(receiver.Calls, c => c.EndsWith("/saves") || c.Contains("/chunks/"));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeletedGameIdentityRemainsStoppedAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-stop-deleted-" + Guid.NewGuid().ToString("N"));
        var settings = new RecorderSettings { OutputFolder = root };
        try
        {
            using (var first = new RecorderSession(settings)) first.DeleteGameIdentity("00000001-00000002");
            using var next = new RecorderSession(settings);
            Assert.True(next.IsDeletedIdentity("00000001-00000002"));
            Assert.False(next.IsDeletedIdentity("00000003-00000004"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ResourceHeldDeletionStaysQueuedUntilTheWebsiteActuallyAcknowledgesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-delete-held-" + Guid.NewGuid().ToString("N"));
        var context = GameIndex.NewContext();
        var workload = new WorkloadPolicy(); workload.SetQuiet(true);
        var receiver = new Receiver();
        try
        {
            using var stream = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local", UploadKilobytesPerSecond = 0 }, receiver) { Workload = workload };
            stream.QueueReplayDeletion(context, "vanilla", "game");
            await stream.FlushReplayDeletionsAsync(default);
            Assert.True(stream.ReplayDeletionQueued(context));
            Assert.Equal(new ReplayDeletionTarget(context, "vanilla", "game"), Assert.Single(stream.QueuedReplayDeletions()));
            Assert.Empty(receiver.Calls);
            workload.SetQuiet(false);
            await stream.FlushReplayDeletionsAsync(default);
            Assert.False(stream.ReplayDeletionQueued(context));
            Assert.Empty(stream.QueuedReplayDeletions());
            Assert.Contains(receiver.Calls, call => call.EndsWith($"recordings/{context}/delete"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // the user, 2026-10-31: "a way to regret deleting replays … hide it for like 1 week but make it able for user to pull it back"
    [Fact]
    public async Task HeldDeletionOnlyHidesTheReplayUntilTheHoldEndsAndUndoBringsItBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-delete-hold-" + Guid.NewGuid().ToString("N"));
        var context = GameIndex.NewContext();
        var receiver = new Receiver();
        try
        {
            using var stream = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local", UploadKilobytesPerSecond = 0 }, receiver);
            stream.QueueReplayDeletion(context, hold: Streamer.DeletionHold, card: new JsonObject { ["mod"] = "Lekmod" }, wasPrivate: false);
            var held = Assert.Single(stream.QueuedReplayDeletions());
            Assert.Equal("recording:" + context, held.Key);
            Assert.True(held.Due > DateTimeOffset.UtcNow.AddDays(6.9));
            Assert.Equal("Lekmod", held.Card!["mod"]!.GetValue<string>());
            await stream.FlushReplayDeletionsAsync(default);
            await stream.FlushReplayDeletionsAsync(default);
            // hidden on the site once (made private), never withdrawn while the hold lasts
            Assert.Equal([$"recordings/{context}/state"], receiver.Calls.Where(c => c.Contains("/recordings/")).Select(c => c[(c.IndexOf("recordings/"))..]));
            Assert.True(stream.ReplayDeletionQueued(context));
            var undone = await stream.CancelReplayDeletionAsync(held.Key, default);
            Assert.True(undone!["hidden"]!.GetValue<bool>());
            Assert.False(undone["wasPrivate"]!.GetValue<bool>());
            Assert.False(stream.ReplayDeletionQueued(context));
            Assert.Null(await stream.CancelReplayDeletionAsync(held.Key, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HeldDeletionIsWithdrawnForGoodOnceTheHoldEnds()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-delete-due-" + Guid.NewGuid().ToString("N"));
        var context = GameIndex.NewContext();
        var receiver = new Receiver();
        try
        {
            using var stream = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local", UploadKilobytesPerSecond = 0 }, receiver);
            stream.QueueReplayDeletion(context, hold: TimeSpan.Zero);
            await stream.FlushReplayDeletionsAsync(default);
            Assert.Contains(receiver.Calls, c => c.EndsWith($"recordings/{context}/delete"));
            Assert.False(stream.ReplayDeletionQueued(context));
            Assert.Null(await stream.CancelReplayDeletionAsync("recording:" + context, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    sealed class Refuser : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/saves")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge) { Content = new StringContent("{\"error\":\"a save is at most 25 MB\"}") });
            string text = path.EndsWith("/register") ? "{\"id\":\"u_0123456789abcdef\",\"key\":\"u_0123456789abcdef.key\"}" : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
        }
    }

    // the user, 2026-10-31: "the recorder needs more feedback on when things go wrong"
    [Fact]
    public async Task ARefusedSaveKeepsTheSitesOwnWordsForTheSavesList()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-refused-" + Guid.NewGuid().ToString("N"));
        var entry = new GameEntry { Identity = "00000001-00000002", Context = GameIndex.NewContext(), Folder = Path.Combine(root, "session-refused"), LastSeen = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(Path.Combine(entry.Folder, "saves"));
        var save = Path.Combine(entry.Folder, "saves", "AutoSave_0050.Civ5Save");
        File.WriteAllText(save, "a save the site refuses");
        new GameIndex(root).Save(entry);
        try
        {
            using var stream = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local", UploadKilobytesPerSecond = 0 }, new Refuser());
            try { await stream.SavesPassAsync(default); } catch (HttpRequestException) { }
            var refused = stream.RefusalOf(save);
            Assert.NotNull(refused);
            Assert.Equal(413, refused!.Status);
            Assert.Equal("a save is at most 25 MB", refused.Why);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
