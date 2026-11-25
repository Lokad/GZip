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
#if NET11_0_OR_GREATER
    private bool _rewound;
#endif

    private byte[] Input => _input ??= ArrayPool<byte>.Shared.Rent(BufferSize);

    internal bool ShouldDrainTrailingInput(Stream source)
    {
#if NET11_0_OR_GREATER
        return _inflater.HasTrailingInput && !source.CanSeek;
#else
        return _inflater.HasTrailingInput;
#endif
    }

    internal void ValidateCompletedMember()
    {
        if (UseStrictValidation && !_inflater.FinishedMember)
            throw new InvalidDataException("The gzip member ended before its trailer.");
    }

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
            if (TryDecode(source, destination, out int written))
                return written;
            if (!Refill(source.Read(Input, 0, BufferSize), destination.IsEmpty) || destination.IsEmpty)
                return 0;
        }
    }

    internal async ValueTask<int> ReadAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryDecode(source, destination.Span, out int written))
                return written;
            int received = await source.ReadAsync(Input.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
            if (!Refill(received, destination.IsEmpty) || destination.IsEmpty)
                return 0;
        }
    }

    private bool TryDecode(Stream source, Span<byte> destination, out int written)
    {
        // An empty read can wait for input, but leaves buffered bytes for the next read.
        if (destination.IsEmpty && !_inflater.HasTrailingInput)
        {
            written = 0;
            return _offset < _length || _inflater.BufferedInputBytes > 0;
        }

        InflateStatus status = _inflater.Decode(_input.AsSpan(_offset, _length - _offset), destination, out int consumed, out written);
        _offset += consumed;
#if NET11_0_OR_GREATER
        if (status == InflateStatus.Done && written == 0 && !_rewound && source.CanSeek)
            RewindTrailingInput(source);
#endif
        return written > 0 || status != InflateStatus.NeedInput;
    }

#if NET11_0_OR_GREATER
    private void RewindTrailingInput(Stream source)
    {
        _rewound = true;
        int unread = _length - _offset + _inflater.BufferedInputBytes;
        if (unread == 0)
            return;

        try
        {
            source.Seek(-unread, SeekOrigin.Current);
        }
        catch
        {
            // Stock gzip returns EOF even if the underlying seek fails.
        }
    }
#endif

    private bool Refill(int received, bool emptyDestination)
    {
        if (received == 0)
        {
            if (!emptyDestination && _receivedInput)
                ValidateCompletedMember();
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
