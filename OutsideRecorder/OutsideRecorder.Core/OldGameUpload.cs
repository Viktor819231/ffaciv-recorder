using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OutsideRecorderCore;

/// <summary>How far one old game's upload got (old-uploads.json in the output folder); the upload goes on from here.</summary>
public sealed record OldUploadState
{
    /// <summary>The batch id the saves go under (X-Batch).</summary>
    public required string Batch { get; init; }
    /// <summary>MD5 of each save sent → the server's id.</summary>
    public Dictionary<string, string> Sent { get; init; } = new();
    /// <summary>The last save sent (the base for the next delta): its key on this PC and the server's id.</summary>
    public string? BaseKey { get; init; }
    /// <summary>The server's id of <see cref="BaseKey"/>.</summary>
    public string? BaseId { get; init; }
    /// <summary>Saves sent since the last whole one.</summary>
    public int SinceFull { get; init; }
    /// <summary>The batch was handed to the converter (POST batches/&lt;id&gt;/process).</summary>
    public bool Processed { get; init; }
}

/// <summary>
/// Sends whole old games (card OR03 "Bulk upload"): every save of a game under one batch id (X-Batch, X-Upload-Kind:
/// old-game), the first whole and the rest as deltas like the stream, then POST batches/&lt;id&gt;/process. Saves the
/// server or this PC already has (same MD5) are skipped; an interrupted upload goes on where it stopped.
/// </summary>
public sealed class OldGameUploader(RecorderSettings settings, Streamer streamer)
{
    readonly object fileLock = new();
    string StateFile => Path.Combine(settings.OutputFolder, "old-uploads.json");

    /// <summary>All upload states by game id.</summary>
    public Dictionary<string, OldUploadState> Load() { lock (fileLock) return LoadAll(settings.OutputFolder); }

    /// <summary>All upload states by game id, of the recordings under <paramref name="outputFolder"/>.</summary>
    public static Dictionary<string, OldUploadState> LoadAll(string outputFolder)
    {
        var file = Path.Combine(outputFolder, "old-uploads.json");
        try { return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, OldUploadState>>(SafeFiles.ReadAllText(file)) ?? [] : []; }
        catch { return []; }
    }

    void Save(string gameId, OldUploadState s)
    {
        lock (fileLock)
        {
            var all = Load();
            all[gameId] = s;
            Directory.CreateDirectory(settings.OutputFolder);
            SafeFiles.Replace(StateFile, JsonSerializer.Serialize(all));
        }
    }

    /// <summary>
    /// Uploads <paramref name="game"/>. <paramref name="known"/>: MD5s this PC already sent (recorded games, other uploads),
    /// skipped. <paramref name="progress"/> gets (saves done, saves in all). Returns (sent now, skipped).
    /// </summary>
    public async Task<(int Sent, int Skipped)> UploadAsync(OldGame game, ISet<string> known, Action<int, int>? progress, CancellationToken ct)
    {
        using var work = await streamer.EnterHeavyWorkAsync(ct);
        if (settings.SafeCapture && !streamer.CanContactServer()) throw new WorkDeferredException("Upload waits until Civ closes.");
        var all = Load();
        var state = all.TryGetValue(game.Id, out var s) ? s : new OldUploadState { Batch = $"old-{game.Id}" };
        bool leftOpen = s is { Processed: false } && s.Sent.Count > 0;   // sent before, stopped before the hand-over
        // a batch already handed over takes no more saves: what is new goes under a fresh batch
        if (state.Processed)
            foreach (var save in game.Saves)
            {
                ct.ThrowIfCancellationRequested();
                if (!streamer.CanContactServer() || streamer.Workload?.CanStartHeavyWork == false)
                    throw new WorkDeferredException("Upload paused while the game or computer is busy.");
                if (state.Sent.ContainsKey(OldGameScanner.Md5(save.Read()))) continue;
                state = new OldUploadState { Batch = $"old-{game.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}", Sent = state.Sent };
                break;
            }
        int done = 0, sent = 0, skipped = 0;
        SaveParts? baseParts = null; string? baseKey = null;
        foreach (var save in game.Saves)
        {
            ct.ThrowIfCancellationRequested();
            if (!streamer.CanContactServer() || streamer.Workload?.CanStartHeavyWork == false) throw new WorkDeferredException("Upload paused while the game or computer is busy.");
            var bytes = save.Read();
            var md5 = OldGameScanner.Md5(bytes);
            if (state.Sent.ContainsKey(md5) || known.Contains(md5)) { skipped++; progress?.Invoke(++done, game.Saves.Count); continue; }
            string? id = null; bool asDelta = false;
            bool full = settings.SaveUpload != "delta" || state.BaseId is null || state.BaseKey is null || state.SinceFull + 1 >= settings.FullSaveEvery;
            if (!full)
            {
                if (baseKey != state.BaseKey)
                {
                    var b = game.Saves.FirstOrDefault(x => x.Key == state.BaseKey);
                    baseParts = b is null ? null : SaveParts.Split(b.Read());
                    baseKey = state.BaseKey;
                }
                var parts = SaveParts.Split(bytes);
                var delta = baseParts is null || parts is null ? null : SaveDelta.Make(baseParts, parts, bytes);
                if (delta is not null && delta.Length < bytes.Length * 0.8)
                {
                    var (ok, answer) = await streamer.PostOldSaveAsync(save, delta, state.BaseId, state.Batch, ct);
                    if (ok) { id = answer; asDelta = true; }
                }
                if (parts is not null) { baseParts = parts; }
            }
            if (id is null)
            {
                var (_, answer) = await streamer.PostOldSaveAsync(save, bytes, null, state.Batch, ct);
                id = answer;
                if (settings.SaveUpload == "delta") baseParts = SaveParts.Split(bytes);
            }
            baseKey = save.Key;
            state = state with { Sent = new(state.Sent) { [md5] = id! }, BaseKey = save.Key, BaseId = id, SinceFull = asDelta ? state.SinceFull + 1 : 0 };
            Save(game.Id, state);
            sent++;
            progress?.Invoke(++done, game.Saves.Count);
        }
        if (sent > 0 || leftOpen)
        {
            await streamer.ProcessBatchAsync(state.Batch, ct);
            Save(game.Id, state with { Processed = true });
        }
        return (sent, skipped);
    }
}

public sealed partial class Streamer
{
    /// <summary>One old save under a batch (OR03 "Bulk upload"); a delta the server cannot use answers (false, null).</summary>
    public async Task<(bool Ok, string? Id)> PostOldSaveAsync(OldSave save, byte[] body, string? deltaBase, string batch, CancellationToken ct)
    {
        await EnsureRegisteredAsync(ct);
        var r = await SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "saves") { Content = new ByteArrayContent(body) };
            m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            m.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString(save.Name));
            m.Headers.TryAddWithoutValidation("X-Folder", FolderOf(save));
            m.Headers.TryAddWithoutValidation("X-File-Time", new DateTimeOffset(save.TimeUtc).ToUnixTimeMilliseconds().ToString());
            m.Headers.TryAddWithoutValidation("X-Batch", batch);
            m.Headers.TryAddWithoutValidation("X-Upload-Kind", "old-game");
            m.Headers.TryAddWithoutValidation("X-Store-Replay", "1");
            if (deltaBase is not null)
            {
                m.Headers.TryAddWithoutValidation("X-Save-Encoding", "orsd1");
                m.Headers.TryAddWithoutValidation("X-Delta-Base", deltaBase);
            }
            return m;
        }, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (r.StatusCode == HttpStatusCode.Conflict && deltaBase is not null && text.Contains("needFull")) return (false, null);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"save {save.Name}: {(int)r.StatusCode} {text}");
        using var doc = JsonDocument.Parse(text);
        Status = Status with { SaveWireBytes = Status.SaveWireBytes + body.Length, LastSent = DateTimeOffset.Now };
        return (true, doc.RootElement.GetProperty("id").GetString());
    }

    /// <summary>POST batches/&lt;id&gt;/process: the batch's saves go to the converter (it sorts them into games by their seeds).</summary>
    public async Task ProcessBatchAsync(string batch, CancellationToken ct)
    {
        await EnsureRegisteredAsync(ct);
        var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"batches/{Uri.EscapeDataString(batch)}/process")
        { Content = new StringContent("{\"store\":true}", Encoding.UTF8, "application/json") }, ct);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"batch {batch}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>
    /// Block / Unblock (OR03 "Visibility"): POST games/&lt;game&gt;/visibility {"blocked"} with this install's key. Returns the
    /// HTTP status (200 = taken; 401 key, 403 this install sent nothing of the game, 404 not converted yet) and the answer.
    /// </summary>
    public async Task<(int Status, string Body)> SetVisibilityAsync(string game, bool blocked, CancellationToken ct)
    {
        await EnsureRegisteredAsync(ct);
        var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"games/{Uri.EscapeDataString(game)}/visibility")
        { Content = new StringContent(JsonSerializer.Serialize(new { blocked }), Encoding.UTF8, "application/json") }, ct);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync(ct));
    }

    /// <summary>"ModdedSaves/multi/auto" for a save in Civ's folders, else the folder's own name (at most 200 characters).</summary>
    static string FolderOf(OldSave s)
    {
        var dir = Path.GetDirectoryName(s.ZipEntry is null ? s.Path : s.ZipEntry) ?? "";
        int at = dir.IndexOf("Civilization 5", StringComparison.OrdinalIgnoreCase);
        var f = (at >= 0 ? dir[(at + "Civilization 5".Length)..] : Path.GetFileName(dir)).Trim('\\', '/').Replace('\\', '/');
        f = new string(f.Where(c => c >= ' ').ToArray());
        return f.Length > 200 ? f[..200] : f.Length == 0 ? "old-saves" : f;
    }
}
