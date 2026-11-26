using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Lokad.GZip.Verification;
using StockGZip = System.IO.Compression.GZipStream;

namespace Lokad.GZip.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Config(typeof(ThroughputConfig))]
public class FileGZipBenchmarks
{
    internal const string WorkloadName = "large-mixed-optimal";

    private CompressionLevel _level;
    private int _bufferSize;
    private DirectoryInfo? _directory;
    private string _input = string.Empty;
    private string _encoded = string.Empty;
    private string _output = string.Empty;
    private string _decoded = string.Empty;

    [Params(WorkloadName)]
    public string Workload { get; set; } = WorkloadName;

    [GlobalSetup]
    public void Prepare()
    {
        ReferenceCase fixture = BenchmarkWorkloads.All.Single(workload => workload.Name == Workload).Generate();
        PrepareFiles(fixture, BenchmarkVerification.Check(fixture));
    }

    private void PrepareFiles(ReferenceCase fixture, byte[] reference)
    {
        _level = fixture.Level;
        _bufferSize = fixture.ChunkSize;
        _directory = Directory.CreateTempSubdirectory("lokad-gzip-bench-");
        _input = Path.Combine(_directory.FullName, "input.bin");
        _encoded = Path.Combine(_directory.FullName, "reference.gz");
        _output = Path.Combine(_directory.FullName, "output.gz");
        _decoded = Path.Combine(_directory.FullName, "decoded.bin");

        try
        {
            File.WriteAllBytes(_input, fixture.Input);
            File.WriteAllBytes(_encoded, reference);
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    internal void Verify(ReferenceCase fixture, byte[] reference)
    {
        PrepareFiles(fixture, reference);
        try
        {
            ManagedCompress();
            VerifyCompressed(fixture, reference, stock: false);
            ManagedCompressAsync().GetAwaiter().GetResult();
            VerifyCompressed(fixture, reference, stock: false);

            StockCompress();
            VerifyCompressed(fixture, reference, stock: true);
            StockCompressAsync().GetAwaiter().GetResult();
            VerifyCompressed(fixture, reference, stock: true);

            StockDecompress();
            VerifyDecoded(fixture.Input);
            ManagedDecompress();
            VerifyDecoded(fixture.Input);
            StockDecompressAsync().GetAwaiter().GetResult();
            VerifyDecoded(fixture.Input);
            ManagedDecompressAsync().GetAwaiter().GetResult();
            VerifyDecoded(fixture.Input);
        }
        finally
        {
            Cleanup();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_directory is null)
            return;
        File.Delete(_input);
        File.Delete(_encoded);
        File.Delete(_output);
        File.Delete(_decoded);
        _directory.Delete();
        _directory = null;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("FileCompress")]
    public long StockCompress() => Compress(stock: true);

    [Benchmark, BenchmarkCategory("FileCompress")]
    public long ManagedCompress() => Compress(stock: false);

    [Benchmark(Baseline = true), BenchmarkCategory("FileCompressAsync")]
    public Task<long> StockCompressAsync() => CompressAsync(stock: true);

    [Benchmark, BenchmarkCategory("FileCompressAsync")]
    public Task<long> ManagedCompressAsync() => CompressAsync(stock: false);

    [Benchmark(Baseline = true), BenchmarkCategory("FileDecompress")]
    public long StockDecompress() => Decompress(stock: true);

    [Benchmark, BenchmarkCategory("FileDecompress")]
    public long ManagedDecompress() => Decompress(stock: false);

    [Benchmark(Baseline = true), BenchmarkCategory("FileDecompressAsync")]
    public Task<long> StockDecompressAsync() => DecompressAsync(stock: true);

    [Benchmark, BenchmarkCategory("FileDecompressAsync")]
    public Task<long> ManagedDecompressAsync() => DecompressAsync(stock: false);

    private long Compress(bool stock)
    {
        using var input = File.OpenRead(_input);
        using var output = File.Create(_output);
        using (Stream encoder = OpenEncoder(output, stock))
            input.CopyTo(encoder, _bufferSize);
        return output.Length;
    }

    private async Task<long> CompressAsync(bool stock)
    {
        await using FileStream input = OpenAsyncInput(_input);
        await using FileStream output = CreateAsyncOutput(_output);
        Stream encoder = OpenEncoder(output, stock);
        await using (encoder.ConfigureAwait(false))
            await input.CopyToAsync(encoder, _bufferSize).ConfigureAwait(false);
        return output.Length;
    }

    private long Decompress(bool stock)
    {
        using var input = File.OpenRead(_encoded);
        using var output = File.Create(_decoded);
        using Stream decoder = OpenDecoder(input, stock);
        decoder.CopyTo(output, _bufferSize);
        return output.Length;
    }

    private async Task<long> DecompressAsync(bool stock)
    {
        await using FileStream input = OpenAsyncInput(_encoded);
        await using FileStream output = CreateAsyncOutput(_decoded);
        Stream decoder = OpenDecoder(input, stock);
        await using (decoder.ConfigureAwait(false))
            await decoder.CopyToAsync(output, _bufferSize).ConfigureAwait(false);
        return output.Length;
    }

    private Stream OpenEncoder(Stream output, bool stock)
        => stock ? new StockGZip(output, _level, leaveOpen: true) : new GZipStream(output, _level, leaveOpen: true);

    private static Stream OpenDecoder(Stream input, bool stock)
        => stock ? new StockGZip(input, CompressionMode.Decompress, leaveOpen: true) :
            new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);

    private static FileStream OpenAsyncInput(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);

    private static FileStream CreateAsyncOutput(string path)
        => new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);

    private void VerifyCompressed(ReferenceCase fixture, byte[] reference, bool stock)
    {
        byte[] compressed = File.ReadAllBytes(_output);
        if (!stock || OperatingSystem.IsLinux())
        {
            if (!reference.AsSpan().SequenceEqual(compressed))
                throw new InvalidDataException("File compression differs from the Linux reference.");
            return;
        }

        using var decoder = new GZipStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
        using var decoded = new MemoryStream(fixture.Input.Length);
        decoder.CopyTo(decoded, _bufferSize);
        if (!decoded.GetBuffer().AsSpan(0, (int)decoded.Length).SequenceEqual(fixture.Input))
            throw new InvalidDataException("Stock file compression did not round trip.");
    }

    private void VerifyDecoded(ReadOnlySpan<byte> expected)
    {
        if (!expected.SequenceEqual(File.ReadAllBytes(_decoded)))
            throw new InvalidDataException("File decompression differs from the original bytes.");
    }
}
