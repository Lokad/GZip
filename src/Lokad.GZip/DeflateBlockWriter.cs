using System.Numerics;
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
    private static readonly uint[] FixedMatchLengths = CreateFixedMatchLengths();

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
        if (HasSymbols)
            ResetBlock();
        _cached ??= this;
    }

    internal bool HasSymbols => _symbolCount != 0;

    internal int RemainingSymbols => MaximumSymbols - _symbolCount;

    internal bool AddLiteral(byte value)
    {
        _literalFrequencies[value]++;
        _symbols[_symbolCount++] = new(value, 0);
        return _symbolCount == MaximumSymbols;
    }

    internal void AddLiterals(ReadOnlySpan<byte> values)
    {
        Span<Symbol> symbols = _symbols.AsSpan(_symbolCount, values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            byte value = values[i];
            _literalFrequencies[value]++;
            symbols[i] = new(value, 0);
        }
        _symbolCount += values.Length;
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

    internal void AddRepeatedMatch(int length, int distance, int count)
    {
        int lengthCode = DeflateCodes.LengthCode(length);
        int distanceCode = DeflateCodes.DistanceCode(distance);
        _literalFrequencies[DeflateCodes.LengthStart + lengthCode] += count;
        _distanceFrequencies[distanceCode] += count;
        _extraBits += count * (DeflateCodes.LengthExtra[lengthCode] + DeflateCodes.DistanceExtra(distanceCode));
        _symbols.AsSpan(_symbolCount, count).Fill(new((ushort)length, (ushort)distance));
        _symbolCount += count;
    }

    internal void Write(DeflateBits bits, ReadOnlySpan<byte> original, bool final, bool forceFixed)
    {
        if (!HasSymbols)
        {
            StartFixed(bits, final);
            EndFixed(bits, final);
            return;
        }

        if (CanSkipTreesForStoredBlock(original.Length, forceFixed))
        {
            WriteStored(bits, original, final);
            ResetBlock();
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

    private bool CanSkipTreesForStoredBlock(int originalLength, bool forceFixed)
    {
        if (_symbolCount < 4096 || originalLength < _symbolCount || originalLength - _symbolCount > (originalLength >> 6))
            return false;

        // Invert the block-size comparison after accounting for match extra bits.
        int minimumBits = (originalLength + 4) * 8 - 10 - _extraBits;
        if (forceFixed)
            return FixedLiterals.CountBits(_literalFrequencies) + FixedDistances.CountBits(_distanceFrequencies) >= minimumBits;

        // Every dynamic block uses at least 14 count bits and four 3-bit code-length values.
        const int minimumHeader = 26;
        const int maximumCodeLength = 15;
        const long scale = 1L << maximumCodeLength;
        // This penalty favors eight-bit codes at the frequencies typical of a nearly literal block.
        int penalty = _symbolCount + _symbolCount / 3;

        // The code-space shares 2^-length sum to at most one in any prefix tree.
        // Minimize weight * length + penalty * share for each literal or length symbol.
        // Subtracting the full penalty bounds the bit count of every valid tree from below.
        long bound = -(penalty * scale);
        foreach (int weight in _literalFrequencies)
        {
            if (weight == 0)
                continue;

            int length = Math.Clamp(BitOperations.Log2((uint)(penalty / weight)), 1, maximumCodeLength);
            bound += (long)weight * length * scale + ((long)penalty << (maximumCodeLength - length));
        }
        if (((bound + scale - 1) >> maximumCodeLength) + minimumHeader < minimumBits)
            return false;

        return FixedLiterals.CountBits(_literalFrequencies) + FixedDistances.CountBits(_distanceFrequencies) >= minimumBits;
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
    {
        uint lengthCode = FixedMatchLengths[length];
        int lengthWidth = (int)(lengthCode & 31);
        int distanceSymbol = DeflateCodes.DistanceCode(distance);
        int extraWidth = DeflateCodes.DistanceExtra(distanceSymbol);
        uint extra = (uint)(distance - 1) & ((1u << extraWidth) - 1);
        uint distanceCode = FixedDistances.Code(distanceSymbol) | (extra << 5);
        ulong code = (lengthCode >> 5) | ((ulong)distanceCode << lengthWidth);
        bits.Write(code, lengthWidth + 5 + extraWidth);
    }

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
        ReadOnlySpan<Symbol> symbols = _symbols.AsSpan(0, _symbolCount);
        for (int index = 0; index < symbols.Length;)
        {
            Symbol symbol = symbols[index++];
            if (symbol.Distance == 0)
            {
                literals.Send(bits, symbol.Value);
                continue;
            }

            ulong code = EncodeMatch(symbol.Value, symbol.Distance, literals, distances, out int width);
            if (index < symbols.Length && symbols[index] == symbol)
            {
                int start = index++;
                while (index < symbols.Length && symbols[index] == symbol)
                    index++;
                bits.WriteRepeated(code, width, index - start + 1);
            }
            else
            {
                bits.Write(code, width);
            }
        }
        literals.Send(bits, DeflateCodes.EndOfBlock);
    }

    private static ulong EncodeMatch(int length, int distance, DeflateCodeTree literals, DeflateCodeTree distances, out int width)
    {
        ulong code;
        if (length == DeflateCodes.MaximumLength)
        {
            const int longestSymbol = DeflateCodes.LiteralCount - 1;
            code = literals.Code(longestSymbol);
            width = literals.Lengths[longestSymbol];
        }
        else
        {
            int lengthCode = DeflateCodes.LengthCode(length);
            int symbol = DeflateCodes.LengthStart + lengthCode;
            width = literals.Lengths[symbol];
            code = literals.Code(symbol) | ((ulong)(length - DeflateCodes.LengthBase[lengthCode]) << width);
            width += DeflateCodes.LengthExtra[lengthCode];
        }

        int distanceCode = DeflateCodes.DistanceCode(distance);
        code |= (ulong)distances.Code(distanceCode) << width;
        width += distances.Lengths[distanceCode];
        code |= (ulong)(distance - DeflateCodes.DistanceBase(distanceCode)) << width;
        width += DeflateCodes.DistanceExtra(distanceCode);
        return code;
    }

    private static uint[] CreateFixedMatchLengths()
    {
        var lengths = new uint[DeflateCodes.MaximumLength + 1];
        for (int length = DeflateCodes.MinimumLength; length < lengths.Length; length++)
        {
            // Distance one appends five zero bits and no extra bits.
            ulong code = EncodeMatch(length, 1, FixedLiterals, FixedDistances, out int width);
            lengths[length] = ((uint)code << 5) | (uint)(width - 5);
        }
        return lengths;
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
