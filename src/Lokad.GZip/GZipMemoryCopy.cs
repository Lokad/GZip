namespace Lokad.GZip;

// Decode the compressed segment during MemoryStream's write callback.
internal sealed class GZipMemoryCopy : Stream
{
    [ThreadStatic]
    private static GZipMemoryCopy? _cached;

    private GZipReader _reader = null!;
    private MemoryStream _source = null!;
    private MemoryStream _destination = null!;
    private byte[] _destinationBuffer = null!;
    private byte[] _output = null!;
    private int _consumed;
    private bool _done;
    private bool _aliased;

    internal static GZipMemoryCopy? Rent(GZipReader reader, Stream source, Stream destination, byte[] output)
    {
        if (source.GetType() != typeof(MemoryStream) || destination.GetType() != typeof(MemoryStream) ||
            ReferenceEquals(source, destination) || !reader.CanCopyFromMemory((MemoryStream)source))
            return null;

        var memoryDestination = (MemoryStream)destination;
        if (!memoryDestination.TryGetBuffer(out ArraySegment<byte> buffer))
            return null;

        GZipMemoryCopy copy = _cached ?? new GZipMemoryCopy();
        _cached = null;
        copy._reader = reader;
        copy._source = (MemoryStream)source;
        copy._destination = memoryDestination;
        copy._destinationBuffer = buffer.Array!;
        copy._output = output;
        return copy;
    }

    internal bool TryCopy()
    {
        long start = _source.Position;
        try
        {
            try
            {
                _source.CopyTo(this, _output.Length);
            }
            finally
            {
                // Preserve the compressed bytes left after a trailing suffix or destination failure.
                _source.Position = start + _consumed;
            }
            return Finish();
        }
        finally
        {
            Return();
        }
    }

    internal async ValueTask<bool> TryCopyAsync(CancellationToken cancellationToken)
    {
        long start = _source.Position;
        try
        {
            try
            {
                await _source.CopyToAsync(this, _output.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _source.Position = start + _consumed;
            }
            return Finish();
        }
        finally
        {
            Return();
        }
    }

    private bool Finish()
    {
        if (_aliased)
            return false;

        _reader.FinishMemoryCopy(_source);
        return true;
    }

    private void Return()
    {
        _reader = null!;
        _source = null!;
        _destination = null!;
        _destinationBuffer = null!;
        _output = null!;
        _consumed = 0;
        _done = _aliased = false;
        _cached = this;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> input)
    {
        if (!CanDecode(input))
            return;

        InflateStatus status;
        do
        {
            status = _reader.DecodeCopyInput(input, _output, out int consumed, out int written);
            _consumed += consumed;
            input = input[consumed..];
            if (written != 0)
                _destination.Write(_output, 0, written);
        } while (status == InflateStatus.NeedOutput);
        _done = status == InflateStatus.Done;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        if (!CanDecode(input.Span))
            return;

        InflateStatus status;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            status = _reader.DecodeCopyInput(input.Span, _output, out int consumed, out int written);
            _consumed += consumed;
            input = input[consumed..];
            if (written != 0)
                await _destination.WriteAsync(_output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        } while (status == InflateStatus.NeedOutput);
        _done = status == InflateStatus.Done;
    }

    private bool CanDecode(ReadOnlySpan<byte> input)
    {
        if (_done || _aliased || input.IsEmpty)
            return false;

        _aliased = _consumed == 0 && input.Overlaps(_destinationBuffer.AsSpan());
        return !_aliased;
    }
}
