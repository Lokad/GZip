using System.Buffers;
using System.Diagnostics;

namespace Lokad.GZip;

internal sealed class GZipSpanEncoder(IDeflateEncoder encoder) : IDisposable
{
    private byte[]? _pending;
    private int _pendingOffset;
    private int _pendingCount;
    private bool _completeAfterPending;
    private DeflateOperation? _operation;
    private bool _finished;
#if !NET11_0_OR_GREATER
    private bool _wroteBytes;
#endif

    internal OperationStatus Encode(ReadOnlySpan<byte> input, Span<byte> output, DeflateOperation operation,
        out int consumed, out int written)
    {
        consumed = written = 0;
        if (_finished)
        {
            if (operation == DeflateOperation.Finish)
                return OperationStatus.Done;
            throw new InvalidOperationException("The gzip member has already finished.");
        }
        if (_operation.HasValue && _operation != operation)
            throw new InvalidOperationException("Complete the pending span operation first.");
        _operation = operation;

        if (_pendingCount != 0)
        {
            int count = Math.Min(_pendingCount, output.Length);
            _pending.AsSpan(_pendingOffset, count).CopyTo(output);
            _pendingOffset += count;
            _pendingCount -= count;
            written = count;
            if (_pendingCount != 0)
                return OperationStatus.DestinationTooSmall;

            output = output[count..];
            if (_completeAfterPending && (operation != DeflateOperation.Write || input.IsEmpty))
                return Complete(operation);
        }

        if (operation == DeflateOperation.Write && input.IsEmpty)
            return Complete(operation);
#if !NET11_0_OR_GREATER
        if (operation != DeflateOperation.Write && !_wroteBytes)
            return Complete(operation);
#endif

        // Preserve the output capacity used by each stream encoder step for canonical block and flush boundaries.
        // Flush and finish can confirm completion when the preceding step filled the caller's span.
        while (!output.IsEmpty || operation != DeflateOperation.Write)
        {
            bool staged = output.Length < GZipWriter.BufferSize;
            Span<byte> encoded = staged
                ? (_pending ??= ArrayPool<byte>.Shared.Rent(GZipWriter.BufferSize)).AsSpan(0, GZipWriter.BufferSize)
                : output[..GZipWriter.BufferSize];
            DeflateProgress progress = encoder.Encode(input, encoded, operation);
            consumed += progress.Consumed;
            input = input[progress.Consumed..];
#if !NET11_0_OR_GREATER
            _wroteBytes |= progress.Consumed != 0;
#endif
            bool complete = operation == DeflateOperation.Write ? input.IsEmpty : progress.Complete;
            Debug.Assert(complete || progress.Consumed != 0 || progress.Written != 0);

            int count = Math.Min(progress.Written, output.Length);
            if (staged)
                encoded[..count].CopyTo(output);
            written += count;
            if (count < progress.Written)
            {
                _pendingOffset = count;
                _pendingCount = progress.Written - count;
                _completeAfterPending = complete;
                return OperationStatus.DestinationTooSmall;
            }

            if (complete)
                return Complete(operation);
            output = output[count..];
        }

        return OperationStatus.DestinationTooSmall;
    }

    public void Dispose()
    {
        encoder.Dispose();
        if (_pending is not null)
            ArrayPool<byte>.Shared.Return(_pending);
    }

    private OperationStatus Complete(DeflateOperation operation)
    {
        _operation = null;
        _finished = operation == DeflateOperation.Finish;
        return OperationStatus.Done;
    }
}
