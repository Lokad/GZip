using System.Numerics;

namespace Lokad.GZip;

internal static class DeflateCodes
{
    internal const int EndOfBlock = 256;
    internal const int LengthStart = EndOfBlock + 1;
    internal const int LiteralCount = 286;
    internal const int DistanceCount = 30;
    internal const int MaximumLength = 258;
    internal const int MinimumLength = 3;

    internal static ReadOnlySpan<ushort> LengthBase => [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    internal static ReadOnlySpan<byte> LengthExtra => [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    internal static ReadOnlySpan<byte> BitLengthOrder => [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
    private static readonly byte[] LengthCodes = CreateLengths();

    internal static int LengthCode(int length) => LengthCodes[length];

    internal static int DistanceCode(int distance)
    {
        int value = distance - 1;
        if (value < 4)
            return value;
        int high = BitOperations.Log2((uint)value);
        return (high << 1) | ((value >> (high - 1)) & 1);
    }

    internal static int DistanceExtra(int symbol) => Math.Max((symbol >> 1) - 1, 0);

    internal static int DistanceBase(int symbol)
        => symbol < 4 ? symbol + 1 : ((2 + (symbol & 1)) << DistanceExtra(symbol)) + 1;

    internal static void FillFixedLiteralLengths(Span<byte> lengths)
    {
        lengths[..144].Fill(8);
        lengths[144..256].Fill(9);
        lengths[256..280].Fill(7);
        lengths[280..].Fill(8);
    }

    internal static int Reverse(int code, int length)
    {
        int reversed = 0;
        for (int i = 0; i < length; i++)
        {
            reversed = (reversed << 1) | (code & 1);
            code >>= 1;
        }
        return reversed;
    }

    private static byte[] CreateLengths()
    {
        var codes = new byte[MaximumLength + 1];
        ReadOnlySpan<ushort> bases = LengthBase;
        for (int i = 0; i < bases.Length - 1; i++)
            codes.AsSpan(bases[i], bases[i + 1] - bases[i]).Fill((byte)i);
        codes[MaximumLength] = (byte)(bases.Length - 1);
        return codes;
    }
}
