using System.Buffers;
using System.IO.Compression;

namespace Lokad.GZip;

/// <summary>A managed gzip stream with deterministic compression.</summary>
public class GZipStream : Stream
{
    private readonly bool _leaveOpen;
    private Stream? _baseStream;
    private GZipReader? _reader;
    private GZipWriter? _writer;
    private int _operation;

    /// <summary>Opens a stream for gzip compression or decompression.</summary>
    public GZipStream(Stream stream, CompressionMode mode)
        : this(stream, mode, leaveOpen: false)
    {
    }

    /// <summary>Opens a gzip stream and optionally leaves the underlying stream open.</summary>
    public GZipStream(Stream stream, CompressionMode mode, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (mode == CompressionMode.Decompress)
        {
            if (!stream.CanRead)
                throw new ArgumentException("The underlying stream must be readable.", nameof(stream));
            _reader = new GZipReader();
        }
        else if (mode == CompressionMode.Compress)
        {
            _writer = CreateWriter(stream, 6, ZLibCompressionStrategy.Default, 15);
        }
        else
        {
            throw new ArgumentException("The compression mode is invalid.", nameof(mode));
        }
        _baseStream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Opens a gzip stream for compression at the specified level.</summary>
    public GZipStream(Stream stream, CompressionLevel compressionLevel)
        : this(stream, compressionLevel, leaveOpen: false)
    {
    }

    /// <summary>Opens a gzip stream for compression and optionally leaves the underlying stream open.</summary>
    public GZipStream(Stream stream, CompressionLevel compressionLevel, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        int level = compressionLevel switch
        {
            CompressionLevel.Optimal => 6,
            CompressionLevel.Fastest => 1,
            CompressionLevel.NoCompression => 0,
            CompressionLevel.SmallestSize => 9,
            _ => throw new ArgumentOutOfRangeException(nameof(compressionLevel)),
        };
        _writer = CreateWriter(stream, level, ZLibCompressionStrategy.Default, 15);
        _baseStream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Opens a gzip stream for compression with the specified encoder options.</summary>
    public GZipStream(Stream stream, ZLibCompressionOptions compressionOptions, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(compressionOptions);
        int window = 15;
#if NET11_0_OR_GREATER
        if (compressionOptions.WindowLog2 >= 0)
            window = Math.Max(compressionOptions.WindowLog2, 9);
#endif
        int level = compressionOptions.CompressionLevel < 0 ? 6 : compressionOptions.CompressionLevel;
        _writer = CreateWriter(stream, level, compressionOptions.CompressionStrategy, window);
        _baseStream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Gets the underlying stream.</summary>
    public Stream BaseStream
    {
        get
        {
            Stream? stream = _baseStream;
            ObjectDisposedException.ThrowIf(stream is null, this);
            return stream;
        }
    }

    /// <inheritdoc/>
    public override bool CanRead => _reader is not null && _baseStream?.CanRead == true;

    /// <inheritdoc/>
    public override bool CanWrite => _writer is not null && _baseStream?.CanWrite == true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        _ = BaseStream;
        ValidateBufferArguments(buffer, offset, count);
        return ReadCore(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        if (GetType() != typeof(GZipStream))
            return base.Read(buffer);
        return ReadCore(buffer);
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return ReadCore(buffer) == 0 ? -1 : buffer[0];
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _ = BaseStream;
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsyncCore(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => GetType() == typeof(GZipStream)
            ? ReadAsyncCore(buffer, cancellationToken)
            : base.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        _ = BaseStream;
        ValidateBufferArguments(buffer, offset, count);
        WriteCore(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (GetType() != typeof(GZipStream))
        {
            base.Write(buffer);
            return;
        }
        WriteCore(buffer);
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value)
    {
        if (GetType() == typeof(GZipStream))
            Write(new ReadOnlySpan<byte>(in value));
        else
            base.WriteByte(value);
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _ = BaseStream;
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsyncCore(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => GetType() == typeof(GZipStream)
            ? WriteAsyncCore(buffer, cancellationToken)
            : base.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc/>
    public override void Flush()
    {
        Stream destination = EnterOperation();
        try
        {
            _writer?.Flush(destination);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Stream destination = EnterOperation();
        if (cancellationToken.IsCancellationRequested)
        {
            ExitOperation();
            return Task.FromCanceled(cancellationToken);
        }
        if (_writer is not GZipWriter writer)
        {
            ExitOperation();
            return Task.CompletedTask;
        }

        return FlushAsyncCore(writer, destination, cancellationToken);
    }

    /// <inheritdoc/>
    public override void CopyTo(Stream destination, int bufferSize)
    {
        Stream source = EnterOperation();
        byte[]? buffer = null;
        try
        {
            ValidateCopyToArguments(destination, bufferSize);
            if (!CanRead)
                throw new NotSupportedException();

            GZipReader reader = Reader;
            buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            if (GZipMemoryCopy.Rent(reader, source, destination, buffer, bufferSize) is { } copy && copy.TryCopy())
                return;

            int count;
            while ((count = reader.Read(source, buffer.AsSpan(0, bufferSize))) != 0)
                destination.Write(buffer, 0, count);
            if (reader.ShouldDrainTrailingInput(source))
                source.CopyTo(Stream.Null, bufferSize);
            reader.ValidateCompletedMember();
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        Stream source = EnterOperation();
        try
        {
            ValidateCopyToArguments(destination, bufferSize);
            if (!CanRead)
                throw new NotSupportedException();
            if (cancellationToken.IsCancellationRequested)
            {
                Task canceled = Task.FromCanceled(cancellationToken);
                ExitOperation();
                return canceled;
            }
            return CopyAsyncCore(Reader, source, destination, bufferSize, cancellationToken);
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (!disposing)
                return;
            Stream? stream = DetachStream();
            if (stream is null)
                return;

            GZipWriter? writer = _writer;
            GZipReader? reader = _reader;
            _writer = null;
            _reader = null;
            try
            {
                writer?.Finish(stream);
            }
            finally
            {
                try
                {
                    writer?.Dispose();
                    reader?.Dispose();
                    if (!_leaveOpen)
                        stream.Dispose();
                }
                finally
                {
                    ExitOperation();
                }
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    /// <inheritdoc/>
    public override ValueTask DisposeAsync()
    {
        if (GetType() != typeof(GZipStream))
            return base.DisposeAsync();
        Stream? stream = DetachStream();
        if (stream is null)
            return ValueTask.CompletedTask;

        GZipWriter? writer = _writer;
        GZipReader? reader = _reader;
        _writer = null;
        _reader = null;
        return DisposeAsyncCore(stream, writer, reader);
    }

    private static GZipWriter CreateWriter(Stream stream, int level, ZLibCompressionStrategy strategy, int window)
    {
        if (!stream.CanWrite)
            throw new ArgumentException("The underlying stream must be writable.", nameof(stream));
        return new(level == 0 ? new StoredEncoder(window) : new CompressedEncoder(level, strategy, window));
    }

    private GZipReader Reader => _reader ?? throw new InvalidOperationException("The gzip stream is open for compression.");

    private GZipWriter Writer => _writer ?? throw new InvalidOperationException("The gzip stream is open for decompression.");

    private int ReadCore(Span<byte> buffer)
    {
        Stream source = EnterOperation();
        try
        {
            return Reader.Read(source, buffer);
        }
        finally
        {
            ExitOperation();
        }
    }

    private void WriteCore(ReadOnlySpan<byte> buffer)
    {
        Stream destination = EnterOperation();
        try
        {
            Writer.Write(destination, buffer);
        }
        finally
        {
            ExitOperation();
        }
    }

    private ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        Stream source = EnterOperation();
        try
        {
            GZipReader reader = Reader;
            if (cancellationToken.IsCancellationRequested)
            {
                ValueTask<int> canceled = ValueTask.FromCanceled<int>(cancellationToken);
                ExitOperation();
                return canceled;
            }
            return ReadAsyncGuarded(reader, source, buffer, cancellationToken);
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    private ValueTask WriteAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        Stream destination = EnterOperation();
        try
        {
            GZipWriter writer = Writer;
            if (cancellationToken.IsCancellationRequested)
            {
                ValueTask canceled = ValueTask.FromCanceled(cancellationToken);
                ExitOperation();
                return canceled;
            }
            return WriteAsyncGuarded(writer, destination, buffer, cancellationToken);
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    private async ValueTask<int> ReadAsyncGuarded(GZipReader reader, Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(source, buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private ValueTask WriteAsyncGuarded(GZipWriter writer, Stream destination, ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken)
    {
        ValueTask pending = writer.WriteAsync(destination, buffer, cancellationToken);
        if (!pending.IsCompleted)
            return AwaitWriteAsync(pending);

        ExitOperation();
        return pending;
    }

    private async ValueTask AwaitWriteAsync(ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private async Task FlushAsyncCore(GZipWriter writer, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            await writer.FlushAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private async Task CopyAsyncCore(GZipReader reader, Stream source, Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        byte[]? buffer = null;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            if (GZipMemoryCopy.Rent(reader, source, destination, buffer, bufferSize) is { } copy &&
                await copy.TryCopyAsync(cancellationToken).ConfigureAwait(false))
                return;

            int count;
            while ((count = await reader.ReadAsync(source, buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false)) != 0)
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (reader.ShouldDrainTrailingInput(source))
                await source.CopyToAsync(Stream.Null, bufferSize, cancellationToken).ConfigureAwait(false);
            reader.ValidateCompletedMember();
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            ExitOperation();
        }
    }

    private async ValueTask DisposeAsyncCore(Stream stream, GZipWriter? writer, GZipReader? reader)
    {
        try
        {
            if (writer is not null)
                await writer.FinishAsync(stream).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                writer?.Dispose();
                reader?.Dispose();
                if (!_leaveOpen)
                    await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ExitOperation();
            }
        }
    }

    private Stream EnterOperation()
    {
        AcquireOperation();
        Stream? stream = _baseStream;
        if (stream is not null)
            return stream;

        ExitOperation();
        throw new ObjectDisposedException(GetType().FullName);
    }

    private Stream? DetachStream()
    {
        if (Volatile.Read(ref _baseStream) is null)
            return null;

        AcquireOperation();
        Stream? stream = Interlocked.Exchange(ref _baseStream, null);
        if (stream is null)
            ExitOperation();
        return stream;
    }

    private void AcquireOperation()
    {
        if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0)
        {
            _ = BaseStream;
            throw new InvalidOperationException("An operation is already active on the gzip stream.");
        }
    }

    private void ExitOperation() => Volatile.Write(ref _operation, 0);
}
