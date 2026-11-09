using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>The window's own settings (ui-settings.json): loading with defaults, saving, start with Windows, and what they mean for capture and uploads.</summary>
sealed partial class LiveBackend
{
    bool Setting(string path, bool fallback = true)
    {
        JsonNode? n = ui["settings"];
        foreach (var p in path.Split('.')) n = n?[p];
        return n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    }

    string UploadFormat() => CapturePreferences.Format(ui["settings"]!.AsObject());

    void ApplyCaptureSettings()
    {
        CapturePreferences.Apply(session, ui["settings"]!.AsObject(), SetupPending);
        // The per-recording gate separates automatic schedules from an explicit Upload now.
        CapturePreferences.ApplyUploadPolicy(uploadPolicy, ui["settings"]!.AsObject(), SetupPending);
    }

    // ---- the window's own settings ---------------------------------------------------------------------------------------
    JsonObject LoadUi()
    {
        JsonObject? saved = null;
        try { if (File.Exists(settingsFile)) saved = JsonNode.Parse(File.ReadAllText(settingsFile)) as JsonObject; } catch { }
        var defaults = J.Obj(
            ("firstRun", true),
            ("settings", J.Obj(("diagnostics", false), ("backupUnsupported", true), ("captureMoves", true), ("recording", true), ("trackLogs", fileSettings.TrackLogs), ("uploadTiming", fileSettings.UploadTiming), ("recordSinglePlayer", false), ("looksPerSecond", 20), ("keepCopies", fileSettings.KeepCopies), ("stream", true), ("upload", false), ("publishDelay", 3),
                ("notify", J.Obj(("lobby", true), ("saved", false), ("reminders", true), ("problems", true), ("requests", true), ("updates", true))),
                ("startWithWindows", true), ("dataFolder", fileSettings.OutputFolder), ("steam", J.Obj(("linked", false))), ("language", "en"))),
            ("autoSmall", true));
        if (saved is not null) Merge(defaults, saved);
        // Older versions only remembered the old introduction. Show this setup once without
        // resetting the user's choices, and do not start capturing/sending until it is finished.
        Onboarding.Migrate(defaults);
        if (saved?["settings"]?["trackLogs"] is null && saved?["settings"]?["captureMoves"] is JsonValue oldCapture)
            defaults["settings"]!["trackLogs"] = oldCapture.DeepClone();
        defaults["settings"]!["experimentalMemoryOptIn"] ??= fileSettings.MemoryTest;
        defaults["settings"]!["saveUpload"] ??= "delta";
        if (saved?["settings"]?["experimentalMemoryOptIn"] is null) defaults["settings"]!["looksPerSecond"] = 4;
        if (fileSettings.MemoryTest) { defaults["settings"]!["experimentalMemoryOptIn"] = true; defaults["settings"]!["trackLogs"] = true; }
        if (defaults["settings"]!["experimentalMemoryOptIn"]?.GetValue<bool>() != true)
        {
            defaults["settings"]!["trackLogs"]=false; defaults["settings"]!["captureMoves"]=false;
        }
        if (fileSettings.SafeCapture) { defaults["settings"]!["uploadTiming"]="after"; defaults["settings"]!["keepCopies"]=true; defaults["settings"]!["backupUnsupported"]=false; }
        // the test build (OR03): stream to the local test server and start with Windows, once, whatever an older preview saved
        if (defaults["testBuild"]?.GetValue<int>() is not >= 1)
        {
            defaults["settings"]!["stream"] = true;
            defaults["settings"]!["upload"] = false;
            defaults["settings"]!["startWithWindows"] = true;
            if (!preview) defaults["testBuild"] = 1;
        }
        // Windows itself is the truth (the player may have switched it off in Task Manager)
        if (saved?["testBuild"] is not null) defaults["settings"]!["startWithWindows"] = StartWithWindows;
        defaults["settings"]!["dataFolder"] = fileSettings.OutputFolder;
        if (!preview) SaveUi(defaults);
        return defaults;
    }

    static void Merge(JsonObject into, JsonObject from)
    {
        foreach (var (k, v) in from)
        {
            if (v is JsonObject o && into[k] is JsonObject t) Merge(t, o);
            else if (!OtherKind(into[k], v)) into[k] = v?.DeepClone();   // a saved value of another kind keeps the default
        }
    }

    /// <summary>True when both are set and of different JSON kinds (true and false are one kind): the setting would make
    /// GetValue throw on every refresh and at start.</summary>
    static bool OtherKind(JsonNode? known, JsonNode? value)
    {
        static string KindOf(JsonNode n) => n.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? "bool" : n.GetValueKind().ToString();
        return known is not null && value is not null && KindOf(known) != KindOf(value);
    }

    void SaveUi(JsonObject? o = null) { if (preview) return; try { SafeFiles.Replace(settingsFile, (o ?? ui).ToJsonString(new JsonSerializerOptions { WriteIndented = true })); } catch { } }

    /// <summary>Looks at the game per second (Settings: 5, 10 or 20; 20 by default).</summary>
    int LooksPerSecond() => CapturePreferences.Rate(ui["settings"]!.AsObject());

    // ---- start with Windows (HKCU Run, no admin) -------------------------------------------------------------------------
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "FFACiv Recorder";

    /// <summary>Windows' own start list for this user: the program starts hidden by the clock (--tray).</summary>
    static bool StartWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) is string; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray"); else k.DeleteValue(RunName, false);
        }
    }
}
