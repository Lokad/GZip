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
        Assert.Throws<ObjectDisposedException>(() => stream.BaseStream);
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
    public async Task DetachesAndHonorsOwnershipAfterFinishFails(bool leaveOpen)
    {
        using var destination = new FailingDestination();
        var gzip = new ManagedGZip(destination, CompressionLevel.Optimal, leaveOpen);
        gzip.Write("payload"u8);
        destination.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(() => gzip.DisposeAsync().AsTask());
        Assert.Equal(!leaveOpen, destination.Disposed);
        Assert.False(gzip.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => gzip.BaseStream);
        await gzip.DisposeAsync();
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
