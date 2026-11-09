#if !RECORDER_PUBLIC_RELEASE
using System.Diagnostics;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

/// <summary>
/// Developer builds only (never in the installer): the two command-line tools of the old FFACivOutsideRecorder.exe window,
/// which was removed on 2026-11-09 (nothing built, shipped or tested it any more).
///   --check                        one look at Civ, printed (no window, no recording)
///   --replay-tape &lt;session folder&gt;  re-reads a recorded tape with this version, writes events.replay.jsonl next to it
/// </summary>
static class DevTools
{
    public static int Run(string[] args)
    {
        int rt = Array.IndexOf(args, "--replay-tape");
        if (rt >= 0)
        {
            if (rt + 1 >= args.Length) { Console.WriteLine("usage: --replay-tape <session folder>"); return 2; }
            try
            {
                var (samples, lines, output) = TapeReplay.Run(args[rt + 1]);
                Console.WriteLine($"replayed {samples} samples -> {lines} lines in {output}");
                return 0;
            }
            catch (Exception e) { Console.WriteLine("replay failed: " + e.Message); return 2; }
        }
        return Check();
    }

    static int Check()
    {
        var civs = GameFinder.CivProcesses().ToList();
        Console.WriteLine($"Civ processes: {civs.Count}");
        foreach (var p in civs)
        {
            using var process = p;
            var f = GameFinder.Inspect(p);
            Console.WriteLine($"pid {f.ProcessId} {f.ExeName}: {f.Status}");
            if (f.GameCore is { } core) Console.WriteLine($"  core {core.Path} base=0x{core.Base:X8} size=0x{core.Size:X} sha256={f.GameCoreSha256}");
            Console.WriteLine($"  in-game FFACiv recorder loaded: {f.InGameRecorderLoaded}");
            if (f.Layout is null) continue;
            using var mem = new ProcessMemory(p.Id);
            var r = new StateReader(mem, f.GameCore!.Base, f.Layout);
            Console.WriteLine($"  globals check: {(r.CheckGlobals(out var why) ? "ok" : why)}");
            var sw = Stopwatch.StartNew();
            var (st, s, w) = r.Read(0);
            Console.WriteLine($"  sample: {st} {w} in {sw.Elapsed.TotalMilliseconds:F1} ms");
            if (s is null) continue;
            Console.WriteLine($"  turn {s.Turn}, you are player {s.ActivePlayer}, game type {s.GameType}, map {s.MapWidth}x{s.MapHeight}, rejected reads {s.Rejected}");
            // Only your own units and cities are listed (never other players' numbers or positions: no help for cheating).
            Console.WriteLine($"  players in game: {string.Join(", ", s.Players.Where(x => x.Alive).Select(x => $"{x.Id}{(x.Civ is { } c ? " " + c : $" civ#{x.CivType}")}{(x.Slot == 3 ? " (human)" : "")}"))}");
            var mine = s.Units.Where(u => u.Owner == s.ActivePlayer).ToList();
            var myCities = s.Cities.Where(c => c.Owner == s.ActivePlayer).ToList();
            Console.WriteLine($"  your units {mine.Count}, your cities {myCities.Count}");
            foreach (var u in mine)
                Console.WriteLine($"    unit {u.Id} {r.UnitTypeKey(u.Type) ?? "type#" + u.Type} at {u.X},{u.Y} dmg={u.Damage} moves={u.Moves / 60.0:F1} fort={u.FortifyTurns} embarked={u.Embarked}");
            foreach (var c in myCities)
                Console.WriteLine($"    city {c.Id} '{c.Name}' at {c.X},{c.Y} pop={c.Population} dmg={c.Damage}");
        }
        return 0;
    }
}
#endif
