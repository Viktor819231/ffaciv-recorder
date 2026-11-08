using System.Text.Json.Nodes;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>Explicit memory permission is separate from old logging preferences and upload policy.</summary>
static class CapturePreferences
{
    public static bool MemoryRequested(JsonObject settings) => settings["experimentalMemoryOptIn"]?.GetValue<bool>() == true
        && settings["trackLogs"]?.GetValue<bool>() == true;

    public static int Rate(JsonObject settings) => RecorderSettings.NormalizeLooksPerSecond(
        settings["looksPerSecond"] is JsonValue value && value.TryGetValue<int>(out var rate) ? rate : 4);

    public static string Format(JsonObject settings) => settings["saveUpload"]?.GetValue<string>() == "full" ? "full" : "delta";

    public static bool UploadDuringPlay(JsonObject settings) => settings["uploadTiming"]?.GetValue<string>() == "during";

    /// <summary>Simple setup (the user, 2026-11-08: "barebones, no local parsing and no memory logs, just upload"): copy
    /// Civ's autosaves, send them whole after the game and let the website make the replay. Nothing reads Civ's memory.</summary>
    public static void ApplySimpleSetup(JsonObject settings)
    {
        settings["recording"] = true;
        settings["experimentalMemoryOptIn"] = false;
        settings["trackLogs"] = false;
        settings["captureMoves"] = false;
        settings["saveUpload"] = "full";
        settings["uploadTiming"] = "after";
        settings["stream"] = true;
    }

    public static void ApplyUploadPolicy(UploadPolicy policy, JsonObject settings, bool setupPending)
    {
        policy.Automatic = !setupPending;
        policy.AfterPlaying = !UploadDuringPlay(settings);
    }

    public static void Apply(RecorderSession session, JsonObject settings, bool setupPending)
    {
        bool enabled = MemoryRequested(settings) && settings["recording"]?.GetValue<bool>() != false;
        session.AllowExperimentalMemoryLogging = enabled;
        session.TrackLogs = enabled;
        session.CaptureMoves = session.TrackLogs;
        session.RecordingEnabled = !setupPending && settings["recording"]?.GetValue<bool>() != false;
        session.SampleMs = session.TrackLogs ? 1000 / Rate(settings) : 1000;
    }
}
