# Benchmarks

The BenchmarkDotNet suite compares the stock and managed streams on .NET 11
with the same OS, input and write schedule. It reports elapsed time,
managed allocation, and throughput in uncompressed MiB/s. Each operation
creates and disposes a gzip stream. The memory-stream buffers are reused
between invocations.

The [workload catalog](../verification/BenchmarkWorkloads.cs) includes tiny
messages, larger text and binary data, several compression levels and
buffer sizes, and periodic flushes. Smoke verification checks complete
gzip output against hashes captured on the pinned Linux .NET 11 runtime, checks
cross-decoding with the stock stream, and prints the compressed sizes.
Benchmark setup runs the same checks before timing.

The file-stream cases use the mixed payload and include opening and creating
files through `FileStream`. They exercise the host's ordinary filesystem
cache and have their own benchmark rows.

```sh
dotnet run --project benchmarks/Lokad.GZip.Benchmarks -c Release -f net11.0 -- --smoke
dotnet run --project benchmarks/Lokad.GZip.Benchmarks -c Release -f net11.0 -- --filter '*'
```

BenchmarkDotNet writes raw results under `BenchmarkDotNet.Artifacts/` on
the default settings. For a full run on a quiet Linux VM with a clean
release checkout, run the capture script. It records the revision, CPU,
runtime, host load, smoke checks and BenchmarkDotNet outputs together.

```sh
bash benchmarks/run-reference-linux.sh
```

The default capture directory is `artifacts/benchmarks/<revision>/`.
Pass a directory and BenchmarkDotNet arguments to run a selected workload.
