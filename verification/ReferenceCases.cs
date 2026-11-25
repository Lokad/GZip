using System.IO.Compression;

namespace Lokad.GZip.Verification;

internal enum EmptyOperation
{
    Dispose,
    Write,
    Flush,
}

internal sealed record ReferenceCase(
    string Name,
    byte[] Input,
    CompressionLevel Level = CompressionLevel.Optimal,
    int ChunkSize = int.MaxValue,
    int FlushEvery = 0,
    EmptyOperation Empty = EmptyOperation.Dispose,
    int? NumericLevel = null,
    ZLibCompressionStrategy Strategy = ZLibCompressionStrategy.Default,
    int WindowLog2 = -1);

internal static class ReferenceCases
{
    internal static IEnumerable<ReferenceCase> Goldens()
    {
        yield return new("probe", "Lokad.GZip\n"u8.ToArray());
        yield return new("empty-dispose", []);
        yield return new("empty-write", [], Empty: EmptyOperation.Write);
        yield return new("empty-flush", [], Empty: EmptyOperation.Flush);
        yield return new("one-byte", [0xA5]);
        yield return new("stored", RandomBytes(257), CompressionLevel.NoCompression);
        yield return new("stored-flush", RandomBytes(53), CompressionLevel.NoCompression, 11, 2);
        yield return new("fastest", MixedBytes(977), CompressionLevel.Fastest, 81);
        yield return new("smallest-size", TextBytes(32769), CompressionLevel.SmallestSize, 1673);
        yield return new("entropy", RandomBytes(4097), ChunkSize: 127);
        yield return new("long-match", new byte[98601], ChunkSize: 32768);
        yield return new("dynamic", TextBytes(19537), ChunkSize: 309);
        yield return new("window", TextBytes(65539), ChunkSize: 32767);
        yield return new("frequent-flush", TextBytes(4369), ChunkSize: 59, FlushEvery: 3);
        yield return new("huffman-only", TextBytes(997), NumericLevel: 5, Strategy: ZLibCompressionStrategy.HuffmanOnly);
        yield return new("run-length", TextBytes(1063), NumericLevel: 2, Strategy: ZLibCompressionStrategy.RunLengthEncoding);
        yield return new("filtered", MixedBytes(2341), NumericLevel: 4, Strategy: ZLibCompressionStrategy.Filtered);
        yield return new("fixed", TextBytes(6071), NumericLevel: 7, Strategy: ZLibCompressionStrategy.Fixed);
#if NET11_0_OR_GREATER
        yield return new("small-window", TextBytes(4239), NumericLevel: 5, WindowLog2: 9);
#endif
    }

    internal static IEnumerable<ReferenceCase> Extended()
    {
        foreach (ReferenceCase fixture in Goldens())
            yield return fixture;

        byte[] mixed = MixedBytes(70001);
        foreach (int level in Enumerable.Range(-1, 11))
        {
            foreach (ZLibCompressionStrategy strategy in Enum.GetValues<ZLibCompressionStrategy>())
            {
                yield return new($"options-{level}-{strategy}", mixed, ChunkSize: 1997, NumericLevel: level, Strategy: strategy);
                yield return new($"options-flush-{level}-{strategy}", mixed[..1027], ChunkSize: 257, FlushEvery: 1,
                    NumericLevel: level, Strategy: strategy);
            }
        }

        foreach (int size in new[] { 257, 258, 259, 8191, 8192, 8193, 16383, 16384, 16385, 32767, 32768, 32769, 65534, 65535, 65536 })
        {
            yield return new($"boundary-stored-{size}", RandomBytes(size), CompressionLevel.NoCompression);
            yield return new($"boundary-mixed-{size}", MixedBytes(size), ChunkSize: 8192);
        }

#if NET11_0_OR_GREATER
        foreach (int window in Enumerable.Range(8, 8))
            yield return new($"window-{window}", mixed, NumericLevel: 6, WindowLog2: window);
#endif
    }

    internal static ZLibCompressionOptions GetOptions(ReferenceCase fixture)
    {
        var options = new ZLibCompressionOptions
        {
            CompressionLevel = fixture.NumericLevel.GetValueOrDefault(-1),
            CompressionStrategy = fixture.Strategy,
        };
#if NET11_0_OR_GREATER
        options.WindowLog2 = fixture.WindowLog2;
#endif
        return options;
    }

    internal static Stream CreateStockStream(Stream destination, ReferenceCase fixture)
    {
        return fixture.NumericLevel.HasValue
            ? new System.IO.Compression.GZipStream(destination, GetOptions(fixture), leaveOpen: true)
            : new System.IO.Compression.GZipStream(destination, fixture.Level, leaveOpen: true);
    }

    internal static async Task<byte[]> CompressAsync(
        ReferenceCase fixture,
        Func<Stream, ReferenceCase, Stream> createStream,
        bool async)
    {
        using var output = new MemoryStream();
        Stream stream = createStream(output, fixture);
        try
        {
            if (fixture.Input.Length == 0)
            {
                if (fixture.Empty == EmptyOperation.Write)
                {
                    if (async)
                        await stream.WriteAsync(ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
                    else
                        stream.Write(ReadOnlySpan<byte>.Empty);
                }
                else if (fixture.Empty == EmptyOperation.Flush)
                {
                    if (async)
                        await stream.FlushAsync().ConfigureAwait(false);
                    else
                        stream.Flush();
                }
            }

            int chunk = 0;
            for (int offset = 0; offset < fixture.Input.Length;)
            {
                int length = Math.Min(fixture.Input.Length - offset, fixture.ChunkSize);
                ReadOnlyMemory<byte> slice = fixture.Input.AsMemory(offset, length);
                if (async)
                    await stream.WriteAsync(slice).ConfigureAwait(false);
                else
                    stream.Write(slice.Span);
                offset += length;

                if (fixture.FlushEvery > 0 && ++chunk % fixture.FlushEvery == 0)
                {
                    if (async)
                        await stream.FlushAsync().ConfigureAwait(false);
                    else
                        stream.Flush();
                }
            }
        }
        finally
        {
            if (async)
                await stream.DisposeAsync().ConfigureAwait(false);
            else
                stream.Dispose();
        }
        return output.ToArray();
    }

    internal static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        uint state = 0x69257386;
        for (int i = 0; i < bytes.Length; ++i)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[i] = (byte)state;
        }
        return bytes;
    }

    internal static byte[] MixedBytes(int length)
    {
        byte[] bytes = RandomBytes(length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if ((i % 193) < 129)
                bytes[i] = (byte)('a' + (i % 21));
        }
        return bytes;
    }

    internal static byte[] TextBytes(int length)
    {
        var bytes = new byte[length];
        ReadOnlySpan<byte> phrase = "{\"id\":1920687,\"active\":true,\"location\":\"Paris\",\"timezone\":\"Europe/Paris\"}\n"u8;
        for (int i = 0; i < bytes.Length; ++i)
        {
            byte value = phrase[i % phrase.Length];
            if (value is >= (byte)'0' and <= (byte)'9')
                value = (byte)('0' + (i % 4099) % 10);
            bytes[i] = value;
        }
        return bytes;
    }
}
