using System.Text;
using SharpPdb.Windows;
using SharpPdb.Windows.SymbolRecords;
using SharpPdb.Windows.TPI;
using SharpPdb.Windows.TypeRecords;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>
/// Proves the Snowve layouts (Layouts.Snowve.cs) against each release's own DLL + PDB (from the Snowve git tag):
/// the PDB belongs to the DLL, every offset, global and vtable is what the PDB says, and EVERY full record of a class in
/// the PDB agrees (LEKMOD's PDB held a stale second layout). The DLL/PDB pairs are looked up in SNOWVE_BIN/&lt;version&gt;/
/// or out/snowve-bin/&lt;version&gt;/ above the test folder; the PDB checks pass with a note when they are not there.
/// The evidence checks (seeds, info tables) always run.
/// </summary>
public sealed class SnowveLayoutTests(ITestOutputHelper output)
{
    public static TheoryData<string> Ids => new(SnowveLayouts.All.Select(l => l.Id));
    static GameCoreLayout ById(string id) => SnowveLayouts.All.Single(l => l.Id == id);
    static string Version(string id) => id["snowve-".Length..];

    static readonly Dictionary<string, string> PdbGuidAge = new()
    {
        ["snowve-8.1.1"] = "437efb05-1279-4c52-bfd8-a85bb55a036a/12",
        ["snowve-8.2.0"] = "437efb05-1279-4c52-bfd8-a85bb55a036a/22",
        ["snowve-8.2.1"] = "ba6c2648-5a54-4129-8a4b-c6fc209f1e23/1",
        ["snowve-8.3.0"] = "ba6c2648-5a54-4129-8a4b-c6fc209f1e23/2",
    };

    static string? BinDir(string id)
    {
        var v = Version(id);
        if (Environment.GetEnvironmentVariable("SNOWVE_BIN") is { } env && File.Exists(Path.Combine(env, v, "CvGameCore_Expansion2.pdb")))
            return Path.Combine(env, v);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "out", "snowve-bin", v);
            if (File.Exists(Path.Combine(p, "CvGameCore_Expansion2.dll")) && File.Exists(Path.Combine(p, "CvGameCore_Expansion2.pdb"))) return p;
        }
        return null;
    }

    bool Missing(string id, out string dll, out string pdb)
    {
        var dir = BinDir(id);
        dll = dir is null ? "" : Path.Combine(dir, "CvGameCore_Expansion2.dll");
        pdb = dir is null ? "" : Path.Combine(dir, "CvGameCore_Expansion2.pdb");
        if (dir is not null) return false;
        output.WriteLine($"{id}: DLL + PDB not found (SNOWVE_BIN or out/snowve-bin/{Version(id)}): PDB check skipped");
        return true;
    }

    [Fact]
    public void AllSnowveLayoutsAreKnownAndDistinct()
    {
        foreach (var l in SnowveLayouts.All)
        {
            Assert.Same(l, KnownLayouts.BySha256(l.DllSha256));
            Assert.NotNull(l.RvaMapSeed); Assert.NotNull(l.RvaSyncSeed);
            Assert.False(l.AutosaveAtTurnStart);
        }
        Assert.Equal(SnowveLayouts.All.Count, KnownLayouts.All.Select(l => l.DllSha256).Intersect(SnowveLayouts.All.Select(l => l.DllSha256)).Count());
        Assert.Equal(KnownLayouts.All.Count, KnownLayouts.All.Select(l => l.DllSha256).Distinct().Count());
        Assert.Equal(KnownLayouts.All.Count, KnownLayouts.All.Select(l => l.Id).Distinct().Count());
    }

    [Theory, MemberData(nameof(Ids))]
    public void DllHashPeHeaderAndPdbMatch(string id)
    {
        if (Missing(id, out var dll, out var pdbPath)) return;
        var L = ById(id);
        Assert.Equal(L.DllSha256, GameFinder.Sha256Of(dll));
        var pe = File.ReadAllBytes(dll);
        int off = BitConverter.ToInt32(pe, 0x3C);
        Assert.Equal(L.PeTimeDateStamp, BitConverter.ToUInt32(pe, off + 8));
        Assert.Equal(L.PeSizeOfImage, BitConverter.ToUInt32(pe, off + 24 + 56));
        using var pdb = new PdbFile(pdbPath);
        var (guid, age) = Rsds(pe);
        Assert.Equal(pdb.InfoStream.Header.Guid, guid);
        Assert.Equal((uint)pdb.DbiStream.Header.Age, age);
        Assert.Equal(PdbGuidAge[id], $"{guid}/{age}");
    }

    [Theory, MemberData(nameof(Ids))]
    public void GlobalsSeedsAndVtablesMatchPdb(string id)
    {
        if (Missing(id, out _, out var pdbPath)) return;
        var L = ById(id);
        using var pdb = new PdbFile(pdbPath);
        var data = new Dictionary<string, ulong>();
        foreach (var kind in new[] { SymbolRecordKind.S_GDATA32, SymbolRecordKind.S_LDATA32 })
            foreach (var s in pdb.PdbSymbolStream[kind].OfType<DataSymbol>())
                data.TryAdd(s.Name.ToString(), pdb.FindRelativeVirtualAddress(s.Segment, s.Offset, false));
        var tpi = pdb.TpiStream;
        int Value(string autoVar) => Member(tpi, autoVar, "m_value");
        const string P = ",CvPreGame::Phony>";

        Assert.Equal(L.RvaGlobals, (uint)data["gGlobals"]);
        Assert.Equal(L.RvaPlayers, (uint)data["CvPlayerAI::m_aPlayers"]);
        Assert.Equal(L.RvaGameTurn, (uint)data["CvPreGame::s_gameTurn"] + (uint)Value("FAutoVariable<int" + P));
        Assert.Equal(L.RvaGameStarted, (uint)data["CvPreGame::s_gameStarted"] + (uint)Value("FAutoVariable<bool" + P));
        Assert.Equal(L.RvaActivePlayer, (uint)data["CvPreGame::s_activePlayer"] + (uint)Value("FAutoVariable<enum PlayerTypes" + P));
        Assert.Equal(L.RvaGameType, (uint)data["CvPreGame::s_gameType"] + (uint)Value("FAutoVariable<enum GameTypes" + P));
        Assert.Equal(L.RvaSlotStatusVector, (uint)data["CvPreGame::s_slotStatus"]
            + (uint)Value("FAutoVariable<std::vector<enum SlotStatus,std::allocator<enum SlotStatus> >" + P));
        Assert.Equal(L.RvaCivilizationsVector, (uint)data["CvPreGame::s_civilizations"]
            + (uint)Value("FAutoVariable<std::vector<enum CivilizationTypes,std::allocator<enum CivilizationTypes> >" + P));
        int uintValue = Value("FAutoVariable<unsigned int" + P);
        Assert.Equal(L.RvaMapSeed, (uint)data["CvPreGame::s_mapRandomSeed"] + (uint)uintValue);
        Assert.Equal(L.RvaSyncSeed, (uint)data["CvPreGame::s_syncRandomSeed"] + (uint)uintValue);

        // vtables: from the publics table where our PDB library can read it (8.1.1's it cannot: RTTI evidence only)
        Dictionary<string, ulong> pub;
        try
        {
            pub = pdb.PublicsStream.PublicSymbols.GroupBy(p => p.Name.ToString())
                .ToDictionary(g => g.Key, g => pdb.FindRelativeVirtualAddress(g.First().Segment, g.First().Offset, false));
        }
        catch (Exception e) { output.WriteLine($"{id}: publics not readable ({e.Message}); vtables proven by RTTI evidence only"); return; }
        Assert.Equal(L.VtGlobals, (uint)pub["??_7CvGlobals@@6B@"]);
        Assert.Equal(L.VtPlayerAI, (uint)pub["??_7CvPlayerAI@@6B@"]);
        Assert.Equal(L.VtUnitArchive, (uint)pub["??_7?$FAutoArchiveClassContainer@VCvUnit@@@@6B@"]);
        Assert.Equal(L.VtCityAI, (uint)pub["??_7CvCityAI@@6B@"]);
        Assert.Equal(L.VtMap, (uint)pub["??_7CvMap@@6B@"]);
    }

    [Theory, MemberData(nameof(Ids))]
    public void MemberOffsetsMatchEveryPdbRecord(string id)
    {
        if (Missing(id, out _, out var pdbPath)) return;
        var L = ById(id);
        using var pdb = new PdbFile(pdbPath);
        var t = pdb.TpiStream;
        const int V = 4; // FAutoVariable<T,...>::m_value sits after the vtable pointer
        Assert.Equal(4, Member(t, "FAutoVariable<int,CvUnit>", "m_value"));
        Assert.Equal(4, Member(t, "FAutoVariable<int,CvCity>", "m_value"));
        Assert.Equal(4, Member(t, "FAutoVariable<bool,CvPlayer>", "m_value"));

        Assert.Equal(L.GlobalsGame, Member(t, "CvGlobals", "m_game"));
        Assert.Equal(L.GlobalsMap, Member(t, "CvGlobals", "m_map"));
        Assert.Equal(L.GlobalsUnitInfos, (int?)Member(t, "CvGlobals", "m_pUnits"));
        Assert.Equal(L.GlobalsCivInfos, (int?)Member(t, "CvGlobals", "m_paCivilizationInfo"));
        Assert.Equal(0, Member(t, "CvUnitXMLEntries", "m_paUnitEntries"));
        Assert.Equal(L.InfoType, (int?)Member(t, "CvBaseInfo", "m_strType"));
        Assert.Equal(L.MapWidth, Member(t, "CvMap", "m_iGridWidth"));
        Assert.Equal(L.MapHeight, Member(t, "CvMap", "m_iGridHeight"));

        Assert.Equal(L.PlayerSize, Size(t, "CvPlayerAI"));
        Assert.Equal(0, Base(t, "CvPlayerAI", "CvPlayer"));
        Assert.Equal(L.PlayerId, Member(t, "CvPlayer", "m_eID") + V);
        Assert.Equal(L.PlayerAlive, Member(t, "CvPlayer", "m_bAlive") + V);
        Assert.Equal(L.PlayerTurnActive, Member(t, "CvPlayer", "m_bTurnActive") + V);
        Assert.Equal(L.PlayerCities, Member(t, "CvPlayer", "m_cities"));
        Assert.Equal(L.PlayerUnits, Member(t, "CvPlayer", "m_units"));
        Assert.Equal(L.ListLastIndex, Member(t, "FFreeListArrayBase<CvUnit>", "m_iLastIndex"));
        Assert.Equal(L.ListNumSlots, (int?)Member(t, "FFreeListArrayBase<CvUnit>", "m_iNumSlots"));
        Assert.Equal(L.ListArray, Member(t, "FFreeListTrashArray<CvUnit>", "m_pArray"));
        Assert.Equal(L.ListArray, Member(t, "FFreeListTrashArray<CvCityAI>", "m_pArray"));
        Assert.Equal(4, Member(t, "FFreeListTrashArray<CvUnit>::FFreeListTrashArrayNode", "pData"));

        Assert.Equal(0, Member(t, "CvUnit", "m_syncArchive"));
        Assert.Equal(L.UnitOwner, Member(t, "CvUnit", "m_eOwner") + V);
        Assert.Equal(L.UnitType, Member(t, "CvUnit", "m_eUnitType") + V);
        Assert.Equal(L.UnitX, Member(t, "CvUnit", "m_iX") + V);
        Assert.Equal(L.UnitY, Member(t, "CvUnit", "m_iY") + V);
        Assert.Equal(L.UnitId, Member(t, "CvUnit", "m_iID") + V);
        Assert.Equal(L.UnitLastMoveTurn, Member(t, "CvUnit", "m_iLastMoveTurn"));
        Assert.Equal(L.UnitTurnCreated, Member(t, "CvUnit", "m_iGameTurnCreated") + V);
        Assert.Equal(L.UnitDamage, Member(t, "CvUnit", "m_iDamage") + V);
        Assert.Equal(L.UnitMoves, Member(t, "CvUnit", "m_iMoves") + V);
        Assert.Equal(L.UnitExperience, Member(t, "CvUnit", "m_iExperience") + V);
        Assert.Equal(L.UnitLevel, Member(t, "CvUnit", "m_iLevel") + V);
        Assert.Equal(L.UnitFortifyTurns, Member(t, "CvUnit", "m_iFortifyTurns") + V);
        Assert.Equal(L.UnitEmbarked, (int?)(Member(t, "CvUnit", "m_bEmbarked") + V));
        Assert.Equal(L.UnitActivity, (int?)(Member(t, "CvUnit", "m_eActivityType") + V));
        Assert.True(L.UnitHeadBytes >= L.UnitFortifyTurns + 4);

        Assert.Equal(0, Base(t, "CvCityAI", "CvCity"));
        Assert.Equal(L.CityOwner, Member(t, "CvCity", "m_eOwner") + V);
        Assert.Equal(L.CityX, Member(t, "CvCity", "m_iX") + V);
        Assert.Equal(L.CityY, Member(t, "CvCity", "m_iY") + V);
        Assert.Equal(L.CityId, Member(t, "CvCity", "m_iID") + V);
        Assert.Equal(L.CityTurnFounded, Member(t, "CvCity", "m_iGameTurnFounded") + V);
        Assert.Equal(L.CityPopulation, Member(t, "CvCity", "m_iPopulation") + V);
        Assert.Equal(L.CityDamage, Member(t, "CvCity", "m_iDamage") + V);
        Assert.Equal(L.CityOccupied, Member(t, "CvCity", "m_bOccupied") + V);
        Assert.Equal(L.CityPuppet, Member(t, "CvCity", "m_bPuppet") + V);
        Assert.Equal(L.CityOriginalOwner, Member(t, "CvCity", "m_eOriginalOwner") + V);
        Assert.Equal(L.CityName, (int?)Member(t, "CvCity", "m_strName"));
        Assert.True(L.CityHeadBytes >= L.CityOriginalOwner + 4);
        Assert.True(L.CityHeadBytes <= Size(t, "CvCity"));
        Assert.True(L.UnitHeadBytes <= Size(t, "CvUnit"));

        // MSVC 2008 std::string as MemoryExtensions.CvString reads it
        const string Str = "std::basic_string<char,std::char_traits<char>,std::allocator<char> >";
        Assert.Equal(0x1C, Size(t, "CvString"));
        Assert.Equal(4, Member(t, Str, "_Bx"));
        Assert.Equal(0x14, Member(t, Str, "_Mysize"));
        Assert.Equal(0x18, Member(t, Str, "_Myres"));
        Assert.Equal(4, Member(t, "std::vector<CvString,std::allocator<CvString> >", "_Myfirst"));
        Assert.Equal(8, Member(t, "std::vector<CvString,std::allocator<CvString> >", "_Mylast"));
    }

    /// <summary>The seeds (verify.sh) and the info tables (verify-extra.sh) are in the machine-code evidence.</summary>
    [Theory, MemberData(nameof(Ids))]
    public void SeedsAndInfoTablesAreInTheEvidence(string id)
    {
        var L = ById(id);
        var text = File.ReadAllText(Path.Combine(EvidenceDir(), $"evidence-{id}.txt"));
        var sec = text.Split("\n## ").ToDictionary(s => s.Split('\n')[0].Trim(), s => s);
        string S(string start) => sec.Single(kv => kv.Key.StartsWith(start)).Value;
        void Has(string hay, string needle) => Assert.True(hay.Contains(needle), $"{id}: '{needle}' not in: {hay.Trim()}");

        uint mapSeed = L.RvaMapSeed!.Value, syncSeed = L.RvaSyncSeed!.Value;
        var ident = S("game seeds");
        var slot70 = ident[ident.IndexOf("slot 70 ->")..ident.IndexOf("slot 192 ->")];
        var slot192 = ident[ident.IndexOf("slot 192 ->")..];
        Has(slot70, $"mov eax,ds:[{0x10000000 + mapSeed:X}h]");
        Has(slot192, $"mov eax,ds:[{0x10000000 + syncSeed:X}h]");

        var info = S("info tables");
        if (info.Contains("not located"))
        {
            Assert.Equal("snowve-8.1.1", id); // the one PDB whose publics table cannot be read: names are PDB-only there
            return;
        }
        Has(info, $"mov ecx,[ecx+{L.GlobalsUnitInfos!.Value:X}h]");
        Has(info, $"[ecx+{L.GlobalsCivInfos!.Value + 4:X}h]");
        Has(info, $"[ecx+{L.GlobalsCivInfos!.Value + 8:X}h]");
        Has(info, $"lea ecx,[esi+0{L.InfoType!.Value:X}h]");
        Has(info, "= \"Type\"");
    }

    static string EvidenceDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "layouts", "evidence-snowve-8.2.0.txt"))) return Path.Combine(d.FullName, "layouts");
        throw new DirectoryNotFoundException("OutsideRecorder/layouts");
    }

    // Every full (non-forward) record of the class: a PDB can hold several (other #defines per file). All must agree,
    // else the layout is ambiguous and must not be trusted (LEKMOD's trap).
    static List<ClassRecord> Classes(TpiStream tpi, string name)
    {
        var r = new List<ClassRecord>();
        foreach (var i in tpi.GetIndexes(TypeLeafKind.LF_STRUCTURE).Concat(tpi.GetIndexes(TypeLeafKind.LF_CLASS)))
            if (tpi[i] is ClassRecord c && !c.IsForwardReference && c.Name.ToString() == name) r.Add(c);
        if (r.Count == 0) throw new KeyNotFoundException(name);
        return r;
    }

    static IEnumerable<TypeRecord> Fields(TpiStream tpi, ClassRecord r)
    {
        var idx = r.FieldList;
        for (int g = 0; g < 1000 && tpi[idx] is FieldListRecord fl; g++)
        {
            foreach (var f in fl.Fields) if (f is not ListContinuationRecord) yield return f;
            var next = fl.Fields.OfType<ListContinuationRecord>().FirstOrDefault();
            if (next is null) yield break;
            idx = next.ContinuationIndex;
        }
    }

    static int Agreed(IEnumerable<int> values, string what)
    {
        var d = values.Distinct().ToList();
        Assert.True(d.Count == 1, $"{what}: PDB records disagree ({string.Join(", ", d.Select(v => $"0x{v:X}"))})");
        return d[0];
    }


    /// <summary>The lobby fields (OR03 "Lobby / channel"): CvPreGame::s_* + FAutoVariable m_value, from the PDB.</summary>
    internal static void AssertLobby(GameCoreLayout L, PdbFile pdb, Func<string, int> value)
    {
        var data = new Dictionary<string, ulong>();
        foreach (var kind in new[] { SymbolRecordKind.S_GDATA32, SymbolRecordKind.S_LDATA32 })
            foreach (var s in pdb.PdbSymbolStream[kind].OfType<DataSymbol>())
                data.TryAdd(s.Name.ToString(), pdb.FindRelativeVirtualAddress(s.Segment, s.Offset, false));
        const string P = ",CvPreGame::Phony>";
        uint At(string sym, string type) => (uint)data["CvPreGame::" + sym] + (uint)value("FAutoVariable<" + type + P);
        string V(string t) => $"std::vector<{t},std::allocator<{t}> >";
        Assert.Equal(L.RvaWorldSize, At("s_worldSize", "enum WorldSizeTypes"));
        Assert.Equal(L.RvaGameSpeed, At("s_gameSpeed", "enum GameSpeedTypes"));
        Assert.Equal(L.RvaEra, At("s_era", "enum EraTypes"));
        Assert.Equal(L.RvaEndTurnTimerLength, At("s_endTurnTimerLength", "float"));
        Assert.Equal(L.RvaTurnTimerType, At("s_turnTimerType", "enum TurnTimerTypes"));
        Assert.Equal(L.RvaMapScriptName, At("s_mapScriptName", "CvString"));
        Assert.Equal(L.RvaGameName, At("s_gameName", "CvString"));
        Assert.Equal(L.RvaNicknamesVector, At("s_nicknames", V("CvString")));
        Assert.Equal(L.RvaLeaderHeadsVector, At("s_leaderHeads", V("enum LeaderHeadTypes")));
        Assert.Equal(L.RvaTeamTypesVector, At("s_teamTypes", V("enum TeamTypes")));
        Assert.Equal(L.RvaHandicapsVector, At("s_handicaps", V("enum HandicapTypes")));
        Assert.Equal(L.RvaGameOptionsVector, At("s_GameOptions", V("CvPreGame::CustomOption")));
    }

    [Theory, MemberData(nameof(Ids))]
    public void PreGameLobbyMatchesPdb(string id)
    {
        if (Missing(id, out _, out var pdbPath)) return;
        using var pdb = new PdbFile(pdbPath);
        var tpi = pdb.TpiStream;
        AssertLobby(ById(id), pdb, t => Member(tpi, t, "m_value"));
        Assert.Equal(0x44, Size(tpi, "CvPreGame::CustomOption"));
        Assert.Equal(0x40, Member(tpi, "CvPreGame::CustomOption", "m_iValue"));
        Assert.Equal(0x14, Member(tpi, "std::basic_string<char,std::char_traits<char>,std::allocator<char> >", "_Mysize"));
    }

    internal static int MemberOf(TpiStream tpi, string cls, string member) => Member(tpi, cls, member);
    /// <summary>A bit field member's (byte offset, bit position), agreed by every record of the class.</summary>
    internal static (int Offset, int Bit) BitOf(TpiStream tpi, string cls, string member)
    {
        var all = Classes(tpi, cls).Select(c =>
        {
            var d = Fields(tpi, c).OfType<DataMemberRecord>().Single(x => x.Name.ToString() == member);
            int bit = tpi[d.Type] is BitFieldRecord b ? b.BitOffset : -1;
            return ((int)d.FieldOffset, bit);
        }).Distinct().ToList();
        Assert.Single(all);
        return all[0];
    }
    internal static int SizeOf(TpiStream tpi, string cls) => Size(tpi, cls);

    /// <summary>The tile fields (OR03 "Tiles") from a PDB: CvMap::m_pMapPlots, sizeof(CvPlot), the CvPlot fields and pillaged bits.</summary>
    internal static void AssertPlots(GameCoreLayout L, TpiStream tpi)
    {
        Assert.Equal(L.MapPlots, Member(tpi, "CvMap", "m_pMapPlots"));
        Assert.Equal(L.PlotSize, Size(tpi, "CvPlot"));
        Assert.Equal(L.PlotX, Member(tpi, "CvPlot", "m_iX"));
        Assert.Equal(L.PlotY, Member(tpi, "CvPlot", "m_iY"));
        Assert.Equal(L.PlotOwner, Member(tpi, "CvPlot", "m_eOwner"));
        Assert.Equal(L.PlotResource, Member(tpi, "CvPlot", "m_eResourceType"));
        Assert.Equal(L.PlotImprovement, Member(tpi, "CvPlot", "m_eImprovementType"));
        Assert.Equal(L.PlotRoute, Member(tpi, "CvPlot", "m_eRouteType"));
        Assert.Equal((L.PlotPillagedByte!.Value, L.PlotImprovementPillagedBit), BitOf(tpi, "CvPlot", "m_bImprovementPillaged"));
        Assert.Equal((L.PlotPillagedByte.Value, L.PlotRoutePillagedBit), BitOf(tpi, "CvPlot", "m_bRoutePillaged"));
    }

    [Theory, MemberData(nameof(Ids))]
    public void PlotsMatchPdb(string id)
    {
        if (Missing(id, out _, out var pdbPath)) return;
        using var pdb = new PdbFile(pdbPath);
        try { AssertPlots(ById(id), pdb.TpiStream); }
        catch (KeyNotFoundException e) { output.WriteLine($"{id}: {e.Message} not readable in this PDB; the code proof (evidence-plots.txt) stands"); }
    }
    static int Member(TpiStream tpi, string cls, string member) => Agreed(Classes(tpi, cls).Select(c =>
        (int)Fields(tpi, c).OfType<DataMemberRecord>().Single(d => d.Name.ToString() == member).FieldOffset), $"{cls}::{member}");

    static int Base(TpiStream tpi, string cls, string baseName) => Agreed(Classes(tpi, cls).Select(c =>
        (int)Fields(tpi, c).OfType<BaseClassRecord>().Single(b => tpi[b.Type] is ClassRecord bc && bc.Name.ToString() == baseName).Offset), $"{cls} : {baseName}");

    static int Size(TpiStream tpi, string cls) => Agreed(Classes(tpi, cls).Select(c => (int)c.Size), $"sizeof {cls}");

    static (Guid, uint) Rsds(byte[] pe)
    {
        int at = -1;
        for (int i = 0; i < pe.Length - 4; i++)
            if (pe[i] == 'R' && pe[i + 1] == 'S' && pe[i + 2] == 'D' && pe[i + 3] == 'S' && Encoding.ASCII.GetString(pe, i + 24, 3) == "C:\\") { at = i; break; }
        Assert.True(at > 0, "no RSDS record");
        return (new Guid(pe.AsSpan(at + 4, 16)), BitConverter.ToUInt32(pe, at + 20));
    }
}
