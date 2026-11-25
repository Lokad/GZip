using System.Buffers;

namespace Lokad.GZip;

internal sealed class GZipWriter(IDeflateEncoder encoder) : IDisposable
{
    private const int BufferSize = 8192;
    private readonly byte[] _output = ArrayPool<byte>.Shared.Rent(BufferSize);
#if !NET11_0_OR_GREATER
    private bool _wroteBytes;
#endif

    public void Dispose()
    {
        encoder.Dispose();
        ArrayPool<byte>.Shared.Return(_output);
    }

    internal void Write(Stream destination, ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
            return;
        while (!input.IsEmpty)
        {
            DeflateProgress progress = encoder.Encode(input, _output.AsSpan(0, BufferSize), DeflateOperation.Write);
            if (progress.Written > 0)
                destination.Write(_output, 0, progress.Written);
            input = input[progress.Consumed..];
        }
#if !NET11_0_OR_GREATER
        _wroteBytes = true;
#endif
    }

    internal async ValueTask WriteAsync(Stream destination, ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        if (input.IsEmpty)
            return;
        while (!input.IsEmpty)
        {
            DeflateProgress progress = encoder.Encode(input.Span, _output.AsSpan(0, BufferSize), DeflateOperation.Write);
            if (progress.Written > 0)
                await destination.WriteAsync(_output.AsMemory(0, progress.Written), cancellationToken).ConfigureAwait(false);
            input = input[progress.Consumed..];
        }
#if !NET11_0_OR_GREATER
        _wroteBytes = true;
#endif
    }

    internal void Flush(Stream destination)
    {
#if NET11_0_OR_GREATER
        Complete(destination, DeflateOperation.Flush);
#else
        if (_wroteBytes)
            Complete(destination, DeflateOperation.Flush);
#endif
        destination.Flush();
    }

    internal async ValueTask FlushAsync(Stream destination, CancellationToken cancellationToken)
    {
        await CompleteAsync(destination, DeflateOperation.Flush, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void Finish(Stream destination)
    {
#if NET11_0_OR_GREATER
        Complete(destination, DeflateOperation.Finish);
#else
        if (_wroteBytes)
            Complete(destination, DeflateOperation.Finish);
#endif
    }

    internal ValueTask FinishAsync(Stream destination)
    {
#if !NET11_0_OR_GREATER
        if (!_wroteBytes)
            return ValueTask.CompletedTask;
#endif
        return CompleteAsync(destination, DeflateOperation.Finish, CancellationToken.None);
    }

    private void Complete(Stream destination, DeflateOperation operation)
    {
        DeflateProgress progress;
        do
        {
            progress = encoder.Encode(ReadOnlySpan<byte>.Empty, _output.AsSpan(0, BufferSize), operation);
            if (progress.Written > 0)
                destination.Write(_output, 0, progress.Written);
        } while (!progress.Complete);
    }

    private async ValueTask CompleteAsync(Stream destination, DeflateOperation operation, CancellationToken cancellationToken)
    {
        DeflateProgress progress;
        do
        {
            progress = encoder.Encode(ReadOnlySpan<byte>.Empty, _output.AsSpan(0, BufferSize), operation);
            if (progress.Written > 0)
                await destination.WriteAsync(_output.AsMemory(0, progress.Written), cancellationToken).ConfigureAwait(false);
        } while (!progress.Complete);
    }
}
