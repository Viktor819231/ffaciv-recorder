using System.Net;
using System.Text.Json.Nodes;

namespace OutsideRecorderTests;

public sealed class LiveShareTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
    static LiveShareInvite Invite(string token = "invite") => new(token, "https://example.test/replay/live/share/" + token, Now.AddHours(4));

    [Fact]
    public async Task PlayerFollowsNewContextOnlyAfterNewPrivateAndShareAcknowledgements()
    {
        var live = new LiveShareSession(() => Now);
        live.Start("player", 4, "first");
        Assert.False(live.CanSend("first"));
        Assert.True(await live.PrepareAsync("first", () => Task.FromResult(true)));
        Assert.False(live.CanSend("first"));
        Assert.True(await live.BindAsync("first", token => { Assert.Null(token); return Task.FromResult(Invite()); }, _ => Task.CompletedTask));
        Assert.True(live.CanSend("first"));
        Assert.True(live.CanSendLobby);
        // A temporary game disconnect does not mutate this explicit player consent.
        Assert.True(live.Snapshot.Requested);
        Assert.False(live.CanSend("second"));
        Assert.False(await live.PrepareAsync("second", () => Task.FromResult(false)));
        Assert.False(live.CanSend("second"));
        Assert.False(live.CanSend("first"));
        Assert.True(await live.PrepareAsync("second", () => Task.FromResult(true)));
        Assert.True(await live.BindAsync("second", token => { Assert.Equal("invite", token); return Task.FromResult(Invite()); }, _ => Task.CompletedTask));
        Assert.True(live.CanSend("second"));
        Assert.False(live.CanSend("first"));
        Assert.Equal(Invite().Url, live.Snapshot.Url);
    }

    [Fact]
    public async Task GameScopeNeverAuthorizesAnotherRecording()
    {
        var live = new LiveShareSession(() => Now);
        live.Start("game", 1, "first");
        Assert.False(await live.PrepareAsync("second", () => throw new Exception("Must not call")));
        Assert.False(live.CanSendLobby);
        Assert.False(live.CanSend("second"));
    }

    [Fact]
    public async Task GameInviteCanRejoinSameSeedsWithNewPrivateContextButNeverFollowDifferentGame()
    {
        var live = new LiveShareSession(() => Now);
        live.Start("game", 4, "first", "00000001-00000002");
        Assert.True(await live.PrepareAsync("first", () => Task.FromResult(true), "00000001-00000002"));
        await live.BindAsync("first", _ => Task.FromResult(Invite()), _ => Task.CompletedTask, "00000001-00000002");
        Assert.False(live.CanSend("rejoin"));
        Assert.False(await live.PrepareAsync("rejoin", () => Task.FromResult(false), "00000001-00000002"));
        Assert.False(live.CanSend("rejoin"));
        Assert.True(await live.PrepareAsync("rejoin", () => Task.FromResult(true), "00000001-00000002"));
        await live.BindAsync("rejoin", token => { Assert.Equal("invite", token); return Task.FromResult(Invite()); }, _ => Task.CompletedTask, "00000001-00000002");
        Assert.True(live.CanSend("rejoin"));
        Assert.False(await live.PrepareAsync("third", () => throw new Exception("Must not authorize a different game"), "00000003-00000004"));
        Assert.False(live.CanSend("third"));
    }

    [Fact]
    public async Task CrashRejoinKeepsTheSamePlayerInviteAndOriginalDeadline()
    {
        var time = Now;
        var live = new LiveShareSession(() => time);
        live.Start("player", 4, "before-crash");
        await live.PrepareAsync("before-crash", () => Task.FromResult(true));
        await live.BindAsync("before-crash", _ => Task.FromResult(Invite()), _ => Task.CompletedTask);
        var before = live.Snapshot;
        time = time.AddMinutes(20); // Civ is gone; Recorder remains running.
        Assert.True(live.Snapshot.Requested);
        Assert.False(live.CanSend("rejoined-context"));
        await live.PrepareAsync("rejoined-context", () => Task.FromResult(true));
        await live.BindAsync("rejoined-context", token => {
            Assert.Equal(before.Token, token);
            return Task.FromResult(Invite());
        }, _ => Task.CompletedTask);
        Assert.Equal(before.Url, live.Snapshot.Url);
        Assert.Equal(before.ExpiresAt, live.Snapshot.ExpiresAt);
        time = Now.AddHours(4);
        Assert.False(live.Snapshot.Requested);
    }

    [Fact]
    public async Task StopDuringCreationRevokesLateInviteAndNeverAuthorizesData()
    {
        var live = new LiveShareSession(() => Now);
        live.Start("player", 4, "first");
        await live.PrepareAsync("first", () => Task.FromResult(true));
        var pending = new TaskCompletionSource<LiveShareInvite>();
        string? revoked = null;
        var bind = live.BindAsync("first", _ => pending.Task, token => { revoked = token; return Task.CompletedTask; });
        live.Stop(); pending.SetResult(Invite());
        Assert.False(await bind);
        Assert.Equal("invite", revoked);
        Assert.False(live.CanSend("first"));
        Assert.Null(live.Snapshot.Url);
    }

    [Fact]
    public async Task WallClockExpiryAndRestartCannotResumeAnInvite()
    {
        var time = Now;
        var live = new LiveShareSession(() => time);
        live.Start("player", 4, "first");
        await live.PrepareAsync("first", () => Task.FromResult(true));
        await live.BindAsync("first", _ => Task.FromResult(Invite()), _ => Task.CompletedTask);
        time = Now.AddHours(4);
        Assert.False(live.CanSend("first"));
        Assert.False(live.CanSendLobby);
        Assert.False(live.Snapshot.Requested);
        Assert.Equal("invite", live.Stop());
        Assert.False(new LiveShareSession(() => time).Snapshot.Requested);
    }

    [Fact]
    public async Task FailedPrivateRequestCannotReuseACachedPrivateAnswer()
    {
        var root = Path.Combine(Path.GetTempPath(), "live-share-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var receiver = new ShareSite();
            using var sender = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://localhost/replay/api/uploader/" }, receiver);
            var context = new string('a', 32);
            Assert.True(await sender.ApplyReplayChoiceAsync(context, new JsonObject { ["private"] = true, ["initialize"] = true }, default));
            Assert.True(sender.ReadReplay(context)["private"]!.GetValue<bool>());
            receiver.FailPrivacy = true;
            var live = new LiveShareSession(() => Now);
            live.Start("player", 4, context);
            Assert.False(await live.PrepareAsync(context, async () => await sender.ApplyReplayChoiceAsync(context,
                new JsonObject { ["private"] = true, ["initialize"] = true }, default) && sender.ReadReplay(context)["private"]!.GetValue<bool>()));
            Assert.False(live.CanSend(context));
            Assert.DoesNotContain(receiver.Paths, p => p.EndsWith("live/share"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MetadataRevokeStillWorksWhileRecordingPayloadIsBlockedAfterStop()
    {
        var root = Path.Combine(Path.GetTempPath(), "live-share-transport-" + Guid.NewGuid().ToString("N"));
        var context = new string('a', 32);
        var game = new GameEntry { Identity = "00000001-00000002", Context = context, Folder = Path.Combine(root, "session-test"), LastSeen = Now };
        Directory.CreateDirectory(game.Folder);
        new GameIndex(root).Save(game);
        File.WriteAllText(Path.Combine(game.Folder, "events.jsonl"), "{\"kind\":\"test\"}\n");
        try
        {
            var live = new LiveShareSession(() => Now);
            var receiver = new ShareSite();
            // Only control metadata is permitted until the separately checked recording gate opens.
            using var sender = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://localhost/replay/api/uploader/" }, receiver)
                { CanContactServer = () => true, CanUploadGame = g => live.CanSend(g.Context) };
            live.Start("player", 4, context);
            await sender.PassAsync(default);
            Assert.DoesNotContain(receiver.Paths, p => p.Contains("/chunks/"));
            await live.PrepareAsync(context, async () => await sender.ApplyReplayChoiceAsync(context, new JsonObject { ["private"] = true, ["initialize"] = true }, default));
            await sender.PassAsync(default);
            Assert.DoesNotContain(receiver.Paths, p => p.Contains("/chunks/"));
            await live.BindAsync(context, token => sender.BindLiveShareAsync(token, "player", 4, game.Identity, new string('b', 32), context, default), token => sender.RevokeLiveShareAsync(token, default));
            await sender.PassAsync(default);
            Assert.Contains(receiver.Paths, p => p.Contains("/chunks/"));
            int count = receiver.Paths.Count(p => p.Contains("/chunks/"));
            var revoked = live.Stop();
            File.AppendAllText(Path.Combine(game.Folder, "events.jsonl"), "{\"kind\":\"new-data\"}\n");
            await sender.RevokeLiveShareAsync(revoked!, default);
            await sender.PassAsync(default);
            Assert.Equal(count, receiver.Paths.Count(p => p.Contains("/chunks/")));
            Assert.Equal("create", receiver.SharePosts[0]["action"]!.GetValue<string>());
            Assert.Equal("player", receiver.SharePosts[0]["scope"]!.GetValue<string>());
            Assert.Equal(context, receiver.SharePosts[0]["context"]!.GetValue<string>());
            Assert.Equal(4, receiver.SharePosts[0]["durationHours"]!.GetValue<int>());
            Assert.Equal("revoke", receiver.SharePosts[^1]["action"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class ShareSite : HttpMessageHandler
    {
        public bool FailPrivacy;
        public List<string> Paths = [];
        public List<JsonObject> SharePosts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            if (path.EndsWith("/state") && FailPrivacy) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            if (path.EndsWith("live/share")) SharePosts.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            string body = path.EndsWith("register") ? "{\"id\":\"u_0123456789abcdef\",\"key\":\"u_0123456789abcdef.key\"}" : path.EndsWith("/state") ? "{\"private\":true}"
                : path.EndsWith("live/share") ? "{\"token\":\"invite\",\"url\":\"https://example.test/replay/live/share/invite\",\"expiresAt\":\"2026-10-29T16:00:00Z\"}" : "{\"stored\":true}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
