# Linux gzip references

Each fixture contains the complete bytes emitted by
`System.IO.Compression.GZipStream`. The file names refer to the inputs,
options and write schedules in [ReferenceCases.cs](../ReferenceCases.cs).
Files with an `-async` suffix record a different result from the async
sequence. For the other cases, capture verifies that sync and async
produce identical bytes.

The captures use Microsoft's Linux x64 builds on WSL 2 Ubuntu 24.04.3 and
an Intel Core i7-14700KF with SSE4.2, AVX2 and PCLMULQDQ exposed to the guest.

| Target | Runtime | Compression assembly source revision | Linux x64 runtime archive SHA-512 |
| --- | --- | --- | --- |
| `net10.0` | 10.0.15 | `8e0f8ed2312954727f8919d7d1c2f78b483b4ee7` | `a90177d75bc891f71aa814dde3bcf76a356f5a506824669f1891754457954a3d13b72f3ca04c98fa0fe588514a5a781f8a87b17930ee802105c14f9edb28318a` |
| `net11.0` | 11.0.0 | `297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f` | `d0846e7ad4a6b07fe3fb7c033a381e6938ec16afbe0b7f7454ff2f9b58247cbbc35758cc785af424385c4572c801060cbd656a822ef624f4907d3cc10a16a080` |

On .NET 10, flushing an untouched stream synchronously produces zero
bytes. Flushing it asynchronously before disposal produces 15 bytes with
the gzip header and a non-final empty stored block. On .NET 11 both
sequences produce a complete 25-byte member.

Regenerate into fresh directories with the pinned SDK and runtimes from
the repository root on Linux x64. Compare the listed hashes and the set
of file names with the committed manifest and fixtures for each target.
Use the `--extended` option to include every compression level and
strategy, window settings and additional buffer boundaries.

```sh
dotnet build verification/Lokad.GZip.Reference -c Release
dotnet exec --fx-version 10.0.15 verification/Lokad.GZip.Reference/bin/Release/net10.0/Lokad.GZip.Reference.dll capture /tmp/gzip-reference-10
dotnet exec --fx-version 11.0.0 verification/Lokad.GZip.Reference/bin/Release/net11.0/Lokad.GZip.Reference.dll capture /tmp/gzip-reference-11
```

Run the `probe` subcommand on either operating system to print a minimal
comparison of the headers and empty-stream behaviors.
[.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
[.NET 11 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json)

Use `capture-benchmarks` in place of `capture` on the .NET 11 target to
regenerate the `net11.0.benchmarks.sha256` manifest used by benchmark smoke checks. The
[catalog](../BenchmarkWorkloads.cs) defines their payload and write schedule.
