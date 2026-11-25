using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace Lokad.GZip;

// Managed adaptation of the stored-block policy in zlib-ng 2.2.5.
internal sealed class StoredEncoder(int windowBits) : IDeflateEncoder
{
    // The reference sets memory level 7 at compression level zero.
    private const int PendingSize = 32768;
    private const int BlockHeaderSize = 5;
    private const int MaximumPayload = PendingSize - BlockHeaderSize;
    private readonly int _windowSize = 1 << windowBits;
    private readonly byte[] _window = ArrayPool<byte>.Shared.Rent(2 << windowBits);
    private readonly byte[] _pending = ArrayPool<byte>.Shared.Rent(PendingSize);
    private int _position;
    private int _blockStart;
    private int _pendingStart;
    private int _pendingEnd;
    private uint _crc = Crc32.Initial;
    private uint _size;
    private bool _headerWritten;
    private bool _syncMarkerWritten;
    private bool _finalBlockWritten;
    private bool _trailerWritten;

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_pending);
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
        if (!input.IsEmpty)
        {
            Debug.Assert(operation == DeflateOperation.Write);
            _syncMarkerWritten = false;
        }

        CopyPending(ref output);
        if (output.IsEmpty)
            return false;

        if (!_headerWritten)
        {
            GZipFormat.WriteHeader(_pending, extraFlags: 4);
            _headerWritten = true;
            _pendingEnd = GZipFormat.HeaderSize;
            CopyPending(ref output);
            if (output.IsEmpty)
                return false;
        }

        if (_finalBlockWritten)
            return Finish(ref output);
        if (operation == DeflateOperation.Flush && _syncMarkerWritten)
            return true;

        int minimum = Math.Min(MaximumPayload, _windowSize);
        WriteDirectBlocks(ref input, ref output, operation, minimum);
        if (_finalBlockWritten)
            return Finish(ref output);

        if (operation == DeflateOperation.Flush && input.IsEmpty && _position == _blockStart)
        {
            WriteBlockHeader(_pending, 0, final: false);
            _pendingEnd = BlockHeaderSize;
            CopyPending(ref output);
            // Stock deflate continues the flush after a call that filled its output buffer.
            _syncMarkerWritten = !output.IsEmpty;
            return _syncMarkerWritten;
        }

        FillWindow(ref input);
        int remaining = _position - _blockStart;
        if (remaining >= minimum ||
            (operation != DeflateOperation.Write && input.IsEmpty && remaining <= MaximumPayload))
        {
            int length = Math.Min(remaining, MaximumPayload);
            _finalBlockWritten = operation == DeflateOperation.Finish && input.IsEmpty && length == remaining;
            WriteBlockHeader(_pending, length, _finalBlockWritten);
            _window.AsSpan(_blockStart, length).CopyTo(_pending.AsSpan(BlockHeaderSize));
            _pendingEnd = BlockHeaderSize + length;
            _blockStart += length;
            CopyPending(ref output);
            return false;
        }

        return operation == DeflateOperation.Write && input.IsEmpty;
    }

    private void WriteDirectBlocks(ref ReadOnlySpan<byte> input, ref Span<byte> output, DeflateOperation operation, int minimum)
    {
        int inputLength = input.Length;
        while (output.Length >= BlockHeaderSize)
        {
            int held = _position - _blockStart;
            long available = (long)held + input.Length;
            int length = (int)Math.Min(available, Math.Min(ushort.MaxValue, output.Length - BlockHeaderSize));
            if (length < minimum && (operation == DeflateOperation.Write || length != available ||
                (length == 0 && operation != DeflateOperation.Finish)))
                break;

            _finalBlockWritten = operation == DeflateOperation.Finish && length == available;
            WriteBlockHeader(output, length, _finalBlockWritten);
            output = output[BlockHeaderSize..];

            int buffered = Math.Min(held, length);
            _window.AsSpan(_blockStart, buffered).CopyTo(output);
            _blockStart += buffered;
            int direct = length - buffered;
            input[..direct].CopyTo(output[buffered..]);
            Accept(input[..direct]);
            input = input[direct..];
            output = output[length..];
            if (_finalBlockWritten)
                break;
        }

        int consumed = inputLength - input.Length;
        if (consumed > 0)
        {
            Debug.Assert(_blockStart == _position);
            if (consumed >= _windowSize)
                _position = _windowSize;
            else
            {
                if (2 * _windowSize - _position <= consumed)
                    _position -= _windowSize;
                _position += consumed;
            }
            _blockStart = _position;
        }
    }

    private void FillWindow(ref ReadOnlySpan<byte> input)
    {
        int available = 2 * _windowSize - _position;
        if (input.Length > available && _blockStart >= _windowSize)
        {
            int remaining = _position - _blockStart;
            _window.AsSpan(_blockStart, remaining).CopyTo(_window.AsSpan(_blockStart - _windowSize));
            _position -= _windowSize;
            _blockStart -= _windowSize;
            available += _windowSize;
        }

        int count = Math.Min(available, input.Length);
        input[..count].CopyTo(_window.AsSpan(_position));
        Accept(input[..count]);
        input = input[count..];
        _position += count;
    }

    private bool Finish(ref Span<byte> output)
    {
        if (!_trailerWritten)
        {
            GZipFormat.WriteTrailer(_pending, _crc, _size);
            _trailerWritten = true;
            _pendingEnd = GZipFormat.TrailerSize;
            CopyPending(ref output);
        }
        return _pendingEnd == 0;
    }

    private void CopyPending(ref Span<byte> output)
    {
        int count = Math.Min(_pendingEnd - _pendingStart, output.Length);
        _pending.AsSpan(_pendingStart, count).CopyTo(output);
        output = output[count..];
        _pendingStart += count;
        if (_pendingStart == _pendingEnd)
            _pendingStart = _pendingEnd = 0;
    }

    private void Accept(ReadOnlySpan<byte> input)
    {
        _crc = Crc32.Update(_crc, input);
        _size = unchecked(_size + (uint)input.Length);
    }

    private static void WriteBlockHeader(Span<byte> output, int length, bool final)
    {
        output[0] = final ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(output[1..], (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(output[3..], (ushort)~length);
    }
}
