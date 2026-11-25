# Changelog

## Unreleased

- Adds a managed gzip stream for .NET 10 and .NET 11 with deterministic
  compression against the corresponding Microsoft Linux references.
- Covers the modern `GZipStream` API, including `ZLibCompressionOptions`,
  asynchronous I/O and concatenated gzip members.
