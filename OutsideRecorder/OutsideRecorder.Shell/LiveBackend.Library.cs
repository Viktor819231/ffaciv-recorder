using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>The recordings on this PC and the replays on the site: the games list, each game's facts, its saves and why they wait.</summary>
sealed partial class LiveBackend
{
    bool libraryBusy;

    JsonArray siteGames = [];

    readonly Dictionary<string, (long Length, DateTime Time, GameFacts Facts)> factsCache = [];

    static (long QueuedBytes, int QueuedSaves, int? LastTurn, int SentSaves) StreamOf(string folder)
    {
        try
        {
            var f = Path.Combine(folder, "stream.json");
            var state = File.Exists(f) ? JsonSerializer.Deserialize<StreamState>(SafeFiles.ReadAllText(f)) ?? new() : new();
            var events = Path.Combine(folder, "events.jsonl");
            long queued = File.Exists(events) ? Math.Max(0, new FileInfo(events).Length - state.SentOffset) : 0;
            int queuedSaves = Streamer.SavesOf(folder).Count(x => !state.SentSaves.ContainsKey(Path.GetFileName(x)));
            int? last = state.SentSaves.Keys.Select(RecorderSession.AutosaveTurn).Where(t => t is not null).Max();
            return (queued, queuedSaves, last, state.SentSaves.Count);
        }
        catch { return (0, 0, null, 0); }
    }

    // ---- the games on this PC --------------------------------------------------------------------------------------------
    /// <summary>What the history shows of one recording folder (read once per change of its events.jsonl).</summary>
    sealed record GameFacts(string? Mod, string? ModVersion, int? LastTurn, JsonArray Players, string? Identity, DateTimeOffset Date);

    /// <summary>The recordings on this PC (one folder per game, games.json says which game): mod, turns, civs, stream state.</summary>
    JsonArray ListGames(RecorderStatus s, string mode)
    {
        var list = new JsonArray();
        try
        {
            if (!Directory.Exists(fileSettings.OutputFolder)) return list;
            var blocks = visibility.All();
            var known = new GameIndex(fileSettings.OutputFolder).Load().GroupBy(e => Path.GetFullPath(e.Folder), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(fileSettings.OutputFolder, "session-*").OrderByDescending(d => Path.GetFileName(d)).Take(40))
            {
                var file = Path.Combine(dir, "events.jsonl");
                if (!File.Exists(file) && !Streamer.SavesOf(dir).Any()) continue;
                var facts = File.Exists(file) ? FactsOf(dir, file) : new GameFacts("Civ V · save files only", null,
                    Streamer.SavesOf(dir).Select(RecorderSession.AutosaveTurn).Where(t => t is not null).DefaultIfEmpty().Max(), [], null,
                    RecorderSession.StartedOf(dir) ?? Directory.GetCreationTime(dir));
                known.TryGetValue(Path.GetFullPath(dir), out var entry);
                if (entry is not null && session.IsDeletedIdentity(entry.Identity)) continue;
                bool now = s.Recording && s.Folder is not null && string.Equals(Path.GetFullPath(s.Folder), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)
                    && (entry is null || !Streamer.HasEnded(entry));
                var (qb, qs, lastSent, sentSaves) = StreamOf(dir);
                bool streamed = sentSaves > 0;
                var status = now ? "recording" : !streamed ? "local" : qb > 0 || qs > 0 ? (mode == "off" || entry is not null && !CanUploadRecording(entry) ? "local" : "sending") : "streamed";
                list.Add(J.Obj(("id", Path.GetFileName(dir)), ("mod", facts.Mod ?? "Civ V"), ("modVersion", facts.ModVersion), ("date", J.Ms(facts.Date)),
                    ("turns", now ? s.Turn : facts.LastTurn), ("players", facts.Players.DeepClone()), ("status", status), ("folder", dir),
                    ("identity", entry?.Identity ?? facts.Identity), ("sentSaves", sentSaves), ("lastSentTurn", lastSent), ("queuedSaves", qs)));
                var gid = entry?.Identity ?? facts.Identity;
                var choice = gid is null ? null : blocks.GetValueOrDefault(gid);
                string? why = gid is null || !Regex.IsMatch(gid, "^[0-9a-f]{8}-[0-9a-f]{8}$") ? "This recording has no game id, so the site cannot tell which game it is."
                    : now || gid == s.Identity ? "This game is still being played. Block it after the game." : null;
                var g = (JsonObject)list[^1]!;
                // A finished game's last lines and its finish wait for Civ closed (its main menu too while playing uploads),
                // also with the livestream on: the global reason does not say so then, and the window fell back to "waiting for the server".
                bool waitsForMenu = entry is not null && !now && !CanUploadRecording(entry) && !uploadPolicy.IsIdle();
                g["waitsForMenu"] = waitsForMenu;
                g["uploadWaitReason"] = UploadWaitReason() ?? (waitsForMenu
                    ? uploadPolicy.AfterPlaying ? "Civ is still open. The replay is made by itself once Civ is closed."
                        : "Civ is still open in a game or a lobby. The replay is made by itself once Civ is at its main menu or closed." : null);
                var (firstTurn, lastSaved, savedTurns) = TurnsOf(dir);
                g["firstTurn"] = firstTurn; g["lastTurn"] = now ? s.Turn ?? lastSaved : lastSaved; g["savedTurns"] = savedTurns;
                if (list.Count <= 3 || savesShown.Contains(Path.GetFileName(dir)))
                {
                    g["saves"] = SaveList(dir, entry?.Context);
                    g["savesWait"] = SaveWaitReason(entry, mode, streamer?.Status);
                }
                if (entry is not null) g["uploadSchedule"] = UploadSchedule(entry.Context);
                g["blocked"] = choice?.Blocked == true; g["blockConfirmed"] = choice?.Confirmed; g["blockError"] = choice?.LastError;
                g["canBlock"] = why is null; g["blockWhy"] = why; g["context"] = entry?.Context;
                var replay = entry?.Context is { } cx ? ReplayUiState(cx) : null;
                g["replay"] = replay;
                if (replay?.Str("game") is { } key) { g["siteGame"] = key.Split('/')[^1]; g["siteMod"] = replay.Str("mod"); }
                if (!now && replay?["lastTurn"] is { } lastTurn) g["turns"] = lastTurn.DeepClone();
                if (replay?.Str("state") == "deleted") { list.RemoveAt(list.Count - 1); continue; }
                if (replay?.Str("url") is { } url) g["url"] = url;
            }
        }
        catch { }
        foreach (var remote in siteGames.OfType<JsonObject>())
        {
            var url = remote.Str("pageUrl");
            if (url is null) continue;
            var existing = list.OfType<JsonObject>().FirstOrDefault(g => g.Str("url") == url || (g.Str("siteGame") == remote.Str("game") && g.Str("siteMod") == remote.Str("mod")));
            var g = existing ?? J.Obj(("id", "site-" + remote.Str("mod") + "-" + remote.Str("game")), ("mod", remote.Str("mod")),
                ("turns", remote["lastTurn"]?.DeepClone()), ("date", DateValue(remote["lastSaveAt"]) is > 0 and var at ? at : null), ("players", new JsonArray()),
                ("status", remote["private"] is JsonValue hidden && hidden.TryGetValue<bool>(out var isPrivate) && isPrivate ? "locked" : "public"));
            g["url"] = url; g["siteGame"] = remote.Str("game"); g["siteMod"] = remote.Str("mod");
            g["private"] = remote["private"]?.DeepClone(); g["restrictedUntil"] = remote["restrictedUntil"]?.DeepClone();
            if (existing is null) list.Add(g);
        }
        return new JsonArray(list.OfType<JsonObject>().OrderByDescending(g => DateValue(g["date"])).Select(g => (JsonNode)g.DeepClone()).ToArray());
    }

    static long DateValue(JsonNode? value) => long.TryParse(value?.ToString(), out var n) ? n : DateTimeOffset.TryParse(value?.ToString(), out var t) ? t.ToUnixTimeMilliseconds() : 0;

    void RefreshLibrary()
    {
        if (!NormalServerAllowed) return;
        if (preview || libraryBusy || Environment.TickCount64 < nextLibrary || streamer is null) return;
        libraryBusy = true; nextLibrary = Environment.TickCount64 + 15000;
        _ = Task.Run(async () =>
        {
            try { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await streamer.FlushReplayDeletionsAsync(ct.Token);
                var answer = await streamer.GetReplayLibraryAsync(ct.Token);
                siteGames = deletedCards.Filter(answer["games"] as JsonArray ?? []); nextChecks = 0;
            } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            finally { libraryBusy = false; }
        });
    }

    /// <summary>Set from other threads (Undo) instead of clearing <see cref="factsCache"/> there: a Dictionary must not be
    /// changed while the window's thread reads it.</summary>
    volatile bool factsStale;

    GameFacts FactsOf(string dir, string file)
    {
        if (factsStale) { factsStale = false; factsCache.Clear(); }
        if (!NormalServerAllowed)
            return factsCache.TryGetValue(dir, out var previous) ? previous.Facts : new GameFacts("Recorded game", null, null, [], null, Directory.GetCreationTime(dir));
        var fi = new FileInfo(file);
        if (factsCache.TryGetValue(dir, out var c) && c.Length == fi.Length && c.Time == fi.LastWriteTimeUtc) return c.Facts;
        string? first = null, lastSnapshot = null;
        try
        {
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using (var r = new StreamReader(f, Encoding.UTF8, false, 8192, leaveOpen: true)) first = r.ReadLine();
            // the newest snapshot (every unit and city at a turn start) says who plays: look in the last 2 MB
            long start = Math.Max(0, f.Length - 2 * 1024 * 1024);
            f.Seek(start, SeekOrigin.Begin);
            var tail = new StreamReader(f, Encoding.UTF8).ReadToEnd();
            int at = tail.LastIndexOf("\"kind\":\"snapshot\"", StringComparison.Ordinal);
            if (at >= 0)
            {
                int ls = tail.LastIndexOf('\n', at) + 1, le = tail.IndexOf('\n', at);
                lastSnapshot = tail[ls..(le < 0 ? tail.Length : le)].TrimEnd('\r');
            }
        }
        catch { }
        string? layout = null, identity = null;
        try { var d = JsonNode.Parse(first ?? "{}")?["data"]; layout = d?["mod"]?.GetValue<string>(); identity = d?["identity"]?.GetValue<string>(); } catch { }
        var players = new JsonArray();
        try
        {
            if (lastSnapshot is not null && JsonNode.Parse(lastSnapshot)?["data"] is JsonObject snap)
            {
                int? me = snap["activePlayer"]?.GetValue<int?>();
                var ps = (snap["players"] as JsonArray ?? []).OfType<JsonObject>().Select(p => new PlayerState
                {
                    Id = p["id"]!.GetValue<int>(), Alive = p["alive"]?.GetValue<bool>() ?? false, TurnActive = false,
                    Slot = p["slot"]?.GetValue<int?>(), CivType = p["civType"]?.GetValue<int?>(), Civ = p["civ"]?.GetValue<string?>(),
                });
                players = Players(ps, me);
            }
        }
        catch { }
        var (mod, ver) = ModOf(layout);
        var facts = new GameFacts(mod, ver, JsonlRecording.LastTurn(file), players, identity, RecorderSession.StartedOf(dir) ?? Directory.GetCreationTime(dir));
        factsCache[dir] = (fi.Length, fi.LastWriteTimeUtc, facts);
        return facts;
    }

    string? UploadWaitReason() => SetupPending ? "Finish setup before uploading."
        : session.TurnTransitionQuiet ? "Waiting for the new turn to settle."
        : !workload.CanStartHeavyWork ? workload.Reason
        : session.HasPendingAutosaves ? "Finishing pending save copies."
        : !LiveServerAllowed && uploadPolicy.AfterPlaying && !uploadPolicy.IsIdle() ? "Waiting for Civ to close: games upload after it is closed." : null;

    JsonObject UploadSchedule(string context)
    {
        var d = delayedUploads.Get(context);
        return J.Obj(("dueAt", d.DueAt is { } due ? J.Ms(due) : null), ("held", d.State is "recording" or "local"),
            ("ended", d.EndedAt is not null), ("warning", d.WarningAt is not null), ("state", d.State),
            ("enabled", Setting("stream")), ("endPrompted", d.EndPrompted), ("hidden", d.Hold == "hidden"));
    }

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
                : "Saves are sent once Civ is closed. The replay then stays hidden for 2 hours. To send while you play, close Civ and choose While playing.";
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
}
