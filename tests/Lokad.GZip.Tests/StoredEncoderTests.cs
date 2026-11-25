using System.IO.Compression;
using Lokad.GZip.Verification;
using static Lokad.GZip.Tests.EncoderTestCases;

namespace Lokad.GZip.Tests;

public sealed class StoredEncoderTests
{
    [Fact]
    public async Task MatchesLinuxStoredGoldens()
    {
        foreach (ReferenceCase fixture in ReferenceCases.Goldens().Where(fixture => fixture.Level == CompressionLevel.NoCompression))
        {
            byte[] expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Goldens", fixture.Name + ".gz"));
            AssertBytes(expected, await EncodeAsync(fixture, async: false), fixture.Name);
            AssertBytes(expected, await EncodeAsync(fixture, async: true), fixture.Name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchesStockEmptySchedules(bool async)
    {
        foreach (EmptyOperation operation in Enum.GetValues<EmptyOperation>())
        {
            var fixture = new ReferenceCase(operation.ToString(), [], CompressionLevel.NoCompression, Empty: operation);
            await AssertMatchesStock(fixture, async);
        }
    }

    [Fact]
    public async Task MatchesStockAcrossStoredBlockBoundaries()
    {
        foreach (int size in new[] { 1, 8177, 8187, 8192, 32762, 32763, 32768, 65535, 65536, 130993, 250027 })
        {
            foreach (int chunk in new[] { 127, 8192, 32763, int.MaxValue })
            {
                foreach (int flushEvery in new[] { 0, 1, 3 })
                {
                    var fixture = new ReferenceCase($"size {size}, chunk {chunk}, flush every {flushEvery}",
                        ReferenceCases.RandomBytes(size), CompressionLevel.NoCompression, chunk, flushEvery);
                    await AssertMatchesStock(fixture);
                }
            }
        }
    }

#if NET11_0_OR_GREATER
    [Fact]
    public async Task MatchesStockWithNarrowWindows()
    {
        foreach (int window in Enumerable.Range(9, 6))
        {
            foreach (int chunk in new[] { 43, 1027, 8192, int.MaxValue })
            {
                foreach (int flushEvery in new[] { 0, 1, 2 })
                {
                    var fixture = new ReferenceCase($"window {window}, chunk {chunk}, flush every {flushEvery}",
                        ReferenceCases.RandomBytes(29039), ChunkSize: chunk, FlushEvery: flushEvery, NumericLevel: 0, WindowLog2: window);
                    await AssertMatchesStock(fixture);
                }
            }
        }
    }
#endif

    private static async Task AssertMatchesStock(ReferenceCase fixture, bool async = false)
    {
        byte[] expected = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async);
        if (!OperatingSystem.IsLinux() && expected.Length > 9)
            expected[9] = 3;
        AssertBytes(expected, await EncodeAsync(fixture, async), fixture.Name);
    }

}
