namespace OutsideRecorderTests;

public sealed class MemoryTestSettingsTests
{
    [Fact]
    public void SafeDesktopNeedsBothRuntimePermissionAndLoggingChoiceAndRevocationStopsIt()
    {
        var output = Path.Combine(Path.GetTempPath(), "memory-permission-" + Guid.NewGuid().ToString("N"));
        var settings = new RecorderSettings { SafeCapture = true, TrackLogs = true, Tape = false, SaveFolders = [], OutputFolder = output };
        using var session = new RecorderSession(settings) { FindCiv = false };
        Assert.False(session.AllowExperimentalMemoryLogging);
        session.TrackLogs = true;
        Assert.False(session.TrackLogs);
        session.AllowExperimentalMemoryLogging = true;
        Assert.False(session.TrackLogs); // Permission by itself does not start capture.
        Assert.Equal(250, session.SampleMs);
        session.TrackLogs = true;
        Assert.True(session.TrackLogs);
        session.TrackLogs = false;
        Assert.False(session.TrackLogs);
        session.TrackLogs = true;
        session.AllowExperimentalMemoryLogging = false;
        Assert.False(session.TrackLogs);
        Assert.False(session.TurnTransitionQuiet);
        Assert.True(settings.SafeCapture);
        // With logging off, sampling returns before it can access an attached process/reader.
        typeof(RecorderSession).GetMethod("Sample", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(session, null);
        Assert.Null(session.Attached);
    }

    [Fact]
    public void MemoryExperimentOverridesOlderHeavySettingsButKeepsDestination()
    {
        var settings = new RecorderSettings { SampleMs = 50, Tape = true, SaveUpload = "delta",
            StreamUrl = "https://test.example/api/uploader/", OutputFolder = "test-output" }.ForDesktop(true);
        Assert.True(settings.MemoryTest);
        Assert.False(settings.SafeCapture);
        Assert.True(settings.TrackLogs);
        Assert.Equal(250, settings.SampleMs);
        Assert.False(settings.Tape);
        Assert.Equal("full", settings.SaveUpload);
        Assert.Equal("during", settings.UploadTiming);
        Assert.Equal("test-output", settings.OutputFolder);
        Assert.Equal("https://test.example/api/uploader/", settings.StreamUrl);
        using var session = new RecorderSession(settings with { SaveFolders = [], OutputFolder = Path.GetTempPath() }) { FindCiv = false };
        Assert.Equal(250, session.SampleMs);
        Assert.True(session.TrackLogs);
        session.TrackLogs = false;
        Assert.False(session.TrackLogs);
        session.TrackLogs = true;
        Assert.True(session.TrackLogs);
    }

    [Fact]
    public void NormalDesktopLaunchRemainsSaveOnly()
    {
        var settings = new RecorderSettings { TrackLogs = true, UploadTiming = "during" }.ForDesktop(false);
        Assert.True(settings.SafeCapture);
        Assert.False(settings.MemoryTest);
        Assert.False(settings.TrackLogs);
        Assert.False(settings.Tape);
        Assert.False(settings.SnapshotEachTurn);
        Assert.Equal("after", settings.UploadTiming);
        Assert.Equal("full", settings.SaveUpload);
        Assert.True(settings.KeepCopies);
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(2, 2)] [InlineData(4, 4)]
    [InlineData(5, 5)] [InlineData(10, 10)] [InlineData(20, 20)]
    [InlineData(0, 4)] [InlineData(-1, 4)] [InlineData(100, 4)]
    public void RatesAreBoundedAndFourIsTheFallback(int value, int expected) =>
        Assert.Equal(expected, RecorderSettings.NormalizeLooksPerSecond(value));

    [Theory]
    [InlineData(2, 250)] [InlineData(25, 250)] [InlineData(40, 400)]
    public void FourHzCanSlowDownButNeverSpeedsUp(double readMs, int expectedInterval) =>
        Assert.Equal(expectedInterval, RecorderSession.IntervalFor(250, readMs));
}
