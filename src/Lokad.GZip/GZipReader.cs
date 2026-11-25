using System.Buffers;

namespace Lokad.GZip;

internal sealed class GZipReader : IDisposable
{
    private const int BufferSize = 8192;
    private static readonly bool UseStrictValidation =
        AppContext.TryGetSwitch("System.IO.Compression.UseStrictValidation", out bool strict) && strict;

    private readonly GZipInflater _inflater = new();
    private byte[]? _input;
    private int _offset;
    private int _length;
    private bool _receivedInput;

    private byte[] Input => _input ??= ArrayPool<byte>.Shared.Rent(BufferSize);

    public void Dispose()
    {
        _inflater.Dispose();
        if (_input is not null)
            ArrayPool<byte>.Shared.Return(_input);
    }

    internal int Read(Stream source, Span<byte> destination)
    {
        while (true)
        {
            if (TryDecode(destination, out int written))
                return written;
            if (!Refill(source.Read(Input, 0, BufferSize), destination.IsEmpty) || destination.IsEmpty)
                return 0;
        }
    }

    internal async ValueTask<int> ReadAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryDecode(destination.Span, out int written))
                return written;
            int received = await source.ReadAsync(Input.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
            if (!Refill(received, destination.IsEmpty) || destination.IsEmpty)
                return 0;
        }
    }

    private bool TryDecode(Span<byte> destination, out int written)
    {
        InflateStatus status = _inflater.Decode(_input.AsSpan(_offset, _length - _offset), destination, out int consumed, out written);
        _offset += consumed;
        return written > 0 || status != InflateStatus.NeedInput;
    }

    private bool Refill(int received, bool emptyDestination)
    {
        if (received == 0)
        {
            if (UseStrictValidation && !emptyDestination && _receivedInput && !_inflater.FinishedMember)
                throw new InvalidDataException("The gzip member ended before its trailer.");
            return false;
        }

        if ((uint)received > BufferSize)
            throw new InvalidDataException("The underlying stream returned an invalid byte count.");
        _receivedInput = true;
        _offset = 0;
        _length = received;
        return true;
    }
}
