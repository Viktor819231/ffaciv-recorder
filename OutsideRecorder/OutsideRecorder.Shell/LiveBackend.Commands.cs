using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>Commands from the window, a pop-up or the tray menu (Handle, then HandleUx for the newer screens); each may answer with { dialog?, toast? }.</summary>
sealed partial class LiveBackend
{
    // ---- commands ------------------------------------------------------------------------------------------------------
    JsonObject NotInTestBuild => J.Obj(("toast", J.Toast("info", Site ? "Not in this version yet" : "Not in the test build",
        Site ? $"This part comes later. This version records your games and sends them to {ServerShort}, where they become replays." : "This part comes later. The test build records, keeps every game on this PC and streams it to the test server on this PC.")));

    public JsonObject? Handle(string cmd, JsonObject args)
    {
        if (HandleUx(cmd, args, out var uxResult)) return uxResult;
        switch (cmd)
        {
            case "fixAutosave":
            {
                var (changes, backup, error) = AutosaveCheck.Fix(AutosaveCheck.IniPath);
                nextChecks = 0;
                if (error == "civRunning") return J.Obj(("dialog", J.Obj(("kind", "civRunning"))));
                if (error is not null) return J.Obj(("toast", J.Toast("warn", "Could not fix it", error)));
                if (changes.Count == 0) return J.Obj(("toast", J.Toast("good", "Already fine", "Civ already saves every turn.")));
                return J.Obj(("dialog", J.Obj(("kind", "fixed"), ("file", ShortPath(AutosaveCheck.IniPath)), ("backup", Path.GetFileName(backup)),
                    ("changes", J.Arr(changes.Select(c => (JsonNode?)J.Obj(("key", c.Key), ("was", c.Was), ("now", c.Now))))))));
            }
            case "pauseGame":
                if (session.Status.Paused) { session.ResumeGame(); return J.Obj(("toast", J.Toast("good", "Recording this game again", "It goes on in the same folder."))); }
                StopLivestream();
                session.PauseGame();
                return J.Obj(("toast", PausedRecordingToast()));
            case "dontRecord":
                StopLivestream();
                session.PauseGame();
                return J.Obj(("toast", PausedRecordingToast()));
            case "copyLiveLink":
            {
                var s = session.Status; StreamStatus? st; string mode;
                lock (streamLock) { st = streamer?.Status; mode = StreamMode; }
                var link = LiveLink(s, st, mode);
                if (link["busy"]?.GetValue<bool>() == true) { slots.Request(); return J.Obj(("toast", J.Toast("info", "Livestream requested", "The link will appear in a notification when a live slot is available."))); }
                if (link["url"]?.GetValue<string>() is not { } url) return J.Obj(("toast", J.Toast("warn", "No livestream link yet", link.Str("why") ?? "")));
                try { Clipboard.SetText(url); }
                catch { return J.Obj(("toast", J.Toast("warn", "Could not copy invite", "Open the stream lobby and copy its address from your browser."))); }
                var t = J.Toast("good", "Invite link copied", "Ready to share. Anyone with this private link can watch; it is not listed publicly.");
                t["actions"] = new JsonArray(J.Obj(("label", "Open in browser"), ("cmd", "openSite"), ("args", J.Obj(("url", url))), ("primary", true)));
                t["timeout"] = 15000;
                return J.Obj(("toast", t), ("copied", url));
            }
            case "setSetting":
            {
                var key = args.Str("key") ?? "";
                if (key is "trackLogs" or "captureMoves") return Handle("setMemoryLogging", J.Obj(("enabled", args["value"]?.DeepClone())));
                if (fileSettings.SafeCapture && key is "backupUnsupported" or "keepCopies") return J.Obj(("toast", J.Toast("info", "Conservative file handling", "This profile keeps local save copies and uses verified capture paths.")));
                if (key == "updates.autoSmall") return NotInTestBuild;
                if (key == "upload") return J.Obj(("toast", J.Toast("info", Site ? "Use the switch above" : "Not in the test build", Site ? $"\"Send my games to {ServerShort}\" turns sending on and off." : "Sending to ffaciv.com comes later. The test build streams to the test server on this PC (\"Live stream to the test server\").")));
                var boundary = StreamingSetupPolicy.ChangeReason(ui["settings"]!.AsObject(), key, args["value"], UploadPolicy.CivRunning(), session.Status.Lobby is not null);
                if (boundary is not null) return J.Obj(("toast", J.Toast("warn", "Prepare before your game", boundary)));
                var parts = key.Split('.');
                var node = ui["settings"]!.AsObject();
                foreach (var p in parts[..^1]) node = node[p]!.AsObject();
                if (OtherKind(node[parts[^1]], args["value"])) return J.Obj(("toast", J.Toast("warn", "Could not change it", $"\"{key}\" takes another kind of value.")));
                node[parts[^1]] = args["value"]?.DeepClone();
                if (key == "startWithWindows") { try { StartWithWindows = args["value"]?.GetValue<bool>() ?? false; } catch (Exception e) { return J.Obj(("toast", J.Toast("warn", "Could not change it", e.Message))); } ui["settings"]!["startWithWindows"] = StartWithWindows; }
                if (key == "recording")
                {
                    session.RecordingEnabled = !SetupPending && Setting("recording");
                    if (session.RecordingEnabled) session.ResumeGame();
                    else StopLivestream();
                }
                if (key is "recording" or "trackLogs" or "uploadTiming" or "stream") { ApplyCaptureSettings(); nextChecks = 0; }
                if (key == "diagnostics") diagnostics.Enabled = Setting("diagnostics", false);
                if (key == "backupUnsupported") session.BackupUnsupportedEnabled = Setting("backupUnsupported");
                if (key == "captureMoves") { ui["settings"]!["trackLogs"] = Setting("captureMoves"); ApplyCaptureSettings(); nextChecks = 0; }
                if (key == "recordSinglePlayer") session.RecordSinglePlayer = Setting("recordSinglePlayer", false);
                if (key == "looksPerSecond") { session.SampleMs = session.CaptureMoves ? 1000 / LooksPerSecond() : 1000; nextChecks = 0; }
                SaveUi();
                if (key is "stream" or "keepCopies") { RestartStream(); nextChecks = 0; }
                return null;
            }
            case "finishFirstRun":
                Onboarding.Complete(ui);
                session.RecordingEnabled = Setting("recording");
                ApplyCaptureSettings();
                try { StartWithWindows = Setting("startWithWindows"); } catch { }
                SaveUi();
                Start();
                return J.Obj(("toast", J.Toast("good", "Setup complete", "Your choices are saved. Revisit setup from Options whenever you like.")));
            case "openFolder":
            {
                var path = args.Str("path") ?? FolderOf(args.Str("game")) ?? fileSettings.OutputFolder;
                if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return null;
            }
            case "openSite":
                WebUi.OpenExternal(args.Str("url") ?? "https://ffaciv.com");
                return null;
            case "editSettings":
                try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{settingsTxt}\"") { UseShellExecute = true }); } catch { }
                return J.Obj(("toast", J.Toast("info", "settings.txt opened", "Changes there are used after the recorder is started again (tray icon → Quit, then start it).")));
            case "sendReport":
                if (Interlocked.Exchange(ref reportBusy, 1) != 0)
                    return J.Obj(("toast", J.Toast("info", "Your report is already being prepared", "The recorder will confirm when the local file is ready.")));
                var reportArgs = (JsonObject)args.DeepClone();
                Task.Run(() =>
                {
                    try { if (SaveReport(reportArgs)["toast"] is JsonObject toast) Toast?.Invoke(toast); }
                    finally { Interlocked.Exchange(ref reportBusy, 0); }
                });
                return J.Obj(("toast", J.Toast("info", "Preparing your local report", "It will be saved on your Desktop. Nothing is uploaded.")));
            case "scanOldGames": oldGames.Scan(); return null;
            case "recordAnyway":
                session.RecordThisSinglePlayerGame();
                return J.Obj(("toast", J.Toast("good", "Recording this single-player game", "Only this one. Single-player games stay off in Settings.")));
            case "checkUpdates": releases.Check(asked: true); return null;
            case "updateNow": WebUi.OpenExternal(releases.DownloadUrl); return null;
            case "updateLater": releases.Later(args.Str("when") ?? "tomorrow"); return null;
            case "deleteGame":
            {
                var game = games.OfType<JsonObject>().FirstOrDefault(g => g.Str("id") == args.Str("game"));
                var context = game?.Str("context"); var identity = game?.Str("identity");
                if (context is null && game?.Str("siteGame") is { } siteGame && streamer is { } knownSender)
                {
                    var known = new GameIndex(fileSettings.OutputFolder).Load().LastOrDefault(e => knownSender.ReadReplay(e.Context).Str("game") == game.Str("siteMod") + "/" + siteGame);
                    if (known is not null) { context = known.Context; identity = known.Identity; }
                }
                if (args.Str("game") == "live")
                {
                    context = session.Status.Context ?? args.Str("context");
                    identity = session.Status.Identity ?? new GameIndex(fileSettings.OutputFolder).Load().LastOrDefault(e => e.Context == context)?.Identity;
                    if (identity is not null) session.PauseGame();
                }
                if (game is null && context is null) return J.Obj(("toast", J.Toast("warn", "Game no longer found", "Refresh your games and try again.")));
                // Older recorder builds left session folders without games.json entries or server IDs.
                // They are local copies, so there is no website withdrawal to queue.
                if (context is null && game?.Str("siteGame") is null)
                {
                    var folder = game?.Str("folder");
                    if (folder is null) return J.Obj(("toast", J.Toast("warn", "Game no longer found", "Refresh your games and try again.")));
                    LocalRecordingDeletion.Delete(fileSettings.OutputFolder, folder);
                    if (identity is not null) session.DeleteGameIdentity(identity);
                    factsCache.Remove(folder);
                    deletedCards.Remember(game, context, identity);
                    games = deletedCards.Filter(games); siteGames = deletedCards.Filter(siteGames);
                    nextLibrary = nextChecks = 0;
                    return J.Obj(("toast", J.Toast("good", "Local recording deleted", "Removed this recorder's copy. Civ's original saves were kept.")));
                }
                if (streamer is not { } sender) return J.Obj(("toast", J.Toast("warn", "Recorder is starting", "Try again in a moment.")));
                bool current = identity is not null && identity == session.Status.Identity && session.Status.Recording;
                // held for 7 days (the user, 2026-10-31: "a way to regret deleting replays"): hidden on the site now, Undo
                // brings it back with its upload choice and visibility; the real withdrawal comes when the hold ends
                var card = HeldCard(game, context, identity);
                if (context is not null)
                {
                    var before = delayedUploads.Get(context);
                    card["uploadDecision"] = J.Obj(("state", before.State), ("endedAt", before.EndedAt is { } e ? J.Ms(e) : null),
                        ("dueAt", before.DueAt is { } du ? J.Ms(du) : null), ("postponed", before.Postponed), ("endPrompted", before.EndPrompted), ("hold", before.Hold));
                }
                if (identity is not null) session.DeleteGameIdentity(identity);
                if (context is not null) delayedUploads.KeepLocal(context);
                sender.QueueReplayDeletion(context, game?.Str("siteMod"), game?.Str("siteGame"), waitForEnd: current, hold: Streamer.DeletionHold, card: card,
                    wasPrivate: game?["private"]?.GetValue<bool>() == true || game?["replay"]?["private"]?.GetValue<bool>() == true);
                deletedCards.Remember(game, context, identity);
                games = deletedCards.Filter(games); siteGames = deletedCards.Filter(siteGames);
                nextLibrary = nextChecks = 0;
                _ = Task.Run(async () => { try {
                    using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await sender.HideHeldDeletionsNowAsync(ct.Token);   // "hidden from the website now" is true now, mid-game too
                    await sender.FlushReplayDeletionsAsync(ct.Token);
                    nextLibrary = nextChecks = 0; }
                    catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { /* the regular deletion pass hides it later */ } });
                var deleted = J.Toast("info", "Recording deleted · Undo for 7 days", "Hidden from your list and from the website now. It is erased for good in 7 days; until then Undo brings it back (also under Previous games). Civ's original saves are kept.");
                deleted["actions"] = new JsonArray(J.Obj(("label", "Undo"), ("cmd", "undoDelete"), ("args", J.Obj(("key", ReplayDeletionTarget.KeyOf(context, game?.Str("siteMod"), game?.Str("siteGame"))))), ("primary", true)));
                deleted["timeout"] = 15000;
                return J.Obj(("toast", deleted));
            }
            case "undoDelete": return UndoDelete(args.Str("key"));
            case "showSaves":
                if (args.Str("game") is { } shown) { if (savesShown.Count >= 5) savesShown.Clear(); savesShown.Add(shown); nextChecks = 0; }
                return null;
            case "completeGame":
            case "keepShortGame":
            case "replayChoice":
            {
                var game = games.OfType<JsonObject>().FirstOrDefault(g => g.Str("id") == args.Str("game"));
                var context = args.Str("context") ?? game?.Str("context") ?? session.Status.Context;
                if (streamer is not { } sender) return J.Obj(("toast", J.Toast("warn", "Sending is off", "Switch sending on in Settings first.")));
                var choice = new JsonObject();
                if (cmd == "completeGame") { choice["action"] = "complete"; choice["keepShort"] = true; if (session.Status.Context == context) { choice["waitForEnd"] = true; session.PauseGame(); } }
                // finished and uploaded = public, unless the player chose privacy by hand (the user, 2026-11-01)
                if (cmd == "completeGame" && context is not null && EndPrivateWhilePlaying(context)) choice["private"] = false;
                if (cmd == "keepShortGame") choice["keepShort"] = true;
                if (cmd == "replayChoice") foreach (var key in new[] { "private", "restricted" }) if (args[key] is { } v) choice[key] = v.DeepClone();
                if (cmd == "replayChoice" && args["private"] is not null && context is not null) { EndPrivateWhilePlaying(context); delayedUploads.EndHold(context); }
                if (game?.Str("siteGame") is { } siteGame && cmd == "replayChoice")
                    _ = Task.Run(async () => { try { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await sender.SetReplayAccessAsync(game.Str("siteMod")!, siteGame, choice, ct.Token); nextLibrary = 0; }
                        catch (Exception e) { Toast?.Invoke(J.Toast("warn", "Access change not saved", e.Message)); } });
                else if (context is not null)
                {
                    sender.ChooseReplay(context, choice);
                    // the player's choice goes now, also while uploads wait for Civ's main menu; a finish waits for the last
                    // save and log, and its "public" with it (Mark as done can be pressed mid-game): only its keep goes now
                    bool onSite = sender.ReadReplay(context)["state"] is not null;
                    if (cmd == "replayChoice" || cmd == "keepShortGame" && onSite) SendAccessChoice(sender, context, choice);
                    else if (cmd == "completeGame" && onSite) SendAccessChoice(sender, context, new JsonObject { ["keepShort"] = true });
                }
                else return J.Obj(("toast", J.Toast("warn", "Not received yet", "Wait for the game to reach the server.")));
                nextChecks = 0;
                return J.Obj(("toast", J.Toast("info", cmd == "completeGame" ? "Finishing this game" : "Choice queued", cmd == "completeGame" ? "Recording kept, including games under 50 turns. Capture stops for this recording; finishing waits for upload permission and server acknowledgement. Replay visibility is unchanged." : "The server confirms the change in the game card.")));
            }
            case "setBlocked":
            {
                var g = games.OfType<JsonObject>().FirstOrDefault(x => x.Str("id") == args.Str("game"));
                if (g is null) return null;
                if (g["canBlock"]?.GetValue<bool>() != true) return J.Obj(("toast", J.Toast("info", "Not now", g.Str("blockWhy") ?? "")));
                bool blocked = args["blocked"]?.GetValue<bool>() ?? true;
                visibility.Set(g.Str("identity")!, g.Str("context"), blocked);
                nextChecks = 0;
                return J.Obj(("toast", J.Toast(blocked ? "info" : "good", blocked ? "Game blocked" : "Game unblocked",
                    blocked ? "Hidden from everyone on the site. Unblock brings it back." : "It shows on the site again.")));
            }
            case "addOldFolder":
            {
                using var pick = new FolderBrowserDialog { Description = "A folder with old Civ V saves (its subfolders and zip files are looked at too)", UseDescriptionForTitle = true };
                if (pick.ShowDialog() == DialogResult.OK) oldGames.AddFolder(pick.SelectedPath);
                return null;
            }
            case "uploadOldGames":
            {
                var ids = (args["ids"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>().ToList() ?? [];
                if (ids.Count == 0) return J.Obj(("toast", J.Toast("info", "No game picked", "Tick the games to upload. Only whole games are uploaded.")));
                // the answer to the first-upload question comes with the upload itself, and is remembered for the next ones
                if (args.Str("format") is "delta" or "full") { ui["settings"]!["oldGamesUpload"] = args.Str("format"); SaveUi(); }
                if (oldGames.Upload(ids) is { } why) return J.Obj(("toast", J.Toast("warn", "Not uploading", why)));
                return J.Obj(("toast", J.Toast("good", ids.Count == 1 ? "Uploading 1 game" : $"Uploading {ids.Count} games", Site ? $"To {ServerShort}, where they become replays." : "To the test server on this PC. Nothing is public in the test build.")));
            }
            case "setOldGamesUpload":
            {
                var format = args.Str("format");
                if (format is not ("delta" or "full")) return J.Obj(("toast", J.Toast("warn", "Choose an upload size", "Choose a smaller upload or whole saves.")));
                ui["settings"]!["oldGamesUpload"] = format; SaveUi();
                return J.Obj(("toast", J.Toast("good", "Upload size saved", format == "delta"
                    ? "Old games upload smaller: this PC sends only what changed from save to save. The replay is the same."
                    : "Old games upload as whole saves: the website does all the work. The replay is the same.")));
            }
            case "stopOldUpload": oldGames.Stop(); return null;
            case "dismissOldHint": ui["oldHintDismissed"] = true; SaveUi(); return null;
            case "chooseFolder":
                return J.Obj(("toast", J.Toast("info", "Games folder", "Changing the folder comes later. It is set in settings.txt (output_folder) for now.")));
            default:
                return NotInTestBuild;
        }
    }

    bool HandleUx(string cmd, JsonObject args, out JsonObject? result)
    {
        result = null;
        if (cmd == "checkCrashRecords")
        {
            if (!Setting("diagnostics", false)) {
                result = J.Obj(("toast", J.Toast("info", "Recorder diagnostics are off", "Enable diagnostic collection in Recorder to check crash records.")));
                return true;
            }
            _ = crashDiagnostics.RefreshAsync();
            result = J.Obj(("toast", J.Toast("info", "Checking local crash records", "Looking for Civ V faults reported by Windows in the last 24 hours. Nothing is uploaded.")));
            return true;
        }
        if (cmd == "refreshLobby")
        {
            session.RequestLobbyRefresh(); nextChecks = 0;
            result = J.Obj(("toast", J.Toast("info", "Lobby refresh requested", BackgroundWorkAllowed
                ? "Checking the lobby again. Its details will update here."
                : "The refresh is queued until computer resources are available. See Checks.")));
            return true;
        }
        if (cmd == "setMemoryLogging")
        {
            bool enabled = args["enabled"]?.GetValue<bool>() == true;
            var boundary = StreamingSetupPolicy.ChangeReason(ui["settings"]!.AsObject(), "trackLogs", JsonValue.Create(enabled), UploadPolicy.CivRunning(), session.Status.Lobby is not null);
            if (boundary is not null) { result = J.Obj(("toast", J.Toast("warn", "Prepare before your game", boundary))); return true; }
            if (enabled && (!Setting("recording") || otherRecorder))
            {
                result = J.Obj(("toast", J.Toast("warn", "Memory logs are not enabled", MemoryLoggingState().Str("reason") ?? "Choose automatic recording first.")));
                return true;
            }
            if (enabled && !Setting("experimentalMemoryOptIn", false)) ui["settings"]!["looksPerSecond"] = 4;
            if (session.Status.Context is { } previous && session.TrackLogs != enabled) delayedUploads.KeepLocal(previous);
            ui["settings"]!["experimentalMemoryOptIn"] = enabled;
            ui["settings"]!["trackLogs"] = enabled;
            ui["settings"]!["captureMoves"] = enabled;
            if (!enabled) StopLivestream();
            ApplyCaptureSettings(); SaveUi(); nextChecks = nextUx = 0;
            result = J.Obj(("toast", J.Toast("good", enabled ? "Memory logs enabled" : "Memory logs off", enabled
                ? "External read-only observations start with a supported game. Livestreaming still needs its own Start action."
                : "The recorder uses save copies only. Existing logs are kept; the previous recording stays local until you choose to upload it.")));
            return true;
        }
        if (cmd == "simpleSetup")
        {
            var settings = ui["settings"]!.AsObject();
            // Simple setup only turns things off, except sending: that one may not start in the middle of a game.
            var boundary = StreamingSetupPolicy.ChangeReason(settings, "stream", JsonValue.Create(true), UploadPolicy.CivRunning(), session.Status.Lobby is not null);
            if (boundary is not null) { result = J.Obj(("toast", J.Toast("warn", "Prepare before your game", boundary))); return true; }
            if (session.Status.Context is { } previous && session.TrackLogs) delayedUploads.KeepLocal(previous);
            CapturePreferences.ApplySimpleSetup(settings);
            StopLivestream();
            session.RecordingEnabled = !SetupPending;
            if (session.RecordingEnabled) session.ResumeGame();
            ApplyCaptureSettings();
            session.SampleMs = 1000;
            SaveUi(); RestartStream(); nextChecks = nextUx = 0;
            result = J.Obj(("toast", J.Toast("good", "Simple setup chosen", "The recorder copies Civ's autosaves and uploads them after the game. Nothing reads Civ's memory.")));
            return true;
        }
        if (cmd == "setUploadFormat")
        {
            var format = args.Str("format");
            var boundary = StreamingSetupPolicy.ChangeReason(ui["settings"]!.AsObject(), "saveUpload", JsonValue.Create(format), UploadPolicy.CivRunning(), session.Status.Lobby is not null);
            if (boundary is not null) { result = J.Obj(("toast", J.Toast("warn", "Prepare before your game", boundary))); return true; }
            if (format is not ("delta" or "full")) result = J.Obj(("toast", J.Toast("warn", "Choose an upload method", "Choose local preparation or whole save files.")));
            else
            {
                ui["settings"]!["saveUpload"] = format; SaveUi(); RestartStream(); nextChecks = 0;
                result = J.Obj(("toast", J.Toast("good", "Upload method saved", format == "delta" ? "This PC prepares smaller changes. The website still creates the replay." : "Whole save files upload for processing on the website.")));
            }
            return true;
        }
        if (cmd == "setCaptureMethod")
        {
            var method = args.Str("method");
            if (method == "memory" && StreamingSetupPolicy.ChangeReason(ui["settings"]!.AsObject(), "trackLogs", JsonValue.Create(true), UploadPolicy.CivRunning(), session.Status.Lobby is not null) is { } boundary)
            { result = J.Obj(("toast", J.Toast("warn", "Prepare before your game", boundary))); return true; }
            if (method is not ("saves" or "memory" or "manual"))
                result = J.Obj(("toast", J.Toast("warn", "Choose a recording method", "Choose automatic save copies, experimental memory recording, or manual saves.")));
            else
            {
                ui["settings"]!["recording"] = method != "manual";
                // The separate memory page owns this preference. Returning to automatic capture keeps it.
                if (method == "memory")
                {
                    ui["settings"]!["trackLogs"] = true;
                    ui["settings"]!["captureMoves"] = true;
                    ui["settings"]!["experimentalMemoryOptIn"] = true;
                }
                if (method == "memory") ui["settings"]!["looksPerSecond"] = 4;
                session.RecordingEnabled = !SetupPending && method != "manual";
                if (session.RecordingEnabled) session.ResumeGame();
                if (method != "memory") { ui["settings"]!["uploadTiming"] = "after"; StopLivestream(); }
                ApplyCaptureSettings();
                session.SampleMs = session.CaptureMoves ? 1000 / LooksPerSecond() : 1000;
                SaveUi(); nextChecks = 0;
                result = J.Obj(("toast", J.Toast("good", "Recording method saved", method == "manual"
                    ? "Keep your own saves and select a whole game in Manual upload. Existing upload choices are kept."
                    : method == "memory" ? "Save copies and experimental memory observations selected. Livestreaming stays off until you get a link on Recorder."
                    : "The recorder will keep autosave copies. Your separate memory logging choice is kept.")));
            }
            return true;
        }
        if (cmd == "startLivestream")
        {
            var current = session.Status;
            StreamStatus? st; lock (streamLock) st = streamer?.Status;
            bool active = current.Context is not null && current.Identity is not null && (current.Recording || current.Game is not null);
            bool latestOnly = active && args["startCurrentSave"]?.GetValue<bool>() == true;
            var existingShare = liveShare.Snapshot;
            var scope = existingShare.Requested ? existingShare.Scope : "player";
            var reason = StreamingSetupPolicy.EnableReason(ui["settings"]!.AsObject(), UploadPolicy.CivRunning(), current.Lobby is not null, active, current.Paused, StreamingSetupPolicy.QueuedSaves(current.Folder), latestOnly);
            bool futureOnly = active && !latestOnly && scope == "player" && reason is not null;
            if (futureOnly) reason = StreamingSetupPolicy.FutureInviteReason(ui["settings"]!.AsObject(), UploadPolicy.CivRunning(), current.Lobby is not null);
            if (SetupPending || otherRecorder) reason = "Finish Recorder setup and close any other recorder instance first.";
            if (reason is not null) { result = J.Obj(("toast", J.Toast("warn", "Prepare before your game", reason))); return true; }
            if (existingShare.Requested && (!latestOnly || !waitingForFirstGame)) return true;
            if (existingShare.Requested && (existingShare.Token is null || Volatile.Read(ref lobbyInviteBusy) != 0))
            { result = J.Obj(("toast", J.Toast("info", "Invite still preparing", "Wait for the invite link to finish preparing, then choose Stream current save."))); return true; }
            int hours = existingShare.Requested ? liveShareHours : args["durationHours"]?.GetValue<int>() ?? 4;
            if (scope is not ("game" or "player") || hours is not (1 or 4 or 8 or 24)) { result = J.Obj(("toast", J.Toast("warn", "Choose a valid invite", "Choose this game or follow player, for 1, 4, 8 or 24 hours."))); return true; }
            if (StreamingSetupPolicy.NeedsSetup(ui["settings"]!.AsObject()) && args["configure"]?.GetValue<bool>() != true)
            { result = J.Obj(("toast", J.Toast("info", "Review streaming settings", "Confirm automatic recording and experimental player-action logs in the streaming setup first."))); return true; }
            LiveSaveWindow? saveWindow = null;
            if (latestOnly) {
                var saves = current.Folder is null ? [] : Streamer.SavesOf(current.Folder);
                if (saves.Count == 0) { result = J.Obj(("toast", J.Toast("info", "Waiting for an autosave", "No settled save is available yet. Try after Civ creates its next autosave."))); return true; }
                saveWindow = new LiveSaveWindow(current.Context!, saves);
            }
            preexistingShareContexts = new GameIndex(fileSettings.OutputFolder).Load().Select(g => g.Context).ToHashSet();
            if (current.Context is not null) preexistingShareContexts.Add(current.Context);
            pendingShareChannel = current.Channel ?? Channel.IdOf(fileSettings.OutputFolder);
            var streamContext = active && !futureOnly ? current.Context : pendingShareChannel;
            if (streamContext is null) { result = J.Obj(("toast", J.Toast("info", "Wait for a recording", "No recording context is available yet."))); return true; }
            // Establish explicit sharing intent before restarting any sender or changing capture settings.
            if (!existingShare.Requested) liveShare.Start(scope, hours, streamContext, active && !futureOnly ? current.Identity : null);
            liveSaveWindow = saveWindow;
            waitingForFirstGame = !active || futureOnly;
            liveShareHours = hours; liveShareProblem = null; livestreamStarting = true;
            if (args["configure"]?.GetValue<bool>() == true) {
                ui["settings"]!["recording"] = true;
                ui["settings"]!["experimentalMemoryOptIn"] = true;
                ui["settings"]!["trackLogs"] = true;
                ui["settings"]!["captureMoves"] = true;
                if (!session.TrackLogs) ui["settings"]!["looksPerSecond"] = 4;
                session.ResumeGame(); ApplyCaptureSettings(); SaveUi();
            }
            RestartStream();
            slots.Start();
            if (waitingForFirstGame) _ = PrepareLobbyInvite(current);
            result = J.Obj(("toast", J.Toast("info", latestOnly ? "Starting from current save" : "Preparing private invite", latestOnly ? "Existing server data remains available. The latest settled save and new saves can upload; older unsent saves stay on this PC. Waiting for private access and a live slot before sending." : "The link can be prepared before Civ starts. No previous games are uploaded by enabling it. Private access is checked again for each new recording.")));
            return true;
        }
        if (cmd == "stopLivestream")
        {
            StopLivestream();
            result = J.Obj(("toast", J.Toast("info", "Ending private livestream", "Releasing your live slot. Local recording continues.")));
            return true;
        }
        if (cmd is not ("uploadGameNow" or "postponeUpload" or "confirmGameFinished" or "continueGame")) return false;
        var game = games.OfType<JsonObject>().FirstOrDefault(g => g.Str("id") == args.Str("game"));
        var context = game?.Str("context") ?? (args.Str("game") == "live" ? session.Status.Context : null);
        if (context is null) { result = J.Obj(("toast", J.Toast("warn", "Game not found", "Refresh Games and try again."))); return true; }
        switch (cmd)
        {
            case "continueGame":
                delayedUploads.KeepLocal(context);
                if (session.Status.Context == context) session.ResumeGame();
                result = J.Obj(("toast", J.Toast("info", "Game kept on this PC", "Automatic upload is paused for this game. Choose Upload now when you finish.")));
                break;
            case "postponeUpload":
                int hours = args["hours"]?.GetValue<int>() ?? 2;
                bool hiding = delayedUploads.Get(context).Hold == "hidden";
                if (hours == 0) delayedUploads.KeepLocal(context);
                else delayedUploads.Postpone(context, TimeSpan.FromHours(Math.Clamp(hours, 2, 24 * 30)));
                result = J.Obj(("toast", hours == 0 ? J.Toast("good", "Automatic upload cancelled", "This game stays on this PC until you choose Upload now.")
                    : hiding ? J.Toast("good", "Kept hidden longer", "A warning appears five minutes before it goes public.")
                    : J.Toast("good", "Upload postponed", "A new warning will appear five minutes before upload.")));
                break;
            default:
                bool wasHidden = delayedUploads.Get(context).Hold == "hidden";
                delayedUploads.UploadNow(context);
                var choice = new JsonObject { ["action"] = "complete", ["keepShort"] = true, ["waitForEnd"] = true };
                bool livestreamPrivate = EndPrivateWhilePlaying(context);
                if (livestreamPrivate || cmd == "confirmGameFinished" || wasHidden) choice["private"] = false;
                if (wasHidden && streamer is { } shown) RevealHeldReplay(shown, context, now: true);
                if (cmd == "confirmGameFinished") { choice["restricted"] = false; if (liveShare.Snapshot.Scope == "game") StopLivestream(); }
                streamer?.ChooseReplay(context, choice);
                // already on the site (a livestream sent it): public + keep go now; only the finish waits for the last save and log
                bool onSite = streamer?.ReadReplay(context)["state"] is not null;
                if (onSite && !wasHidden && streamer is { } publisher) SendAccessChoice(publisher, context, choice);
                if (onSite && game?["queuedSaves"]?.GetValue<int>() == 0 && choice["private"] is not null)
                    result = J.Obj(("toast", J.Toast("good", $"Going public on {ServerShort}", "Every save is already on the site. The replay is made when Civ is at its main menu or closed, or an hour after the last save.")));
                else result = J.Obj(("toast", J.Toast("info", "Upload queued", uploadPolicy.CanSend()
                    ? session.HasPendingAutosaves || !workload.CanStartHeavyWork
                        ? "Waiting for pending save copies and available resources. Your upload choice is saved."
                        : "Sending when the server is available. The game card will confirm processing and provide your replay link."
                    : "Ready to upload after Civ closes. Keep the recorder running, or reopen it later.")));
                break;
        }
        nextChecks = nextLibrary = 0;
        return true;
    }

    // ---- the Saves list (the user, 2026-10-31: "more feedback on when things go wrong … see what savefiles the server has") ----
}
