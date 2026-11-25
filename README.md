# Lokad.GZip

Lokad.GZip provides deterministic gzip compression and decompression in
managed C# for .NET 10 and .NET 11. It emits the gzip bytes of the pinned
Microsoft Linux runtime for each target, including on Windows. The
compression options, input bytes and sequence of writes and flushes
determine the output.

Use an alias to keep existing `GZipStream` calls and BCL compression options.

```csharp
using System.IO.Compression;
using GZipStream = Lokad.GZip.GZipStream;

await using var input = File.OpenRead("input.json");
await using var output = File.Create("input.json.gz");
await using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
await input.CopyToAsync(gzip);
```

Run `dotnet test Lokad.GZip.slnx -c Release` with the SDK specified in
`global.json` and both runtimes installed. See the [Linux reference
fixtures](verification/goldens/README.md) for pinned versions and
regeneration commands, and [benchmarks](docs/BENCHMARKS.md) for the workload
catalog and comparison commands.
