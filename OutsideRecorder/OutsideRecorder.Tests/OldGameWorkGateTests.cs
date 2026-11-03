namespace OutsideRecorderTests;

public sealed class OldGameWorkGateTests
{
    [Fact]
    public async Task ExplicitManualUploadIgnoresAutomaticConsentButStillWaitsForResources()
    {
        var root = Path.Combine(Path.GetTempPath(), "manual-resource-consent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "AutoSave_0001.Civ5Save");
            var bytes = Enumerable.Range(0, 64).Select(n => (byte)n).ToArray();
            File.WriteAllBytes(file, bytes);
            var game = new OldGame { Id = "manual", Folder = root, Saves = [new OldSave
                { Path = file, Name = Path.GetFileName(file), TimeUtc = DateTime.UtcNow, Size = bytes.Length }] };
            var settings = new RecorderSettings { OutputFolder = Path.Combine(root, "output"), Stream = "local", SafeCapture = true, SaveUpload = "full", UploadKilobytesPerSecond = 0 };
            var workload = new WorkloadPolicy(); workload.SetQuiet(true);
            var receiver = new ManualSite();
            using var stream = new Streamer(settings, receiver) { CanUpload = () => false, CanUploadGame = _ => false, Workload = workload };
            var uploader = new OldGameUploader(settings, stream);
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Assert.ThrowsAsync<WorkDeferredException>(() => uploader.UploadAsync(game, new HashSet<string>(), null, default));
            Assert.Equal(0, receiver.Requests);
            workload.SetQuiet(false);
            Assert.Equal((1, 0), await uploader.UploadAsync(game, new HashSet<string>(), null, default));
            Assert.Equal(bytes, receiver.Save);
            Assert.True(receiver.Processed);
            Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class ManualSite : HttpMessageHandler
    {
        public int Requests;
        public byte[]? Save;
        public bool Processed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            string body = "{}";
            if (path.EndsWith("/register")) body = "{\"id\":\"test\",\"key\":\"test-key\"}";
            if (path.EndsWith("/saves")) { Save = await request.Content!.ReadAsByteArrayAsync(ct); body = "{\"id\":\"save\"}"; }
            if (path.EndsWith("/process")) Processed = true;
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public void FindAndGroupRefuseBeforeEnumeratingInputWhenPaused()
    {
        static IEnumerable<T> NeverEnumerate<T>() => Enumerable.Range(0, 1)
            .Select<int, T>(_ => throw new InvalidOperationException("Input must not be enumerated while paused."));
        int checks = 0;
        bool Paused() { checks++; return false; }
        Assert.Throws<WorkDeferredException>(() => OldGameScanner.Find(NeverEnumerate<string>(), canContinue: Paused));
        Assert.Throws<WorkDeferredException>(() => OldGameScanner.Group(NeverEnumerate<OldSave>(), Paused));
        Assert.Equal(2, checks);
    }

    [Fact]
    public void GroupRechecksBeforeOpeningNewestSaveAndDoesNotReturnPartialGames()
    {
        int checks = 0;
        var missing = new OldSave
        {
            Path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "AutoSave_0001.Civ5Save"),
            Name = "AutoSave_0001.Civ5Save", TimeUtc = DateTime.UtcNow, Size = 100,
        };
        Assert.Throws<WorkDeferredException>(() => OldGameScanner.Group([missing], () => ++checks < 4));
        Assert.Equal(4, checks);
        Assert.False(File.Exists(missing.Path));
    }

    [Fact]
    public void FindRechecksBeforeOpeningZip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "recorder-gated-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "not-an-archive.zip"), "This file must not be opened as an archive.");
            int checks = 0;
            Assert.Throws<WorkDeferredException>(() => OldGameScanner.Find([], [dir], () => ++checks < 4));
            Assert.Equal(4, checks);
        }
        finally { Directory.Delete(dir, true); }
    }
}
