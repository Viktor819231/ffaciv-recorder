namespace OutsideRecorderTests;

public sealed class CaptureControlsTests
{
    [Fact]
    public async Task StoredSavesWaitThroughRestartThenUploadWithoutLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-controls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "AutoSave_0012.Civ5Save");
            File.WriteAllBytes(source, Enumerable.Range(0, 512).Select(x => (byte)x).ToArray());
            var capture = new SaveOnlyCapture(Path.Combine(root, "recordings"));
            var copy = capture.Copy(new AutosaveSeen(source, 512, File.GetLastWriteTimeUtc(source)));
            Assert.NotNull(copy);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copy!));
            Assert.False(File.Exists(Path.Combine(capture.Entry!.Folder, "events.jsonl")));
            Assert.False(File.Exists(Path.Combine(capture.Entry.Folder, "tape.bin.gz")));
            var settings = new RecorderSettings { OutputFolder = Path.Combine(root, "recordings"), Stream = "local", SaveUpload = "full" };
            bool playing = true;
            var policy = new UploadPolicy { AfterPlaying = true, IsPlaying = () => playing };
            var firstSite = new FakeSite();
            using (var stream = new Streamer(settings, firstSite) { CanUpload = policy.CanSend })
                await stream.PassAsync(default);
            Assert.Equal(0, firstSite.Requests);
            Assert.True(File.Exists(copy));
            var secondSite = new FakeSite();
            using (var stream = new Streamer(settings, secondSite) { CanUpload = policy.CanSend })
            {
                await stream.PassAsync(default);
                Assert.Equal(0, secondSite.Requests);
                playing = false;
                await stream.PassAsync(default);
                Assert.Single(secondSite.Saves);
                Assert.Empty(secondSite.Chunks);
                await stream.PassAsync(default);
                Assert.Single(secondSite.Saves);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ManualOnlyAndSuspendedSendersNeverFlushQueuedData()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-manual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = new UploadPolicy { Automatic = false, IsPlaying = () => false };
            var site = new FakeSite();
            using var stream = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local" }, site) { CanUpload = policy.CanSend };
            stream.Start();
            await stream.PassAsync(default);
            stream.Finish(TimeSpan.FromSeconds(1));
            Assert.Equal(0, site.Requests);
            stream.SuspendUploads();
            policy.Automatic = true;
            await stream.PassAsync(default);
            Assert.Equal(0, site.Requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SaveOnlySessionCopiesNewFilesAndManualOnlyStopsCopying()
    {
        var root = Path.Combine(Path.GetTempPath(), "recorder-no-memory-" + Guid.NewGuid().ToString("N"));
        var saves = Path.Combine(root, "source");
        Directory.CreateDirectory(saves);
        var output = Path.Combine(root, "output");
        try
        {
            using (var session = new RecorderSession(new RecorderSettings { OutputFolder = output, SaveFolders = [saves], TrackLogs = false, Tape = true }) { FindCiv = false })
            {
                session.Start();
                File.WriteAllBytes(Path.Combine(saves, "AutoSave_0001.Civ5Save"), new byte[32]);
                Assert.True(SpinWait.SpinUntil(() => session.Status.LastSaveAt is not null, 6000));
                Assert.Null(session.Attached);
                Assert.Equal(0, session.Status.Events);
                Assert.Equal(0, session.Status.LooksPerSecond);
                session.RecordingEnabled = false;
                Assert.True(SpinWait.SpinUntil(() => session.Status.Paused, 2000));
                File.WriteAllBytes(Path.Combine(saves, "AutoSave_0002.Civ5Save"), new byte[33]);
                await Task.Delay(2200);
                Assert.Single(Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(output, "events.jsonl", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(output, "tape*", SearchOption.AllDirectories));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CaptureSettingsRoundTrip()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "track_logs = off\nupload_timing = after\n");
            var settings = RecorderSettings.Load(file);
            Assert.False(settings.TrackLogs);
            Assert.Equal("after", settings.UploadTiming);
        }
        finally { File.Delete(file); }
    }
}
