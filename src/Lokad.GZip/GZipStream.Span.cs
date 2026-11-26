using System.Buffers;
using System.IO.Compression;

namespace Lokad.GZip;

public partial class GZipStream
{
    private readonly bool _spanOnly;
    private GZipSpanEncoder? _spanEncoder;

    private bool HasSpanCodec => _reader is not null || _spanEncoder is not null;

    private GZipStream()
    {
        _spanOnly = true;
        _reader = new(stopAtMemberEnd: true);
    }

    private GZipStream(IDeflateEncoder encoder)
    {
        _spanOnly = true;
        _spanEncoder = new GZipSpanEncoder(encoder);
    }

    /// <summary>Creates a gzip encoder for caller-provided spans.</summary>
    public static GZipStream CreateSpanEncoder(CompressionLevel compressionLevel = CompressionLevel.Optimal)
        => new(CreateEncoder(ResolveLevel(compressionLevel), ZLibCompressionStrategy.Default, 15));

    /// <summary>Creates a gzip encoder for caller-provided spans with the specified options.</summary>
    public static GZipStream CreateSpanEncoder(ZLibCompressionOptions compressionOptions)
    {
        var (level, strategy, window) = ResolveOptions(compressionOptions);
        return new(CreateEncoder(level, strategy, window));
    }

    /// <summary>Creates a decoder for a single gzip member in caller-provided spans.</summary>
    public static GZipStream CreateSpanDecoder() => new();

    /// <summary>Encodes bytes from the supplied input into the supplied output.</summary>
    /// <remarks>
    /// Resubmit the unconsumed input with a fresh output span after
    /// <see cref="OperationStatus.DestinationTooSmall"/>.
    /// </remarks>
    public OperationStatus Write(ReadOnlySpan<byte> input, Span<byte> output, out int consumed, out int written)
        => EncodeSpan(input, output, DeflateOperation.Write, out consumed, out written);

    /// <summary>Flushes buffered compressed bytes to the supplied output.</summary>
    /// <remarks>Continue flushing with fresh output spans until this operation returns <see cref="OperationStatus.Done"/>.</remarks>
    public OperationStatus Flush(Span<byte> output, out int written)
        => EncodeSpan([], output, DeflateOperation.Flush, out _, out written);

    /// <summary>Completes the gzip member and drains its remaining bytes.</summary>
    /// <remarks>
    /// Continue finishing with fresh output spans until this operation returns <see cref="OperationStatus.Done"/>.
    /// Disposal releases the codec state.
    /// </remarks>
    public OperationStatus Finish(Span<byte> output, out int written)
        => EncodeSpan([], output, DeflateOperation.Finish, out _, out written);

    /// <summary>Decodes bytes from the supplied input into the supplied output.</summary>
    /// <param name="input">Compressed bytes available for this call.</param>
    /// <param name="output">Storage for decoded bytes. Its range must be disjoint from <paramref name="input"/>.</param>
    /// <param name="consumed">The number of input bytes consumed during this call.</param>
    /// <param name="written">The number of output bytes written during this call.</param>
    /// <param name="isFinalInput">Whether this input and any unconsumed suffix are the end of the source.</param>
    /// <returns>The current operation status. A verified member ends with <see cref="OperationStatus.Done"/>.</returns>
    /// <remarks>
    /// Resubmit any unconsumed input with each continuation. A completed member leaves following bytes
    /// in the input span. Decoded bytes are provisional until the trailer has been validated.
    /// </remarks>
    public OperationStatus Read(ReadOnlySpan<byte> input, Span<byte> output, out int consumed, out int written,
        bool isFinalInput = false)
    {
        EnterSpanOperation();
        try
        {
            RequireDisjointSpans(input, output);
            return Reader.DecodeMember(input, output, isFinalInput, out consumed, out written);
        }
        finally
        {
            ExitOperation();
        }
    }

    private OperationStatus EncodeSpan(ReadOnlySpan<byte> input, Span<byte> output, DeflateOperation operation,
        out int consumed, out int written)
    {
        EnterSpanOperation();
        try
        {
            RequireDisjointSpans(input, output);
            GZipSpanEncoder encoder = _spanEncoder ?? throw new InvalidOperationException("The gzip stream is open for decompression.");
            return encoder.Encode(input, output, operation, out consumed, out written);
        }
        finally
        {
            ExitOperation();
        }
    }

    private static void RequireDisjointSpans(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Overlaps(output))
            throw new ArgumentException("The input and output spans must be disjoint.", nameof(output));
    }

    private void EnterSpanOperation()
    {
        AcquireOperation();
        if (_spanOnly && HasSpanCodec)
            return;

        ExitOperation();
        ThrowIfDisposed();
        throw new InvalidOperationException("The gzip stream uses an underlying stream.");
    }

    private void ThrowIfSpanOnly()
    {
        if (_spanOnly)
        {
            ThrowIfDisposed();
            throw new NotSupportedException("This gzip stream processes caller-provided spans.");
        }
    }

    private void DisposeSpan()
    {
        if (Volatile.Read(ref _reader) is null && Volatile.Read(ref _spanEncoder) is null)
            return;

        AcquireOperation();
        try
        {
            GZipReader? reader = _reader;
            GZipSpanEncoder? encoder = _spanEncoder;
            _reader = null;
            _spanEncoder = null;
            reader?.Dispose();
            encoder?.Dispose();
        }
        finally
        {
            ExitOperation();
        }
    }
}
