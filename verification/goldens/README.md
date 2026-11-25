# Linux gzip references

The binary fixtures contain the complete bytes emitted by
`System.IO.Compression.GZipStream` for the core cases. The SHA-256
manifests cover the extended cases, including every compression level and
strategy, window settings, and buffer boundaries. The file names refer to
the inputs, options and write schedules in [ReferenceCases.cs](../ReferenceCases.cs).
An `-async` entry records a different result for the async schedule.
The tests check both schedules against the pinned Linux manifests on every OS.

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

Run the extended capture from the repository root on Linux x64 to check
the committed manifests. The capture tool checks the runtime version and
compression assembly source commit. Use fresh output directories for
each run.

```sh
dotnet build verification/Lokad.GZip.Reference -c Release
dotnet exec --fx-version 10.0.15 verification/Lokad.GZip.Reference/bin/Release/net10.0/Lokad.GZip.Reference.dll capture /tmp/gzip-reference-10 --extended \
  | awk '/^[0-9a-f]+  /' | diff -u verification/goldens/net10.0.sha256 -
dotnet exec --fx-version 11.0.0 verification/Lokad.GZip.Reference/bin/Release/net11.0/Lokad.GZip.Reference.dll capture /tmp/gzip-reference-11 --extended \
  | awk '/^[0-9a-f]+  /' | diff -u verification/goldens/net11.0.sha256 -
```

Use `capture <directory>` to regenerate the core binary fixtures.

Run the `probe` subcommand on either operating system to print a minimal
comparison of the headers and empty-stream behaviors.
[.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
[.NET 11 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json)

Use `capture-benchmarks` in place of `capture` on the .NET 11 target to
regenerate the `net11.0.benchmarks.sha256` manifest used by benchmark smoke checks. The
[catalog](../BenchmarkWorkloads.cs) defines their payload and write schedule.

## Differential audit

The audit covers 6,250 short operation schedules and 1,536 seeded payloads
per target. The short cases compare complete gzip output and the number
of bytes visible after each write or flush. The seeded cases mix sync
and async operations, vary the compression settings and check decoded
bytes through fragmented reads, including concatenated members.

From a clean checkout on Linux x64, capture the stock .NET 11 results
with the pinned runtime.

```sh
dotnet restore verification/Lokad.GZip.Reference/Lokad.GZip.Reference.csproj --locked-mode
dotnet build verification/Lokad.GZip.Reference -c Release --no-restore
dotnet exec --fx-version 11.0.0 verification/Lokad.GZip.Reference/bin/Release/net11.0/Lokad.GZip.Reference.dll capture-audit artifacts/audit/net11.0.sha256
```

Keep that manifest when moving to Windows or using a second checkout.
Build there with the same restore and build commands, then run the audit
against the captured Linux file.

```sh
dotnet exec --fx-version 11.0.0 verification/Lokad.GZip.Reference/bin/Release/net11.0/Lokad.GZip.Reference.dll audit artifacts/audit/net11.0.sha256
```

For .NET 10, substitute `10.0.15` and `net10.0` in the capture and audit
commands. Set `DOTNET_EnableHWIntrinsic=0` for a scalar check against the
same manifest. The audit reports the first differing case by name.
