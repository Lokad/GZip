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
    private const int BufferSize = 8192;
    private DirectoryInfo? _directory;
    private string _input = string.Empty;
    private string _encoded = string.Empty;
    private string _output = string.Empty;
    private string _decoded = string.Empty;

    [Params("mixed-optimal")]
    public string Workload { get; set; } = "mixed-optimal";

    [GlobalSetup]
    public void Prepare()
    {
        ReferenceCase fixture = BenchmarkWorkloads.All.Single(workload => workload.Name == Workload).Generate();
        byte[] reference = BenchmarkVerification.Check(fixture);
        _directory = Directory.CreateTempSubdirectory("lokad-gzip-bench-");
        _input = Path.Combine(_directory.FullName, "input.bin");
        _encoded = Path.Combine(_directory.FullName, "reference.gz");
        _output = Path.Combine(_directory.FullName, "output.gz");
        _decoded = Path.Combine(_directory.FullName, "decoded.bin");

        try
        {
            File.WriteAllBytes(_input, fixture.Input);
            File.WriteAllBytes(_encoded, reference);
            ManagedCompress();
            if (!reference.AsSpan().SequenceEqual(File.ReadAllBytes(_output)))
                throw new InvalidDataException("File compression differs from the Linux reference.");

            StockCompress();
            using (var stockOutput = new GZipStream(File.OpenRead(_output), CompressionMode.Decompress))
            {
                using var stockDecoded = new MemoryStream(fixture.Input.Length);
                stockOutput.CopyTo(stockDecoded, BufferSize);
                if (!stockDecoded.GetBuffer().AsSpan(0, (int)stockDecoded.Length).SequenceEqual(fixture.Input))
                    throw new InvalidDataException("Stock file compression did not round trip.");
            }

            StockDecompress();
            VerifyDecoded(fixture.Input);
            ManagedDecompress();
            VerifyDecoded(fixture.Input);
        }
        catch
        {
            Cleanup();
            throw;
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

    [Benchmark(Baseline = true), BenchmarkCategory("FileDecompress")]
    public long StockDecompress() => Decompress(stock: true);

    [Benchmark, BenchmarkCategory("FileDecompress")]
    public long ManagedDecompress() => Decompress(stock: false);

    private long Compress(bool stock)
    {
        using var input = File.OpenRead(_input);
        using var output = File.Create(_output);
        using (Stream encoder = stock ? new StockGZip(output, CompressionLevel.Optimal, leaveOpen: true) :
            new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            input.CopyTo(encoder, BufferSize);
        }
        return output.Length;
    }

    private long Decompress(bool stock)
    {
        using var input = File.OpenRead(_encoded);
        using var output = File.Create(_decoded);
        using Stream decoder = stock ? new StockGZip(input, CompressionMode.Decompress, leaveOpen: true) :
            new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        decoder.CopyTo(output, BufferSize);
        return output.Length;
    }

    private void VerifyDecoded(ReadOnlySpan<byte> expected)
    {
        if (!expected.SequenceEqual(File.ReadAllBytes(_decoded)))
            throw new InvalidDataException("File decompression differs from the original bytes.");
    }
}
