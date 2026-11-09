using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>The corner pop-ups the recorder makes itself: notices, server problems, recording controls.</summary>
sealed partial class LiveBackend
{
    string? lastAutosave;

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> notifiedLiveLinks = new();

    long streamDownSince = -1;

    bool toldStreamDown;

    /// <summary>Pop-ups from the recorder, as the player chose them in Settings. Never one per turn.</summary>
    void OnNotice(string title, string text)
    {
        nextChecks = 0;
        bool problem = title.StartsWith("Unsupported") || title.StartsWith("Not") || title.Contains("stopped") || title.StartsWith("In-game") || title.StartsWith("Civ's autosave");
        bool detected = title is "Civ V found" or "Recording game" or "Civ closed" or "Single-player game — not recorded" or "Multiplayer lobby";
        if (problem && !Setting("notify.problems")) return;
        if (detected && !Setting("notify.lobby")) return;
        if (title == "Recording saved" && !Setting("notify.lobby")) return;
        var kind = problem ? "warn" : title.StartsWith("Recording") ? "good" : "info";
        // the core's own wording can name the game's mod folder: the window says it neutrally
        if (title == "Not recording") { title = "This game isn't recorded"; text = "This game version is not supported for realtime recording."; }
        var t = J.Toast(kind, title, text);
        if (title is "Civ V found" or "Multiplayer lobby" or "Recording saved" or "Civ closed") return;
        if (title == "Recording game")
        {
            t["title"] = "Game detected · recording";
            t["text"] = "Your game is being recorded. You can keep playing.";
            t["actions"] = RecordingActions();
        }
        if (title == "Single-player game — not recorded")
        {
            t["actions"] = new JsonArray(J.Obj(("label", "Record it anyway"), ("cmd", "recordAnyway"), ("primary", true)));
            t["timeout"] = 30000;
        }
        Toast?.Invoke(t);
    }

    /// <summary>Pop-ups the window makes itself: the test server lost for a while during a game (once per outage), and
    /// "saved" when the player asked for one per saved turn (off by default).</summary>
    void WatchForToasts(RecorderStatus s, StreamStatus? st, string mode)
    {
        if (!uploadPolicy.CanSend()) return;
        if (slots.Enabled && !preview && Setting("notify.lobby") && LiveLink(s, st, mode).Str("url") is { } liveUrl
            && notifiedLiveLinks.TryAdd(liveUrl, 0))
        {
            var ready = J.Toast("good", "Private livestream ready", "Only people with your link can watch. It is not listed publicly.");
            AddNotificationLink(ready, liveUrl);
            Toast?.Invoke(ready);
        }
        if (s.LastAutosave is { } la && la != lastAutosave)
        {
            if (lastAutosave is not null && s.Recording && Setting("notify.saved", false))
                Toast?.Invoke(J.Toast("good", $"Turn {s.Turn} saved", "Kept on this PC" + (mode == "off" ? "." : $" and sent to {ServerName}.")));
            lastAutosave = la;
        }
        // a problem the player must hear about once: cap reached, version too old, blocked / switched off
        if (st?.Problem is { } pr && pr != toldProblem && pr is "cap" or "update" or "blocked" or "off" && Setting("notify.problems"))
        {
            toldProblem = pr;
            var (title, text) = ProblemWords(st);
            var t = J.Toast("warn", title, text);
            if (pr == "update" && st.DownloadUrl is { } dl) t["actions"] = new JsonArray(J.Obj(("label", "Get the new version"), ("cmd", "openSite"), ("args", J.Obj(("url", dl))), ("primary", true)));
            Toast?.Invoke(t);
        }
        if (st?.Problem is null) toldProblem = null;
        bool down = mode != "off" && st is { State: var state } && state.StartsWith("server not reachable");
        if (!down) { streamDownSince = -1; toldStreamDown = false; return; }
        if (streamDownSince < 0) streamDownSince = Environment.TickCount64;
        if (!toldStreamDown && s.Recording && Environment.TickCount64 - streamDownSince > 30_000 && Setting("notify.problems"))
        {
            toldStreamDown = true;
            Toast?.Invoke(J.Toast("warn", $"Can't reach {ServerShort}", "The game is kept on this PC and sent when the server is back. " + WhenDown));
        }
    }

    void AddNotificationLink(JsonObject toast, string url)
    {
        notifiedLiveLinks.TryAdd(url, 0);
        toast["text"] = (toast.Str("text") ?? "") + "\n" + url;
        var actions = toast["actions"] as JsonArray ?? new JsonArray();
        if (toast["actions"] is null) toast["actions"] = actions;
        actions.Add(J.Obj(("label", "Open livestream"), ("cmd", "openSite"), ("args", J.Obj(("url", url)))));
        if (actions.Count == 1) actions.Add(J.Obj(("label", "Copy link"), ("cmd", "copyLiveLink"), ("primary", true)));
        toast["timeout"] = 30000;
    }

    string? toldProblem;

    /// <summary>The stream's problem in plain words (title, text) for the window and the pop-ups.</summary>
    (string Title, string Text) ProblemWords(StreamStatus st) => st.Problem switch
    {
        "cap" => ("Today's upload limit is reached", $"The rest is sent by itself after {(st.Until is { } u ? u.ToLocalTime().ToString("HH:mm") : "midnight UTC")}. Nothing is lost: it waits on this PC."),
        "update" => ("Please update the recorder", $"This version is too old for {ServerShort}{(st.MinVersion is { } v ? $" (it needs {v} or newer)" : "")}. Nothing is lost: your games wait on this PC and go out after the update."),
        "blocked" => ($"This PC can't send to {ServerShort}", "Your games stay on this PC. If you think this is a mistake, write to us on the site."),
        "off" => ($"Sending is paused on {ServerShort}", "Your games wait on this PC and are sent when it opens again."),
        "key" => ("Signing this PC in again", $"{ServerShort} did not know this PC any more; it is registered again by itself."),
        "unreachable" => ($"Can't reach {ServerShort}", WhenDown),
        _ => ("", ""),
    };

    JsonArray RecordingActions() => new(
        J.Obj(("label", "Stop recording"), ("cmd", "pauseGame")),
        J.Obj(("label", "Options"), ("cmd", "showOptions")));

    JsonObject PausedRecordingToast()
    {
        var toast = J.Toast("info", "Recording paused", "New saves are no longer copied. Existing recordings stay on this PC.");
        toast["actions"] = new JsonArray(
            J.Obj(("label", "Start recording"), ("cmd", "setSetting"), ("args", J.Obj(("key", "recording"), ("value", true))), ("primary", true)),
            J.Obj(("label", "Options"), ("cmd", "showOptions")));
        return toast;
    }
}
