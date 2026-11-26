using System.Buffers;
using System.IO.Compression;
using Lokad.GZip.Verification;
using static Lokad.GZip.Tests.EncoderTestCases;

namespace Lokad.GZip.Tests;

public sealed class SpanEncoderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(8192)]
    [InlineData(32768)]
    public async Task OutputFragmentationPreservesTheReferenceSchedule(int outputSize)
    {
        foreach (ReferenceCase fixture in ReferenceCases.Goldens())
        {
            byte[] expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Goldens", fixture.Name + ".gz"));
            AssertBytes(expected, Encode(fixture, outputSize), fixture.Name);
        }

        var stored = new ReferenceCase("large stored write", ReferenceCases.RandomBytes(210017), CompressionLevel.NoCompression,
            ChunkSize: 135737, FlushEvery: 1);
        byte[] streamBytes = await EncodeAsync(stored, async: false);
        AssertBytes(streamBytes, Encode(stored, outputSize), stored.Name);
    }

    [Fact]
    public void APreemptedFlushResumesBeforeFinishing()
    {
        using var encoder = GZipStream.CreateSpanEncoder();
        byte[] output = new byte[8192];
        Assert.Equal(OperationStatus.DestinationTooSmall, encoder.Write("abc"u8, [], out int consumed, out int written));
        Assert.Equal(0, consumed);
        Assert.Equal(0, written);
        Assert.Equal(OperationStatus.Done, encoder.Write("abc"u8, output, out consumed, out written));
        Assert.Equal(3, consumed);

        Assert.Equal(OperationStatus.DestinationTooSmall, encoder.Flush([], out written));
        Assert.Equal(0, written);
        Assert.Throws<InvalidOperationException>(() => encoder.Finish(output, out _));
        Assert.Equal(OperationStatus.Done, encoder.Flush(output, out written));
        Assert.True(written > 0);

        Assert.Equal(OperationStatus.Done, encoder.Finish(output, out written));
        Assert.True(written >= 8);
        Assert.Equal(OperationStatus.Done, encoder.Finish([], out written));
        Assert.Equal(0, written);
        Assert.Throws<InvalidOperationException>(() => encoder.Write("again"u8, output, out _, out _));
    }

    [Fact]
    public void SpanEncoderRejectsOverlapAndUsesItsOwnLifetime()
    {
        byte[] shared = new byte[128];
        using var encoder = GZipStream.CreateSpanEncoder();
        Assert.Throws<ArgumentException>(() => encoder.Write(shared.AsSpan(0, 64), shared.AsSpan(63), out _, out _));
        Assert.False(encoder.CanWrite);
        Assert.Throws<NotSupportedException>(() => encoder.WriteByte(0));
        using var decoder = GZipStream.CreateSpanDecoder();
        Assert.Throws<InvalidOperationException>(() => decoder.Finish(shared, out _));

        encoder.Dispose();
        encoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => encoder.Finish(shared, out _));
    }

    private static byte[] Encode(ReferenceCase fixture, int outputSize)
    {
        using GZipStream encoder = fixture.NumericLevel.HasValue
            ? GZipStream.CreateSpanEncoder(ReferenceCases.GetOptions(fixture))
            : GZipStream.CreateSpanEncoder(fixture.Level);
        using var output = new MemoryStream();
        byte[] buffer = new byte[outputSize];

        if (fixture.Input.Length == 0)
        {
            if (fixture.Empty == EmptyOperation.Write)
                Write([]);
            else if (fixture.Empty == EmptyOperation.Flush)
                Drain(flush: true);
        }

        int chunk = 0;
        for (int offset = 0; offset < fixture.Input.Length;)
        {
            int length = Math.Min(fixture.Input.Length - offset, fixture.ChunkSize);
            Write(fixture.Input.AsSpan(offset, length));
            offset += length;
            if (fixture.FlushEvery > 0 && ++chunk % fixture.FlushEvery == 0)
                Drain(flush: true);
        }
        Drain(flush: false);
        return output.ToArray();

        void Write(ReadOnlySpan<byte> input)
        {
            while (true)
            {
                OperationStatus status = encoder.Write(input, buffer, out int consumed, out int written);
                Assert.InRange(consumed, 0, input.Length);
                Assert.InRange(written, 0, buffer.Length);
                input = input[consumed..];
                output.Write(buffer.AsSpan(0, written));
                if (status == OperationStatus.Done)
                {
                    Assert.True(input.IsEmpty);
                    break;
                }
                Assert.Equal(OperationStatus.DestinationTooSmall, status);
                Assert.Equal(buffer.Length, written);
            }
        }

        void Drain(bool flush)
        {
            while (true)
            {
                OperationStatus status = flush ? encoder.Flush(buffer, out int written) : encoder.Finish(buffer, out written);
                Assert.InRange(written, 0, buffer.Length);
                output.Write(buffer.AsSpan(0, written));
                if (status == OperationStatus.Done)
                    break;
                Assert.Equal(OperationStatus.DestinationTooSmall, status);
                Assert.Equal(buffer.Length, written);
            }
        }
    }
}
