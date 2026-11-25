using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Lokad.GZip.Benchmarks;
using Lokad.GZip.Verification;

if (args is ["--smoke"])
{
    foreach (BenchmarkWorkload workload in BenchmarkWorkloads.All)
    {
        byte[] encoded = BenchmarkVerification.Check(workload.Generate());
        Console.WriteLine($"{workload.Name} {workload.Size:N0} bytes in, {encoded.Length:N0} gzip bytes");
    }
    var files = new FileGZipBenchmarks();
    files.Prepare();
    files.Cleanup();
    Console.WriteLine("File-stream compression and decompression verified.");
    return 0;
}

Summary[] summaries = BenchmarkSwitcher.FromAssembly(typeof(GZipBenchmarks).Assembly).Run(args).ToArray();
return !args.Contains("--list") && summaries.Any(summary =>
    summary.HasCriticalValidationErrors || summary.Reports.Any(report => !report.Success)) ? 1 : 0;
