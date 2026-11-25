namespace Lokad.GZip;

internal enum DeflateOperation
{
    Write,
    Flush,
    Finish,
}

internal readonly record struct DeflateProgress(int Consumed, int Written, bool Complete);

internal interface IDeflateEncoder : IDisposable
{
    DeflateProgress Encode(ReadOnlySpan<byte> input, Span<byte> output, DeflateOperation operation);
}
