using System.Diagnostics;
using System.Text.Json;
namespace OutsideRecorderCore;

/// <summary>
/// Testing the stream without Civ, from a recorded game:
///   --stream-test &lt;recording folder&gt; &lt;saves folder&gt; &lt;output folder&gt; [--url U] [--speed 100] [--exit-after S]
///       replays the tapes (all session-* folders, in time order) as if Civ were played now, speed× faster, into one
///       recording folder, copies each autosave when its turn starts, and streams it all (events in chunks, saves as deltas).
///       --exit-after S: the process ends abruptly after S seconds (a recorder crash / restart test).
///   --stream-only &lt;output folder&gt; [--url U]: only sends what is still queued, until nothing is left (the restart).
///   --stream-verify &lt;output folder&gt; [--url U]: compares what the local server stored with the local files.
///   --site-check &lt;output folder&gt; --url U [--game G]: against a real site (OR05): the live-slot route (take, renew,
///       request, release) and Block / Unblock of the streamed game.
/// Common: --stream site|local (default local), --keep-copies on|off (default on). Lives in the core so both programs
/// (FFACivRecorder.exe and the check tool) run it.
/// </summary>
public static class StreamTest
{
    static string Arg(string[] args, string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }

    static RecorderSettings Settings(string output, string[] args) => new()
    {
        OutputFolder = output, Stream = Arg(args, "--stream", "local"), StreamUrl = Arg(args, "--url", ""),
        KeepCopies = Arg(args, "--keep-copies", "on") != "off", StreamIntervalS = int.Parse(Arg(args, "--interval", "3")),
        SaveUpload = Arg(args, "--save-upload", "delta"), FullSaveEvery = int.Parse(Arg(args, "--full-every", "10")), SaveFolders = [],
    };

    public static int Run(string[] args)
    {
        int i = Array.IndexOf(args, "--stream-test");
        string recording = args[i + 1], saves = args[i + 2], output = args[i + 3];
        double speed = double.Parse(Arg(args, "--speed", "100"), System.Globalization.CultureInfo.InvariantCulture);
        int exitAfter = int.Parse(Arg(args, "--exit-after", "0"));
        var settings = Settings(output, args);
        Directory.CreateDirectory(output);
        using var streamer = new Streamer(settings);
        streamer.Start();
        var wall = Stopwatch.StartNew();
        if (exitAfter > 0) new Thread(() => { Thread.Sleep(exitAfter * 1000); Console.WriteLine($"exit-after {exitAfter}s: ending abruptly ({Line(streamer.Status)})"); Console.Out.Flush(); Environment.Exit(3); }) { IsBackground = true }.Start();

        var byTurn = Directory.GetFiles(saves, "AutoSave_*.Civ5Save").Select(f => (Turn: RecorderSession.AutosaveTurn(f), File: f))
            .Where(x => x.Turn is not null).GroupBy(x => x.Turn!.Value).ToDictionary(g => g.Key, g => g.First().File);
        var index = new GameIndex(output);
        JsonlRecording? rec = null; GamePipeline? pipe = null; GameEntry? entry = null;
        long now = 0, first = -1, samples = 0; int lastTurn = -1;
        // --resume: the recorder was restarted in the middle of the game: go on after the last line already written
        long skipUntil = -1;
        if (args.Contains("--resume") && index.Load().LastOrDefault() is { } last && File.Exists(Path.Combine(last.Folder, "events.jsonl")))
        {
            var tail = File.ReadLines(Path.Combine(last.Folder, "events.jsonl")).LastOrDefault(l => l.TrimEnd().EndsWith('}'));
            if (tail is not null) skipUntil = JsonDocument.Parse(tail).RootElement.GetProperty("timeUs").GetInt64();
            Console.WriteLine($"resuming after timeUs {skipUntil}");
        }
        foreach (var session in Directory.GetDirectories(recording, "session-*").Order())
        {
            var mem = new ReplayMemory(); StateReader? reader = null; TapeHeader? header = null; GameCoreLayout? layout = null;
            foreach (var (h, s) in TapeReader.Read(Path.Combine(session, "tape.bin.gz")))
            {
                if (h is not null) { mem.Clear(); header = h; layout = KnownLayouts.All.Single(l => l.Id == h.Layout); reader = new StateReader(mem, h.ModuleBase, layout) { BlockReads = h.Reads >= 2 }; continue; }
                mem.Apply(s!);
                if (s!.TimeUs <= skipUntil) continue;
                if (first < 0) first = s!.TimeUs;
                now = s!.TimeUs;
                var due = TimeSpan.FromMilliseconds((now - first) / 1000.0 / speed);
                if (due > wall.Elapsed) Thread.Sleep(due - wall.Elapsed);
                var (st, snap, _) = reader!.Read(now / 1000);
                if (st == SampleStatus.Ok && snap is not null && rec is null && GamePipeline.IsLoaded(snap))
                {
                    var id = GamePipeline.IdentityOf(snap);
                    var known = index.Find(id, DateTimeOffset.UtcNow);
                    var folder = known?.Folder ?? Path.Combine(output, $"session-streamtest-{DateTime.Now:yyyyMMddTHHmmss}");
                    entry = new GameEntry { Identity = id, Folder = folder, Context = known?.Context ?? GameIndex.NewContext(), ProcessId = 0, LastSeen = DateTimeOffset.UtcNow };
                    index.Save(entry);
                    rec = new JsonlRecording(folder, "events.jsonl", () => now);
                    pipe = new GamePipeline(rec, reader.UnitTypeKey, true, known is null ? -1 : JsonlRecording.LastTurn(Path.Combine(folder, "events.jsonl")) ?? -1);
                    // the same first line the real recorder writes (RecorderSession.BeginGame): the converter needs the identity
                    rec.Write("recording_profile", snap.Turn, new Dictionary<string, object?>
                    {
                        ["recorder"] = "FFACiv Outside Recorder", ["method"] = "read-only memory sampling (stream test replay)", ["sampleMs"] = header!.SampleMs,
                        ["layout"] = layout!.Id, ["mod"] = layout.Name, ["coreSha256"] = header.CoreSha256,
                        ["autosaveTiming"] = layout.AutosaveAtTurnStart ? "start_of_turn" : "end_of_turn",
                        ["gameType"] = snap.GameType, ["activePlayer"] = snap.ActivePlayer, ["mapWidth"] = snap.MapWidth, ["mapHeight"] = snap.MapHeight,
                        ["identity"] = id, ["context"] = entry.Context, ["mapSeed"] = snap.MapSeed, ["syncSeed"] = snap.SyncSeed, ["tape"] = false,
                        ["processId"] = 0, ["recorderStarted"] = DateTimeOffset.Now.ToString("o"), ["streamTest"] = true,
                    });
                    rec.Write("lifecycle", snap.Turn, new Dictionary<string, object?> { ["state"] = known is null ? "recording" : "game_rejoined", ["reason"] = "stream test" });
                }
                pipe?.Step(st, snap, now / 1000);
                if (snap is not null && snap.Turn != lastTurn && rec is not null)
                {
                    lastTurn = snap.Turn;
                    if (byTurn.TryGetValue(snap.Turn, out var file))
                    {
                        // the same copy as the real recorder: whole (.part + rename), never over another copy
                        var copy = SafeFiles.CopySave(file, Path.Combine(rec.Folder, "saves"), DateTime.UtcNow);
                        rec.Write("autosave", snap.Turn, new Dictionary<string, object?> { ["fileTurn"] = snap.Turn, ["copy"] = copy is null ? null : Path.GetRelativePath(rec.Folder, copy),
                            ["timing"] = layout!.AutosaveAtTurnStart ? "start_of_turn" : "end_of_turn" });
                    }
                }
                rec?.Flush();
                samples++;
                if (samples % 5000 == 0) Console.WriteLine($"{wall.Elapsed.TotalSeconds:F0}s turn {lastTurn}: {Line(streamer.Status)}");
            }
        }
        rec?.Write("lifecycle", lastTurn, new Dictionary<string, object?> { ["state"] = "civ_closed", ["reason"] = "stream test: end of recording", ["exitCode"] = "0x00000000" });
        rec?.Dispose();
        Console.WriteLine($"recording done in {wall.Elapsed.TotalSeconds:F0}s ({samples} samples); waiting for the stream to empty");
        return WaitEmpty(streamer, 1800);
    }

    static int WaitEmpty(Streamer streamer, int seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(1000);
            var st = streamer.Status;
            if (st.State == "connected" && st.QueuedEventBytes == 0 && st.QueuedSaves == 0) { Console.WriteLine("stream empty: " + Line(st)); return 0; }
        }
        Console.WriteLine("stream NOT empty: " + Line(streamer.Status));
        return 4;
    }

    public static string Line(StreamStatus s) =>
        $"{s.State}; queued {s.QueuedEventBytes / 1024} KB events, {s.QueuedSaves} saves; sent {s.Chunks} chunks {s.EventWireBytes / 1024} KB, " +
        $"saves {s.FullSaves} full + {s.DeltaSaves} delta {s.SaveWireBytes / 1024} KB; requests {s.Requests}; chunk packing {s.ChunkCpuMs:F0} ms, deltas {s.DeltaCpuMs:F0} ms" +
        (s.LastError is { } e ? $"; last error: {e}" : "");

    public static int StreamOnly(string[] args)
    {
        var output = args[Array.IndexOf(args, "--stream-only") + 1];
        using var streamer = new Streamer(Settings(output, args));
        streamer.Start();
        return WaitEmpty(streamer, int.Parse(Arg(args, "--wait", "600")));
    }

    public static int Verify(string[] args)
    {
        if (Arg(args, "--stream", "local") == "site") { Console.WriteLine("--stream-verify reads the local test server's /dev/state; use --site-check for a site"); return 2; }
        var output = args[Array.IndexOf(args, "--stream-verify") + 1];
        var settings = Settings(output, args);
        var root = new Uri(settings.StreamAddress).GetLeftPart(UriPartial.Authority);
        using var http = new HttpClient();
        var state = JsonDocument.Parse(http.GetStringAsync(root + "/dev/state").Result).RootElement;
        int problems = 0;
        foreach (var game in new GameIndex(output).Load())
        {
            var chunks = state.GetProperty("chunks").EnumerateArray().Where(c => c.GetProperty("context").GetString() == game.Context).ToList();
            long at = 0; bool contiguous = true;
            foreach (var c in chunks) { if (c.GetProperty("offset").GetInt64() != at) contiguous = false; at += c.GetProperty("bytes").GetInt64(); }
            var local = File.ReadAllBytes(Path.Combine(game.Folder, "events.jsonl"));
            var stored = http.GetByteArrayAsync($"{root}/dev/recording/{game.Context}").Result;
            bool same = local.AsSpan().SequenceEqual(stored);
            Console.WriteLine($"events: {chunks.Count} chunks, each offset once and in order: {contiguous}; {stored.Length} of {local.Length} bytes stored, identical: {same}");
            if (!contiguous || !same) problems++;
            var sent = JsonSerializer.Deserialize<StreamState>(File.ReadAllText(Path.Combine(game.Folder, "stream.json")))!;
            int exact = 0, content = 0, bad = 0;
            foreach (var file in Streamer.SavesOf(game.Folder))
            {
                if (!sent.SentSaves.TryGetValue(Path.GetFileName(file), out var id)) { bad++; Console.WriteLine($"  not sent: {Path.GetFileName(file)}"); continue; }
                var original = File.ReadAllBytes(file);
                var server = http.GetByteArrayAsync($"{root}/dev/save/{id}").Result;
                if (original.AsSpan().SequenceEqual(server)) { exact++; continue; }
                var a = SaveParts.Split(original); var b = SaveParts.Split(server);
                if (a is not null && b is not null && a.ContentSha256() == b.ContentSha256()) content++;
                else { bad++; Console.WriteLine($"  DIFFERENT: {Path.GetFileName(file)}"); }
            }
            Console.WriteLine($"saves: {exact} byte-identical (sent whole), {content} same content (rebuilt from a delta), {bad} missing or different");
            if (bad > 0) problems++;
        }
        var counters = state.GetProperty("counters");
        Console.WriteLine($"server counters: {counters}");
        return problems == 0 ? 0 : 5;
    }
}


/// <summary>The site checks of the 0.5.0 harness (OR05): live slots and Block, with this install's own key.</summary>
public static class SiteCheck
{
    static string Arg(string[] args, string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }

    public static int Run(string[] args)
    {
        var output = args[Array.IndexOf(args, "--site-check") + 1];
        var settings = new RecorderSettings { OutputFolder = output, Stream = Arg(args, "--stream", "site"), StreamUrl = Arg(args, "--url", "") };
        var game = Arg(args, "--game", new GameIndex(output).Load().FirstOrDefault(g => !g.Identity.StartsWith("channel:"))?.Identity ?? "1a2b3c4d-5e6f7a8b");
        using var s = new Streamer(settings, version: Arg(args, "--version", "0.5.0"));
        int bad = 0;
        void Check(string what, bool ok, string detail) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}: {detail}"); if (!ok) bad++; }
        // --only take|request|release|block|unblock: one step (for scripted checks with several installs)
        var only = Arg(args, "--only", "");
        if (only.Length > 0)
        {
            try
            {
                if (only is "block" or "unblock")
                {
                    var (c, b) = s.SetVisibilityAsync(game, only == "block", default).GetAwaiter().GetResult();
                    Check(only, c is >= 200 and < 300, $"{c} {b}");
                }
                else
                {
                    var st = s.LiveSlotAsync(game, null, only, default).GetAwaiter().GetResult();
                    Console.WriteLine($"{only}: {st.State} position {st.Position} requested {st.Requested} slots {st.Slots} free {st.Free} watch {st.Watch} again {st.AskAgainSeconds}s");
                }
            }
            catch (Exception e) { Check(only, false, e.Message); }
            return bad == 0 ? 0 : 5;
        }
        try
        {
            var take = s.LiveSlotAsync(game, null, "take", default).GetAwaiter().GetResult();
            Check("slot take", take.State is "granted" or "busy" or "off", $"{take.State} position {take.Position} slots {take.Slots} free {take.Free} watch {take.Watch} again {take.AskAgainSeconds}s");
            var renew = s.LiveSlotAsync(game, null, "take", default).GetAwaiter().GetResult();
            Check("slot renew", renew.State == take.State, renew.State);
            var other = Arg(args, "--other-game", "");
            if (other.Length > 0)
            {
                var req = s.LiveSlotAsync(other, null, "request", default).GetAwaiter().GetResult();
                Check("slot request (other game)", req.State is "busy" or "granted", $"{req.State} requested {req.Requested} position {req.Position}");
            }
            var rel = s.LiveSlotAsync(game, null, "release", default).GetAwaiter().GetResult();
            Check("slot release", rel.State is "released" or "idle", rel.State);
            var (code, body) = s.SetVisibilityAsync(game, true, default).GetAwaiter().GetResult();
            Check("block", code is >= 200 and < 300, $"{code} {body}");
            (code, body) = s.SetVisibilityAsync(game, false, default).GetAwaiter().GetResult();
            Check("unblock", code is >= 200 and < 300, $"{code} {body}");
        }
        catch (Exception e) { Check("site check", false, e.Message); }
        Console.WriteLine($"problem: {s.Status.Problem ?? "none"}; requests {s.Status.Requests}");
        return bad == 0 ? 0 : 5;
    }
}
