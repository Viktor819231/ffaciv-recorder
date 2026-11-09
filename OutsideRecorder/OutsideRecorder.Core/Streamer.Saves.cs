using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OutsideRecorderCore;

/// <summary>The saves sender: each recording's save copies, oldest first, whole or as a delta; refusals; clean-up.</summary>
public sealed partial class Streamer
{
    /// <summary>The saves of every recording not sent yet, oldest first (each as a delta of the one before when that is smaller).</summary>
    public async Task SavesPassAsync(CancellationToken ct)
    {
        if (!MayUpload) return;
        using var work = await EnterHeavyWorkAsync(ct);
        if (!MayUpload) return;
        Diagnostics?.Activity("Processing queued saves");
        await EnsureRegisteredAsync(ct);
        await FlushReplayDeletionsAsync(ct);
        int queued = 0;
        foreach (var game in new GameIndex(settings.OutputFolder).Load())
        {
            if (!MayUpload || !CanContactServer()) break;
            if (HeldBack(game) || !CanUploadGame(game) || DeletionPending(game)) continue;
            try { queued += await SaveGameAsync(game, ct); }
            catch (Exception e) when (OneGameOnly(e)) { HoldBack(game, e); }
        }
        queuedSaves = queued;
    }

    /// <summary>One recording's part of a saves pass; returns how many of its saves still wait.</summary>
    async Task<int> SaveGameAsync(GameEntry game, CancellationToken ct)
    {
        if (settings.SafeCapture && game.Identity.StartsWith("files:") && ReadReplay(game.Context)["state"]?.GetValue<string>() is not "ready" and not "processing"
            && ReadReplay(game.Context,true)["action"] is null)
            ChooseReplay(game.Context,new System.Text.Json.Nodes.JsonObject { ["action"]="complete",["keepShort"]=true,["waitForEnd"]=true });
        var state = Directory.Exists(game.Folder) ? await SendSavesAsync(game, Load(game.Folder), ct, SavePassLimit?.Invoke() ?? settings.SaveBatchLimit) : Load(game.Folder);
        await RefreshReplayAsync(game, state, ct);
        // the cheap test first: most saves of an older game are on the site already
        int waiting = SavesOf(game.Folder).Count(f => !state.SentSaves.ContainsKey(Path.GetFileName(f)) && CanUploadSave(game, f));
        if (!settings.KeepCopies && MayUploadGame(game)) CleanUp(game, state);
        return waiting;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> heldBack = new();

    /// <summary>How long a recording rests after the site refused it for good (a 4xx about that recording) or its own files
    /// could not be read: other recordings go on meanwhile. Before, one refusal failed every pass at the same game, so no
    /// later game was ever sent.</summary>
    public static readonly TimeSpan RefusedRest = TimeSpan.FromMinutes(30), UnreadableRest = TimeSpan.FromMinutes(1);

    bool HeldBack(GameEntry game) => heldBack.TryGetValue(game.Context, out var until) && Environment.TickCount64 < until;

    /// <summary>Errors that belong to one recording; a lost connection, a timeout or a busy PC still end the whole pass.</summary>
    static bool OneGameOnly(Exception e) => !ResourceDeferred(e) && e is SiteRefusedException or IOException or JsonException;

    void HoldBack(GameEntry game, Exception e)
    {
        var rest = e is SiteRefusedException ? RefusedRest : UnreadableRest;
        heldBack[game.Context] = Environment.TickCount64 + (long)rest.TotalMilliseconds;
        Diagnostics?.Error("upload of one recording", e);
        Log($"{Path.GetFileName(game.Folder)}: {e.Message} (other recordings go on; this one is tried again in {rest.TotalMinutes:F0} min)");
        Status = Status with { LastError = $"{DateTime.Now:HH:mm:ss} {e.Message}" };
    }

    /// <summary>A recording quiet this long, fully sent and ended, is removed from this PC (keep_copies = off).</summary>
    public static readonly TimeSpan CleanupAfter = TimeSpan.FromMinutes(60);

    /// <summary>
    /// keep_copies = off: a save copy goes once the server confirmed it (the last one sent stays: the next delta is made
    /// against it); a whole recording folder goes once everything in it was confirmed, it ended (its last line says so)
    /// and nothing was written to it for <see cref="CleanupAfter"/>. Channels (the lobby) are kept.
    /// </summary>
    void CleanUp(GameEntry game, StreamState state)
    {
        int n = 0;
        foreach (var f in SavesOf(game.Folder))
        {
            var name = Path.GetFileName(f);
            if (state.SentSaves.ContainsKey(name) && name != state.BaseFile) { try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        var events = Path.Combine(game.Folder, "events.jsonl");
        if (!game.Identity.StartsWith("channel:") && File.Exists(events) && Load(game.Folder).SentOffset >= new FileInfo(events).Length
            && SavesOf(game.Folder).All(f => state.SentSaves.ContainsKey(Path.GetFileName(f)))
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(events) > CleanupAfter && Ended(events))
        {
            try { if (LocalRecordingDeletion.Delete(settings.OutputFolder, game.Folder)) n++; Log($"cleanup: {Path.GetFileName(game.Folder)} sent and removed from this PC"); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (n > 0) Status = Status with { CleanedFiles = Status.CleanedFiles + n };
    }

    /// <summary>The save copies of a recording, oldest first. Both senders and the window ask several times a second: the
    /// folder listing already carries each file's time, so there is no second look per save (that was one Windows call per
    /// save copy, for every recording, on every ask).</summary>
    public static List<string> SavesOf(string folder)
    {
        var dir = Path.Combine(folder, "saves");
        var info = new DirectoryInfo(dir);
        if (!info.Exists) return [];
        return info.EnumerateFiles("*.Civ5Save").Select(f => (Path: Path.Combine(dir, f.Name), Time: f.LastWriteTimeUtc))
            .OrderBy(f => f.Time).ThenBy(f => f.Path).Select(f => f.Path).ToList();
    }

    readonly Dictionary<string, int> deltaTries = [];

    async Task<StreamState> SendSavesAsync(GameEntry game, StreamState state, CancellationToken ct, int max = int.MaxValue)
    {
        if (!MayUploadGame(game)) return state;
        int sentNow = 0;
        foreach (var file in SavesOf(game.Folder))
        {
            var name = Path.GetFileName(file);
            // already on the site: nothing to do, so no gate to ask (each asking reads this game's upload choice from disk)
            if (state.SentSaves.ContainsKey(name)) continue;
            if (!MayUploadGame(game)) break;
            if (sentNow >= Math.Min(max, SavePassLimit?.Invoke() ?? int.MaxValue)) break;
            if (!CanUploadSave(game, file)) continue;
            // copies are written as .part and renamed (SafeFiles.CopySave), so a listed save is whole; one another program
            // holds right now waits for the next pass instead of failing the pass (and the events behind it)
            byte[] bytes;
            try { bytes = SafeFiles.ReadAllBytes(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = Status with { LastError = $"{DateTime.Now:HH:mm:ss} {name}: {e.Message} (next pass)" }; break; }
            string? id = null; bool asDelta = false; int wireBytes = 0;
            var baseFile = state.BaseFile is null ? null : Path.Combine(game.Folder, "saves", state.BaseFile);
            // a delta the server failed on twice (no answer, an error) goes whole: one bad delta never holds the saves up
            bool full = ForceFullSave(game, file) || settings.SaveUpload != "delta" || state.BaseId is null || baseFile is null || !File.Exists(baseFile)
                || state.SinceFull + 1 >= settings.FullSaveEvery || deltaTries.GetValueOrDefault(name) >= 2;
            if (!full)
            {
                if (!MayUploadGame(game)) return state;
                var sw = Stopwatch.StartNew();
                // the base was the last save sent: its unpacked parts are usually still here
                var baseParts = lastParts is { } lp && lp.File == state.BaseFile ? lp.Parts : SaveParts.Split(SafeFiles.ReadAllBytes(baseFile!));
                var parts = SaveParts.Split(bytes);
                if (parts is not null) lastParts = (name, parts);
                byte[]? delta = baseParts is null || parts is null ? null : SaveDelta.Make(baseParts, parts, bytes);
                Status = Status with { DeltaCpuMs = Status.DeltaCpuMs + sw.Elapsed.TotalMilliseconds };
                if (delta is not null && delta.Length < bytes.Length * 0.8)
                {
                    deltaTries[name] = deltaTries.GetValueOrDefault(name) + 1;
                    var (ok, answerId, needFull) = await PostSaveAsync(game, name, file, delta, state.BaseId, ct);
                    if (ok) { id = answerId; asDelta = true; wireBytes = delta.Length; }
                    else if (!needFull) return state;
                }
            }
            if (id is null)
            {
                var (ok, answerId, _) = await PostSaveAsync(game, name, file, bytes, null, ct);
                if (!ok) return state;
                id = answerId; wireBytes = bytes.Length;
            }
            sentNow++;
            var sent = new Dictionary<string, string>(state.SentSaves) { [name] = id! };
            state = state with { SentSaves = sent, BaseFile = name, BaseId = id, SinceFull = asDelta ? state.SinceFull + 1 : 0 };
            SaveSaves(game.Folder, state);
            Status = Status with
            {
                SaveWireBytes = Status.SaveWireBytes + wireBytes, LastSent = DateTimeOffset.Now,
                FullSaves = Status.FullSaves + (asDelta ? 0 : 1), DeltaSaves = Status.DeltaSaves + (asDelta ? 1 : 0),
            };
        }
        return state;
    }

    async Task<(bool Ok, string? Id, bool NeedFull)> PostSaveAsync(GameEntry game, string name, string file, byte[] body, string? deltaBase, CancellationToken ct)
    {
        if (!MayUploadGame(game) || !CanUploadSave(game, file)) return (false, null, false);
        using var r = await SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "saves") { Content = new ByteArrayContent(body) };
            m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            m.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString(OriginalName(name)));
            m.Headers.TryAddWithoutValidation("X-Folder", "outside-recorder");
            m.Headers.TryAddWithoutValidation("X-File-Time", new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds().ToString());
            m.Headers.TryAddWithoutValidation("X-Recording-Id", game.Context);
            if (!game.Identity.StartsWith("files:", StringComparison.Ordinal)) m.Headers.TryAddWithoutValidation("X-Game-Identity", game.Identity);
            if (!game.Identity.StartsWith("channel:")) m.Headers.TryAddWithoutValidation("X-Recorder-Lifecycle", "1");
            if (deltaBase is not null)
            {
                m.Headers.TryAddWithoutValidation("X-Save-Encoding", "orsd1");
                m.Headers.TryAddWithoutValidation("X-Delta-Base", deltaBase);
            }
            return m;
        }, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (r.StatusCode == HttpStatusCode.Conflict && deltaBase is not null && text.Contains("needFull")) return (false, null, true);
        if (!r.IsSuccessStatusCode)
        {
            refusals[Path.GetFullPath(file)] = new SaveRefusal(DateTimeOffset.UtcNow, (int)r.StatusCode, SiteWords(text));
            throw RefusalOrError(r.StatusCode, $"save {name}: {(int)r.StatusCode} {SiteWords(text)}");
        }
        refusals.TryRemove(Path.GetFullPath(file), out _);
        using var doc = JsonDocument.Parse(text);
        return (true, doc.RootElement.GetProperty("id").GetString(), false);
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, SaveRefusal> refusals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the site last refused this save copy (null: never, or it took it since). Kept while the recorder runs.</summary>
    public SaveRefusal? RefusalOf(string file) => refusals.TryGetValue(Path.GetFullPath(file), out var r) ? r : null;

    /// <summary>"AutoSave_0013 BC-3220 (114502).Civ5Save" (a second copy of one name) -> "AutoSave_0013 BC-3220.Civ5Save".</summary>
    public static string OriginalName(string name) => Regex.Replace(name, @" \(\d{6}\)(?=\.Civ5Save$)", "", RegexOptions.IgnoreCase);
}
