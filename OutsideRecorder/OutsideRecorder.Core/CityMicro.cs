namespace OutsideRecorderCore;

/// <summary>City-management offsets verified against each supported DLL/PDB (evidence-city-micro.txt).</summary>
public sealed record CityMicroLayout(int Citizens, int Queue, int Food100, int OverflowRaw, int SpecialistInfos, int BuildingInfos)
{
    /// <summary>Bytes needed from the city, including its citizen pointer and queue header.</summary>
    public int CityBytes => Math.Max(Citizens + 4, Queue + 12);
}

/// <summary>A queued order, retaining the game's order/type IDs and repeat flag.</summary>
public sealed record CityOrder(int OrderType, int Data1, int Data2, bool Repeat);

/// <summary>
/// Observed city management, sampled once a second. Null fields were unreadable, not zero/empty.
/// Plot arrays contain city-relative indices 0..36; specialist/building arrays are indexed by database ID.
/// </summary>
public sealed record CityMicroState
{
    /// <summary>Owning player slot.</summary>
    public required int Owner { get; init; }
    /// <summary>City identity within that player.</summary>
    public required int CityId { get; init; }
    /// <summary>City tile column.</summary>
    public required int X { get; init; }
    /// <summary>City tile row.</summary>
    public required int Y { get; init; }
    /// <summary>Sample time, retained when reused in a later turn snapshot.</summary>
    public required long ReadAtMs { get; init; }
    /// <summary>The game's CityAIFocusTypes value.</summary>
    public int? Focus { get; init; }
    /// <summary>The player's avoid-growth setting.</summary>
    public bool? AvoidGrowth { get; init; }
    /// <summary>Manual specialist assignment is enabled.</summary>
    public bool? NoAutoAssignSpecialists { get; init; }
    /// <summary>Worked city-relative plot indices.</summary>
    public int[]? WorkedPlots { get; init; }
    /// <summary>Locked city-relative plot indices.</summary>
    public int[]? ForcedPlots { get; init; }
    /// <summary>Citizens assigned as default specialists.</summary>
    public int? DefaultSpecialists { get; init; }
    /// <summary>Default specialists explicitly forced by the player.</summary>
    public int? ForcedDefaultSpecialists { get; init; }
    /// <summary>Assigned specialist counts by specialist type ID.</summary>
    public int[]? Specialists { get; init; }
    /// <summary>Assigned specialist counts by building type ID.</summary>
    public int[]? SpecialistsByBuilding { get; init; }
    /// <summary>Locked specialist counts by building type ID.</summary>
    public int[]? ForcedSpecialistsByBuilding { get; init; }
    /// <summary>Production orders in queue order, or null if the queue could not be read consistently.</summary>
    public CityOrder[]? Queue { get; init; }
    /// <summary>Stored food in hundredths, as GetFoodTimes100 reads it.</summary>
    public int? Food100 { get; init; }
    /// <summary>Stored production overflow, without an assumed scale or derived modifiers.</summary>
    public int? OverflowProductionRaw { get; init; }

    /// <summary>Stable field names shared by baseline snapshots and before/after changes.</summary>
    public Dictionary<string, object?> Fields() => new()
    {
        ["focus"] = Focus, ["avoidGrowth"] = AvoidGrowth, ["noAutoAssignSpecialists"] = NoAutoAssignSpecialists,
        ["workedCityPlots"] = WorkedPlots, ["forcedCityPlots"] = ForcedPlots,
        ["defaultSpecialists"] = DefaultSpecialists, ["forcedDefaultSpecialists"] = ForcedDefaultSpecialists,
        ["specialists"] = Specialists, ["specialistsByBuilding"] = SpecialistsByBuilding,
        ["forcedSpecialistsByBuilding"] = ForcedSpecialistsByBuilding,
        ["queue"] = Queue, ["food100"] = Food100, ["overflowProductionRaw"] = OverflowProductionRaw,
    };

    /// <summary>Compact named observation, including the time actually read rather than the enclosing snapshot's time.</summary>
    public Dictionary<string, object?> Data() => new()
    {
        ["owner"] = Owner, ["cityId"] = CityId, ["x"] = X, ["y"] = Y,
        ["readAtMs"] = ReadAtMs, ["fields"] = Fields(),
    };
}

/// <summary>Change-only city-management logging; the first observation establishes a baseline.</summary>
public sealed class CityMicroDiffer
{
    readonly Dictionary<(int, int), CityMicroState> known = new();
    /// <summary>Latest observations, including each observation's age.</summary>
    public IEnumerable<CityMicroState> States => known.Values;
    /// <summary>A reload starts a new baseline rather than diffing an abandoned timeline.</summary>
    public void Clear() => known.Clear();

    /// <summary>Forget a removed or transferred city so reused IDs do not inherit its assignments.</summary>
    public void RemoveAt(int x, int y)
    {
        foreach (var key in known.Where(p => p.Value.X == x && p.Value.Y == y).Select(p => p.Key).ToArray()) known.Remove(key);
    }

    /// <summary>Only fresh samples are fed here; null means no micro scan was due.</summary>
    public IEnumerable<GameEvent> Next(IReadOnlyList<CityMicroState>? sample, int turn)
    {
        if (sample is null) yield break;
        foreach (var state in sample)
        {
            var key = (state.Owner, state.CityId);
            if (!known.TryGetValue(key, out var before) || (before.X, before.Y) != (state.X, state.Y))
            {
                known[key] = state;
                yield return new GameEvent("city_micro_baseline", turn, state.Data());
                continue;
            }
            var old = before.Fields();
            var changes = new Dictionary<string, object?>();
            foreach (var (name, value) in state.Fields())
            {
                var was = old[name];
                bool same = (was, value) switch
                {
                    (int[] a, int[] b) => a.AsSpan().SequenceEqual(b),
                    (CityOrder[] a, CityOrder[] b) => a.SequenceEqual(b),
                    _ => Equals(was, value),
                };
                if (!same) changes[name] = new object?[] { was, value };
            }
            known[key] = state;
            if (changes.Count > 0)
                yield return new GameEvent("city_micro_changed", turn, new Dictionary<string, object?>
                {
                    ["owner"] = state.Owner, ["cityId"] = state.CityId, ["x"] = state.X, ["y"] = state.Y,
                    ["readAtMs"] = state.ReadAtMs, ["previousReadAtMs"] = before.ReadAtMs, ["changes"] = changes,
                });
        }
    }
}
