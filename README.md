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

Decompression supports concatenated gzip members. On .NET 11, reading to
the end or copying from a seekable stream leaves the underlying position
at the start of trailing non-gzip data. With a fragmented seekable source,
the stock .NET 11 stream's position after `CopyTo` can depend on how the
source chunks its input. Lokad.GZip preserves the gzip boundary in that
case too.

## Caller-provided spans

The span API supports incremental processing with caller buffers.
Given compressed bytes and a writable destination span, create a decoder
and pass both spans to `Read`.

```csharp
using System.Buffers;
using GZipStream = Lokad.GZip.GZipStream;

using var decoder = GZipStream.CreateSpanDecoder();
OperationStatus status = decoder.Read(compressed, destination, out int consumed, out int written,
    isFinalInput: true);
```

Advance the input by `consumed` and the output by `written`. On
`DestinationTooSmall`, resume with fresh output space and the remaining
input. On `NeedMoreData`, supply the next input fragment. Set
`isFinalInput` on the final input fragment and its continuations. `Done` marks a
validated member and leaves following bytes in the input span.

For compression, use `CreateSpanEncoder` and `Write` with both spans.
Resume `Write` with fresh output space until it returns `Done`. Pass
empty input to drain any pending output once all the input is consumed.
Use `Flush` for a sync-flush boundary and `Finish` to emit the trailer.
Resume each of those operations with fresh output space until `Done`.
Input and output ranges must be disjoint. An untouched .NET 10 encoder
produces zero bytes, matching the pinned empty-stream behavior.

Run `dotnet test Lokad.GZip.slnx -c Release` with the SDK specified in
`global.json` and both runtimes installed. See the [Linux reference
fixtures](verification/goldens/README.md) for pinned versions and
regeneration commands, and [benchmarks](docs/BENCHMARKS.md) for the workload
catalog and comparison commands.
