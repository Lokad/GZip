using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace Lokad.GZip;

internal static partial class Crc32
{
    internal const uint Initial = uint.MaxValue;
    private static readonly uint[] Table = CreateTable();

    internal static uint Update(uint crc, byte value) => Table[(byte)(crc ^ value)] ^ (crc >> 8);

    internal static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        if (Pclmulqdq.IsSupported && bytes.Length >= 64)
            crc = UpdatePclmul(crc, ref bytes);

        if (ArmCrc32.Arm64.IsSupported)
        {
            while (bytes.Length >= sizeof(ulong))
            {
                crc = ArmCrc32.Arm64.ComputeCrc32(crc, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
                bytes = bytes[sizeof(ulong)..];
            }
        }
        else
        {
            while (bytes.Length >= sizeof(ulong))
            {
                ulong block = BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^ crc;
                crc = Table[7 * 256 + (byte)block] ^ Table[6 * 256 + (byte)(block >> 8)] ^
                    Table[5 * 256 + (byte)(block >> 16)] ^ Table[4 * 256 + (byte)(block >> 24)] ^
                    Table[3 * 256 + (byte)(block >> 32)] ^ Table[2 * 256 + (byte)(block >> 40)] ^
                    Table[256 + (byte)(block >> 48)] ^ Table[(byte)(block >> 56)];
                bytes = bytes[sizeof(ulong)..];
            }
        }
        foreach (byte value in bytes)
            crc = Update(crc, value);
        return crc;
    }

    private static uint[] CreateTable()
    {
        const uint polynomial = 0xEDB88320;
        var table = new uint[8 * 256];
        for (uint value = 0; value < 256; value++)
        {
            uint code = value;
            for (int bit = 0; bit < 8; bit++)
                code = (code >> 1) ^ ((code & 1) != 0 ? polynomial : 0);
            table[value] = code;
        }
        for (int index = 256; index < table.Length; index++)
        {
            uint code = table[index - 256];
            table[index] = table[(byte)code] ^ (code >> 8);
        }
        return table;
    }
}
