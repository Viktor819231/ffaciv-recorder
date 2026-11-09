using System.Buffers.Binary;

namespace OutsideRecorderCore;

/// <summary>Why a sample could not be taken (shown in the window, no event is written).</summary>
public enum SampleStatus
{
    /// <summary>A snapshot was read.</summary>
    Ok,
    /// <summary>Civ is in the menus / loading: no game is running.</summary>
    NoGame,
    /// <summary>A check failed (wrong vtable, impossible value): nothing trusted.</summary>
    LayoutMismatch,
    /// <summary>Memory could not be read (Civ closing).</summary>
    Unreadable,
}

/// <summary>
/// Reads one snapshot of the game from Civ's memory, using a <see cref="GameCoreLayout"/> from the PDB.
/// Every object is checked (vtable, id, owner, tile inside the map) and skipped when a check fails,
/// because the game keeps changing memory while we read it.
/// </summary>
public sealed partial class StateReader(IMemory mem, uint moduleBase, GameCoreLayout l)
{
    readonly Dictionary<int, string?> unitTypeKeys = new();
    readonly Dictionary<int, string?> civKeys = new();
    readonly Dictionary<(int, int), string?> cityNames = new();
    /// <summary>The tile table's read buffer, kept between reads: ~5 MB on a huge map, read once a second (the tape keeps its own copy).</summary>
    byte[]? tileBytes;

    uint Abs(uint rva) => moduleBase + rva;

    /// <summary>For measuring only: told which part of a sample is being read ("globals", "players", "units", "cities", "timer", "tiles").</summary>
    public Action<string>? Phase { get; set; }

    /// <summary>
    /// Version 2 reads (0.4): each player's fields in one span, each unit's in one block, cities every <see cref="CitiesEveryMs"/>,
    /// the tiles every <see cref="TilesEveryMs"/> in one block. False = the 0.3 way (a call per field, every look): replays of
    /// older tapes, which hold only those reads.
    /// </summary>
    public bool BlockReads { get; init; } = true;
    /// <summary>Cities are read at most this often (ms; 200 = 5 a second). The units every look.</summary>
    public int CitiesEveryMs { get; init; } = 200;
    /// <summary>Tiles are read at most this often (ms; 1000 = once a second).</summary>
    public int TilesEveryMs { get; init; } = 1000;
    /// <summary>City assignments and production queues are read once a second, independently of city health.</summary>
    public int CityMicroEveryMs { get; init; } = 1000;
    /// <summary>Old tapes do not contain the optional city-management reads.</summary>
    public bool ReadCityMicro { get; init; } = true;
    /// <summary>Reads version 4: a unit type or civilization name that could not be read is asked again at its next use
    /// instead of staying unknown for the rest of the game. False = replays of older tapes, made without those reads.</summary>
    public bool RetryUnreadNames { get; init; } = true;
    /// <summary>Reads version 4: a new turn's timer start is read at the turn's first look (it used to wait up to 200 ms,
    /// so the turn's first timer line had no start). False = replays of older tapes.</summary>
    public bool TimerStartOnNewTurn { get; init; } = true;
    long citiesAtMs = long.MinValue, tilesAtMs = long.MinValue, settleAtMs = long.MinValue;
    int settleTurn = int.MinValue;
    /// <summary>How long after a turn's first look its snapshot state is taken (cities and tiles read fresh then).</summary>
    public const int TurnSettleMs = 500;
    readonly Dictionary<int, List<CityState>> lastCities = [];

    /// <summary>Checks that the global objects look right (vtables). False = this layout must not be used.</summary>
    public bool CheckGlobals(out string why)
    {
        why = "";
        if (mem.Ptr(Abs(l.RvaGlobals)) != Abs(l.VtGlobals)) { why = "CvGlobals vtable does not match"; return false; }
        return true;
    }

    /// <summary>
    /// Only the game's globals (in the game core's own image) and the turn and map size: whether a game is running, without
    /// touching any player, unit, city or tile. Used while Civ loads or right after it: memory then is being built and freed.
    /// </summary>
    public (SampleStatus Status, int Turn, int Width, int Height) ReadHeader()
    {
        var (status, _, g) = ReadGlobals(sample: false);
        return status == SampleStatus.Ok ? (status, g.Turn, g.Width, g.Height) : (status, -1, -1, -1);
    }

    /// <summary>What every look starts with: the game core's globals, the game, map and player array, the map size and turn.</summary>
    readonly record struct Globals(uint Gc, uint Game, uint Map, uint Players, int Width, int Height, int Turn, int Active);

    /// <summary>
    /// The opening checks of <see cref="ReadHeader"/> and of a sample. A map object that cannot be read is no game (Civ is
    /// building or freeing it); a readable one with another vtable is a mismatch only for a sample (<paramref name="sample"/>):
    /// the header look runs while memory is in flux and calls it no game. A sample also reads the active player.
    /// </summary>
    (SampleStatus Status, string Why, Globals G) ReadGlobals(bool sample)
    {
        uint gc = Abs(l.RvaGlobals);
        if (mem.Ptr(gc) is not uint vt) return (SampleStatus.Unreadable, "game memory not readable", default);
        if (vt != Abs(l.VtGlobals)) return (SampleStatus.LayoutMismatch, "CvGlobals vtable does not match", default);
        // "game started" where confirmed; otherwise the map/turn checks below decide
        bool started = l.RvaGameStarted is uint gs ? mem.Bool(Abs(gs)) ?? false : true;
        uint game = mem.Ptr(gc + (uint)l.GlobalsGame) ?? 0, map = mem.Ptr(gc + (uint)l.GlobalsMap) ?? 0;
        uint players = mem.Ptr(Abs(l.RvaPlayers)) ?? 0;
        if (!started || game == 0 || map == 0 || players == 0) return (SampleStatus.NoGame, "no game running", default);
        if (mem.Ptr(map) is not uint mapVt) return (SampleStatus.NoGame, "CvMap not readable", default);
        if (mapVt != Abs(l.VtMap)) return (sample ? SampleStatus.LayoutMismatch : SampleStatus.NoGame, "CvMap vtable does not match", default);
        int width = mem.Int(map + (uint)l.MapWidth) ?? -1, height = mem.Int(map + (uint)l.MapHeight) ?? -1;
        int turn = mem.Int(Abs(l.RvaGameTurn)) ?? -1;
        int active = sample ? mem.Int(Abs(l.RvaActivePlayer)) ?? -1 : -1;
        if (width <= 0 || width > 512 || height <= 0 || height > 512 || turn < 0 || turn > 5000)
            return (SampleStatus.NoGame, "map not ready", default);
        return (SampleStatus.Ok, "", new Globals(gc, game, map, players, width, height, turn, active));
    }

    /// <summary>Takes one sample. <paramref name="timeMs"/> is stored as-is.</summary>
    public (SampleStatus Status, GameSnapshot? Snapshot, string Why) Read(long timeMs)
    {
        microSample = null;
        var result = ReadSample(timeMs);
        if (result.Status != SampleStatus.Ok) microAtMs = long.MinValue;
        return result;
    }

    (SampleStatus Status, GameSnapshot? Snapshot, string Why) ReadSample(long timeMs)
    {
        Phase?.Invoke("globals");
        var (status, why, g) = ReadGlobals(sample: true);
        if (status != SampleStatus.Ok) return (status, null, why);

        var slots = l.RvaSlotStatusVector is uint sv ? IntVector(Abs(sv)) : [];
        var civs = l.RvaCivilizationsVector is uint cv ? IntVector(Abs(cv)) : [];
        Phase?.Invoke("players");

        var playerList = new List<PlayerState>();
        var units = new List<UnitState>();
        var cities = new List<CityState>();
        int rejected = 0;
        if (!BlockReads)
        {
            ReadPlayersPerField(g, slots, civs, playerList, units, cities, ref rejected);
            return (SampleStatus.Ok, Snapshot(g, timeMs, civs, playerList, units, cities, rejected, tiles: null, micro: null, timerStart: false), "");
        }
        BeginMicroSample(timeMs, g.Turn, g.Gc);
        // a new turn: Civ's turn work (new units, growth, borders, AI moves) takes ~0.3-0.5 s; TurnSettleMs after the
        // turn's first look, cities and tiles are read fresh whatever their pace, for the turn's snapshot (0.4.3)
        if (g.Turn != settleTurn) { settleTurn = g.Turn; settleAtMs = timeMs + TurnSettleMs; }
        bool settleNow = settleAtMs != long.MinValue && timeMs >= settleAtMs;
        if (settleNow) settleAtMs = long.MinValue;
        bool readCities = microSample is not null || settleNow || citiesAtMs == long.MinValue || timeMs - citiesAtMs >= CitiesEveryMs || timeMs < citiesAtMs;
        if (readCities) citiesAtMs = timeMs;
        ReadPlayersBlocks(g.Players, g.Width, g.Height, slots, civs, g.Gc, readCities, playerList, units, cities, ref rejected);
        TileTable? tiles = null;
        if (l.MapPlots is not null && (settleNow || tilesAtMs == long.MinValue || timeMs - tilesAtMs >= TilesEveryMs || timeMs < tilesAtMs))
        {
            tilesAtMs = timeMs;
            Phase?.Invoke("tiles");
            tiles = ReadTiles(g.Map, g.Width, g.Height, timeMs);
        }
        return (SampleStatus.Ok, Snapshot(g, timeMs, civs, playerList, units, cities, rejected, tiles, microSample, timerStart: true), "");
    }

    /// <summary>A sample's snapshot. The pre-game values (game type, seeds) and then the turn timer are read last, in this order.</summary>
    GameSnapshot Snapshot(Globals g, long timeMs, List<int> civs, List<PlayerState> players, List<UnitState> units, List<CityState> cities,
        int rejected, TileTable? tiles, List<CityMicroState>? micro, bool timerStart) => new()
    {
        TimeMs = timeMs, Turn = g.Turn, ActivePlayer = g.Active, GameType = l.RvaGameType is uint gt ? mem.Int(Abs(gt)) : null,
        MapWidth = g.Width, MapHeight = g.Height, SlotCivs = civs,
        MapSeed = l.RvaMapSeed is uint ms ? (uint?)mem.Ptr(Abs(ms)) : null,
        SyncSeed = l.RvaSyncSeed is uint ss ? (uint?)mem.Ptr(Abs(ss)) : null,
        Players = players, Units = units, Cities = cities, Rejected = rejected, Tiles = tiles, CityMicro = micro,
        TurnTimer = Timer(g, timeMs, timerStart),
    };

    /// <summary>The turn timer; version 2 reads also keep its start fresh.</summary>
    TurnTimerState? Timer(Globals g, long timeMs, bool withStart)
    {
        Phase?.Invoke("timer");
        return withStart ? TimerWithStart(g.Turn, g.Game, timeMs) : TimerAt(g.Turn);
    }

    /// <summary>The 0.3 way (replays of version 1 tapes): every player field and list header in its own call.</summary>
    void ReadPlayersPerField(Globals g, List<int> slots, List<int> civs, List<PlayerState> playerList, List<UnitState> units,
        List<CityState> cities, ref int rejected)
    {
        for (int p = 0; p < l.MaxPlayers; p++)
        {
            uint pp = g.Players + (uint)(p * l.PlayerSize);
            if (mem.Ptr(pp) != Abs(l.VtPlayerAI)) continue; // not a player object (slot never built)
            if (mem.Int(pp + (uint)l.PlayerId) != p) continue;
            bool alive = mem.Bool(pp + (uint)l.PlayerAlive) ?? false;
            int? slot = p < slots.Count ? slots[p] : null;
            if (!alive && slot is null or 0 or 2) continue; // unused slot
            Phase?.Invoke("units");
            ReadUnitsFrom(ListObjects(pp + (uint)l.PlayerUnits), l.UnitHeadBytes, fromBlock: false, p, g.Width, g.Height, units, ref rejected);
            Phase?.Invoke("cities");
            ReadCitiesFrom(ListObjects(pp + (uint)l.PlayerCities), p, g.Width, g.Height, cities, ref rejected);
            Phase?.Invoke("players");
            playerList.Add(new PlayerState
            {
                Id = p, Alive = alive, TurnActive = mem.Bool(pp + (uint)l.PlayerTurnActive) ?? false, Slot = slot,
                CivType = p < civs.Count ? civs[p] : null, Civ = p < civs.Count ? CivKey(civs[p], g.Gc) : null,
            });
        }
    }

    long timerStartAtMs = long.MinValue;

    /// <summary>The turn timer with its start (QPC ticks) read again at most 5 times a second (one 8-byte read), and at once
    /// on a new turn (<see cref="TimerStartOnNewTurn"/>).</summary>
    TurnTimerState? TimerWithStart(int turn, uint game, long timeMs)
    {
        bool newTurn = turn != timerTurn;
        var t = TimerAt(turn);
        if (t is null || l.GameTurnTimer is not int off) return t;
        // a new turn replaced the timer with a record without its start: handing that back for up to 200 ms wrote the
        // turn's timer line without its start, and a second line once the start was read
        bool due = newTurn && TimerStartOnNewTurn;
        if (!due && timerStartAtMs != long.MinValue && timeMs - timerStartAtMs < 200 && timeMs >= timerStartAtMs) return timer;
        timerStartAtMs = timeMs;
        Span<byte> b = stackalloc byte[8];
        return timer = t with { StartTicks = mem.TryRead(game + (uint)off, b) ? BinaryPrimitives.ReadInt64LittleEndian(b) : null };
    }

    /// <summary>Bytes of a player read in one call: from the object start to the end of the later of its two lists.</summary>
    int PlayerSpan => Math.Max(Math.Max(l.PlayerUnits, l.PlayerCities) + 0x20, Math.Max(l.PlayerAlive, l.PlayerTurnActive) + 4);
    /// <summary>Bytes of a unit read in one call: the head plus the embarked / activity fields further in.</summary>
    int UnitSpan => Math.Max(l.UnitHeadBytes, Math.Max((l.UnitEmbarked ?? 0) + 1, (l.UnitActivity ?? 0) + 4));

    /// <summary>Version 2: the vtable of every slot (4 bytes), then each real player in one span with both list headers in it.</summary>
    void ReadPlayersBlocks(uint players, int w, int h, List<int> slots, List<int> civs, uint gc, bool readCities,
        List<PlayerState> playerList, List<UnitState> units, List<CityState> cities, ref int rejected)
    {
        Phase?.Invoke("players");
        var span = new byte[PlayerSpan];
        for (int p = 0; p < l.MaxPlayers; p++)
        {
            uint pp = players + (uint)(p * l.PlayerSize);
            if (mem.Ptr(pp) != Abs(l.VtPlayerAI)) continue; // not a player object (slot never built)
            if (!mem.TryRead(pp, span)) continue;
            int I(int off) => BinaryPrimitives.ReadInt32LittleEndian(span.AsSpan(off));
            if ((uint)I(0) != Abs(l.VtPlayerAI) || I(l.PlayerId) != p) continue;
            bool alive = span[l.PlayerAlive] != 0;
            int? slot = p < slots.Count ? slots[p] : null;
            if (!alive && slot is null or 0 or 2) continue; // unused slot
            Phase?.Invoke("units");
            ReadUnitsFrom(ListFrom(span, l.PlayerUnits), UnitSpan, fromBlock: true, p, w, h, units, ref rejected);
            Phase?.Invoke("cities");
            if (readCities)
            {
                var mine = new List<CityState>();
                ReadCitiesFrom(ListFrom(span, l.PlayerCities), p, w, h, mine, ref rejected);
                lastCities[p] = mine;
            }
            if (lastCities.TryGetValue(p, out var known)) cities.AddRange(known);
            Phase?.Invoke("players");
            playerList.Add(new PlayerState
            {
                Id = p, Alive = alive, TurnActive = span[l.PlayerTurnActive] != 0, Slot = slot,
                CivType = p < civs.Count ? civs[p] : null, Civ = p < civs.Count ? CivKey(civs[p], gc) : null,
            });
        }
    }

    /// <summary>A FFreeListTrashArray whose header is already in <paramref name="span"/> at <paramref name="at"/>: one read of its nodes.</summary>
    List<(int Index, uint Ptr)> ListFrom(byte[] span, int at) => ListNodes(
        BinaryPrimitives.ReadInt32LittleEndian(span.AsSpan(at + l.ListLastIndex)),
        l.ListNumSlots is int ns ? BinaryPrimitives.ReadInt32LittleEndian(span.AsSpan(at + ns)) : 8192,
        BinaryPrimitives.ReadUInt32LittleEndian(span.AsSpan(at + l.ListArray)));

    /// <summary>Walks one FFreeListTrashArray the 0.3 way: its header a field at a time, then its nodes.</summary>
    List<(int Index, uint Ptr)> ListObjects(uint list)
    {
        int lastIndex = mem.Int(list + (uint)l.ListLastIndex) ?? -1;
        int slots = l.ListNumSlots is int ns ? mem.Int(list + (uint)ns) ?? 0 : 8192;
        uint array = mem.Ptr(list + (uint)l.ListArray) ?? 0;
        return ListNodes(lastIndex, slots, array);
    }

    /// <summary>The object pointers in use (index, pointer) of a list with this header, its nodes read in one call.</summary>
    List<(int Index, uint Ptr)> ListNodes(int lastIndex, int slots, uint array)
    {
        var r = new List<(int, uint)>();
        if (lastIndex < 0 || array == 0 || lastIndex >= slots || slots > 8192) return r;
        var nodes = new byte[(lastIndex + 1) * 8];
        if (!mem.TryRead(array, nodes)) return r;
        for (int i = 0; i <= lastIndex; i++)
        {
            uint data = BinaryPrimitives.ReadUInt32LittleEndian(nodes.AsSpan(i * 8 + 4));
            if (data != 0) r.Add((i, data));
        }
        return r;
    }

    /// <summary>
    /// The units of one list, each read in one block of <paramref name="bytes"/> and checked (vtable, owner, id slot, tile on
    /// the map, damage); one that fails is skipped. Version 2 takes embarked and activity from the block
    /// (<paramref name="fromBlock"/>); the 0.3 way reads them after the checks, a call each.
    /// </summary>
    void ReadUnitsFrom(List<(int Index, uint Ptr)> objs, int bytes, bool fromBlock, int owner, int w, int h, List<UnitState> into, ref int rejected)
    {
        var buf = new byte[bytes];
        foreach (var (index, ptr) in objs)
        {
            if (!mem.TryRead(ptr, buf)) { rejected++; continue; }
            int I(int off) => BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off));
            int id = I(l.UnitId), x = I(l.UnitX), y = I(l.UnitY), dmg = I(l.UnitDamage);
            // FFreeListTrashArray: id = (serial << 13) | slot index.
            if ((uint)I(0) != Abs(l.VtUnitArchive) || I(l.UnitOwner) != owner || (id & 0x1FFF) != index
                || x < 0 || x >= w || y < 0 || y >= h || dmg < 0 || dmg > 1000) { rejected++; continue; }
            into.Add(new UnitState
            {
                Owner = owner, Id = id, Type = I(l.UnitType), X = x, Y = y, Damage = dmg, Moves = I(l.UnitMoves),
                FortifyTurns = I(l.UnitFortifyTurns), Experience = I(l.UnitExperience), Level = I(l.UnitLevel),
                TurnCreated = I(l.UnitTurnCreated), LastMoveTurn = I(l.UnitLastMoveTurn),
                Embarked = l.UnitEmbarked is int ue ? fromBlock ? buf[ue] != 0 : mem.Bool(ptr + (uint)ue) : null,
                Activity = l.UnitActivity is int ua ? fromBlock ? I(ua) : mem.Int(ptr + (uint)ua) : null,
            });
        }
    }

    /// <summary>
    /// All tiles in one read of CvMap::m_pMapPlots (width × height × sizeof(CvPlot)); every plot's own x/y must match its
    /// place, else nothing is trusted (null).
    /// </summary>
    TileTable? ReadTiles(uint map, int w, int h, long timeMs)
    {
        if (l.MapPlots is not int mp || l.PlotSize is not int size || l.PlotOwner is not int po || l.PlotResource is not int pr
            || l.PlotImprovement is not int pi || l.PlotRoute is not int pt || l.PlotPillagedByte is not int pb) return null;
        if (mem.Ptr(map + (uint)mp) is not uint plots || plots == 0) return null;
        int n = w * h;
        if (tileBytes?.Length != n * size) tileBytes = new byte[n * size];
        var buf = tileBytes;
        if (!mem.TryRead(plots, buf)) return null;
        var t = new TileTable(w, h, new sbyte[n], new sbyte[n], new sbyte[n], new sbyte[n], new bool[n], new bool[n], timeMs);
        for (int i = 0; i < n; i++)
        {
            int o = i * size;
            if (BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(o + l.PlotX)) != i % w || BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(o + l.PlotY)) != i / w) return null;
            t.Owner[i] = (sbyte)buf[o + po]; t.Resource[i] = (sbyte)buf[o + pr]; t.Improvement[i] = (sbyte)buf[o + pi]; t.Route[i] = (sbyte)buf[o + pt];
            t.ImprovementPillaged[i] = (buf[o + pb] >> l.PlotImprovementPillagedBit & 1) != 0;
            t.RoutePillaged[i] = (buf[o + pb] >> l.PlotRoutePillagedBit & 1) != 0;
        }
        return t;
    }

    int timerTurn = int.MinValue;
    TurnTimerState? timer;

    /// <summary>The turn timer, read again only when the turn changes (the game sets it per turn at most).</summary>
    TurnTimerState? TimerAt(int turn)
    {
        if (turn == timerTurn) return timer;
        timerTurn = turn;
        return timer = ReadTimer();
    }

    TurnTimerState? ReadTimer()
    {
        if (l.RvaEndTurnTimerLength is null && l.RvaTurnTimerType is null && l.RvaGameOptionsVector is null) return null;
        var options = Options();
        bool? on = options is null ? null : options.TryGetValue("GAMEOPTION_END_TURN_TIMER_ENABLED", out var v) && v != 0;
        float? seconds = l.RvaEndTurnTimerLength is uint t && mem.Int(Abs(t)) is int bits ? BitConverter.Int32BitsToSingle(bits) : null;
        return new TurnTimerState(on, l.RvaTurnTimerType is uint tt ? mem.Int(Abs(tt)) : null, seconds);
    }

    /// <summary>The game options (CvPreGame CustomOption: char[64] name + int value), or null when not readable.</summary>
    Dictionary<string, int>? Options()
    {
        if (l.RvaGameOptionsVector is not uint ov || mem.Vector(Abs(ov)) is not (uint first, uint last)) return null;
        const int Size = 0x44;
        if ((last - first) % Size != 0 || (last - first) / Size > 400) return null;
        var buf = new byte[last - first];
        if (!mem.TryRead(first, buf)) return null;
        var r = new Dictionary<string, int>();
        for (int i = 0; i + Size <= buf.Length; i += Size)
        {
            int end = Array.IndexOf(buf, (byte)0, i, 64);
            var name = System.Text.Encoding.ASCII.GetString(buf, i, (end < 0 ? i + 64 : end) - i);
            if (name.Length == 0 || name.Any(c => c < ' ' || c > '~')) return null;   // not option names: do not trust any of it
            r[name] = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i + 64));
        }
        return r;
    }

    /// <summary>
    /// The staging room (pre-game): slots, nicknames, civs, leaders, teams, map, speed, era, options, timer. Read-only and
    /// readable before a game starts; null when the game core's globals do not look right.
    /// </summary>
    public LobbySnapshot? ReadLobby()
    {
        uint gc = Abs(l.RvaGlobals);
        if (mem.Ptr(gc) != Abs(l.VtGlobals)) return null;
        bool started = l.RvaGameStarted is uint gs && (mem.Bool(Abs(gs)) ?? false);
        var slots = l.RvaSlotStatusVector is uint sv ? IntVector(Abs(sv)) : [];
        var civs = l.RvaCivilizationsVector is uint cv ? IntVector(Abs(cv)) : [];
        var leaders = l.RvaLeaderHeadsVector is uint lv ? IntVector(Abs(lv)) : [];
        var teams = l.RvaTeamTypesVector is uint tv ? IntVector(Abs(tv)) : [];
        var handicaps = l.RvaHandicapsVector is uint hv ? IntVector(Abs(hv)) : [];
        var names = l.RvaNicknamesVector is uint nv ? StringVector(Abs(nv), MaxMajorCivs) : [];
        var list = new List<LobbySlot>();
        for (int p = 0; p < Math.Min(slots.Count, MaxMajorCivs); p++)
        {
            if (slots[p] == 2) continue;   // closed
            int? civ = p < civs.Count ? civs[p] : null;
            list.Add(new LobbySlot
            {
                Id = p, Status = slots[p], Nickname = p < names.Count && names[p] is { Length: > 0 } n ? n : null,
                Civ = civ, CivKey = civ is int c && c >= 0 ? CivKey(c, gc) : null,
                Leader = p < leaders.Count ? leaders[p] : null, Team = p < teams.Count ? teams[p] : null, Handicap = p < handicaps.Count ? handicaps[p] : null,
            });
        }
        int? Int(uint? rva) => rva is uint r ? mem.Int(Abs(r)) : null;
        return new LobbySnapshot
        {
            GameStarted = started, GameType = Int(l.RvaGameType), ActivePlayer = Int(l.RvaActivePlayer),
            GameName = l.RvaGameName is uint gn ? mem.CvString(Abs(gn), 256) : null,
            MapScript = l.RvaMapScriptName is uint ms ? mem.CvString(Abs(ms), 512) : null,
            WorldSize = Int(l.RvaWorldSize), GameSpeed = Int(l.RvaGameSpeed), Era = Int(l.RvaEra),
            TurnTimer = ReadTimer(), Slots = list, Options = Options() ?? new Dictionary<string, int>(),
        };
    }

    /// <summary>The major civilizations' slots (MAX_MAJOR_CIVS).</summary>
    public const int MaxMajorCivs = 22;

    List<string?> StringVector(uint addr, int max)
    {
        var r = new List<string?>();
        if (mem.Vector(addr) is not (uint first, uint last) || (last - first) % 0x1C != 0) return r;
        for (uint at = first; at < last && r.Count < max; at += 0x1C) r.Add(mem.CvString(at, 128));
        return r;
    }

    /// <summary>UNIT_* key of a unit type, read once from the game's own unit table (asked again while unreadable).</summary>
    public string? UnitTypeKey(int type)
    {
        if (unitTypeKeys.TryGetValue(type, out var k) && (k is not null || !RetryUnreadNames)) return k;
        string? key = null;
        if (l.GlobalsUnitInfos is int ui && l.InfoType is int it0 && mem.Ptr(Abs(l.RvaGlobals) + (uint)ui) is uint entries && entries != 0
            && mem.Vector(entries) is (uint first, uint last) && type >= 0 && type < (last - first) / 4
            && mem.Ptr(first + (uint)type * 4) is uint info && info != 0)
            key = mem.CvString(info + (uint)it0, 64);
        if (key is not null && !key.StartsWith("UNIT_", StringComparison.Ordinal)) key = null;
        return unitTypeKeys[type] = key;
    }

    /// <summary>CIVILIZATION_* key of a civilization type, read once from the game's own table (asked again while unreadable).</summary>
    string? CivKey(int type, uint gc)
    {
        if (civKeys.TryGetValue(type, out var k) && (k is not null || !RetryUnreadNames)) return k;
        string? key = null;
        if (l.GlobalsCivInfos is int ci && l.InfoType is int it1 && mem.Vector(gc + (uint)ci) is (uint first, uint last) && type >= 0 && type < (last - first) / 4
            && mem.Ptr(first + (uint)type * 4) is uint info && info != 0)
            key = mem.CvString(info + (uint)it1, 64);
        if (key is not null && !key.StartsWith("CIVILIZATION_", StringComparison.Ordinal)) key = null;
        return civKeys[type] = key;
    }

    List<int> IntVector(uint addr)
    {
        var r = new List<int>();
        if (mem.Vector(addr) is not (uint first, uint last) || last - first > 4 * 256) return r;
        var buf = new byte[last - first];
        if (!mem.TryRead(first, buf)) return r;
        for (int i = 0; i + 4 <= buf.Length; i += 4) r.Add(BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i)));
        return r;
    }

    void ReadCitiesFrom(List<(int Index, uint Ptr)> objs, int owner, int w, int h, List<CityState> into, ref int rejected)
    {
        var buf = new byte[l.CityHeadBytes];
        foreach (var (index, ptr) in objs)
        {
            if (!mem.TryRead(ptr, buf)) { rejected++; continue; }
            int I(int off) => BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off));
            int id = I(l.CityId), x = I(l.CityX), y = I(l.CityY), pop = I(l.CityPopulation);
            if ((uint)I(0) != Abs(l.VtCityAI) || I(l.CityOwner) != owner || (id & 0x1FFF) != index
                || x < 0 || x >= w || y < 0 || y >= h || pop < 0 || pop > 200) { rejected++; continue; }
            if (!cityNames.TryGetValue((owner, id), out var name) || name is null)
                cityNames[(owner, id)] = name = l.CityName is int cn ? mem.CvString(ptr + (uint)cn, 128) : null;
            var city = new CityState
            {
                Owner = owner, Id = id, X = x, Y = y, Population = pop, Damage = I(l.CityDamage), TurnFounded = I(l.CityTurnFounded),
                OriginalOwner = I(l.CityOriginalOwner), Puppet = buf[l.CityPuppet] != 0, Occupied = buf[l.CityOccupied] != 0, Name = name,
            };
            into.Add(city);
            if (microSample is not null) microSample.Add(ReadMicro(ptr, city));
        }
    }
}
