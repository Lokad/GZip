using System.IO.Compression;
using static Lokad.GZip.Verification.ReferenceCases;

namespace Lokad.GZip.Verification;

internal sealed record BenchmarkWorkload(
    string Name, int Size, Func<int, byte[]> CreateData, CompressionLevel Level, int BufferSize, int FlushEvery = 0)
{
    internal ReferenceCase Generate() => new(Name, CreateData(Size), Level, BufferSize, FlushEvery);
}

internal static class BenchmarkWorkloads
{
    private const int KiB = 1024;
    private const int MiB = 1024 * KiB;

    internal static IReadOnlyList<BenchmarkWorkload> All { get; } =
    [
        new("tiny-text", 96, TextBytes, CompressionLevel.Optimal, 256),
        new("small-text-fast", 16 * KiB, TextBytes, CompressionLevel.Fastest, 4 * KiB),
        new("text-optimal", MiB, TextBytes, CompressionLevel.Optimal, 32 * KiB),
        new("mixed-optimal", MiB, MixedBytes, CompressionLevel.Optimal, 8 * KiB),
        new("mixed-fast", MiB, MixedBytes, CompressionLevel.Fastest, 64 * KiB),
        new("mixed-smallest", 256 * KiB, MixedBytes, CompressionLevel.SmallestSize, 8 * KiB),
        new("entropy-optimal", MiB, RandomBytes, CompressionLevel.Optimal, 32 * KiB),
        new("entropy-stored", MiB, RandomBytes, CompressionLevel.NoCompression, 64 * KiB),
        new("mixed-flush", 64 * KiB, MixedBytes, CompressionLevel.Optimal, 1009, FlushEvery: 4),
        new("runs-optimal", MiB, size => new byte[size], CompressionLevel.Optimal, 32 * KiB),
    ];
}
