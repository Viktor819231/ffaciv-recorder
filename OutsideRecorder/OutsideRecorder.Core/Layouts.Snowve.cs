namespace OutsideRecorderCore;

/// <summary>
/// Snowve (gitlab.com/megawac1/snowve, an NQMod-based cousin of LEKMOD, installed as a DLC folder "MP ModsPack").
/// Its version text in the game is stuck at "8.0.1", so each release is known only by its DLL's SHA-256.
/// Every value: read from the release's own PDB (GUID + age match the DLL), then confirmed in the DLL's machine code
/// (layouts/evidence-snowve-*.txt, tools/BinaryVerify/verify.sh + verify-extra.sh). Unlike LEKMOD, the PDBs hold no stale
/// second layout (the duplicate CvUnit/CvCity/CvGame records are identical; SnowveLayoutTests checks every record).
/// </summary>
public static class SnowveLayouts
{
    /// <summary>
    /// Snowve 8.2.0 (git tag 8.2.0, 2026-10-14). Evidence: layouts/evidence-snowve-8.2.0.txt.
    /// CvUnit differs from LEKMOD after m_iMoves (+0xC); CvPlayerAI is 0xF7F4 bytes, units list at +0x1260.
    /// </summary>
    public static readonly GameCoreLayout Snowve820 = new()
    {
        Name = "Snowve 8.2.0",
        CityMicro = new(0xB88, 0x9C0, 0x1DC, 0x200, 0x5B4, 0x6A4),
        Id = "snowve-8.2.0",
        DllSha256 = "b37002a3dd67848e27ec1b0f8d1c474c2899076878681b0b31ad5db38a67a88b",
        PeTimeDateStamp = 0x6AD0016E,
        PeSizeOfImage = 0x723000,
        // Same NQMod autosave code as LEKMOD (CvGame.cpp: post-turn AutoSave(false, true) "just before first player
        // deactivation"); the site reads Snowve saves as end of turn. From source + saves, not yet seen live.
        AutosaveAtTurnStart = false,

        RvaGlobals = 0x3D5288,
        RvaPlayers = 0x3F6A44,
        RvaGameTurn = 0x3F6FC0,
        RvaGameStarted = 0x3F6FB4,
        RvaActivePlayer = 0x3F6B04,
        RvaGameType = 0x3F6FCC,
        RvaSlotStatusVector = 0x3F7490,
        RvaCivilizationsVector = 0x3F6D38,
        RvaMapSeed = 0x3F70FC,      // code: CvDllPreGame slot 70 -> "mov eax,ds:[103F70FCh]" (evidence "## game seeds")
        RvaSyncSeed = 0x3F74CC,     // code: CvDllPreGame slot 192 -> "mov eax,ds:[103F74CCh]"
        // the lobby (8.2.0, 8.2.1, 8.3.0 alike): PDB CvPreGame::s_* + m_value (SnowveLayoutTests.PreGameLobbyMatchesPdb)
        RvaWorldSize = 0x3F7808, RvaGameSpeed = 0x3F6FA8, RvaEra = 0x3F6EE4, RvaEndTurnTimerLength = 0x3F6F18, RvaTurnTimerType = 0x3F7638,
        RvaMapScriptName = 0x3F7120, RvaGameName = 0x3F6F84, RvaNicknamesVector = 0x3F722C, RvaLeaderHeadsVector = 0x3F7068,
        RvaTeamTypesVector = 0x3F74F4, RvaHandicapsVector = 0x3F7000, RvaGameOptionsVector = 0x3F7824,
        // tiles (all four releases alike; evidence-plots.txt): pillaged bits in byte 0x1CE
        MapPlots = 0xFE4, PlotSize = 0x1E4, PlotOwner = 0x4, PlotResource = 0x1B8, PlotImprovement = 0x1B9, PlotRoute = 0x1BF,
        PlotPillagedByte = 0x1CE, PlotImprovementPillagedBit = 0, PlotRoutePillagedBit = 1,
        GameTurnTimer = 0x18,   // CvDllGame slot 83 -> GetTurnTimerData reads [this+18h]/[this+1Ch] (all four releases; PDB m_curTurnTimer)

        VtGlobals = 0x3515A8,
        VtPlayerAI = 0x362D7C,
        VtUnitArchive = 0x3738E4,
        VtCityAI = 0x333940,
        VtMap = 0x3590F8,

        GlobalsGame = 0x30,
        GlobalsMap = 0x34,
        GlobalsUnitInfos = 0x6A8,   // code: getUnitInfo() "mov ecx,[ecx+6A8h]"
        GlobalsCivInfos = 0x4CC,    // code: getCivilizationInfo(i) reads [ecx+4D0h]/[ecx+4D4h] (= vector _Myfirst/_Mylast)
        InfoType = 0xB0,            // code: CvBaseInfo::CacheResults stores column "Type" at [esi+0B0h]
        MapWidth = 0xFB4,
        MapHeight = 0xFB8,

        PlayerSize = 0xF7F4,
        PlayerId = 0x2C,
        PlayerAlive = 0x95C,
        PlayerTurnActive = 0x978,
        PlayerCities = 0x1244,
        PlayerUnits = 0x1260,
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
        UnitExperience = 0xF0,
        UnitLevel = 0xFC,
        UnitFortifyTurns = 0x15C,
        UnitEmbarked = 0x684,
        UnitActivity = 0x938,

        CityHeadBytes = 0x3E0,
        CityOwner = 0x54,
        CityX = 0x60,
        CityY = 0x6C,
        CityId = 0x78,
        CityTurnFounded = 0x9C,
        CityPopulation = 0xC0,
        CityDamage = 0x2A4,
        CityOccupied = 0x38C,
        CityPuppet = 0x398,
        CityOriginalOwner = 0x3D8,
        CityName = 0x9A0,
    };

    /// <summary>
    /// Snowve 8.2.1 (git tag 8.2.1, 2026-10-24). Same as 8.2.0 except: CvPlayer grew by 4 bytes before m_bAlive
    /// (player 0xF7F8, alive +0x960, units list +0x1264) and the vtables moved. Evidence: layouts/evidence-snowve-8.2.1.txt.
    /// </summary>
    public static readonly GameCoreLayout Snowve821 = Snowve820 with
    {
        Name = "Snowve 8.2.1",
        Id = "snowve-8.2.1",
        DllSha256 = "369584b78d31e8ed4f92870101c288f0496f2b15350b9461c44387f2178d171c",
        PeTimeDateStamp = 0x6ADAE029,
        VtGlobals = 0x351598,
        VtPlayerAI = 0x362D8C,
        VtUnitArchive = 0x3738F4,
        VtMap = 0x3590E8,
        PlayerSize = 0xF7F8,
        PlayerAlive = 0x960,
        PlayerTurnActive = 0x97C,
        PlayerCities = 0x1248,
        PlayerUnits = 0x1264,
    };

    /// <summary>Snowve 8.3.0 (git tag 8.3.0, 2026-10-25, newest): same layout as 8.2.1, new DLL. Evidence: layouts/evidence-snowve-8.3.0.txt.</summary>
    public static readonly GameCoreLayout Snowve830 = Snowve821 with
    {
        Name = "Snowve 8.3.0",
        Id = "snowve-8.3.0",
        DllSha256 = "32892137b1e76dbf7a5d1205a701630e05e2b3c2025432273a356226e6812d83",
        PeTimeDateStamp = 0x6ADEE23C,
    };

    /// <summary>
    /// Snowve 8.1.1 (git tag 8.1.1, 2026-10-09). Objects as 8.2.0; globals and vtables elsewhere.
    /// Its PDB's publics table cannot be read by our PDB library, so the three info-table offsets (names only) are from the
    /// PDB's type records alone, like LEKMOD's; a wrong one gives no "UNIT_"/"CIVILIZATION_" name, never a wrong name.
    /// </summary>
    public static readonly GameCoreLayout Snowve811 = Snowve820 with
    {
        Name = "Snowve 8.1.1",
        Id = "snowve-8.1.1",
        DllSha256 = "bfe5f287897aa2a8aa2b6a470aa0dcda30d06bd1d3a2cf0d0852f03a1c88cf44",
        PeTimeDateStamp = 0x6AC2FE34,
        PeSizeOfImage = 0x721000,
        RvaGlobals = 0x3D3288,
        RvaPlayers = 0x3F4A44,
        RvaGameTurn = 0x3F4FC0,
        RvaGameStarted = 0x3F4FB4,
        RvaActivePlayer = 0x3F4B04,
        RvaGameType = 0x3F4FCC,
        RvaSlotStatusVector = 0x3F5490,
        RvaCivilizationsVector = 0x3F4D38,
        RvaMapSeed = 0x3F50FC,
        RvaSyncSeed = 0x3F54CC,
        RvaWorldSize = 0x3F5808, RvaGameSpeed = 0x3F4FA8, RvaEra = 0x3F4EE4, RvaEndTurnTimerLength = 0x3F4F18, RvaTurnTimerType = 0x3F5638,
        RvaMapScriptName = 0x3F5120, RvaGameName = 0x3F4F84, RvaNicknamesVector = 0x3F522C, RvaLeaderHeadsVector = 0x3F5068,
        RvaTeamTypesVector = 0x3F54F4, RvaHandicapsVector = 0x3F5000, RvaGameOptionsVector = 0x3F5824,
        VtGlobals = 0x34F590,
        VtPlayerAI = 0x360D1C,
        VtUnitArchive = 0x371874,
        VtCityAI = 0x331960,
        VtMap = 0x3570E0,
    };

    /// <summary>All Snowve layouts, newest first.</summary>
    public static readonly IReadOnlyList<GameCoreLayout> All = [Snowve830, Snowve821, Snowve820, Snowve811];
}
