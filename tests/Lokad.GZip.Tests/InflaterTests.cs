using System.Buffers.Binary;
using Lokad.GZip.Verification;
using StockGZip = System.IO.Compression.GZipStream;

namespace Lokad.GZip.Tests;

public sealed class InflaterTests
{
    [Theory]
    [InlineData("1B03", "Reserved DEFLATE length symbol.")]
    [InlineData("1B7D", "Reserved DEFLATE distance symbol.")]
    [InlineData("1BDDFF0F00", "DEFLATE distance exceeds the available history.")]
    public void RejectsInvalidMatchWithRoomForFastDecoding(string deflate, string message)
    {
        byte[] header = new byte[GZipFormat.HeaderSize];
        GZipFormat.WriteHeader(header, extraFlags: 0);
        byte[] member = [.. header, .. Convert.FromHexString(deflate), .. new byte[32]];
        byte[] output = new byte[DeflateCodes.MaximumLength];
        output.AsSpan().Fill(0xA5);
        using var inflater = new GZipInflater();
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => inflater.Decode(member, output, out _, out _));
        Assert.Equal(message, error.Message);
        Assert.All(output, value => Assert.Equal((byte)0xA5, value));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(258)]
    [InlineData(259)]
    public void DecodesMaximumLengthMatchAtOutputBoundary(int bufferSize)
    {
        byte[] expected = new byte[259];
        expected.AsSpan().Fill((byte)'A');
        byte[] header = new byte[GZipFormat.HeaderSize];
        GZipFormat.WriteHeader(header, extraFlags: 0);
        byte[] trailer = new byte[GZipFormat.TrailerSize];
        GZipFormat.WriteTrailer(trailer, Crc32.Update(Crc32.Initial, expected), (uint)expected.Length);
        // One literal and a fixed match of length 258 and distance one.
        byte[] member = [.. header, 0x73, 0x1c, 0x05, 0, .. trailer, 0xA5, .. new byte[32]];

        var (output, finished) = Decode(member, member.Length, bufferSize);
        Assert.True(finished);
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RejectsCodesFromAPreviousDistanceTree(byte length)
    {
        var tree = new InflateTree(2);
        tree.Build([1, 1], allowEmpty: true);
        Assert.Equal(1, ReadSymbol(tree, 0x5F));

        tree.Build([length, 0], allowEmpty: true);
        if (length == 1)
            Assert.Equal(0, ReadSymbol(tree, 0x5E));
        Assert.Throws<InvalidDataException>(() => ReadSymbol(tree, 0x5F));

        static int ReadSymbol(InflateTree tree, byte data)
        {
            var bits = new BitReader();
            ReadOnlySpan<byte> input = [data];
            Assert.True(tree.TryRead(ref bits, ref input, out int symbol));
            return symbol;
        }
    }

    [Fact]
    public void DecodesFullLengthNonOverlappingMatchesAcrossHistoryCommits()
    {
        const int length = DeflateCodes.MaximumLength;
        const int repeats = 256;
        byte[] pattern = ReferenceCases.RandomBytes(length);
        byte[] expected = new byte[length * repeats];
        using var bits = new DeflateBits();
        GZipFormat.WriteHeader(bits.ReserveAligned(GZipFormat.HeaderSize), extraFlags: 0);
        DeflateBlockWriter.StartFixed(bits, final: true);
        foreach (byte value in pattern)
            DeflateBlockWriter.WriteFixedLiteral(bits, value);
        for (int i = 0; i < repeats; i++)
        {
            pattern.CopyTo(expected.AsSpan(i * length, length));
            if (i > 0)
                DeflateBlockWriter.WriteFixedMatch(bits, length, distance: length);
        }
        DeflateBlockWriter.EndFixed(bits, final: true);
        GZipFormat.WriteTrailer(bits.ReserveAligned(GZipFormat.TrailerSize),
            Crc32.Update(Crc32.Initial, expected), (uint)expected.Length);
        byte[] encoded = new byte[bits.Pending];
        Span<byte> destination = encoded;
        bits.Drain(ref destination);

        var (decoded, finished) = Decode(encoded, inputSize: 512, outputSize: 32768);
        Assert.True(finished);
        Assert.Equal(expected, decoded);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(8, 7)]
    [InlineData(17, 1)]
    [InlineData(8192, 8192)]
    public void DecodesLinuxGoldens(int inputSize, int outputSize)
    {
        foreach (ReferenceCase fixture in ReferenceCases.Goldens())
        {
            byte[] encoded = ReadGolden(fixture.Name);
            var (decoded, finished) = Decode(encoded, inputSize, outputSize);
            Assert.True(fixture.Input.AsSpan().SequenceEqual(decoded), fixture.Name);
            Assert.Equal(encoded.Length != 0, finished);
        }
    }

    [Fact]
    public async Task DecodesExtendedStockCorpus()
    {
        foreach (ReferenceCase fixture in ReferenceCases.Extended())
        {
            byte[] encoded = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: false);
            var (decoded, finished) = Decode(encoded, 257, 579);
            Assert.True(fixture.Input.AsSpan().SequenceEqual(decoded), fixture.Name);
            Assert.Equal(encoded.Length != 0, finished);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8192)]
    public void DecodesConcatenatedMembersAndOptionalHeader(int inputSize)
    {
        byte[] original = ReadGolden("probe");
        byte[] header = [0x1f, 0x8b, 8, 0x1f, 0, 0, 0, 0, 0, 3, 3, 0, 0xDE, 0xAD, 0xBE, (byte)'n', 0, (byte)'c', 0];
        byte[] extended = [.. header, 0, 0, .. original.AsSpan(10)];
        BinaryPrimitives.WriteUInt16LittleEndian(extended.AsSpan(header.Length), (ushort)~Crc32.Update(Crc32.Initial, header));
        byte[] encoded = [.. extended, .. original, 0x55, 0xAA];
        byte[] expected = "Lokad.GZip\nLokad.GZip\n"u8.ToArray();

        var (decoded, finished) = Decode(encoded, inputSize, 3);
        Assert.Equal(expected, decoded);
        Assert.True(finished);

        using var stock = new StockGZip(new MemoryStream(encoded), System.IO.Compression.CompressionMode.Decompress);
        using var stockOutput = new MemoryStream();
        stock.CopyTo(stockOutput);
        Assert.Equal(expected, stockOutput.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(23)]
    [InlineData(28)]
    public void RejectsInvalidHeaderAndTrailer(int offset)
    {
        byte[] data = ReadGolden("probe");
        data[offset] ^= 0x80;
        Assert.Throws<InvalidDataException>(() => Decode(data, 1, 1));
    }

    [Fact]
    public void ExposesAnUnfinishedMemberAfterTruncation()
    {
        byte[] original = ReadGolden("probe");
        var (decoded, finished) = Decode(original[..^1], 1, 2);
        Assert.Equal("Lokad.GZip\n"u8.ToArray(), decoded);
        Assert.False(finished);
    }

#if !NET11_0_OR_GREATER
    [Fact]
    public void DecodesTheUnfinishedNet10AsyncEmptyFlush()
    {
        var (decoded, finished) = Decode(ReadGolden("empty-flush-async"), 1, 1);
        Assert.Empty(decoded);
        Assert.False(finished);
    }
#endif

    [Fact]
    public void RejectsInvalidBlockType()
    {
        byte[] data = ReadGolden("probe");
        data[10] |= 7;
        Assert.Throws<InvalidDataException>(() => Decode(data, 1, 3));
    }

    [Fact]
    public void UsesTheIeeeCrcPolynomial()
    {
        Assert.Equal(0xCBF43926u, ~Crc32.Update(Crc32.Initial, "123456789"u8));
    }

    private static byte[] ReadGolden(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Goldens", name + ".gz"));

    private static (byte[] Output, bool FinishedMember) Decode(byte[] encoded, int inputSize, int outputSize)
    {
        using var inflater = new GZipInflater();
        using var output = new MemoryStream();
        var buffer = new byte[outputSize];
        int offset = 0;
        int endOfFeed = Math.Min(inputSize, encoded.Length);
        while (true)
        {
            ReadOnlySpan<byte> input = encoded.AsSpan(offset, endOfFeed - offset);
            InflateStatus status = inflater.Decode(input, buffer, out int consumed, out int written);
            offset += consumed;
            output.Write(buffer.AsSpan(0, written));
            if (status == InflateStatus.Done)
                break;
            if (status == InflateStatus.NeedInput)
            {
                Assert.Equal(endOfFeed, offset);
                if (offset == encoded.Length)
                    break;
                endOfFeed = Math.Min(offset + inputSize, encoded.Length);
            }
            else
            {
                Assert.Equal(buffer.Length, written);
            }
        }
        return (output.ToArray(), inflater.FinishedMember);
    }
}
