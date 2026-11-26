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

## Quiet-VM results

On 2026-11-26, BenchmarkDotNet 0.16.0-preview.2 ran all 92 cases at
revision `36a7d25e3e5d` on Ubuntu 24.04.5 LTS with four
AMD EPYC 9V45 vCPUs.
The driver and workers used .NET 11.0.0 from SDK 11.0.100. Both streams
passed the pinned output checks before timing. Per-case coefficients of
variation had a median of 0.53% and a maximum of 4.37%.
`vmstat` recorded zero CPU steal in all 381 five-second samples.

All 46 paired comparisons completed within 1.05× stock time.
The highest ratio was 1.0234×.
Ratios divide the mean Lokad.GZip time by the mean stock time. Values
below 1.0× are faster. Throughput uses the synchronous managed times and
the uncompressed input sizes.

| Workload | Compress | Compress async | Decompress | Decompress async | Managed compress MiB/s | Managed decompress MiB/s | Gzip bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| tiny-text | 0.982× | 1.003× | 0.391× | 0.414× | 30.6 | 347.6 | 102 |
| small-text-fast | 0.828× | 0.743× | 0.849× | 0.847× | 5,883.8 | 9,464.7 | 458 |
| text-optimal | 1.005× | 1.023× | 0.992× | 0.995× | 3,513.2 | 16,967.1 | 8,971 |
| mixed-optimal | 0.775× | 0.786× | 0.710× | 0.735× | 234.6 | 1,845.4 | 369,243 |
| large-mixed-optimal | 0.787× | 0.799× | 0.732× | 0.739× | 228.0 | 1,773.1 | 5,906,903 |
| mixed-fast | 1.021× | 1.018× | 0.834× | 0.834× | 547.1 | 1,734.5 | 385,869 |
| mixed-smallest | 0.878× | 0.879× | 0.560× | 0.558× | 69.5 | 2,350.2 | 91,076 |
| entropy-optimal | 0.604× | 0.632× | 0.971× | 0.964× | 113.5 | 26,439.6 | 1,048,917 |
| entropy-stored | 0.952× | 1.001× | 0.967× | 0.971× | 17,853.9 | 27,365.7 | 1,048,759 |
| mixed-flush | 0.779× | 0.800× | 1.003× | 0.991× | 111.4 | 716.3 | 24,054 |
| runs-optimal | 0.681× | 0.711× | 0.851× | 0.926× | 13,476.6 | 21,002.8 | 1,051 |
| large-mixed-optimal (file) | 0.877× | n/a | 0.979× | n/a | 119.0 | 86.4 | 5,906,903 |

| Operation | Stock bytes/op | Lokad.GZip bytes/op |
| --- | ---: | ---: |
| Compress in memory | 232 | 152–304 |
| Compress in memory, async | 304 | 224–376 |
| Decompress in memory | 304 | 280 |
| Decompress in memory, async | 376 | 352 |
| Compress a file | 4,856 | 4,928 |
| Decompress a file | 4,947 | 4,923 |

## Reproduce

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
