using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>The private livestream: the invite link, its consent and revocations, the lobby invite, and what the window shows about it.</summary>
sealed partial class LiveBackend
{
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
                catch (HttpRequestException e) when (e.StatusCode is { } code && (int)code is >= 400 and < 500
                    && code is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests))
                {
                    // the site refuses this link for good (it is gone already): asking again every 5 s cannot change that
                    lock (revokedInvites) { revokedInvites.Remove(token); SaveInviteRevocations(); }
                }
                catch { return; }
            }
        }
        finally { lock (revokedInvites) revokingInvites = false; }
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
}
