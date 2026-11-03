using System.Text;
using System.Text.Json.Nodes;

namespace OutsideRecorderCore;

/// <summary>Durable replay choices and the server's acknowledged generation progress.</summary>
public sealed partial class Streamer
{
    readonly object replayLock = new();
    readonly Dictionary<string, long> replayPoll = [];
    /// <summary>The latest explicit access-change rejection, without credentials or raw response bodies.</summary>
    public string? ReplayChoiceError { get; private set; }
    string ReplayFile(string context, bool pending = false) => Path.Combine(settings.OutputFolder, "replays", context + (pending ? ".pending.json" : ".json"));

    /// <summary>Queues a choice even while offline; completion is sent after all saves and logs are acknowledged.</summary>
    public void ChooseReplay(string context, JsonObject choice)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(context, "^[a-f0-9]{32}$")) throw new ArgumentException("Invalid recording.");
        lock (replayLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReplayFile(context))!);
            var pending = ReadReplay(context, true);
            foreach (var (key, value) in choice) pending[key] = value?.DeepClone();
            SafeFiles.Replace(ReplayFile(context, true), pending.ToJsonString());
            replayPoll.Remove(context);
        }
    }

    /// <summary>An explicit access change can reach an existing server copy even while automatic uploads are held.</summary>
    public async Task<bool> ApplyReplayChoiceAsync(string context, JsonObject choice, CancellationToken ct)
    {
        // The exact initialization command is transport-only; retaining it would poison a later merged completion.
        var durable = (JsonObject)choice.DeepClone();
        durable.Remove("initialize");
        ChooseReplay(context, durable);
        await EnsureRegisteredAsync(ct);
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"recordings/{context}/state")
        { Content = new StringContent(choice.ToJsonString(), Encoding.UTF8, "application/json") }, ct);
        if (!r.IsSuccessStatusCode) {
            ReplayChoiceError = r.StatusCode == System.Net.HttpStatusCode.Gone
                ? "HTTP 410: the website expired this recording or lobby channel. A server repair is needed before this channel can prepare an invite."
                : $"HTTP {(int)r.StatusCode}: the website did not confirm the access change.";
            return false; // A cached private choice must never authorize a new livestream.
        }
        ReplayChoiceError = null;
        var body = await r.Content.ReadAsStringAsync(ct);
        lock (replayLock)
        {
            var pending = ReadReplay(context, true);
            foreach (var (key, value) in durable)
                if (JsonNode.DeepEquals(pending[key], value)) pending.Remove(key);
            SafeFiles.Replace(ReplayFile(context), body);
            if (pending.Count == 0) File.Delete(ReplayFile(context, true));
            else SafeFiles.Replace(ReplayFile(context, true), pending.ToJsonString());
        }
        return true;
    }

    /// <summary>The last server answer, never an invented completion or URL.</summary>
    public JsonObject ReadReplay(string context, bool pending = false)
    {
        lock (replayLock)
        {
            try { return JsonNode.Parse(SafeFiles.ReadAllText(ReplayFile(context, pending))) as JsonObject ?? new(); }
            catch { return new(); }
        }
    }

    async Task RefreshReplayAsync(GameEntry game, StreamState state, CancellationToken ct)
    {
        if (!MayUpload || !CanContactServer() || !CanUploadGame(game)) return;
        if (game.Identity.StartsWith("channel:")) return;
        lock (replayLock)
        {
            if (replayPoll.GetValueOrDefault(game.Context) > Environment.TickCount64) return;
            replayPoll[game.Context] = Environment.TickCount64 + 15000;
        }
        try
        {
            var pending = ReadReplay(game.Context, true);
            var eventsFile = Path.Combine(game.Folder, "events.jsonl");
            bool drained = (!File.Exists(eventsFile) || state.SentOffset >= new FileInfo(eventsFile).Length)
                && SavesOf(game.Folder).All(f => state.SentSaves.ContainsKey(Path.GetFileName(f)));
            bool send = pending.Count > 0 && (pending["action"] is null || (drained && (pending["waitForEnd"]?.GetValue<bool>() != true || Ended(eventsFile))));
            if (!MayUpload || !CanContactServer() || !CanUploadGame(game)) return;
            var path = $"recordings/{game.Context}/state";
            using var r = await SendAsync(() => new HttpRequestMessage(send ? HttpMethod.Post : HttpMethod.Get, path)
            { Content = send ? new StringContent(pending.ToJsonString(), Encoding.UTF8, "application/json") : null }, ct);
            if (!r.IsSuccessStatusCode) return;
            var body = await r.Content.ReadAsStringAsync(ct);
            if (!settings.KeepCopies && JsonNode.Parse(body)?["state"]?.GetValue<string>() == "deleted" && drained
                && File.Exists(eventsFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(eventsFile) >= TimeSpan.FromHours(1))
            {
                // Delete only this recorder's own expired copy, never Civ's original save folder.
                try { LocalRecordingDeletion.Delete(settings.OutputFolder, game.Folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            lock (replayLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReplayFile(game.Context))!);
                SafeFiles.Replace(ReplayFile(game.Context), body);
                if (send && ReadReplay(game.Context, true).ToJsonString() == pending.ToJsonString()) File.Delete(ReplayFile(game.Context, true));
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { Log("Replay status: " + e.Message); }
    }

    /// <summary>Processed games remain manageable after confirmed local copies are removed.</summary>
    public async Task<JsonObject> GetReplayLibraryAsync(CancellationToken ct)
    {
        await EnsureRegisteredAsync(ct);
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "games"), ct);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))?.AsObject() ?? new();
    }

    /// <summary>Changes access for one of this install's processed games.</summary>
    public async Task SetReplayAccessAsync(string mod, string game, JsonObject choice, CancellationToken ct)
    {
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"games/{Uri.EscapeDataString(mod)}/{Uri.EscapeDataString(game)}/access")
        { Content = new StringContent(choice.ToJsonString(), Encoding.UTF8, "application/json") }, ct);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>The converter's acknowledged progress and game links for a manual full-game upload.</summary>
    public async Task<JsonObject> GetBatchProgressAsync(string batch, CancellationToken ct)
    {
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"batches/{Uri.EscapeDataString(batch)}"), ct);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))!.AsObject();
    }
}
