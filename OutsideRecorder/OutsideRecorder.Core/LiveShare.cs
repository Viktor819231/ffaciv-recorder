using System.Text;
using System.Text.Json.Nodes;

namespace OutsideRecorderCore;

public sealed record LiveShareInvite(string Token, string Url, DateTimeOffset ExpiresAt);
public sealed record LiveShareSnapshot(bool Requested, string Scope, DateTimeOffset? ExpiresAt, string? Token, string? Url, string? AuthorizedContext);

/// <summary>In-memory explicit consent. Every new recording needs its own fresh private acknowledgement and share binding.</summary>
public sealed class LiveShareSession
{
    readonly object gate = new();
    readonly Func<DateTimeOffset> clock;
    int revision;
    bool requested;
    string scope = "game";
    DateTimeOffset? expires;
    string? initialContext, initialGame, preparedContext, authorizedContext, token, url;
    public LiveShareSession(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    bool Valid => requested && expires > clock();
    public LiveShareSnapshot Snapshot { get { lock (gate) return new(Valid, scope, expires, token, url, authorizedContext); } }
    public void Start(string mode, int hours, string context, string? game = null)
    {
        if (mode is not ("game" or "player") || hours is not (1 or 4 or 8 or 24)) throw new ArgumentException("Choose a game or player link and 1, 4, 8 or 24 hours.");
        lock (gate) { revision++; requested = true; scope = mode; expires = clock().AddHours(hours); initialContext = context; initialGame = game; preparedContext = authorizedContext = token = url = null; }
    }
    public bool CanSend(string? context) { lock (gate) return Valid && context is not null && authorizedContext == context; }
    public bool CanSendLobby { get { lock (gate) return Valid && scope == "player" && token is not null; } }
    public string? Stop()
    {
        lock (gate) { revision++; requested = false; preparedContext = authorizedContext = null; var old = token; token = url = null; return old; }
    }
    public bool Accepts(string context, string? game = null) { lock (gate) return Valid && (scope == "player" || initialGame is not null && initialGame == game || initialGame is null && initialContext == context); }
    public async Task<bool> PrepareAsync(string context, Func<Task<bool>> privateAcknowledgement, string? game = null)
    {
        int version;
        lock (gate) { if (!Accepts(context, game)) return false; if (preparedContext == context) return true; version = revision; authorizedContext = null; }
        if (!await privateAcknowledgement()) return false;
        lock (gate) { if (version != revision || !Accepts(context, game)) return false; preparedContext = context; return true; }
    }
    public async Task<bool> BindAsync(string context, Func<string?, Task<LiveShareInvite>> bind, Func<string, Task> revoke, string? game = null)
    {
        int version; string? previous;
        lock (gate) { if (!Accepts(context, game) || preparedContext != context) return false; if (authorizedContext == context) return true; version = revision; previous = token; }
        var invite = await bind(previous);
        bool accepted;
        lock (gate)
        {
            accepted = version == revision && Accepts(context, game) && preparedContext == context && invite.ExpiresAt > clock();
            if (accepted) { token = invite.Token; url = invite.Url; expires = invite.ExpiresAt; authorizedContext = context; }
        }
        if (!accepted) await revoke(invite.Token);
        return accepted;
    }
}

public sealed partial class Streamer
{
    public async Task<LiveShareInvite> BindLiveShareAsync(string? token, string scope, int hours, string? game, string? channel, string context, CancellationToken ct, bool lobby = false)
    {
        await EnsureRegisteredAsync(ct);
        if (Status.TimedShares == false) throw new HttpRequestException("This website needs the livestream-link update before sharing.", null, System.Net.HttpStatusCode.NotFound);
        var body = new JsonObject { ["action"] = token is null ? "create" : "update", ["token"] = token, ["scope"] = scope,
            ["durationHours"] = hours, ["game"] = game, ["channel"] = channel, ["context"] = context, ["lobby"] = lobby };
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "live/share")
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") }, ct);
        r.EnsureSuccessStatusCode();
        var answer = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))!.AsObject();
        return new(answer["token"]!.GetValue<string>(), answer["url"]!.GetValue<string>(), DateTimeOffset.Parse(answer["expiresAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
    }
    public async Task RevokeLiveShareAsync(string token, CancellationToken ct)
    {
        await EnsureRegisteredAsync(ct);
        var body = new JsonObject { ["action"] = "revoke", ["token"] = token };
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "live/share")
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") }, ct);
        r.EnsureSuccessStatusCode();
    }
}
