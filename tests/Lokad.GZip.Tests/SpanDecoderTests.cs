using System.Buffers;
using System.IO.Compression;

namespace Lokad.GZip.Tests;

public sealed class SpanDecoderTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 3)]
    [InlineData(64, 16)]
    public void ReadsOneMemberWithFreshSpans(int inputSize, int outputSize)
    {
        byte[] member = ReadMember();
        byte[] input = [.. member, .. member, 0xA5];
        int offered = Math.Min(inputSize, input.Length);
        int offset = 0;
        using var decoder = GZipStream.CreateSpanDecoder();
        using var decoded = new MemoryStream();

        while (true)
        {
            byte[] buffer = new byte[outputSize];
            OperationStatus status = decoder.Read(input.AsSpan(offset, offered - offset), buffer,
                out int consumed, out int written, isFinalInput: offered == input.Length);
            Assert.InRange(consumed, 0, offered - offset);
            Assert.InRange(written, 0, buffer.Length);
            offset += consumed;
            decoded.Write(buffer.AsSpan(0, written));

            if (status == OperationStatus.Done)
                break;
            if (status == OperationStatus.NeedMoreData)
            {
                Assert.Equal(offered, offset);
                Assert.True(offered < input.Length);
                offered = Math.Min(offered + inputSize, input.Length);
            }
            else
            {
                Assert.Equal(OperationStatus.DestinationTooSmall, status);
                Assert.Equal(buffer.Length, written);
            }
        }

        Assert.Equal(member.Length, offset);
        Assert.Equal("Lokad.GZip\n"u8.ToArray(), decoded.ToArray());
        Assert.Equal(OperationStatus.Done, decoder.Read(input.AsSpan(offset), [], out int extraInput, out int extraOutput));
        Assert.Equal(0, extraInput);
        Assert.Equal(0, extraOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidFinalInputReportsProgressAndEndsTheSession(bool corrupt)
    {
        byte[] member = ReadMember();
        if (corrupt)
            member[^8] ^= 0x80;
        int length = corrupt ? member.Length : member.Length - 1;
        byte[] output = new byte[64];
        using var decoder = GZipStream.CreateSpanDecoder();

        Assert.Equal(OperationStatus.InvalidData, decoder.Read(member.AsSpan(0, length), output,
            out int consumed, out int written, isFinalInput: true));
        Assert.Equal(length, consumed);
        Assert.Equal("Lokad.GZip\n"u8.ToArray(), output.AsSpan(0, written).ToArray());
        Assert.Equal(OperationStatus.InvalidData, decoder.Read(member, output, out consumed, out written));
        Assert.Equal(0, consumed);
        Assert.Equal(0, written);
    }

    [Fact]
    public void EmptySpansCanRequestInputOrOutput()
    {
        byte[] member = ReadMember();
        using var decoder = GZipStream.CreateSpanDecoder();
        Assert.Equal(OperationStatus.NeedMoreData, decoder.Read([], [], out int consumed, out int written));
        Assert.Equal(0, consumed);
        Assert.Equal(0, written);

        Assert.Equal(OperationStatus.DestinationTooSmall, decoder.Read(member, [], out consumed, out written));
        Assert.InRange(consumed, 0, member.Length);
        Assert.Equal(0, written);
        byte[] output = new byte[64];
        Assert.Equal(OperationStatus.Done, decoder.Read(member.AsSpan(consumed), output, out int rest, out written));
        Assert.Equal(member.Length, consumed + rest);
        Assert.Equal("Lokad.GZip\n"u8.ToArray(), output.AsSpan(0, written).ToArray());
    }

    [Fact]
    public async Task SpanDecoderRejectsOverlapAndUsesItsOwnLifetime()
    {
        byte[] buffer = ReadMember();
        using var decoder = GZipStream.CreateSpanDecoder();
        Assert.Throws<ArgumentException>(() => decoder.Read(buffer, buffer.AsSpan(1), out _, out _));
        Assert.False(decoder.CanRead);
        Assert.False(decoder.CanWrite);
        Assert.Throws<NotSupportedException>(() => decoder.BaseStream);
        Assert.Throws<NotSupportedException>(() => decoder.ReadByte());

        using var source = new MemoryStream(buffer);
        using var stream = new GZipStream(source, CompressionMode.Decompress);
        Assert.Throws<InvalidOperationException>(() => stream.Read([], [], out _, out _));

        await decoder.DisposeAsync();
        await decoder.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => decoder.Read([], [], out _, out _));
    }

    private static byte[] ReadMember()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Goldens", "probe.gz"));
}
