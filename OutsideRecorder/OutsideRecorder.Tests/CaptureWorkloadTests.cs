using System.Reflection;

namespace OutsideRecorderTests;

public sealed class CaptureWorkloadTests
{
    static void Invoke(RecorderSession session, string method) =>
        typeof(RecorderSession).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(session, null);

    [Fact]
    public void ExperimentalCaptureClearsUploadHoldOnlyAfterPendingSaveIsCopied()
    {
        var root = Path.Combine(Path.GetTempPath(), "memory-copy-gate-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            var now = DateTime.UtcNow;
            var workload = new WorkloadPolicy();
            workload.SetQuiet(true, "Computer busy");
            using var session = new RecorderSession(new RecorderSettings { SafeCapture = true, OutputFolder = Path.Combine(root, "output"), SaveFolders = [source] }, () => now)
                { FindCiv = false, Workload = workload, AllowExperimentalMemoryLogging = true };
            session.TrackLogs = true;
            using var recording = new JsonlRecording(Path.Combine(root, "session"), "events.jsonl", () => 0);
            typeof(RecorderSession).GetField("rec", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, recording);
            File.WriteAllBytes(Path.Combine(source, "AutoSave_0001.Civ5Save"), new byte[32]);
            Invoke(session, "PollAutosaves");
            Assert.True(session.HasPendingAutosaves);
            now = now.AddSeconds(6);
            workload.SetQuiet(false);
            Invoke(session, "PollAutosaves");
            Assert.Single(Directory.GetFiles(recording.Folder, "*.Civ5Save", SearchOption.AllDirectories));
            Assert.False(session.HasPendingAutosaves);
            Assert.Contains("[]", File.ReadAllText(Path.Combine(root, "output", "pending-autosaves.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MetadataDeferralDoesNotConsumeNotificationsAndChangesMustSettleAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "watcher-held-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTime.UtcNow;
            var watcher = new AutosaveWatcher([root], settleFor: TimeSpan.FromSeconds(5), utcNow: () => now);
            var path = Path.Combine(root, "AutoSave_0001.Civ5Save");
            File.WriteAllBytes(path, new byte[32]);
            Assert.Empty(watcher.Poll(reportReady: false));
            now = now.AddSeconds(20);
            Assert.Empty(watcher.Poll(reportReady: false));
            File.AppendAllText(path, "more");
            Assert.Empty(watcher.Poll(reportReady: false));
            now = now.AddSeconds(4);
            Assert.Empty(watcher.Poll());
            now = now.AddSeconds(1);
            Assert.Equal(new FileInfo(path).Length, Assert.Single(watcher.Poll()).Size);
            Assert.Empty(watcher.Poll());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WatcherReportBudgetRetainsOtherReadyFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "watcher-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var watcher = new AutosaveWatcher([root]);
            foreach (var n in new[] { 1, 2, 3 }) File.WriteAllBytes(Path.Combine(root, $"AutoSave_{n:0000}.Civ5Save"), new byte[32]);
            Assert.Empty(watcher.Poll());
            var ready = Enumerable.Range(0, 3).SelectMany(_ => watcher.Poll(maxReports: 1)).ToArray();
            Assert.Equal(3, ready.Select(s => s.Path).Distinct().Count());
            Assert.Empty(watcher.Poll());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BusySessionPreservesReadySavesThenCopiesOnlyOnePerPoll()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-held-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        try
        {
            var workload = new WorkloadPolicy();
            workload.SetQuiet(true, "Computer busy");
            using var session = new RecorderSession(new RecorderSettings { OutputFolder = output, SaveFolders = [source], TrackLogs = false }) { FindCiv = false, Workload = workload };
            foreach (var n in new[] { 1, 2, 3 }) File.WriteAllBytes(Path.Combine(source, $"AutoSave_{n:0000}.Civ5Save"), new byte[32]);
            for (int i = 0; i < RecorderSession.CopyTries + 5; i++) Invoke(session, "PollSaveOnly");
            Assert.False(Directory.Exists(output));
            Assert.Contains("waiting", session.Status.State);
            Assert.True(session.HasPendingAutosaves);
            workload.SetQuiet(false);
            Invoke(session, "PollSaveOnly");
            Assert.Single(Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories));
            Assert.Equal(2, session.PendingAutosaves);
            Invoke(session, "PollSaveOnly");
            Invoke(session, "PollSaveOnly");
            Assert.Equal(3, Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories).Length);
            Assert.Equal(0, session.PendingAutosaves);
            Assert.False(session.HasPendingAutosaves);
            Assert.Null(session.Attached);
            Assert.Empty(Directory.GetFiles(output, "events.jsonl", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UploadGateWaitsUntilBothReadyAutosavesAreActuallyCopied()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-backlog-gate-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        try
        {
            var now = DateTime.UtcNow;
            using var session = new RecorderSession(new RecorderSettings { OutputFolder = output, SaveFolders = [source], SafeCapture = true }, () => now) { FindCiv = false };
            Assert.False(session.HasPendingAutosaves);
            foreach (var n in new[] { 1, 2 }) File.WriteAllBytes(Path.Combine(source, $"AutoSave_{n:0000}.Civ5Save"), new byte[32]);
            Invoke(session, "PollSaveOnly");
            Assert.True(session.HasPendingAutosaves); // settling metadata has not entered the retry queue
            Assert.Equal(0, session.PendingAutosaves);
            now = now.AddSeconds(5);
            Invoke(session, "PollSaveOnly");
            Assert.True(session.HasPendingAutosaves);
            Assert.Single(Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories));
            Invoke(session, "PollSaveOnly");
            Assert.False(session.HasPendingAutosaves);
            Assert.Equal(2, Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeliberatelyDisabledCaptureDoesNotLeaveUploadGateHeld()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-disabled-gate-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            var workload = new WorkloadPolicy();
            workload.SetQuiet(true);
            using var session = new RecorderSession(new RecorderSettings { OutputFolder = Path.Combine(root, "output"), SaveFolders = [source], SafeCapture = true }) { FindCiv = false, Workload = workload };
            File.WriteAllBytes(Path.Combine(source, "AutoSave_0001.Civ5Save"), new byte[32]);
            Invoke(session, "PollSaveOnly");
            Assert.True(session.HasPendingAutosaves);
            session.RecordingEnabled = false;
            Invoke(session, "PollSaveOnly");
            Assert.False(session.HasPendingAutosaves);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ResourcePauseDoesNotExhaustAnExistingCopyRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-retry-held-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        try
        {
            var workload = new WorkloadPolicy();
            using var session = new RecorderSession(new RecorderSettings { OutputFolder = output, SaveFolders = [source], TrackLogs = false }) { FindCiv = false, Workload = workload };
            var path = Path.Combine(source, "AutoSave_0001.Civ5Save");
            File.WriteAllBytes(path, new byte[32]);
            Invoke(session, "PollSaveOnly");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Invoke(session, "PollSaveOnly");
                Assert.Equal(1, session.PendingAutosaves);
                workload.SetQuiet(true);
                for (int i = 0; i < RecorderSession.CopyTries + 5; i++) Invoke(session, "PollSaveOnly");
                Assert.Equal(1, session.PendingAutosaves);
            }
            workload.SetQuiet(false);
            Invoke(session, "PollSaveOnly");
            Assert.Single(Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories));
            Assert.Equal(0, session.PendingAutosaves);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MemoryBudgetGateRunsBeforeSearchOrAnyMemoryAccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "memory-held-" + Guid.NewGuid().ToString("N"));
        var workload = new WorkloadPolicy();
        workload.SetQuiet(true, "Computer busy");
        using var session = new RecorderSession(new RecorderSettings { OutputFolder = root, SaveFolders = [] }) { FindCiv = false, Workload = workload };
        // Sample has no ProcessMemory/reader at all: getting past the gate would fail immediately.
        Invoke(session, "Sample");
        Invoke(session, "Search");
        Assert.Null(session.Attached);
        Assert.Equal("Computer busy", session.Status.State);
        Assert.Equal(0, session.Status.LooksPerSecond);
    }

    [Fact]
    public void LocalReportIncludesActualCopyAndDeferralActivityWithoutSourcePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-activity-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        try
        {
            using var diagnostics = new SessionDiagnostics(output, "test", crashEvidence: (_, _) => new("No test crash records", []));
            var workload = new WorkloadPolicy();
            using var session = new RecorderSession(new RecorderSettings { OutputFolder = output, SaveFolders = [source], TrackLogs = false })
                { FindCiv = false, Workload = workload, Diagnostics = diagnostics };
            File.WriteAllBytes(Path.Combine(source, "AutoSave_0001.Civ5Save"), new byte[32]);
            workload.SetQuiet(true, "Computer busy");
            Invoke(session, "PollSaveOnly");
            Invoke(session, "PollSaveOnly");
            workload.SetQuiet(false);
            Invoke(session, "PollSaveOnly");
            var report = File.ReadAllText(diagnostics.Export(Path.Combine(root, "report.txt")));
            Assert.Contains("Autosave copying deferred", report);
            Assert.Contains("Copying a settled autosave", report);
            Assert.Contains("Autosave copy completed", report);
            Assert.DoesNotContain(source, report);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeferredObservedSaveSurvivesRestartAndResettlesWithoutCollectingUnrelatedOldSaves()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-restart-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        try
        {
            var now = DateTime.UtcNow;
            var settings = new RecorderSettings { OutputFolder = output, SaveFolders = [source], SafeCapture = true };
            var workload = new WorkloadPolicy();
            workload.SetQuiet(true);
            using (var first = new RecorderSession(settings, () => now) { FindCiv = false, Workload = workload })
            {
                File.WriteAllBytes(Path.Combine(source, "AutoSave_0001.Civ5Save"), new byte[32]);
                Invoke(first, "PollSaveOnly");
                now = now.AddSeconds(10);
                Invoke(first, "PollSaveOnly");
            }
            var manifest = Path.Combine(output, "pending-autosaves.json");
            Assert.Contains("AutoSave_0001", File.ReadAllText(manifest));
            File.WriteAllBytes(Path.Combine(source, "AutoSave_0099.Civ5Save"), new byte[32]);
            using (var resumed = new RecorderSession(settings, () => now) { FindCiv = false })
            {
                Assert.True(resumed.HasPendingAutosaves);
                Invoke(resumed, "PollSaveOnly");
                Assert.Empty(Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories));
                now = now.AddSeconds(4);
                Invoke(resumed, "PollSaveOnly");
                Assert.Empty(Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories));
                now = now.AddSeconds(1);
                Invoke(resumed, "PollSaveOnly");
                Assert.EndsWith("AutoSave_0001.Civ5Save", Assert.Single(Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories)));
                Assert.Equal("[]", File.ReadAllText(manifest));
                Assert.False(resumed.TrackLogs);
                Assert.Null(resumed.Attached);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FailedCopyRetryAlsoSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-retry-restart-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        try
        {
            var now = DateTime.UtcNow;
            var settings = new RecorderSettings { OutputFolder = output, SaveFolders = [source], SafeCapture = true };
            var path = Path.Combine(source, "AutoSave_0001.Civ5Save");
            using (var first = new RecorderSession(settings, () => now) { FindCiv = false })
            {
                File.WriteAllBytes(path, new byte[32]);
                Invoke(first, "PollSaveOnly");
                now = now.AddSeconds(5);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Invoke(first, "PollSaveOnly");
                Assert.Equal(1, first.PendingAutosaves);
            }
            Assert.Contains("\"Tries\":1", File.ReadAllText(Path.Combine(output, "pending-autosaves.json")));
            using (var resumed = new RecorderSession(settings, () => now) { FindCiv = false })
            {
                Assert.True(resumed.HasPendingAutosaves);
                Invoke(resumed, "PollSaveOnly");
                Assert.Empty(Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories));
                now = now.AddSeconds(5);
                Invoke(resumed, "PollSaveOnly");
                Assert.Single(Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ChangedSourceAndUnwatchedPathsAreNotRecoveredFromManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-stale-recovery-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            var now = DateTime.UtcNow;
            var path = Path.Combine(source, "AutoSave_0001.Civ5Save");
            var outside = Path.Combine(root, "AutoSave_0002.Civ5Save");
            File.WriteAllBytes(path, new byte[32]); File.WriteAllBytes(outside, new byte[32]);
            var stale = new AutosaveSeen(path, 32, File.GetLastWriteTimeUtc(path));
            var untrusted = new AutosaveSeen(outside, 32, File.GetLastWriteTimeUtc(outside));
            File.AppendAllText(path, "changed");
            var watcher = new AutosaveWatcher([source], settleFor: TimeSpan.FromSeconds(5), utcNow: () => now);
            Assert.Empty(watcher.RestorePending([stale, untrusted]));
            now = now.AddMinutes(1);
            Assert.Empty(watcher.Poll());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CorruptPendingManifestDoesNotCrashOrCollectUnrelatedExistingSaves()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-corrupt-recovery-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(source); Directory.CreateDirectory(output);
        try
        {
            File.WriteAllText(Path.Combine(output, "pending-autosaves.json"), "{broken");
            File.WriteAllBytes(Path.Combine(source, "AutoSave_0001.Civ5Save"), new byte[32]);
            using var session = new RecorderSession(new RecorderSettings { OutputFolder = output, SaveFolders = [source], SafeCapture = true }) { FindCiv = false };
            Invoke(session, "PollSaveOnly");
            Assert.Contains("invalid", session.Status.LastError);
            Assert.Empty(Directory.GetFiles(output, "*.Civ5Save", SearchOption.AllDirectories));
            Assert.False(session.TrackLogs);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CaptureGapStartsAFreshBaselineWithoutInventedMovesOrDeaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var rec = new JsonlRecording(root, "events.jsonl", () => 0))
            {
                var pipe = new GamePipeline(rec, null, true);
                GameSnapshot Snapshot(int x) => new()
                {
                    Turn = 10, TimeMs = 0, ActivePlayer = 0, MapWidth = 80, MapHeight = 52,
                    Players = [new PlayerState { Id = 0, Alive = true, TurnActive = true }],
                    Units = [new UnitState { Owner = 0, Id = 1, Type = 1, X = x, Y = 1, Damage = 0, Moves = 60, FortifyTurns = 0, Experience = 0, Level = 1, TurnCreated = 0, LastMoveTurn = 0 }], Cities = [],
                };
                pipe.Step(SampleStatus.Ok, Snapshot(1), 0);
                pipe.Step(SampleStatus.Ok, Snapshot(1), 100);
                pipe.ResetAfterCaptureGap();
                pipe.Step(SampleStatus.Ok, Snapshot(12), 200);
                pipe.Step(SampleStatus.Ok, Snapshot(12), 300);
                rec.Flush();
            }
            var lines = File.ReadAllLines(Path.Combine(root, "events.jsonl"));
            Assert.Contains(lines, l => l.Contains("capture_gap"));
            Assert.DoesNotContain(lines, l => l.Contains("UnitSetXY") || l.Contains("UnitRemoved") || l.Contains("UnitCreated"));
        }
        finally { Directory.Delete(root, true); }
    }
}
