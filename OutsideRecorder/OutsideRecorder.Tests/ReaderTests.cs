using System.Buffers.Binary;
using System.Text;

namespace OutsideRecorderTests;

/// <summary>A fake 32-bit address space: a few byte blocks at fixed addresses.</summary>
sealed class FakeMemory : IMemory
{
    readonly SortedDictionary<uint, byte[]> blocks = new();
    public byte[] Block(uint at, int size) { var b = new byte[size]; blocks[at] = b; return b; }
    public bool TryRead(uint address, Span<byte> buffer)
    {
        foreach (var (at, b) in blocks)
            if (address >= at && address + buffer.Length <= at + b.Length) { b.AsSpan((int)(address - at), buffer.Length).CopyTo(buffer); return true; }
        return false;
    }
    public void I(uint address, int value)
    {
        foreach (var (at, b) in blocks)
            if (address >= at && address + 4 <= at + b.Length) { BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan((int)(address - at)), value); return; }
        throw new InvalidOperationException($"no block at {address:X}");
    }
    public void B(uint address, bool v) { foreach (var (at, b) in blocks) if (address >= at && address < at + b.Length) { b[address - at] = (byte)(v ? 1 : 0); return; } }
    /// <summary>Writes an MSVC 2008 short std::string (inline, &lt; 16 chars).</summary>
    public void Str(uint address, string s) { var bytes = Encoding.UTF8.GetBytes(s); foreach (var (at, b) in blocks) if (address >= at && address < at + b.Length) { bytes.CopyTo(b, (int)(address - at + 4)); I(address + 0x14, bytes.Length); I(address + 0x18, 15); return; } }
}

/// <summary>Builds a tiny fake Lekmod game in memory with the real layout, then reads it back.</summary>
public sealed class ReaderTests
{
    const uint Base = 0x10000000;
    static readonly GameCoreLayout L = KnownLayouts.Lekmod354;

    /// <summary>Both known layouts.</summary>
    public static TheoryData<string> Layouts => new(KnownLayouts.All.Select(l => l.Id));
    static GameCoreLayout ById(string id) => KnownLayouts.All.Single(l => l.Id == id);

    static (FakeMemory Mem, uint UnitPtr, uint CityPtr) FakeGame() => FakeGame(L);

    internal static (FakeMemory Mem, uint UnitPtr, uint CityPtr) FakeGame(GameCoreLayout L)
    {
        var m = new FakeMemory();
        m.Block(Base + L.RvaGlobals, 0x2200);
        uint lo = new[] { L.RvaPlayers, L.RvaGameTurn, L.RvaActivePlayer, L.RvaGameStarted ?? L.RvaGameTurn }.Min() & ~0xFFFu;
        m.Block(Base + lo, 0x3000);                 // CvPreGame statics + m_aPlayers
        m.I(Base + L.RvaGlobals, (int)(Base + L.VtGlobals));
        uint game = 0x20000000, map = 0x21000000, players = 0x30000000;
        m.Block(game, 0x1610); m.Block(map, 0x10EC);
        m.I(Base + L.RvaGlobals + (uint)L.GlobalsGame, (int)game);
        m.I(Base + L.RvaGlobals + (uint)L.GlobalsMap, (int)map);
        m.I(map, (int)(Base + L.VtMap)); m.I(map + (uint)L.MapWidth, 80); m.I(map + (uint)L.MapHeight, 52);
        m.I(Base + L.RvaPlayers, (int)players);
        if (L.RvaGameStarted is uint gs) m.B(Base + gs, true);
        m.I(Base + L.RvaGameTurn, 42);
        m.I(Base + L.RvaActivePlayer, 0);
        m.Block(players, L.PlayerSize * 2);
        for (int p = 0; p < 2; p++)
        {
            uint pp = players + (uint)(p * L.PlayerSize);
            m.I(pp, (int)(Base + L.VtPlayerAI)); m.I(pp + (uint)L.PlayerId, p); m.B(pp + (uint)L.PlayerAlive, true);
        }
        // player 0: two unit slots (second one is a torn/freed object), one city
        uint nodes = 0x40000000, unit = 0x41000000, bad = 0x41100000, cnodes = 0x42000000, city = 0x43000000;
        m.Block(nodes, 16); m.Block(unit, L.UnitHeadBytes + 0x1000); m.Block(bad, L.UnitHeadBytes + 0x1000);
        m.I(players + (uint)(L.PlayerUnits + L.ListLastIndex), 1);
        m.I(players + (uint)(L.PlayerUnits + 0x10), 64);
        m.I(players + (uint)(L.PlayerUnits + L.ListArray), (int)nodes);
        m.I(nodes + 4, (int)unit); m.I(nodes + 12, (int)bad);
        m.I(unit, (int)(Base + L.VtUnitArchive)); m.I(unit + (uint)L.UnitOwner, 0); m.I(unit + (uint)L.UnitId, 8192);
        m.I(unit + (uint)L.UnitX, 41); m.I(unit + (uint)L.UnitY, 14); m.I(unit + (uint)L.UnitDamage, 27); m.I(unit + (uint)L.UnitMoves, 120);
        m.I(unit + (uint)L.UnitType, 82); m.I(unit + (uint)L.UnitFortifyTurns, 0);
        m.I(bad, 0x12345678); // freed memory: wrong vtable
        m.Block(cnodes, 8); m.Block(city, 0xC9C);
        m.I(players + (uint)(L.PlayerCities + L.ListLastIndex), 0);
        m.I(players + (uint)(L.PlayerCities + 0x10), 64);
        m.I(players + (uint)(L.PlayerCities + L.ListArray), (int)cnodes);
        m.I(cnodes + 4, (int)city);
        m.I(city, (int)(Base + L.VtCityAI)); m.I(city + (uint)L.CityOwner, 0); m.I(city + (uint)L.CityId, 8192);
        m.I(city + (uint)L.CityX, 27); m.I(city + (uint)L.CityY, 30); m.I(city + (uint)L.CityPopulation, 5); m.I(city + (uint)L.CityDamage, 12);
        m.Str(city + (uint)L.CityName!.Value, "Buenos Aires");
        return (m, unit, city);
    }

    [Theory, MemberData(nameof(Layouts))]
    public void ReadsUnitsCitiesAndTurn(string layoutId)
    {
        var L = ById(layoutId);
        var (m, _, _) = FakeGame(L);
        var r = new StateReader(m, Base, L);
        Assert.True(r.CheckGlobals(out _));
        var (st, s, _) = r.Read(5);
        Assert.Equal(SampleStatus.Ok, st);
        Assert.Equal(42, s!.Turn);
        Assert.Equal(80, s.MapWidth);
        var u = Assert.Single(s.Units);
        Assert.Equal((0, 8192, 41, 14, 27, 120, 82), (u.Owner, u.Id, u.X, u.Y, u.Damage, u.Moves, u.Type));
        Assert.Equal(1, s.Rejected); // the freed object is skipped, not read as a unit
        var c = Assert.Single(s.Cities);
        Assert.Equal(("Buenos Aires", 5, 12, 27, 30), (c.Name, c.Population, c.Damage, c.X, c.Y));
        Assert.Equal(2, s.Players.Count);
    }

    [Fact]
    public void RejectsImpossibleUnits()
    {
        var (m, unit, _) = FakeGame();
        m.I(unit + (uint)L.UnitX, 500); // off the map: a torn read
        var (_, s, _) = new StateReader(m, Base, L).Read(0);
        Assert.Empty(s!.Units);
        Assert.Equal(2, s.Rejected);
    }

    [Fact]
    public void WrongGlobalsVtableIsRefused()
    {
        var (m, _, _) = FakeGame();
        m.I(Base + L.RvaGlobals, 0x1234);
        var r = new StateReader(m, Base, L);
        Assert.False(r.CheckGlobals(out _));
        Assert.Equal(SampleStatus.LayoutMismatch, r.Read(0).Status);
    }

    [Fact]
    public void NoGameWhileInMenus()
    {
        var (m, _, _) = FakeGame();
        m.B(Base + L.RvaGameStarted!.Value, false);
        Assert.Equal(SampleStatus.NoGame, new StateReader(m, Base, L).Read(0).Status);
    }

    [Fact]
    public void PeHeaderCheck()
    {
        var m = new FakeMemory();
        var b = m.Block(Base, 0x400);
        b[0] = (byte)'M'; b[1] = (byte)'Z'; BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x3C), 0x100);
        b[0x100] = (byte)'P'; b[0x101] = (byte)'E';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x108), L.PeTimeDateStamp);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x100 + 24 + 56), L.PeSizeOfImage);
        Assert.True(GameFinder.PeHeaderMatches(m, Base, L));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x108), 1);
        Assert.False(GameFinder.PeHeaderMatches(m, Base, L));
    }

    [Fact]
    public void UnknownDllHasNoLayout()
    {
        Assert.Null(KnownLayouts.BySha256(new string('0', 64)));
        Assert.Same(L, KnownLayouts.BySha256(L.DllSha256.ToUpperInvariant()));
    }
}
