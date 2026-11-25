using System.IO.Compression;
using Lokad.GZip.Verification;
using StockGZip = System.IO.Compression.GZipStream;

namespace Lokad.GZip.Tests;

public sealed class ReaderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MemoryCopyHandlesOriginsMembersAndTrailingBytes(bool async, bool readPrefix)
    {
        byte[] data = ReferenceCases.RandomBytes(32768);
        byte[] member = CreateStoredMember(data);
        byte[] encoded = [.. new byte[18], .. member, .. member, .. new byte[9000]];
        using var source = new MemoryStream(encoded, 11, encoded.Length - 11, writable: false, publiclyVisible: false)
        {
            Position = 7
        };
        using var reader = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        using var output = new MemoryStream();
        if (readPrefix)
        {
            byte[] first = new byte[1];
            int count = async ? await reader.ReadAsync(first.AsMemory()) : reader.Read(first.AsSpan());
            Assert.Equal(1, count);
            output.Write(first);
        }

        if (async)
            await reader.CopyToAsync(output, 257);
        else
            reader.CopyTo(output, 257);

        byte[] expected = [.. data, .. data];
        Assert.Equal(expected, output.ToArray());
#if NET11_0_OR_GREATER
        Assert.Equal(7L + 2L * member.Length, source.Position);
#else
        Assert.Equal(source.Length, source.Position);
#endif
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryCopyCanResumeAfterDestinationFailure(bool async)
    {
        const int bufferSize = 257;
        byte[] data = ReferenceCases.RandomBytes(32768);
        using var source = new MemoryStream(CreateStoredMember(data), writable: false);
        using var reader = new GZipStream(source, CompressionMode.Decompress);
        using var failing = new MemoryStream(Array.Empty<byte>(), 0, 0, writable: true, publiclyVisible: true);

        if (async)
            await Assert.ThrowsAsync<NotSupportedException>(() => reader.CopyToAsync(failing, bufferSize));
        else
            Assert.Throws<NotSupportedException>(() => reader.CopyTo(failing, bufferSize));

        using var output = new MemoryStream();
        if (async)
            await reader.CopyToAsync(output, bufferSize);
        else
            reader.CopyTo(output, bufferSize);
        Assert.Equal(data[bufferSize..], output.ToArray());
    }

    [Fact]
    public void MemoryCopyFallsBackForSharedStorage()
    {
        byte[] backing = CreateStoredMember(ReferenceCases.RandomBytes(32768));
        using var source = new MemoryStream(backing, writable: false);
        using var destination = new MemoryStream(backing, 0, backing.Length, writable: true, publiclyVisible: true);
        using var reader = new GZipReader();
        GZipMemoryCopy? copy = GZipMemoryCopy.Rent(reader, source, destination, new byte[8192], 8192);
        Assert.NotNull(copy);
        Assert.False(copy.TryCopy());
        Assert.Equal(0, source.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryCopyChecksTruncatedTrailerWithStrictValidation(bool async)
    {
        byte[] data = ReferenceCases.RandomBytes(32768);
        byte[] member = CreateStoredMember(data);
        using var source = new MemoryStream(member, 0, member.Length - 1, writable: false, publiclyVisible: false);
        using var reader = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();

        if (AppContext.TryGetSwitch("System.IO.Compression.UseStrictValidation", out bool strict) && strict)
            await Assert.ThrowsAsync<InvalidDataException>(CopyAsync);
        else
            await CopyAsync();

        Assert.Equal(data, output.ToArray());

        async Task CopyAsync()
        {
            if (async)
                await reader.CopyToAsync(output, 257);
            else
                reader.CopyTo(output, 257);
        }
    }

    private static byte[] CreateStoredMember(byte[] input)
    {
        using var output = new MemoryStream();
        using (var writer = new StockGZip(output, CompressionLevel.NoCompression, leaveOpen: true))
            writer.Write(input);
        return output.ToArray();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StoredRefillsPreserveHistoryAndValidateTrailer(bool async, bool file)
    {
        const int refill = 8192;
        byte[] stored = ReferenceCases.RandomBytes(6 * refill);
        byte[] expected = [.. stored, .. stored.AsSpan(4 * refill, 258)];
        byte[] header = new byte[GZipFormat.HeaderSize];
        GZipFormat.WriteHeader(header, extraFlags: 0);
        byte[] trailer = new byte[GZipFormat.TrailerSize];
        GZipFormat.WriteTrailer(trailer, Crc32.Update(Crc32.Initial, expected), (uint)expected.Length);

        // The fixed match reaches data copied when the stored block wrapped the history window.
        byte[] member = [.. header, 0, 0, 0xc0, 0xff, 0x3f, .. stored, 0x1b, 0xdd, 0xff, 0x0f, 0, .. trailer];
        using Stream source = file
            ? new FileStream(Path.Combine(Path.GetTempPath(), $"lokad-gzip-{Guid.NewGuid():N}.gz"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096,
                FileOptions.DeleteOnClose | (async ? FileOptions.Asynchronous : FileOptions.None))
            : new MemoryStream();
        source.Write(member);
        source.Position = 0;
        using var output = new MemoryStream();
        await CopyAsync(output);
        Assert.Equal(expected, output.ToArray());
        Assert.Equal((long)member.Length, source.Position);

        source.Position = member.Length - GZipFormat.TrailerSize;
        source.WriteByte((byte)(trailer[0] ^ 1));
        source.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => CopyAsync(Stream.Null));

        source.SetLength(2 * refill + 10);
        source.Position = 0;
        if (AppContext.TryGetSwitch("System.IO.Compression.UseStrictValidation", out bool strict) && strict)
            await Assert.ThrowsAsync<InvalidDataException>(() => CopyAsync(Stream.Null));
        else
            await CopyAsync(Stream.Null);

        async Task CopyAsync(Stream destination)
        {
            using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
            byte[] buffer = new byte[refill];
            int count;
            while ((count = async ? await gzip.ReadAsync(buffer.AsMemory()) : gzip.Read(buffer.AsSpan())) != 0)
                destination.Write(buffer.AsSpan(0, count));
        }
    }

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
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedEmptyReadsKeepTheFirstFragmentBuffered(bool async)
    {
        byte[] member = ReadGolden("probe");
        foreach (bool invalidHeader in new[] { false, true })
        {
            if (invalidHeader)
                member[0] = 0;

            using var source = new ShortStream(member, 1);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            for (int i = 0; i < 3; i++)
                Assert.Equal(0, async ? await gzip.ReadAsync(Memory<byte>.Empty) : gzip.Read(Span<byte>.Empty));
            Assert.Equal(1, source.AsynchronousReads + source.SynchronousReads);

            using var decoded = new MemoryStream();
            if (invalidHeader)
                await Assert.ThrowsAsync<InvalidDataException>(() => CopyAsync(gzip, decoded, async));
            else
            {
                await CopyAsync(gzip, decoded, async);
                Assert.Equal("Lokad.GZip\n"u8.ToArray(), decoded.ToArray());
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingAndCopyingAnEmptySourceMatchesStockValidation(bool async)
    {
        using var stock = new StockGZip(new MemoryStream(), CompressionMode.Decompress);
        using var managed = new GZipStream(new MemoryStream(), CompressionMode.Decompress);
        byte[] buffer = new byte[1];
        Assert.Equal(0, async ? await stock.ReadAsync(buffer) : stock.Read(buffer.AsSpan()));
        Assert.Equal(0, async ? await managed.ReadAsync(buffer) : managed.Read(buffer.AsSpan()));

        Exception? expected = await Record.ExceptionAsync(() => CopyAsync(stock, Stream.Null, async));
        Exception? actual = await Record.ExceptionAsync(() => CopyAsync(managed, Stream.Null, async));
        if (AppContext.TryGetSwitch("System.IO.Compression.UseStrictValidation", out bool strict) && strict)
            Assert.IsType<InvalidDataException>(expected);
        else
            Assert.Null(expected);
        Assert.Equal(expected?.GetType(), actual?.GetType());
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

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, false, 32)]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 32)]
    public async Task LeavesTheExpectedPositionAfterTrailingData(bool async, bool copy, int fragment)
    {
        byte[] member = ReadGolden("probe");
        byte[] trailing = new byte[copy ? 9000 : 37];
        trailing.AsSpan().Fill(0xA9);
        byte[] encoded = [0xB6, .. member, .. member, .. trailing];

        (byte[] expected, long expectedPosition) = await ReadToEndAsync(managed: false);
        (byte[] actual, long actualPosition) = await ReadToEndAsync(managed: true);
        Assert.Equal("Lokad.GZip\nLokad.GZip\n"u8.ToArray(), expected);
        Assert.Equal(expected, actual);
#if NET11_0_OR_GREATER
        if (copy && fragment > 0)
        {
            // The stock copy path can read past the gzip boundary on short-reading streams.
            Assert.Equal(1L + 2L * member.Length, actualPosition);
            return;
        }
#endif
        Assert.Equal(expectedPosition, actualPosition);

        async Task<(byte[], long)> ReadToEndAsync(bool managed)
        {
            using MemoryStream source = fragment == 0 ? new MemoryStream(encoded) : new SeekableShortStream(encoded, fragment);
            source.Position = 1;
            using Stream gzip = managed ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true) :
                new StockGZip(source, CompressionMode.Decompress, leaveOpen: true);
            using var decoded = new MemoryStream();
            if (copy)
            {
                if (async)
                    await gzip.CopyToAsync(decoded);
                else
                    gzip.CopyTo(decoded);
            }
            else
            {
                byte[] buffer = new byte[17];
                int read;
                while ((read = async ? await gzip.ReadAsync(buffer.AsMemory()) : gzip.Read(buffer.AsSpan())) > 0)
                    decoded.Write(buffer, 0, read);
                long position = source.Position;
                Assert.Equal(0, async ? await gzip.ReadAsync(buffer.AsMemory()) : gzip.Read(buffer.AsSpan()));
                Assert.Equal(position, source.Position);
            }
            return (decoded.ToArray(), source.Position);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyToDrainsTrailingDataFromNonSeekableSources(bool async)
    {
        byte[] member = ReadGolden("probe");
        byte[] tail = new byte[71];
        using var source = new ShortStream([.. member, .. tail], 17);
        using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        using var decoded = new MemoryStream();
        if (async)
            await gzip.CopyToAsync(decoded);
        else
            gzip.CopyTo(decoded);

        Assert.Equal("Lokad.GZip\n"u8.ToArray(), decoded.ToArray());
        Assert.Equal(-1, source.ReadByte());
    }

    [Fact]
    public void ValidatesTheSecondHeaderByteWhenTheSignatureSpansReads()
    {
        byte[] member = ReadGolden("probe");
        byte[] encoded = [.. member, 0x1F, 0x00];
        using var reference = new StockGZip(new ShortStream(encoded, 1), CompressionMode.Decompress);
        using var managed = new GZipStream(new ShortStream(encoded, 1), CompressionMode.Decompress);
        Assert.Throws<InvalidDataException>(() => reference.CopyTo(Stream.Null));
        Assert.Throws<InvalidDataException>(() => managed.CopyTo(Stream.Null));
    }

    private static byte[] ReadGolden(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Goldens", name + ".gz"));

    private static async Task CopyAsync(Stream source, Stream destination, bool async)
    {
        if (async)
            await source.CopyToAsync(destination);
        else
            source.CopyTo(destination);
    }

    private sealed class SeekableShortStream(byte[] input, int fragment) : MemoryStream(input)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, fragment));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, fragment)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, fragment)], cancellationToken);
    }

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
