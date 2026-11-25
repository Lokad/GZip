using Lokad.GZip.Verification;

namespace Lokad.GZip.Tests;

public sealed class Crc32Tests
{
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
                    Assert.Equal(expected, Crc32.Update(seed, bytes.AsSpan(offset, length)));
                    expected = Crc32.Update(expected, bytes[offset + length]);
                }
            }
        }
    }
}
