using SharpPdb.Windows;
using SharpPdb.Windows.TPI;
using SharpPdb.Windows.TypeRecords;
using Xunit.Abstractions;

namespace OutsideRecorderTests;

public sealed class CityMicroLayoutTests(ITestOutputHelper output)
{
    public static TheoryData<string> Layouts => new(KnownLayouts.All.Where(l => l.Id != KnownLayouts.Bnw103279.Id).Select(l => l.Id));

    [Theory, MemberData(nameof(Layouts))]
    public void EveryPdbRecordAgreesWithTheNewFields(string id)
    {
        var l = KnownLayouts.All.Single(l => l.Id == id);
        string? dll, pdbPath;
        if (id == KnownLayouts.Lekmod354.Id)
        {
            dll = Environment.GetEnvironmentVariable("MICRO_LEKMOD_DLL");
            pdbPath = Environment.GetEnvironmentVariable("MICRO_LEKMOD_PDB");
        }
        else
        {
            var root = Environment.GetEnvironmentVariable("SNOWVE_BIN") ?? "out/snowve-bin";
            var folder = Path.Combine(root, id["snowve-".Length..]);
            dll = Path.Combine(folder, "CvGameCore_Expansion2.dll"); pdbPath = Path.Combine(folder, "CvGameCore_Expansion2.pdb");
        }
        if (!File.Exists(dll) || !File.Exists(pdbPath)) { output.WriteLine($"{id}: binary evidence unavailable; PDB check not run"); return; }
        Assert.Equal(l.DllSha256, GameFinder.Sha256Of(dll));
        using var pdb = new PdbFile(pdbPath);
        var binary = File.ReadAllBytes(dll);
        var rsds = binary.AsSpan().IndexOf("RSDS"u8);
        Assert.True(rsds >= 0);
        Assert.Equal(pdb.InfoStream.Header.Guid, new Guid(binary.AsSpan(rsds + 4, 16)));
        Assert.Equal((uint)pdb.DbiStream.Header.Age, BitConverter.ToUInt32(binary, rsds + 20));
        var t = pdb.TpiStream; var micro = l.CityMicro!;
        void Member(string cls, string field, int expected)
        {
            var classes = t.GetIndexes(TypeLeafKind.LF_CLASS).Concat(t.GetIndexes(TypeLeafKind.LF_STRUCTURE))
                .Select(i => t[i]).OfType<ClassRecord>().Where(c => !c.IsForwardReference && c.Name.ToString() == cls).ToArray();
            Assert.NotEmpty(classes);
            foreach (var c in classes)
                Assert.Equal(expected, (int)Fields(t, c).OfType<DataMemberRecord>().Single(f => f.Name.ToString() == field).FieldOffset);
        }
        Member("FAutoVariable<int,CvCity>", "m_value", 4);
        Member("CvCity", "m_pCityCitizens", micro.Citizens); Member("CvCity", "m_orderQueue", micro.Queue);
        Member("CvCity", "m_iFood", micro.Food100 - 4); Member("CvCity", "m_iOverflowProduction", micro.OverflowRaw - 4);
        Member("CvGlobals", "m_paSpecialistInfo", micro.SpecialistInfos); Member("CvGlobals", "m_pBuildings", micro.BuildingInfos);
        foreach (var (name, offset) in new[] { ("m_pCity", 0), ("m_bNoAutoAssignSpecialists", 5), ("m_eCityAIFocusTypes", 0x14),
            ("m_bForceAvoidGrowth", 0x18), ("m_pabWorkingPlot", 0x19), ("m_pabForcedWorkingPlot", 0x3E),
            ("m_iNumDefaultSpecialists", 0x64), ("m_iNumForcedDefaultSpecialists", 0x68), ("m_aiSpecialistCounts", 0x6C),
            ("m_aiNumSpecialistsInBuilding", 0x74), ("m_aiNumForcedSpecialistsInBuilding", 0x78), ("m_bInited", 0x80) })
            Member("CvCityCitizens", name, offset);
        Member("CvBuildingXMLEntries", "m_paBuildingEntries", 0);
        Member("FFastSmallFixedList<OrderData,25,1,297,0>", "mVec", 0);
        Member("BaseVector<OrderData,1>", "m_pData", 0); Member("BaseVector<OrderData,1>", "m_uiCurrSize", 4);
        Member("BaseVector<OrderData,1>", "m_uiCurrMaxSize", 8);
        Member("OrderData", "eOrderType", 0); Member("OrderData", "iData1", 4); Member("OrderData", "iData2", 8); Member("OrderData", "bSave", 12);
        output.WriteLine($"{id}: matching DLL/PDB; city, citizen, queue and database-count fields verified");
    }

    static IEnumerable<TypeRecord> Fields(TpiStream t, ClassRecord c)
    {
        var index = c.FieldList;
        for (int guard = 0; guard < 1000 && t[index] is FieldListRecord fields; guard++)
        {
            foreach (var field in fields.Fields) yield return field;
            var next = fields.Fields.OfType<ListContinuationRecord>().FirstOrDefault();
            if (next is null) yield break;
            index = next.ContinuationIndex;
        }
    }
}
