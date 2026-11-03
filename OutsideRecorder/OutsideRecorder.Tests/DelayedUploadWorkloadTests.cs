namespace OutsideRecorderTests;

/// <summary>Local upload consent and resource permissions are independent; neither can override the other.</summary>
public sealed class DelayedUploadWorkloadTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "recorder-combined-gates-" + Guid.NewGuid().ToString("N"));

    GameEntry Setup()
    {
        var game = new GameEntry { Identity = "files:test", Context = GameIndex.NewContext(), Folder = Path.Combine(root, "session-test") };
        Directory.CreateDirectory(Path.Combine(game.Folder, "saves"));
        File.WriteAllBytes(Path.Combine(game.Folder, "saves", "AutoSave_0001.Civ5Save"), new byte[64]);
        File.WriteAllText(Path.Combine(game.Folder, "events.jsonl"), "{\"kind\":\"test\"}\n");
        new GameIndex(root).Save(game);
        return game;
    }

    RecorderSettings Settings => new() { OutputFolder = root, Stream = "local", SafeCapture = true, SaveUpload = "full", UploadKilobytesPerSecond = 0 };

    [Fact]
    public async Task UnknownOrLocalDecisionCannotCreateCompletionOrReadLockedCaptureFiles()
    {
        var game = Setup(); var decisions = new DelayedUploads(root); var site = new FakeSite();
        using var stream = new Streamer(Settings, site) { CanUploadGame = decisions.CanUpload, Workload = new() };
        using var lockedSave = new FileStream(Path.Combine(game.Folder, "saves", "AutoSave_0001.Civ5Save"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var lockedEvents = new FileStream(Path.Combine(game.Folder, "events.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await stream.PassAsync(default);
        decisions.KeepLocal(game.Context);
        await stream.PassAsync(default);
        Assert.Empty(site.Saves); Assert.Empty(site.Chunks);
        Assert.Empty(stream.ReadReplay(game.Context, true));
        Assert.False(File.Exists(Path.Combine(game.Folder, "stream.json")));
    }

    [Fact]
    public async Task ExplicitUploadCannotOverrideQuietOrGameIdleGateAndResumesWhenBothPermit()
    {
        var game = Setup(); var decisions = new DelayedUploads(root); decisions.UploadNow(game.Context);
        var policy = new WorkloadPolicy(); policy.SetQuiet(true);
        var site = new FakeSite(); bool idle = false;
        using var stream = new Streamer(Settings, site)
        { CanUploadGame = decisions.CanUpload, Workload = policy, CanContactServer = () => idle };
        await stream.PassAsync(default);
        Assert.Equal(0, site.Requests); Assert.Empty(stream.ReadReplay(game.Context, true));
        policy.SetQuiet(false);
        await Assert.ThrowsAsync<WorkDeferredException>(() => stream.SavesPassAsync(default));
        Assert.Equal(0, site.Requests); Assert.Empty(stream.ReadReplay(game.Context, true));
        idle = true;
        await stream.PassAsync(default);
        Assert.Single(site.Saves); Assert.Single(site.Chunks);
        Assert.True(File.Exists(Path.Combine(game.Folder, "saves", "AutoSave_0001.Civ5Save")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
