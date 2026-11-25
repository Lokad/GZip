using System.IO.Compression;
using System.Security.Cryptography;
using Lokad.GZip.Verification;
using static Lokad.GZip.Tests.EncoderTestCases;

namespace Lokad.GZip.Tests;

public sealed class CompressedEncoderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchesLinuxGoldens(bool async)
    {
        foreach (ReferenceCase fixture in ReferenceCases.Goldens().Where(Applies))
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Goldens", fixture.Name);
            bool distinctAsync = async && File.Exists(path + "-async.gz");
            byte[] expected = File.ReadAllBytes(path + (distinctAsync ? "-async.gz" : ".gz"));
            AssertBytes(expected, await EncodeAsync(fixture, async), fixture.Name);
        }
    }

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

    [Fact]
    public async Task MatchesStockAcrossLevelsAndFlushes()
    {
        foreach (ReferenceCase fixture in ReferenceCases.Extended().Where(Applies))
            await CompareAsync(fixture);
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
            foreach ((int chunk, int flush) in new[] { (1, 0), (263, 7), (8192, 1), (int.MaxValue, 0) })
                await CompareAsync(new($"level {level}, length {length}, chunk {chunk}, flush {flush}",
                    data, ChunkSize: chunk, FlushEvery: flush, NumericLevel: level));
        }
    }

    [Theory]
    [InlineData(ZLibCompressionStrategy.HuffmanOnly)]
    [InlineData(ZLibCompressionStrategy.RunLengthEncoding)]
    public async Task MatchesStockAcrossBlockAndWindowBoundaries(ZLibCompressionStrategy strategy)
    {
        foreach (int window in new[] { 9, 15 })
        {
#if !NET11_0_OR_GREATER
            if (window != 15)
                continue;
#endif
            foreach (int size in new[] { 8187, 16383, 65529, 131101 })
            {
                foreach (int chunk in new[] { 43, 8192, int.MaxValue })
                {
                    foreach (int flushEvery in new[] { 0, 2 })
                    {
                        byte[] input = ReferenceCases.RandomBytes(size);
                        for (int i = 13; i < size; i += 773)
                            input.AsSpan(i, Math.Min(size - i, 533)).Fill((byte)i);
                        await CompareAsync(new($"{strategy} {window}/{size}/{chunk}/{flushEvery}", input,
                            ChunkSize: chunk, FlushEvery: flushEvery, NumericLevel: 6, Strategy: strategy, WindowLog2: window));
                    }
                }
            }
        }
    }

    private static bool Applies(ReferenceCase fixture)
        => fixture.Level != CompressionLevel.NoCompression && fixture.NumericLevel != 0;

    private static async Task CompareAsync(ReferenceCase fixture)
    {
        byte[] expected = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: false);
        if (!OperatingSystem.IsLinux() && expected.Length > 9)
            expected[9] = 3;
        AssertBytes(expected, await EncodeAsync(fixture, async: false), fixture.Name);
    }
}
