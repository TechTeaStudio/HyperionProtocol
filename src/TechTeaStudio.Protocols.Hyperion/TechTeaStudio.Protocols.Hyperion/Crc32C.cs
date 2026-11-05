using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;

namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// CRC-32C (Castagnoli; polynomial 0x1EDC6F41, reflected 0x82F63B78), the checksum used by iSCSI, ext4,
/// Btrfs and SCTP. Uses the SSE4.2 / ARMv8 CRC instructions when available and a table-driven fallback otherwise.
/// </summary>
/// <remarks>
/// Convention: initial value and final XOR are both <c>0xFFFFFFFF</c>, so <c>Compute("123456789") == 0xE3069283</c>
/// and <c>Compute(empty) == 0</c>. <see cref="Append"/> lets you checksum data that arrives in pieces.
/// </remarks>
public static class Crc32C
{
    private const uint ReflectedPolynomial = 0x82F63B78u;

    private static readonly uint[] Table = BuildTable();

    /// <summary>Computes the CRC-32C of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>
    /// Continues a CRC-32C across blocks: pass the value returned for the previous block (or <c>0</c> for the first)
    /// and the next block of data. <c>Append(Append(0, a), b) == Compute(a + b)</c>.
    /// </summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        uint state = ~crc;

        if (Sse42.IsSupported)
            state = AppendSse42(state, data);
        else if (System.Runtime.Intrinsics.Arm.Crc32.IsSupported)
            state = AppendArm(state, data);
        else
            state = AppendTable(state, data);

        return ~state;
    }

    /// <summary>Table-driven implementation; used on hardware without CRC instructions and by tests.</summary>
    internal static uint ComputeSoftware(ReadOnlySpan<byte> data) => ~AppendTable(~0u, data);

    private static uint AppendSse42(uint state, ReadOnlySpan<byte> data)
    {
        int i = 0;

        if (Sse42.X64.IsSupported)
        {
            ulong state64 = state;
            for (; i + 8 <= data.Length; i += 8)
                state64 = Sse42.X64.Crc32(state64, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i, 8)));
            state = (uint)state64;
        }

        for (; i + 4 <= data.Length; i += 4)
            state = Sse42.Crc32(state, BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4)));

        for (; i < data.Length; i++)
            state = Sse42.Crc32(state, data[i]);

        return state;
    }

    private static uint AppendArm(uint state, ReadOnlySpan<byte> data)
    {
        int i = 0;

        if (System.Runtime.Intrinsics.Arm.Crc32.Arm64.IsSupported)
        {
            for (; i + 8 <= data.Length; i += 8)
                state = System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32C(state, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i, 8)));
        }

        for (; i + 4 <= data.Length; i += 4)
            state = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32C(state, BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4)));

        for (; i < data.Length; i++)
            state = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32C(state, data[i]);

        return state;
    }

    private static uint AppendTable(uint state, ReadOnlySpan<byte> data)
    {
        var table = Table;
        foreach (byte b in data)
            state = table[(byte)(state ^ b)] ^ (state >> 8);
        return state;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
                c = (c & 1) != 0 ? (c >> 1) ^ ReflectedPolynomial : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
