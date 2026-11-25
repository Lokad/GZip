using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

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
                    CommitOutput(output, ref memberOutput);
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

        Span<byte> bytes = output[..count];
        if (_distance == 1)
        {
            byte value = previousOutput.IsEmpty ? _history[(_writePosition - 1) & WindowMask] : previousOutput[^1];
            bytes.Fill(value);
        }
        else
        {
            int prefix = Math.Min(count, _distance);
            int source = previousOutput.Length - _distance;
            if (source >= 0)
            {
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

        _remaining -= count;
        output = output[count..];
    }

    private void CommitOutput(Span<byte> output, ref Span<byte> memberOutput)
    {
        int size = memberOutput.Length - output.Length;
        if (size == 0)
            return;

        ReadOnlySpan<byte> bytes = memberOutput[..size];
        memberOutput = output;
        _crc = Crc32.Update(_crc, bytes);
        _size = unchecked(_size + (uint)size);
        _historyCount = Math.Min(_historyCount + Math.Min(size, WindowSize), WindowSize);

        int end = (_writePosition + (size & WindowMask)) & WindowMask;
        int start = _writePosition;
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
