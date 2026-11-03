using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace OutsideRecorderTests;

public sealed class UploadBudgetTests
{
    [Fact]
    public async Task BodyIsPreservedAndOnlyWrittenBytesAreCounted()
    {
        var bytes = Enumerable.Range(0, 100000).Select(i => (byte)i).ToArray();
        long counted = 0;
        var budget = new UploadBudget { Written = n => counted += n };
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var wrapped = budget.Wrap(content);
        Assert.Equal(bytes.Length, wrapped.Headers.ContentLength);
        Assert.Equal("application/octet-stream", wrapped.Headers.ContentType!.MediaType);
        using var output = new MemoryStream();
        await wrapped.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
        Assert.Equal(bytes.Length, counted);
    }

    [Fact]
    public async Task AGameBecomingBusyStopsAtTheNextBoundedWrite()
    {
        long counted = 0;
        var budget = new UploadBudget { CanContinue = () => counted == 0, Written = n => counted += n };
        using var wrapped = budget.Wrap(new ByteArrayContent(new byte[100000]));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<WorkDeferredException>(() => wrapped.CopyToAsync(output));
        Assert.Equal(32768, counted);
        Assert.Equal(counted, output.Length);
    }

    [Fact]
    public async Task BandwidthWaitIsCancellableWithoutSendingTheRest()
    {
        long counted = 0;
        using var cancel = new CancellationTokenSource();
        var budget = new UploadBudget { BytesPerSecond = 1, Written = n => { counted += n; cancel.Cancel(); } };
        using var wrapped = budget.Wrap(new ByteArrayContent(new byte[100000]));
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrapped.CopyToAsync(output, cancel.Token));
        Assert.Equal(32768, counted);
    }

    [Fact]
    public async Task QuietPolicyBlocksAllAutomaticWorkBeforeRegistration()
    {
        var root = Path.Combine(Path.GetTempPath(), "quiet-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = new WorkloadPolicy(); policy.SetQuiet(true);
            var site = new FakeSite();
            using var stream = new Streamer(new RecorderSettings { OutputFolder = root, Stream = "local" }, site) { Workload = policy };
            await stream.PassAsync(default);
            Assert.Equal(0, site.Requests);
            Assert.False(File.Exists(Path.Combine(root, "install.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InterruptedBodyIsRetriedWithoutAcknowledgingOrDeletingTheSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "paced-retry-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "session-test"); Directory.CreateDirectory(Path.Combine(folder, "saves"));
        try
        {
            var file = Path.Combine(folder, "saves", "AutoSave_0001.Civ5Save");
            var bytes = new byte[100000]; new Random(3).NextBytes(bytes); File.WriteAllBytes(file, bytes);
            new GameIndex(root).Save(new GameEntry { Identity = "test", Context = GameIndex.NewContext(), Folder = folder, LastSeen = DateTimeOffset.UtcNow });
            var policy = new WorkloadPolicy();
            var site = new BodySite(policy);
            var settings = new RecorderSettings { OutputFolder = root, Stream = "local", SafeCapture = true, SaveUpload = "full", UploadKilobytesPerSecond = 0 };
            using var diagnostics = new SessionDiagnostics(root, "test", crashEvidence: (_, _) => new("test", []));
            using var stream = new Streamer(settings, site) { Workload = policy, Diagnostics = diagnostics };
            await Assert.ThrowsAsync<WorkDeferredException>(() => stream.SavesPassAsync(default));
            Assert.True(File.Exists(file));
            Assert.DoesNotContain("AutoSave_0001", File.Exists(Path.Combine(folder, "stream.json")) ? File.ReadAllText(Path.Combine(folder, "stream.json")) : "");
            policy.SetQuiet(false); site.Pause = false;
            await stream.SavesPassAsync(default);
            Assert.Equal(bytes, site.Completed);
            Assert.Contains("AutoSave_0001", File.ReadAllText(Path.Combine(folder, "stream.json")));
            Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparationBatchHoldsTheOtherWorkerUntilItsRequestCompletes(bool safeCapture)
    {
        var root = Path.Combine(Path.GetTempPath(), "one-job-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "session-test"); Directory.CreateDirectory(Path.Combine(folder, "saves"));
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "saves", "AutoSave_0001.Civ5Save"), new byte[500]);
            File.WriteAllText(Path.Combine(folder, "events.jsonl"), "{\"kind\":\"test\"}\n");
            new GameIndex(root).Save(new GameEntry { Identity = "test", Context = GameIndex.NewContext(), Folder = folder, LastSeen = DateTimeOffset.UtcNow });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var site = new BodySite(new WorkloadPolicy()) { Pause = false, SaveEntered = entered, ContinueSave = release };
            using var stream = new Streamer(new RecorderSettings { OutputFolder = root, SafeCapture = safeCapture, Stream = "local", SaveUpload = "full", UploadKilobytesPerSecond = 0 }, site);
            var saves = stream.SavesPassAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = new CancellationTokenSource();
            var events = stream.EventsPassAsync(cancel.Token);
            Assert.False(events.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events);
            release.SetResult();
            await saves;
            await stream.EventsPassAsync(default); // the cancelled waiter did not leak the worker lease
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class BodySite(WorkloadPolicy policy) : HttpMessageHandler
    {
        public bool Pause = true;
        public byte[]? Completed;
        public TaskCompletionSource? SaveEntered, ContinueSave;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            string json = "{}";
            if (path.EndsWith("/register")) json = "{\"id\":\"test\",\"key\":\"test-key\"}";
            if (path.EndsWith("/saves"))
            {
                SaveEntered?.TrySetResult();
                if (ContinueSave is { } resume) await resume.Task.WaitAsync(ct);
                using var target = new PausingStream(() => { if (Pause) policy.SetQuiet(true); });
                await request.Content!.CopyToAsync(target, ct);
                Completed = target.ToArray(); json = "{\"id\":\"saved\"}";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    sealed class PausingStream(Action afterWrite) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { await base.WriteAsync(buffer, ct); afterWrite(); }
    }
}
