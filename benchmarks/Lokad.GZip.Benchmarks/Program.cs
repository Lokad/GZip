using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Lokad.GZip.Benchmarks;
using Lokad.GZip.Verification;

if (args is ["--smoke"])
{
    BenchmarkWorkload fileWorkload = BenchmarkWorkloads.All.Single(workload => workload.Name == FileGZipBenchmarks.WorkloadName);
    foreach (BenchmarkWorkload workload in BenchmarkWorkloads.All)
    {
        ReferenceCase fixture = workload.Generate();
        byte[] encoded = BenchmarkVerification.Check(fixture);
        Console.WriteLine($"{workload.Name} {workload.Size:N0} bytes in, {encoded.Length:N0} gzip bytes");
        if (workload == fileWorkload)
            new FileGZipBenchmarks().Verify(fixture, encoded);
        if (SpanGZipBenchmarks.Workloads.Contains(workload.Name))
            SpanGZipBenchmarks.Verify(fixture, encoded);
    }
    Console.WriteLine("File-stream compression and decompression verified.");
    Console.WriteLine("Caller-buffer compression and decompression verified.");
    return 0;
}

bool spans = args.Contains("--span", StringComparer.Ordinal);
if (spans)
    args = args.Where(arg => arg != "--span").ToArray();
Type[] benchmarks = spans ? [typeof(SpanGZipBenchmarks)] : [typeof(GZipBenchmarks), typeof(FileGZipBenchmarks)];
Summary[] summaries = BenchmarkSwitcher.FromTypes(benchmarks).Run(args).ToArray();
if (args.Any(arg => arg is "--list" or "--help" or "-h" or "--version"))
    return 0;

return summaries.Length == 0 || summaries.Any(summary =>
    summary.HasCriticalValidationErrors || summary.Reports.Length == 0 || summary.Reports.Any(report => !report.Success)) ? 1 : 0;
