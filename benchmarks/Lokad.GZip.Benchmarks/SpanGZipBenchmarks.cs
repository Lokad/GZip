using System.Buffers;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Lokad.GZip.Verification;

namespace Lokad.GZip.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Config(typeof(ThroughputConfig))]
public class SpanGZipBenchmarks
{
    private ReferenceCase _fixture = null!;
    private byte[] _reference = [];
    private byte[] _compressed = [];
    private byte[] _decoded = [];
    private MemoryStream _compressedOutput = null!;
    private MemoryStream _decodedOutput = null!;
    private MemoryStream _encodedInput = null!;

    [ParamsSource(nameof(Workloads))]
    public string Workload { get; set; } = string.Empty;

    public static IReadOnlyList<string> Workloads { get; } =
        ["tiny-text", "text-optimal", "mixed-flush", "entropy-stored"];

    [GlobalSetup]
    public void Prepare()
    {
        ReferenceCase fixture = BenchmarkWorkloads.All.Single(workload => workload.Name == Workload).Generate();
        Prepare(fixture, BenchmarkVerification.Check(fixture));
    }

    private void Prepare(ReferenceCase fixture, byte[] reference)
    {
        _fixture = fixture;
        _reference = reference;
        _compressed = new byte[reference.Length];
        _decoded = new byte[fixture.Input.Length];
        _compressedOutput = new MemoryStream(_compressed, 0, _compressed.Length, writable: true, publiclyVisible: true);
        _decodedOutput = new MemoryStream(_decoded, 0, _decoded.Length, writable: true, publiclyVisible: true);
        _encodedInput = new MemoryStream(reference, writable: false);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _compressedOutput.Dispose();
        _decodedOutput.Dispose();
        _encodedInput.Dispose();
    }

    internal static void Verify(ReferenceCase fixture, byte[] reference)
    {
        var benchmark = new SpanGZipBenchmarks();
        benchmark.Prepare(fixture, reference);
        try
        {
            Check(benchmark.StreamCompress, reference, benchmark._compressed);
            Check(benchmark.SpanCompress, reference, benchmark._compressed);
            Check(benchmark.StreamDecompress, fixture.Input, benchmark._decoded);
            Check(benchmark.SpanDecompress, fixture.Input, benchmark._decoded);
        }
        finally
        {
            benchmark.Cleanup();
        }

        void Check(Func<int> run, byte[] expected, byte[] actual)
        {
            for (int index = 0; index < actual.Length; index++)
                actual[index] = (byte)~expected[index];
            if (run() != expected.Length || !expected.AsSpan().SequenceEqual(actual))
                throw new InvalidDataException($"Caller-buffer bytes from {run.Method.Name} differ for {fixture.Name}.");
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Compress")]
    public int StreamCompress()
    {
        _compressedOutput.SetLength(0);
        using (var encoder = new GZipStream(_compressedOutput, _fixture.Level, leaveOpen: true))
        {
            int chunk = 0;
            for (int offset = 0; offset < _fixture.Input.Length;)
            {
                int count = Math.Min(_fixture.ChunkSize, _fixture.Input.Length - offset);
                encoder.Write(_fixture.Input.AsSpan(offset, count));
                offset += count;
                if (_fixture.FlushEvery > 0 && ++chunk % _fixture.FlushEvery == 0)
                    encoder.Flush();
            }
        }
        return (int)_compressedOutput.Length;
    }

    [Benchmark, BenchmarkCategory("Compress")]
    public int SpanCompress()
    {
        using var encoder = GZipStream.CreateSpanEncoder(_fixture.Level);
        int chunk = 0;
        int outputOffset = 0;
        for (int offset = 0; offset < _fixture.Input.Length;)
        {
            int count = Math.Min(_fixture.ChunkSize, _fixture.Input.Length - offset);
            OperationStatus status = encoder.Write(_fixture.Input.AsSpan(offset, count), _compressed.AsSpan(outputOffset),
                out int consumed, out int written);
            if (status != OperationStatus.Done || consumed != count)
                throw new InvalidDataException("The span encoder could not accept the full input chunk.");
            offset += consumed;
            outputOffset += written;
            if (_fixture.FlushEvery > 0 && ++chunk % _fixture.FlushEvery == 0)
            {
                if (encoder.Flush(_compressed.AsSpan(outputOffset), out written) != OperationStatus.Done)
                    throw new InvalidDataException("The span encoder could not flush.");
                outputOffset += written;
            }
        }
        if (encoder.Finish(_compressed.AsSpan(outputOffset), out int finalBytes) != OperationStatus.Done)
            throw new InvalidDataException("The span encoder could not finish.");
        return outputOffset + finalBytes;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Decompress")]
    public int StreamDecompress()
    {
        _encodedInput.Position = 0;
        _decodedOutput.SetLength(0);
        using var decoder = new GZipStream(_encodedInput, CompressionMode.Decompress, leaveOpen: true);
        decoder.CopyTo(_decodedOutput, _fixture.ChunkSize);
        return (int)_decodedOutput.Length;
    }

    [Benchmark, BenchmarkCategory("Decompress")]
    public int SpanDecompress()
    {
        using var decoder = GZipStream.CreateSpanDecoder();
        OperationStatus status = decoder.Read(_reference, _decoded, out int consumed, out int written, isFinalInput: true);
        if (status != OperationStatus.Done || consumed != _reference.Length)
            throw new InvalidDataException("The span decoder could not finish the member.");
        return written;
    }
}
