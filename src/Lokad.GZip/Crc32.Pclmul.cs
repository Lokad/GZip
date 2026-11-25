using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lokad.GZip;

internal static partial class Crc32
{
    // The reflected residues of x^544, x^480, x^160, x^96 and x^64 modulo the IEEE polynomial.
    private const ulong FoldWideLow = 0x154442bd4;
    private const ulong FoldWideHigh = 0x1c6e41596;
    private const ulong FoldNarrowLow = 0x1751997d0;
    private const ulong FoldNarrowHigh = 0x0ccaa009e;
    private const ulong FoldToWord = 0x163cd6124;

    // Reflected polynomial and quotient for Barrett reduction.
    private const ulong Polynomial = 0x1db710641;
    private const ulong BarrettQuotient = 0x1f7011641;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint UpdatePclmul(uint crc, ref ReadOnlySpan<byte> bytes)
    {
        var narrow = Vector128.Create(FoldNarrowLow, FoldNarrowHigh);
        Vector128<ulong> remainder = Pclmulqdq.V512.IsSupported && bytes.Length >= 1024
            ? FoldWide512(crc, ref bytes, narrow)
            : FoldWide128(crc, ref bytes, narrow);
        while (bytes.Length >= 16)
        {
            remainder = Fold(remainder, narrow) ^ Vector128.Create(bytes).AsUInt64();
            bytes = bytes[16..];
        }

        Vector128<ulong> mask = Vector128.Create((ulong)uint.MaxValue);
        remainder = Sse2.ShiftRightLogical128BitLane(remainder, 8) ^
            Pclmulqdq.CarrylessMultiply(remainder, Vector128.CreateScalar(FoldNarrowHigh), 0x00);
        remainder = Sse2.ShiftRightLogical128BitLane(remainder, 4) ^
            Pclmulqdq.CarrylessMultiply(remainder & mask, Vector128.CreateScalar(FoldToWord), 0x00);

        Vector128<ulong> reduction = Vector128.Create(Polynomial, BarrettQuotient);
        Vector128<ulong> quotient = Pclmulqdq.CarrylessMultiply(remainder & mask, reduction, 0x10) & mask;
        return (remainder ^ Pclmulqdq.CarrylessMultiply(quotient, reduction, 0x00)).AsUInt32().GetElement(1);
    }

    private static Vector128<ulong> FoldWide128(uint crc, ref ReadOnlySpan<byte> bytes, Vector128<ulong> narrow)
    {
        var wide = Vector128.Create(FoldWideLow, FoldWideHigh);
        var first = Vector128.Create(bytes).AsUInt64() ^ Vector128.CreateScalar((ulong)crc);
        var second = Vector128.Create(bytes[16..]).AsUInt64();
        var third = Vector128.Create(bytes[32..]).AsUInt64();
        var fourth = Vector128.Create(bytes[48..]).AsUInt64();
        bytes = bytes[64..];

        while (bytes.Length >= 64)
        {
            first = Fold(first, wide) ^ Vector128.Create(bytes).AsUInt64();
            second = Fold(second, wide) ^ Vector128.Create(bytes[16..]).AsUInt64();
            third = Fold(third, wide) ^ Vector128.Create(bytes[32..]).AsUInt64();
            fourth = Fold(fourth, wide) ^ Vector128.Create(bytes[48..]).AsUInt64();
            bytes = bytes[64..];
        }

        Vector128<ulong> remainder = Fold(first, narrow) ^ second;
        remainder = Fold(remainder, narrow) ^ third;
        remainder = Fold(remainder, narrow) ^ fourth;
        return remainder;
    }

    private static Vector128<ulong> FoldWide512(uint crc, ref ReadOnlySpan<byte> bytes, Vector128<ulong> narrow)
    {
        // Eight interleaved 128-bit lanes advance 128 bytes per fold.
        // These are the reflected residues of x^1056 and x^992.
        var lane = Vector128.Create(0x1e88ef372ul, 0x14a7fe880ul);
        var pair = Vector256.Create(lane, lane);
        var wide = Vector512.Create(pair, pair);
        var first = Vector512.Create(bytes).AsUInt64() ^ Vector512.CreateScalar((ulong)crc);
        var second = Vector512.Create(bytes[64..]).AsUInt64();
        bytes = bytes[128..];

        while (bytes.Length >= 128)
        {
            first = Fold(first, wide) ^ Vector512.Create(bytes).AsUInt64();
            second = Fold(second, wide) ^ Vector512.Create(bytes[64..]).AsUInt64();
            bytes = bytes[128..];
        }

        // Collapse the lanes in input order before applying the shared tail reduction.
        Vector128<ulong> remainder = Fold(first.GetLower().GetLower(), narrow) ^ first.GetLower().GetUpper();
        remainder = Fold(remainder, narrow) ^ first.GetUpper().GetLower();
        remainder = Fold(remainder, narrow) ^ first.GetUpper().GetUpper();
        remainder = Fold(remainder, narrow) ^ second.GetLower().GetLower();
        remainder = Fold(remainder, narrow) ^ second.GetLower().GetUpper();
        remainder = Fold(remainder, narrow) ^ second.GetUpper().GetLower();
        return Fold(remainder, narrow) ^ second.GetUpper().GetUpper();
    }

    private static Vector512<ulong> Fold(Vector512<ulong> value, Vector512<ulong> residues)
        => Pclmulqdq.V512.CarrylessMultiply(value, residues, 0x00) ^ Pclmulqdq.V512.CarrylessMultiply(value, residues, 0x11);

    private static Vector128<ulong> Fold(Vector128<ulong> value, Vector128<ulong> residues)
        => Pclmulqdq.CarrylessMultiply(value, residues, 0x00) ^ Pclmulqdq.CarrylessMultiply(value, residues, 0x11);
}
