using System.Globalization;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Lokad.GZip.Verification;
using StockGZip = System.IO.Compression.GZipStream;

namespace Lokad.GZip.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Config(typeof(ThroughputConfig))]
public class GZipBenchmarks
{
    private byte[] _input = [];
    private CompressionLevel _level;
    private int _bufferSize;
    private int _flushEvery;
    private MemoryStream _encoded = new();
    private MemoryStream _output = new();

    [ParamsSource(nameof(Workloads))]
    public string Workload { get; set; } = string.Empty;

    public static IEnumerable<string> Workloads => BenchmarkWorkloads.All.Select(workload => workload.Name);

    [GlobalSetup]
    public void Prepare()
    {
        ReferenceCase fixture = BenchmarkWorkloads.All.Single(workload => workload.Name == Workload).Generate();
        byte[] compressed = BenchmarkVerification.Check(fixture);
        _input = fixture.Input;
        _level = fixture.Level;
        _bufferSize = fixture.ChunkSize;
        _flushEvery = fixture.FlushEvery;
        _encoded = new MemoryStream(compressed, writable: false);
        _output = new MemoryStream(Math.Max(compressed.Length, _input.Length));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _encoded.Dispose();
        _output.Dispose();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Compress")]
    public long StockCompress() => Compress(stock: true);

    [Benchmark, BenchmarkCategory("Compress")]
    public long ManagedCompress() => Compress(stock: false);

    [Benchmark(Baseline = true), BenchmarkCategory("CompressAsync")]
    public Task<long> StockCompressAsync() => CompressAsync(stock: true);

    [Benchmark, BenchmarkCategory("CompressAsync")]
    public Task<long> ManagedCompressAsync() => CompressAsync(stock: false);

    [Benchmark(Baseline = true), BenchmarkCategory("Decompress")]
    public long StockDecompress() => Decompress(stock: true);

    [Benchmark, BenchmarkCategory("Decompress")]
    public long ManagedDecompress() => Decompress(stock: false);

    [Benchmark(Baseline = true), BenchmarkCategory("DecompressAsync")]
    public Task<long> StockDecompressAsync() => DecompressAsync(stock: true);

    [Benchmark, BenchmarkCategory("DecompressAsync")]
    public Task<long> ManagedDecompressAsync() => DecompressAsync(stock: false);

    private long Compress(bool stock)
    {
        _output.SetLength(0);
        using (Stream encoder = OpenEncoder(stock))
        {
            int chunk = 0;
            for (int offset = 0; offset < _input.Length;)
            {
                int count = Math.Min(_bufferSize, _input.Length - offset);
                encoder.Write(_input.AsSpan(offset, count));
                offset += count;
                if (_flushEvery > 0 && ++chunk % _flushEvery == 0)
                    encoder.Flush();
            }
        }
        return _output.Length;
    }

    private async Task<long> CompressAsync(bool stock)
    {
        _output.SetLength(0);
        Stream encoder = OpenEncoder(stock);
        await using (encoder.ConfigureAwait(false))
        {
            int chunk = 0;
            for (int offset = 0; offset < _input.Length;)
            {
                int count = Math.Min(_bufferSize, _input.Length - offset);
                await encoder.WriteAsync(_input.AsMemory(offset, count)).ConfigureAwait(false);
                offset += count;
                if (_flushEvery > 0 && ++chunk % _flushEvery == 0)
                    await encoder.FlushAsync().ConfigureAwait(false);
            }
        }
        return _output.Length;
    }

    private long Decompress(bool stock)
    {
        _output.SetLength(0);
        _encoded.Position = 0;
        using Stream decoder = OpenDecoder(stock);
        decoder.CopyTo(_output, _bufferSize);
        return _output.Length;
    }

    private async Task<long> DecompressAsync(bool stock)
    {
        _output.SetLength(0);
        _encoded.Position = 0;
        Stream decoder = OpenDecoder(stock);
        await using (decoder.ConfigureAwait(false))
            await decoder.CopyToAsync(_output, _bufferSize).ConfigureAwait(false);
        return _output.Length;
    }

    private Stream OpenEncoder(bool stock)
        => stock ? new StockGZip(_output, _level, leaveOpen: true) : new GZipStream(_output, _level, leaveOpen: true);

    private Stream OpenDecoder(bool stock)
        => stock ? new StockGZip(_encoded, CompressionMode.Decompress, leaveOpen: true) :
            new GZipStream(_encoded, CompressionMode.Decompress, leaveOpen: true);
}

internal sealed class ThroughputConfig : ManualConfig
{
    public ThroughputConfig() => AddColumn(new ThroughputColumn());

    private sealed class ThroughputColumn : IColumn
    {
        public string Id => nameof(ThroughputColumn);
        public string ColumnName => "MiB/s";
        public string Legend => "Uncompressed mebibytes processed per second.";
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Statistics;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Dimensionless;
        public bool IsAvailable(Summary summary) => true;
        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
            => GetValue(summary, benchmarkCase, summary.Style);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        {
            double? mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
            if (mean is not > 0)
                return "NA";

            object? parameter = benchmarkCase.Parameters.Items.Single(item => item.Name == nameof(GZipBenchmarks.Workload)).Value;
            if (parameter is not string name)
                return "NA";
            int size = BenchmarkWorkloads.All.Single(workload => workload.Name == name).Size;
            double throughput = size * (1_000_000_000.0 / (1024 * 1024)) / mean.Value;
            return throughput.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
