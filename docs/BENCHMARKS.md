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

On 2026-11-25, BenchmarkDotNet 0.16.0-preview.2 ran all 92 cases at
revision `b33d6f9682a6` on Ubuntu 24.04.5 with four AMD EPYC 9V45 vCPUs.
The driver and workers used .NET 11.0.0 from SDK 11.0.100. Both stock and
managed gzip passed the pinned output checks before timing. The median
per-case coefficient of variation was 1.0%, and the largest was 3.5%.
`vmstat` reported zero CPU steal in all 132 fifteen-second samples.

Ratios divide mean Lokad.GZip time by mean stock time. Values below
1.0× are faster. Throughput columns use the synchronous managed results
and the uncompressed input size.

| Workload | Compress | Compress async | Decompress | Decompress async | Managed compress MiB/s | Managed decompress MiB/s | Gzip bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| tiny-text | 1.023× | 1.024× | 0.671× | 0.623× | 28.2 | 194.1 | 102 |
| small-text-fast | 1.126× | 1.001× | 1.833× | 1.748× | 4,241.5 | 4,378.9 | 458 |
| text-optimal | 1.575× | 1.536× | 2.242× | 2.196× | 2,131.3 | 7,078.9 | 8,971 |
| mixed-optimal | 1.148× | 1.114× | 1.975× | 2.036× | 153.8 | 638.9 | 369,243 |
| large-mixed-optimal | 1.170× | 1.079× | 1.999× | 1.883× | 151.1 | 613.2 | 5,906,903 |
| mixed-fast | 1.117× | 1.102× | 2.037× | 2.096× | 476.8 | 685.9 | 385,869 |
| mixed-smallest | 1.078× | 1.068× | 1.651× | 2.014× | 56.7 | 780.7 | 91,076 |
| entropy-optimal | 1.171× | 1.114× | 2.655× | 2.665× | 60.2 | 9,536.5 | 1,048,917 |
| entropy-stored | 1.708× | 1.696× | 2.598× | 2.769× | 9,508.2 | 9,753.8 | 1,048,759 |
| mixed-flush | 1.173× | 1.082× | 1.498× | 1.507× | 73.1 | 456.6 | 24,054 |
| runs-optimal | 2.284× | 2.088× | 1.834× | 1.895× | 3,984.4 | 9,229.7 | 1,051 |
| large-mixed-optimal (file) | 1.173× | n/a | 1.079× | n/a | 86.5 | 78.2 | 5,906,903 |

Most mixed-data compression took 1.07–1.17× stock time. The largest
compression gap was the 1 MiB zero-filled runs workload at 2.28×. The
largest decompression gaps were the two incompressible workloads at
2.60–2.77×, followed by text and mixed data. Tiny-message decompression
took 0.62–0.67× stock time.

| Operation | Stock bytes/op | Lokad.GZip bytes/op |
| --- | ---: | ---: |
| Compress in memory, `Fastest`, `Optimal`, or `SmallestSize` | 232–233 | 304 |
| Compress in memory, `Fastest`, `Optimal`, or `SmallestSize`, async | 304 | 376 |
| Compress in memory, `NoCompression` | 232 | 152 |
| Compress in memory, `NoCompression`, async | 304 | 224 |
| Decompress in memory | 304 | 280 |
| Decompress in memory, async | 376 | 352 |
| Compress a file | 4,856 | 4,947 |
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
