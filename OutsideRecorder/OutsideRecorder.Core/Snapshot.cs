namespace OutsideRecorderCore;

/// <summary>One unit as seen in one sample.</summary>
public sealed record UnitState
{
    /// <summary>Owner player (0..63).</summary>
    public required int Owner { get; init; }
    /// <summary>The game's unit id (same number the save and the in-game recorder use).</summary>
    public required int Id { get; init; }
    /// <summary>UnitTypes index.</summary>
    public required int Type { get; init; }
    /// <summary>Tile column.</summary>
    public required int X { get; init; }
    /// <summary>Tile row.</summary>
    public required int Y { get; init; }
    /// <summary>Damage taken (0 = full health, 100 = dead).</summary>
    public required int Damage { get; init; }
    /// <summary>Moves left in 60ths of a move.</summary>
    public required int Moves { get; init; }
    /// <summary>Fortify turns; &gt; 0 means fortified (a fortified unit never swaps tiles).</summary>
    public required int FortifyTurns { get; init; }
    /// <summary>Experience points.</summary>
    public required int Experience { get; init; }
    /// <summary>Promotion level.</summary>
    public required int Level { get; init; }
    /// <summary>Embarked on water.</summary>
    public bool? Embarked { get; init; }
    /// <summary>ActivityTypes (0 awake, 1 hold, 2 sleep, 3 heal, 4 sentry, 5 intercept, 6 mission).</summary>
    public int? Activity { get; init; }
    /// <summary>Turn the unit was created.</summary>
    public required int TurnCreated { get; init; }
    /// <summary>Last turn it moved.</summary>
    public required int LastMoveTurn { get; init; }
    /// <summary>Key for lookups: owner + id.</summary>
    public (int Owner, int Id) Key => (Owner, Id);
}

/// <summary>One city as seen in one sample.</summary>
public sealed record CityState
{
    /// <summary>Owner player.</summary>
    public required int Owner { get; init; }
    /// <summary>The game's city id.</summary>
    public required int Id { get; init; }
    /// <summary>Tile column.</summary>
    public required int X { get; init; }
    /// <summary>Tile row.</summary>
    public required int Y { get; init; }
    /// <summary>Citizens.</summary>
    public required int Population { get; init; }
    /// <summary>Damage taken (hit points lost).</summary>
    public required int Damage { get; init; }
    /// <summary>Turn founded.</summary>
    public required int TurnFounded { get; init; }
    /// <summary>Who founded it.</summary>
    public required int OriginalOwner { get; init; }
    /// <summary>Puppet city.</summary>
    public required bool Puppet { get; init; }
    /// <summary>Occupied city.</summary>
    public required bool Occupied { get; init; }
    /// <summary>Name as the game shows it (may be a text key); null when unreadable.</summary>
    public string? Name { get; init; }
}

/// <summary>One player slot.</summary>
public sealed record PlayerState
{
    /// <summary>Player index.</summary>
    public required int Id { get; init; }
    /// <summary>Still in the game.</summary>
    public required bool Alive { get; init; }
    /// <summary>It is this player's turn (in simultaneous turns: everyone still playing).</summary>
    public required bool TurnActive { get; init; }
    /// <summary>SlotStatus: 0 open, 1 computer, 2 closed, 3 human (taken), 4 observer; null when unknown.</summary>
    public int? Slot { get; init; }
    /// <summary>CivilizationTypes number; null when not read.</summary>
    public int? CivType { get; init; }
    /// <summary>Civilization key, e.g. CIVILIZATION_ARGENTINA; null when not read (stock BNW: not confirmed).</summary>
    public string? Civ { get; init; }
}

/// <summary>Everything one sample read. Values not read are null, never guessed.</summary>
public sealed record GameSnapshot
{
    /// <summary>Fresh once-per-second city management observations; null when not sampled.</summary>
    public IReadOnlyList<CityMicroState>? CityMicro { get; init; }
    /// <summary>Milliseconds since the recorder session started.</summary>
    public required long TimeMs { get; init; }
    /// <summary>Game turn (CvPreGame::gameTurn).</summary>
    public required int Turn { get; init; }
    /// <summary>The human at this PC.</summary>
    public required int ActivePlayer { get; init; }
    /// <summary>GameTypes: 0 single player, 1 network multiplayer, 2 hot seat, 3 e-mail (checked against CvPreGame::isHuman in both DLLs).</summary>
    public int? GameType { get; init; }
    /// <summary>The game's map random seed (pre-game; constant for the game's life, the same on every player's PC); null when not read.</summary>
    public uint? MapSeed { get; init; }
    /// <summary>The game's sync random seed (pre-game); null when not read.</summary>
    public uint? SyncSeed { get; init; }
    /// <summary>Every slot's civilization number (all 64, alive or not): the identity's fallback when the seeds are not read.</summary>
    public IReadOnlyList<int> SlotCivs { get; init; } = [];
    /// <summary>Map width in tiles.</summary>
    public required int MapWidth { get; init; }
    /// <summary>Map height in tiles.</summary>
    public required int MapHeight { get; init; }
    /// <summary>The tiles, when they were read in this look (about once a second), else null.</summary>
    public TileTable? Tiles { get; init; }
    /// <summary>The multiplayer turn timer (read when the turn changes), or null when not readable.</summary>
    public TurnTimerState? TurnTimer { get; init; }
    /// <summary>Living or used player slots.</summary>
    public required IReadOnlyList<PlayerState> Players { get; init; }
    /// <summary>All units read.</summary>
    public required IReadOnlyList<UnitState> Units { get; init; }
    /// <summary>All cities read.</summary>
    public required IReadOnlyList<CityState> Cities { get; init; }
    /// <summary>Objects skipped because they failed a check (torn read while the game changed them).</summary>
    public int Rejected { get; init; }
}

/// <summary>
/// The multiplayer turn timer as the game's pre-game data holds it: the "end turn timer" game option, the timer type and
/// the length the game set (seconds). The deadline of the running turn is not readable (null in the recording).
/// </summary>
public sealed record TurnTimerState(bool? Enabled, int? Type, float? Seconds)
{
    /// <summary>QueryPerformanceCounter value when the running turn's timer started (0 = not running), or null when not read.</summary>
    public long? StartTicks { get; init; }
    /// <summary>The turn's length in whole seconds when the timer is on and a length is set, else null.</summary>
    public int? TurnSeconds => Enabled == true && Seconds is float f && f > 0 && f < 100_000 ? (int)Math.Round(f) : null;
    /// <summary>The setting as the recording and lobby lines write it: enabled, type, seconds.</summary>
    public Dictionary<string, object?> Data() => new() { ["enabled"] = Enabled, ["type"] = Type, ["seconds"] = Seconds };
}

/// <summary>One player slot in the staging room.</summary>
public sealed record LobbySlot
{
    /// <summary>Player number (0..21 for the major civs).</summary>
    public required int Id { get; init; }
    /// <summary>SlotStatus: 0 open, 1 computer, 2 closed, 3 human (taken), 4 observer.</summary>
    public required int Status { get; init; }
    /// <summary>The player's nickname (Steam name) for a human, else null.</summary>
    public string? Nickname { get; init; }
    /// <summary>CivilizationTypes picked, -1 = random, null = not read.</summary>
    public int? Civ { get; init; }
    /// <summary>The civ's key (CIVILIZATION_…) when readable.</summary>
    public string? CivKey { get; init; }
    /// <summary>LeaderHeadTypes picked, -1 = random.</summary>
    public int? Leader { get; init; }
    /// <summary>Team number.</summary>
    public int? Team { get; init; }
    /// <summary>HandicapTypes (difficulty) of the slot.</summary>
    public int? Handicap { get; init; }

    /// <summary>The player's name: the nickname without its "@SteamID" (multiplayer keeps "Name@76561198…").</summary>
    public string? Name => Nickname is null ? null : SplitNickname(Nickname).Name;
    /// <summary>The Steam id after the "@" (17 digits), or null.</summary>
    public string? SteamId => Nickname is null ? null : SplitNickname(Nickname).SteamId;

    /// <summary>
    /// A slot that holds someone or can take someone: a human, an AI, an open seat, or an observer who is a real person
    /// (has a nickname). Stock BNW marks every unused major slot "observer" with no name: those are not in use.
    /// </summary>
    public bool InUse => Status is 1 or 3 or 0 || (Status == 4 && !string.IsNullOrEmpty(Nickname));

    /// <summary>"Player@76561198000000001" → ("Player", "76561198000000001"); a name without a Steam id stays whole.</summary>
    public static (string Name, string? SteamId) SplitNickname(string nickname)
    {
        var m = System.Text.RegularExpressions.Regex.Match(nickname, @"^(.*)@(\d{17})$");
        return m.Success && m.Groups[1].Value.Length > 0 ? (m.Groups[1].Value, m.Groups[2].Value) : (nickname, null);
    }
}

/// <summary>The multiplayer staging room as read from the game's pre-game data (before a game is started).</summary>
public sealed record LobbySnapshot
{
    /// <summary>A game is running already (then this is not a lobby).</summary>
    public required bool GameStarted { get; init; }
    /// <summary>GameTypes: 0 single player, 1 network multiplayer, 2 hot seat, 3 e-mail; null when not read.</summary>
    public int? GameType { get; init; }
    /// <summary>The player at this PC.</summary>
    public int? ActivePlayer { get; init; }
    /// <summary>The lobby's name (LEKMOD puts its version tag here).</summary>
    public string? GameName { get; init; }
    /// <summary>The map script file.</summary>
    public string? MapScript { get; init; }
    /// <summary>WorldSizeTypes / GameSpeedTypes / EraTypes numbers (the names live in the game's database).</summary>
    public int? WorldSize { get; init; }
    /// <summary>GameSpeedTypes.</summary>
    public int? GameSpeed { get; init; }
    /// <summary>Start era (EraTypes).</summary>
    public int? Era { get; init; }
    /// <summary>The turn timer setting.</summary>
    public TurnTimerState? TurnTimer { get; init; }
    /// <summary>Major-civ slots that are not closed.</summary>
    public IReadOnlyList<LobbySlot> Slots { get; init; } = [];
    /// <summary>Game options set in the lobby (name → value).</summary>
    public IReadOnlyDictionary<string, int> Options { get; init; } = new Dictionary<string, int>();
    /// <summary>A multiplayer staging room: no game started, network multiplayer, someone in a slot.</summary>
    public bool IsStagingRoom => !GameStarted && GameType == 1 && Slots.Any(s => s.Status == 3);
}

/// <summary>
/// The map's tiles as read in one block (card OR03 "Tiles"): per tile owner, resource, improvement, route and the two
/// pillaged flags, row by row (index = y × width + x). -1 = none. Read about once a second, not every look.
/// </summary>
public sealed record TileTable(int Width, int Height, sbyte[] Owner, sbyte[] Resource, sbyte[] Improvement, sbyte[] Route, bool[] ImprovementPillaged, bool[] RoutePillaged, long TimeMs)
{
    /// <summary>Number of tiles.</summary>
    public int Count => Width * Height;
}
