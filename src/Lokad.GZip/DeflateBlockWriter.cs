using System.Buffers;
using System.Buffers.Binary;

namespace Lokad.GZip;

// Dynamic, fixed and stored block selection follows zlib-ng's trees.c.
internal sealed class DeflateBlockWriter : IDisposable
{
    private const int MaximumSymbols = 16383;
    private const int LengthCodeCount = 19;
    private static readonly DeflateCodeTree FixedLiterals = CreateFixedLiterals();
    private static readonly DeflateCodeTree FixedDistances = CreateFixedDistances();

    private readonly record struct Symbol(ushort Value, ushort Distance);
    private readonly record struct LengthRun(byte Symbol, byte Value, byte Bits);

    [ThreadStatic]
    private static DeflateBlockWriter? _cached;
    private Symbol[] _symbols = [];
    private readonly LengthRun[] _runs = new LengthRun[DeflateCodes.LiteralCount + DeflateCodes.DistanceCount];
    private readonly int[] _literalFrequencies = new int[DeflateCodes.LiteralCount];
    private readonly int[] _distanceFrequencies = new int[DeflateCodes.DistanceCount];
    private readonly int[] _runFrequencies = new int[LengthCodeCount];
    private readonly DeflateCodeTree _literals = new(DeflateCodes.LiteralCount);
    private readonly DeflateCodeTree _distances = new(DeflateCodes.DistanceCount);
    private readonly DeflateCodeTree _lengths = new(LengthCodeCount);
    private int _extraBits;
    private int _symbolCount;
    private int _runCount;
    private int _runBits;

    private DeflateBlockWriter() => _literalFrequencies[DeflateCodes.EndOfBlock] = 1;

    internal static DeflateBlockWriter Rent()
    {
        DeflateBlockWriter writer = _cached ?? new();
        _cached = null;
        writer._symbols = ArrayPool<Symbol>.Shared.Rent(MaximumSymbols);
        return writer;
    }

    public void Dispose()
    {
        if (_symbols.Length == 0)
            return;

        ArrayPool<Symbol>.Shared.Return(_symbols);
        _symbols = [];
        ResetBlock();
        _cached ??= this;
    }

    internal bool HasSymbols => _symbolCount != 0;

    internal bool AddLiteral(byte value)
    {
        _literalFrequencies[value]++;
        _symbols[_symbolCount++] = new(value, 0);
        return _symbolCount == MaximumSymbols;
    }

    internal bool AddMatch(int length, int distance)
    {
        int lengthCode = DeflateCodes.LengthCode(length);
        int distanceCode = DeflateCodes.DistanceCode(distance);
        _literalFrequencies[DeflateCodes.LengthStart + lengthCode]++;
        _distanceFrequencies[distanceCode]++;
        _extraBits += DeflateCodes.LengthExtra[lengthCode] + DeflateCodes.DistanceExtra(distanceCode);
        _symbols[_symbolCount++] = new((ushort)length, (ushort)distance);
        return _symbolCount == MaximumSymbols;
    }

    internal void Write(DeflateBits bits, ReadOnlySpan<byte> original, bool final, bool forceFixed)
    {
        if (!HasSymbols)
        {
            StartFixed(bits, final);
            EndFixed(bits, final);
            return;
        }

        int literalCount = _literals.Build(_literalFrequencies);
        int distanceCount = _distances.Build(_distanceFrequencies);
        CollectRuns(_literals.Lengths[..literalCount]);
        CollectRuns(_distances.Lengths[..distanceCount]);
        _lengths.Build(_runFrequencies, limit: 7);

        ReadOnlySpan<byte> order = DeflateCodes.BitLengthOrder;
        int codeLengthCount = order.Length;
        while (codeLengthCount > 4 && _lengths.Lengths[order[codeLengthCount - 1]] == 0)
            codeLengthCount--;

        int fixedBits = FixedLiterals.CountBits(_literalFrequencies) + FixedDistances.CountBits(_distanceFrequencies);
        int dynamicBits = _literals.CountBits(_literalFrequencies) + _distances.CountBits(_distanceFrequencies) +
            _lengths.CountBits(_runFrequencies) + _runBits + 3 * codeLengthCount + 14;
        int fixedBytes = (fixedBits + _extraBits + 10) >> 3;
        int dynamicBytes = (dynamicBits + _extraBits + 10) >> 3;
        bool useFixed = forceFixed || fixedBytes <= dynamicBytes;
        int compressedBytes = useFixed ? fixedBytes : dynamicBytes;

        if (!original.IsEmpty && original.Length + 4 <= compressedBytes)
        {
            WriteStored(bits, original, final);
        }
        else if (useFixed)
        {
            StartFixed(bits, final);
            WriteSymbols(bits, FixedLiterals, FixedDistances);
        }
        else
        {
            bits.Write(final ? 5u : 4u, 3);
            bits.Write((uint)(literalCount - 257), 5);
            bits.Write((uint)(distanceCount - 1), 5);
            bits.Write((uint)(codeLengthCount - 4), 4);
            foreach (byte symbol in order[..codeLengthCount])
                bits.Write(_lengths.Lengths[symbol], 3);
            foreach (LengthRun run in _runs.AsSpan(0, _runCount))
            {
                _lengths.Send(bits, run.Symbol);
                bits.Write(run.Value, run.Bits);
            }
            WriteSymbols(bits, _literals, _distances);
        }

        ResetBlock();
        if (final)
            bits.Align();
    }

    private void ResetBlock()
    {
        Array.Clear(_literalFrequencies);
        _literalFrequencies[DeflateCodes.EndOfBlock] = 1;
        Array.Clear(_distanceFrequencies);
        Array.Clear(_runFrequencies);
        _extraBits = _symbolCount = _runCount = _runBits = 0;
    }

    internal static void StartFixed(DeflateBits bits, bool final) => bits.Write(final ? 3u : 2u, 3);

    internal static void WriteFixedLiteral(DeflateBits bits, byte value) => FixedLiterals.Send(bits, value);

    internal static void WriteFixedMatch(DeflateBits bits, int length, int distance)
        => WriteMatch(bits, length, distance, FixedLiterals, FixedDistances);

    internal static void EndFixed(DeflateBits bits, bool final)
    {
        FixedLiterals.Send(bits, DeflateCodes.EndOfBlock);
        if (final)
            bits.Align();
    }

    internal static void WriteStored(DeflateBits bits, ReadOnlySpan<byte> bytes, bool final)
    {
        bits.Write(final ? 1u : 0u, 3);
        bits.Align();
        Span<byte> destination = bits.ReserveAligned(4 + bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)~bytes.Length);
        bytes.CopyTo(destination[4..]);
    }

    private void CollectRuns(ReadOnlySpan<byte> lengths)
    {
        int previous = -1;
        int max = lengths[0] == 0 ? 138 : 7;
        int min = lengths[0] == 0 ? 3 : 4;
        int count = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            int current = lengths[i];
            int next = i + 1 < lengths.Length ? lengths[i + 1] : -1;
            if (++count < max && current == next)
                continue;
            if (count < min)
            {
                for (int n = 0; n < count; n++)
                    AddRun(current);
            }
            else if (current != 0)
            {
                if (current != previous)
                {
                    AddRun(current);
                    count--;
                }
                AddRun(16, count - 3, 2);
            }
            else if (count <= 10)
            {
                AddRun(17, count - 3, 3);
            }
            else
            {
                AddRun(18, count - 11, 7);
            }

            previous = current;
            count = 0;
            (min, max) = next == 0 ? (3, 138) : current == next ? (3, 6) : (4, 7);
        }
    }

    private void AddRun(int symbol, int value = 0, int width = 0)
    {
        _runs[_runCount++] = new((byte)symbol, (byte)value, (byte)width);
        _runFrequencies[symbol]++;
        _runBits += width;
    }

    private void WriteSymbols(DeflateBits bits, DeflateCodeTree literals, DeflateCodeTree distances)
    {
        foreach (Symbol symbol in _symbols.AsSpan(0, _symbolCount))
        {
            if (symbol.Distance == 0)
                literals.Send(bits, symbol.Value);
            else
                WriteMatch(bits, symbol.Value, symbol.Distance, literals, distances);
        }
        literals.Send(bits, DeflateCodes.EndOfBlock);
    }

    private static void WriteMatch(DeflateBits bits, int length, int distance, DeflateCodeTree literals, DeflateCodeTree distances)
    {
        int lengthCode = DeflateCodes.LengthCode(length);
        int symbol = DeflateCodes.LengthStart + lengthCode;
        int width = literals.Lengths[symbol];
        ulong code = literals.Code(symbol) | ((ulong)(length - DeflateCodes.LengthBase[lengthCode]) << width);
        width += DeflateCodes.LengthExtra[lengthCode];

        int distanceCode = DeflateCodes.DistanceCode(distance);
        code |= (ulong)distances.Code(distanceCode) << width;
        width += distances.Lengths[distanceCode];
        code |= (ulong)(distance - DeflateCodes.DistanceBase(distanceCode)) << width;
        bits.Write(code, width + DeflateCodes.DistanceExtra(distanceCode));
    }

    private static DeflateCodeTree CreateFixedLiterals()
    {
        Span<byte> lengths = stackalloc byte[288];
        DeflateCodes.FillFixedLiteralLengths(lengths);
        var tree = new DeflateCodeTree(288);
        tree.SetLengths(lengths);
        return tree;
    }

    private static DeflateCodeTree CreateFixedDistances()
    {
        Span<byte> lengths = stackalloc byte[32];
        lengths.Fill(5);
        var tree = new DeflateCodeTree(32);
        tree.SetLengths(lengths);
        return tree;
    }
}
