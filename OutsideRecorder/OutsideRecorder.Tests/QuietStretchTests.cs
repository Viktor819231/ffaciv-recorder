using System.Text.Json;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>
/// The 124-turn live vanilla game (2026-10-27/28): late turns had their lines in the first seconds and then long quiet
/// stretches. From the tape (every look with its time): did the recorder keep looking, and did anything change unseen?
/// </summary>
public sealed class QuietStretchTests(ITestOutputHelper output)
{
    static readonly string Session = TestData.Saves(@"outside-recorder 2026-10-28 vanilla mp live\session-20261027T230147-24700");

    [Fact]
    public void LooksAndChangesInTheQuietStretches()
    {
        var tape = Path.Combine(Session, "tape.bin.gz");
        if (!File.Exists(tape)) { output.WriteLine("tape not on this PC: skipped"); return; }

        // 1. the looks: their times (the tape's own clock), the gaps between them
        var times = new List<long>(); var readMs = new List<double>(); TapeHeader? first = null; int headers = 0;
        foreach (var (h, s) in TapeReader.Read(tape))
        {
            if (h is not null) { first ??= h; headers++; continue; }
            times.Add(s!.TimeUs / 1000); readMs.Add(s.ReadMs);
        }
        var slow = Enumerable.Range(0, times.Count - 1).Where(i => times[i + 1] - times[i] > 1000).Select(i => readMs[i]).Order().ToList();
        output.WriteLine($"the look before each > 1 s gap took: median {(slow.Count > 0 ? slow[slow.Count / 2] : 0):F0} ms, max {(slow.Count > 0 ? slow[^1] : 0):F0} ms; all looks: median {readMs.Order().ElementAt(readMs.Count / 2):F2} ms, p95 {readMs.Order().ElementAt((int)(readMs.Count * 0.95)):F2} ms, p99 {readMs.Order().ElementAt((int)(readMs.Count * 0.99)):F2} ms");
        output.WriteLine($"tape: {headers} part(s), reads mode {first!.Reads}, sample_ms {first.SampleMs}, {times.Count} looks over {(times[^1] - times[0]) / 1000.0 / 60:F0} min");
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        output.WriteLine($"gaps between looks: median {gaps.Order().ElementAt(gaps.Count / 2)} ms, >1 s: {gaps.Count(g => g > 1000)}, >5 s: {gaps.Count(g => g > 5000)}, longest {gaps.Max() / 1000.0:F1} s");

        var autosaveMs = File.ReadLines(Path.Combine(Session, "events.jsonl")).Where(l => l.Contains("\"kind\":\"autosave\"") || l.Contains("\"game_turn_changed\""))
            .Select(l => (Ms: JsonDocument.Parse(l).RootElement.GetProperty("timeMs").GetInt64(), Save: l.Contains("\"kind\":\"autosave\""))).ToList();
        var big = times.Zip(times.Skip(1), (a, b) => (At: a, Gap: b - a)).Where(g => g.Gap > 1000).ToList();
        int nearSave = big.Count(g => autosaveMs.Any(x => x.Save && Math.Abs(x.Ms - g.At) < 3000));
        int nearTurn = big.Count(g => autosaveMs.Any(x => !x.Save && Math.Abs(x.Ms - g.At) < 3000));
        output.WriteLine($"looks > 1 s apart: {big.Count}; within 3 s of an autosave line: {nearSave}; of a turn change: {nearTurn}");

        // 2. the recorded lines: their times; the quiet stretches (> 5 s without a line) and how many looks fell in them
        var lines = File.ReadLines(Path.Combine(Session, "events.jsonl")).Select(l => JsonDocument.Parse(l).RootElement)
            .Select(e => (Ms: e.GetProperty("timeMs").GetInt64(), Kind: e.GetProperty("kind").GetString()!, Turn: e.TryGetProperty("turn", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : -1)).ToList();
        var quiet = lines.Zip(lines.Skip(1), (a, b) => (From: a.Ms, To: b.Ms, Turn: a.Turn)).Where(q => q.To - q.From > 5000).ToList();
        int looksIn = 0; long quietMs = 0;
        foreach (var q in quiet) { looksIn += times.Count(t => t > q.From && t < q.To); quietMs += q.To - q.From; }
        output.WriteLine($"quiet stretches > 5 s in events.jsonl: {quiet.Count}, {quietMs / 1000 / 60} min in all; looks inside them: {looksIn} ({looksIn / Math.Max(1, quietMs / 1000.0):F1} a second)");

        // 3. replay the tape with this version: every change it sees, by time; any inside a quiet stretch of the live file?
        var dir = Path.Combine(Path.GetTempPath(), "outside-recorder-quiet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(tape, Path.Combine(dir, "tape.bin.gz"));
            var (samples, n, outFile) = TapeReplay.Run(dir);
            var replay = File.ReadLines(outFile).Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("kind").GetString() == "result")
                .Select(e => (Ms: e.GetProperty("timeMs").GetInt64(), Method: e.GetProperty("data").GetProperty("method").GetString()!)).ToList();
            var unseen = replay.Where(r => r.Method is "UnitSetXY" or "UnitCreated" or "UnitRemoved" or "unit_damage" or "SetPopulation" or "PlayerCityFounded" or "CityCaptured"
                && quiet.Any(q => r.Ms > q.From + 50 && r.Ms < q.To - 50)).ToList();
            output.WriteLine($"replay: {samples} looks, {n} lines; unit/city changes inside the live file's quiet stretches: {unseen.Count}");
            foreach (var g in unseen.GroupBy(u => u.Method)) output.WriteLine($"  {g.Key}: {g.Count()}");
            var all = File.ReadLines(outFile).Select(l => JsonDocument.Parse(l).RootElement).Where(e => e.GetProperty("kind").GetString() == "result").ToList();
            var live = File.ReadLines(Path.Combine(Session, "events.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).Where(e => e.GetProperty("kind").GetString() == "result").ToList();
            foreach (var u in unseen)
            {
                var e = all.First(x => x.GetProperty("timeMs").GetInt64() == u.Ms && x.GetProperty("data").GetProperty("method").GetString() == u.Method);
                var args = e.GetProperty("data").GetProperty("args");
                var near = live.Where(x => x.GetProperty("data").GetProperty("method").GetString() == u.Method && Math.Abs(x.GetProperty("timeMs").GetInt64() - u.Ms) < 600_000
                    && x.GetProperty("data").GetProperty("args").GetProperty("cityId").GetInt32() == args.GetProperty("cityId").GetInt32()
                    && x.GetProperty("data").GetProperty("args").GetProperty("owner").GetInt32() == args.GetProperty("owner").GetInt32())
                    .Select(x => $"{(x.GetProperty("timeMs").GetInt64() - u.Ms) / 1000.0:+0.0;-0.0} s pop {x.GetProperty("data").GetProperty("args").GetProperty("population")}").ToList();
                var q = quiet.First(q => u.Ms > q.From && u.Ms < q.To);
                output.WriteLine($"  replay {u.Method} at {u.Ms / 1000.0:F1} s turn {e.GetProperty("turn")} {args}; quiet {q.From / 1000.0:F1}-{q.To / 1000.0:F1} s; live lines for that city: {string.Join(", ", near)}");
            }
            // turn 122 in detail
            var t122 = lines.Where(l => l.Turn == 122).ToList();
            if (t122.Count > 0)
            {
                long a = t122.Min(l => l.Ms), b = lines.Where(l => l.Turn == 123).Select(l => l.Ms).DefaultIfEmpty(t122.Max(l => l.Ms)).Min();
                output.WriteLine($"turn 122: {(b - a) / 1000.0:F0} s, {t122.Count} lines, {times.Count(t => t >= a && t < b)} looks ({times.Count(t => t >= a && t < b) / Math.Max(1, (b - a) / 1000.0):F1} a second), replayed unit/city changes {replay.Count(r => r.Ms >= a && r.Ms < b && r.Method != "player_turn_active")}");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
