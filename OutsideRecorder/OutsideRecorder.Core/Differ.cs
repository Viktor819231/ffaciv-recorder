namespace OutsideRecorderCore;

/// <summary>One thing that changed between two samples, in the in-game recorder's event shape (method + args).</summary>
public sealed record GameEvent(string Method, int Turn, IReadOnlyDictionary<string, object?> Args);

/// <summary>
/// Turns a stream of snapshots into events. A sampler only sees the state at each sample, so:
/// a multi-tile move is one UnitSetXY to the end tile; things undone between two samples are not seen.
/// A unit or city must be missing in two samples in a row before it is reported gone (one bad read is not a death).
/// </summary>
public sealed class Differ
{
    /// <summary>Samples a unit/city must be missing before it is reported removed.</summary>
    public int MissingSamplesBeforeRemoval { get; init; } = 2;

    readonly Dictionary<(int, int), UnitState> units = new();
    readonly Dictionary<(int, int), int> unitMissing = new();
    readonly Dictionary<(int, int), CityState> cities = new();
    readonly Dictionary<(int, int), int> cityMissing = new();
    readonly Dictionary<int, PlayerState> players = new();
    int turn = -1;
    bool first = true;

    /// <summary>Optional: turns a unit type number into its UNIT_* key for the events.</summary>
    public Func<int, string?>? UnitTypeKey { get; init; }

    /// <summary>Feeds one snapshot; returns what changed since the last one (nothing for the first).</summary>
    public List<GameEvent> Next(GameSnapshot s)
    {
        var ev = new List<GameEvent>();
        int t = s.Turn;
        GameEvent E(string m, params (string K, object? V)[] a) => new(m, t, a.ToDictionary(x => x.K, x => x.V));

        if (!first && s.Turn != turn) ev.Add(E("game_turn_changed", ("turn", s.Turn), ("previousTurn", turn)));
        turn = s.Turn;

        foreach (var p in s.Players)
        {
            if (players.TryGetValue(p.Id, out var old) && !first)
            {
                if (old.Alive != p.Alive) ev.Add(E("player_alive_changed", ("player", p.Id), ("alive", p.Alive)));
                if (old.TurnActive != p.TurnActive) ev.Add(E("player_turn_active", ("player", p.Id), ("active", p.TurnActive)));
                // multiplayer: a human who drops becomes AI (3 -> 1) and back on rejoin (1 -> 3)
                if (old.Slot != p.Slot) ev.Add(E("player_slot_changed", ("player", p.Id), ("from", old.Slot), ("to", p.Slot),
                    ("fromName", SlotName(old.Slot)), ("toName", SlotName(p.Slot))));
            }
            players[p.Id] = p;
        }

        // ---- units ----
        var seen = new HashSet<(int, int)>();
        var moved = new List<(UnitState Old, UnitState New)>();
        foreach (var u in s.Units)
        {
            seen.Add(u.Key);
            unitMissing.Remove(u.Key);
            if (!units.TryGetValue(u.Key, out var o))
            {
                if (!first) ev.Add(E("UnitCreated", ("owner", u.Owner), ("unitId", u.Id), ("unitType", u.Type),
                    ("unitTypeKey", UnitTypeKey?.Invoke(u.Type)), ("x", u.X), ("y", u.Y)));
            }
            else
            {
                if (o.X != u.X || o.Y != u.Y) moved.Add((o, u));
                if (o.Damage != u.Damage) ev.Add(E("unit_damage", ("owner", u.Owner), ("unitId", u.Id), ("unitType", u.Type),
                    ("x", u.X), ("y", u.Y), ("damage", u.Damage), ("previousDamage", o.Damage), ("moves", u.Moves), ("embarked", u.Embarked)));
                if (u.Level > o.Level) ev.Add(E("UnitPromoted", ("owner", u.Owner), ("unitId", u.Id), ("level", u.Level), ("experience", u.Experience)));
                // fortify / sleep / alert / wake inside a turn (FortifyTurns counting up while fortified is not a change)
                if (o.Activity != u.Activity || (o.FortifyTurns > 0) != (u.FortifyTurns > 0))
                    ev.Add(E("unit_activity", ("owner", u.Owner), ("unitId", u.Id), ("x", u.X), ("y", u.Y),
                        ("activity", u.Activity), ("activityName", ActivityName(u.Activity)), ("previousActivity", o.Activity),
                        ("fortified", u.FortifyTurns > 0), ("previousFortified", o.FortifyTurns > 0), ("fortifyTurns", u.FortifyTurns)));
                if (o.Type != u.Type) ev.Add(E("unit_type_changed", ("owner", u.Owner), ("unitId", u.Id), ("unitType", u.Type), ("previousUnitType", o.Type)));
            }
            units[u.Key] = u;
        }
        ev.AddRange(MoveEvents(moved, t));
        foreach (var key in units.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            int miss = unitMissing.GetValueOrDefault(key) + 1;
            if (miss < MissingSamplesBeforeRemoval) { unitMissing[key] = miss; continue; }
            var o = units[key];
            units.Remove(key); unitMissing.Remove(key);
            ev.Add(E("UnitRemoved", ("owner", o.Owner), ("unitId", o.Id), ("unitType", o.Type), ("x", o.X), ("y", o.Y),
                ("lastDamage", o.Damage), ("note", "killed, disbanded, upgraded or used up: a sampler cannot tell which")));
        }

        // ---- cities ----
        var seenC = new HashSet<(int, int)>();
        foreach (var c in s.Cities)
        {
            var key = (c.Owner, c.Id);
            seenC.Add(key);
            cityMissing.Remove(key);
            if (!cities.TryGetValue(key, out var o))
            {
                if (!first)
                {
                    var lost = cities.Values.FirstOrDefault(x => x.X == c.X && x.Y == c.Y && x.Owner != c.Owner);
                    if (lost is not null)
                    {
                        cities.Remove((lost.Owner, lost.Id)); cityMissing.Remove((lost.Owner, lost.Id));
                        ev.Add(E("CityCaptured", ("x", c.X), ("y", c.Y), ("from", lost.Owner), ("to", c.Owner), ("cityId", c.Id), ("name", c.Name)));
                    }
                    else ev.Add(E("PlayerCityFounded", ("owner", c.Owner), ("cityId", c.Id), ("x", c.X), ("y", c.Y), ("name", c.Name), ("turnFounded", c.TurnFounded)));
                }
            }
            else
            {
                if (o.Population != c.Population) ev.Add(E("SetPopulation", ("owner", c.Owner), ("cityId", c.Id), ("x", c.X), ("y", c.Y),
                    ("previousPopulation", o.Population), ("population", c.Population)));
                if (o.Damage != c.Damage) ev.Add(E("city_damage", ("owner", c.Owner), ("cityId", c.Id), ("x", c.X), ("y", c.Y),
                    ("damage", c.Damage), ("previousDamage", o.Damage)));
                if (o.Puppet != c.Puppet) ev.Add(E("city_puppet_changed", ("owner", c.Owner), ("cityId", c.Id), ("puppet", c.Puppet)));
            }
            cities[key] = c;
        }
        foreach (var key in cities.Keys.Where(k => !seenC.Contains(k)).ToList())
        {
            int miss = cityMissing.GetValueOrDefault(key) + 1;
            if (miss < MissingSamplesBeforeRemoval) { cityMissing[key] = miss; continue; }
            var o = cities[key];
            cities.Remove(key); cityMissing.Remove(key);
            ev.Add(E("CityRemoved", ("owner", o.Owner), ("cityId", o.Id), ("x", o.X), ("y", o.Y), ("name", o.Name)));
        }
        first = false;
        return ev;
    }

    /// <summary>
    /// Moves, with the game's swap rule (user, 2026-10-26): two units exchanging tiles between two samples is ONE swap
    /// only if both still had moves; a fortified unit never swaps. Fortified is judged on the latest state (the new sample):
    /// a unit fortified at the last sample may have woken up and swapped in between (user's Lekmod test, turn 9), and a
    /// unit that moved is never fortified afterwards. Tiles need not be next to each other (Lekmod swapped across 2 tiles).
    /// </summary>
    public static IEnumerable<GameEvent> MoveEvents(List<(UnitState Old, UnitState New)> moved, int turn)
    {
        var swapWith = new Dictionary<(int, int), UnitState>();
        for (int i = 0; i < moved.Count; i++)
            for (int j = i + 1; j < moved.Count; j++)
            {
                var (a0, a1) = moved[i]; var (b0, b1) = moved[j];
                bool exchanged = a1.X == b0.X && a1.Y == b0.Y && b1.X == a0.X && b1.Y == a0.Y;
                if (!exchanged || swapWith.ContainsKey(a0.Key) || swapWith.ContainsKey(b0.Key)) continue;
                if (CanSwap(a0, b0, a1, b1)) { swapWith[a0.Key] = b0; swapWith[b0.Key] = a0; }
            }
        foreach (var (o, u) in moved)
        {
            var args = new Dictionary<string, object?>
            {
                ["owner"] = u.Owner, ["unitId"] = u.Id, ["x"] = u.X, ["y"] = u.Y, ["fromX"] = o.X, ["fromY"] = o.Y,
                ["moves"] = u.Moves, ["previousMoves"] = o.Moves, ["embarked"] = u.Embarked,
            };
            if (swapWith.TryGetValue(o.Key, out var other)) { args["swapWithOwner"] = other.Owner; args["swapWithUnitId"] = other.Id; }
            yield return new GameEvent("UnitSetXY", turn, args);
        }
    }

    /// <summary>ActivityTypes names (CvEnums.h).</summary>
    public static string? ActivityName(int? a) => a switch
    {
        0 => "awake", 1 => "hold", 2 => "sleep", 3 => "heal", 4 => "sentry", 5 => "intercept", 6 => "mission", _ => null,
    };

    /// <summary>SlotStatus names (CvEnums.h): 0 open, 1 computer, 2 closed, 3 human (taken), 4 observer.</summary>
    public static string? SlotName(int? s) => s switch { 0 => "open", 1 => "computer", 2 => "closed", 3 => "human", 4 => "observer", _ => null };

    /// <summary>The swap rule: both units had moves left before, and neither is fortified now (the latest state).</summary>
    public static bool CanSwap(UnitState aBefore, UnitState bBefore, UnitState aNow, UnitState bNow) =>
        aBefore.Moves > 0 && bBefore.Moves > 0 && aNow.FortifyTurns <= 0 && bNow.FortifyTurns <= 0;
}
