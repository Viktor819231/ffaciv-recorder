using System.Diagnostics;

namespace OutsideRecorderCore;

/// <summary>
/// Samples in, recording lines out: the same code runs live and when a tape is replayed (<see cref="TapeReplay"/>),
/// so a replayed tape gives the same events.jsonl as the live run (and a newer version can be checked against it).
/// </summary>
public sealed class GamePipeline
{
    readonly CityMicroDiffer cityMicro = new();
    IReadOnlyList<CityMicroState>? latestCityMicro;
    /// <summary>Off records save metadata only; no between-turn movement or state log is generated.</summary>
    bool captureMoves = true;
    public bool CaptureMoves
    {
        get => captureMoves;
        set
        {
            if (captureMoves == value) return;
            captureMoves = value;
            differ = new Differ { UnitTypeKey = unitKey };
            tiles = null; previous = null; baselineWritten = false; snapshotDueMs = -1;
            cityMicro.Clear(); latestCityMicro = null;
        }
    }
    readonly JsonlRecording rec;
    readonly Func<int, string?>? unitKey;
    readonly bool snapshotEachTurn;
    Differ differ;
    int lastTurn = -1;
    long gapSinceMs = -1, gapStartedMs = -1;
    bool leftWritten, resumedAfterLeft, settling = true, baselineWritten, afterRestart;
    GameSnapshot? candidate;
    int settleTries;
    TileTable? tiles, latestTiles;
    long timerStart;
    long snapshotDueMs = -1;
    GameSnapshot? previous;

    /// <summary>Seconds without a readable game before "game_left" is written.</summary>
    public const int LeftAfterMs = 5000;
    /// <summary>Samples in a row that must agree before a (re)loaded game is taken as the new baseline.</summary>
    public const int SettleSamples = 2;
    /// <summary>After this many samples that never agree, the latest one is taken anyway (a game that keeps changing).</summary>
    public const int SettleGiveUp = 25;

    /// <summary>
    /// Writes into <paramref name="rec"/>. <paramref name="previousTurn"/>: the last turn already in this game's recording
    /// (the same game rejoined after a Civ or recorder restart), so a load of an older turn is marked "game_reloaded" too.
    /// </summary>
    public GamePipeline(JsonlRecording rec, Func<int, string?>? unitKey, bool snapshotEachTurn, int previousTurn = -1)
    {
        this.rec = rec; this.unitKey = unitKey; this.snapshotEachTurn = snapshotEachTurn;
        differ = new Differ { UnitTypeKey = unitKey };
        lastTurn = previousTurn;
        afterRestart = previousTurn >= 0;
    }

    /// <summary>A deliberate capture pause starts a fresh baseline instead of inventing events across the gap.</summary>
    public void ResetAfterCaptureGap(string? why = null)
    {
        rec.Write("lifecycle", lastTurn < 0 ? null : lastTurn,
            new Dictionary<string, object?> { ["state"] = "capture_gap", ["reason"] = "resource budget paused memory capture; event timing is unknown", ["why"] = why });
        differ = new Differ { UnitTypeKey = unitKey };
        cityMicro.Clear(); latestCityMicro = null;
        tiles = null; latestTiles = null; previous = null;
        baselineWritten = false; snapshotDueMs = -1;
        settling = true; candidate = null; settleTries = 0;
        gapSinceMs = -1; gapStartedMs = -1; leftWritten = false; resumedAfterLeft = false;
    }

    /// <summary>Last turn seen.</summary>
    public int LastTurn => lastTurn;

    /// <summary>
    /// A sample of a game that is fully there. While Civ loads a save the map and turn are readable before the players are
    /// built (Snowve T12 reload, 2026-10-27: the first sample had no players, units or cities, so every city came back as
    /// "founded", size 1). A running game always has a living player.
    /// </summary>
    /// (0.4.2: and its units or cities are there — the user's vanilla game had players but 0 units for the first looks of turn 0;
    /// every running game has at least one unit or city.)
    public static bool IsLoaded(GameSnapshot s) => s.Players.Any(p => p.Alive) && (s.Units.Count > 0 || s.Cities.Count > 0);

    static (int, int, int, int, int) Signature(GameSnapshot s) =>
        (s.Turn, s.Players.Count, s.Units.Count, s.Cities.Count, s.Cities.Sum(c => c.Population));

    /// <summary>
    /// Which game this is. The pre-game map and sync random seeds ("1a2b3c4d-5e6f7a8b", the converter's own key for joining
    /// saves of one game): set when the game is created, the same on every player's PC, kept through reloads, rejoins and
    /// rehosts. Never from who is alive (a civ dying split the 2026-10-27 recording into 8 pieces). Without seeds: the map
    /// size and every slot's civilization (all slots, dead or alive) — weaker: two games with the same map size and the same
    /// civs in the same slots would look alike.
    /// </summary>
    public static string IdentityOf(GameSnapshot s) =>
        s.MapSeed is uint m && s.SyncSeed is uint y && (m | y) != 0
            ? $"{m:x8}-{y:x8}"
            : $"map:{s.MapWidth}x{s.MapHeight}|civs:{string.Join(",", s.SlotCivs.Count > 0 ? s.SlotCivs : s.Players.OrderBy(p => p.Id).Select(p => p.CivType ?? -1))}";

    /// <summary>Feeds one sample result.</summary>
    public void Step(SampleStatus status, GameSnapshot? s, long timeMs)
    {
        if (status == SampleStatus.Ok && s?.Tiles is { } seen) latestTiles = seen;   // kept while a (re)load settles
        if (status != SampleStatus.Ok || s is null || !IsLoaded(s))
        {
            latestCityMicro = null;
            candidate = null; settleTries = 0;
            if (gapSinceMs < 0) gapSinceMs = timeMs;
            else if (!leftWritten && timeMs - gapSinceMs > LeftAfterMs)
            {
                rec.Write("lifecycle", lastTurn < 0 ? null : lastTurn, new Dictionary<string, object?> { ["state"] = "game_left", ["reason"] = (s is null ? status : SampleStatus.NoGame).ToString() });
                leftWritten = true;
            }
            return;
        }
        if (s.CityMicro is not null) latestCityMicro = s.CityMicro;
        if (gapSinceMs >= 0)
        {
            // back after menus / loading / a bad read: wait until the game is fully there before using it
            resumedAfterLeft |= leftWritten;
            if (gapStartedMs < 0) gapStartedMs = gapSinceMs;
            gapSinceMs = -1; leftWritten = false;
            settling = true; candidate = null; settleTries = 0;
        }
        if (settling)
        {
            settleTries++;
            if ((candidate is null || Signature(candidate) != Signature(s)) && settleTries < SettleGiveUp) { candidate = s; return; }
            settling = false; candidate = null; settleTries = 0;
        }
        if (!CaptureMoves) { lastTurn = s.Turn; baselineWritten = false; return; }
        bool reset = false, writeSnapshotNow = !baselineWritten;
        if (resumedAfterLeft)
        {
            // back in the same game (rejoin, host migration, reload of the same turn): diff against the last state seen,
            // so what changed while we could not read comes out as events (moves, deaths, new cities)
            rec.Write("lifecycle", s.Turn, new Dictionary<string, object?> { ["state"] = "game_resumed", ["previousTurn"] = lastTurn < 0 ? null : lastTurn, ["gapMs"] = timeMs - gapStartedMs });
            writeSnapshotNow = true;
        }
        resumedAfterLeft = false; gapStartedMs = -1;
        if (lastTurn >= 0 && s.Turn < lastTurn)
        {
            var args = new Dictionary<string, object?> { ["state"] = "game_reloaded", ["fromTurn"] = lastTurn, ["toTurn"] = s.Turn };
            if (afterRestart && !baselineWritten) args["reason"] = "older turn loaded after a restart";
            rec.Write("lifecycle", s.Turn, args);
            reset = true;
        }
        afterRestart = false;
        if (reset) { differ = new Differ { UnitTypeKey = unitKey }; tiles = s.Tiles is null ? latestTiles : null; cityMicro.Clear(); }
        foreach (var e in differ.Next(s))
        {
            rec.Write(e);
            if (e.Method is "CityRemoved" or "CityCaptured" && e.Args["x"] is int x && e.Args["y"] is int y) cityMicro.RemoveAt(x, y);
        }
        foreach (var e in cityMicro.Next(s.CityMicro ?? (writeSnapshotNow ? latestCityMicro : null), s.Turn)) rec.Write(e);
        // the tiles (about once a second): what changed since the last table, one line per tile (OR03 "Tiles")
        if (s.Tiles is { } t)
        {
            if (tiles is not null && tiles.Width == t.Width && tiles.Height == t.Height)
                foreach (var e in TileChanges(tiles, t, s.Turn)) rec.Write(e);
            tiles = t;
        }
        else tiles ??= latestTiles;
        // the multiplayer turn timer when it is on (OR03 "Lobby / channel": turn_timer): each new turn, and when the
        // turn's timer (re)starts: then also when it started (the game's QPC start, the same clock as this PC's)
        if (s.TurnTimer is { Enabled: true } tt && (s.Turn != lastTurn || (tt.StartTicks is long st0 && st0 != 0 && st0 != timerStart)))
        {
            var args = new Dictionary<string, object?> { ["turn"] = s.Turn, ["seconds"] = tt.TurnSeconds, ["type"] = tt.Type, ["length"] = tt.Seconds, ["turnEndsAt"] = null };
            if (tt.StartTicks is long st && st != 0)
            {
                double elapsed = (Stopwatch.GetTimestamp() - st) / (double)Stopwatch.Frequency;
                if (elapsed >= 0 && elapsed < 86_400)
                {
                    args["elapsed"] = Math.Round(elapsed, 1);
                    args["startedAt"] = DateTimeOffset.UtcNow.AddSeconds(-elapsed).ToString("o");
                }
                timerStart = st;
            }
            rec.Write(new GameEvent("turn_timer", s.Turn, args));
        }
        // the turn's snapshot ~0.5 s after its first look, when Civ's turn work is done (the reader reads cities and tiles
        // fresh at that very look); a reload / rejoin / first baseline snapshot at once (0.4.3)
        // a turn whose snapshot was still due (the next turn or a reload came within 0.5 s): its last look stands in
        if (snapshotDueMs >= 0 && (reset || writeSnapshotNow || s.Turn != lastTurn) && previous is not null) { WriteSnapshot(previous); snapshotDueMs = -1; }
        if (reset || writeSnapshotNow) { WriteSnapshot(s); snapshotDueMs = -1; }
        else if (s.Turn != lastTurn && snapshotEachTurn) snapshotDueMs = timeMs + StateReader.TurnSettleMs;
        if (snapshotDueMs >= 0 && timeMs >= snapshotDueMs) { WriteSnapshot(s); snapshotDueMs = -1; }
        previous = s;
        baselineWritten = true;
        lastTurn = s.Turn;
    }

    /// <summary>One "plot_changed" event per tile whose owner, resource, improvement, route or pillaged flags changed: x, y, and old → new of each.</summary>
    public static IEnumerable<GameEvent> TileChanges(TileTable a, TileTable b, int turn)
    {
        for (int i = 0; i < b.Count; i++)
        {
            Dictionary<string, object?>? ch = null;
            void Note(string k, object o, object n) { if (!o.Equals(n)) (ch ??= [])[k] = new[] { o, n }; }
            Note("owner", (int)a.Owner[i], (int)b.Owner[i]);
            Note("resource", (int)a.Resource[i], (int)b.Resource[i]);
            Note("improvement", (int)a.Improvement[i], (int)b.Improvement[i]);
            Note("route", (int)a.Route[i], (int)b.Route[i]);
            Note("improvementPillaged", a.ImprovementPillaged[i], b.ImprovementPillaged[i]);
            Note("routePillaged", a.RoutePillaged[i], b.RoutePillaged[i]);
            if (ch is not null) yield return new GameEvent("plot_changed", turn, new Dictionary<string, object?> { ["x"] = i % b.Width, ["y"] = i / b.Width, ["changes"] = ch });
        }
    }

    /// <summary>
    /// The whole tile table for a snapshot: columns owner, resource, improvement, route, pillaged (bit 0 improvement, bit 1
    /// route), each a byte per tile row by row (index = y × width + x; owner/resource/improvement/route as signed bytes,
    /// -1 = none), the five columns one after another, gzip, base64.
    /// </summary>
    public static Dictionary<string, object?> TileTableData(TileTable t)
    {
        int n = t.Count;
        var raw = new byte[n * 5];
        for (int i = 0; i < n; i++)
        {
            raw[i] = (byte)t.Owner[i]; raw[n + i] = (byte)t.Resource[i]; raw[2 * n + i] = (byte)t.Improvement[i]; raw[3 * n + i] = (byte)t.Route[i];
            raw[4 * n + i] = (byte)((t.ImprovementPillaged[i] ? 1 : 0) | (t.RoutePillaged[i] ? 2 : 0));
        }
        using var o = new MemoryStream();
        using (var z = new System.IO.Compression.GZipStream(o, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(raw);
        return new()
        {
            ["width"] = t.Width, ["height"] = t.Height, ["columns"] = new[] { "owner", "resource", "improvement", "route", "pillaged" },
            ["encoding"] = "gzip+base64, columns one after another, a byte per tile", ["readAtMs"] = t.TimeMs, ["data"] = Convert.ToBase64String(o.ToArray()),
        };
    }

    void WriteSnapshot(GameSnapshot s) => rec.Write("snapshot", s.Turn, new Dictionary<string, object?>
    {
        ["cityMicro"] = cityMicro.States.Where(c => c.ReadAtMs <= s.TimeMs).Select(c => c.Data()).ToArray(),
        ["tiles"] = (s.Tiles ?? tiles) is { } tt ? TileTableData(tt) : null,
        ["source"] = "memory_sample", ["activePlayer"] = s.ActivePlayer, ["gameType"] = s.GameType, ["rejectedReads"] = s.Rejected,
        ["players"] = s.Players.Select(p => new Dictionary<string, object?>
        {
            ["id"] = p.Id, ["alive"] = p.Alive, ["slot"] = p.Slot, ["civType"] = p.CivType, ["civ"] = p.Civ, ["turnActive"] = p.TurnActive,
        }),
        ["units"] = s.Units.Select(u => new object?[] { u.Owner, u.Id, u.Type, u.X, u.Y, u.Damage, u.Moves, u.FortifyTurns, u.Level, u.Embarked }),
        ["unitColumns"] = new[] { "owner", "unitId", "unitType", "x", "y", "damage", "moves60", "fortifyTurns", "level", "embarked" },
        ["cities"] = s.Cities.Select(c => new Dictionary<string, object?>
        {
            ["owner"] = c.Owner, ["cityId"] = c.Id, ["x"] = c.X, ["y"] = c.Y, ["name"] = c.Name, ["population"] = c.Population,
            ["damage"] = c.Damage, ["puppet"] = c.Puppet, ["originalOwner"] = c.OriginalOwner, ["turnFounded"] = c.TurnFounded,
        }),
    });
}

/// <summary>"--replay-tape": runs reader + differ again over a recorded tape and writes events.replay.jsonl.</summary>
public static class TapeReplay
{
    /// <summary>Replays <paramref name="sessionFolder"/>/tape.bin.gz; returns (samples, lines written, output path).</summary>
    public static (int Samples, long Lines, string Output) Run(string sessionFolder, bool snapshotEachTurn = true)
    {
        var tape = Path.Combine(sessionFolder, "tape.bin.gz");
        var output = Path.Combine(sessionFolder, "events.replay.jsonl");
        if (File.Exists(output)) File.Delete(output);
        long now = 0;
        using var rec = new JsonlRecording(sessionFolder, "events.replay.jsonl", () => now, wallClock: false);
        var mem = new ReplayMemory();
        StateReader? reader = null;
        GamePipeline? pipe = null;
        int samples = 0;
        foreach (var (h, s) in TapeReader.Read(tape))
        {
            if (h is not null)
            {
                var layout = KnownLayouts.All.FirstOrDefault(l => l.Id == h.Layout && l.DllSha256 == h.CoreSha256)
                    ?? throw new InvalidDataException($"tape was made with {h.Layout}, which this version does not know");
                mem.Clear();
                reader = new StateReader(mem, h.ModuleBase, layout) { BlockReads = h.Reads >= 2, ReadCityMicro = h.Reads >= 3 };   // the same reads the live run made
                pipe ??= new GamePipeline(rec, reader.UnitTypeKey, snapshotEachTurn);
                continue;
            }
            mem.Apply(s!);
            now = s!.TimeUs;
            var (st, snap, _) = reader!.Read(s.TimeUs / 1000);
            pipe!.Step(st, snap, s.TimeUs / 1000);
            samples++;
        }
        return (samples, rec.Count, output);
    }
}
