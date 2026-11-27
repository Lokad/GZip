# Changelog

## 0.1.0 (unreleased)

- Adds a managed gzip stream for .NET 10 and .NET 11 with deterministic
  compression against the corresponding Microsoft Linux references.
- Covers the modern `GZipStream` API, including `ZLibCompressionOptions`,
  asynchronous I/O and concatenated gzip members.
- Adds incremental span encoding and decoding with caller-provided buffers.
- Matches stock retry behavior after a copy destination fails.
- Matches the disposed `BaseStream` behavior for each .NET target.
- Speeds up fixed-literal encoding and full-length non-overlapping match copies.
