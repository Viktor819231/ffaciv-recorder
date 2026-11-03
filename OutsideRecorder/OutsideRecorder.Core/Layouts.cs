namespace OutsideRecorderCore;

/// <summary>
/// Where the game core keeps the values we read, for ONE exact game core DLL (matched by SHA-256).
/// Every number is confirmed from that DLL's own machine code (OutsideRecorder/layouts/evidence-*.txt, made by
/// tools/BinaryVerify): Lua method -> getter -> [this+offset], RTTI vtables, the GET_PLAYER array, the CvGlobals
/// constructor. A value that could not be confirmed is null and is not read. An unknown DLL gets no layout at all.
/// </summary>
public sealed record GameCoreLayout
{
    /// <summary>Verified optional city-management fields; null for a layout without evidence.</summary>
    public CityMicroLayout? CityMicro { get; init; }
    /// <summary>Human name of the game / mod release.</summary>
    public required string Name { get; init; }
    /// <summary>Short id written into recordings ("bnw-1.0.3.279", "lekmod-35.4.003").</summary>
    public required string Id { get; init; }
    /// <summary>SHA-256 of CvGameCore_Expansion2.dll on disk (lowercase hex).</summary>
    public required string DllSha256 { get; init; }
    /// <summary>PE TimeDateStamp of the loaded image (checked in memory).</summary>
    public required uint PeTimeDateStamp { get; init; }
    /// <summary>PE SizeOfImage of the loaded image.</summary>
    public required uint PeSizeOfImage { get; init; }
    /// <summary>
    /// True when this game writes its autosave at the START of a turn (stock BNW: AutoSave_0013 = turn 13 before anyone moved);
    /// false = at the END of the turn (Lekmod/KEK: AutoSave_0013 = turn 13 after everyone moved).
    /// </summary>
    public required bool AutosaveAtTurnStart { get; init; }

    // ---- globals (RVA = offset from the DLL's load address) ----
    /// <summary>gGlobals (CvGlobals object).</summary>
    public required uint RvaGlobals { get; init; }
    /// <summary>CvPlayerAI::m_aPlayers (pointer to CvPlayerAI[MAX_PLAYERS]).</summary>
    public required uint RvaPlayers { get; init; }
    /// <summary>CvPreGame game turn value (int).</summary>
    public required uint RvaGameTurn { get; init; }
    /// <summary>CvPreGame "game started" value (bool); null = not confirmed.</summary>
    public uint? RvaGameStarted { get; init; }
    /// <summary>CvPreGame active (local) player value.</summary>
    public required uint RvaActivePlayer { get; init; }
    /// <summary>CvPreGame game type value (GameTypes: -1 none, 0 single player, 1 network multiplayer, 2 hot seat, 3 e-mail).</summary>
    public uint? RvaGameType { get; init; }
    /// <summary>CvPreGame slot status std::vector&lt;SlotStatus&gt; (the vector object; _Myfirst at +4).</summary>
    public uint? RvaSlotStatusVector { get; init; }
    /// <summary>CvPreGame civilizations std::vector&lt;CivilizationTypes&gt; (the vector object; _Myfirst at +4).</summary>
    public uint? RvaCivilizationsVector { get; init; }
    /// <summary>CvPreGame map random seed (uint): set when the game is created, kept through reloads and rehosts.</summary>
    public uint? RvaMapSeed { get; init; }
    /// <summary>CvPreGame sync random seed (uint): with the map seed, the game's identity (the converter joins saves by the same two).</summary>
    public uint? RvaSyncSeed { get; init; }


    // ---- the staging room / lobby (CvPreGame, readable before a game starts; card OR03 "Lobby / channel") ----
    // Proven like the seeds: Lekmod and Snowve from their PDBs (CvPreGame::s_* + FAutoVariable m_value), stock BNW from
    // the CvDllPreGame interface the exe calls (slot numbers named from the Snowve PDB; layouts/evidence-bnw-1.0.3.279-pregame.txt).
    /// <summary>CvPreGame world size (WorldSizeTypes, int); slot 197.</summary>
    public uint? RvaWorldSize { get; init; }
    /// <summary>CvPreGame game speed (GameSpeedTypes, int); slot 40.</summary>
    public uint? RvaGameSpeed { get; init; }
    /// <summary>CvPreGame start era (EraTypes, int); slot 27.</summary>
    public uint? RvaEra { get; init; }
    /// <summary>CvPreGame end-turn timer length (float, as the game set it); slot 26.</summary>
    public uint? RvaEndTurnTimerLength { get; init; }
    /// <summary>CvPreGame turn timer type (TurnTimerTypes, int); slot 195.</summary>
    public uint? RvaTurnTimerType { get; init; }
    /// <summary>CvPreGame map script name (the CvString object); slot 73.</summary>
    public uint? RvaMapScriptName { get; init; }
    /// <summary>CvPreGame game (lobby) name (the CvString object); slot 30.</summary>
    public uint? RvaGameName { get; init; }
    /// <summary>CvPreGame nicknames std::vector&lt;CvString&gt; (vector object; 28-byte elements); slot 79.</summary>
    public uint? RvaNicknamesVector { get; init; }
    /// <summary>CvPreGame leader heads std::vector&lt;LeaderHeadTypes&gt; (vector object); slot 61.</summary>
    public uint? RvaLeaderHeadsVector { get; init; }
    /// <summary>CvPreGame team types std::vector&lt;TeamTypes&gt; (vector object); slot 194.</summary>
    public uint? RvaTeamTypesVector { get; init; }
    /// <summary>CvPreGame handicaps std::vector&lt;HandicapTypes&gt; (vector object); slot 44.</summary>
    public uint? RvaHandicapsVector { get; init; }
    /// <summary>CvPreGame game options std::vector&lt;CustomOption&gt; (vector object; 0x44-byte elements: char[64] name, int value); slot 32.</summary>
    public uint? RvaGameOptionsVector { get; init; }

    /// <summary>
    /// CvGame::m_curTurnTimer: the running turn's timer, its first 8 bytes the QueryPerformanceCounter value when it started
    /// (CvDllGame slot 83 GetTurnTimerData; layouts/evidence-turntimer.txt). QPC is one clock for the whole PC, so the
    /// recorder can tell how long the turn has run. The turn's limit itself is computed each time (CvGame::getMaxTurnLen),
    /// not stored: not read.
    /// </summary>
    public int? GameTurnTimer { get; init; }

    // ---- the tiles (CvMap::m_pMapPlots, one array of CvPlot; card OR03 "Tiles"): BNW + Snowve from their code
    //      (layouts/evidence-plots.txt), Lekmod from its PDB. Unproven = null = tiles not read. ----
    /// <summary>CvMap::m_pMapPlots (CvPlot*, width × height plots in one array).</summary>
    public int? MapPlots { get; init; }
    /// <summary>sizeof(CvPlot) (GetPlotByIndex: index × size + m_pMapPlots).</summary>
    public int? PlotSize { get; init; }
    /// <summary>CvPlot::m_iX / m_iY (int16 at +0 / +2), checked on every read plot.</summary>
    public int PlotX { get; init; } = 0;
    /// <summary>CvPlot::m_iY.</summary>
    public int PlotY { get; init; } = 2;
    /// <summary>CvPlot::m_eOwner (signed byte, -1 = nobody).</summary>
    public int? PlotOwner { get; init; }
    /// <summary>CvPlot::m_eResourceType (signed byte; what is there, not who can see it).</summary>
    public int? PlotResource { get; init; }
    /// <summary>CvPlot::m_eImprovementType (signed byte).</summary>
    public int? PlotImprovement { get; init; }
    /// <summary>CvPlot::m_eRouteType (signed byte).</summary>
    public int? PlotRoute { get; init; }
    /// <summary>The byte holding the pillaged bits.</summary>
    public int? PlotPillagedByte { get; init; }
    /// <summary>Bit of m_bImprovementPillaged in that byte.</summary>
    public int PlotImprovementPillagedBit { get; init; }
    /// <summary>Bit of m_bRoutePillaged in that byte.</summary>
    public int PlotRoutePillagedBit { get; init; } = 1;

    // ---- vtables (RVA, from RTTI): an object whose first word is not its class's vtable is not read ----
    /// <summary>CvGlobals vtable.</summary>
    public required uint VtGlobals { get; init; }
    /// <summary>CvPlayerAI vtable.</summary>
    public required uint VtPlayerAI { get; init; }
    /// <summary>FAutoArchiveClassContainer&lt;CvUnit&gt; vtable (the first member of every CvUnit).</summary>
    public required uint VtUnitArchive { get; init; }
    /// <summary>CvCityAI vtable.</summary>
    public required uint VtCityAI { get; init; }
    /// <summary>CvMap vtable.</summary>
    public required uint VtMap { get; init; }

    // ---- CvGlobals ----
    /// <summary>CvGlobals::m_game (CvGame*).</summary>
    public required int GlobalsGame { get; init; }
    /// <summary>CvGlobals::m_map (CvMap*).</summary>
    public required int GlobalsMap { get; init; }
    /// <summary>CvGlobals::m_pUnits (CvUnitXMLEntries*); null = unit type names not read.</summary>
    public int? GlobalsUnitInfos { get; init; }
    /// <summary>CvGlobals::m_paCivilizationInfo (std::vector); null = civ names not read.</summary>
    public int? GlobalsCivInfos { get; init; }
    /// <summary>CvBaseInfo::m_strType (CvString), e.g. "UNIT_WARRIOR".</summary>
    public int? InfoType { get; init; }

    // ---- CvMap ----
    /// <summary>CvMap::m_iGridWidth.</summary>
    public required int MapWidth { get; init; }
    /// <summary>CvMap::m_iGridHeight.</summary>
    public required int MapHeight { get; init; }

    // ---- CvPlayer (array stride = sizeof(CvPlayerAI)) ----
    /// <summary>sizeof(CvPlayerAI).</summary>
    public required int PlayerSize { get; init; }
    /// <summary>m_eID value.</summary>
    public required int PlayerId { get; init; }
    /// <summary>m_bAlive value.</summary>
    public required int PlayerAlive { get; init; }
    /// <summary>m_bTurnActive value.</summary>
    public required int PlayerTurnActive { get; init; }
    /// <summary>m_cities (FFreeListTrashArray&lt;CvCityAI&gt;).</summary>
    public required int PlayerCities { get; init; }
    /// <summary>m_units (FFreeListTrashArray&lt;CvUnit&gt;).</summary>
    public required int PlayerUnits { get; init; }

    // ---- FFreeListTrashArray<T> (same in both: GetAt reads [this+0x18] array, [this+0xC] last index, node = 8 bytes) ----
    /// <summary>m_iLastIndex.</summary>
    public int ListLastIndex { get; init; } = 0x0C;
    /// <summary>m_iNumSlots (not read by GetAt; only used as an upper sanity bound when present).</summary>
    public int? ListNumSlots { get; init; }
    /// <summary>m_pArray.</summary>
    public int ListArray { get; init; } = 0x18;

    // ---- CvUnit ----
    /// <summary>One read of this many bytes from the unit start covers the int fields below.</summary>
    public int UnitHeadBytes { get; init; } = 0x160;
    /// <summary>m_eOwner value.</summary>
    public required int UnitOwner { get; init; }
    /// <summary>m_eUnitType value.</summary>
    public required int UnitType { get; init; }
    /// <summary>m_iX value.</summary>
    public required int UnitX { get; init; }
    /// <summary>m_iY value.</summary>
    public required int UnitY { get; init; }
    /// <summary>m_iID value.</summary>
    public required int UnitId { get; init; }
    /// <summary>m_iLastMoveTurn.</summary>
    public required int UnitLastMoveTurn { get; init; }
    /// <summary>m_iGameTurnCreated value.</summary>
    public required int UnitTurnCreated { get; init; }
    /// <summary>m_iDamage value (0..100).</summary>
    public required int UnitDamage { get; init; }
    /// <summary>m_iMoves value (moves left, 60ths).</summary>
    public required int UnitMoves { get; init; }
    /// <summary>m_iExperience value.</summary>
    public required int UnitExperience { get; init; }
    /// <summary>m_iLevel value.</summary>
    public required int UnitLevel { get; init; }
    /// <summary>m_iFortifyTurns value (&gt; 0 = fortified).</summary>
    public required int UnitFortifyTurns { get; init; }
    /// <summary>m_bEmbarked value (read separately).</summary>
    public int? UnitEmbarked { get; init; }
    /// <summary>m_eActivityType value (read separately; 0 awake, 1 hold, 2 sleep, 3 heal, 4 sentry, 5 intercept, 6 mission).</summary>
    public int? UnitActivity { get; init; }

    // ---- CvCity (CvCityAI starts with CvCity) ----
    /// <summary>One read of this many bytes from the city start covers the fields below except the name.</summary>
    public required int CityHeadBytes { get; init; }
    /// <summary>m_eOwner value.</summary>
    public required int CityOwner { get; init; }
    /// <summary>m_iX value.</summary>
    public required int CityX { get; init; }
    /// <summary>m_iY value.</summary>
    public required int CityY { get; init; }
    /// <summary>m_iID value.</summary>
    public required int CityId { get; init; }
    /// <summary>m_iGameTurnFounded value.</summary>
    public required int CityTurnFounded { get; init; }
    /// <summary>m_iPopulation value.</summary>
    public required int CityPopulation { get; init; }
    /// <summary>m_iDamage value.</summary>
    public required int CityDamage { get; init; }
    /// <summary>m_bOccupied value.</summary>
    public required int CityOccupied { get; init; }
    /// <summary>m_bPuppet value.</summary>
    public required int CityPuppet { get; init; }
    /// <summary>m_eOriginalOwner value.</summary>
    public required int CityOriginalOwner { get; init; }
    /// <summary>m_strName (CvString); null = not read.</summary>
    public int? CityName { get; init; }

    /// <summary>MAX_PLAYERS (63 civs + barbarians).</summary>
    public int MaxPlayers { get; init; } = 64;
}

/// <summary>The layouts this build knows. Add one only with binary evidence (tools/BinaryVerify/verify.sh), never by guessing.</summary>
public static class KnownLayouts
{
    /// <summary>
    /// LEKMOD v35.4.003 (Assets/DLC/LEKMOD_v35.4). Evidence: layouts/evidence-lekmod-35.4.003.txt.
    /// Note: its PDB holds TWO layouts of CvUnit/CvPlayer; the code uses the second (bigger) one
    /// (player 0xF8A8 bytes, units list +0x1318); the first PDB record was stale.
    /// </summary>
    public static readonly GameCoreLayout Lekmod354 = new()
    {
        Name = "LEKMOD v35.4.003",
        CityMicro = new(0xBA4, 0x9C4, 0x188, 0x1B8, 0x5E4, 0x6D4),
        Id = "lekmod-35.4.003",
        DllSha256 = "a5ac79567eff7c738f12c655457d2255199ef5da1e9e43aeb0e2b688b9161fd2",
        PeTimeDateStamp = 0x6ABD3599,
        PeSizeOfImage = 0x753000,
        AutosaveAtTurnStart = false,

        RvaGlobals = 0x403388,
        RvaPlayers = 0x42564C,
        RvaGameTurn = 0x425BC8,
        RvaGameStarted = 0x425BBC,
        RvaActivePlayer = 0x42570C,
        RvaGameType = 0x425BD4,
        RvaSlotStatusVector = 0x426098,
        RvaCivilizationsVector = 0x425940,
        RvaMapSeed = 0x425D04,
        RvaSyncSeed = 0x4260D4,
        // the lobby: PDB CvPreGame::s_* + m_value (LayoutPdbTests.PreGameLobbyMatchesPdb)
        RvaWorldSize = 0x426410, RvaGameSpeed = 0x425BB0, RvaEra = 0x425AEC, RvaEndTurnTimerLength = 0x425B20, RvaTurnTimerType = 0x426240,
        RvaMapScriptName = 0x425D28, RvaGameName = 0x425B8C, RvaNicknamesVector = 0x425E34, RvaLeaderHeadsVector = 0x425C70,
        RvaTeamTypesVector = 0x4260FC, RvaHandicapsVector = 0x425C08, RvaGameOptionsVector = 0x42642C,
        // tiles: PDB CvMap::m_pMapPlots, sizeof(CvPlot), CvPlot fields (LobbyTests.LekmodPlotsMatchItsPdb); pillaged bits 1 and 2
        MapPlots = 0xFE4, PlotSize = 0x1E4, PlotOwner = 0x4, PlotResource = 0x1B8, PlotImprovement = 0x1B9, PlotRoute = 0x1BF,
        PlotPillagedByte = 0x1CD, PlotImprovementPillagedBit = 1, PlotRoutePillagedBit = 2,
        GameTurnTimer = 0x18,   // PDB CvGame::m_curTurnTimer

        VtGlobals = 0x377A64,
        VtPlayerAI = 0x38AD4C,
        VtUnitArchive = 0x39FD04,
        VtCityAI = 0x359890,
        VtMap = 0x380144,

        GlobalsGame = 0x30,
        GlobalsMap = 0x34,
        GlobalsUnitInfos = 0x6D8,   // PDB only (both PDB records agree); a wrong value would give no "UNIT_" name, never a wrong one
        GlobalsCivInfos = 0x4EC,    // PDB only, same safety check ("CIVILIZATION_")
        InfoType = 0xB0,
        MapWidth = 0xFB4,
        MapHeight = 0xFB8,

        PlayerSize = 0xF8A8,
        PlayerId = 0x2C,
        PlayerAlive = 0x960,
        PlayerTurnActive = 0x97C,
        PlayerCities = 0x12FC,
        PlayerUnits = 0x1318,
        ListNumSlots = 0x10,

        UnitOwner = 0x28,
        UnitType = 0x40,
        UnitX = 0x4C,
        UnitY = 0x58,
        UnitId = 0x64,
        UnitLastMoveTurn = 0x84,
        UnitTurnCreated = 0xB4,
        UnitDamage = 0xC0,
        UnitMoves = 0xCC,
        UnitExperience = 0xE4,
        UnitLevel = 0xF0,
        UnitFortifyTurns = 0x150,
        UnitEmbarked = 0x70C,
        UnitActivity = 0xA3C,

        CityHeadBytes = 0x3A0,
        CityOwner = 0x54,
        CityX = 0x60,
        CityY = 0x6C,
        CityId = 0x78,
        CityTurnFounded = 0x9C,
        CityPopulation = 0xC0,
        CityDamage = 0x25C,
        CityOccupied = 0x350,
        CityPuppet = 0x35C,
        CityOriginalOwner = 0x39C,
        CityName = 0x9A4,
    };

    /// <summary>
    /// Stock Civ V Brave New World 1.0.3.279 (no mods; Assets/DLC/Expansion2). No PDB exists: every value is from the
    /// machine code, evidence in layouts/evidence-bnw-1.0.3.279.txt. Unit type and civ names are not read (not confirmed).
    /// </summary>
    public static readonly GameCoreLayout Bnw103279 = new()
    {
        Name = "Civ V BNW 1.0.3.279 (no mods)",
        CityMicro = new(0xB38, 0x980, 0x1D0, 0x1F4, 0x5B4, 0x6A4),
        Id = "bnw-1.0.3.279",
        DllSha256 = "c5483b5857a6fb7ef107af93c5d34282eb713472e2a84d0eb9d3fdc104e155a5",
        PeTimeDateStamp = 0x546CC70C,
        PeSizeOfImage = 0x709000,
        AutosaveAtTurnStart = true,

        RvaGlobals = 0x3BD250,
        RvaPlayers = 0x3DE9DC,
        RvaGameTurn = 0x3DEF58,
        RvaGameStarted = 0x3DEF4C,
        RvaActivePlayer = 0x3DEA9C,
        RvaGameType = 0x3DEF64,
        RvaSlotStatusVector = 0x3DF428,
        RvaCivilizationsVector = 0x3DECD0,
        RvaMapSeed = 0x3DF094,
        RvaSyncSeed = 0x3DF464,
        // the lobby: CvDllPreGame slots (evidence-bnw-1.0.3.279-pregame.txt): "mov eax,ds:[…]" / "mov eax,offset" / "[myfirst]" = vector + 4;
        // team types through the pointer at 0x3A64DC (= 0x103DF48C)
        RvaWorldSize = 0x3DF7A0, RvaGameSpeed = 0x3DEF40, RvaEra = 0x3DEE7C, RvaEndTurnTimerLength = 0x3DEEB0, RvaTurnTimerType = 0x3DF5D0,
        RvaMapScriptName = 0x3DF0B8, RvaGameName = 0x3DEF1C, RvaNicknamesVector = 0x3DF1C4, RvaLeaderHeadsVector = 0x3DF000,
        RvaTeamTypesVector = 0x3DF48C, RvaHandicapsVector = 0x3DEF98, RvaGameOptionsVector = 0x3DF7BC,
        // tiles (evidence-plots.txt): GetPlotByIndex "imul eax,1E4h; add eax,[ecx+0FE4h]"; Plot getters
        MapPlots = 0xFE4, PlotSize = 0x1E4, PlotOwner = 0x4, PlotResource = 0x1B8, PlotImprovement = 0x1B9, PlotRoute = 0x1BF,
        PlotPillagedByte = 0x1CD, PlotImprovementPillagedBit = 0, PlotRoutePillagedBit = 1,
        GameTurnTimer = 0x14D0,   // CvDllGame slot 83 -> CvGame::GetTurnTimerData reads [this+14D0h]/[this+14D4h]

        VtGlobals = 0x3380D8,
        VtPlayerAI = 0x3496CC,
        VtUnitArchive = 0x3592C0,
        VtCityAI = 0x31A0FC,
        VtMap = 0x33FE0C,

        GlobalsGame = 0x30,
        GlobalsMap = 0x34,
        MapWidth = 0xFB4,
        MapHeight = 0xFB8,

        PlayerSize = 0xF5B4,
        PlayerId = 0x2C,
        PlayerAlive = 0x868,
        PlayerTurnActive = 0x884,
        PlayerCities = 0x100C,
        PlayerUnits = 0x1028,

        UnitOwner = 0x28,
        UnitType = 0x40,
        UnitX = 0x4C,
        UnitY = 0x58,
        UnitId = 0x64,
        UnitLastMoveTurn = 0x84,
        UnitTurnCreated = 0xB4,
        UnitDamage = 0xC0,
        UnitMoves = 0xCC,
        UnitExperience = 0xE4,
        UnitLevel = 0xF0,
        UnitFortifyTurns = 0x150,
        UnitEmbarked = 0x62C,
        UnitActivity = 0x8E0,

        CityHeadBytes = 0x3D0,
        CityOwner = 0x54,
        CityX = 0x60,
        CityY = 0x6C,
        CityId = 0x78,
        CityTurnFounded = 0x9C,
        CityPopulation = 0xC0,
        CityDamage = 0x298,
        CityOccupied = 0x380,
        CityPuppet = 0x38C,
        CityOriginalOwner = 0x3CC,
        CityName = 0x960,
    };

    /// <summary>All layouts, looked up by DLL SHA-256.</summary>
    public static readonly IReadOnlyList<GameCoreLayout> All = [Lekmod354, Bnw103279, .. SnowveLayouts.All];

    /// <summary>The layout for this exact DLL, or null (then nothing is read).</summary>
    public static GameCoreLayout? BySha256(string sha256) =>
        All.FirstOrDefault(l => string.Equals(l.DllSha256, sha256, StringComparison.OrdinalIgnoreCase));
}
