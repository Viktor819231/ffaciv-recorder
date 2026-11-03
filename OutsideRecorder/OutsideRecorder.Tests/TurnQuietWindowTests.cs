namespace OutsideRecorderTests;

public sealed class TurnQuietWindowTests
{
    [Fact]
    public async Task TurnPauseKeepsQueuedLogsAndSavesUntilItsDeadline()
    {
        var root = Path.Combine(Path.GetTempPath(), "turn-quiet-queue-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "session-test");
        Directory.CreateDirectory(Path.Combine(folder, "saves"));
        try
        {
            var game = new GameEntry { Context = GameIndex.NewContext(), Identity = "test", Folder = folder };
            new GameIndex(root).Save(game);
            File.WriteAllBytes(Path.Combine(folder, "saves", "AutoSave_0010.Civ5Save"), new byte[64]);
            File.WriteAllText(Path.Combine(folder, "events.jsonl"), "{\"kind\":\"test\"}\n");
            long now = 100;
            var quiet = new TurnQuietWindow(5000, () => now);
            quiet.Observe(10);
            var site = new FakeSite();
            using var stream = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local", SaveUpload = "full", UploadKilobytesPerSecond = 0 }, site)
            { CanUpload = () => !quiet.IsQuiet, CanContactServer = () => !quiet.IsQuiet, Workload = new() };
            await stream.PassAsync(default);
            Assert.Equal(0, site.Requests);
            now = 5099;
            await stream.PassAsync(default);
            Assert.Equal(0, site.Requests);
            now = 5100;
            await stream.PassAsync(default);
            Assert.Single(site.Saves);
            Assert.Equal(File.ReadAllBytes(Path.Combine(folder, "events.jsonl")), site.Joined());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NewTurnPausesForFiveSecondsAndHeaderPollsDoNotExtendIt()
    {
        long now = 100;
        var quiet = new TurnQuietWindow(5000, () => now);
        Assert.False(quiet.IsQuiet);
        Assert.True(quiet.Observe(10));
        Assert.True(quiet.IsQuiet);
        now = 5099;
        Assert.False(quiet.Observe(10));
        Assert.True(quiet.IsQuiet);
        now = 5100;
        Assert.False(quiet.IsQuiet);
        Assert.False(quiet.Observe(10));
        Assert.False(quiet.IsQuiet);
    }

    [Theory]
    [InlineData(11)] [InlineData(3)]
    public void NextTurnAndReloadStartANewPause(int nextTurn)
    {
        long now = 100;
        var quiet = new TurnQuietWindow(5000, () => now);
        quiet.Observe(10);
        now = 4100;
        Assert.True(quiet.Observe(nextTurn));
        now = 9099;
        Assert.True(quiet.IsQuiet);
        now = 9100;
        Assert.False(quiet.IsQuiet);
    }

    [Fact]
    public void DetachReleasesTheUploadGateAndSameTurnInAnotherGamePausesAgain()
    {
        var quiet = new TurnQuietWindow(5000, () => 100);
        quiet.Observe(10);
        quiet.Reset();
        Assert.False(quiet.IsQuiet);
        Assert.True(quiet.Observe(10));
        Assert.True(quiet.IsQuiet);
    }

    [Fact]
    public void DisabledForOtherCaptureProfiles()
    {
        var quiet = new TurnQuietWindow(0, () => 100);
        Assert.False(quiet.Observe(10));
        Assert.False(quiet.IsQuiet);
    }
}
