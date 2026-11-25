using System.Buffers.Binary;

namespace Lokad.GZip;

internal static class GZipFormat
{
    internal const int HeaderSize = 10;
    internal const int TrailerSize = 8;

    internal static void WriteHeader(Span<byte> buffer, byte extraFlags)
    {
        ReadOnlySpan<byte> linuxHeader = [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 3];
        linuxHeader.CopyTo(buffer);
        buffer[8] = extraFlags;
    }

    internal static void WriteTrailer(Span<byte> buffer, uint crc, uint size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, ~crc);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], size);
    }
}
