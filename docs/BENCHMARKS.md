# Benchmarks

The BenchmarkDotNet suite compares the stock and managed streams on .NET 11
with the same OS, input and write schedule. It reports elapsed time,
managed allocation, and throughput in uncompressed MiB/s. Each operation
creates and disposes a gzip stream. The memory-stream buffers are reused
between invocations.

The [workload catalog](../verification/BenchmarkWorkloads.cs) ranges from
96-byte messages to 16 MiB of mixed binary and text data. It includes
several compression levels and buffer sizes, plus periodic flushes. Smoke
verification checks complete gzip output against hashes captured on the
pinned Linux .NET 11 runtime. Both implementations decode the same members
through sync and async calls with the configured copy buffer sizes. Smoke
verification prints the compressed sizes and benchmark setup repeats the
checks before timing.

The file-stream cases use the 16 MiB mixed payload and include opening and
creating files through `FileStream`. They exercise the host's ordinary
filesystem cache and have their own benchmark rows.

```sh
dotnet run --project benchmarks/Lokad.GZip.Benchmarks -c Release -f net11.0 -- --smoke
dotnet run --project benchmarks/Lokad.GZip.Benchmarks -c Release -f net11.0 -- --filter '*'
```

BenchmarkDotNet writes raw results under `BenchmarkDotNet.Artifacts/` on
the default settings. For a full run on a quiet Linux VM with a clean
release checkout, run the capture script. It uses .NET 11.0.0 for the
driver and benchmark processes and records the revision, CPU, runtime,
host load, smoke checks and BenchmarkDotNet outputs together.

```sh
bash benchmarks/run-reference-linux.sh
```

Each run creates a timestamped directory under
`artifacts/benchmarks/<revision>/` so repeated runs retain their own output.
Pass a directory and BenchmarkDotNet arguments to run a selected workload.
