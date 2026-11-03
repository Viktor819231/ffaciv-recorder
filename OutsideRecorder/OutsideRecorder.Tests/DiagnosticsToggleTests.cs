namespace OutsideRecorderTests;

public sealed class DiagnosticsToggleTests
{
    [Fact]
    public void DisabledCollectionDoesNotWriteHistoryOrLookUpCrashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagnostics-toggle-" + Guid.NewGuid().ToString("N"));
        var time = DateTimeOffset.UtcNow;
        int lookups = 0;
        try {
            using var diagnostics = new SessionDiagnostics(root, "test", () => time,
                (_, _) => { lookups++; throw new Exception("Must not query crashes while disabled"); }, enabled: false);
            time += TimeSpan.FromMinutes(1);
            diagnostics.Observe(new ResourceSample { At = time, RecorderCpuPercent = 2 }, "sample");
            diagnostics.Activity("activity"); diagnostics.Error("test", new IOException());
            diagnostics.AddUpload(100); diagnostics.RequestStarted(); diagnostics.UploadFailed(); diagnostics.UploadDeferred();
            Assert.False(Directory.Exists(Path.Combine(root, "diagnostics")));
            var report = diagnostics.Export(Path.Combine(root, "manual-report.txt"));
            Assert.Contains("collection is off", File.ReadAllText(report));
            Assert.Equal(0, lookups);
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void TurningOffDiscardsBufferedWritesAndLeavesExistingHistoryIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagnostics-toggle-" + Guid.NewGuid().ToString("N"));
        var time = DateTimeOffset.UtcNow;
        try {
            using (var diagnostics = new SessionDiagnostics(root, "test", () => time, enabled: false)) {
                diagnostics.Enabled = true;
                time += TimeSpan.FromSeconds(16);
                diagnostics.Observe(new ResourceSample { At = time }, "collected sample");
                var path = Path.Combine(root, "diagnostics", "session-history.jsonl");
                Assert.True(File.Exists(path));
                var before = File.ReadAllText(path);
                diagnostics.Activity("buffered only");
                diagnostics.Enabled = false;
                time += TimeSpan.FromMinutes(1);
                diagnostics.Observe(new ResourceSample { At = time }, "must not collect");
                diagnostics.Dispose();
                Assert.Equal(before, File.ReadAllText(path));
            }
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
