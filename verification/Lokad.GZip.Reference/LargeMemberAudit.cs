using System.Buffers.Binary;
using System.IO.Compression;
using ManagedGZip = Lokad.GZip.GZipStream;
using StockGZip = System.IO.Compression.GZipStream;

namespace Lokad.GZip.Verification;

internal static class LargeMemberAudit
{
    private const int ChunkSize = 1 << 20;
    private const int ChunkCount = 1 << 12;
    private static ReadOnlySpan<byte> Suffix => "Lokad.GZip ISIZE!"u8;

    internal static void Capture(string path)
    {
        byte[] buffer = new byte[ChunkSize];
        using var output = new MemoryStream();
        using (var encoder = new StockGZip(output, CompressionLevel.Fastest, leaveOpen: true))
            WriteMember(encoder, buffer);

        byte[] member = output.ToArray();
        CheckSize(member);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, member);
        Console.WriteLine($"Captured {member.Length} stock Linux bytes in {path}.");
    }

    internal static void Verify(string path)
    {
        byte[] reference = File.ReadAllBytes(path);
        CheckSize(reference);
        byte[] buffer = new byte[ChunkSize];
        using var output = new MemoryStream();
        using (var encoder = new ManagedGZip(output, CompressionLevel.Fastest, leaveOpen: true))
            WriteMember(encoder, buffer);

        if (!output.GetBuffer().AsSpan(0, (int)output.Length).SequenceEqual(reference))
            throw new InvalidDataException("The large member differs from the pinned Linux bytes.");

        output.Position = 0;
        using var decoder = new ManagedGZip(output, CompressionMode.Decompress);
        for (int i = 0; i < ChunkCount; i++)
        {
            decoder.ReadExactly(buffer);
            if (buffer.AsSpan().ContainsAnyExcept((byte)0))
                throw new InvalidDataException($"Large member differs in decoded chunk {i}.");
        }
        Span<byte> suffix = buffer.AsSpan(0, Suffix.Length);
        decoder.ReadExactly(suffix);
        if (!suffix.SequenceEqual(Suffix) || decoder.ReadByte() != -1)
            throw new InvalidDataException("The large member has an unexpected suffix.");

        long size = (long)ChunkCount * ChunkSize + Suffix.Length;
        Console.WriteLine($"Matched {reference.Length} pinned Linux bytes and decoded {size} bytes. ISIZE is {Suffix.Length}.");
    }

    private static void WriteMember(Stream encoder, byte[] buffer)
    {
        for (int i = 0; i < ChunkCount; i++)
            encoder.Write(buffer.AsSpan());
        encoder.Write(Suffix);
    }

    private static void CheckSize(ReadOnlySpan<byte> member)
    {
        if (member.Length < sizeof(uint) || BinaryPrimitives.ReadUInt32LittleEndian(member[^sizeof(uint)..]) != (uint)Suffix.Length)
            throw new InvalidDataException($"The large member's ISIZE must be {Suffix.Length}.");
    }
}
