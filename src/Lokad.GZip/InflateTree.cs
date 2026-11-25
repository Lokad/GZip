using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lokad.GZip;

internal sealed class InflateTree
{
    internal const int RootBits = 9;
    private const int MaxBits = 15;
    private const int RootSize = 1 << RootBits;
    private readonly ushort[] _root = new ushort[RootSize];
    private readonly ushort[] _symbols;
    private readonly int[] _counts = new int[MaxBits + 1];
    private readonly int[] _firstCode = new int[MaxBits + 1];
    private readonly int[] _firstSymbol = new int[MaxBits + 1];
    private int _maximumLength;
    private int _lookupBits;

    internal ReadOnlySpan<ushort> Root => _root;

    internal InflateTree(int symbols) => _symbols = new ushort[symbols];

    internal void Build(ReadOnlySpan<byte> lengths, bool codeLengths = false, bool allowEmpty = false)
    {
        Debug.Assert(lengths.Length <= _symbols.Length);
        Array.Clear(_root);
        Array.Clear(_counts);
        _maximumLength = 0;
        foreach (byte length in lengths)
        {
            Debug.Assert(length <= MaxBits);
            if (length > 0)
            {
                _counts[length]++;
                _maximumLength = Math.Max(_maximumLength, length);
            }
        }

        _lookupBits = Math.Min(RootBits, _maximumLength);
        if (_maximumLength == 0)
        {
            if (!allowEmpty)
                throw new InvalidDataException("Empty DEFLATE Huffman tree.");
            return;
        }

        Span<int> next = stackalloc int[MaxBits + 1];
        int slots = 1;
        int firstCode = 0;
        int firstSymbol = 0;
        for (int length = 1; length <= _maximumLength; length++)
        {
            firstCode = (firstCode + _counts[length - 1]) << 1;
            slots = (slots << 1) - _counts[length];
            if (slots < 0)
                throw new InvalidDataException("Oversubscribed DEFLATE Huffman tree.");
            next[length] = _firstCode[length] = firstCode;
            _firstSymbol[length] = firstSymbol;
            firstSymbol += _counts[length];
        }
        if (slots != 0 && (codeLengths || _maximumLength != 1))
            throw new InvalidDataException("Incomplete DEFLATE Huffman tree.");

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            if (length == 0)
                continue;

            int code = next[length]++;
            _symbols[_firstSymbol[length] + code - _firstCode[length]] = (ushort)symbol;
            if (length <= RootBits)
            {
                int wireCode = DeflateCodes.Reverse(code, length);
                ushort entry = (ushort)((length << RootBits) | symbol);
                int stride = 1 << length;
                for (int i = wireCode; i < RootSize; i += stride)
                    _root[i] = entry;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryRead(ref BitReader bits, ref ReadOnlySpan<byte> input, out int symbol)
    {
        int lookupBits = _lookupBits;
        if (bits.Count < lookupBits && !bits.Fill(ref input, lookupBits))
            return TryReadSlow(ref bits, ref input, out symbol);

        ushort entry = _root[bits.Peek(RootBits)];
        if (entry != 0)
        {
            bits.Consume(entry >> RootBits);
            symbol = entry & (RootSize - 1);
            return true;
        }

        return TryReadSlow(ref bits, ref input, out symbol);
    }

    private bool TryReadSlow(ref BitReader bits, ref ReadOnlySpan<byte> input, out int symbol)
    {
        int code = 0;
        for (int length = 1; length <= _maximumLength; length++)
        {
            if (!bits.Fill(ref input, length))
            {
                symbol = 0;
                return false;
            }

            code = (code << 1) | (int)(bits.Peek(length) >> (length - 1));
            int index = code - _firstCode[length];
            if ((uint)index < (uint)_counts[length])
            {
                bits.Consume(length);
                symbol = _symbols[_firstSymbol[length] + index];
                return true;
            }
        }

        throw new InvalidDataException("Invalid DEFLATE Huffman code.");
    }

}
