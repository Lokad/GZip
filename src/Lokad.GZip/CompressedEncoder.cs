using System.Buffers;
using System.IO.Compression;

namespace Lokad.GZip;

internal sealed partial class CompressedEncoder(int level, ZLibCompressionStrategy strategy, int windowBits) : IDeflateEncoder
{
    private const int MinimumLookahead = DeflateCodes.MaximumLength + DeflateCodes.MinimumLength + 1;
    private const int WindowPadding = 8;
    private readonly int _level = level;
    private readonly ZLibCompressionStrategy _strategy = strategy;
    private readonly int _windowSize = 1 << windowBits;
    private readonly byte[] _window = RentWindow((2 << windowBits) + WindowPadding);
    private readonly DeflateBits _bits = new();
    private readonly DeflateBlockWriter _blocks = DeflateBlockWriter.Rent();
    private readonly DeflateDictionary? _dictionary =
        strategy is ZLibCompressionStrategy.HuffmanOnly or ZLibCompressionStrategy.RunLengthEncoding ? null : new(windowBits, level);
    private int _position;
    private int _lookahead;
    private int _blockStart;
    private int _initialized;
    private uint _crc = Crc32.Initial;
    private uint _size;
    private DeflateOperation? _lastOperation;
    private bool _headerWritten;
    private bool _finalBlockWritten;
    private bool _trailerWritten;

    private static byte[] RentWindow(int length)
    {
        byte[] window = ArrayPool<byte>.Shared.Rent(length);
        // The match finder can read the guard bytes beyond the logical window.
        window.AsSpan(length - WindowPadding, WindowPadding).Clear();
        return window;
    }

    public void Dispose()
    {
        _dictionary?.Dispose();
        _blocks.Dispose();
        _bits.Dispose();
        ArrayPool<byte>.Shared.Return(_window);
    }

    public DeflateProgress Encode(ReadOnlySpan<byte> input, Span<byte> output, DeflateOperation operation)
    {
        int inputLength = input.Length;
        int outputLength = output.Length;
        bool complete = Process(ref input, ref output, operation);
        return new(inputLength - input.Length, outputLength - output.Length, complete);
    }

    private bool Process(ref ReadOnlySpan<byte> input, ref Span<byte> output, DeflateOperation operation)
    {
        DeflateOperation? previous = _lastOperation;
        _lastOperation = operation;
        if (_bits.Pending != 0)
        {
            if (Drain(ref output))
                return false;
        }
        else if (operation == DeflateOperation.Flush && previous == DeflateOperation.Flush)
        {
            return true;
        }

        if (!_headerWritten)
        {
            int extraFlags = _level == 9 ? 2 : _strategy >= ZLibCompressionStrategy.HuffmanOnly || _level < 2 ? 4 : 0;
            GZipFormat.WriteHeader(_bits.ReserveAligned(GZipFormat.HeaderSize), (byte)extraFlags);
            _headerWritten = true;
            if (Drain(ref output))
                return false;
        }

        if (_finalBlockWritten)
            return Finish(ref output);

        if (_level == 1 && _dictionary is DeflateDictionary quick)
        {
            if (!EncodeQuick(quick, ref input, ref output, operation))
                return false;
            if (_finalBlockWritten)
                return Finish(ref output);
        }
        else if (!EncodeBlock(ref input, ref output, operation))
        {
            return false;
        }

        if (operation == DeflateOperation.Write)
            return true;
        if (operation == DeflateOperation.Finish)
        {
            _finalBlockWritten = true;
            return !WriteBlock(ref output, final: true) && Finish(ref output);
        }
        if (_blocks.HasSymbols && WriteBlock(ref output, final: false))
            return false;

        DeflateBlockWriter.WriteStored(_bits, ReadOnlySpan<byte>.Empty, final: false);
        return !Drain(ref output);
    }

    private bool EncodeBlock(ref ReadOnlySpan<byte> input, ref Span<byte> output, DeflateOperation operation)
    {
        if (_strategy == ZLibCompressionStrategy.HuffmanOnly)
            return EncodeLiterals(ref input, ref output);
        if (_strategy == ZLibCompressionStrategy.RunLengthEncoding)
            return EncodeRuns(ref input, ref output, operation);
        if (_dictionary is not DeflateDictionary dictionary)
            throw new InvalidOperationException("The match dictionary has not been initialized.");

        if (_level == 2)
            return EncodeFast(dictionary, ref input, ref output, operation);
        if (_level <= 6)
            return EncodeMedium(dictionary, ref input, ref output, operation);
        return EncodeSlow(dictionary, ref input, ref output, operation);
    }

    private bool EncodeLiterals(ref ReadOnlySpan<byte> input, ref Span<byte> output)
    {
        while (true)
        {
            if (_lookahead == 0)
            {
                FillWindow(ref input);
                if (_lookahead == 0)
                    return true;
            }

            byte value = _window[_position];
            Advance(1);
            if (_blocks.AddLiteral(value) && WriteBlock(ref output, final: false))
                return false;
        }
    }

    private bool EncodeRuns(ref ReadOnlySpan<byte> input, ref Span<byte> output, DeflateOperation operation)
    {
        while (true)
        {
            if (_lookahead <= DeflateCodes.MaximumLength)
            {
                FillWindow(ref input);
                if (_lookahead == 0 || (_lookahead <= DeflateCodes.MaximumLength && operation == DeflateOperation.Write))
                    return true;
            }

            int run = 0;
            if (_position > 0 && _lookahead >= DeflateCodes.MinimumLength)
            {
                byte previous = _window[_position - 1];
                int limit = Math.Min(DeflateCodes.MaximumLength, _lookahead);
                while (run < limit && _window[_position + run] == previous)
                    run++;
            }

            bool full;
            if (run >= DeflateCodes.MinimumLength)
            {
                full = _blocks.AddMatch(run, 1);
                Advance(run);
            }
            else
            {
                full = _blocks.AddLiteral(_window[_position]);
                Advance(1);
            }
            if (full && WriteBlock(ref output, final: false))
                return false;
        }
    }

    private void Advance(int length)
    {
        _position += length;
        _lookahead -= length;
    }

    private void FillWindow(ref ReadOnlySpan<byte> input)
    {
        int capacity = 2 * _windowSize;
        if (_position >= capacity - MinimumLookahead)
        {
            // Sliding copies the whole upper half, including unused space.
            if (_initialized < capacity)
            {
                _window.AsSpan(_initialized, capacity - _initialized).Clear();
                _initialized = capacity;
            }
            _window.AsSpan(_windowSize, _windowSize).CopyTo(_window);
            _position -= _windowSize;
            _blockStart -= _windowSize;
            if (_matchStart >= _windowSize)
                _matchStart -= _windowSize;
            else
                _matchStart = _previousLength = 0;
            _insert = Math.Min(_insert, _position);
            _dictionary?.Slide();
        }

        int available = capacity - _position - _lookahead;
        int count = Math.Min(available, input.Length);
        if (count > 0)
        {
            ReadOnlySpan<byte> accepted = input[..count];
            Span<byte> destination = _window.AsSpan(_position + _lookahead);
            if (count == _windowSize && Crc32.CanFoldAndCopy(count))
                _crc = Crc32.FoldAndCopy(_crc, accepted, destination);
            else
            {
                accepted.CopyTo(destination);
                _crc = Crc32.Update(_crc, accepted);
            }
            _lookahead += count;
            _size = unchecked(_size + (uint)count);
            input = input[count..];
        }

        int end = _position + _lookahead;
        if (_initialized < capacity)
        {
            int paddingEnd = Math.Min(capacity, end + DeflateCodes.MaximumLength);
            int paddingStart = Math.Max(end, _initialized);
            if (paddingEnd > paddingStart)
                _window.AsSpan(paddingStart, paddingEnd - paddingStart).Clear();
            _initialized = Math.Max(_initialized, paddingEnd);
        }

        if (count > 0 && _dictionary is DeflateDictionary dictionary && _lookahead + _insert >= DeflateCodes.MinimumLength)
        {
            int start = _position - _insert;
            int length = _insert - (_lookahead == 1 ? 1 : 0);
            dictionary.Reinsert(_window, start, length);
            _insert -= length;
        }
    }

    private bool WriteBlock(ref Span<byte> output, bool final)
    {
        ReadOnlySpan<byte> original = _blockStart >= 0 ? _window.AsSpan(_blockStart, _position - _blockStart) : default;
        _blocks.Write(_bits, original, final, forceFixed: _strategy == ZLibCompressionStrategy.Fixed);
        _blockStart = _position;
        return Drain(ref output);
    }

    private bool Finish(ref Span<byte> output)
    {
        if (!_trailerWritten)
        {
            GZipFormat.WriteTrailer(_bits.ReserveAligned(GZipFormat.TrailerSize), _crc, _size);
            _trailerWritten = true;
            Drain(ref output);
        }
        return _bits.Pending == 0;
    }

    private bool Drain(ref Span<byte> output)
    {
        _bits.Drain(ref output);
        if (!output.IsEmpty)
            return false;
        _lastOperation = null;
        return true;
    }
}
