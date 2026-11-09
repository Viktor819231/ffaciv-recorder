using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OutsideRecorderCore;

/// <summary>The events sender: each recording's events.jsonl in gzip chunks at their byte offsets.</summary>
public sealed partial class Streamer
{
    /// <summary>The events of every recording (what is there now), chunk by chunk.</summary>
    public async Task EventsPassAsync(CancellationToken ct)
    {
        if (!MayUpload) return;
        using var work = await EnterHeavyWorkAsync(ct);
        if (!MayUpload) return;
        Diagnostics?.Activity("Uploading recorder events");
        await EnsureRegisteredAsync(ct);
        long queued = 0;
        foreach (var game in new GameIndex(settings.OutputFolder).Load())
        {
            if (!MayUpload || !CanContactServer()) break;
            if (HeldBack(game) || !CanUploadGame(game) || !Directory.Exists(game.Folder) || DeletionPending(game)) continue;
            try
            {
                var state = await SendEventsAsync(game, Load(game.Folder), ct);
                var file = Path.Combine(game.Folder, "events.jsonl");
                queued += File.Exists(file) ? Math.Max(0, new FileInfo(file).Length - state.SentOffset) : 0;
            }
            catch (Exception e) when (OneGameOnly(e)) { HoldBack(game, e); }
        }
        queuedEventBytes = queued;
    }

    async Task<StreamState> SendEventsAsync(GameEntry game, StreamState state, CancellationToken ct)
    {
        if (!MayUploadGame(game)) return state;
        var path = Path.Combine(game.Folder, "events.jsonl");
        if (!File.Exists(path)) return state;
        // only what is there now: a game writing faster than we send (a fast replay, a busy turn) must not keep this
        // loop chasing the end of the file while the saves behind it wait
        int sentChunks = 0;
        long until = new FileInfo(path).Length;
        while (!ct.IsCancellationRequested)
        {
            if (!MayUploadGame(game)) return state;
            byte[] chunk;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (Math.Min(f.Length, until) <= state.SentOffset && state.PendingLength is null) return state;
                if (f.Length <= state.SentOffset) return state;
                f.Seek(state.SentOffset, SeekOrigin.Begin);
                if (state.PendingLength is int pending)
                {
                    // the chunk that was on its way when the program stopped: the very same bytes again ("duplicate" if it arrived)
                    chunk = new byte[pending];
                    f.ReadExactly(chunk);
                }
                else
                {
                    var buf = new byte[(int)Math.Min(ChunkBytes, Math.Min(f.Length, until) - state.SentOffset)];
                    f.ReadExactly(buf);
                    int end = Array.LastIndexOf(buf, (byte)'\n') + 1; // whole lines only
                    if (end == 0)
                    {
                        if (buf.Length == ChunkBytes) throw new InvalidDataException($"a line longer than {ChunkBytes} bytes in {path}");
                        return state; // the writer is in the middle of a line
                    }
                    chunk = buf[..end];
                    // written before sending: never a longer chunk at an offset the server may already hold
                    state = state with { PendingLength = chunk.Length };
                    SaveEvents(game.Folder, state);
                }
            }
            if (!MayUploadGame(game)) return state;
            var sw = Stopwatch.StartNew();
            var hash = Convert.ToHexStringLower(SHA256.HashData(chunk));
            using var gz = new MemoryStream();
            if (!MayUploadGame(game)) return state;
            using (var z = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true)) z.Write(chunk);
            var wire = gz.ToArray();
            if (!MayUploadGame(game)) return state;
            var cpu = sw.Elapsed.TotalMilliseconds; // one thread, busy the whole time: wall time = CPU time of packing
            long offset = state.SentOffset;
            using var r = await SendAsync(() =>
            {
                var m = new HttpRequestMessage(HttpMethod.Post, $"recordings/{game.Context}/chunks/{offset}") { Content = new ByteArrayContent(wire) };
                m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                m.Headers.TryAddWithoutValidation("X-Recording-Encoding", "gzip");
                m.Headers.TryAddWithoutValidation("X-Content-SHA256", hash);
                m.Headers.TryAddWithoutValidation("X-Game-Identity", game.Identity);
                if (!game.Identity.StartsWith("channel:")) m.Headers.TryAddWithoutValidation("X-Recorder-Lifecycle", "1");
                return m;
            }, ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode) throw RefusalOrError(r.StatusCode, $"chunk {offset}: {(int)r.StatusCode} {SiteWords(body)}");
            state = state with { SentOffset = offset + chunk.Length, PendingLength = null };
            SaveEvents(game.Folder, state);
            Status = Status with
            {
                EventWireBytes = Status.EventWireBytes + wire.Length, Chunks = Status.Chunks + 1, LastSent = DateTimeOffset.Now,
                ChunkCpuMs = Status.ChunkCpuMs + Math.Max(cpu, 0), LastError = Status.LastError,
            };
            if (++sentChunks >= (EventPassLimit?.Invoke() ?? settings.EventChunkLimit)) return state;
        }
        return state;
    }
}
