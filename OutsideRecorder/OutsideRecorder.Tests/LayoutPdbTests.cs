using System.Text;
using SharpPdb.Windows;
using SharpPdb.Windows.SymbolRecords;
using SharpPdb.Windows.TPI;
using SharpPdb.Windows.TypeRecords;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

/// <summary>
/// Proves Layouts.cs against the installed Lekmod DLL + PDB: the PDB belongs to the DLL (GUID + age),
/// and every offset, global and vtable is what the PDB says. Passes with a note when Lekmod is not installed.
/// </summary>
public sealed class LayoutPdbTests(ITestOutputHelper output)
{
    static readonly string Dir = Environment.GetEnvironmentVariable("LEKMOD_DIR")
        ?? @"C:\Program Files (x86)\Steam\steamapps\common\Sid Meier's Civilization V\Assets\DLC\LEKMOD_v35.4";
    static string Dll => Path.Combine(Dir, "CvGameCore_Expansion2.dll");
    static string Pdb => Path.Combine(Dir, "CvGameCore_Expansion2.pdb");
    static readonly GameCoreLayout L = KnownLayouts.Lekmod354;

    bool Missing()
    {
        if (File.Exists(Dll) && File.Exists(Pdb)) return false;
        output.WriteLine($"Lekmod not installed at {Dir}: PDB check skipped");
        return true;
    }

    [Fact]
    public void DllHashAndPeHeaderMatchLayout()
    {
        if (Missing()) return;
        Assert.Equal(L.DllSha256, GameFinder.Sha256Of(Dll));
        var pe = File.ReadAllBytes(Dll);
        int off = BitConverter.ToInt32(pe, 0x3C);
        Assert.Equal(L.PeTimeDateStamp, BitConverter.ToUInt32(pe, off + 8));
        Assert.Equal(L.PeSizeOfImage, BitConverter.ToUInt32(pe, off + 24 + 56));
    }

    [Fact]
    public void PdbBelongsToDll()
    {
        if (Missing()) return;
        using var pdb = new PdbFile(Pdb);
        var (guid, age) = Rsds(File.ReadAllBytes(Dll));
        Assert.Equal(pdb.InfoStream.Header.Guid, guid);
        Assert.Equal((uint)pdb.DbiStream.Header.Age, age);
        Assert.Equal("4d0dd08d-b7a6-46f4-86d3-ae7f45dac65b/26", $"{guid}/{age}");
    }

    [Fact]
    public void GlobalsAndVtablesMatchPdb()
    {
        if (Missing()) return;
        using var pdb = new PdbFile(Pdb);
        var data = new Dictionary<string, ulong>();
        foreach (var kind in new[] { SymbolRecordKind.S_GDATA32, SymbolRecordKind.S_LDATA32 })
            foreach (var s in pdb.PdbSymbolStream[kind].OfType<DataSymbol>())
                data.TryAdd(s.Name.ToString(), pdb.FindRelativeVirtualAddress(s.Segment, s.Offset, false));
        var pub = pdb.PublicsStream.PublicSymbols.GroupBy(p => p.Name.ToString())
            .ToDictionary(g => g.Key, g => pdb.FindRelativeVirtualAddress(g.First().Segment, g.First().Offset, false));
        var tpi = pdb.TpiStream;
        int Value(string autoVar) => Member(tpi, autoVar, "m_value"); // FAutoVariable<T,...>::m_value

        Assert.Equal(L.RvaGlobals, (uint)data["gGlobals"]);
        Assert.Equal(L.RvaPlayers, (uint)data["CvPlayerAI::m_aPlayers"]);
        Assert.Equal(L.RvaGameTurn, (uint)data["CvPreGame::s_gameTurn"] + (uint)Value("FAutoVariable<int,CvPreGame::Phony>"));
        Assert.Equal(L.RvaGameStarted, (uint)data["CvPreGame::s_gameStarted"] + (uint)Value("FAutoVariable<bool,CvPreGame::Phony>"));
        Assert.Equal(L.RvaActivePlayer, (uint)data["CvPreGame::s_activePlayer"] + (uint)Value("FAutoVariable<enum PlayerTypes,CvPreGame::Phony>"));
        Assert.Equal(L.RvaGameType, (uint)data["CvPreGame::s_gameType"] + 4u);
        Assert.Equal(L.RvaSlotStatusVector, (uint)data["CvPreGame::s_slotStatus"]
            + (uint)Value("FAutoVariable<std::vector<enum SlotStatus,std::allocator<enum SlotStatus> >,CvPreGame::Phony>"));
        Assert.Equal(L.RvaCivilizationsVector, (uint)data["CvPreGame::s_civilizations"]
            + (uint)Value("FAutoVariable<std::vector<enum CivilizationTypes,std::allocator<enum CivilizationTypes> >,CvPreGame::Phony>"));

        Assert.Equal(L.VtGlobals, (uint)pub["??_7CvGlobals@@6B@"]);
        Assert.Equal(L.VtPlayerAI, (uint)pub["??_7CvPlayerAI@@6B@"]);
        Assert.Equal(L.VtUnitArchive, (uint)pub["??_7?$FAutoArchiveClassContainer@VCvUnit@@@@6B@"]);
        Assert.Equal(L.VtCityAI, (uint)pub["??_7CvCityAI@@6B@"]);
        Assert.Equal(L.VtMap, (uint)pub["??_7CvMap@@6B@"]);
    }

    [Fact]
    public void MemberOffsetsMatchPdb()
    {
        if (Missing()) return;
        using var pdb = new PdbFile(Pdb);
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
        Assert.Equal(0, Member(t, "CvUnit", "m_syncArchive"));
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

        // MSVC 2008 std::string as MemoryExtensions.CvString reads it
        const string Str = "std::basic_string<char,std::char_traits<char>,std::allocator<char> >";
        Assert.Equal(0x1C, Size(t, "CvString"));
        Assert.Equal(4, Member(t, Str, "_Bx"));
        Assert.Equal(0x14, Member(t, Str, "_Mysize"));
        Assert.Equal(0x18, Member(t, Str, "_Myres"));
        Assert.Equal(4, Member(t, "std::vector<CvString,std::allocator<CvString> >", "_Myfirst"));
        Assert.Equal(8, Member(t, "std::vector<CvString,std::allocator<CvString> >", "_Mylast"));
    }

    // Lekmod's PDB holds two full records for CvUnit/CvPlayer/CvGlobals (one stale, from a file built with other #defines).
    // The machine code (layouts/evidence-lekmod-35.4.003.txt) matches the newest record, so that one is used.
    static ClassRecord Class(TpiStream tpi, string name)
    {
        ClassRecord? found = null;
        foreach (var i in tpi.GetIndexes(TypeLeafKind.LF_STRUCTURE).Concat(tpi.GetIndexes(TypeLeafKind.LF_CLASS)).OrderBy(i => i.Index))
            if (tpi[i] is ClassRecord r && !r.IsForwardReference && r.Name.ToString() == name) found = r;
        return found ?? throw new KeyNotFoundException(name);
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

    static int Member(TpiStream tpi, string cls, string member) =>
        (int)Fields(tpi, Class(tpi, cls)).OfType<DataMemberRecord>().Single(d => d.Name.ToString() == member).FieldOffset;

    static int Base(TpiStream tpi, string cls, string baseName) =>
        (int)Fields(tpi, Class(tpi, cls)).OfType<BaseClassRecord>().Single(b => tpi[b.Type] is ClassRecord c && c.Name.ToString() == baseName).Offset;

    static int Size(TpiStream tpi, string cls) => (int)Class(tpi, cls).Size;

    static (Guid, uint) Rsds(byte[] pe)
    {
        int at = -1;
        for (int i = 0; i < pe.Length - 4; i++)
            if (pe[i] == 'R' && pe[i + 1] == 'S' && pe[i + 2] == 'D' && pe[i + 3] == 'S' && Encoding.ASCII.GetString(pe, i + 24, 3) == "C:\\") { at = i; break; }
        Assert.True(at > 0, "no RSDS record");
        return (new Guid(pe.AsSpan(at + 4, 16)), BitConverter.ToUInt32(pe, at + 20));
    }
}
