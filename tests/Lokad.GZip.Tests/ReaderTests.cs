using System.IO.Compression;
using Lokad.GZip.Verification;
using StockGZip = System.IO.Compression.GZipStream;

namespace Lokad.GZip.Tests;

public sealed class ReaderTests
{
    [Fact]
    public async Task ReadsShortNonSeekableStreamAsynchronously()
    {
        ReferenceCase fixture = ReferenceCases.Goldens().Single(fixture => fixture.Name == "long-match");
        byte[] encoded = ReadGolden(fixture.Name);
        using var source = new ShortStream(encoded, 7);
        using var output = new MemoryStream();
        using var reader = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);

        Assert.Equal(0, await reader.ReadAsync(Memory<byte>.Empty));
        Assert.Equal(1, source.AsynchronousReads);
        var buffer = new byte[311];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            output.Write(buffer.AsSpan(0, read));
        }

        Assert.Equal(fixture.Input, output.ToArray());
        Assert.Equal(0, source.SynchronousReads);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(4096)]
    public void ReadsConcatenatedMembersFromANonSeekableStream(int fragment)
    {
        byte[] member = ReadGolden("probe");
        byte[] encoded = [.. member, .. member];
        using var source = new ShortStream(encoded, fragment);
        using var reference = new StockGZip(new ShortStream(encoded, fragment), CompressionMode.Decompress);
        using var expected = new MemoryStream();
        reference.CopyTo(expected);

        using var reader = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        Assert.Equal(0, reader.Read(Span<byte>.Empty));
        Assert.Equal(1, source.SynchronousReads);
        using var output = new MemoryStream();
        reader.CopyTo(output, 17);

        Assert.Equal(expected.ToArray(), output.ToArray());
        Assert.Equal(0, source.AsynchronousReads);
    }

    [Fact]
    public async Task ReadCanResumeAfterSourceCancellation()
    {
        byte[] member = ReadGolden("probe");
        using var source = new ShortStream(member, 1);
        using var reader = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        var buffer = new byte[20];
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(buffer, cancellation.Token).AsTask());

        using var output = new MemoryStream();
        await reader.CopyToAsync(output, 20, CancellationToken.None);
        Assert.Equal("Lokad.GZip\n"u8.ToArray(), output.ToArray());
    }

    private static byte[] ReadGolden(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Goldens", name + ".gz"));

    private sealed class ShortStream(byte[] input, int fragment) : Stream
    {
        private int _offset;
        public int SynchronousReads { get; private set; }
        public int AsynchronousReads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            SynchronousReads++;
            return ReadData(buffer.AsSpan(offset, count));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            AsynchronousReads++;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return ReadData(buffer.Span);
        }

        private int ReadData(Span<byte> buffer)
        {
            int count = Math.Min(Math.Min(buffer.Length, fragment), input.Length - _offset);
            input.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
