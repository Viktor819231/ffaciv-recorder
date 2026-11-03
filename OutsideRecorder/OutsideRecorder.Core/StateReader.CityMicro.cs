using System.Buffers.Binary;

namespace OutsideRecorderCore;

public sealed partial class StateReader
{
    long microAtMs = long.MinValue;
    int microTurn = -1;
    List<CityMicroState>? microSample;
    int? specialistCount, buildingCount;

    void BeginMicroSample(long timeMs, int turn, uint gc)
    {
        if (turn < microTurn) microAtMs = long.MinValue;
        microTurn = turn;
        if (!ReadCityMicro || l.CityMicro is not { } layout ||
            (microAtMs != long.MinValue && timeMs >= microAtMs && timeMs - microAtMs < CityMicroEveryMs)) return;
        microAtMs = timeMs;
        microSample = [];
        specialistCount = MicroVectorCount(gc + (uint)layout.SpecialistInfos, 256);
        var buildings = mem.Ptr(gc + (uint)layout.BuildingInfos);
        buildingCount = buildings is > 0 ? MicroVectorCount(buildings.Value, 4096) : null;
    }

    // MSVC vector: proxy at +0, first/end/capacity at +4/+8/+12 (PDB and BNW getNum*Infos).
    int? MicroVectorCount(uint address, int maximum)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!mem.TryRead(address, bytes)) return null;
        uint first = U(bytes, 4), end = U(bytes, 8), cap = U(bytes, 12);
        if (first == 0 || end < first || cap < end || (end - first) % 4 != 0 || (cap - first) % 4 != 0) return null;
        var count = (end - first) / 4;
        return count <= maximum ? (int)count : null;
    }

    CityMicroState ReadMicro(uint cityPtr, CityState city)
    {
        var unknown = new CityMicroState { Owner = city.Owner, CityId = city.Id, X = city.X, Y = city.Y, ReadAtMs = microAtMs };
        var layout = l.CityMicro!;
        var cityBytes = new byte[Math.Max(l.CityHeadBytes, layout.CityBytes)];
        // Failure in optional reads must not lose the existing city-health/population observation.
        if (!mem.TryRead(cityPtr, cityBytes) || U(cityBytes, 0) != Abs(l.VtCityAI) ||
            I(cityBytes, l.CityId) != city.Id || I(cityBytes, l.CityOwner) != city.Owner ||
            I(cityBytes, l.CityX) != city.X || I(cityBytes, l.CityY) != city.Y) return unknown;
        var result = unknown with
        {
            Food100 = I(cityBytes, layout.Food100), OverflowProductionRaw = I(cityBytes, layout.OverflowRaw),
            Queue = ReadMicroQueue(cityPtr + (uint)layout.Queue, cityBytes.AsSpan(layout.Queue, 12)),
        };
        uint citizens = U(cityBytes, layout.Citizens);
        var data = new byte[0x84];
        if (citizens == 0 || !mem.TryRead(citizens, data) || U(data, 0) != cityPtr || data[0x80] != 1 ||
            data[5] > 1 || data[0x18] > 1 || I(data, 0x14) is < -1 or > 32) return result;
        var specialists = ReadMicroCounts(U(data, 0x6C), specialistCount);
        var byBuilding = ReadMicroCounts(U(data, 0x74), buildingCount);
        var forcedByBuilding = ReadMicroCounts(U(data, 0x78), buildingCount);
        // Recheck the linked object and its pointers after copying arrays; reject a detected concurrent replacement.
        var check = new byte[data.Length];
        if (!mem.TryRead(citizens, check) || !data.AsSpan().SequenceEqual(check) ||
            mem.Ptr(cityPtr + (uint)layout.Citizens) != citizens || mem.Int(cityPtr + (uint)l.CityId) != city.Id ||
            mem.Int(cityPtr + (uint)l.CityOwner) != city.Owner) return unknown;
        return result with
        {
            Focus = I(data, 0x14), AvoidGrowth = data[0x18] != 0, NoAutoAssignSpecialists = data[5] != 0,
            WorkedPlots = MicroPlotIndices(data.AsSpan(0x19, 37)), ForcedPlots = MicroPlotIndices(data.AsSpan(0x3E, 37)),
            DefaultSpecialists = Count(I(data, 0x64)), ForcedDefaultSpecialists = Count(I(data, 0x68)),
            Specialists = specialists, SpecialistsByBuilding = byBuilding, ForcedSpecialistsByBuilding = forcedByBuilding,
        };
    }

    CityOrder[]? ReadMicroQueue(uint address, ReadOnlySpan<byte> header)
    {
        uint ptr = U(header, 0), count = U(header, 4), cap = U(header, 8);
        if (count > 256 || count > cap || cap > 4096 || (ptr == 0 && count != 0)) return null;
        var bytes = new byte[count * 16];
        if (count > 0 && !mem.TryRead(ptr, bytes)) return null;
        Span<byte> after = stackalloc byte[12];
        if (!mem.TryRead(address, after) || !header.SequenceEqual(after)) return null;
        var orders = new CityOrder[count];
        for (int i = 0; i < orders.Length; i++)
        {
            int at = i * 16;
            if (I(bytes, at) is < 0 or > 4 || bytes[at + 12] > 1) return null;
            orders[i] = new(I(bytes, at), I(bytes, at + 4), I(bytes, at + 8), bytes[at + 12] != 0);
        }
        return orders;
    }

    int[]? ReadMicroCounts(uint ptr, int? count)
    {
        if (count is null || (count > 0 && ptr == 0)) return null;
        var bytes = new byte[count.Value * 4];
        if (bytes.Length > 0 && !mem.TryRead(ptr, bytes)) return null;
        var values = new int[count.Value];
        for (int i = 0; i < values.Length; i++)
        {
            var value = Count(I(bytes, i * 4));
            if (value is null) return null;
            values[i] = value.Value;
        }
        return values;
    }

    static int? Count(int value) => value is >= 0 and <= 10000 ? value : null;
    static int[]? MicroPlotIndices(ReadOnlySpan<byte> values)
    {
        var result = new List<int>();
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > 1) return null;
            if (values[i] != 0) result.Add(i);
        }
        return result.ToArray();
    }
    static int I(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    static uint U(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
}
