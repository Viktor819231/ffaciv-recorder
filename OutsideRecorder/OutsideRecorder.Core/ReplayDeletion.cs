using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OutsideRecorderCore;

/// <summary>A durable withdrawal target; its queue entry remains until the website accepts it.</summary>
public sealed record ReplayDeletionTarget(string? Context, string? Mod, string? Game)
{
    /// <summary>The queue entry's key ("recording:&lt;context&gt;" or "game:&lt;mod&gt;/&lt;game&gt;"): what Undo names.</summary>
    public string Key => KeyOf(Context, Mod, Game);
    /// <summary>The queue entry's key for a recording or a site game.</summary>
    public static string KeyOf(string? context, string? mod, string? game) => context is null ? $"game:{mod}/{game}" : "recording:" + context;
    /// <summary>When the player pressed Delete (null: an older queue entry).</summary>
    public DateTimeOffset? DeletedAt { get; init; }
    /// <summary>Until then the deletion is held and Undo brings the recording back (null: deleted as soon as the site answers).</summary>
    public DateTimeOffset? Due { get; init; }
    /// <summary>The recording's card as it was, so the held deletion can still be shown and undone.</summary>
    public JsonObject? Card { get; init; }
}

public sealed partial class Streamer
{
    readonly SemaphoreSlim deletingReplays = new(1, 1);
    string DeletionsFolder => Path.Combine(settings.OutputFolder, "replay-deletions");
    /// <summary>The queue file of a deletion key: its SHA-256, so any key is a safe file name.</summary>
    string DeletionFile(string key) => Path.Combine(DeletionsFolder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    /// <summary>Queued withdrawal targets let the UI hide accepted deletions even when resource gates defer server work.</summary>
    public IReadOnlyList<ReplayDeletionTarget> QueuedReplayDeletions()
    {
        if (!Directory.Exists(DeletionsFolder)) return [];
        var targets = new List<ReplayDeletionTarget>();
        foreach (var file in Directory.GetFiles(DeletionsFolder, "*.json"))
            try
            {
                if (JsonNode.Parse(SafeFiles.ReadAllText(file)) is not JsonObject o) continue;
                targets.Add(new(o["context"]?.GetValue<string>(), o["mod"]?.GetValue<string>(), o["game"]?.GetValue<string>())
                {
                    DeletedAt = o["deletedAt"] is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at.GetValue<long>()) : null,
                    Due = o["due"] is { } due ? DateTimeOffset.FromUnixTimeMilliseconds(due.GetValue<long>()) : null,
                    Card = o["card"] as JsonObject,
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
        return targets;
    }

    /// <summary>True until this confirmed user request has been acknowledged by the website.</summary>
    public bool ReplayDeletionQueued(string? context, string? mod = null, string? game = null)
    {
        if (context is null && (string.IsNullOrWhiteSpace(mod) || string.IsNullOrWhiteSpace(game))) return false;
        return File.Exists(DeletionFile(ReplayDeletionTarget.KeyOf(context, mod, game)));
    }

    /// <summary>The days a deleted recording stays hidden but can be brought back (the user, 2026-10-31: "a way to regret deleting replays").</summary>
    public static readonly TimeSpan DeletionHold = TimeSpan.FromDays(7);

    /// <summary>
    /// Persists a confirmed deletion immediately, including while the website is offline. With <paramref name="hold"/> the
    /// replay is only made private on the site at once; the real withdrawal (and the local copy's removal) waits until the
    /// hold ends, and <see cref="CancelReplayDeletionAsync"/> brings it back before then. <paramref name="card"/> is kept
    /// to show the held recording; <paramref name="wasPrivate"/> is restored by Undo.
    /// </summary>
    public void QueueReplayDeletion(string? context, string? mod = null, string? game = null, bool waitForEnd = false,
        TimeSpan? hold = null, JsonObject? card = null, bool wasPrivate = false)
    {
        if (context is not null && !GameIndex.IsContext(context)) throw new ArgumentException("Invalid recording.");
        if (context is null && (string.IsNullOrWhiteSpace(mod) || string.IsNullOrWhiteSpace(game))) throw new ArgumentException("No game selected.");
        Directory.CreateDirectory(DeletionsFolder);
        var file = DeletionFile(ReplayDeletionTarget.KeyOf(context, mod, game));
        var now = DateTimeOffset.UtcNow;
        var command = new JsonObject { ["context"] = context, ["mod"] = mod, ["game"] = game, ["waitForEnd"] = waitForEnd };
        if (hold is { } h)
        {
            command["deletedAt"] = now.ToUnixTimeMilliseconds(); command["due"] = (now + h).ToUnixTimeMilliseconds();
            command["card"] = card?.DeepClone(); command["wasPrivate"] = wasPrivate;
        }
        SafeFiles.Replace(file, command.ToJsonString());
    }

    /// <summary>
    /// Undo: takes a held deletion out of the queue before its hold ends (waits for a deletion pass in progress). Returns
    /// the queue entry (its card, whether the site already hid it and whether it was private before), or null when it is
    /// gone already: the hold ended and the site withdrew it.
    /// </summary>
    public async Task<JsonObject?> CancelReplayDeletionAsync(string key, CancellationToken ct)
    {
        var file = DeletionFile(key);
        await deletingReplays.WaitAsync(ct);
        try
        {
            if (!File.Exists(file) || JsonNode.Parse(SafeFiles.ReadAllText(file)) is not JsonObject command) return null;
            File.Delete(file);
            return command;
        }
        finally { deletingReplays.Release(); }
    }

    /// <summary>
    /// Delete with Undo: the replay leaves the website at once, like any other visibility choice (0.7.25), also while uploads
    /// wait for Civ's main menu or a calmer PC. Before, the hide waited with the uploads, while the window already said
    /// "Hidden from your list and from the website now". Only the hide goes now; the withdrawal itself waits for its hold.
    /// </summary>
    public async Task HideHeldDeletionsNowAsync(CancellationToken ct)
    {
        if (!Directory.Exists(DeletionsFolder) || !await deletingReplays.WaitAsync(0, ct)) return;
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var file in Directory.GetFiles(DeletionsFolder, "*.json"))
            {
                try
                {
                    var command = JsonNode.Parse(SafeFiles.ReadAllText(file))!.AsObject();
                    if (command["due"] is not { } due || due.GetValue<long>() <= now || command["hidden"]?.GetValue<bool>() == true) continue;
                    await HideHeldReplayAsync(file, command, command["context"]?.GetValue<string>(), ct);
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException or IOException or UnauthorizedAccessException) { }
            }
        }
        finally { deletingReplays.Release(); }
    }

    /// <summary>A held deletion's first step: the replay is made private on the site (nothing there yet: nothing to hide).
    /// A few bytes the player asked for: it goes past the upload gates, like Make private.</summary>
    async Task<bool> HideHeldReplayAsync(string file, JsonObject command, string? context, CancellationToken ct)
    {
        accessChange.Value = true;   // flows into this call's requests only
        await EnsureRegisteredAsync(ct);
        var path = context is not null ? $"recordings/{context}/state"
            : $"games/{Uri.EscapeDataString(command["mod"]!.GetValue<string>())}/{Uri.EscapeDataString(command["game"]!.GetValue<string>())}/access";
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent("{\"private\":true}", Encoding.UTF8, "application/json") }, ct);
        if (!r.IsSuccessStatusCode && r.StatusCode is not System.Net.HttpStatusCode.NotFound and not System.Net.HttpStatusCode.Gone) return false;
        command["hidden"] = true;
        SafeFiles.Replace(file, command.ToJsonString());
        return true;
    }

    bool DeletionPending(GameEntry game)
    {
        if (ReadReplay(game.Context)["state"]?.GetValue<string>() == "deleted") return true;
        return ReplayDeletionQueued(game.Context);
    }

    /// <summary>Retries explicit deletions; successful requests suppress future uploads from the deleted recording.</summary>
    public async Task FlushReplayDeletionsAsync(CancellationToken ct)
    {
        if (!CanContactServer() || Workload?.CanStartHeavyWork == false) return;
        if (!Directory.Exists(DeletionsFolder) || !await deletingReplays.WaitAsync(0, ct)) return;
        try
        {
            foreach (var file in Directory.GetFiles(DeletionsFolder, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                if (!CanContactServer() || Workload?.CanStartHeavyWork == false) return;
                if (deletionRest.TryGetValue(file, out var until) && Environment.TickCount64 < until) continue;
                // one deletion the site refuses (or a damaged queue file) waits on its own: before, it threw here, before
                // the saves pass, so no save of any game was sent again
                try { await FlushReplayDeletionAsync(file, ct); }
                catch (Exception e) when (e is SiteRefusedException or System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException or IOException or UnauthorizedAccessException)
                {
                    deletionRest[file] = Environment.TickCount64 + (long)RefusedRest.TotalMilliseconds;
                    Log($"deletion {Path.GetFileName(file)}: {e.Message} (tried again in {RefusedRest.TotalMinutes:F0} min)");
                }
            }
        }
        finally { deletingReplays.Release(); }
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> deletionRest = new(StringComparer.OrdinalIgnoreCase);

    async Task FlushReplayDeletionAsync(string file, CancellationToken ct)
    {
        var command = JsonNode.Parse(SafeFiles.ReadAllText(file))!.AsObject();
        var context = command["context"]?.GetValue<string>();
        // held (Undo still possible): only hidden on the site until the hold ends
        if (command["due"] is { } due && due.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            // not hidden yet (the site refused or failed): asked again in a minute, while the other deletions go on
            if (command["hidden"]?.GetValue<bool>() != true && !await HideHeldReplayAsync(file, command, context, ct))
                deletionRest[file] = Environment.TickCount64 + 60_000;
            return;
        }
        var entry = context is null ? null : new GameIndex(settings.OutputFolder).Load().LastOrDefault(g => g.Context == context);
        var eventsFile = entry is null ? null : Path.Combine(entry.Folder, "events.jsonl");
        if (command["waitForEnd"]?.GetValue<bool>() == true && eventsFile is not null && File.Exists(eventsFile) && !Ended(eventsFile)) return;
        await EnsureRegisteredAsync(ct);
        var path = context is not null ? $"recordings/{context}/delete"
            : $"games/{Uri.EscapeDataString(command["mod"]!.GetValue<string>())}/{Uri.EscapeDataString(command["game"]!.GetValue<string>())}/delete";
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") }, ct);
        // 404/410: the site has nothing (left) of it for this install, which is what the deletion wanted
        if (!response.IsSuccessStatusCode && response.StatusCode is not System.Net.HttpStatusCode.NotFound and not System.Net.HttpStatusCode.Gone)
            throw RefusalOrError(response.StatusCode, $"delete: {(int)response.StatusCode} {SiteWords(await response.Content.ReadAsStringAsync(ct))}");
        if (context is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReplayFile(context))!);
            SafeFiles.Replace(ReplayFile(context), "{\"state\":\"deleted\"}");
            if (entry is not null && Directory.Exists(entry.Folder))
            {
                // the site copy is gone; a local copy that cannot go now (a linked folder, a file in use) stays on this PC
                try { LocalRecordingDeletion.Delete(settings.OutputFolder, entry.Folder); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"deletion: local copy kept ({e.Message})"); }
            }
            File.Delete(ReplayFile(context, true));
        }
        File.Delete(file);
    }
}
