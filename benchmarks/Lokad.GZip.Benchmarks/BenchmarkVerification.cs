using System.IO.Compression;
using System.Security.Cryptography;
using Lokad.GZip.Verification;
using StockGZip = System.IO.Compression.GZipStream;

namespace Lokad.GZip.Benchmarks;

internal static class BenchmarkVerification
{
    private static readonly Dictionary<string, string> Digests = ReadReferenceDigests();

    internal static byte[] Check(ReferenceCase fixture)
    {
        byte[] managed = ReferenceCases.CompressAsync(fixture, CreateManaged, async: false).GetAwaiter().GetResult();
        byte[] asynchronous = ReferenceCases.CompressAsync(fixture, CreateManaged, async: true).GetAwaiter().GetResult();
        if (!managed.AsSpan().SequenceEqual(asynchronous))
            throw new InvalidDataException($"Sync and async output differs for {fixture.Name}.");

        string actual = Convert.ToHexStringLower(SHA256.HashData(managed));
        if (!Digests.TryGetValue(fixture.Name, out string? expected) || actual != expected)
            throw new InvalidDataException($"Linux gzip reference differs for {fixture.Name}. Expected {expected}, got {actual}.");

        byte[] baseline = ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: false).GetAwaiter().GetResult();
        if (OperatingSystem.IsLinux() && !baseline.AsSpan().SequenceEqual(managed))
            throw new InvalidDataException($"The current Linux runtime differs from the pinned output for {fixture.Name}.");

        RoundTrip(managed, fixture, stock: true, async: false).GetAwaiter().GetResult();
        RoundTrip(baseline, fixture, stock: false, async: true).GetAwaiter().GetResult();
        return managed;
    }

    private static Stream CreateManaged(Stream destination, ReferenceCase fixture)
        => new GZipStream(destination, fixture.Level, leaveOpen: true);

    private static Dictionary<string, string> ReadReferenceDigests()
    {
        using Stream source = typeof(BenchmarkVerification).Assembly.GetManifestResourceStream("reference.sha256") ??
            throw new InvalidOperationException("The Linux benchmark reference is missing.");
        using var reader = new StreamReader(source);
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            digests.Add(Path.GetFileNameWithoutExtension(parts[1]), parts[0]);
        }
        return digests;
    }

    private static async Task RoundTrip(byte[] source, ReferenceCase fixture, bool stock, bool async)
    {
        using var buffer = new MemoryStream(source, writable: false);
        using var output = new MemoryStream(fixture.Input.Length);
        using Stream stream = stock ? new StockGZip(buffer, CompressionMode.Decompress) :
            new GZipStream(buffer, CompressionMode.Decompress);
        if (async)
            await stream.CopyToAsync(output).ConfigureAwait(false);
        else
            stream.CopyTo(output);
        if (!output.GetBuffer().AsSpan(0, (int)output.Length).SequenceEqual(fixture.Input))
            throw new InvalidDataException($"Round trip failed for {fixture.Name}.");
    }
}
