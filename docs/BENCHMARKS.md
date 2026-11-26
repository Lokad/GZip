# Benchmarks

Both streams use the same inputs and write schedules, verified against
pinned Linux gzip bytes. File timings include opening files and disposing
streams. [Workloads](../verification/BenchmarkWorkloads.cs)

## Quiet-VM results

BenchmarkDotNet 0.16.0-preview.2 ran 96 stream cases at `d5baa242a4c8`
on 2026-11-26 with .NET 11.0.0, Ubuntu 24.04.5 LTS, and four AMD EPYC
9V45 vCPUs. The full run passed all 48 comparisons at 1.05× and all 24
decode comparisons at 1.00×.

Ratios use mean Lokad.GZip time divided by stock `GZipStream` time.
Lower is faster. The main table gives synchronous throughput in
uncompressed MiB/s.

| Workload | Compress | Compress async | Decompress | Decompress async | Managed compress MiB/s | Managed decompress MiB/s | Gzip bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| tiny-text | 1.003× | 1.025× | 0.393× | 0.420× | 28.8 | 349.2 | 102 |
| small-text-fast | 0.789× | 0.768× | 0.850× | 0.836× | 6,028.8 | 9,657.9 | 458 |
| text-optimal | 1.023× | 0.979× | 0.964× | 0.980× | 3,429.6 | 17,168.0 | 8,971 |
| mixed-optimal | 0.806× | 0.772× | 0.709× | 0.701× | 226.1 | 1,850.1 | 369,243 |
| large-mixed-optimal | 0.784× | 0.798× | 0.715× | 0.717× | 228.4 | 1,798.3 | 5,906,903 |
| mixed-fast | 0.931× | 0.964× | 0.831× | 0.832× | 586.5 | 1,734.7 | 385,869 |
| mixed-smallest | 0.888× | 0.872× | 0.545× | 0.544× | 70.1 | 2,407.6 | 91,076 |
| entropy-optimal | 0.613× | 0.622× | 0.956× | 0.972× | 115.1 | 27,008.8 | 1,048,917 |
| entropy-stored | 1.003× | 0.974× | 0.984× | 0.985× | 17,247.2 | 27,641.5 | 1,048,759 |
| mixed-flush | 0.796× | 0.806× | 0.983× | 0.985× | 108.6 | 732.1 | 24,054 |
| runs-optimal | 0.701× | 0.705× | 0.849× | 0.857× | 13,057.6 | 21,058.9 | 1,051 |
| large-mixed-optimal (file) | 0.868× | 0.793× | 0.983× | 0.956× | 119.7 | 85.4 | 5,906,903 |

| Async file operation | Stock mean (ms) | Lokad.GZip mean (ms) | Stock MiB/s | Lokad.GZip MiB/s |
| --- | ---: | ---: | ---: | ---: |
| Compress | 207.157 | 164.337 | 77.2 | 97.4 |
| Decompress | 198.892 | 190.155 | 80.4 | 84.1 |

| Operation | Stock bytes/op | Lokad.GZip bytes/op |
| --- | ---: | ---: |
| Compress in memory | 232 | 160–312 |
| Compress in memory, async | 304 | 232–384 |
| Decompress in memory | 304 | 288 |
| Decompress in memory, async | 376 | 360 |
| Compress a file | 4,856 | 4,936 |
| Compress a file, async | 123,355 | 237,338 |
| Decompress a file | 4,947 | 4,931 |
| Decompress a file, async | 52,720 | 114,667 |

### Independent repeats

Two async file repeats exceeded 1.05× during sustained I/O wait. Stock's
coefficient of variation ranged from 20.0% to 27.3%. The fixed-count
repeat used 35 warmup and 35 measurement iterations per method.

| Workload | Operation | Full run | First repeat | Second repeat | Fixed-count repeat |
| --- | --- | ---: | ---: | ---: | ---: |
| large-mixed-optimal (file) | Decompress async | 0.9561× | 1.3038× | 1.0899× | 0.7481× |

Alternating stock and managed runs measured 0.943–0.991× on ext4 and
1.465–1.628× on tmpfs.

## Caller-buffer results

The direct span API and managed stream adapter use preallocated output
buffers and the same compression schedule.

| Workload | Operation | Stream mean (µs) | Span mean (µs) | Span / stream | Stream bytes/op | Span bytes/op |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| tiny-text | Compress | 2.952 | 3.036 | 1.0283× | 312 | 336 |
| tiny-text | Decompress | 0.276 | 0.222 | 0.8039× | 288 | 288 |
| text-optimal | Compress | 286.891 | 287.075 | 1.0006× | 312 | 336 |
| text-optimal | Decompress | 58.818 | 44.393 | 0.7547× | 288 | 288 |
| mixed-flush | Compress | 583.237 | 557.835 | 0.9564× | 312 | 336 |
| mixed-flush | Decompress | 84.325 | 81.520 | 0.9667× | 288 | 288 |
| entropy-stored | Compress | 57.920 | 48.411 | 0.8358× | 160 | 184 |
| entropy-stored | Decompress | 35.909 | 29.849 | 0.8312× | 288 | 288 |

## Reproduce

```sh
dotnet run --project benchmarks/Lokad.GZip.Benchmarks -c Release -f net11.0 -- --smoke
dotnet run --project benchmarks/Lokad.GZip.Benchmarks -c Release -f net11.0 -- --filter '*'
```

On a quiet Linux VM, the runner records the revision, environment, smoke
checks, and raw .NET 11.0.0 results under `artifacts/benchmarks/`.

```sh
bash benchmarks/run-reference-linux.sh
bash benchmarks/run-reference-linux.sh artifacts/benchmarks/caller-buffers --span --filter '*'
```
