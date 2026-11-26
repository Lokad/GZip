using Lokad.GZip.Verification;

namespace Lokad.GZip.Tests;

public sealed class Crc32Tests
{
    [Fact]
    public void UsesTheIeeeCrcPolynomial()
    {
        Assert.Equal(0xCBF43926u, ~Crc32.Update(Crc32.Initial, "123456789"u8));
    }

    [Fact]
    public void BatchedCrcMatchesBytewiseCrcAtEveryLengthAndAlignment()
    {
        const int maxLength = 2048;
        byte[] bytes = ReferenceCases.RandomBytes(maxLength + 8);
        foreach (uint seed in new[] { 0u, Crc32.Initial, 0xAB865312u })
        {
            for (int offset = 0; offset < 8; offset++)
            {
                uint expected = seed;
                for (int length = 0; length <= maxLength; length++)
                {
                    ReadOnlySpan<byte> input = bytes.AsSpan(offset, length);
                    Assert.Equal(expected, Crc32.Update(seed, input));
                    expected = Crc32.Update(expected, bytes[offset + length]);
                }
            }
        }
    }

    [WideCopyFact]
    public void FusedCopiesMatchBytewiseCrcAndRequestedHistory()
    {
        const int windowSize = 32768;
        byte[] bytes = ReferenceCases.RandomBytes(2 * windowSize + 8);
        byte[] copied = new byte[windowSize + 8];
        Assert.False(Crc32.CanFoldAndCopy(511));
        Assert.True(Crc32.CanFoldAndCopy(512));

        foreach (uint seed in new[] { 0u, Crc32.Initial, 0xAB865312u })
        {
            for (int offset = 0; offset < 8; offset++)
            {
                foreach (int length in new[] { 512, 513, 639, 640, 2048, windowSize })
                {
                    ReadOnlySpan<byte> input = bytes.AsSpan(offset, length);
                    Span<byte> output = copied.AsSpan(7 - offset, length);
                    output.Fill(0xA5);
                    Assert.Equal(Bytewise(seed, input), Crc32.FoldAndCopy(seed, input, output));
                    Assert.True(input.SequenceEqual(output));
                }
            }

            ReadOnlySpan<byte> wrapped = bytes.AsSpan(3, 2 * windowSize);
            Span<byte> history = copied.AsSpan(5, windowSize);
            history.Fill(0xA5);
            Assert.Equal(Bytewise(seed, wrapped), Crc32.FoldAndCopySuffix(seed, wrapped, history));
            Assert.True(wrapped[^windowSize..].SequenceEqual(history));
        }

        static uint Bytewise(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
                crc = Crc32.Update(crc, value);
            return crc;
        }
    }

    private sealed class WideCopyFactAttribute : FactAttribute
    {
        public WideCopyFactAttribute()
        {
            if (!Crc32.CanFoldAndCopy(512))
                Skip = "Requires the 512-bit carry-less multiply path.";
        }
    }
}
