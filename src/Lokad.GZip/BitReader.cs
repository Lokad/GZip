using System.Buffers.Binary;

namespace Lokad.GZip;

internal struct BitReader
{
    private ulong _buffer;

    internal int Count { get; private set; }

    internal bool Fill(ref ReadOnlySpan<byte> input, int count)
    {
        if (Count < count && input.Length >= sizeof(uint))
        {
            // Requests are at most 32 bits, so the reservoir still fits in a ulong.
            _buffer |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(input) << Count;
            Count += 32;
            input = input[sizeof(uint)..];
            return true;
        }

        while (Count < count && !input.IsEmpty)
        {
            _buffer |= (ulong)input[0] << Count;
            Count += 8;
            input = input[1..];
        }
        return Count >= count;
    }

    internal uint Peek(int count) => (uint)(_buffer & ((1UL << count) - 1));

    internal void Consume(int count)
    {
        _buffer >>= count;
        Count -= count;
    }

    internal void Align() => Consume(Count & 7);

    internal bool TryRead(ref ReadOnlySpan<byte> input, int count, out uint value)
    {
        if (!Fill(ref input, count))
        {
            value = 0;
            return false;
        }

        value = Peek(count);
        Consume(count);
        return true;
    }
}
