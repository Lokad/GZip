using System.IO.Compression;
using System.Security.Cryptography;
using Lokad.GZip.Verification;
using StockGZip = System.IO.Compression.GZipStream;

namespace Lokad.GZip.Benchmarks;

internal static class BenchmarkVerification
{
    private static readonly Dictionary<string, string> Digests =
        ReferenceDigests.Read(typeof(BenchmarkVerification).Assembly, "reference.sha256");

    internal static byte[] Check(ReferenceCase fixture)
        => CheckAsync(fixture).GetAwaiter().GetResult();

    private static async Task<byte[]> CheckAsync(ReferenceCase fixture)
    {
        if (Environment.Version.Major != 11)
            throw new InvalidOperationException("Lokad.GZip benchmarks require .NET 11.");

        byte[] managed = await CheckCompressionAsync(fixture, CreateManaged, "Managed").ConfigureAwait(false);
        string actual = Convert.ToHexStringLower(SHA256.HashData(managed));
        if (!Digests.TryGetValue(fixture.Name + ".gz", out string? expected) || actual != expected)
            throw new InvalidDataException($"Linux gzip reference differs for {fixture.Name}. Expected {expected}, got {actual}.");

        byte[] baseline = await CheckCompressionAsync(fixture, ReferenceCases.CreateStockStream, "Stock").ConfigureAwait(false);
        bool sameMember = baseline.AsSpan().SequenceEqual(managed);
        if (OperatingSystem.IsLinux() && !sameMember)
            throw new InvalidDataException($"The current Linux runtime differs from the pinned output for {fixture.Name}.");

        foreach (bool async in new[] { false, true })
        {
            await RoundTrip(managed, fixture, stock: true, async).ConfigureAwait(false);
            await RoundTrip(managed, fixture, stock: false, async).ConfigureAwait(false);
            if (!sameMember)
                await RoundTrip(baseline, fixture, stock: false, async).ConfigureAwait(false);
        }
        return managed;
    }

    private static async Task<byte[]> CheckCompressionAsync(ReferenceCase fixture,
        Func<Stream, ReferenceCase, Stream> createStream, string implementation)
    {
        byte[] sync = await ReferenceCases.CompressAsync(fixture, createStream, async: false).ConfigureAwait(false);
        byte[] async = await ReferenceCases.CompressAsync(fixture, createStream, async: true).ConfigureAwait(false);
        if (!sync.AsSpan().SequenceEqual(async))
            throw new InvalidDataException($"{implementation} sync and async output differs for {fixture.Name}.");
        return sync;
    }

    private static Stream CreateManaged(Stream destination, ReferenceCase fixture)
        => new GZipStream(destination, fixture.Level, leaveOpen: true);

    private static async Task RoundTrip(byte[] source, ReferenceCase fixture, bool stock, bool async)
    {
        using var buffer = new MemoryStream(source, writable: false);
        using var output = new MemoryStream(fixture.Input.Length);
        Stream stream = stock ? new StockGZip(buffer, CompressionMode.Decompress, leaveOpen: true) :
            new GZipStream(buffer, CompressionMode.Decompress, leaveOpen: true);
        if (async)
        {
            await using (stream.ConfigureAwait(false))
                await stream.CopyToAsync(output, fixture.ChunkSize).ConfigureAwait(false);
        }
        else
        {
            using (stream)
                stream.CopyTo(output, fixture.ChunkSize);
        }
        if (!output.GetBuffer().AsSpan(0, (int)output.Length).SequenceEqual(fixture.Input))
            throw new InvalidDataException($"{stream.GetType().FullName} round trip failed for {fixture.Name} (async={async}).");
    }
}
