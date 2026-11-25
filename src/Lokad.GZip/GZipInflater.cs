using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lokad.GZip;

internal enum InflateStatus
{
    NeedInput,
    NeedOutput,
    Done,
}

internal sealed class GZipInflater : IDisposable
{
    private const int WindowSize = 32768;
    private const int WindowMask = WindowSize - 1;
    private const int HeaderCrc = 2;
    private const int HeaderExtra = 4;
    private const int HeaderName = 8;
    private const int HeaderComment = 16;
    private const int EndOfBlock = DeflateCodes.EndOfBlock;
    private const int MaxLiterals = DeflateCodes.LiteralCount;
    private const int MaxDistances = DeflateCodes.DistanceCount;

    private static readonly InflateTree FixedLiterals = CreateFixedLiterals();
    private static readonly InflateTree FixedDistances = CreateFixedDistances();

    private enum State
    {
        Header,
        HeaderFlags,
        HeaderTail,
        ExtraLength,
        Extra,
        HeaderString,
        HeaderChecksum,
        BlockHeader,
        StoredLength,
        StoredData,
        DynamicHeader,
        CodeLengthAlphabet,
        CodeLengths,
        RepeatLength,
        Symbol,
        Literal,
        Length,
        Distance,
        DistanceExtra,
        Match,
        Trailer,
        NextMember,
        Done,
    }

    private sealed class DynamicTrees
    {
        [ThreadStatic]
        private static DynamicTrees? _cached;

        internal readonly byte[] CodeLengths = new byte[19];
        internal readonly byte[] Lengths = new byte[MaxLiterals + MaxDistances];
        internal readonly InflateTree CodeLengthTree = new(19);
        internal readonly InflateTree Literals = new(MaxLiterals);
        internal readonly InflateTree Distances = new(MaxDistances);

        internal static DynamicTrees Rent()
        {
            DynamicTrees trees = _cached ?? new();
            _cached = null;
            return trees;
        }

        internal void Return() => _cached ??= this;
    }

    private readonly byte[] _history = ArrayPool<byte>.Shared.Rent(WindowSize);
    private readonly byte[] _field = new byte[10];
    private readonly DynamicTrees _trees = DynamicTrees.Rent();
    private BitReader _bits;
    private State _state;
    private InflateTree _literals = FixedLiterals;
    private InflateTree _distances = FixedDistances;
    private int _fieldCount;
    private int _headerFlags;
    private uint _headerCrc = Crc32.Initial;
    private uint _crc = Crc32.Initial;
    private uint _size;
    private int _writePosition;
    private int _historyCount;
    private bool _finalBlock;
    private int _literalCount;
    private int _distanceCount;
    private int _codeLengthCount;
    private int _lengthIndex;
    private int _symbol;
    private int _remaining;
    private int _distance;

    internal bool FinishedMember => _state is State.NextMember or State.Done;
    internal bool HasTrailingInput => _state == State.Done;

    internal int BufferedInputBytes => _bits.Count / 8;

    public void Dispose()
    {
        _trees.Return();
        ArrayPool<byte>.Shared.Return(_history);
    }

    internal InflateStatus Decode(ReadOnlySpan<byte> input, Span<byte> output, out int consumed, out int written)
    {
        int inputLength = input.Length;
        int outputLength = output.Length;
        Span<byte> memberOutput = output;
        InflateStatus status;
        try
        {
            status = Process(ref input, ref output, ref memberOutput);
        }
        finally
        {
            CommitOutput(output, ref memberOutput);
        }
        consumed = inputLength - input.Length;
        written = outputLength - output.Length;
        return status;
    }

    private InflateStatus Process(ref ReadOnlySpan<byte> input, ref Span<byte> output, ref Span<byte> memberOutput)
    {
        while (true)
        {
            switch (_state)
            {
                case State.Header:
                    if (!ReadField(ref input, 2, checksum: true))
                        return InflateStatus.NeedInput;
                    if (_field[0] != 0x1f || _field[1] != 0x8b)
                        throw new InvalidDataException("Invalid gzip header.");
                    _state = State.HeaderFlags;
                    break;

                case State.HeaderFlags:
                    if (!ReadField(ref input, 2, checksum: true))
                        return InflateStatus.NeedInput;
                    if (_field[0] != 8 || (_field[1] & 0xe0) != 0)
                        throw new InvalidDataException("Invalid gzip method or header flags.");
                    _headerFlags = _field[1];
                    _state = State.HeaderTail;
                    break;

                case State.HeaderTail:
                    if (!ReadField(ref input, 6, checksum: true))
                        return InflateStatus.NeedInput;
                    AdvanceHeader();
                    break;

                case State.ExtraLength:
                    if (!ReadField(ref input, 2, checksum: true))
                        return InflateStatus.NeedInput;
                    _remaining = BinaryPrimitives.ReadUInt16LittleEndian(_field);
                    _state = State.Extra;
                    break;

                case State.Extra:
                    while (_remaining > 0)
                    {
                        if (!_bits.TryRead(ref input, 8, out uint extra))
                            return InflateStatus.NeedInput;
                        _headerCrc = Crc32.Update(_headerCrc, (byte)extra);
                        _remaining--;
                    }
                    AdvanceHeader();
                    break;

                case State.HeaderString:
                    if (!_bits.TryRead(ref input, 8, out uint next))
                        return InflateStatus.NeedInput;
                    _headerCrc = Crc32.Update(_headerCrc, (byte)next);
                    if (next == 0)
                        AdvanceHeader();
                    break;

                case State.HeaderChecksum:
                    if (!ReadField(ref input, 2, checksum: false))
                        return InflateStatus.NeedInput;
                    if (BinaryPrimitives.ReadUInt16LittleEndian(_field) != (ushort)~_headerCrc)
                        throw new InvalidDataException("Invalid gzip header checksum.");
                    AdvanceHeader();
                    break;

                case State.BlockHeader:
                    if (!_bits.TryRead(ref input, 3, out uint header))
                        return InflateStatus.NeedInput;
                    StartBlock(header);
                    break;

                case State.StoredLength:
                    if (!_bits.TryRead(ref input, 32, out uint storedLength))
                        return InflateStatus.NeedInput;
                    _remaining = (ushort)storedLength;
                    if ((ushort)(storedLength >> 16) != (ushort)~_remaining)
                        throw new InvalidDataException("Invalid stored DEFLATE block length.");
                    _state = State.StoredData;
                    break;

                case State.StoredData:
                    if (_remaining == 0)
                    {
                        EndBlock();
                        break;
                    }
                    if (output.IsEmpty)
                        return InflateStatus.NeedOutput;
                    if (_bits.Count == 0 && !input.IsEmpty)
                    {
                        int length = Math.Min(_remaining, Math.Min(input.Length, output.Length));
                        ReadOnlySpan<byte> data = input[..length];
                        data.CopyTo(output);
                        _remaining -= length;
                        input = input[length..];
                        output = output[length..];
                        break;
                    }
                    if (!_bits.TryRead(ref input, 8, out uint storedByte))
                        return InflateStatus.NeedInput;
                    Emit((byte)storedByte, ref output);
                    _remaining--;
                    break;

                case State.DynamicHeader:
                    if (!_bits.TryRead(ref input, 14, out uint counts))
                        return InflateStatus.NeedInput;
                    StartDynamicBlock(counts);
                    break;

                case State.CodeLengthAlphabet:
                    while (_lengthIndex < _codeLengthCount)
                    {
                        if (!_bits.TryRead(ref input, 3, out uint codeLength))
                            return InflateStatus.NeedInput;
                        _trees.CodeLengths[DeflateCodes.BitLengthOrder[_lengthIndex++]] = (byte)codeLength;
                    }
                    _trees.CodeLengthTree.Build(_trees.CodeLengths, codeLengths: true);
                    _lengthIndex = 0;
                    _state = State.CodeLengths;
                    break;

                case State.CodeLengths:
                    if (_lengthIndex == _literalCount + _distanceCount)
                    {
                        BuildDynamicTrees();
                        break;
                    }
                    if (!_trees.CodeLengthTree.TryRead(ref _bits, ref input, out _symbol))
                        return InflateStatus.NeedInput;
                    if (_symbol <= 15)
                        _trees.Lengths[_lengthIndex++] = (byte)_symbol;
                    else
                        _state = State.RepeatLength;
                    break;

                case State.RepeatLength:
                    if (!ReadRepeatedLength(ref input))
                        return InflateStatus.NeedInput;
                    _state = State.CodeLengths;
                    break;

                case State.Symbol:
                    if (input.Length >= sizeof(ulong) && output.Length >= 2)
                    {
                        if (ReferenceEquals(_literals, FixedLiterals))
                            DecodeFast<FixedCodes>(ref input, ref output, memberOutput);
                        else if (_literals.HasMostlyEightBitCodes)
                            DecodeFast<WithEightBitPairs>(ref input, ref output, memberOutput);
                        else
                            DecodeFast<WithoutEightBitPairs>(ref input, ref output, memberOutput);
                    }

                    bool canResumeFast = input.Length >= sizeof(ulong) && output.Length > 2;
                    InflateTree literals = _literals;
                    int symbol;
                    while (true)
                    {
                        if (!literals.TryRead(ref _bits, ref input, out symbol))
                            return InflateStatus.NeedInput;
                        if (symbol >= EndOfBlock)
                            break;
                        if (output.IsEmpty)
                        {
                            _symbol = symbol;
                            _state = State.Literal;
                            return InflateStatus.NeedOutput;
                        }
                        Emit((byte)symbol, ref output);
                        // A long literal code may be followed by more root-table symbols.
                        if (canResumeFast)
                            goto case State.Symbol;
                    }
                    if (symbol == EndOfBlock)
                    {
                        EndBlock();
                        break;
                    }
                    if (symbol >= MaxLiterals)
                        throw new InvalidDataException("Reserved DEFLATE length symbol.");
                    // Keep a resume state while jumping directly through the match.
                    _symbol = symbol;
                    _state = State.Length;
                    goto case State.Length;

                case State.Literal:
                    if (output.IsEmpty)
                        return InflateStatus.NeedOutput;
                    Emit((byte)_symbol, ref output);
                    _state = State.Symbol;
                    break;

                case State.Length:
                    int lengthCode = _symbol - DeflateCodes.LengthStart;
                    if (!_bits.TryRead(ref input, DeflateCodes.LengthExtra[lengthCode], out uint lengthExtra))
                        return InflateStatus.NeedInput;
                    _remaining = DeflateCodes.LengthBase[lengthCode] + (int)lengthExtra;
                    _state = State.Distance;
                    goto case State.Distance;

                case State.Distance:
                    if (!_distances.TryRead(ref _bits, ref input, out _symbol))
                        return InflateStatus.NeedInput;
                    if (_symbol >= MaxDistances)
                        throw new InvalidDataException("Reserved DEFLATE distance symbol.");
                    _state = State.DistanceExtra;
                    goto case State.DistanceExtra;

                case State.DistanceExtra:
                    int distanceBits = DeflateCodes.DistanceExtra(_symbol);
                    if (!_bits.TryRead(ref input, distanceBits, out uint distanceExtra))
                        return InflateStatus.NeedInput;
                    _distance = DeflateCodes.DistanceBase(_symbol) + (int)distanceExtra;
                    if (_distance - (memberOutput.Length - output.Length) > _historyCount)
                        throw new InvalidDataException("DEFLATE distance exceeds the available history.");
                    _state = State.Match;
                    goto case State.Match;

                case State.Match:
                    EmitMatch(ref output, memberOutput[..(memberOutput.Length - output.Length)]);
                    if (_remaining > 0)
                        return InflateStatus.NeedOutput;
                    _state = State.Symbol;
                    goto case State.Symbol;

                case State.Trailer:
                    CommitOutput(output, ref memberOutput, keepHistory: false);
                    if (!ReadField(ref input, 8, checksum: false))
                        return InflateStatus.NeedInput;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(_field) != ~_crc)
                        throw new InvalidDataException("Invalid gzip data checksum.");
                    if (BinaryPrimitives.ReadUInt32LittleEndian(_field.AsSpan(4)) != _size)
                        throw new InvalidDataException("Invalid gzip uncompressed size.");
                    _state = State.NextMember;
                    break;

                case State.NextMember:
                    if (!_bits.Fill(ref input, 8))
                        return InflateStatus.NeedInput;
                    if (_bits.Peek(8) != 0x1f)
                    {
                        _state = State.Done;
                        break;
                    }
                    if (_bits.Fill(ref input, 16) && _bits.Peek(16) != 0x8b1f)
                    {
                        _state = State.Done;
                        break;
                    }
                    _headerCrc = _crc = Crc32.Initial;
                    _fieldCount = _writePosition = _historyCount = 0;
                    _size = 0;
                    _state = State.Header;
                    break;

                default:
                    Debug.Assert(_state == State.Done);
                    return InflateStatus.Done;
            }
        }
    }

    private bool ReadField(ref ReadOnlySpan<byte> input, int size, bool checksum)
    {
        while (_fieldCount < size)
        {
            if (!_bits.TryRead(ref input, 8, out uint next))
                return false;
            byte value = (byte)next;
            _field[_fieldCount++] = value;
            if (checksum)
                _headerCrc = Crc32.Update(_headerCrc, value);
        }
        _fieldCount = 0;
        return true;
    }

    private void AdvanceHeader()
    {
        if ((_headerFlags & HeaderExtra) != 0)
        {
            _headerFlags &= ~HeaderExtra;
            _state = State.ExtraLength;
        }
        else if ((_headerFlags & HeaderName) != 0)
        {
            _headerFlags &= ~HeaderName;
            _state = State.HeaderString;
        }
        else if ((_headerFlags & HeaderComment) != 0)
        {
            _headerFlags &= ~HeaderComment;
            _state = State.HeaderString;
        }
        else if ((_headerFlags & HeaderCrc) != 0)
        {
            _headerFlags &= ~HeaderCrc;
            _state = State.HeaderChecksum;
        }
        else
        {
            _state = State.BlockHeader;
        }
    }

    private void StartBlock(uint header)
    {
        _finalBlock = (header & 1) != 0;
        switch (header >> 1)
        {
            case 0:
                _bits.Align();
                _state = State.StoredLength;
                break;
            case 1:
                _literals = FixedLiterals;
                _distances = FixedDistances;
                _state = State.Symbol;
                break;
            case 2:
                _state = State.DynamicHeader;
                break;
            default:
                throw new InvalidDataException("Reserved DEFLATE block type.");
        }
    }

    private void StartDynamicBlock(uint counts)
    {
        _literalCount = (int)(counts & 31) + 257;
        _distanceCount = (int)((counts >> 5) & 31) + 1;
        _codeLengthCount = (int)(counts >> 10) + 4;
        if (_literalCount > MaxLiterals || _distanceCount > MaxDistances)
            throw new InvalidDataException("Invalid DEFLATE alphabet size.");
        _lengthIndex = 0;
        Array.Clear(_trees.CodeLengths);
        _state = State.CodeLengthAlphabet;
    }

    private bool ReadRepeatedLength(ref ReadOnlySpan<byte> input)
    {
        int extraBits = _symbol switch
        {
            16 => 2,
            17 => 3,
            _ => 7,
        };
        if (!_bits.TryRead(ref input, extraBits, out uint extra))
            return false;

        int repeat = (int)extra + (_symbol == 18 ? 11 : 3);
        if ((_symbol == 16 && _lengthIndex == 0) || repeat > _literalCount + _distanceCount - _lengthIndex)
            throw new InvalidDataException("Invalid DEFLATE code length repeat.");
        byte value = _symbol == 16 ? _trees.Lengths[_lengthIndex - 1] : (byte)0;
        _trees.Lengths.AsSpan(_lengthIndex, repeat).Fill(value);
        _lengthIndex += repeat;
        return true;
    }

    private void BuildDynamicTrees()
    {
        if (_trees.Lengths[EndOfBlock] == 0)
            throw new InvalidDataException("Missing DEFLATE end-of-block code.");
        _trees.Literals.Build(_trees.Lengths.AsSpan(0, _literalCount));
        _trees.Distances.Build(_trees.Lengths.AsSpan(_literalCount, _distanceCount), allowEmpty: true);
        _literals = _trees.Literals;
        _distances = _trees.Distances;
        _state = State.Symbol;
    }

    private void EndBlock()
    {
        if (_finalBlock)
            _bits.Align();
        _state = _finalBlock ? State.Trailer : State.BlockHeader;
    }

    // Bit 8 is set for the end marker and length symbols. A zero entry has no root code.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLiteral<TCodes>(ushort entry) where TCodes : struct
        => (entry & EndOfBlock) == 0 && (typeof(TCodes) == typeof(FixedCodes) || entry != 0);

    private readonly struct FixedCodes { }
    private readonly struct WithEightBitPairs { }
    private readonly struct WithoutEightBitPairs { }

    // Decode complete root-table tokens, then let the resumable parser handle the next token.
    private void DecodeFast<TCodes>(ref ReadOnlySpan<byte> input, ref Span<byte> output,
        scoped ReadOnlySpan<byte> memberOutput) where TCodes : struct
    {
        const int rootBits = InflateTree.RootBits;
        const int rootMask = (1 << rootBits) - 1;
        const int fixedDistanceBits = 5;
        int maxMatchBits = typeof(TCodes) == typeof(FixedCodes)
            ? 8 + fixedDistanceBits + 5 + 13
            : _literals.LookupBits + 5 + _distances.LookupBits + 13;
        ReadOnlySpan<ushort> literals = _literals.Root;
        ReadOnlySpan<ushort> distances = _distances.Root;
        ReadOnlySpan<byte> lengthExtras = DeflateCodes.LengthExtra;
        ReadOnlySpan<ushort> lengths = DeflateCodes.LengthBase;
        ulong bits = _bits.BufferedBits;
        int available = _bits.Count;
        int read = 0;
        int written = 0;
        int previousLength = memberOutput.Length - output.Length;
        while (written < output.Length - 1)
        {
            if (available < 2 * rootBits)
            {
                if (read > input.Length - sizeof(ulong))
                    break;
                RefillFast(input, ref bits, ref available, ref read);
            }

            ushort entry = literals[(int)bits & rootMask];
            int used = entry >> rootBits;
            if (IsLiteral<TCodes>(entry))
            {
                const int eightBitLiteral = 8 << rootBits;
                if (typeof(TCodes) == typeof(WithEightBitPairs) && (entry & 0xff00) == eightBitLiteral)
                {
                    ushort next = literals[(int)(bits >> 8) & rootMask];
                    if ((next & 0xff00) == eightBitLiteral)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(output[written..], (ushort)((byte)entry | ((byte)next << 8)));
                        written += 2;
                        bits >>= 16;
                        available -= 16;
                        continue;
                    }
                }

                output[written++] = (byte)entry;
                if (typeof(TCodes) == typeof(FixedCodes))
                {
                    // Fixed literals 144–255 have canonical prefixes 25 through 31.
                    // The uint shift selects a bit using the reversed five-bit prefix.
                    const uint nineBitPrefixes = 0x88888880u;
                    bits >>= 8 + (int)((nineBitPrefixes >> (int)bits) & 1);
                }
                else
                {
                    bits >>= used;
                }
                available -= used;
                entry = literals[(int)bits & rootMask];
                if (IsLiteral<TCodes>(entry))
                {
                    output[written++] = (byte)entry;
                    used = entry >> rootBits;
                    bits >>= used;
                    available -= used;
                }
                continue;
            }
            else
            {
                int symbol = entry & rootMask;
                if ((typeof(TCodes) != typeof(FixedCodes) && entry == 0) || symbol == EndOfBlock || symbol >= MaxLiterals)
                    break;
                if (available < maxMatchBits)
                {
                    if (read > input.Length - sizeof(ulong))
                        break;
                    RefillFast(input, ref bits, ref available, ref read);
                }
                ulong extra = bits >> used;
                int code = symbol - DeflateCodes.LengthStart;
                // Length symbol 285 encodes 258 without extra bits.
                bool maximumLength = symbol == MaxLiterals - 1;
                int lengthBits = maximumLength ? 0 : lengthExtras[code];
                int length = maximumLength ? DeflateCodes.MaximumLength
                    : lengths[code] + (int)(extra & ((1UL << lengthBits) - 1));
                extra >>= lengthBits;
                used += lengthBits;
                if (length > output.Length - written)
                    break;

                ushort distanceEntry = distances[(int)extra & (typeof(TCodes) == typeof(FixedCodes) ? (1 << fixedDistanceBits) - 1 : rootMask)];
                int distanceCode = distanceEntry & rootMask;
                if ((typeof(TCodes) != typeof(FixedCodes) && distanceEntry == 0) || distanceCode >= MaxDistances)
                    break;

                int distanceLength = typeof(TCodes) == typeof(FixedCodes) ? fixedDistanceBits : distanceEntry >> rootBits;
                extra >>= distanceLength;
                int distanceBits = DeflateCodes.DistanceExtra(distanceCode);
                int distance = DeflateCodes.DistanceBase(distanceCode) + (int)(extra & ((1UL << distanceBits) - 1));
                int produced = previousLength + written;
                if (distance - produced > _historyCount)
                    break;

                Span<byte> match = output.Slice(written, length);
                CopyMatch(match, memberOutput[..produced], distance);
                written += length;
                used += distanceLength + distanceBits;
            }
            bits >>= used;
            available -= used;
        }

        // Return only the bits from input bytes this loop consumed.
        if (available < 64)
            bits &= (1UL << available) - 1;
        _bits.SetBuffer(bits, available);
        input = input[read..];
        output = output[written..];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RefillFast(ReadOnlySpan<byte> input, ref ulong bits, ref int available, ref int read)
    {
        // Advance by whole bytes. The next load will repeat any partial byte in the accumulator.
        bits |= BinaryPrimitives.ReadUInt64LittleEndian(input[read..]) << available;
        read += (63 - available) >> 3;
        available = 56 + (available & 7);
    }

    private static void Emit(byte value, ref Span<byte> output)
    {
        output[0] = value;
        output = output[1..];
    }

    private void EmitMatch(ref Span<byte> output, scoped ReadOnlySpan<byte> previousOutput)
    {
        int count = Math.Min(_remaining, output.Length);
        if (count == 0)
            return;

        CopyMatch(output[..count], previousOutput, _distance);
        _remaining -= count;
        output = output[count..];
    }

    private void CopyMatch(Span<byte> bytes, scoped ReadOnlySpan<byte> previousOutput, int distance)
    {
        int count = bytes.Length;
        if (distance == 1)
        {
            byte value = previousOutput.IsEmpty ? _history[(_writePosition - 1) & WindowMask] : previousOutput[^1];
            bytes.Fill(value);
        }
        else
        {
            int prefix = Math.Min(count, distance);
            int source = previousOutput.Length - distance;
            if (source >= 0)
            {
                if (prefix == DeflateCodes.MaximumLength)
                {
                    // Copy the common full-length match with a constant-size copy and a two-byte tail.
                    const int headLength = DeflateCodes.MaximumLength - sizeof(ushort);
                    previousOutput.Slice(source, headLength).CopyTo(bytes);
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes[headLength..],
                        BinaryPrimitives.ReadUInt16LittleEndian(previousOutput[(source + headLength)..]));
                    return;
                }
                previousOutput.Slice(source, prefix).CopyTo(bytes);
            }
            else
            {
                int historyBytes = Math.Min(-source, prefix);
                int historyStart = (_writePosition + source) & WindowMask;
                int contiguous = Math.Min(WindowSize - historyStart, historyBytes);
                _history.AsSpan(historyStart, contiguous).CopyTo(bytes);
                if (contiguous < historyBytes)
                    _history.AsSpan(0, historyBytes - contiguous).CopyTo(bytes[contiguous..]);
                previousOutput[..(prefix - historyBytes)].CopyTo(bytes[historyBytes..]);
            }

            // A match longer than its distance repeats the initial prefix.
            for (int written = prefix; written < count;)
            {
                int length = Math.Min(written, count - written);
                bytes[..length].CopyTo(bytes[written..]);
                written += length;
            }
        }
    }

    internal bool CanReadStored(int count)
        => _state == State.StoredData && _bits.Count == 0 && _remaining >= count;

    internal void CommitStored(ReadOnlySpan<byte> bytes)
    {
        _remaining -= bytes.Length;
        CommitBytes(bytes);
        if (_remaining == 0)
            EndBlock();
    }

    private void CommitOutput(Span<byte> output, ref Span<byte> memberOutput, bool keepHistory = true)
    {
        int size = memberOutput.Length - output.Length;
        if (size == 0)
            return;

        ReadOnlySpan<byte> bytes = memberOutput[..size];
        memberOutput = output;
        if (keepHistory)
        {
            CommitBytes(bytes);
        }
        else
        {
            // The final block has ended and these bytes cannot be used by a match.
            _size = unchecked(_size + (uint)size);
            _crc = Crc32.Update(_crc, bytes);
        }
    }

    private void CommitBytes(ReadOnlySpan<byte> bytes)
    {
        int size = bytes.Length;
        _size = unchecked(_size + (uint)size);
        _historyCount = Math.Min(_historyCount + Math.Min(size, WindowSize), WindowSize);

        int end = (_writePosition + (size & WindowMask)) & WindowMask;
        int start = _writePosition;
        if (_state == State.StoredData && size > WindowSize && end == 0 &&
            (size & 127) == 0 && Crc32.CanFoldAndCopy(size))
        {
            _crc = Crc32.FoldAndCopySuffix(_crc, bytes, _history.AsSpan(0, WindowSize));
            _writePosition = end;
            return;
        }

        // Fold each contiguous part in order so the checksum follows the output across a wrap.
        if (size <= WindowSize && (size >= 512 || _state == State.StoredData) && Crc32.CanFoldAndCopy(size))
        {
            int contiguous = Math.Min(size, WindowSize - start);
            FoldAndCopyHistory(bytes[..contiguous], _history.AsSpan(start));
            if (contiguous < size)
                FoldAndCopyHistory(bytes[contiguous..], _history);
            _writePosition = end;
            return;
        }

        _crc = Crc32.Update(_crc, bytes);
        if (size > WindowSize)
        {
            bytes = bytes[^WindowSize..];
            start = end;
        }

        int first = Math.Min(bytes.Length, WindowSize - start);
        bytes[..first].CopyTo(_history.AsSpan(start));
        bytes[first..].CopyTo(_history);
        _writePosition = end;
    }

    private void FoldAndCopyHistory(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (Crc32.CanFoldAndCopy(data.Length))
            _crc = Crc32.FoldAndCopy(_crc, data, destination);
        else
        {
            _crc = Crc32.Update(_crc, data);
            data.CopyTo(destination);
        }
    }

    private static InflateTree CreateFixedLiterals()
    {
        Span<byte> lengths = stackalloc byte[288];
        DeflateCodes.FillFixedLiteralLengths(lengths);
        var tree = new InflateTree(lengths.Length);
        tree.Build(lengths);
        return tree;
    }

    private static InflateTree CreateFixedDistances()
    {
        Span<byte> lengths = stackalloc byte[32];
        lengths.Fill(5);
        var tree = new InflateTree(lengths.Length);
        tree.Build(lengths);
        return tree;
    }
}
