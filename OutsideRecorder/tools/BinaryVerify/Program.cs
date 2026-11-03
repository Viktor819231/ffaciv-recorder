using System.Text;
using System.Text.RegularExpressions;
using Iced.Intel;

// BinaryVerify <dll> <command> ...   (read-only; prints evidence, RVAs relative to the DLL's image base)
//   lua <anchorName> <Name>...      wrapper of Lua method <Name> in the registration block that also registers <anchorName>;
//                                   prints field offsets read by the wrapper and by small getters it calls, and absolute addresses
//   rtti <.?AVClass@@>              vtable RVA(s) of a class, from MSVC RTTI
//   stores <vtableRva>              globals whose first word is set to this vtable by static init code (the object's RVA)
//   players                         most common "index * size + [global]" pairs (GET_PLAYER inlined): array global + stride
//   dis <rva> [count]               disassemble
if (args.Length < 2) { Console.WriteLine("usage: BinaryVerify <dll> lua|rtti|stores|players|dis ..."); return 1; }
var pe = new Pe(File.ReadAllBytes(args[0]));
var code = pe.DecodeCode();
switch (args[1])
{
    case "lua": Lua(args[2], args[3..]); break;
    case "rtti": foreach (var v in pe.RttiVtables(args[2])) Console.WriteLine($"{args[2]} vtable RVA=0x{v:X}"); break;
    case "stores":
        {
            uint vt = pe.Base + Convert.ToUInt32(args[2], 16);
            foreach (var i in code.Where(i => i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryBase == Register.None
                && i.MemoryIndex == Register.None && i.Op1Kind == OpKind.Immediate32 && i.Immediate32 == vt))
                Console.WriteLine($"  store vtable at code RVA 0x{i.IP - pe.Base:X} -> object RVA 0x{(uint)i.MemoryDisplacement64 - pe.Base:X}");
            break;
        }
    case "players": Players(); break;
    case "objects":
        {
            // objects <vtableRva>: constructors that store this vtable into [reg] -> their callers' "mov ecx, imm" = static object
            uint vt = pe.Base + Convert.ToUInt32(args[2], 16);
            var index = new Dictionary<ulong, int>(); for (int k = 0; k < code.Count; k++) index[code[k].IP] = k;
            foreach (var st in code.Where(i => i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryBase != Register.None
                && i.MemoryDisplacement64 == 0 && i.MemoryIndex == Register.None && i.Op1Kind == OpKind.Immediate32 && i.Immediate32 == vt).ToList())
            {
                int k = index[st.IP];
                while (k > 0 && code[k - 1].Mnemonic != Mnemonic.Int3 && st.IP - code[k - 1].IP < 0x400) k--;
                ulong start = code[k].IP;
                Console.WriteLine($"  ctor at 0x{start - pe.Base:X} (vtable store 0x{st.IP - pe.Base:X})");
                for (int c = 0; c < code.Count; c++)
                {
                    if (code[c].Mnemonic is not (Mnemonic.Call or Mnemonic.Jmp) || code[c].Op0Kind != OpKind.NearBranch32 || code[c].NearBranch32 != start) continue;
                    for (int b = c - 1; b >= Math.Max(0, c - 4); b--)
                        if (code[b].Mnemonic == Mnemonic.Mov && code[b].Op0Register == Register.ECX && code[b].Op1Kind == OpKind.Immediate32)
                        { Console.WriteLine($"    caller 0x{code[c].IP - pe.Base:X}: object RVA 0x{code[b].Immediate32 - pe.Base:X}"); break; }
                }
            }
            break;
        }
    case "xref":
        {
            uint va = pe.Base + Convert.ToUInt32(args[2], 16);
            var hits = code.Where(i => Enumerable.Range(0, i.OpCount).Any(o => i.GetOpKind(o) == OpKind.Memory) && i.MemoryBase == Register.None
                && (uint)i.MemoryDisplacement64 == va).ToList();
            Console.WriteLine($"  0x{args[2]}: {hits.Count} absolute uses");
            foreach (var i in hits.Take(int.Parse(args.Length > 3 ? args[3] : "5"))) Console.WriteLine($"    {i.IP - pe.Base:X6} {i}");
            break;
        }
    case "vfn":
        {
            // vfn <.?AVClass@@> <slot>: disassemble the slot-th virtual function of a class
            foreach (var vt in pe.RttiVtables(args[2]))
            {
                uint fn = pe.U32(pe.Base + vt + 4 * uint.Parse(args[3])) ?? 0;
                Console.WriteLine($"  vtable 0x{vt:X} slot {args[3]} -> 0x{fn - pe.Base:X}");
                foreach (var i in pe.Decode(fn, 200).TakeWhile(i => i.Mnemonic != Mnemonic.Int3)) Console.WriteLine($"    {i.IP - pe.Base:X6} {i}");
            }
            break;
        }
    case "vtab":
        {
            // vtab <.?AVClass@@> <count>: the first <count> virtual function RVAs of a class (to name slots from a PDB)
            foreach (var vt in pe.RttiVtables(args[2]))
                for (uint k = 0; k < uint.Parse(args[3]); k++)
                    Console.WriteLine($"slot {k} 0x{(pe.U32(pe.Base + vt + 4 * k) ?? 0) - pe.Base:X}");
            break;
        }
    case "dis":
        {
            uint rva = Convert.ToUInt32(args[2], 16); int n = args.Length > 3 ? int.Parse(args[3]) : 40;
            foreach (var i in pe.Decode(pe.Base + rva, n * 8).Take(n)) Console.WriteLine($"  {i.IP - pe.Base:X6} {i}");
            break;
        }
}
return 0;

void Lua(string anchor, string[] names)
{
    // Method(Name): push 0; push l<Name>; push L; call lua_pushcclosure; push "<Name>"; push t; push L; call lua_setfield
    var regs = new List<(string Name, uint Func, uint Site)>();
    for (int k = 0; k < code.Count; k++)
    {
        var ins = code[k];
        if (ins.Mnemonic != Mnemonic.Push || ins.Op0Kind != OpKind.Immediate32 && ins.Op0Kind != OpKind.Immediate8to32) continue;
        uint imm = ins.Immediate32;
        if (pe.IsCode(imm)) continue;
        var s = pe.CStr(imm, 64);
        if (s is null || !Regex.IsMatch(s, "^[A-Z][A-Za-z0-9_]+$")) continue;
        for (int j = k - 1; j >= Math.Max(0, k - 8); j--)
            if (code[j].Mnemonic == Mnemonic.Push && code[j].Op0Kind == OpKind.Immediate32 && pe.IsCode(code[j].Immediate32))
            { regs.Add((s, code[j].Immediate32, (uint)ins.IP)); break; }
    }
    regs.Sort((a, b) => a.Site.CompareTo(b.Site));
    // blocks: consecutive registrations less than 0x100 bytes apart
    var blocks = new List<List<(string Name, uint Func, uint Site)>>();
    foreach (var r in regs)
    {
        if (blocks.Count == 0 || r.Site - blocks[^1][^1].Site > 0x100) blocks.Add([]);
        blocks[^1].Add(r);
    }
    var block = blocks.Where(b => b.Any(r => r.Name == anchor)).OrderByDescending(b => b.Count).FirstOrDefault();
    if (block is null) { Console.WriteLine($"no registration block with {anchor}"); return; }
    Console.WriteLine($"block with {anchor}: {block.Count} methods, sites 0x{block[0].Site - pe.Base:X}..0x{block[^1].Site - pe.Base:X}");
    foreach (var name in names)
    {
        var hits = block.Where(r => r.Name == name).ToList();
        if (hits.Count != 1) { Console.WriteLine($"  {name}: {hits.Count} registrations -> not confirmed"); continue; }
        Console.WriteLine($"  {name}: wrapper RVA 0x{hits[0].Func - pe.Base:X}  {Evidence(hits[0].Func)}");
    }
}

string Evidence(uint func)
{
    var sb = new StringBuilder();
    var body = Body(func, 2000);
    sb.Append("inline[").Append(string.Join(",", MemReads(body))).Append("]");
    foreach (var c in Callees(body)) Getter(sb, c, 1);
    return sb.ToString();
}

// The function up to the int3 padding that follows its last ret.
List<Instruction> Body(uint va, int max)
{
    var all = pe.Decode(va, max);
    int pad = all.FindIndex(i => i.Mnemonic == Mnemonic.Int3);
    return pad < 0 ? all : all.Take(pad).ToList();
}

// Functions called directly, jumped to as a tail call, or passed as a member-function pointer (push imm).
IEnumerable<uint> Callees(List<Instruction> body) => body
    .Where(i => (i.Mnemonic is Mnemonic.Call or Mnemonic.Jmp) && i.Op0Kind == OpKind.NearBranch32 && !(i.NearBranch32 > body[0].IP && i.NearBranch32 <= body[^1].IP))
    .Select(i => i.NearBranch32)
    .Concat(body.Where(i => i.Mnemonic == Mnemonic.Push && i.Op0Kind == OpKind.Immediate32 && pe.IsCode(i.Immediate32)).Select(i => i.Immediate32))
    .Distinct();

void Getter(StringBuilder sb, uint va, int depth)
{
    var g = Body(va, 400);
    if (g.Count == 0 || g.Count > 40 || g.All(i => i.Mnemonic != Mnemonic.Ret) && g[^1].Mnemonic != Mnemonic.Jmp) return; // only small functions (ret or tail jump)
    if (g.Any(i => i.Mnemonic == Mnemonic.Jmp && i.Op0Kind == OpKind.Memory)) return;  // import thunk
    sb.Append($" {new string('>', depth)}fn@0x{va - pe.Base:X}[").Append(string.Join(",", MemReads(g))).Append(']');
    if (depth < 3) foreach (var c in Callees(g)) Getter(sb, c, depth + 1);
}

IEnumerable<string> MemReads(List<Instruction> body)
{
    foreach (var i in body)
        for (int o = 0; o < i.OpCount; o++)
        {
            if (o == 0 && i.Mnemonic == Mnemonic.Add && i.Op0Register == Register.ECX && i.Op1Kind is OpKind.Immediate32 or OpKind.Immediate8to32)
                yield return $"this+=0x{i.Immediate32:X}";
            if (o == 1 && i.Mnemonic == Mnemonic.Lea && i.Op0Register == Register.ECX && i.MemoryBase == Register.ECX)
                yield return $"this+=0x{(uint)i.MemoryDisplacement64:X}";
            if (i.GetOpKind(o) != OpKind.Memory || i.Mnemonic is Mnemonic.Lea or Mnemonic.Push or Mnemonic.Call) continue;
            if (i.MemoryBase is Register.ESP or Register.EBP) continue;
            uint d = (uint)i.MemoryDisplacement64;
            if (i.MemoryBase == Register.None && i.MemoryIndex == Register.None) yield return $"abs:0x{d - pe.Base:X}";
            else if (i.MemoryIndex == Register.None && d >= 4) yield return $"{(o == 0 ? "w" : "")}{i.MemoryBase}+0x{d:X}({i.MemorySize})";
        }
}

void Players()
{
    // CvPlayerAI::getPlayer inlined: imul r, r, SIZE ; add r, [m_aPlayers]   (or mov r2,[m_aPlayers] nearby)
    var pairs = new Dictionary<(uint Size, uint Global), int>();
    for (int k = 0; k < code.Count - 4; k++)
    {
        var i = code[k];
        if (i.Mnemonic != Mnemonic.Imul || i.OpCount != 3) continue;
        uint size = i.Immediate32;
        if (size < 0x1000 || size > 0x40000) continue;
        for (int j = k + 1; j <= k + 3; j++)
        {
            var a = code[j];
            if (a.Op1Kind == OpKind.Memory && a.MemoryBase == Register.None && a.MemoryIndex == Register.None && a.Mnemonic is Mnemonic.Add or Mnemonic.Mov)
            {
                var key = (size, (uint)a.MemoryDisplacement64 - pe.Base);
                pairs[key] = pairs.GetValueOrDefault(key) + 1;
                break;
            }
        }
    }
    foreach (var (k, n) in pairs.OrderByDescending(p => p.Value).Take(8)) Console.WriteLine($"  size 0x{k.Size:X} global RVA 0x{k.Global:X}: {n} sites");
}

sealed class Pe
{
    readonly byte[] img;
    public uint Base;
    readonly List<(uint Va, uint Size, uint Raw, uint RawSize, bool Exec)> secs = new();
    public Pe(byte[] file)
    {
        img = file;
        int pe = BitConverter.ToInt32(file, 0x3C), opt = pe + 24;
        Base = BitConverter.ToUInt32(file, opt + 28);
        int n = BitConverter.ToUInt16(file, pe + 6), s0 = opt + BitConverter.ToUInt16(file, pe + 20);
        for (int i = 0; i < n; i++)
        {
            int s = s0 + i * 40;
            secs.Add((Base + BitConverter.ToUInt32(file, s + 12), BitConverter.ToUInt32(file, s + 8), BitConverter.ToUInt32(file, s + 20),
                BitConverter.ToUInt32(file, s + 16), (BitConverter.ToUInt32(file, s + 36) & 0x20000000) != 0));
        }
    }
    public bool IsCode(uint va) => secs.Any(s => s.Exec && va >= s.Va && va < s.Va + s.Size);
    int Off(uint va) { foreach (var s in secs) if (va >= s.Va && va < s.Va + Math.Min(s.Size, s.RawSize)) return (int)(va - s.Va + s.Raw); return -1; }
    public uint? U32(uint va) { int o = Off(va); return o < 0 ? null : BitConverter.ToUInt32(img, o); }
    public string? CStr(uint va, int max)
    {
        int o = Off(va); if (o < 0) return null;
        int e = Array.IndexOf(img, (byte)0, o, Math.Min(max, img.Length - o));
        return e < 0 ? null : Encoding.ASCII.GetString(img, o, e - o);
    }
    public List<Instruction> Decode(uint va, int bytes, bool stopOnInvalid = true)
    {
        int o = Off(va); var list = new List<Instruction>(); if (o < 0) return list;
        var d = Iced.Intel.Decoder.Create(32, new ByteArrayCodeReader(img, o, Math.Min(bytes, img.Length - o)));
        d.IP = va;
        while (d.IP < va + (uint)bytes) { d.Decode(out var i); if (i.IsInvalid) { if (stopOnInvalid) break; continue; } list.Add(i); }
        return list;
    }
    public List<Instruction> DecodeCode()
    {
        var all = new List<Instruction>();
        foreach (var s in secs.Where(s => s.Exec)) all.AddRange(Decode(s.Va, (int)Math.Min(s.Size, s.RawSize), false));
        return all;
    }
    /// <summary>MSVC RTTI: TypeDescriptor(name) &lt;- CompleteObjectLocator(offset 0) &lt;- vtable[-1].</summary>
    public IEnumerable<uint> RttiVtables(string mangled)
    {
        var name = Encoding.ASCII.GetBytes(mangled + "\0");
        var tds = new List<uint>();
        foreach (var s in secs.Where(s => !s.Exec))
            for (int o = (int)s.Raw; o < s.Raw + s.RawSize - name.Length; o++)
                if (img.AsSpan(o, name.Length).SequenceEqual(name)) tds.Add(s.Va + (uint)(o - s.Raw) - 8);
        foreach (var td in tds)
            foreach (var s in secs.Where(s => !s.Exec))
                for (int o = (int)s.Raw; o + 20 <= s.Raw + s.RawSize; o += 4)
                {
                    if (BitConverter.ToUInt32(img, o + 12) != td || BitConverter.ToUInt32(img, o) != 0 || BitConverter.ToUInt32(img, o + 4) != 0) continue;
                    uint col = s.Va + (uint)(o - s.Raw);
                    foreach (var s2 in secs.Where(x => !x.Exec))
                        for (int p = (int)s2.Raw; p + 8 <= s2.Raw + s2.RawSize; p += 4)
                            if (BitConverter.ToUInt32(img, p) == col && IsCode(BitConverter.ToUInt32(img, p + 4)))
                                yield return s2.Va + (uint)(p - s2.Raw) + 4 - Base;
                }
    }
}
