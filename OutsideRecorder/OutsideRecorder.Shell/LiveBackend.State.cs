using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>The state the window draws (OR02 "State"): status, current game, checks, resources, and the tray's short version while the window is hidden.</summary>
sealed partial class LiveBackend
{
    // ---- state ---------------------------------------------------------------------------------------------------------
    public JsonObject BuildState() => Build(window: true);

    public JsonObject BuildTrayState() => Build(window: false);

    /// <summary>Set while the window was hidden at a games-list refresh: the list is made again as soon as it shows.</summary>
    bool gamesStale = true;

    JsonObject Build(bool window)
    {
        var s = session.Status;
        UpdateRecordingUx(s);
        if (window) RefreshLibrary();   // the site's replay list is only shown, never acted on
        StreamStatus? st; string mode;
        lock (streamLock) { st = streamer?.Status; mode = StreamMode; }
        if (preview && mode != "off") st = ProbeStatus();
        if (Environment.TickCount64 >= nextChecks)
        {
            checks = BuildChecks(s, st, mode);
            if (window && BackgroundWorkAllowed) games = ListGames(s, mode);
            else if (!window) gamesStale = true;
            nextChecks = Environment.TickCount64 + 4000;
        }
        else if (window && gamesStale && BackgroundWorkAllowed) games = ListGames(s, mode);
        if (window) gamesStale = false;
        games = deletedCards.Filter(games);
        WatchForToasts(s, st, mode);
        var (mod, modVersion) = ModOf(s.Layout);
        JsonObject status;
        JsonObject? current = null;
        bool inGame = s.Identity is not null && s.Players.Count > 0 && s.Game is not null;
        if (!Setting("recording")) status = J.Obj(("kind", "paused"), ("title", "Recording is off"),
            ("detail", "No new saves are copied. Existing recordings follow your upload choices in Games."));
        else if (!session.TrackLogs) status = J.Obj(("kind", s.Paused ? "paused" : SaveCaptureActive(s) ? "recording" : "waiting"),
            ("title", s.Paused ? "Recording paused" : SaveCaptureActive(s) ? "Recording your game" : "Waiting for Civ"),
            ("detail", s.Paused ? "Start recording to keep new saves." : SaveCaptureActive(s) ? "Autosaves are being kept on this PC. Keep playing." : "Start Civ V and play. Recording starts with the first autosave."),
            ("note", Setting("stream") ? "After the game, you have 2 hours to upload, postpone or delete." : "Uploads are off. Your recordings stay on this PC."));
        else if (s.Recording && session.TurnTransitionQuiet)
            status = J.Obj(("kind", "recording"), ("title", "Recording · new turn settling"), ("detail", "Detailed reads resume after the 5-second pause."));
        else if (s.Recording && s.State == "Recording")
            status = J.Obj(("kind", "recording"), ("title", $"Recording {mod ?? "Civ V"} game"), ("detail", Join(s.Turn is int t ? $"Turn {t}" : null, s.Mode)));
        else if (s.Recording) status = J.Obj(("kind", "found"), ("title", $"Civ V found · {mod} {modVersion}".TrimEnd()), ("detail", "In the menus or loading. Recording goes on when the game is back."));
        else if (inGame && s.SinglePlayerSkipped) status = J.Obj(("kind", "paused"), ("title", "Single-player game — not recorded"),
            ("detail", "Single-player games are off in Settings (for testing, coming later). Multiplayer and hot seat games are recorded."),
            ("action", J.Obj(("cmd", "recordAnyway"), ("label", "Record it anyway"))));
        else if (inGame && s.Paused) status = J.Obj(("kind", "paused"), ("title", "Not recording this game"), ("detail", Setting("recording") ? "You paused it. Press \"Record this game again\" to go on." : "Recording is off in Settings."));
        else if (s.State.StartsWith("Waiting for Civ")) status = J.Obj(("kind", "waiting"), ("title", "Waiting for Civ"), ("detail", "Start a game and it is recorded by itself."));
        else if (s.Lobby is { } lb) status = J.Obj(("kind", "found"), ("title", "In the multiplayer lobby"),
            ("detail", Join($"{lb.Slots.Count(x => x.Status == 3)} players", lb.MapScript is { } m ? MapName(m) : null, WorldSizeName(lb.WorldSize), lb.TurnTimer?.TurnSeconds is int ts ? $"turn timer {ts} s" : lb.TurnTimer?.Enabled == true ? "turn timer on" : null) + ". Recorded when the game starts."));
        else if (s.State.StartsWith("Civ found")) status = J.Obj(("kind", "found"), ("title", $"Civ V found · {mod} {modVersion}".TrimEnd()), ("detail", "Recording starts when a game is running."));
        else if (s.UnsupportedKek) status = J.Obj(("kind", "paused"), ("title", "KEK Mod is supplied by Oblast"), ("detail", "KEK saves are not copied or uploaded by this recorder."));
        else if (s.UnsupportedBackingUp) status = J.Obj(("kind", "attention"), ("title", "Unsupported build — keeping autosaves"),
            ("detail", $"{s.UnsupportedSaves} autosaves kept on this PC, with a diagnostic log. Upload them after support is fixed. Realtime movement logs are unavailable."),
            ("action", J.Obj(("cmd", "openFolder"), ("label", "Open backup folder"), ("args", J.Obj(("path", s.UnsupportedFolder))))));
        else if (s.State == "Starting") status = J.Obj(("kind", "starting"), ("title", "Starting…"));
        else status = J.Obj(("kind", "unsupported"), ("title", "This game isn't recorded"), ("detail", "This game version is not supported for realtime recording."),
            ("note", "Civ's original autosaves are kept. Manual upload may be possible once the website supports this mod version. KEK Mod games come from Oblast."));
        if (otherRecorder)
            status = J.Obj(("kind", "paused"), ("title", "Another recorder is running"), ("detail", "The old FFACiv Outside Recorder window records right now. Close it (its icon by the clock or its window), then start this one again."));
        else if (!Setting("recording") && !inGame)
            status = J.Obj(("kind", "paused"), ("title", "Recording is off"), ("detail", "Turn the recorder back on here when you want to record."));
        // A busy PC (or the few seconds after each new turn) holds save copies and uploads back. This used to replace every
        // status with "Waiting for Civ": at each turn change while recording, and for a whole game on a busy PC, where the
        // game was really not being recorded (the user's screenshot, 2026-11-09: "Civ detected · waiting for a game", turn 5).
        if (!otherRecorder && Setting("recording") && !workload.CanStartHeavyWork)
        {
            bool busy = !session.TurnTransitionQuiet;   // the turn pause is normal and short: not worth a line
            if (status.Str("kind") == "recording")
            {
                if (busy) status["note"] = "Your PC is busy right now: save copies and uploads wait a little so your game stays smooth. Nothing is lost.";
            }
            else if (s.CivDetected || inGame || s.Lobby is not null)
            {
                if (busy && status.Str("kind") is "waiting" or "found")
                    // "attention": the page then shows this title (and the tray its attention icon), not "waiting for a game"
                    status = J.Obj(("kind", "attention"), ("title", "Your PC is busy — the recorder holds back"),
                        ("detail", "Civ is using the computer fully, so the recorder waits to keep your game smooth. Civ keeps its own autosaves; they are copied when the computer calms down."),
                        ("note", "Details in Checks."));
            }
            else status = J.Obj(("kind", "waiting"), ("title", "Waiting for Civ"), ("detail", "The recorder is on. Keep playing; resource details are available in Checks."));
        }
        if (!window)
        {
            if (session.IsDeletedIdentity(s.Identity)) status = J.Obj(("kind", "paused"), ("title", "This game was deleted"));
            return J.Obj(("status", status), ("checks", checks.DeepClone()));
        }

        if (!session.TrackLogs && s.Context is not null && s.Folder is not null && SaveCaptureActive(s))
        {
            current = J.Obj(("id", "live"), ("context", s.Context), ("mod", "Civ V"), ("turn", s.Turn), ("players", new JsonArray()),
                ("paused", s.Paused), ("folder", s.Folder), ("upload", Upload(s.Folder, st, mode)), ("liveLink", LiveLink(s, st, mode)));
            AddSaves(current, s.Folder, s.Context, mode, st);
        }
        else if (inGame && (s.Recording || s.Paused))
        {
            var knownCurrent = new GameIndex(fileSettings.OutputFolder).Load().LastOrDefault(e => e.Identity == s.Identity);
            var currentContext = s.Context ?? knownCurrent?.Context;
            var folder = s.Folder ?? knownCurrent?.Folder;
            current = J.Obj(("id", "live"), ("mod", mod ?? "Civ V"), ("modVersion", modVersion), ("mode", s.Mode ?? "game"), ("turn", s.Turn),
                ("players", Players(s.Players, s.ActivePlayer)), ("cityStates", s.Players.Count(p => !IsMajor(p) && p.Alive)),
                ("startedAt", s.GameStarted is { } gs ? J.Ms(gs) : null), ("lastSaveAt", s.LastSaveAt is { } ls ? J.Ms(ls) : null),
                ("paused", s.Paused), ("ended", !s.Recording), ("context", currentContext), ("upload", Upload(folder, st, mode)), ("publish", J.Obj(("state", "local"))), ("folder", folder),
                ("liveLink", LiveLink(s, st, mode)), ("replay", currentContext is { } context ? ReplayUiState(context) : null));
            AddSaves(current, folder, currentContext, mode, st);
        }
        else if (s.Lobby is { } lobby)
        {
            current = J.Obj(("id", "lobby"), ("lobby", true), ("mod", mod ?? "Civ V"), ("modVersion", modVersion), ("mode", "multiplayer lobby"),
                ("players", J.Arr(lobby.Slots.Where(x => x.InUse).Select(x => (JsonNode?)J.Obj(
                    ("name", x.Status == 3 ? x.Name ?? "Player" : x.Status == 1 ? "AI" : x.Status == 0 ? "Open" : x.Status == 4 ? (x.Name ?? "Observer") + " (watching)" : "–"),
                    ("civ", x.Status is 0 or 4 ? "–" : x.Civ == -1 ? "Random" : CivName(x.CivKey)), ("team", x.Team),
                    ("color", x.Id == lobby.ActivePlayer ? "#f0c860" : x.Status == 3 ? "#5ec8bc" : "#7d8b88"), ("you", x.Id == lobby.ActivePlayer), ("human", x.Status == 3))))),
                ("map", lobby.MapScript is { } m2 ? MapName(m2) : null), ("worldSize", lobby.WorldSize), ("mapSize", WorldSizeName(lobby.WorldSize)),
                ("turnTimerOn", lobby.TurnTimer?.Enabled == true), ("gameSpeed", lobby.GameSpeed), ("era", lobby.Era),
                ("turnTimerSeconds", lobby.TurnTimer?.TurnSeconds), ("options", lobby.Options.Count(o => o.Value != 0)), ("gameName", lobby.GameName),
                ("upload", Upload(null, st, mode)), ("publish", J.Obj(("state", "local"))), ("liveLink", LiveLink(s, st, mode)));
        }

        if (session.IsDeletedIdentity(s.Identity) || current is not null && deletedCards.Hides(current)) { current = null; status = J.Obj(("kind", "paused"), ("title", "This game was deleted"), ("detail", "It will not be recorded again. New games are recorded normally.")); }

        return J.Obj(("live", true), ("production", Production), ("testBuild", !Production), ("site", Site), ("serverShort", ServerShort), ("serverName", Site ? ServerShort : "The test server"), ("version", version), ("firstRun", ui["firstRun"]?.DeepClone()),
            ("status", status), ("civDetected", s.CivDetected || s.Game is not null || s.Lobby is not null), ("current", current), ("checks", checks.DeepClone()), ("games", games.DeepClone()),
            ("deleted", HeldDeletions()),
            ("updates", releases.State()),
            ("resources", ResourceState()),
            ("crashDiagnostics", CrashDiagnosticsState()),
            ("memoryLogging", MemoryLoggingState()),
            ("reportBusy", Volatile.Read(ref reportBusy) != 0),
            ("safeCapture", fileSettings.SafeCapture), ("livestream", LivestreamState(s, st, mode)), ("settings", ui["settings"]!.DeepClone()), ("oldGames", oldGames.State()), ("oldHintDismissed", ui["oldHintDismissed"]?.DeepClone() ?? false),
            ("test", J.Obj(("channelUrl", ChannelUrl(s.Channel)), ("streamUrl", fileSettings.StreamAddress), ("liveSiteUrl", fileSettings.LiveSiteAddress), ("settingsFile", settingsTxt), ("streamState", st?.State ?? "off"))));
    }

    JsonObject CrashDiagnosticsState()
    {
        var s = crashDiagnostics.Snapshot;
        return J.Obj(("busy", s.Busy), ("checkedAt", s.CheckedAt is { } at ? J.Ms(at) : null),
            ("status", s.Result?.Status), ("crashes", new JsonArray((s.Result?.Crashes ?? [])
                .Select(c => (JsonNode)J.Obj(("at", J.Ms(c.At)), ("application", c.Application), ("module", c.Module), ("exceptionCode", c.ExceptionCode))).ToArray())));
    }

    JsonObject ResourceState()
    {
        var r = Volatile.Read(ref latestResources);
        return J.Obj(("reason", session.TurnTransitionQuiet ? "Waiting for the new turn to settle" : workload.Reason), ("paused", !workload.CanStartHeavyWork || session.TurnTransitionQuiet),
            ("cpuPercent", r?.RecorderCpuPercent), ("systemCpuPercent", r?.SystemCpuPercent),
            ("memoryBytes", r?.WorkingSetBytes), ("peakMemoryBytes", r?.PeakWorkingSetBytes),
            ("uploadKibPerSecond", UploadRate), ("idleUploadMode", FastUploads), ("cpuBudgetPercent", workload.RecorderCpuBudgetPercent),
            ("cpuLimitPercent", fileSettings.CpuLimitPercent), ("cpuLimitError", cpuLimitError));
    }

    StreamStatus? probe; long nextProbe;

    /// <summary>For the pictures only: whether the test server answers at all (a TCP connect, nothing sent).</summary>
    StreamStatus ProbeStatus()
    {
        if (probe is not null && Environment.TickCount64 < nextProbe) return probe;
        bool up = false;
        try
        {
            var u = new Uri(fileSettings.StreamAddress);
            using var c = new System.Net.Sockets.TcpClient();
            up = c.ConnectAsync(u.Host, u.Port).Wait(800) && c.Connected;
        }
        catch { }
        nextProbe = Environment.TickCount64 + 4000;
        return probe = new StreamStatus { State = up ? "connected" : "server not reachable – queued, retrying" };
    }

    static bool IsMajor(PlayerState p) => p.Id < 22 && p.Civ is not ("CIVILIZATION_MINOR" or "CIVILIZATION_BARBARIAN");

    /// <summary>The major civs of a game for the chips: civ name, human or AI, which one is the player at this PC.</summary>
    /// Only living players: the memory has no "was ever in the game" mark, and unused slots hold a civ too (not alive).
    static JsonArray Players(IEnumerable<PlayerState> players, int? me) => J.Arr(players.Where(p => IsMajor(p) && p.Alive).Select(p => (JsonNode?)J.Obj(
        ("name", p.Slot == 3 ? "Human" : p.Slot == 1 ? "AI" : "–"), ("civ", CivName(p.Civ)),
        ("color", p.Id == me ? "#f0c860" : p.Slot == 3 ? "#5ec8bc" : "#7d8b88"), ("you", p.Id == me), ("human", p.Slot == 3), ("alive", p.Alive))));

    /// <summary>"CIVILIZATION_UC_CUBA_BATISTA" → "Cuba Batista"; mod prefixes (UC_, MC_, JFD_ …) dropped.</summary>
    public static string CivName(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "–";
        var k = Regex.Replace(key, "^CIVILIZATION_", "");
        k = Regex.Replace(k, "^(UC|MC|JFD|TCM|LS|CL|SENSHI|DMS|EW|LITE)_", "");
        return string.Join(' ', k.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    /// <summary>The stream of one game folder: off / queued (server away) / sending / sent, how far, whole saves vs changes.</summary>
    JsonObject? ReplayUiState(string context)
    {
        if (streamer is not { } sender) return null;
        var state = sender.ReadReplay(context);
        var pending = sender.ReadReplay(context, true);
        state["completionQueued"] = pending.Str("action") == "complete";
        state["keepShortRequested"] = pending["keepShort"]?.GetValue<bool>() == true;
        // The finish carries "public" with it: the site copy stays private (livestream) until the finish goes out.
        state["publicRequested"] = pending["private"] is JsonValue p && p.TryGetValue<bool>(out var priv) && !priv;
        return state;
    }

    JsonObject Upload(string? folder, StreamStatus? st, string mode)
    {
        if (mode == "off") return J.Obj(("state", "off"), ("note", "Kept on this PC. Automatic sending is disabled."), ("target", "test"));
        if (!uploadPolicy.CanSend() && !LiveServerAllowed) return J.Obj(("state", "held"), ("target", "test"), ("note", uploadPolicy.Automatic
            ? "Stored on this PC. Uploading and processing will start after Civ closes." : "Manual upload only. Automatic sending is off."));
        var (queuedBytes, queuedSaves, lastTurn, sentSaves) = folder is null ? (0L, 0, (int?)null, 0) : StreamOf(folder);
        bool down = st?.State is { } sx && (sx.StartsWith("server not reachable") || sx.StartsWith("stalled"));
        var state = down ? "queued" : st?.State is "starting" or null ? "sending" : queuedBytes > 0 || queuedSaves > 0 ? "sending" : "sent";
        var o = J.Obj(("state", state), ("target", "test"), ("server", fileSettings.StreamAddress), ("queued", queuedSaves), ("queuedBytes", queuedBytes),
            ("lastSentTurn", lastTurn), ("sentSaves", sentSaves), ("lastSentAt", st?.LastSent is { } t ? J.Ms(t) : null),
            ("wholeSaves", st?.FullSaves ?? 0), ("deltaSaves", st?.DeltaSaves ?? 0), ("eventBytes", st?.EventWireBytes ?? 0), ("saveBytes", st?.SaveWireBytes ?? 0),
            ("error", down ? st?.LastError : null));
        return o;
    }

    /// <summary>The address where the game can be watched live on the local live site (OR03 "Watch link"), or why not yet.</summary>
    JsonObject LiveLink(RecorderStatus s, StreamStatus? st, string mode)
    {
        var share = liveShare.Snapshot;
        if (!share.Requested) return J.Obj(("disabled", true), ("url", null), ("why", "Get a private livestream link from the active recorder."));
        var slot = slots.Identity == s.Identity ? slots.State : LiveSlotState.Unknown;
        string? why = share.Url is not null ? null : liveShareProblem ?? (slot.State == "busy"
            ? "Waiting for an available livestream slot. Your invite appears once the server confirms it."
            : "Confirming private access and creating your timed invite…");
        return J.Obj(("url", share.Url), ("disabled", share.Url is null), ("why", why), ("private", true),
            ("busy", slot.State == "busy"), ("position", slot.Position), ("requested", slot.Requested),
            ("scope", share.Scope), ("expiresAt", share.ExpiresAt is { } expiry ? J.Ms(expiry) : null), ("channel", share.Scope == "player"));
    }

    /// <summary>The player's own channel on the live site (OR03 "Lobby / channel"): the lobby before the game, the game after.</summary>
    string? ChannelUrl(string? channel)
    {
        if (channel is null) return null;
        var site = fileSettings.LiveSiteAddress.EndsWith('/') ? fileSettings.LiveSiteAddress : fileSettings.LiveSiteAddress + "/";
        return $"{site}live/channel/{channel}";
    }

    /// <summary>The map size's name: the game's Worlds table keeps Duel, Tiny, Small, Standard, Large, Huge as rows 0-5 (vanilla, Lekmod, Snowve alike).</summary>
    static string? WorldSizeName(int? size) => size switch { 0 => "Duel", 1 => "Tiny", 2 => "Small", 3 => "Standard", 4 => "Large", 5 => "Huge", _ => null };

    /// <summary>"Assets\Maps\Lekmap v6.3\LekmapPangaeaFractalv6.3.lua" → "LekmapPangaeaFractalv6.3".</summary>
    static string MapName(string script) => Path.GetFileNameWithoutExtension(script.Replace('\\', '/').Split('/')[^1]);

    static string? Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>The game core's name → (mod, version): "LEKMOD v35.4.003" → ("Lekmod", "35.4"), "Snowve 8.2.1" → ("Snowve", "8.2.1"), BNW → ("Vanilla", "BNW").</summary>
    public static (string? Mod, string? Version) ModOf(string? layout)
    {
        if (layout is null) return (null, null);
        if (layout.Contains("LEKMOD", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(layout, @"v?(\d+\.\d+)");
            return ("Lekmod", m.Success ? m.Groups[1].Value : null);
        }
        if (layout.Contains("Snowve", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(layout, @"(\d+\.\d+\.\d+)");
            return ("Snowve", m.Success ? m.Groups[1].Value : null);
        }
        if (layout.Contains("BNW", StringComparison.OrdinalIgnoreCase)) return ("Vanilla", "BNW");
        return (null, null);
    }

    bool civWasRunning;

    JsonArray BuildChecks(RecorderStatus s, StreamStatus? st, string mode)
    {
        var list = new JsonArray();
        var ini = AutosaveCheck.Read(AutosaveCheck.IniPath);
        if (ini is null) list.Add(J.Obj(("id", "autosave"), ("level", "info"), ("title", "Civ's autosave setting not found yet"), ("text", "Start Civ V once; then we can check that it saves every turn.")));
        else
        {
            var (between, kept) = ini.Value;
            bool everyTurn = between == 1, enough = kept is int k && k >= 10;
            if (everyTurn && enough) list.Add(J.Obj(("id", "autosave"), ("level", "ok"), ("title", "Civ saves every turn"), ("text", "Autosave is set to every turn, so the replay has every turn.")));
            else
            {
                bool running = UploadPolicy.CivRunning();
                if (civWasRunning && !running && Setting("notify.problems"))
                {
                    var t = J.Toast("warn", "Civ is closed: fix the autosave setting now?", "Civ does not save every turn, so replays would miss turns. We change two numbers and keep a copy of the old file.");
                    t["actions"] = new JsonArray(J.Obj(("label", "Fix it for me"), ("cmd", "fixAutosave"), ("primary", true)));
                    t["timeout"] = 30000;
                    Toast?.Invoke(t);
                }
                civWasRunning = running;
                var text = !everyTurn
                    ? (between is int b ? $"Civ saves only every {b} turns, so the replay would miss turns." : "Civ's autosave is not set to every turn.")
                    : $"Civ keeps only {kept} autosaves; we would like at least 10.";
                list.Add(J.Obj(("id", "autosave"), ("level", "warn"), ("title", everyTurn ? "Civ keeps few autosaves" : "Civ does not save every turn"), ("text", text + (running ? " Civ must be closed to change this." : "")),
                    ("action", J.Obj(("cmd", "fixAutosave"), ("label", "Fix it for me"), ("disabled", running), ("why", running ? "Close Civ first, then press this." : null)))));
            }
        }
        if (s.UnsupportedBackingUp) list.Add(J.Obj(("id", "unsupported"), ("level", "warn"), ("title", "Autosave backup only"),
            ("text", "This DLL has no verified realtime support. Autosaves and a diagnostic log are kept locally for manual upload after support is fixed."),
            ("action", J.Obj(("cmd", "openFolder"), ("label", "Open backup folder"), ("args", J.Obj(("path", s.UnsupportedFolder)))))));
        if (s.InGameRecorderPresent || RecorderSession.InstalledInGameRecorder() is not null)
            list.Add(J.Obj(("id", "ingame"), ("level", "info"), ("title", "The in-game recorder is also installed"),
                ("text", "Found the FFACiv Recorder mod in Civ's DLC folder. Both can run; you only need one. To use only this program, move the folder FFACiv_Recorder out of Assets\\DLC while Civ is closed."),
                ("action", J.Obj(("cmd", "openFolder"), ("label", "Show the folder"), ("args", J.Obj(("path", Path.GetDirectoryName(RecorderSession.InstalledInGameRecorder() ?? ""))))))));
        var (mod, ver) = ModOf(s.Layout);
        if (s.Game is not null && !s.State.StartsWith("Waiting"))
            list.Add(mod is null
                ? J.Obj(("id", "version"), ("level", "warn"), ("title", "This game version is not supported yet"), ("text", "Realtime recording is off for this game version. Civ keeps its own autosaves; unsupported builds keep local autosave backups and a diagnostic log when backup is on. KEK Mod games are supplied by Oblast."))
                : J.Obj(("id", "version"), ("level", "ok"), ("title", $"{mod} {ver} is supported".Replace("  ", " ")), ("text", "This game version can be recorded.")));
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(fileSettings.OutputFolder))!;
            var free = new DriveInfo(root).AvailableFreeSpace / 1e9;
            list.Add(free < 2
                ? J.Obj(("id", "disk"), ("level", "warn"), ("title", "Disk space is low"), ("text", $"Only {free:0.0} GB free on {root.TrimEnd('\\')}. Recording goes on; free some space soon."))
                : J.Obj(("id", "disk"), ("level", "ok"), ("title", "Enough disk space"), ("text", $"{free:0} GB free. A long game needs about 200 MB.")));
        }
        catch { }
        if (s.Game is not null && s.LooksPerSecond > 0)
            list.Add(s.LooksPerSecond < s.LooksPerSecondWanted - 0.5
                ? J.Obj(("id", "pace"), ("level", "info"), ("title", $"Looking {s.LooksPerSecond:0} times a second (slowed down)"),
                    ("text", $"One look takes {s.LookMs:0.0} ms on this PC right now, so the recorder looks less often ({s.LooksPerSecondWanted:0} asked) to stay under a tenth of one processor core."))
                : J.Obj(("id", "pace"), ("level", "ok"), ("title", $"Looking {s.LooksPerSecond:0} times a second"),
                    ("text", $"Units every look, cities 5 times a second, the map's tiles once a second. One look takes {s.LookMs:0.0} ms on average, about {s.LookMs * s.LooksPerSecond / 10:0.0} % of one processor core.")));
        if (!uploadPolicy.CanSend()) list.Add(J.Obj(("id", "stream"), ("level", "info"), ("title", uploadPolicy.Automatic ? "Stored for upload after Civ closes" : "Manual upload only"), ("text", "Automatic uploads are held by the recording controls on Home. Existing local copies stay queued.")));
        else if (mode == "off") list.Add(J.Obj(("id", "stream"), ("level", "info"), ("title", "Uploads are off"), ("text", "Games stay on this PC. Turn on Allow uploads on Home when ready.")));
        else if (st?.Problem is "cap" or "update" or "blocked" or "off" or "key")
        {
            var (title, text) = ProblemWords(st);
            var c = J.Obj(("id", "stream"), ("level", st.Problem is "update" or "blocked" ? "bad" : "warn"), ("title", title), ("text", text));
            if (st.Problem == "update" && st.DownloadUrl is { } dl) c["action"] = J.Obj(("cmd", "openSite"), ("label", "Get the new version"), ("args", J.Obj(("url", dl))));
            list.Add(c);
        }
        else if (st?.State.StartsWith("stalled") == true)
            list.Add(J.Obj(("id", "stream"), ("level", "warn"), ("title", fileSettings.SafeCapture ? "Upload worker stalled — restart the recorder" : "Stream stalled — starting it again"),
                ("text", fileSettings.SafeCapture ? "New uploads are held and local copies remain queued. Restart the recorder to retry." : "The sender stopped making progress, so it was started again. Local copies remain queued. Details in stream.log next to the games.")));
        else if (st?.State.StartsWith("server not reachable") == true)
            list.Add(J.Obj(("id", "stream"), ("level", "warn"), ("title", $"Can't reach {ServerShort}"),
                ("text", $"Nothing answers at {fileSettings.StreamAddress}. {WhenDown}")));
        else if (st?.State.StartsWith("connected") == true)
            list.Add(J.Obj(("id", "stream"), ("level", "ok"), ("title", $"Connected to {ServerShort}"), ("text", LiveRequested ? "Private livestream requested. Recording data sends after private access is confirmed; local copies are retained." : "Queued saves upload after Civ has stayed closed for 30 seconds and their upload countdown permits it. Local save copies are retained.")));
        else list.Add(J.Obj(("id", "stream"), ("level", "info"), ("title", $"Connecting to {ServerShort}…"), ("text", fileSettings.StreamAddress)));
        return list;
    }

    JsonObject MemoryLoggingState() => J.Obj(("enabled", session.TrackLogs),
        ("canEnable", Setting("recording") && !otherRecorder),
        ("reason", !Setting("recording") ? "Choose automatic recording first." : otherRecorder ? "Close the other recorder first." : null),
        ("readsPerSecond", LooksPerSecond()), ("cityManagementIntervalMs", 1000), ("turnQuietMs", 5000));
}
