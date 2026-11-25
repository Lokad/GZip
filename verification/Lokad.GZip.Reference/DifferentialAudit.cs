using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using ManagedGZip = Lokad.GZip.GZipStream;

namespace Lokad.GZip.Verification;

internal static class DifferentialAudit
{
    private static readonly int[] InputFragments = [1, 7, 8, 9, 31, 257, 8191, 65536];
    private static readonly int[] OutputSizes = [1, 7, 257, 8191, 8192, 32767, 32768, 32769, 65536];

    internal static async Task CaptureAsync(string manifest)
    {
        var entries = new List<string>();
        foreach (AuditCase item in AuditCases.Schedules().Concat(AuditCases.Payloads()))
        {
            (string digest, _) = await EncodeAsync(item, ReferenceCases.CreateStockStream);
            entries.Add($"{digest}  {item.Fixture.Name}");
        }
        string path = Path.GetFullPath(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, entries);
        Console.WriteLine($"Captured {entries.Count} stock Linux cases in {path}.");
    }

    internal static async Task VerifyAsync(string manifest)
    {
        Dictionary<string, string> expected;
        using (StreamReader reader = File.OpenText(manifest))
            expected = ReferenceDigests.Read(reader, manifest);

        int schedules = 0;
        int payloads = 0;
        foreach (AuditCase item in AuditCases.Schedules().Concat(AuditCases.Payloads()))
        {
            string name = item.Fixture.Name;
            if (!expected.Remove(name, out string? reference))
                throw new InvalidDataException($"Linux audit entry {name} is missing.");

            (string digest, byte[] member) = await EncodeAsync(item, CreateManagedStream);
            if (digest != reference)
                throw new InvalidDataException($"{name} differs from its pinned Linux result. Expected {reference}, got {digest}.");

            if (item.RecordWriteLengths)
                schedules++;
            else
                await CheckDecompressionAsync(item.Fixture, member, ++payloads);
        }
        if (expected.Count > 0)
            throw new InvalidDataException($"Unexpected Linux audit entry {expected.Keys.First()}.");

        Console.WriteLine($"Matched {schedules} short schedules and {payloads} seeded payloads to the pinned Linux manifest.");
        Console.WriteLine($"Verified {payloads} fragmented decompression cases.");
    }

    private static Stream CreateManagedStream(Stream output, ReferenceCase fixture)
        => new ManagedGZip(output, ReferenceCases.GetOptions(fixture), leaveOpen: true);

    private static async Task<(string Digest, byte[] Member)> EncodeAsync(
        AuditCase item, Func<Stream, ReferenceCase, Stream> createStream)
    {
        using var output = new MemoryStream();
        Stream stream = createStream(output, item.Fixture);
        byte[] lengths = item.RecordWriteLengths ? new byte[item.Operations.Count * sizeof(long)] : [];
        int position = 0;
        try
        {
            foreach (AuditOperation operation in item.Operations)
            {
                ReadOnlyMemory<byte> input = item.Fixture.Input.AsMemory(operation.Offset, operation.Count);
                switch (operation.Kind)
                {
                    case AuditOperationKind.Write:
                        stream.Write(input.Span);
                        break;
                    case AuditOperationKind.WriteAsync:
                        await stream.WriteAsync(input);
                        break;
                    case AuditOperationKind.Flush:
                        stream.Flush();
                        break;
                    case AuditOperationKind.FlushAsync:
                        await stream.FlushAsync();
                        break;
                    case AuditOperationKind.WriteByte:
                        stream.WriteByte(0xE7);
                        break;
                }
                if (item.RecordWriteLengths)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(lengths.AsSpan(position), output.Length);
                    position += sizeof(long);
                }
            }
        }
        finally
        {
            if (item.DisposeAsync)
                await stream.DisposeAsync();
            else
                stream.Dispose();
        }

        byte[] member = output.ToArray();
        if (lengths.Length == 0)
            return (Convert.ToHexStringLower(SHA256.HashData(member)), member);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(lengths);
        hash.AppendData(member);
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), member);
    }

    private static async Task CheckDecompressionAsync(ReferenceCase fixture, byte[] member, int index)
    {
        byte[] input = fixture.Input;
        if (index % 19 == 0 && input.Length > 0)
        {
            member = [.. member, .. member];
            input = [.. input, .. input];
        }

        int fragment = InputFragments[index % InputFragments.Length];
        int length = OutputSizes[index % OutputSizes.Length];
        if (fragment == 1 && member.Length > 8192)
            fragment = 31;
        if (length < 7 && input.Length > 8192)
            length = 31;

        using var source = new FragmentedMemoryStream(member, fragment);
        using var decoder = new ManagedGZip(source, CompressionMode.Decompress, leaveOpen: true);
        byte[] buffer = new byte[length];
        bool async = index % 2 == 0;
        int decoded = 0;
        while (true)
        {
            int count = async ? await decoder.ReadAsync(buffer.AsMemory()) : decoder.Read(buffer.AsSpan());
            if (count == 0)
                break;
            if (count > input.Length - decoded || !buffer.AsSpan(0, count).SequenceEqual(input.AsSpan(decoded, count)))
                throw new InvalidDataException($"{fixture.Name} differs at decoded offset {decoded} with input fragments of {fragment} bytes.");
            decoded += count;
        }
        if (decoded != input.Length || source.Position != source.Length)
            throw new InvalidDataException($"{fixture.Name} ended at input {source.Position}/{source.Length} and output {decoded}/{input.Length}.");
    }

    private sealed class FragmentedMemoryStream(byte[] data, int fragment) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(fragment, count));

        public override int Read(Span<byte> buffer)
            => base.Read(buffer[..Math.Min(fragment, buffer.Length)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(fragment, buffer.Length)], cancellationToken);
    }
}
