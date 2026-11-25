using Lokad.GZip.Verification;

namespace Lokad.GZip.Tests;

public sealed class Crc32Tests
{
    [Fact]
    public void BatchedCrcMatchesBytewiseCrcAtEveryLengthAndAlignment()
    {
        const int maxLength = 2048;
        byte[] bytes = ReferenceCases.RandomBytes(maxLength + 8);
        byte[] copied = new byte[bytes.Length];
        foreach (uint seed in new[] { 0u, Crc32.Initial, 0xAB865312u })
        {
            for (int offset = 0; offset < 8; offset++)
            {
                uint expected = seed;
                for (int length = 0; length <= maxLength; length++)
                {
                    ReadOnlySpan<byte> input = bytes.AsSpan(offset, length);
                    Assert.Equal(expected, Crc32.Update(seed, input));
                    if (Crc32.CanFoldAndCopy(length))
                    {
                        Span<byte> output = copied.AsSpan(7 - offset, length);
                        output.Fill(0xA5);
                        Assert.Equal(expected, Crc32.FoldAndCopy(seed, input, output));
                        Assert.True(input.SequenceEqual(output));
                    }
                    expected = Crc32.Update(expected, bytes[offset + length]);
                }
            }
        }
    }
}
