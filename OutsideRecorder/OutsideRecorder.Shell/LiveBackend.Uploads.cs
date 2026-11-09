using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>When a finished game uploads and goes public (the user, 2026-11-02: "wait 2 hours to become visible"), the player's visibility choices, and the gate every upload asks.</summary>
sealed partial class LiveBackend
{
    readonly DelayedUploads delayedUploads;

    long nextUx;

    string? detectedCapture;

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

    /// <summary>Sends the player's own choice now (Make public / private, restrict, keep a short game): a site copy otherwise
    /// stayed private (or a short game was deleted after its quiet hour) while the choice waited for Civ's main menu with
    /// the finish (the user, 2026-11-04: "when i make public its actually will be made public"). The finish itself still waits.</summary>
    void SendAccessChoice(Streamer sender, string context, JsonObject choice) => _ = Task.Run(async () =>
    {
        var access = new JsonObject();
        foreach (var key in new[] { "private", "restricted", "keepShort" }) if (choice[key] is { } v) access[key] = v.DeepClone();
        if (access.Count == 0) return;
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (!await sender.ApplyReplayChoiceAsync(context, access, ct.Token))
                Toast?.Invoke(J.Toast("warn", "The website did not confirm it", $"{sender.ReplayChoiceError} Your choice is kept on this PC and sent again later."));
            nextLibrary = nextChecks = 0;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
        { Toast?.Invoke(J.Toast("warn", $"Can't reach {ServerShort}", "Your choice is kept on this PC and sent again later.")); }
    });

    void PublishFinishedReplay(Streamer sender, string context) => _ = Task.Run(async () =>
    {
        try { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await sender.ApplyReplayChoiceAsync(context, new JsonObject { ["private"] = false }, ct.Token); nextLibrary = nextChecks = 0; }
        catch { /* the choice is queued: the next state check sends it */ }
    });

    // The user, 2026-11-02: "wait 2 hours to become visible" instead of waiting 2 hours to upload. A finished game uploads
    // once Civ is closed (2026-11-09: "upload when civ is not running"), hidden on the site, and goes public two hours later.
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

    bool SaveCaptureActive(RecorderStatus s) => s.Folder is not null && s.Context is not null
        && !File.Exists(Path.Combine(s.Folder, "capture-ended")) && s.Recording;

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
            // reserved only when its saves can follow: sent hours ahead (Civ still open after Mark as done), the empty
            // reservation was deleted by the site as a short game after one quiet hour and the saves then got "expired"
            if (d.State == "waiting" && d.Hold is null && Setting("stream") && NormalServerAllowed && streamer is { } hider) HideUntilPublic(hider, game.Context);
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
                : Setting("stream") ? uploadPolicy.AfterPlaying && UploadPolicy.CivRunning()
                    ? $"It uploads once you close Civ, hidden, and goes public on {ServerShort} 2 hours after that. Still playing? Keep it on this PC."
                    : $"It uploads now, hidden, and goes public on {ServerShort} at {d.DueAt?.ToLocalTime():HH:mm}. Still playing? Keep it on this PC."
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
}
