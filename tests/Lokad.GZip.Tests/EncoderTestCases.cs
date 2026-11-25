using Lokad.GZip.Verification;

namespace Lokad.GZip.Tests;

internal static class EncoderTestCases
{
    internal static Task<byte[]> EncodeAsync(ReferenceCase fixture, bool async)
        => ReferenceCases.CompressAsync(fixture, CreateStream, async);

    private static Stream CreateStream(Stream destination, ReferenceCase fixture)
        => fixture.NumericLevel.HasValue
            ? new GZipStream(destination, ReferenceCases.GetOptions(fixture), leaveOpen: true)
            : new GZipStream(destination, fixture.Level, leaveOpen: true);

    internal static void AssertBytes(byte[] expected, byte[] actual, string name)
    {
        int different = 0;
        int sharedLength = Math.Min(expected.Length, actual.Length);
        while (different < sharedLength && expected[different] == actual[different])
            different++;
        Assert.True(different == expected.Length && expected.Length == actual.Length,
            $"{name} differs at offset {different}. Expected {expected.Length} bytes, got {actual.Length}.");
    }
}
