using System.Net;
using System.Text;
using System.Text.Json;

namespace OutsideRecorderCore;

/// <summary>
/// The "busy line" (card OR05 "Step 2 contract", lib/live-slots.js): a game is streamed LIVE to viewers only while this
/// install holds one of the site's few live slots. Recording and uploading for the replay never wait for a slot.
/// States: "granted" (the livestream link works; <see cref="Watch"/>), "busy" (all slots taken: <see cref="Position"/> in
/// line; <see cref="Requested"/> after "Request a livestream"), "off" (livestreams switched off on the site),
/// "unavailable" (the route is not there), "released", "unknown" (not asked yet / no answer).
/// </summary>
public sealed record LiveSlotState(string State, int? Position = null, bool Requested = false, int? Slots = null, int? Free = null,
    string? Watch = null, int AskAgainSeconds = 30)
{
    /// <summary>Not asked yet.</summary>
    public static readonly LiveSlotState Unknown = new("unknown");
}

public sealed partial class Streamer
{
    /// <summary>
    /// One call of the site's live-slot route: POST live/slot {game, channel, action: take | request | release}.
    /// take = get or renew the slot (every renewEverySeconds while recording), request = "Request a livestream" (the press
    /// is kept), release = the game ended. 404 = no slots on this server: "unavailable" on a site, "granted" on the local
    /// test server (it has no line).
    /// </summary>
    public async Task<LiveSlotState> LiveSlotAsync(string game, string? channel, string action, CancellationToken ct)
    {
        await EnsureRegisteredAsync(ct);
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "live/slot")
        { Content = new StringContent(JsonSerializer.Serialize(new { game, channel, action }), Encoding.UTF8, "application/json") }, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return new LiveSlotState(settings.Stream == "local" ? "granted" : "unavailable");
        if (!r.IsSuccessStatusCode) return LiveSlotState.Unknown;
        return Parse(await r.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The route's answer: {status, slots{total,used,free}, queue{position,waiting}, requested, renewEverySeconds, retryAfterSeconds, watch}.</summary>
    public static LiveSlotState Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        static int? Int(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        JsonElement Obj(string k) => e.TryGetProperty(k, out var v) ? v : default;
        string status = e.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
        bool requested = e.TryGetProperty("requested", out var q) && q.ValueKind == JsonValueKind.True;
        string? watch = e.TryGetProperty("watch", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
        int renew = Int(e, "renewEverySeconds") ?? 60, retry = Int(e, "retryAfterSeconds") ?? 30;
        var (total, free, position) = (Int(Obj("slots"), "total"), Int(Obj("slots"), "free"), Int(Obj("queue"), "position"));
        return status switch
        {
            "granted" => new LiveSlotState("granted", Slots: total, Free: free, Watch: watch, AskAgainSeconds: renew),
            "busy" or "requested" => new LiveSlotState("busy", position, requested || status == "requested", total, free, AskAgainSeconds: retry),
            "off" => new LiveSlotState("off", Slots: 0, AskAgainSeconds: Math.Max(retry, 300)),
            "released" or "idle" => new LiveSlotState(status, Slots: total, Free: free, AskAgainSeconds: retry),
            _ => LiveSlotState.Unknown,
        };
    }

    /// <summary>Gives the slot and the place in line back (the game ended); errors ignored.</summary>
    public async Task ReleaseLiveSlotAsync(string game, string? channel, CancellationToken ct)
    {
        try { await LiveSlotAsync(game, channel, "release", ct); } catch { }
    }
}
