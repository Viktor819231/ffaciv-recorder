using System.Reflection;
using System.Text;
using SharpPdb.Windows;
using SharpPdb.Windows.SymbolRecords;
using SharpPdb.Windows.TypeRecords;
using SharpPdb.Windows.TPI;

// Usage: LayoutDump <dll> <pdb> [info] [struct=<Name>]... [global=<substring>]... [publics=<substring>]...
// Read-only: opens the two files, prints what it finds. Used to (re)make OutsideRecorder/Layouts.cs.
if (args.Length < 2) { Console.WriteLine("usage: LayoutDump <dll> <pdb> [info] [struct=Name] [global=substr] [publics=substr]"); return 1; }
var dllPath = args[0];
var pdbPath = args[1];
using var pdb = new PdbFile(pdbPath);

if (args.Contains("info"))
{
    var (guid, age, name, stamp, sizeOfImage, imageBase) = ReadRsds(File.ReadAllBytes(dllPath));
    Console.WriteLine($"dll RSDS guid={guid} age={age} pdb={name}");
    Console.WriteLine($"dll TimeDateStamp=0x{stamp:X8} SizeOfImage=0x{sizeOfImage:X} ImageBase=0x{imageBase:X}");
    DumpProps("pdb info", pdb.InfoStream.Header);
    DumpProps("pdb dbi header", pdb.DbiStream.GetType().GetProperty("Header")?.GetValue(pdb.DbiStream));
}

foreach (var s in args.Where(a => a.StartsWith("struct=")).Select(a => a[7..])) DumpStruct(pdb.TpiStream, s);

var globalFilters = args.Where(a => a.StartsWith("global=")).Select(a => a[7..]).ToList();
if (globalFilters.Count > 0)
{
    var stream = pdb.PdbSymbolStream;
    foreach (var kind in new[] { SymbolRecordKind.S_GDATA32, SymbolRecordKind.S_LDATA32 })
        foreach (var sym in stream[kind].OfType<DataSymbol>())
        {
            var n = sym.Name.ToString();
            if (!globalFilters.Any(f => n.Contains(f, StringComparison.Ordinal))) continue;
            var rva = pdb.FindRelativeVirtualAddress(sym.Segment, sym.Offset, false);
            Console.WriteLine($"  {kind} {n} RVA=0x{rva:X} type={TypeName(pdb.TpiStream, sym.Type)}");
        }
}
foreach (var f in args.Where(a => a.StartsWith("publics=")).Select(a => a[8..]))
    foreach (var p in pdb.PublicsStream.PublicSymbols)
        if (p.Name.ToString().Contains(f, StringComparison.Ordinal))
            Console.WriteLine($"  public {p.Name} RVA=0x{pdb.FindRelativeVirtualAddress(p.Segment, p.Offset, false):X}");
return 0;

static void DumpProps(string title, object? o)
{
    if (o is null) { Console.WriteLine($"{title}: (none)"); return; }
    foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (p.GetIndexParameters().Length > 0) continue;
        object? v; try { v = p.GetValue(o); } catch { continue; }
        if (v is null || v is System.Collections.IEnumerable && v is not string) continue;
        Console.WriteLine($"{title}.{p.Name} = {v}");
    }
}

static void DumpStruct(TpiStream tpi, string structName)
{
    int found = 0; // a class can have several full records (different #defines per file): print them all
    foreach (var index in tpi.GetIndexes(TypeLeafKind.LF_STRUCTURE).Concat(tpi.GetIndexes(TypeLeafKind.LF_CLASS)))
    {
        if (tpi[index] is not ClassRecord record || record.Name.ToString() != structName || record.IsForwardReference) continue;
        Console.WriteLine($"==== {record.Name} size=0x{record.Size:X} (type index {index})");
        found++;
        var all = new List<TypeRecord>();
        var listIndex = record.FieldList;
        // Big classes split their member list into several LF_FIELDLIST records chained by LF_INDEX.
        for (int guard = 0; guard < 1000 && tpi[listIndex] is FieldListRecord fl; guard++)
        {
            var next = fl.Fields.OfType<ListContinuationRecord>().FirstOrDefault();
            all.AddRange(fl.Fields.Where(f => f is not ListContinuationRecord));
            if (next is null) break;
            listIndex = next.ContinuationIndex;
        }
        foreach (var field in all)
        {
            switch (field)
            {
                case StaticDataMemberRecord st:
                    Console.WriteLine($"  static {TypeName(tpi, st.Type),-40} {st.Name}");
                    break;
                case DataMemberRecord data:
                    Console.WriteLine($"  +0x{data.FieldOffset:X4} {TypeName(tpi, data.Type),-40} {data.Name}");
                    break;
                case BaseClassRecord baseClass:
                    Console.WriteLine($"  base +0x{baseClass.Offset:X} {TypeName(tpi, baseClass.Type)}");
                    break;
                case VirtualFunctionPointerRecord:
                    Console.WriteLine("  +0x0000 vfptr");
                    break;
            }
        }
    }
    if (found == 0) Console.WriteLine($"==== {structName}: not found");
}

static string TypeName(TpiStream tpi, TypeIndex type)
{
    if (type.IsSimple) return type.SimpleKind + (type.SimpleMode != SimpleTypeMode.Direct ? "*" : "");
    return tpi[type] switch
    {
        PointerRecord pointer => TypeName(tpi, pointer.ReferentType) + "*",
        ArrayRecord array => $"{TypeName(tpi, array.ElementType)}[{array.Size}]",
        BitFieldRecord bits => $"{TypeName(tpi, bits.Type)}:{bits.BitSize}@{bits.BitOffset}",
        ClassRecord cls => cls.Name.ToString(),
        UnionRecord u => "union " + u.Name,
        EnumRecord en => en.Name.ToString(),
        ModifierRecord mod => TypeName(tpi, mod.ModifiedType),
        var r => r.Kind.ToString(),
    };
}

// The DLL's CodeView record says which PDB it was built with (GUID + age): the two must match.
static (Guid Guid, uint Age, string Name, uint Stamp, uint SizeOfImage, uint ImageBase) ReadRsds(byte[] pe)
{
    int peOff = BitConverter.ToInt32(pe, 0x3C);
    uint stamp = BitConverter.ToUInt32(pe, peOff + 8);
    int opt = peOff + 24;
    uint imageBase = BitConverter.ToUInt32(pe, opt + 28);
    uint sizeOfImage = BitConverter.ToUInt32(pe, opt + 56);
    int dataDirs = opt + 96;
    uint debugRva = BitConverter.ToUInt32(pe, dataDirs + 6 * 8);
    uint debugSize = BitConverter.ToUInt32(pe, dataDirs + 6 * 8 + 4);
    int sections = opt + BitConverter.ToUInt16(pe, peOff + 20);
    int nsec = BitConverter.ToUInt16(pe, peOff + 6);
    int RvaToOff(uint rva)
    {
        for (int i = 0; i < nsec; i++)
        {
            int s = sections + i * 40;
            uint va = BitConverter.ToUInt32(pe, s + 12), vs = BitConverter.ToUInt32(pe, s + 8), raw = BitConverter.ToUInt32(pe, s + 20);
            if (rva >= va && rva < va + Math.Max(vs, BitConverter.ToUInt32(pe, s + 16))) return (int)(rva - va + raw);
        }
        throw new Exception("rva outside sections");
    }
    int d = RvaToOff(debugRva);
    for (int i = 0; i < debugSize / 28; i++)
    {
        int e = d + i * 28;
        if (BitConverter.ToUInt32(pe, e + 12) != 2) continue; // IMAGE_DEBUG_TYPE_CODEVIEW
        int raw = BitConverter.ToInt32(pe, e + 24);
        if (Encoding.ASCII.GetString(pe, raw, 4) != "RSDS") continue;
        var g = new Guid(pe.AsSpan(raw + 4, 16));
        uint age = BitConverter.ToUInt32(pe, raw + 20);
        int end = Array.IndexOf(pe, (byte)0, raw + 24);
        return (g, age, Encoding.ASCII.GetString(pe, raw + 24, end - raw - 24), stamp, sizeOfImage, imageBase);
    }
    throw new Exception("no RSDS record");
}
