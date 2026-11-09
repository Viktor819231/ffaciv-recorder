using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>Save diagnostic report: the local zip of the player's choosing (nothing is uploaded).</summary>
sealed partial class LiveBackend
{
    string? FolderOf(string? game) => game is null ? null : game == "live" ? session.Status.Folder : Path.Combine(fileSettings.OutputFolder, game);

    static string ShortPath(string p)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return p.StartsWith(docs, StringComparison.OrdinalIgnoreCase) ? "Documents" + p[docs.Length..] : p;
    }

    /// <summary>The problem report as a zip on the Desktop (sending it comes later): only the parts the player ticked.</summary>
    JsonObject SaveReport(JsonObject args)
    {
        var parts = (args["parts"] as JsonArray)?.Select(p => p?.GetValue<string>()).ToHashSet() ?? [];
        var zipPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"FFACiv Recorder report {DateTime.Now:yyyy-MM-dd HHmmss}-{Guid.NewGuid():N}.zip");
        string? diagnosticPath = null;
        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            var s = session.Status;
            var sb = new StringBuilder();
            sb.AppendLine($"FFACiv Recorder {version}, {DateTime.Now:o}");
            sb.AppendLine($"What happened: {args.Str("text")}");
            sb.AppendLine($"Capture: {(session.TrackLogs ? $"Experimental external memory logs ({LooksPerSecond()}/s)" : "Save files only; memory reading disabled")}; recording: {s.Recording}; paused: {s.Paused}");
            StreamStatus? st; lock (streamLock) st = streamer?.Status;
            sb.AppendLine($"Uploads: {StreamMode}; completed event bytes: {st?.EventWireBytes}; completed save bytes: {st?.SaveWireBytes}");
            sb.AppendLine($"Resource policy: {workload.Reason}; upload limit: {fileSettings.UploadKilobytesPerSecond} KiB/s; requested hard CPU ceiling: {fileSettings.CpuLimitPercent}% (0 = off)");
            sb.AppendLine($"Hard CPU ceiling: {(cpuLimit is not null ? "active" : cpuLimitError is not null ? "unavailable; adaptive controls remain active" : "off")}");
            sb.AppendLine("Crash cause: undetermined. Low recorder resource use does not prove or exclude involvement.");
            if (parts.Contains("pc")) sb.AppendLine($"Windows: {Environment.OSVersion} | 64-bit: {Environment.Is64BitOperatingSystem} | Civ settings file: {File.Exists(AutosaveCheck.IniPath)}");
            bool attachmentsAllowed = workload.CanStartHeavyWork && (!fileSettings.SafeCapture || uploadPolicy.IsIdle());
            if (!attachmentsAllowed) sb.AppendLine("Game/save attachments omitted while Civ or the computer is busy. Resource diagnostics remain available.");
            using (var w = new StreamWriter(zip.CreateEntry("report.txt").Open())) w.Write(sb.ToString());
            if (parts.Contains("log"))
            {
                diagnosticPath = Path.Combine(Path.GetTempPath(), $"ffaciv-diagnostic-{Guid.NewGuid():N}.txt");
                diagnostics.Export(diagnosticPath);
                zip.CreateEntryFromFile(diagnosticPath, "session-health.txt");
            }
            var last = Directory.Exists(fileSettings.OutputFolder) ? Directory.GetDirectories(fileSettings.OutputFolder, "session-*").OrderByDescending(d => d).FirstOrDefault() : null;
            if (attachmentsAllowed && parts.Contains("session") && last is not null && File.Exists(Path.Combine(last, "events.jsonl")))
            {
                using var src = new FileStream(Path.Combine(last, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = zip.CreateEntry("last-game/events.jsonl").Open();
                src.CopyTo(dst);
            }
            var backup = Directory.Exists(fileSettings.OutputFolder) ? Directory.GetDirectories(fileSettings.OutputFolder, "unsupported-*").OrderByDescending(d => d).FirstOrDefault() : null;
            if (attachmentsAllowed && backup is not null && parts.Contains("session"))
                foreach (var name in new[] { "diagnostics.jsonl", "backup.json" })
                {
                    var file = Path.Combine(backup, name);
                    if (File.Exists(file))
                    {
                        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var dst = zip.CreateEntry("unsupported-backup/" + name).Open(); src.CopyTo(dst);
                    }
                }
            if (attachmentsAllowed && parts.Contains("save") && (last ?? backup) is { } saveFolder && Streamer.SavesOf(saveFolder).LastOrDefault() is { } save)
                zip.CreateEntryFromFile(save, "last-game/" + Path.GetFileName(save));
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
            return J.Obj(("toast", J.Toast("good", "Report saved on your Desktop", "Attach this zip when asking for help. It has not been sent automatically.")));
        }
        catch (Exception e) { return J.Obj(("toast", J.Toast("warn", "Could not make the report", e.Message))); }
        finally { if (diagnosticPath is not null) { try { File.Delete(diagnosticPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }

    int reportBusy;
}
