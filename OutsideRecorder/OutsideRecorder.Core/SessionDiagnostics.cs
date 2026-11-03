using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OutsideRecorderCore;

/// <summary>Small local resource history and a user-exported report; diagnostic failures never stop recording.</summary>
public sealed class SessionDiagnostics : IDisposable
{
    const int MaxEntries = 240, MaxPending = 128, MaxLogBytes = 1024 * 1024;
    readonly object gate = new();
    readonly string directory, version;
    readonly DateTimeOffset started;
    readonly Func<DateTimeOffset> clock;
    readonly Func<DateTimeOffset, DateTimeOffset, CrashEvidenceResult> crashEvidence;
    readonly Queue<DiagnosticEntry> entries = new();
    readonly Queue<string> pending = new();
    readonly Queue<UploadBucket> recentUploads = new();
    readonly Statistics systemCpu = new(), recorderCpu = new(), workingSet = new(), privateMemory = new();
    DateTimeOffset lastFlush;
    DateTimeOffset? firstSample, lastSample;
    long uploadedBytes, sampleCount, requests, failures, deferred;
    long? peakWorkingSet;
    string lastActivity = "Starting";
    bool disposed, logUnavailable, enabled;

    /// <summary>Creates a local-only session. Injected clocks and crash lookup allow deterministic offline tests.</summary>
    public SessionDiagnostics(string outputFolder, string version, Func<DateTimeOffset>? clock = null,
        Func<DateTimeOffset, DateTimeOffset, CrashEvidenceResult>? crashEvidence = null, bool enabled = true)
    {
        this.enabled = enabled;
        directory = Path.Combine(outputFolder, "diagnostics");
        this.version = Clean(version);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.crashEvidence = crashEvidence ?? WindowsCrashEvidence.Read;
        started = lastFlush = this.clock();
        Activity("Recorder session started");
    }

    /// <summary>Optional history collection; turning it off drops buffered writes and leaves existing files intact.</summary>
    public bool Enabled
    {
        get { lock (gate) return enabled; }
        set { lock (gate) {
            if (disposed || enabled == value) return;
            enabled = value;
            pending.Clear();
            if (value) ActivityLocked("Diagnostic collection enabled", null, clock());
        } }
    }

    /// <summary>Adds a sampled whole-process resource measurement, without reading any Civ memory.</summary>
    public void Observe(ResourceSample sample, string activity)
    {
        lock (gate)
        {
            if (disposed || !enabled) return;
            sample = sample with
            {
                RecorderCpuPercent = ValidPercent(sample.RecorderCpuPercent), SystemCpuPercent = ValidPercent(sample.SystemCpuPercent),
                WorkingSetBytes = ValidBytes(sample.WorkingSetBytes), PrivateBytes = ValidBytes(sample.PrivateBytes),
                PeakWorkingSetBytes = ValidBytes(sample.PeakWorkingSetBytes), AvailableMemoryBytes = ValidBytes(sample.AvailableMemoryBytes),
                TotalMemoryBytes = ValidBytes(sample.TotalMemoryBytes),
            };
            sampleCount++;
            firstSample ??= sample.At;
            lastSample = sample.At;
            systemCpu.Add(sample.SystemCpuPercent); recorderCpu.Add(sample.RecorderCpuPercent);
            workingSet.Add(sample.WorkingSetBytes); privateMemory.Add(sample.PrivateBytes);
            if (sample.PeakWorkingSetBytes is long peak) peakWorkingSet = Math.Max(peakWorkingSet ?? 0, peak);
            ActivityLocked(activity, null, sample.At);
            Add(new(sample.At, "resource", lastActivity, sample));
            FlushIfDue();
        }
    }

    /// <summary>Records activity changes, never raw file paths, URLs, keys or exception messages.</summary>
    public void Activity(string activity, string? detail = null)
    {
        lock (gate)
        {
            if (disposed || !enabled) return;
            ActivityLocked(activity, detail, clock());
            FlushIfDue();
        }
    }

    void ActivityLocked(string activity, string? detail, DateTimeOffset at)
    {
        var description = Clean(activity);
        if (!string.IsNullOrWhiteSpace(detail)) description += ": " + Clean(detail);
        if (description == lastActivity) return;
        lastActivity = description;
        Add(new(at, "activity", description));
    }

    /// <summary>Counts request-body bytes written to the HTTP transport, including retry attempts; excludes protocol overhead.</summary>
    public void AddUpload(long bytes)
    {
        if (bytes <= 0) return;
        lock (gate)
        {
            if (disposed || !enabled) return;
            uploadedBytes = bytes > long.MaxValue - uploadedBytes ? long.MaxValue : uploadedBytes + bytes;
            var now = clock();
            TrimUploads(now);
            var second = now.ToUnixTimeSeconds();
            if (recentUploads.LastOrDefault() is { } last && last.Second == second) last.Bytes += bytes;
            else recentUploads.Enqueue(new(second, bytes));
            while (recentUploads.Count > 31) recentUploads.Dequeue();
        }
    }

    /// <summary>Counts an HTTP request attempt, including retries.</summary>
    public void RequestStarted() { lock (gate) if (!disposed && enabled) requests++; }
    /// <summary>Counts an actual HTTP request failure, including unsuccessful status responses.</summary>
    public void UploadFailed() { lock (gate) if (!disposed && enabled) failures++; }
    /// <summary>Counts a request deferred before starting, separately from network failures.</summary>
    public void UploadDeferred() { lock (gate) if (!disposed && enabled) deferred++; }

    /// <summary>Records the error category and code; arbitrary exception text may contain credentials or paths.</summary>
    public void Error(string component, Exception error)
    {
        lock (gate)
        {
            if (disposed || !enabled) return;
            Add(new(clock(), "error", $"{Clean(component)}: {error.GetType().Name} (0x{error.HResult:X8})"));
            FlushIfDue();
        }
    }

    /// <summary>Saves a readable local report chosen by the user. It is never automatically uploaded.</summary>
    public string Export(string destinationPath)
    {
        lock (gate) {
            if (!enabled) {
                var path = Path.GetFullPath(destinationPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "Recorder diagnostic collection is off. No new resource history or Windows crash lookup was collected for this report.");
                return path;
            }
        }
        var until = clock();
        DiagnosticEntry[] retained;
        lock (gate)
        {
            if (!disposed) Flush();
            retained = RetainedHistory().Where(e => e.SessionStarted != started && e.At >= until.AddHours(-24)).TakeLast(120).ToArray();
        }
        var retainedStart = retained.Length > 0 ? retained.Min(e => e.SessionStarted ?? e.At) : started;
        var evidenceSince = retainedStart < started ? retainedStart : started;
        if (evidenceSince < until.AddHours(-24)) evidenceSince = until.AddHours(-24);
        CrashEvidenceResult evidence;
        try { evidence = crashEvidence(evidenceSince, until); }
        catch { evidence = new("Windows crash records unavailable", []); }
        string report;
        lock (gate)
        {
            if (!disposed) Flush();
            var text = new StringBuilder();
            text.AppendLine("FFACiv Recorder — local diagnostic report");
            text.AppendLine($"Recorder version: {version}");
            text.AppendLine($"Session started (UTC): {started:O}");
            text.AppendLine($"Report generated (UTC): {until:O}");
            text.AppendLine($"Current activity: {lastActivity}");
            text.AppendLine("Crash cause: undetermined. This report does not prove or exclude recorder involvement.");
            text.AppendLine();
            text.AppendLine("Resource usage");
            text.AppendLine("CPU percentages are a share of total machine CPU capacity, not one processor core.");
            text.AppendLine("Recorder CPU and memory cover the main recorder process only; WebView2 and other helper processes are excluded.");
            text.AppendLine($"Recorder CPU: {recorderCpu.Describe("%", 1)}");
            text.AppendLine($"System CPU: {systemCpu.Describe("%", 1)}");
            text.AppendLine($"Recorder physical memory: {workingSet.Describe(" MiB", 1024 * 1024)}");
            text.AppendLine($"Recorder private memory: {privateMemory.Describe(" MiB", 1024 * 1024)}");
            text.AppendLine($"Process peak physical memory: {MiB(peakWorkingSet)}");
            text.AppendLine($"Upload request-body bytes written: {uploadedBytes.ToString(CultureInfo.InvariantCulture)} (includes retries; excludes HTTP/TLS overhead; not confirmed server receipt)");
            text.AppendLine(UploadSummary(until));
            text.AppendLine($"Resource observations: {sampleCount}; first: {Timestamp(firstSample)}; last: {Timestamp(lastSample)}");
            text.AppendLine("Averages use available samples; highest sampled CPU can miss brief spikes. Unknown values are not zero.");
            text.AppendLine($"Metric sample counts: recorder CPU {recorderCpu.Count}, system CPU {systemCpu.Count}, physical memory {workingSet.Count}, private memory {privateMemory.Count}.");
            text.AppendLine(logUnavailable ? "Local history could not be fully written; recording continued." : "Local history is capped at two 1 MiB files and buffered between writes; an abrupt stop can lose the newest buffered entries.");
            text.AppendLine();
            text.AppendLine("Windows crash evidence");
            text.AppendLine($"Lookup window (UTC): {evidenceSince:O} to {until:O}; includes retained earlier-session history up to 24 hours old when available.");
            text.AppendLine(Clean(evidence.Status));
            foreach (var crash in evidence.Crashes.Take(32))
                text.AppendLine($"{crash.At:O} | {Clean(crash.Application)} | fault module: {Clean(crash.Module ?? "unknown")} | exception: {Clean(crash.ExceptionCode ?? "unknown")}");
            text.AppendLine("Only matching Civ Application Error events in this window are included. A missing event does not prove a normal exit; a faulting module does not establish the root cause.");
            text.AppendLine();
            text.AppendLine("Recent observations (bounded history, UTC)");
            foreach (var entry in entries)
            {
                text.Append($"{entry.At:O} | {entry.Kind} | {entry.Message}");
                if (entry.Resources is { } s)
                    text.Append($" | recorder CPU {Percent(s.RecorderCpuPercent)}, system CPU {Percent(s.SystemCpuPercent)}, memory {MiB(s.WorkingSetBytes)}, available system memory {MiB(s.AvailableMemoryBytes)}");
                text.AppendLine();
            }
            text.AppendLine();
            text.AppendLine("Retained earlier-session observations (separate from current-session totals)");
            if (retained.Length == 0) text.AppendLine("No retained earlier-session observations available in the last 24 hours.");
            foreach (var entry in retained)
            {
                text.Append($"{entry.At:O} | {Clean(entry.Kind)} | {Clean(entry.Message)}");
                if (entry.Resources is { } s)
                    text.Append($" | recorder CPU {Percent(ValidPercent(s.RecorderCpuPercent))}, system CPU {Percent(ValidPercent(s.SystemCpuPercent))}, memory {MiB(ValidBytes(s.WorkingSetBytes))}, available system memory {MiB(ValidBytes(s.AvailableMemoryBytes))}");
                text.AppendLine();
            }
            text.AppendLine();
            text.AppendLine("No save contents, game-memory dumps, authentication keys or full file paths are included. Review this report before sharing it.");
            report = text.ToString();
        }
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, report, new UTF8Encoding(false));
        return fullPath;
    }

    void Add(DiagnosticEntry entry)
    {
        entry = entry with { SessionStarted = started };
        entries.Enqueue(entry);
        while (entries.Count > MaxEntries) entries.Dequeue();
        pending.Enqueue(JsonSerializer.Serialize(entry));
        while (pending.Count > MaxPending) pending.Dequeue();
    }

    void FlushIfDue() { if (clock() - lastFlush >= TimeSpan.FromSeconds(15)) Flush(); }

    void Flush()
    {
        lastFlush = clock();
        Add(new(lastFlush, "summary", $"Upload body bytes {uploadedBytes}; {UploadSummary(lastFlush)}; resource samples {sampleCount}; current activity {lastActivity}"));
        if (pending.Count == 0) return;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "session-history.jsonl");
            var previous = Path.Combine(directory, "session-history.previous.jsonl");
            while (pending.TryDequeue(out var line))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaxLogBytes)
                    File.Move(path, previous, overwrite: true);
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                file.Write(bytes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { logUnavailable = true; } // Keep diagnostics subordinate to recording.
    }

    IEnumerable<DiagnosticEntry> RetainedHistory()
    {
        foreach (var name in new[] { "session-history.previous.jsonl", "session-history.jsonl" })
        {
            string content;
            try
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path) || new FileInfo(path).Length > MaxLogBytes) continue;
                content = SafeFiles.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            foreach (var line in content.Split('\n').Take(16384))
            {
                if (line.Length == 0 || line.Length > 8192) continue;
                DiagnosticEntry? entry = null;
                try { entry = JsonSerializer.Deserialize<DiagnosticEntry>(line); }
                catch (JsonException) { }
                if (entry is not null && entry.Kind is not null && entry.Message is not null) yield return entry;
            }
        }
    }

    string UploadSummary(DateTimeOffset at)
    {
        TrimUploads(at);
        var recentBytes = recentUploads.Sum(b => (double)b.Bytes);
        var elapsed = Math.Max(1, (at - started).TotalSeconds);
        return $"Upload rate: recent 30-second window {(recentBytes / Math.Min(30, elapsed) / 1024).ToString("F2", CultureInfo.InvariantCulture)} KiB/s; session average {(uploadedBytes / elapsed / 1024).ToString("F2", CultureInfo.InvariantCulture)} KiB/s; requests {requests}; failed {failures}; deferred {deferred}";
    }

    void TrimUploads(DateTimeOffset at)
    {
        while (recentUploads.TryPeek(out var first) && first.Second <= at.ToUnixTimeSeconds() - 30) recentUploads.Dequeue();
    }

    /// <summary>Flushes pending local history, without contacting a server or reading the game.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (!enabled) { disposed = true; return; }
            Add(new(clock(), "activity", "Recorder session ended"));
            Flush(); disposed = true;
        }
    }

    static string Clean(string value)
    {
        var text = value.Length > 1024 ? value[..1024] : value;
        text = Regex.Replace(text, @"(?:https?://|[A-Za-z]:[\\/]|\\\\)\S+", "[redacted]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?<![\w])/\S+", "[redacted]");
        text = Regex.Replace(text, @"(?:Bearer\s+\S+|(?:token|key|authorization|password|secret)\s*[:=]\s*\S+)", "[redacted]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b[A-Za-z0-9_+/=-]{32,}\b", "[redacted]");
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return text.Length > 240 ? text[..240] : text;
    }

    static string Timestamp(DateTimeOffset? at) => at?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown";
    static double? ValidPercent(double? value) => value is double d && double.IsFinite(d) && d >= 0 && d <= 100 ? d : null;
    static long? ValidBytes(long? value) => value is >= 0 ? value : null;
    static string Percent(double? value) => value is double d && double.IsFinite(d) ? d.ToString("F2", CultureInfo.InvariantCulture) + "%" : "unknown";
    static string MiB(long? value) => value is long bytes ? (bytes / 1048576d).ToString("F1", CultureInfo.InvariantCulture) + " MiB" : "unknown";
    sealed record DiagnosticEntry(DateTimeOffset At, string Kind, string Message, ResourceSample? Resources = null, DateTimeOffset? SessionStarted = null);
    sealed class UploadBucket(long second, long bytes)
    {
        public long Second { get; } = second;
        public long Bytes { get; set; } = bytes;
    }
    sealed class Statistics
    {
        public long Count { get; private set; }
        double mean, max;
        public void Add(double? value)
        {
            if (value is not double v || !double.IsFinite(v) || v < 0) return;
            Count++; mean += (v - mean) / Count; max = Math.Max(max, v);
        }
        public string Describe(string unit, double divisor) => Count == 0 ? "unknown" : $"average {(mean / divisor).ToString("F2", CultureInfo.InvariantCulture)}{unit}; highest sampled {(max / divisor).ToString("F2", CultureInfo.InvariantCulture)}{unit}";
    }
}
