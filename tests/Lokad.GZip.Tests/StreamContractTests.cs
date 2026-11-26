using System.IO.Compression;
using Lokad.GZip.Verification;
using ManagedGZip = Lokad.GZip.GZipStream;
using StockGZip = System.IO.Compression.GZipStream;

namespace Lokad.GZip.Tests;

public sealed class StreamContractTests
{
    [Fact]
    public void ReportsCapabilitiesAndValidatesArguments()
    {
        using var storage = new MemoryStream();
        using var compressor = new ManagedGZip(storage, CompressionMode.Compress, leaveOpen: true);
        Assert.Same(storage, compressor.BaseStream);
        Assert.True(compressor.CanWrite);
        Assert.False(compressor.CanRead);
        Assert.False(compressor.CanSeek);
        Assert.Throws<NotSupportedException>(() => compressor.Length);
        Assert.Throws<NotSupportedException>(() => compressor.Position);
        Assert.Throws<NotSupportedException>(() => compressor.Position = 0);
        Assert.Throws<NotSupportedException>(() => compressor.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => compressor.SetLength(0));
        Assert.Throws<NotSupportedException>(() => compressor.CopyTo(Stream.Null));
        Assert.Throws<InvalidOperationException>(() => compressor.ReadByte());
        Assert.Throws<ArgumentOutOfRangeException>(() => compressor.Write(new byte[1], -1, 1));
        Assert.Throws<ArgumentException>(() => new ManagedGZip(storage, (CompressionMode)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedGZip(storage, (CompressionLevel)42));

        using var readOnly = new MemoryStream([], writable: false);
        Assert.Throws<ArgumentException>(() => new ManagedGZip(readOnly, CompressionMode.Compress));
        using var decompressor = new ManagedGZip(readOnly, CompressionMode.Decompress, leaveOpen: true);
        Assert.True(decompressor.CanRead);
        Assert.False(decompressor.CanWrite);
        Assert.Throws<InvalidOperationException>(() => decompressor.WriteByte(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTripsThroughThePublicStream(bool async)
    {
        byte[] expected = ReferenceCases.MixedBytes(70001);
        using var storage = new MemoryStream();
        var stream = new ManagedGZip(storage, CompressionLevel.SmallestSize, leaveOpen: true);

        stream.WriteByte(expected[0]);
        if (async)
        {
            await stream.WriteAsync(expected.AsMemory(1, 1000));
            await stream.FlushAsync();
            await stream.WriteAsync(expected, 1001, expected.Length - 1001);
            await stream.DisposeAsync();
            await stream.DisposeAsync();
        }
        else
        {
            stream.Write(expected.AsSpan(1, 1000));
            stream.Flush();
            stream.Write(expected, 1001, expected.Length - 1001);
            stream.Dispose();
            stream.Dispose();
        }

        Assert.False(stream.CanWrite);
        Assert.True(storage.CanWrite);
        storage.Position = 0;
        using (var stock = new StockGZip(storage, CompressionMode.Decompress, leaveOpen: true))
        {
            using var decoded = new MemoryStream();
            stock.CopyTo(decoded);
            Assert.Equal(expected, decoded.ToArray());
        }

        storage.Position = 0;
        using var managed = new ManagedGZip(storage, CompressionMode.Decompress, leaveOpen: true);
        Assert.Equal(expected[0], managed.ReadByte());
        using var output = new MemoryStream();
        if (async)
            await managed.CopyToAsync(output, 257);
        else
            managed.CopyTo(output, 257);
        Assert.Equal(expected.AsSpan(1).ToArray(), output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaseStreamMatchesStockAfterDisposal(bool async)
    {
        using var input = new MemoryStream();
        using var managed = new ManagedGZip(input, CompressionMode.Decompress, leaveOpen: true);
        using var stock = new StockGZip(input, CompressionMode.Decompress, leaveOpen: true);
        Assert.Same(stock.BaseStream, managed.BaseStream);
        Assert.Same(input, managed.BaseStream);

        if (async)
        {
            await managed.DisposeAsync();
            await stock.DisposeAsync();
        }
        else
        {
            managed.Dispose();
            stock.Dispose();
        }

#if NET11_0_OR_GREATER
        Assert.Throws<ObjectDisposedException>(() => stock.BaseStream);
#else
        Assert.Null(stock.BaseStream);
#endif
        AssertDisposedBaseStream(managed);
    }

    [Fact]
    public void DisposedArrayOperationsThrowBeforeValidatingArguments()
    {
        using var input = new MemoryStream();
        var gzip = new ManagedGZip(input, CompressionMode.Decompress, leaveOpen: true);
        gzip.Dispose();

        Assert.Throws<ObjectDisposedException>(() => gzip.Read([], -1, 0));
        Assert.Throws<ObjectDisposedException>(() => gzip.Write([], -1, 0));
        Assert.Throws<ObjectDisposedException>(() => { _ = gzip.ReadAsync([], -1, 0, CancellationToken.None); });
        Assert.Throws<ObjectDisposedException>(() => { _ = gzip.WriteAsync([], -1, 0, CancellationToken.None); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachesAndHonorsOwnershipAfterFinishFails(bool leaveOpen)
    {
        using var destination = new FailingDestination();
        var gzip = new ManagedGZip(destination, CompressionLevel.Optimal, leaveOpen);
        gzip.Write("payload"u8);
        destination.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(() => gzip.DisposeAsync().AsTask());
        Assert.Equal(!leaveOpen, destination.Disposed);
        Assert.False(gzip.CanWrite);
        AssertDisposedBaseStream(gzip);
        await gzip.DisposeAsync();
    }

    private static void AssertDisposedBaseStream(ManagedGZip gzip)
    {
#if NET11_0_OR_GREATER
        Assert.Throws<ObjectDisposedException>(() => gzip.BaseStream);
#else
        Assert.Null(gzip.BaseStream);
#endif
    }

    [Fact]
    public async Task ASecondAsyncOperationCannotCorruptAPendingRead()
    {
        byte[] member = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Goldens", "probe.gz"));
        using var source = new DeferredReadStream(member);
        using var gzip = new ManagedGZip(source, CompressionMode.Decompress, leaveOpen: true);
        byte[] output = new byte[64];
        Task<int> pending = gzip.ReadAsync(output).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => { _ = gzip.ReadAsync(output); });
        source.Release.SetResult();
        Assert.Equal(11, await pending);
        Assert.Equal("Lokad.GZip\n"u8.ToArray(), output.AsSpan(0, 11).ToArray());
        Assert.Equal(0, await gzip.ReadAsync(output));
    }

    [Fact]
    public async Task ASecondAsyncOperationCannotCorruptAPendingWrite()
    {
        byte[] input = "Lokad.GZip\n"u8.ToArray();
        using var destination = new DeferredWriteStream();
        await using var gzip = new ManagedGZip(destination, CompressionLevel.Optimal, leaveOpen: true);
        Task pending = gzip.WriteAsync(input).AsTask();
        try
        {
            Assert.False(pending.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = gzip.WriteAsync(input); });
        }
        finally
        {
            destination.Release.TrySetResult();
            await pending;
        }

        await gzip.WriteAsync(ReadOnlyMemory<byte>.Empty);
        await gzip.DisposeAsync();
        byte[] expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Goldens", "probe.gz"));
        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public async Task CanceledDestinationFlushReleasesTheOperationGuard()
    {
        using var cancellation = new CancellationTokenSource();
        using var destination = new DeferredFlushStream();
        using var gzip = new ManagedGZip(destination, CompressionLevel.Optimal, leaveOpen: true);
        gzip.Write("Lokad.GZip\n"u8);

        Task pending = gzip.FlushAsync(cancellation.Token);
        try
        {
            Assert.False(pending.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = gzip.FlushAsync(); });
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            destination.Release.TrySetResult();
        }
        await gzip.FlushAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingFinishDefersDestinationDisposal(bool leaveOpen)
    {
        using var destination = new DeferredWriteStream();
        var gzip = new ManagedGZip(destination, CompressionLevel.Optimal, leaveOpen);
        gzip.Write("Lokad.GZip\n"u8);

        Task pending = gzip.DisposeAsync().AsTask();
        try
        {
            Assert.False(pending.IsCompleted);
            Assert.True(destination.CanWrite);
            AssertDisposedBaseStream(gzip);
            Assert.Throws<ObjectDisposedException>(() => gzip.WriteByte(1));
        }
        finally
        {
            destination.Release.TrySetResult();
            await pending;
        }
        Assert.Equal(leaveOpen, destination.CanWrite);
        byte[] expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Goldens", "probe.gz"));
        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public async Task ACompletedWriteFaultReleasesTheOperationGuard()
    {
        byte[] input = "Lokad.GZip\n"u8.ToArray();
        using var destination = new FailingDestination { FailWrites = true };
        await using var gzip = new ManagedGZip(destination, CompressionLevel.Optimal, leaveOpen: true);
        ValueTask pending = gzip.WriteAsync(input);
        Assert.True(pending.IsFaulted);
        destination.FailWrites = false;

        await gzip.WriteAsync(ReadOnlyMemory<byte>.Empty);
        await Assert.ThrowsAsync<IOException>(() => pending.AsTask());
    }

    [Fact]
    public void SynchronousWriteKeepsItsBuffersDuringReentrantDisposal()
    {
        using var destination = new CallbackDestination();
        using var gzip = new ManagedGZip(destination, CompressionLevel.Optimal, leaveOpen: true);
        destination.OnWrite = () => Assert.Throws<InvalidOperationException>(gzip.Dispose);
        gzip.Write("Lokad.GZip\n"u8);
        destination.OnWrite = null;
        gzip.Dispose();

        byte[] expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Goldens", "probe.gz"));
        Assert.Equal(expected, destination.ToArray());
    }

    private sealed class CallbackDestination : MemoryStream
    {
        internal Action? OnWrite { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            OnWrite?.Invoke();
            base.Write(buffer, offset, count);
        }
    }

    private sealed class DeferredReadStream(byte[] member) : MemoryStream(member)
    {
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Read(buffer.Span);
        }
    }

    private sealed class DeferredWriteStream : MemoryStream
    {
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class DeferredFlushStream : MemoryStream
    {
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Release.Task.WaitAsync(cancellationToken);
    }

    private sealed class FailingDestination : MemoryStream
    {
        internal bool FailWrites { get; set; }
        internal bool Disposed { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => FailWrites ? ValueTask.FromException(new IOException("I/O failed.")) : base.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
