using System.Text.Json.Nodes;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

sealed partial class LiveBackend
{
    readonly DelayedUploads delayedUploads;
    long nextUx;
    string? detectedCapture;
    volatile bool livestreamStarting;
    int lobbyInviteBusy;
    readonly LiveShareSession liveShare = new();
    int liveShareHours = 4;
    string? pendingShareChannel;
    bool waitingForFirstGame;
    LiveSaveWindow? liveSaveWindow;
    HashSet<string> preexistingShareContexts = [];
    string? liveShareProblem;
    readonly HashSet<string> revokedInvites = [];
    bool revokesLoaded;
    volatile bool revokingInvites;

    void LoadInviteRevocations()
    {
        if (revokesLoaded) return;
        revokesLoaded = true;
        try { foreach (var value in JsonNode.Parse(SafeFiles.ReadAllText(Path.Combine(fileSettings.OutputFolder, "live-share-revocations.json")))!.AsArray())
            if (value?.GetValue<string>() is { } token) revokedInvites.Add(token); } catch { }
    }

    void SaveInviteRevocations()
    {
        LoadInviteRevocations();
        try { SafeFiles.Replace(Path.Combine(fileSettings.OutputFolder, "live-share-revocations.json"), new JsonArray(revokedInvites.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()).ToJsonString()); }
        catch { /* local upload authorization is already revoked, even if the retry list cannot be written */ }
    }

    async Task FlushInviteRevocations()
    {
        lock (revokedInvites)
        {
            if (revokingInvites) return;
            LoadInviteRevocations();
            if (revokedInvites.Count == 0) return;
            revokingInvites = true;
        }
        try
        {
            string[] tokens; lock (revokedInvites) tokens = revokedInvites.ToArray();
            foreach (var token in tokens)
            {
                try
                {
                    if (streamer is not { } sender) return;
                    using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await sender.RevokeLiveShareAsync(token, ct.Token);
                    lock (revokedInvites) { revokedInvites.Remove(token); SaveInviteRevocations(); }
                }
                catch { return; }
            }
        }
        finally { lock (revokedInvites) revokingInvites = false; }
    }

    // The user, 2026-11-01: "upload should make it public and generate a preview page". A private livestream keeps its
    // recording private only while the game is played: the finished, uploaded game goes public, so the site lists it with
    // its preview and game page. A replay the player made private (or public) by hand keeps that choice.
    readonly HashSet<string> privateWhilePlaying = [];
    bool privateWhilePlayingLoaded;
    string PrivateWhilePlayingFile => Path.Combine(fileSettings.OutputFolder, "private-while-playing.json");

    void LoadPrivateWhilePlaying()
    {
        if (privateWhilePlayingLoaded) return;
        privateWhilePlayingLoaded = true;
        try { foreach (var value in JsonNode.Parse(SafeFiles.ReadAllText(PrivateWhilePlayingFile))!.AsArray())
            if (value?.GetValue<string>() is { } context) privateWhilePlaying.Add(context); } catch { }
    }

    void SavePrivateWhilePlaying()
    {
        try { SafeFiles.Replace(PrivateWhilePlayingFile, new JsonArray(privateWhilePlaying.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()).ToJsonString()); }
        catch { /* at worst the finished replay stays private until the player makes it public */ }
    }

    /// <summary>The livestream made this recording private: its replay goes public once the game is finished and uploaded.</summary>
    void MarkPrivateWhilePlaying(string context)
    {
        lock (privateWhilePlaying) { LoadPrivateWhilePlaying(); if (privateWhilePlaying.Add(context)) SavePrivateWhilePlaying(); }
    }

    /// <summary>Forgets the mark; true when the recording was private only because of the livestream (so it goes public now).</summary>
    bool EndPrivateWhilePlaying(string context)
    {
        lock (privateWhilePlaying) { LoadPrivateWhilePlaying(); if (!privateWhilePlaying.Remove(context)) return false; SavePrivateWhilePlaying(); return true; }
    }

    void PublishFinishedReplay(Streamer sender, string context) => _ = Task.Run(async () =>
    {
        try { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await sender.ApplyReplayChoiceAsync(context, new JsonObject { ["private"] = false }, ct.Token); nextLibrary = nextChecks = 0; }
        catch { /* the choice is queued: the next state check sends it */ }
    });

    // The user, 2026-11-02: "wait 2 hours to become visible" instead of waiting 2 hours to upload. A finished game uploads
    // as soon as Civ is in its main menu or closed, hidden on the site, and goes public two hours after the game.
    readonly Dictionary<string, long> holdRetryAt = [];
    int revealing;
    bool HoldWorkDue(string context)
    {
        lock (holdRetryAt) { if (holdRetryAt.GetValueOrDefault(context) > Environment.TickCount64) return false; holdRetryAt[context] = Environment.TickCount64 + 30000; return true; }
    }

    /// <summary>Makes the finished game's site copy private before any of it is sent; one already on the site keeps its visibility.</summary>
    void HideUntilPublic(Streamer sender, string context)
    {
        if (!HoldWorkDue(context)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // a livestream or an earlier part of this game reached the site already: its visibility stays as it is
                if (sender.ReadReplay(context)["state"] is not null) { delayedUploads.MarkHold(context, "kept"); nextChecks = 0; return; }
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (await sender.ApplyReplayChoiceAsync(context, new JsonObject { ["private"] = true, ["initialize"] = true }, ct.Token)
                    && sender.ReadReplay(context)["private"]?.GetValue<bool>() == true)
                { delayedUploads.MarkHold(context, "hidden"); nextChecks = 0; }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException) { /* tried again in 30 s */ }
        });
    }

    /// <summary>The hidden time is over (or the player chose Make public now): the replay goes public, even mid-game.</summary>
    void RevealHeldReplay(Streamer sender, string context, bool now = false)
    {
        if (!HoldWorkDue(context) && !now) return;
        Interlocked.Increment(ref revealing);
        _ = Task.Run(async () =>
        {
            try
            {
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (await sender.ApplyReplayChoiceAsync(context, new JsonObject { ["private"] = false }, ct.Token)
                    || sender.ReplayChoiceError?.StartsWith("HTTP 410") == true)
                { delayedUploads.Revealed(context); nextLibrary = nextChecks = 0; }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException) { /* tried again in 30 s */ }
            finally { Interlocked.Decrement(ref revealing); }
        });
    }

    void StopLivestream()
    {
        livestreamStarting = false;
        waitingForFirstGame = false; pendingShareChannel = null; preexistingShareContexts = []; liveSaveWindow = null;
        var token = liveShare.Stop();
        slots.Stop();
        if (token is not null) _ = QueueInviteRevocation(token);
        else _ = FlushInviteRevocations();
    }

    Task QueueInviteRevocation(string token)
    {
        lock (revokedInvites) { revokedInvites.Add(token); SaveInviteRevocations(); }
        return FlushInviteRevocations();
    }

    void CheckLivestreamConsent()
    {
        _ = FlushInviteRevocations();
        var state = liveShare.Snapshot;
        if (!state.Requested && (slots.Enabled || livestreamStarting)) StopLivestream();
        else if (state.Requested && (!Setting("recording") || !session.TrackLogs || session.Status.Paused && !waitingForFirstGame)) StopLivestream();
        else if (state.Requested && slots.State.State is "off" or "unavailable")
        {
            StopLivestream();
            liveShareProblem = "Livestreaming is not enabled on this website yet. Local recording continues.";
            Toast?.Invoke(J.Toast("warn", "Livestream unavailable", liveShareProblem));
        }
        else if (state.Requested && state.Scope == "player" && state.Token is null && (!session.Status.Recording || waitingForFirstGame))
            _ = PrepareLobbyInvite(session.Status);
    }

    async Task PrepareLobbyInvite(RecorderStatus s)
    {
        var channel = s.Channel ?? pendingShareChannel;
        if (channel is null || !LiveControlAllowed || Interlocked.Exchange(ref lobbyInviteBusy, 1) != 0) return;
        try
        {
            if (streamer is not { } sender) return;
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            bool prepared = await liveShare.PrepareAsync(channel, async () =>
                await sender.ApplyReplayChoiceAsync(channel, new JsonObject { ["private"] = true, ["initialize"] = true }, ct.Token)
                && sender.ReadReplay(channel)["private"]?.GetValue<bool>() == true);
            if (!prepared) { liveShareProblem = sender.ReplayChoiceError ?? "Waiting for the website to acknowledge private access."; return; }
            if (session.Status.Recording && !waitingForFirstGame) return;
            bool ready = await liveShare.BindAsync(channel,
                token => sender.BindLiveShareAsync(token, "player", liveShareHours, null, channel, channel, ct.Token, lobby: true), QueueInviteRevocation);
            if (ready) { liveShareProblem = null; nextChecks = 0; }
        }
        catch (Exception e) { liveShareProblem = e.Message; }
        finally { livestreamStarting = false; Interlocked.Exchange(ref lobbyInviteBusy, 0); }
    }

    async Task<bool> PrepareLivestreamContext(RecorderStatus s)
    {
        if (StreamingSetupPolicy.AwaitFreshContext(waitingForFirstGame, s.Context, preexistingShareContexts)) {
            liveShareProblem = "Your invite is for a future game. The current recording and its backlog stay excluded.";
            return false;
        }
        if (s.Recording) waitingForFirstGame = false;
        if (s.Context is null || !liveShare.Accepts(s.Context, s.Identity)) { if (liveShare.Snapshot.Requested && liveShare.Snapshot.Scope == "game") StopLivestream(); return false; }
        if (!BackgroundWorkAllowed || streamer is not { } sender) return false;
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            bool ready = await liveShare.PrepareAsync(s.Context, async () =>
            {
                bool wasPrivate = sender.ReadReplay(s.Context)["private"]?.GetValue<bool>() == true;
                bool made = await sender.ApplyReplayChoiceAsync(s.Context, new JsonObject { ["private"] = true, ["initialize"] = true }, ct.Token)
                    && sender.ReadReplay(s.Context)["private"]?.GetValue<bool>() == true;
                if (made && !wasPrivate) MarkPrivateWhilePlaying(s.Context);
                return made;
            }, s.Identity);
            if (!ready) liveShareProblem = sender.ReplayChoiceError ?? "Waiting for the server to confirm private access. No recording data is sent yet.";
            return ready;
        }
        catch (Exception e) { liveShareProblem = e.Message; return false; }
    }

    async Task<bool> BindLivestreamShare(RecorderStatus s)
    {
        if (s.Context is null || s.Identity is null || streamer is not { } sender || session.Status.Context != s.Context) return false;
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            bool ready = await liveShare.BindAsync(s.Context,
                token => sender.BindLiveShareAsync(token, liveShare.Snapshot.Scope, liveShareHours, s.Identity, s.Channel, s.Context, ct.Token),
                QueueInviteRevocation, s.Identity);
            if (ready) { livestreamStarting = false; liveShareProblem = null; nextChecks = 0; }
            return ready;
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            StopLivestream();
            liveShareProblem = "This website needs the livestream-link update before sharing.";
            Toast?.Invoke(J.Toast("warn", "Livestream link unavailable", liveShareProblem));
            return false;
        }
        catch (Exception e) { liveShareProblem = e.Message; return false; }
    }

    bool CanUploadRecording(GameEntry game)
    {
        if (!BackgroundWorkAllowed || session.HasPendingAutosaves || session.IsDeletedIdentity(game.Identity)) return false;
        bool normal = delayedUploads.CanUpload(game, Setting("stream"), !uploadPolicy.AfterPlaying && session.Status.Recording
            && !session.Status.Paused && session.Status.Context == game.Context && !Streamer.HasEnded(game));
        bool idle = uploadPolicy.IsIdle();
        if (!LiveRequested) return normal;
        if (game.Identity == "channel:" + session.Status.Channel && game.Context == session.Status.Channel)
            return LiveServerAllowed && slots.Enabled && liveShare.Snapshot.Scope == "player"
                && (liveShare.CanSend(session.Status.Context) || !session.Status.Recording && liveShare.CanSendLobby);
        return StreamingSetupPolicy.RecordingAllowed(true, LiveServerAllowed && slots.Enabled, liveShare.CanSend(game.Context),
            session.Status.Context == game.Context, idle, normal);
    }

    JsonObject MemoryLoggingState() => J.Obj(("enabled", session.TrackLogs),
        ("canEnable", Setting("recording") && !otherRecorder),
        ("reason", !Setting("recording") ? "Choose automatic recording first." : otherRecorder ? "Close the other recorder first." : null),
        ("readsPerSecond", LooksPerSecond()), ("cityManagementIntervalMs", 1000), ("turnQuietMs", 5000));

    string? UploadWaitReason() => SetupPending ? "Finish setup before uploading."
        : session.TurnTransitionQuiet ? "Waiting for the new turn to settle."
        : !workload.CanStartHeavyWork ? workload.Reason
        : session.HasPendingAutosaves ? "Finishing pending save copies."
        : !LiveServerAllowed && uploadPolicy.AfterPlaying && !uploadPolicy.IsIdle() ? "Waiting for Civ's main menu (not a staging lobby) or Civ closed, for 5 seconds." : null;

    bool SaveCaptureActive(RecorderStatus s) => s.Folder is not null && s.Context is not null
        && !File.Exists(Path.Combine(s.Folder, "capture-ended")) && s.Recording;

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

    JsonObject UploadSchedule(string context)
    {
        var d = delayedUploads.Get(context);
        return J.Obj(("dueAt", d.DueAt is { } due ? J.Ms(due) : null), ("held", d.State is "recording" or "local"),
            ("ended", d.EndedAt is not null), ("warning", d.WarningAt is not null), ("state", d.State),
            ("enabled", Setting("stream")), ("endPrompted", d.EndPrompted), ("hidden", d.Hold == "hidden"));
    }

    void UpdateRecordingUx(RecorderStatus s)
    {
        if (preview || !BackgroundWorkAllowed || Environment.TickCount64 < nextUx) return;
        nextUx = Environment.TickCount64 + 2000;
        var entries = new GameIndex(fileSettings.OutputFolder).Load();
        foreach (var game in entries.Where(g => !g.Identity.StartsWith("channel:") && Directory.Exists(g.Folder)))
        {
            if (session.IsDeletedIdentity(game.Identity)) continue;
            bool active = s.Context == game.Context && s.Recording && !s.Paused && !Streamer.HasEnded(game);
            if (active)
            {
                var decision = delayedUploads.Get(game.Context);
                if (decision.State == "waiting" && !decision.Postponed) delayedUploads.Resume(game.Context);
                if (!session.TrackLogs && detectedCapture != game.Context && Setting("notify.lobby"))
                {
                    detectedCapture = game.Context;
                    var t = J.Toast("good", "Game detected · recording", "Autosaves are being kept on this PC. Keep playing.");
                    t["actions"] = RecordingActions();
                    Toast?.Invoke(t);
                }
                continue;
            }
            if (!Streamer.HasEnded(game) && (s.Context == game.Context || UploadPolicy.CivRunning())) continue;
            if (slots.Identity == game.Identity && liveShare.Snapshot.Scope == "game" && !(s.Recording && s.Identity == game.Identity)) StopLivestream();
            if (streamer?.ReadReplay(game.Context)["state"]?.GetValue<string>() is "ready" or "processing")
            {
                // the site finished it by itself (an hour without saves): the livestream's privacy ends with the game too
                if (streamer is { } finished && EndPrivateWhilePlaying(game.Context)) PublishFinishedReplay(finished, game.Context);
                continue;
            }
            var d = delayedUploads.Schedule(game.Context);
            if (d.State == "waiting" && d.Hold is null && Setting("stream") && streamer is { } hider) HideUntilPublic(hider, game.Context);
            if (d.State == "waiting" && streamer is { } queuedSender && queuedSender.ReadReplay(game.Context)["state"]?.GetValue<string>() is not ("ready" or "processing"))
            {
                var pending = queuedSender.ReadReplay(game.Context, true);
                bool waitForEnd = Streamer.HasEnded(game) || s.Context == game.Context || UploadPolicy.CivRunning();
                if (pending["action"] is null || pending["waitForEnd"]?.GetValue<bool>() == true && !waitForEnd)
                {
                    // public goes with the finish: it is sent only after the last save and log, never mid-game
                    var finish = new JsonObject { ["action"] = "complete", ["keepShort"] = true, ["waitForEnd"] = waitForEnd };
                    if (EndPrivateWhilePlaying(game.Context)) finish["private"] = false;
                    queuedSender.ChooseReplay(game.Context, finish);
                }
            }
            if (d.EndPrompted) continue;
            delayedUploads.MarkEndPrompted(game.Context);
            if (!Setting("notify.reminders")) continue;
            bool uploaded = StreamOf(game.Folder).SentSaves > 0;
            var toast = J.Toast("info", "Finished playing?", uploaded
                ? "Its replay goes public on ffaciv.com once the game is uploaded. Still playing? Keep it open."
                : Setting("stream") ? $"It uploads now, hidden, and goes public on {ServerShort} at {d.DueAt?.ToLocalTime():HH:mm}. Still playing? Keep it on this PC."
                    : "Uploads are off. Your game stays on this PC until you choose to upload it.");
            var id = Path.GetFileName(game.Folder);
            toast["actions"] = new JsonArray(
                J.Obj(("label", uploaded ? "Finish & make public" : Setting("stream") ? "Make public now" : "Upload now"), ("cmd", uploaded ? "confirmGameFinished" : "uploadGameNow"), ("args", J.Obj(("game", id))), ("primary", true)),
                J.Obj(("label", "Still playing"), ("cmd", "continueGame"), ("args", J.Obj(("game", id)))),
                J.Obj(("label", "Options"), ("cmd", "showOptions")));
            toast["timeout"] = 60000;
            Toast?.Invoke(toast);
            nextChecks = 0;
        }
        // Warnings are actionable even when ordinary notifications have been muted. Going public does not depend on the
        // upload switch: the game is on the site already, hidden only for the two hours.
        foreach (var d in delayedUploads.TakeWarnings())
        {
            var game = entries.FirstOrDefault(g => g.Context == d.Context);
            if (d.Hold != "hidden" || game is null || session.IsDeletedIdentity(game.Identity)) continue;
            var id = Path.GetFileName(game.Folder);
            var turn = Streamer.SavesOf(game.Folder).Select(RecorderSession.AutosaveTurn).Where(t => t is not null).DefaultIfEmpty().Max();
            var title = turn is { } n ? $"Civ V game · turn {n}" : "Your Civ V game";
            var toast = J.Toast("warn", "Public in 5 minutes", $"{title} goes public on {ServerShort}. Keep it hidden longer, or delete it.");
            toast["actions"] = new JsonArray(
                J.Obj(("label", "Hide 2 more hours"), ("cmd", "postponeUpload"), ("args", J.Obj(("game", id), ("hours", 2))), ("primary", true)),
                J.Obj(("label", "Delete…"), ("cmd", "reviewDelete"), ("args", J.Obj(("game", id)))));
            toast["timeout"] = 60000;
            Toast?.Invoke(toast); nextChecks = 0;
        }
        if (streamer is { } revealer)
            foreach (var d in delayedUploads.TakeReveals()) RevealHeldReplay(revealer, d.Context);
    }

    string? LivestreamUnavailable(RecorderStatus s, StreamStatus? st, string mode) =>
        !session.TrackLogs ? "Enable experimental memory logs first."
        : st?.TimedShares == false ? "This website needs the livestream-link update before sharing."
        : !Setting("recording") ? "Turn on recording first."
        : !BackgroundWorkAllowed ? session.TurnTransitionQuiet ? "Wait for the turn to settle." : workload.Reason
        : s.Paused || !(s.Recording && s.Identity is not null || s.Lobby is not null && s.Channel is not null) ? "Join a supported lobby or start a game first."
        : st?.Problem is "update" or "blocked" or "off" ? ProblemWords(st).Text : null;

    JsonObject LivestreamState(RecorderStatus s, StreamStatus? st, string mode)
    {
        var why = LivestreamUnavailable(s, st, mode);
        var share = liveShare.Snapshot;
        return J.Obj(("active", share.Requested), ("starting", livestreamStarting), ("canStart", why is null && !share.Requested), ("reason", why), ("problem", liveShareProblem),
            ("url", share.Requested ? share.Url : null), ("scope", share.Scope), ("expiresAt", share.ExpiresAt is { } expiry ? J.Ms(expiry) : null),
            ("status", share.Requested && !s.Recording ? "waiting" : slots.State.State), ("private", true),
            ("needsSetup", StreamingSetupPolicy.NeedsSetup(ui["settings"]!.AsObject())),
            ("waitingForGame", waitingForFirstGame),
            ("technical", J.Obj(("server", fileSettings.StreamAddress), ("transportState", st?.State), ("lastError", liveShareProblem ?? st?.LastError),
                ("slot", slots.State.State), ("controlAllowed", LiveControlAllowed), ("payloadAllowed", LiveServerAllowed && liveShare.CanSend(s.Context)),
                ("waitReason", BackgroundWorkAllowed ? null : session.TurnTransitionQuiet ? "Turn quiet period" : workload.Reason),
                ("queuedSaves", st?.QueuedSaves), ("queuedEventBytes", st?.QueuedEventBytes), ("fullSaves", st?.FullSaves), ("deltaSaves", st?.DeltaSaves),
                ("saveBytes", st?.SaveWireBytes), ("eventBytes", st?.EventWireBytes), ("requests", st?.Requests), ("chunks", st?.Chunks),
                ("deltaCpuMs", st?.DeltaCpuMs), ("chunkCpuMs", st?.ChunkCpuMs), ("lastSentAt", st?.LastSent is { } sent ? J.Ms(sent) : null),
                ("saveBatchLimit", PrivateSaveLimit), ("eventChunkLimit", PrivateEventLimit), ("idleUploadMode", FastUploads),
                ("uploadKibPerSecond", UploadRate), ("eventChunkBytes", Streamer.ChunkBytes), ("saveUpload", UploadFormat()))),
            ("currentSaveReason", StreamingSetupPolicy.EnableReason(ui["settings"]!.AsObject(), s.CivDetected || s.Game is not null, s.Lobby is not null, s.Context is not null && s.Identity is not null && (s.Recording || s.Game is not null), s.Paused, StreamingSetupPolicy.QueuedSaves(s.Folder), latestOnly: true)),
            ("followSetupReason", StreamingSetupPolicy.FutureInviteReason(ui["settings"]!.AsObject(), s.CivDetected || s.Game is not null, s.Lobby is not null)),
            ("setupReason", StreamingSetupPolicy.EnableReason(ui["settings"]!.AsObject(), s.CivDetected || s.Game is not null, s.Lobby is not null, s.Context is not null && s.Identity is not null && (s.Recording || s.Game is not null), s.Paused, StreamingSetupPolicy.QueuedSaves(s.Folder))));
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
            if (boundary is not null) { result = J.Obj(("toast", J.Toast("info", "Prepare before your game", boundary))); return true; }
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
        if (cmd == "setUploadFormat")
        {
            var format = args.Str("format");
            var boundary = StreamingSetupPolicy.ChangeReason(ui["settings"]!.AsObject(), "saveUpload", JsonValue.Create(format), UploadPolicy.CivRunning(), session.Status.Lobby is not null);
            if (boundary is not null) { result = J.Obj(("toast", J.Toast("info", "Prepare before your game", boundary))); return true; }
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
            { result = J.Obj(("toast", J.Toast("info", "Prepare before your game", boundary))); return true; }
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
            if (reason is not null) { result = J.Obj(("toast", J.Toast("info", "Prepare before your game", reason))); return true; }
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
                result = J.Obj(("toast", J.Toast("info", "Upload queued", uploadPolicy.CanSend()
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

    /// <summary>Recordings whose Saves list the player opened on the Previous games screen (the newest ones always carry theirs).</summary>
    readonly HashSet<string> savesShown = new(StringComparer.Ordinal);

    static StreamState StreamStateOf(string folder)
    {
        var f = Path.Combine(folder, "stream.json");
        return File.Exists(f) ? System.Text.Json.JsonSerializer.Deserialize<StreamState>(SafeFiles.ReadAllText(f)) ?? new() : new();
    }

    /// <summary>
    /// A recording's save copies for its Saves list: the turn, when it was saved, the site's id once the site took it, or
    /// the site's refusal; whether it is older than the livestream's start (kept for after the game); saves the site took
    /// whose copy left this PC since are listed too. The site's own view of each comes with the recording's state.
    /// </summary>
    JsonArray SaveList(string? folder, string? context)
    {
        var list = new JsonArray();
        if (folder is null) return list;
        try
        {
            var sent = StreamStateOf(folder).SentSaves;
            var here = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Streamer.SavesOf(folder))
            {
                var name = Path.GetFileName(file);
                here.Add(name);
                var refused = sent.ContainsKey(name) ? null : streamer?.RefusalOf(file);
                list.Add(J.Obj(("file", Streamer.OriginalName(name)), ("turn", RecorderSession.AutosaveTurn(name)), ("at", J.Ms(new DateTimeOffset(File.GetLastWriteTimeUtc(file)))),
                    ("id", sent.GetValueOrDefault(name)), ("beforeStream", context is not null && LiveRequested && liveSaveWindow?.Includes(context, file) == false),
                    ("refused", refused is null ? null : J.Obj(("at", J.Ms(refused.At)), ("status", refused.Status), ("why", refused.Why)))));
            }
            foreach (var (name, id) in sent)
                if (!here.Contains(name))
                    list.Add(J.Obj(("file", Streamer.OriginalName(name)), ("turn", RecorderSession.AutosaveTurn(name)), ("id", id), ("removed", true)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return list;
    }

    /// <summary>The current recording's Saves list, the reason the rest waits, and its turns ("T30–144").</summary>
    void AddSaves(JsonObject current, string? folder, string? context, string mode, StreamStatus? st)
    {
        var entry = context is null ? null : new GameIndex(fileSettings.OutputFolder).Load().LastOrDefault(e => e.Context == context);
        var (first, last, count) = TurnsOf(folder);
        current["firstTurn"] = first; current["lastTurn"] = last; current["savedTurns"] = count;
        current["saves"] = SaveList(folder, context);
        current["savesWait"] = SaveWaitReason(entry, mode, st);
    }

    /// <summary>The turns a recording holds saves of (here or on the site): first, last and how many, for "T30–144".</summary>
    static (int? First, int? Last, int Count) TurnsOf(string? folder)
    {
        if (folder is null) return (null, null, 0);
        try
        {
            var turns = Streamer.SavesOf(folder).Select(Path.GetFileName).Concat(StreamStateOf(folder).SentSaves.Keys)
                .Select(n => RecorderSession.AutosaveTurn(n!)).OfType<int>().Distinct().ToList();
            return turns.Count == 0 ? (null, null, 0) : (turns.Min(), turns.Max(), turns.Count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return (null, null, 0); }
    }

    /// <summary>Why a recording's saves that are not on the site yet wait right now, in plain words (null: nothing holds them back).</summary>
    string? SaveWaitReason(GameEntry? entry, string mode, StreamStatus? st)
    {
        if (SetupPending) return "Finish the recorder's setup first.";
        if (entry is not null && session.IsDeletedIdentity(entry.Identity)) return "You deleted this recording, so nothing more of it is sent.";
        if (mode == "off") return "Automatic uploads are off, so saves stay on this PC. Turn on Automatic uploads (with Civ closed), or press Upload now after the game.";
        if (st?.Problem is "cap" or "update" or "blocked" or "off" or "key") return ProblemWords(st).Text;
        if (st?.State.StartsWith("server not reachable") == true) return $"Can't reach {ServerShort}. Saves are sent when the connection is back; nothing is lost.";
        if (session.TurnTransitionQuiet) return "Waiting a few seconds for the new turn to settle, so the game is not slowed.";
        if (!workload.CanStartHeavyWork) return $"Paused to keep your PC smooth: {workload.Reason}";
        if (session.HasPendingAutosaves) return "Finishing the copy of the newest save.";
        if (!uploadPolicy.CanSend() && !LiveServerAllowed)
            return LiveRequested ? "The livestream waits for its private access and a free livestream slot; saves are sent once both are confirmed."
                : !uploadPolicy.Automatic ? "Automatic uploads are held: saves stay on this PC until you upload them."
                : "Saves are sent when you leave the game: Civ's main menu or Civ closed, not a staging lobby. The replay then stays hidden for 2 hours. To send while you play, close Civ and choose While playing.";
        if (entry is not null && !CanUploadRecording(entry))
        {
            var d = delayedUploads.Get(entry.Context);
            return d.State == "local" ? "You chose to keep this recording on this PC. Press Upload now to send it."
                : d.State == "waiting" ? "Marking the replay hidden on the site first; the saves follow right away."
                : LiveRequested ? "Only saves from when the livestream started are sent during play; older ones go after the game."
                : "Waiting for upload permission for this recording.";
        }
        return null;
    }

    /// <summary>What a held deletion keeps of the recording's card: enough to show it on Home and under Recently deleted.</summary>
    JsonObject HeldCard(JsonObject? game, string? context, string? identity)
    {
        game ??= context is null ? null : games.OfType<JsonObject>().FirstOrDefault(g => g.Str("context") == context);
        var card = new JsonObject();
        foreach (var key in new[] { "id", "mod", "modVersion", "date", "turns", "firstTurn", "lastTurn", "savedTurns", "players", "siteGame", "siteMod", "url" })
            if (game?[key] is { } v) card[key] = v.DeepClone();
        card["context"] = context;
        card["identity"] = identity;
        if (game is null) { card["id"] = context ?? "live"; card["mod"] = "Civ V"; card["date"] = J.Ms(DateTimeOffset.Now); card["turns"] = session.Status.Turn; card["players"] = new JsonArray(); }
        return card;
    }

    /// <summary>The deletions still held (Undo possible), newest first, each with the card it had.</summary>
    JsonArray HeldDeletions()
    {
        var list = new JsonArray();
        if (streamer is not { } sender) return list;
        foreach (var t in sender.QueuedReplayDeletions().Where(t => t.Due is not null && t.Due > DateTimeOffset.UtcNow).OrderByDescending(t => t.DeletedAt))
            list.Add(J.Obj(("key", t.Key), ("deletedAt", t.DeletedAt is { } at ? J.Ms(at) : null), ("due", J.Ms(t.Due!.Value)), ("card", t.Card?.DeepClone())));
        return list;
    }

    /// <summary>Undo: the held deletion leaves the queue, the recording shows and records again, its upload choice and privacy come back.</summary>
    JsonObject? UndoDelete(string? key)
    {
        if (streamer is not { } sender || key is null) return J.Obj(("toast", J.Toast("warn", "Recorder is starting", "Try again in a moment.")));
        _ = Task.Run(async () =>
        {
            try
            {
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var command = await sender.CancelReplayDeletionAsync(key, ct.Token);
                if (command is null) { Toast?.Invoke(J.Toast("warn", "Too late to undo", "The hold had ended: the recording was deleted.")); return; }
                var card = command["card"] as JsonObject;
                string? context = command["context"]?.GetValue<string>(), mod = command["mod"]?.GetValue<string>(), game = command["game"]?.GetValue<string>();
                if (card?.Str("identity") is { } identity) session.RestoreGameIdentity(identity);
                if (context is not null && card?["uploadDecision"] is JsonObject d && d.Str("state") is { } state)
                    delayedUploads.Restore(new UploadDecision
                    {
                        Context = context, State = state, Postponed = d["postponed"]?.GetValue<bool>() == true, EndPrompted = d["endPrompted"]?.GetValue<bool>() == true,
                        EndedAt = d["endedAt"] is { } e ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetValue<long>()) : null,
                        DueAt = d["dueAt"] is { } du ? DateTimeOffset.FromUnixTimeMilliseconds(du.GetValue<long>()) : null,
                        Hold = d.Str("hold"),
                    });
                deletedCards.Forget(card, context, card?.Str("identity"), mod, game);
                factsCache.Clear();
                nextLibrary = nextChecks = 0;
                // the site hid it at Delete: it shows again as it was
                if (command["hidden"]?.GetValue<bool>() == true && command["wasPrivate"]?.GetValue<bool>() != true)
                {
                    var choice = new JsonObject { ["private"] = false };
                    if (context is not null) await sender.ApplyReplayChoiceAsync(context, choice, ct.Token);
                    else if (mod is not null && game is not null) await sender.SetReplayAccessAsync(mod, game, choice, ct.Token);
                }
                Toast?.Invoke(J.Toast("good", "Recording brought back", "It is in your list again, with the visibility it had before."));
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
            {
                Toast?.Invoke(J.Toast("info", "Brought back · visibility pending", "The recording is back in your list. The website did not answer yet: check its visibility in Replay settings."));
            }
        });
        return J.Obj(("toast", J.Toast("info", "Bringing it back…", "Undoing the deletion.")));
    }
}
