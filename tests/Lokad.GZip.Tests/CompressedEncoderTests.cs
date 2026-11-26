using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Lokad.GZip.Verification;
using static Lokad.GZip.Tests.EncoderTestCases;

namespace Lokad.GZip.Tests;

public sealed class CompressedEncoderTests
{
    private const int DefaultWindowSize = 32768;

    [Fact]
    public async Task MatchesExtendedLinuxManifest()
    {
        Dictionary<string, string> expected = ReferenceDigests.Read(typeof(CompressedEncoderTests).Assembly, "reference.sha256");
        foreach (ReferenceCase fixture in ReferenceCases.Extended())
        {
            string syncName = fixture.Name + ".gz";
            Assert.True(expected.Remove(syncName, out string? syncDigest), $"Missing Linux reference for {syncName}.");
            string asyncName = fixture.Name + "-async.gz";
            string asyncDigest = expected.Remove(asyncName, out string? distinctDigest) ? distinctDigest : syncDigest;

            AssertDigest(syncName, syncDigest, await EncodeAsync(fixture, async: false));
            AssertDigest(asyncName, asyncDigest, await EncodeAsync(fixture, async: true));
        }
        Assert.Empty(expected.Keys);
    }

    private static void AssertDigest(string name, string expected, byte[] encoded)
    {
        string actual = Convert.ToHexStringLower(SHA256.HashData(encoded));
        Assert.True(actual == expected, $"{name} SHA-256 differs from the pinned Linux output. Expected {expected}, got {actual}.");
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("fastest")]
    [InlineData("smallest-size")]
    [InlineData("frequent-flush")]
    public Task MatchesRepresentativeStockCases(string name)
        => CompareAsync(ReferenceCases.Goldens().Single(fixture => fixture.Name == name));

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public async Task MatchesStockWithDirtyPooledBuffers(int level)
    {
        const int lookahead = DeflateCodes.MaximumLength + DeflateCodes.MinimumLength + 1;
        const int slideAt = 2 * DefaultWindowSize - lookahead;
        foreach (int length in new[] { 96, 2 * slideAt + 1 })
        {
            await CompareAsync(new($"pooled buffers level {level} length {length}", ReferenceCases.MixedBytes(length),
                ChunkSize: slideAt, FlushEvery: 1, NumericLevel: level), dirtyBuffers: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task MatchesStockAcrossLookaheadAndOutputBoundaries(int level)
    {
        foreach (int length in new[] { 261, 262, 263, 16383, 32769, 65523, 200003 })
        {
            byte[] data = ReferenceCases.RandomBytes(length);
            for (int offset = 71; offset < data.Length; offset += 613)
                data.AsSpan(0, Math.Min(data.Length - offset, 321)).CopyTo(data.AsSpan(offset));
            foreach ((int chunk, int flush) in DistinctWriteSchedules(length, [(1, 0), (263, 7), (8192, 1), (int.MaxValue, 0)]))
                await CompareAsync(new($"level {level}, length {length}, chunk {chunk}, flush {flush}",
                    data, ChunkSize: chunk, FlushEvery: flush, NumericLevel: level));
        }
    }

    [Theory]
    [InlineData(ZLibCompressionStrategy.HuffmanOnly)]
    [InlineData(ZLibCompressionStrategy.RunLengthEncoding)]
    public async Task MatchesStockAcrossBlockAndWindowBoundaries(ZLibCompressionStrategy strategy)
    {
        var schedules = from chunk in new[] { 43, 8192, int.MaxValue }
                        from flush in new[] { 0, 2 }
                        select (chunk, flush);
        foreach (int size in new[] { 8187, 16383, 65529, 131101 })
        {
            byte[] input = ReferenceCases.RandomBytes(size);
            for (int i = 13; i < size; i += 773)
                input.AsSpan(i, Math.Min(size - i, 533)).Fill((byte)i);

            foreach (int window in new[] { 9, 15 })
            {
#if !NET11_0_OR_GREATER
                if (window != 15)
                    continue;
#endif
                foreach ((int chunk, int flush) in DistinctWriteSchedules(size, schedules))
                    await CompareAsync(new($"{strategy} {window}/{size}/{chunk}/{flush}", input,
                        ChunkSize: chunk, FlushEvery: flush, NumericLevel: 6, Strategy: strategy, WindowLog2: window));
            }
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task MatchesStockAcrossLongRunBoundaries(int level)
    {
        const int maximumSymbols = 16383;
        int blockBoundary = maximumSymbols * DeflateCodes.MaximumLength;
        byte[] input = new byte[blockBoundary + 2 * DefaultWindowSize];
        input.AsSpan().Fill(0xa6);
        input[0] = 0x5b;
        // End the long run after it has crossed a block boundary, then revisit its byte value.
        int tail = blockBoundary + 37;
        ReferenceCases.RandomBytes(573).CopyTo(input.AsSpan(tail));
        input.AsSpan(input.Length - 517).Fill(0x5b);
        await Check(new("long run across a DEFLATE block", input, NumericLevel: level));

        input = input[^((2 * DefaultWindowSize) + 319)..];
        await Check(new("long runs with frequent flushes", input, ChunkSize: 1021, FlushEvery: 7, NumericLevel: level));
#if NET11_0_OR_GREATER
        await Check(new("long runs with a small window", input, ChunkSize: 521, FlushEvery: 11,
            NumericLevel: level, WindowLog2: 10));
#endif

        static async Task Check(ReferenceCase fixture)
        {
            byte[] stock = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: false);
            if (!OperatingSystem.IsLinux())
                stock[9] = 3;
            AssertBytes(stock, await EncodeAsync(fixture, async: false), fixture.Name);
            AssertBytes(stock, await EncodeAsync(fixture, async: true), fixture.Name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchesStockWhenHighEntropyBlocksChooseStoredOrDynamic(bool biased)
    {
        byte[] input = ReferenceCases.RandomBytes(4 * 16383 + 7);
        if (biased)
        {
            for (int i = 0; i < input.Length; i += 8)
                input[i] = 0;
        }

        foreach (int chunkSize in new[] { 8191, input.Length })
        {
            var fixture = new ReferenceCase($"high entropy biased={biased}, chunk={chunkSize}", input,
                ChunkSize: chunkSize, NumericLevel: 6, Strategy: ZLibCompressionStrategy.HuffmanOnly);
            byte[] stock = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: false);
            if (!OperatingSystem.IsLinux())
                stock[9] = 3;
            Assert.Equal(biased ? 2 : 0, (stock[10] >> 1) & 3);
            AssertBytes(stock, await EncodeAsync(fixture, async: false), fixture.Name);
            AssertBytes(stock, await EncodeAsync(fixture, async: true), fixture.Name);
        }
    }

    [Fact]
    public async Task MatchesStockWhenALiteralBlockEndsBeforeAMatch()
    {
        const int boundary = 16383;
        byte[] input = ReferenceCases.RandomBytes(boundary + DefaultWindowSize);
        input.AsSpan(123, 137).CopyTo(input.AsSpan(boundary));
        var priorWords = new HashSet<uint>();
        for (int position = 0; position < boundary; position++)
            Assert.True(priorWords.Add(BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(position))));
        Assert.Contains(BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(boundary)), priorWords);

        foreach (int chunkSize in new[] { 8192, input.Length })
        {
            var fixture = new ReferenceCase($"literal block, chunk={chunkSize}", input, ChunkSize: chunkSize, NumericLevel: 6);
            byte[] stock = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: false);
            if (!OperatingSystem.IsLinux())
                stock[9] = 3;
            Assert.Equal(0, (stock[10] >> 1) & 3);
            Assert.Equal(boundary, BinaryPrimitives.ReadUInt16LittleEndian(stock.AsSpan(11)));
            AssertBytes(stock, await EncodeAsync(fixture, async: false), fixture.Name);
            AssertBytes(stock, await EncodeAsync(fixture, async: true), fixture.Name);
        }
    }

    [MinimumPreviousTheory]
    [InlineData(507, 522)]
    [InlineData(517, 527)]
    public void KeepsTheFirstMinimumWhenPredecessorLinksWrap(int first, int second)
    {
        const int start = 501;
        const int count = 40;
        const int minimum = 64;
        using var dictionary = new DeflateDictionary(windowBits: 9, level: 2);
        for (int position = start - 1; position < start + count; position++)
            dictionary.InsertWord(0, position);
        dictionary.InsertWord(1, minimum);
        dictionary.InsertWord(1, first);
        dictionary.InsertWord(1, minimum);
        dictionary.InsertWord(1, second);

        int candidate = 600;
        Assert.Equal(first - start, dictionary.FindMinimumPrevious(start, count, ref candidate));
        Assert.Equal(minimum, candidate);
        Assert.Equal(0, dictionary.FindMinimumPrevious(start, count, ref candidate));
    }

    private sealed class MinimumPreviousTheoryAttribute : TheoryAttribute
    {
        public MinimumPreviousTheoryAttribute()
        {
            if (!DeflateDictionary.CanFindMinimumPrevious)
                Skip = "Requires SSE4.1 or Arm64 Advanced SIMD.";
        }
    }

    private static async Task CompareAsync(ReferenceCase fixture, bool dirtyBuffers = false)
    {
        byte[] expected = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: false);
        if (!OperatingSystem.IsLinux() && expected.Length > 9)
            expected[9] = 3;
        if (dirtyBuffers)
        {
            byte[] window = ArrayPool<byte>.Shared.Rent(2 * DefaultWindowSize + 8);
            window.AsSpan().Fill(0xD7);
            ArrayPool<byte>.Shared.Return(window);

            ushort[] links = ArrayPool<ushort>.Shared.Rent(DefaultWindowSize);
            links.AsSpan().Fill(0xFFD7);
            ArrayPool<ushort>.Shared.Return(links);
        }
        AssertBytes(expected, await EncodeAsync(fixture, async: false), fixture.Name);
    }
}
