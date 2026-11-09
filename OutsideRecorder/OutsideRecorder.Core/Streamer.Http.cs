using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OutsideRecorderCore;

/// <summary>The site talk itself: registering this install, each request with its gates and timeouts, and what an answer means (the 429/426/403/401 rules, a refusal of one recording).</summary>
public sealed partial class Streamer
{
    /// <summary>A site answer in the 400s other than the ones about the whole install or timing (401, 408, 426, 429): it
    /// refuses this one request for good, so retrying it at once only blocks the rest.</summary>
    static Exception RefusalOrError(HttpStatusCode status, string message) =>
        (int)status is >= 400 and < 500 && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests) && (int)status != 426
            ? new SiteRefusedException(message, status) : new HttpRequestException(message, null, status);

    async Task EnsureRegisteredAsync(CancellationToken ct)
    {
        if (install is not null && (!settings.RequireReplayLifecycle || replayLifecycleChecked)) return;
        await registering.WaitAsync(ct);
        try
        {
            if (settings.RequireReplayLifecycle && !replayLifecycleChecked)
            {
                using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "config"), ct, auth: false);
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                if (r.IsSuccessStatusCode) Status = Status with { TimedShares = SupportsTimedShares(doc.RootElement) };
                if (!r.IsSuccessStatusCode || !doc.RootElement.TryGetProperty("recorder", out var recorder)
                    || !recorder.TryGetProperty("lifecycle", out var lifecycle) || lifecycle.ValueKind != JsonValueKind.True
                    || (settings.RequiredControlsVersion > 1 && (!recorder.TryGetProperty("controlsVersion", out var controls)
                        || !controls.TryGetInt32(out var controlVersion) || controlVersion < settings.RequiredControlsVersion)))
                    throw new HttpRequestException("The website needs the recorder update. Saves wait on this PC until it is ready.");
                replayLifecycleChecked = true;
            }
            install ??= await RegisterAsync(ct);
        }
        finally { registering.Release(); }
    }

    bool replayLifecycleChecked;

    /// <summary>A successfully fetched config must explicitly advertise the timed-share capability.</summary>
    public static bool SupportsTimedShares(JsonElement config) => config.ValueKind == JsonValueKind.Object
        && config.TryGetProperty("recorder", out var recorder) && recorder.ValueKind == JsonValueKind.Object
        && recorder.TryGetProperty("timedShares", out var enabled) && enabled.ValueKind == JsonValueKind.True;

    string InstallFile => Path.Combine(settings.OutputFolder, $"stream-install-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(http.BaseAddress!.ToString())))[..12]}.json");

    /// <summary>
    /// The install's id and key for this server: registered once (POST register, the uploader's install role) and kept
    /// next to the recordings with the key protected for this Windows user (<see cref="KeyStore"/>; an older plain file
    /// is protected on first use).
    /// </summary>
    async Task<(string, string)> RegisterAsync(CancellationToken ct)
    {
        var file = InstallFile;
        if (File.Exists(file))
        {
            var known = JsonSerializer.Deserialize<Dictionary<string, string>>(SafeFiles.ReadAllText(file))!;
            if (known.TryGetValue("keyProtected", out var p) && KeyStore.Unprotect(p) is { } key) return (known["id"], key);
            if (known.TryGetValue("key", out var plain))
            {
                SafeFiles.Replace(file, JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = known["id"], ["keyProtected"] = KeyStore.Protect(plain) }));
                return (known["id"], plain);
            }
            // not readable (another Windows user, another PC): register anew
        }
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "register")
        { Content = new StringContent(JsonSerializer.Serialize(new { version }), Encoding.UTF8, "application/json") }, ct, auth: false);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"register: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync(ct)}");
        var answer = JsonSerializer.Deserialize<Dictionary<string, string>>(await r.Content.ReadAsStringAsync(ct))!;
        Directory.CreateDirectory(settings.OutputFolder);
        SafeFiles.Replace(file, JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = answer["id"], ["keyProtected"] = KeyStore.Protect(answer["key"]) }));
        return (answer["id"], answer["key"]);
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, bool auth = true)
    {
        try { return await SendCoreAsync(make, ct, auth); }
        catch (Exception e) when (ResourceDeferred(e))
        { Diagnostics?.UploadDeferred(); throw new WorkDeferredException("Upload deferred while the game or computer is busy."); }
    }

    static bool ResourceDeferred(Exception e)
    {
        for (Exception? current = e; current is not null; current = current.InnerException)
            if (current is WorkDeferredException) return true;
        return false;
    }

    /// <summary>Set while a visibility change (Make public / private, restrict) is sent: a few bytes the player asked for, so it
    /// goes out at once, also while uploads wait for Civ's main menu or a calmer PC (the user, 2026-11-04: "when i make public
    /// its actually will be made public"). Scoped to that one call: other requests keep their gates.</summary>
    static readonly AsyncLocal<bool> accessChange = new();

    async Task<HttpResponseMessage> SendCoreAsync(Func<HttpRequestMessage> make, CancellationToken ct, bool auth)
    {
        bool small = accessChange.Value;
        if (!small && Workload?.CanStartHeavyWork == false) throw new WorkDeferredException("Server work paused while the computer is busy.");
        if (!small && !CanContactServer()) throw new HttpRequestException("Server work waits for game/menu, resource and upload permission.");
        using var request = make();
        if (!small && request.Content is { } content) request.Content = uploadBudget.Wrap(content);
        if (auth) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", install!.Value.Key);
        request.Headers.TryAddWithoutValidation("X-Program-Version", version);
        Status = Status with { Requests = Status.Requests + 1 };
        Diagnostics?.RequestStarted();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Deliberate bandwidth pacing is not a network stall.
        double pacedSeconds = settings.UploadKilobytesPerSecond > 0 ? (request.Content?.Headers.ContentLength ?? 0) / (settings.UploadKilobytesPerSecond * 1024d) : 0;
        limit.CancelAfter(RequestTimeout + TimeSpan.FromSeconds(pacedSeconds));
        HttpResponseMessage r;
        try
        {
            r = await http.SendAsync(request, limit.Token);
            await r.Content.LoadIntoBufferAsync(limit.Token);   // the answer's body too, within the same time
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Diagnostics?.UploadFailed();
            throw new TimeoutException($"no answer from the server in {RequestTimeout.TotalSeconds:F0} s ({request.Method} {request.RequestUri})");
        }
        catch (Exception e) when (!ResourceDeferred(e)) { Diagnostics?.UploadFailed(); throw; }
        if (!r.IsSuccessStatusCode) Diagnostics?.UploadFailed();
        if (r.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var after = r.Headers.RetryAfter?.Delta ?? (r.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(10));
            retryAfter = DateTime.UtcNow + after;
            Status = Status with { Problem = "cap", Until = DateTimeOffset.UtcNow + after };
            throw new HttpRequestException($"server says wait {after.TotalMinutes:F0} min (daily allowance)");
        }
        if ((int)r.StatusCode == 426)
        {
            // this version is too old for the site: stop sending until the program is updated (everything waits on this PC)
            string? min = null, url = null;
            try { using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)); min = doc.RootElement.TryGetProperty("minVersion", out var m) ? m.GetString() : null; url = doc.RootElement.TryGetProperty("downloadUrl", out var u) ? u.GetString() : null; } catch { }
            retryAfter = DateTime.UtcNow + TimeSpan.FromHours(1);
            Status = Status with { Problem = "update", MinVersion = min, DownloadUrl = url };
            throw new HttpRequestException($"this version is too old for the site (needs {min})");
        }
        if (r.StatusCode == HttpStatusCode.Forbidden && auth && await InstallRefusal(r, ct) is var (blocked, enabled, whole) && whole)
        {
            retryAfter = DateTime.UtcNow + TimeSpan.FromMinutes(30);
            Status = Status with { Problem = blocked ? "blocked" : !enabled ? "off" : "blocked" };
            throw new HttpRequestException(blocked ? "this install is blocked on the site" : "sending is switched off on the site");
        }
        if (r.StatusCode == HttpStatusCode.Unauthorized && auth)
        {
            // the site does not know this key (a new test environment, a reset): register again on the next pass
            try { File.Move(InstallFile, InstallFile + ".old", overwrite: true); } catch { }
            install = null;
            Status = Status with { Problem = "key" };
            throw new HttpRequestException("the site did not know this install's key; registering again");
        }
        if (r.IsSuccessStatusCode && Status.Problem is not null and not "unreachable") Status = Status with { Problem = null, Until = null };
        if (activeSender.Value is { } working) working.Heartbeat = Environment.TickCount64;   // an answer came: not stalled
        return r;
    }

    /// <summary>
    /// A 403 that refuses this whole install (the uploader's answer carries "blocked" / "enabled": switched off or blocked)
    /// — not a 403 of one route about one game (e.g. Block of a game this install sent nothing of), which the caller handles.
    /// </summary>
    static async Task<(bool Blocked, bool Enabled, bool Whole)> InstallRefusal(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var e = doc.RootElement;
            bool hasB = e.TryGetProperty("blocked", out var b), hasE = e.TryGetProperty("enabled", out var en);
            return (hasB && b.ValueKind == JsonValueKind.True, !hasE || en.ValueKind != JsonValueKind.False, hasB || hasE);
        }
        catch { return (false, true, false); }
    }

    /// <summary>The site's own words from an error answer ({"error": "…"}), else the start of the answer.</summary>
    static string SiteWords(string text)
    {
        try { if (System.Text.Json.Nodes.JsonNode.Parse(text)?["error"]?.GetValue<string>() is { Length: > 0 } words) return words; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        text = text.Trim();
        return text.Length == 0 ? "no reason given" : text.Length > 200 ? text[..200] + "…" : text;
    }
}
