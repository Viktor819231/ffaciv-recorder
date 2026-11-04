using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace OutsideRecorderTests;

public sealed class ReplayControlTests
{
    [Theory]
    [InlineData("{}", 1)]
    [InlineData("{\"recorder\":{\"lifecycle\":true}}", 2)]
    public async Task TheUserBuildWaitsForServerRetentionSupportBeforeUploading(string config, int controlsVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var receiver = new Receiver(config);
        try
        {
            using var streamer = new Streamer(new RecorderSettings
            {
                OutputFolder = root, Stream = "local", StreamUrl = "http://localhost/replay/api/uploader/", RequireReplayLifecycle = true, RequiredControlsVersion = controlsVersion,
            }, receiver);
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => streamer.PassAsync(default));
            Assert.Contains("website needs the recorder update", error.Message);
            Assert.All(receiver.Calls, call => Assert.StartsWith("GET ", call));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SavefileOnlySkipsMovementLogsAndCanResumeWithAFreshSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-save-only-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var rec = new JsonlRecording(root, "events.jsonl", () => 0, false))
            {
                var pipeline = new GamePipeline(rec, null, true) { CaptureMoves = false };
                var snap = new GameSnapshot
                {
                    TimeMs = 0, Turn = 50, ActivePlayer = 0, MapWidth = 40, MapHeight = 24, MapSeed = 1, SyncSeed = 2,
                    Players = [new PlayerState { Id = 0, Alive = true, TurnActive = true }],
                    Units = [new UnitState { Owner = 0, Id = 1, Type = 1, X = 1, Y = 1, Damage = 0, Moves = 60, FortifyTurns = 0, Experience = 0, Level = 1, TurnCreated = 0, LastMoveTurn = 0 }],
                    Cities = [],
                };
                pipeline.Step(SampleStatus.Ok, snap, 0);
                pipeline.Step(SampleStatus.Ok, snap, 200);
                rec.Flush();
                Assert.Equal(0, new FileInfo(Path.Combine(root, "events.jsonl")).Length);
                Assert.Equal(50, pipeline.LastTurn);
                pipeline.CaptureMoves = true;
                pipeline.Step(SampleStatus.Ok, snap with { Turn = 51 }, 1000);
            }
            Assert.Contains("\"kind\":\"snapshot\"", File.ReadAllText(Path.Combine(root, "events.jsonl")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExplicitPrivacyAppliesWhileAutomaticUploadsAreHeld()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-held-choice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var receiver = new Receiver();
            using var streamer = new Streamer(new RecorderSettings { OutputFolder=root, Stream="local" },receiver) { CanUpload=()=>false };
            var context = new string('a',32);
            await streamer.ApplyReplayChoiceAsync(context,new JsonObject { ["private"]=true },default);
            Assert.Contains(receiver.Calls,c=>c.StartsWith("POST") && c.EndsWith("/state"));
            Assert.DoesNotContain(receiver.Calls,c=>c.EndsWith("/saves") || c.Contains("/chunks"));
            Assert.Empty(streamer.ReadReplay(context,true));
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }

    // The user, 2026-11-04: "when i make public its actually will be made public". A livestreamed game's Make public waited
    // with its finish for Civ's main menu, so the site copy stayed private (and a short game's keep came too late).
    [Fact]
    public async Task MakePublicGoesOutWhileServerWorkWaitsAndTheFinishStaysQueued()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-public-now-" + Guid.NewGuid().ToString("N"));
        try
        {
            var receiver = new Receiver();
            using var streamer = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local" }, receiver) { CanContactServer = () => false };
            var context = new string('b', 32);
            streamer.ChooseReplay(context, new JsonObject { ["action"] = "complete", ["keepShort"] = true, ["waitForEnd"] = true, ["private"] = false });
            Assert.True(await streamer.ApplyReplayChoiceAsync(context, new JsonObject { ["private"] = false, ["keepShort"] = true }, default));
            var sent = Assert.Single(receiver.StatePosts);
            Assert.False(sent["private"]!.GetValue<bool>());
            Assert.Null(sent["action"]);
            var pending = streamer.ReadReplay(context, true);
            Assert.Equal("complete", pending["action"]!.GetValue<string>());
            Assert.Null(pending["private"]);
            // only that call was let through: other server work still waits for Civ's main menu
            await Assert.ThrowsAsync<HttpRequestException>(() => streamer.GetReplayLibraryAsync(default));
            Assert.DoesNotContain(receiver.Calls, c => c.EndsWith("/games"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedInitializerRetainsPrivacyWithoutPoisoningLaterCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-initialize-choice-" + Guid.NewGuid().ToString("N"));
        var context = new string('a', 32);
        var game = new GameEntry { Context = context, Identity = "files:" + context, Folder = Path.Combine(root, "session-test") };
        Directory.CreateDirectory(game.Folder);
        new GameIndex(root).Save(game);
        try
        {
            var receiver = new Receiver { FailNextState = true };
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local" };
            using (var first = new Streamer(settings, receiver))
            {
                var initialize = new JsonObject { ["initialize"] = true, ["private"] = true };
                await first.ApplyReplayChoiceAsync(context, initialize, default);
                Assert.Contains("HTTP 503",first.ReplayChoiceError);
                Assert.True(initialize["initialize"]!.GetValue<bool>()); // Caller command remains exact for the POST.
                Assert.True(first.ReadReplay(context, true)["private"]!.GetValue<bool>());
                Assert.Null(first.ReadReplay(context, true)["initialize"]);
                var direct = Assert.Single(receiver.StatePosts);
                Assert.True(direct["initialize"]!.GetValue<bool>());
                Assert.Equal(2, direct.Count);
            }
            var resumedReceiver = new Receiver();
            using var resumed = new Streamer(settings, resumedReceiver);
            resumed.ChooseReplay(context, new JsonObject { ["action"] = "complete", ["keepShort"] = true });
            Assert.Null(resumed.ReadReplay(context, true)["initialize"]);
            await resumed.PassAsync(default);
            var completion = Assert.Single(resumedReceiver.StatePosts);
            Assert.Equal("complete", completion["action"]!.GetValue<string>());
            Assert.True(completion["private"]!.GetValue<bool>());
            Assert.True(completion["keepShort"]!.GetValue<bool>());
            Assert.Null(completion["initialize"]);
            Assert.Empty(resumed.ReadReplay(context, true));
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class Receiver(string config = "{}") : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        public readonly List<JsonObject> StatePosts = [];
        public bool Down;
        public bool FailNextState;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("Offline");
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.Method + " " + path);
            if (request.Method == HttpMethod.Post && path.EndsWith("/state"))
            {
                StatePosts.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
                if (FailNextState)
                {
                    FailNextState = false;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
                }
            }
            var answer = path.EndsWith("/config") ? config : path.EndsWith("register") ? "{\"id\":\"u_0123456789abcdef\",\"key\":\"u_0123456789abcdef.key\"}"
                : path.EndsWith("/state") ? "{\"state\":\"processing\",\"saves\":1,\"processed\":0,\"url\":null}"
                : path.EndsWith("/saves") ? "{\"id\":\"20261028120000-12345678\"}" : "{\"stored\":true}";
            if (request.Content is not null) await request.Content.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) };
        }
    }

    [Fact]
    public async Task DoneSurvivesRestartAndWaitsUntilSavesAndLogsAreAcknowledged()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-completion-" + Guid.NewGuid().ToString("N"));
        var game = new GameEntry { Identity = "00000001-00000002", Context = new string('a', 32), Folder = Path.Combine(root, "session-test"), LastSeen = DateTimeOffset.UtcNow };
        Directory.CreateDirectory(Path.Combine(game.Folder, "saves"));
        new GameIndex(root).Save(game);
        File.WriteAllText(Path.Combine(game.Folder, "events.jsonl"), "{\"kind\":\"lifecycle\",\"sequence\":1}\n");
        File.WriteAllBytes(Path.Combine(game.Folder, "saves", "AutoSave_0050 AD-1000.Civ5Save"), Encoding.UTF8.GetBytes("opaque save bytes"));
        var settings = new RecorderSettings { OutputFolder = root, Stream = "local", StreamUrl = "http://localhost/replay/api/uploader/" };
        try
        {
            using (var first = new Streamer(settings, new Receiver { Down = true })) first.ChooseReplay(game.Context, new JsonObject { ["action"] = "complete", ["waitForEnd"] = true });
            var receiver = new Receiver();
            using var resumed = new Streamer(settings, receiver);
            Assert.Equal("complete", resumed.ReadReplay(game.Context, true)["action"]!.GetValue<string>());
            await resumed.PassAsync(default);
            Assert.DoesNotContain(receiver.Calls, c => c.StartsWith("POST") && c.EndsWith("/state"));
            Assert.NotEmpty(resumed.ReadReplay(game.Context, true));
            File.AppendAllText(Path.Combine(game.Folder, "events.jsonl"), "{\"kind\":\"lifecycle\",\"sequence\":2,\"data\":{\"state\":\"paused\"}}\n");
            resumed.ChooseReplay(game.Context, new JsonObject { ["action"] = "complete", ["waitForEnd"] = true });
            await resumed.PassAsync(default);
            int save = receiver.Calls.FindIndex(c => c.EndsWith("/saves"));
            int done = receiver.Calls.FindIndex(c => c.StartsWith("POST") && c.EndsWith("/state"));
            Assert.True(save >= 0 && done > save);
            Assert.Contains(receiver.Calls.Take(save), c => c.Contains("/chunks/0"));
            Assert.Empty(resumed.ReadReplay(game.Context, true));
            Assert.Equal("processing", resumed.ReadReplay(game.Context)["state"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }
}
