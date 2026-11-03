using System.Security;

namespace OutsideRecorderTests;

public sealed class SessionDiagnosticsTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
    static CrashEvidenceResult NoCrashes(DateTimeOffset from, DateTimeOffset to) => new("No matching Civ crash event found", []);

    [Fact]
    public void ReportCountsWholeProcessSamplesUploadAttemptsAndUnknowns()
    {
        var dir = Temporary();
        try
        {
            var now = Start;
            using var diagnostics = new SessionDiagnostics(dir, "test", () => now, NoCrashes);
            diagnostics.Observe(new ResourceSample { At = now, RecorderCpuPercent = 1, WorkingSetBytes = 10485760, PeakWorkingSetBytes = 20971520 }, "Waiting for settled save");
            now += TimeSpan.FromSeconds(2);
            diagnostics.Observe(new ResourceSample { At = now, RecorderCpuPercent = 3, SystemCpuPercent = 90, WorkingSetBytes = 31457280 }, "Waiting for CPU");
            diagnostics.AddUpload(123); diagnostics.AddUpload(77); diagnostics.AddUpload(-1);
            var path = diagnostics.Export(Path.Combine(dir, "report.txt"));
            var report = File.ReadAllText(path);
            Assert.Contains("Recorder CPU: average 2.00%; highest sampled 3.00%", report);
            Assert.Contains("Recorder physical memory: average 20.00 MiB; highest sampled 30.00 MiB", report);
            Assert.Contains("Process peak physical memory: 20.0 MiB", report);
            Assert.Contains("Upload request-body bytes written: 200", report);
            Assert.Contains("private memory: unknown", report);
            Assert.Contains("recorder CPU 2, system CPU 1", report);
            Assert.Contains("Crash cause: undetermined", report);
            Assert.Contains("can miss brief spikes", report);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ArbitraryErrorsPathsAndCredentialsDoNotEnterReportOrHistory()
    {
        var dir = Temporary();
        try
        {
            using var diagnostics = new SessionDiagnostics(dir, "test", () => Start, NoCrashes);
            diagnostics.Activity("Upload", @"C:\Users\Personal\private.save https://example.test/?key=credential Bearer secret123 token=private456");
            diagnostics.Error("Upload", new IOException(@"secret password C:\private\secret.txt"));
            var report = File.ReadAllText(diagnostics.Export(Path.Combine(dir, "report.txt")));
            var history = File.ReadAllText(Path.Combine(dir, "diagnostics", "session-history.jsonl"));
            foreach (var text in new[] { report, history })
            {
                Assert.DoesNotContain("Personal", text); Assert.DoesNotContain("credential", text);
                Assert.DoesNotContain("secret123", text); Assert.DoesNotContain("private456", text);
                Assert.DoesNotContain("secret.txt", text); Assert.Contains("IOException", text);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoggingFailureDoesNotInterruptCaptureAndCrashLookupFailureStaysUnknown()
    {
        var dir = Temporary();
        try
        {
            var blocked = Path.Combine(dir, "not-a-directory"); File.WriteAllText(blocked, "x");
            var now = Start;
            using var diagnostics = new SessionDiagnostics(blocked, "test", () => now, (_, _) => throw new SecurityException("secret"));
            now += TimeSpan.FromSeconds(20);
            diagnostics.Observe(new ResourceSample { At = now }, "Waiting");
            var report = File.ReadAllText(diagnostics.Export(Path.Combine(dir, "report.txt")));
            Assert.Contains("could not be fully written", report);
            Assert.Contains("Windows crash records unavailable", report);
            Assert.DoesNotContain("secret", report);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PersistentHistoryAndReportRemainBoundedAcrossLongSessions()
    {
        var dir = Temporary();
        try
        {
            var now = Start;
            using var diagnostics = new SessionDiagnostics(dir, "test", () => now, NoCrashes);
            for (int i = 0; i < 6000; i++)
            {
                now += TimeSpan.FromSeconds(2);
                diagnostics.Observe(new ResourceSample { At = now, RecorderCpuPercent = 1, SystemCpuPercent = 50, WorkingSetBytes = 20000000 }, "Recording");
            }
            var path = diagnostics.Export(Path.Combine(dir, "report.txt"));
            Assert.True(new FileInfo(path).Length < 150000);
            var histories = Directory.GetFiles(Path.Combine(dir, "diagnostics"), "*.jsonl");
            Assert.Equal(2, histories.Length);
            Assert.All(histories, file => Assert.True(new FileInfo(file).Length <= 1024 * 1024));
            Assert.Contains("Resource observations: 6000", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UploadRatesRequestsFailuresAndDeferralsAreSeparate()
    {
        var dir = Temporary();
        try
        {
            var now = Start;
            using var diagnostics = new SessionDiagnostics(dir, "test", () => now, NoCrashes);
            now += TimeSpan.FromSeconds(10);
            diagnostics.RequestStarted(); diagnostics.AddUpload(10240);
            diagnostics.UploadFailed(); diagnostics.RequestStarted(); diagnostics.AddUpload(10240);
            diagnostics.UploadDeferred();
            now += TimeSpan.FromSeconds(10);
            var report = File.ReadAllText(diagnostics.Export(Path.Combine(dir, "report.txt")));
            Assert.Contains("recent 30-second window 1.00 KiB/s; session average 1.00 KiB/s; requests 2; failed 1; deferred 1", report);
            now += TimeSpan.FromSeconds(31);
            report = File.ReadAllText(diagnostics.Export(Path.Combine(dir, "report.txt")));
            Assert.Contains("recent 30-second window 0.00 KiB/s", report);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExportAfterRestartRetainsPreviousEvidenceWithoutCombiningTotals()
    {
        var dir = Temporary();
        try
        {
            var now = Start;
            using (var previous = new SessionDiagnostics(dir, "old", () => now, NoCrashes))
            {
                previous.Activity("Copying settled save"); previous.RequestStarted(); previous.AddUpload(500);
                previous.UploadFailed(); now += TimeSpan.FromSeconds(20);
                previous.Observe(new ResourceSample { At = now }, "Waiting for Civ to close");
            }
            now += TimeSpan.FromMinutes(1);
            DateTimeOffset? queriedFrom = null;
            using var current = new SessionDiagnostics(dir, "new", () => now, (from, to) => { queriedFrom = from; return NoCrashes(from, to); });
            var report = File.ReadAllText(current.Export(Path.Combine(dir, "report.txt")));
            Assert.Contains("Retained earlier-session observations (separate from current-session totals)", report);
            Assert.Contains("Copying settled save", report);
            Assert.Contains("Upload body bytes 500", report);
            Assert.Contains("Upload request-body bytes written: 0", report);
            Assert.Contains("WebView2 and other helper processes are excluded", report);
            Assert.Equal(Start, queriedFrom);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void InvalidCountersRemainUnknownAndCannotBreakDiagnosticSerialization()
    {
        var dir = Temporary();
        try
        {
            using var diagnostics = new SessionDiagnostics(dir, "test", () => Start, NoCrashes);
            diagnostics.Observe(new ResourceSample { At = Start, RecorderCpuPercent = double.NaN, SystemCpuPercent = double.PositiveInfinity, WorkingSetBytes = -1 }, "Waiting");
            var report = File.ReadAllText(diagnostics.Export(Path.Combine(dir, "report.txt")));
            Assert.Contains("Recorder CPU: unknown", report); Assert.Contains("System CPU: unknown", report);
            Assert.Contains("physical memory: unknown", report);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ConcurrentWorkersDoNotLoseUploadCounts()
    {
        var dir = Temporary();
        try
        {
            using var diagnostics = new SessionDiagnostics(dir, "test", () => Start, NoCrashes);
            Parallel.For(0, 1000, i => { diagnostics.AddUpload(10); diagnostics.Activity("Upload pending"); });
            var report = File.ReadAllText(diagnostics.Export(Path.Combine(dir, "report.txt")));
            Assert.Contains("Upload request-body bytes written: 10000", report);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("CivilizationV_DX11.exe", "Application Error", "1000", true)]
    [InlineData("unrelated.exe", "Application Error", "1000", false)]
    [InlineData("CivilizationV.exe", "Windows Error Reporting", "1001", false)]
    public void CrashParserRequiresMatchingApplicationFault(string app, string provider, string eventId, bool expected)
    {
        var xml = CrashXml(app, provider, eventId);
        var crash = WindowsCrashEvidence.Parse(xml, Start.AddMinutes(-1), Start.AddMinutes(1));
        Assert.Equal(expected, crash is not null);
        if (crash is not null)
        {
            Assert.Equal("game.dll", crash.Module); Assert.Equal("c0000005", crash.ExceptionCode);
            Assert.Equal(app, crash.Application);
        }
    }

    [Fact]
    public void CrashParserRejectsOldOrMalformedEventsAndDiscardsFullPaths()
    {
        Assert.Null(WindowsCrashEvidence.Parse("<broken>", Start, Start.AddMinutes(1)));
        Assert.Null(WindowsCrashEvidence.Parse(CrashXml("CivilizationV.exe", "Application Error", "1000"), Start.AddSeconds(1), Start.AddMinutes(1)));
        var crash = WindowsCrashEvidence.Parse(CrashXml(@"C:\Private\CivilizationV.exe", "Application Error", "1000"), Start.AddSeconds(-1), Start.AddMinutes(1));
        Assert.Equal("CivilizationV.exe", crash?.Application);
        Assert.Equal("game.dll", crash?.Module);
    }

    static string CrashXml(string app, string provider, string id) => $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Name="{provider}"/><EventID>{id}</EventID><TimeCreated SystemTime="2026-10-29T12:00:00.000Z"/></System><EventData><Data Name="AppName">{app}</Data><Data Name="ModuleName">C:\Private\game.dll</Data><Data Name="ExceptionCode">c0000005</Data></EventData></Event>
        """;
    static string Temporary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "recorder-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); return dir;
    }
}
