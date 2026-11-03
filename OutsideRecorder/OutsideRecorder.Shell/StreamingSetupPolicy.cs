using OutsideRecorderCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OutsideRecorderShell;

/// <summary>Setup may prepare future capture; it must not enable new heavy work in an already-running game.</summary>
static class StreamingSetupPolicy
{
    public static bool RecordingAllowed(bool sharing, bool liveAllowed, bool authorized, bool current, bool idle, bool normalApproved) =>
        !sharing ? normalApproved : (liveAllowed && authorized && (current || idle)) || (idle && normalApproved);
    public static int QueuedSaves(string? folder)
    {
        if (folder is null) return 0;
        try {
            var progress = Path.Combine(folder, "stream.json");
            var state = File.Exists(progress) ? JsonSerializer.Deserialize<StreamState>(SafeFiles.ReadAllText(progress)) ?? throw new InvalidDataException() : new StreamState();
            return Streamer.SavesOf(folder).Count(f => !state.SentSaves.ContainsKey(Path.GetFileName(f)));
        } catch { return int.MaxValue; } // Unknown progress must never release a backlog.
    }

    public static bool NeedsSetup(JsonObject settings) => settings["recording"]?.GetValue<bool>() != true || !CapturePreferences.MemoryRequested(settings);
    public static string? EnableReason(JsonObject settings, bool civRunning, bool lobby, bool active, bool paused, int queuedSaves, bool latestOnly = false)
    {
        if (active && NeedsSetup(settings))
            return "Set up streaming before starting a game. Close Civ V first; Steam can stay open. This game will not be uploaded as a catch-up batch.";
        if (active && queuedSaves == int.MaxValue && latestOnly) return "Upload progress could not be read. Keep the recording on this PC until that progress is repaired; starting from a current save must preserve existing receipts.";
        if (active && queuedSaves > 2 && !latestOnly) return "This game has an unsent save backlog. Choose Start from current save to leave older unsent saves on this PC.";
        if (active && paused) return "Resume the recording first.";
        if (civRunning && !lobby && !active && NeedsSetup(settings)) return "Close Civ V before enabling capture settings. Steam can stay open.";
        return null;
    }

    public static string? FutureInviteReason(JsonObject settings, bool civRunning, bool lobby) =>
        civRunning && !lobby && NeedsSetup(settings)
            ? "Close Civ V before enabling capture settings. Steam can stay open."
            : null;

    public static bool AwaitFreshContext(bool waiting, string? context, ISet<string> existing) =>
        waiting && context is not null && existing.Contains(context);

    public static string? ChangeReason(JsonObject settings, string key, JsonNode? value, bool civRunning, bool lobby)
    {
        if (!civRunning || lobby) return null;
        bool on = value is JsonValue v && v.TryGetValue<bool>(out var enabled) && enabled;
        bool increases = key is "trackLogs" or "captureMoves" or "experimentalMemoryOptIn" ? on && !CapturePreferences.MemoryRequested(settings)
            : key == "saveUpload" ? value?.ToString() == "delta" && CapturePreferences.Format(settings) != "delta"
            : key == "uploadTiming" ? value?.ToString() == "during" && !CapturePreferences.UploadDuringPlay(settings)
            : key == "stream" ? on && settings["stream"]?.GetValue<bool>() != true
            : key == "recording" && on && settings["recording"]?.GetValue<bool>() != true && CapturePreferences.MemoryRequested(settings);
        return increases ? "Close Civ V before enabling new capture, local preparation or while-playing uploads. Steam can stay open. Your current recording and queued data are kept." : null;
    }
}
