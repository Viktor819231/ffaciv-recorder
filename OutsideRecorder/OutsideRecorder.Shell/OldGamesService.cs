using System.Text.Json;
using System.Text.Json.Nodes;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>
/// "Upload old games" (OR03 "Bulk upload"): finds the games in Civ's save folders (and folders the player adds, zips
/// included), says which ones this PC already sent (recorded or uploaded: the same save bytes), and uploads the games the
/// player ticks, whole, one after another, in the background. Never single saves (the user's rule).
/// </summary>
sealed class OldGamesService(RecorderSettings settings, Func<Streamer?> streamer, bool preview) : IDisposable
{
    /// <summary>Resource pressure defers scans and individual file hashes without changing saved choices.</summary>
    public Func<bool> CanWork { get; init; } = () => true;
    /// <summary>Distinguishes an explicit sending switch from a temporary resource or playing pause.</summary>
    public Func<bool> SendingEnabled { get; init; } = () => true;
    public Func<RecorderSettings>? UploadSettings { get; init; }
    readonly CancellationTokenSource lifetime = new();
    readonly object gate = new();
    readonly string foldersFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFACiv", "OutsideRecorder", "app", "old-folders.txt");
    SaveFingerprints? fingerprints;
    /// <summary>The MD5s of saves read before, also from earlier runs (made on first use: the output folder is known by then).</summary>
    SaveFingerprints Prints => fingerprints ??= new SaveFingerprints(settings.OutputFolder);
    List<OldGame> games = [];
    Dictionary<string, int> already = [];            // game id → saves this PC already sent
    readonly Dictionary<string, (string State, int Done, int Total, string? Note)> progress = [];
    readonly Queue<string> queue = new();
    readonly Dictionary<string, JsonObject> generation = [];
    bool scanning, uploading, scanned;
    DateTime? scannedAt;
    CancellationTokenSource? stop;

    bool MayWork() => !lifetime.IsCancellationRequested && CanWork() && (!settings.SafeCapture || !UploadPolicy.CivRunning());
    void CheckWork()
    {
        lifetime.Token.ThrowIfCancellationRequested();
        if (!MayWork()) throw new WorkDeferredException("Waiting until the game and computer are ready.");
    }

    /// <summary>Folders the player added (kept between starts).</summary>
    List<string> Extra()
    {
        try { return File.Exists(foldersFile) ? [.. File.ReadAllLines(foldersFile).Where(l => l.Trim().Length > 0)] : []; } catch { return []; }
    }

    /// <summary>Looks through the save folders in the background (the window shows "Looking…").</summary>
    public void Scan()
    {
        if (lifetime.IsCancellationRequested) return;
        lock (gate) { if (scanning) return; scanning = true; }
        Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    if (!MayWork()) { await Task.Delay(2000, lifetime.Token); continue; }
                    try
                    {
                        CheckWork();
                        // Only publish a complete scan, preserving the last good list if work is deferred midway.
                        var extra = Extra().Concat(Directory.Exists(settings.OutputFolder) ? Directory.GetDirectories(settings.OutputFolder, "unsupported-*") : []);
                        var saves = OldGameScanner.Find(OldGameScanner.CivSaveFolders(), extra, MayWork);
                        var found = OldGameScanner.Group(saves, MayWork).Where(g => !OldGameScanner.NotOffered(g)).ToList();
                        var known = Known();
                        var counts = found.ToDictionary(g => g.Id, g => g.Saves.Count(s => Md5Of(s) is { } m && known.Contains(m)));
                        Prints.Save();
                        CheckWork();
                        lock (gate) { games = found; already = counts; scannedAt = DateTime.Now; scanned = true; }
                        foreach (var (id, state) in OldGameUploader.LoadAll(settings.OutputFolder).Where(x => x.Value.Processed))
                        { lock (gate) if (generation.ContainsKey(id) || progress.ContainsKey(id)) continue; _ = TrackGeneration(id, lifetime.Token); }
                        break;
                    }
                    catch (WorkDeferredException) { Prints.Save(); await Task.Delay(2000, lifetime.Token); }
                }
            }
            catch { }
            finally { lock (gate) scanning = false; }
        });
    }

    string? Md5Of(OldSave s)
    {
        CheckWork();
        if (Prints.Known(s) is { } m) return m;
        try { CheckWork(); return Prints.Of(s, s.Read()); }
        catch (WorkDeferredException) { throw; }
        catch { return null; }
    }

    /// <summary>MD5s this PC sent already: every save the recorder copied (recorded games) and every old save uploaded.</summary>
    HashSet<string> Known()
    {
        var set = new HashSet<string>();
        CheckWork();
        try
        {
            if (Directory.Exists(settings.OutputFolder))
                foreach (var dir in Directory.GetDirectories(settings.OutputFolder, "session-*"))
                {
                    CheckWork();
                    foreach (var f in Streamer.SavesOf(dir))
                    {
                        var i = new FileInfo(f);
                        if (Md5Of(new OldSave { Path = f, Name = i.Name, TimeUtc = i.LastWriteTimeUtc, Size = i.Length }) is { } m) set.Add(m);
                    }
                }
        }
        catch (WorkDeferredException) { throw; }
        catch { }
        CheckWork();
        foreach (var u in OldGameUploader.LoadAll(settings.OutputFolder).Values) set.UnionWith(u.Sent.Keys);
        return set;
    }

    /// <summary>Adds a folder (its subfolders and zip files too) and looks again.</summary>
    public void AddFolder(string dir)
    {
        try
        {
            var list = Extra();
            if (!list.Contains(dir, StringComparer.OrdinalIgnoreCase)) { list.Add(dir); Directory.CreateDirectory(Path.GetDirectoryName(foldersFile)!); File.WriteAllLines(foldersFile, list); }
        }
        catch { }
        Scan();
    }

    /// <summary>Queues whole games for upload; they go one after another.</summary>
    public string? Upload(IEnumerable<string> ids)
    {
        if (preview) return "Not in a picture run.";
        if (lifetime.IsCancellationRequested) return "The recorder is closing.";
        if (!SendingEnabled()) return "Finish setup first. Manual uploads remain available when automatic uploads are off.";
        lock (gate)
        {
            foreach (var id in ids)
                if (games.Any(g => g.Id == id) && !queue.Contains(id) && progress.GetValueOrDefault(id).State is not ("uploading" or "queued"))
                { queue.Enqueue(id); generation.Remove(id); progress[id] = ("queued", 0, games.First(g => g.Id == id).Saves.Count, "Waiting for the selected games ahead of this one."); }
            if (uploading || queue.Count == 0) return null;
            uploading = true;
            stop?.Dispose();
            stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        }
        var ct = stop.Token;
        Task.Run(async () =>
        {
            while (true)
            {
                string id; OldGame game;
                lock (gate)
                {
                    if (queue.Count == 0 || ct.IsCancellationRequested) { uploading = false; foreach (var q in queue) progress.Remove(q); queue.Clear(); break; }
                    id = queue.Dequeue();
                    var selected = games.FirstOrDefault(g => g.Id == id);
                    if (selected is null) { progress[id] = ("stopped", 0, 0, "These saves are no longer available. Scan the folder again."); continue; }
                    game = selected;
                    progress[id] = ("uploading", 0, game.Saves.Count, null);
                }
                try
                {
                    int sent = 0, skipped = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!SendingEnabled()) throw new InvalidOperationException("Finish setup before uploading. Your uploaded progress is retained.");
                        var s = MayWork() ? streamer() : null;
                        if (s is null)
                        {
                            lock (gate) progress[id] = ("queued", progress[id].Done, game.Saves.Count, "Waiting until the game and computer are ready. Upload resumes automatically.");
                            await Task.Delay(2000, ct);
                            continue;
                        }
                        try
                        {
                            (sent, skipped) = await new OldGameUploader(UploadSettings?.Invoke() ?? settings, s, Prints).UploadAsync(game, new HashSet<string>(),
                                (d, t) => { lock (gate) progress[id] = ("uploading", Math.Max(progress[id].Done, d), t, null); }, ct);
                            break;
                        }
                        catch (WorkDeferredException)
                        {
                            lock (gate) progress[id] = ("queued", progress[id].Done, game.Saves.Count, "Paused to leave resources for the game. Upload resumes automatically.");
                            await Task.Delay(2000, ct);
                        }
                        catch (ObjectDisposedException)
                        {
                            // a changed setting started the sender again and closed the old one under this upload: go on with the new one
                            await Task.Delay(500, ct);
                        }
                    }
                    lock (gate) progress[id] = ("processing", 0, game.Saves.Count, $"All {game.Saves.Count} saves received. Waiting for server parsing…");
                    // Packaging can take time; report only what the converter acknowledged.
                    _ = TrackGeneration(id, lifetime.Token);
                }
                catch (OperationCanceledException) { lock (gate) progress[id] = ("stopped", progress[id].Done, game.Saves.Count, "Stopped. Upload again to go on where it stopped."); }
                catch (Exception e) { lock (gate) progress[id] = ("error", progress[id].Done, game.Saves.Count, e is HttpRequestException ? "The server did not take it: " + e.Message : e.Message); }
            }
            Scan();
        });
        return null;
    }

    async Task TrackGeneration(string id, CancellationToken ct)
    {
        int wait = 5000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!MayWork()) { await Task.Delay(2000, ct); continue; }
                var batch = OldGameUploader.LoadAll(settings.OutputFolder).GetValueOrDefault(id)?.Batch;
                if (batch is null || !SendingEnabled()) return;
                if (streamer() is not { } s) { await Task.Delay(2000, ct); continue; }
                var state = await s.GetBatchProgressAsync(batch, ct);
                lock (gate)
                {
                    generation[id] = BatchUploadProgress.Read(state);
                    var phase = generation[id]["phase"]!.GetValue<string>();
                    progress[id] = (phase == "ready" ? "done" : phase is "unsupported" or "error" ? phase : "processing", state["processed"]?.GetValue<int>() ?? 0,
                        state["saves"]?.GetValue<int>() ?? 0, generation[id]["note"]?.GetValue<string>());
                    if (phase is "ready" or "error") return;
                    // waiting for the site to support this mod version takes days, a busy server minutes: no ask every 5 s
                    wait = phase switch { "unsupported" => 600_000, "held" => 60_000, _ => 5000 };
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) return;
                lock (gate) if (progress.TryGetValue(id, out var p)) progress[id] = (p.State, p.Done, p.Total, "Connection interrupted. Checking server progress again automatically…");
            }
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Stops after the save on its way (the next upload goes on from there).</summary>
    public void Stop() { lock (gate) stop?.Cancel(); }

    /// <summary>Cancels deferred scans and uploads when the recorder closes.</summary>
    public void Dispose() { lifetime.Cancel(); Stop(); }

    /// <summary>What the window shows (OR02 state: oldGames).</summary>
    public JsonObject State()
    {
        lock (gate)
        {
            var list = new JsonArray();
            foreach (var g in games)
            {
                int have = already.GetValueOrDefault(g.Id);
                var p = progress.TryGetValue(g.Id, out var pr) ? pr : default;
                string status = p.State is { } st ? st : have >= g.Saves.Count ? "uploaded" : have > 0 ? "partial" : "new";
                var server = generation.GetValueOrDefault(g.Id);
                var phase = server?["phase"]?.GetValue<string>() ?? (status == "processing" ? "parsing" : status);
                var uploaded = status is "uploading" or "queued" or "stopped" ? p.Done : status is "processing" or "done" or "unsupported" || server is not null ? g.Saves.Count : have;
                list.Add(J.Obj(("id", g.Id), ("mod", g.Label.Mod ?? "Civ V with mods"),
                    ("from", J.Ms(new DateTimeOffset(g.From))), ("to", J.Ms(new DateTimeOffset(g.To))), ("saves", g.Saves.Count),
                    ("firstTurn", g.FirstTurn), ("lastTurn", g.LastTurn), ("mapSize", g.Label.MapSize), ("speed", g.Label.Speed), ("map", g.Label.MapScript),
                    ("folder", g.Folder), ("megabytes", Math.Round(g.Saves.Sum(s => s.Size) / 1048576.0, 1)),
                    ("already", have), ("status", status), ("done", p.Done), ("total", p.Total == 0 ? g.Saves.Count : p.Total),
                    ("phase", phase), ("uploaded", uploaded), ("parsed", server?["parsed"]?.DeepClone()), ("parseTotal", server?["parseTotal"]?.DeepClone()),
                    ("note", p.Note), ("links", server?["links"]?.DeepClone())));
            }
            return J.Obj(("scanning", scanning), ("scanned", scanned), ("scannedAt", scannedAt is { } t ? J.Ms(new DateTimeOffset(t)) : null), ("uploading", uploading),
                ("folders", J.Arr(OldGameScanner.CivSaveFolders().Where(Directory.Exists).Concat(Extra()).Select(f => (JsonNode?)f))),
                ("newCount", games.Count(g => already.GetValueOrDefault(g.Id) < g.Saves.Count)), ("games", list));
        }
    }
}
