using System.Buffers.Binary;

namespace Lokad.GZip;

internal struct BitReader
{
    private ulong _buffer;

    internal int Count { get; private set; }

    internal ulong BufferedBits => _buffer;

    internal void SetBuffer(ulong bits, int count)
    {
        _buffer = bits;
        Count = count;
    }

    internal bool Fill(ref ReadOnlySpan<byte> input, int count)
    {
        if (Count < count && input.Length >= sizeof(uint))
        {
            if (Count <= 8 && input.Length >= sizeof(ulong))
            {
                // Refill seven bytes, leaving room for up to eight bits already held.
                ulong next = BinaryPrimitives.ReadUInt64LittleEndian(input) & (ulong.MaxValue >> 8);
                _buffer |= next << Count;
                Count += 56;
                input = input[7..];
            }
            else
            {
                // Requests are at most 32 bits, so this refill fits at any other position.
                _buffer |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(input) << Count;
                Count += 32;
                input = input[sizeof(uint)..];
            }
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
        if (Count < count && !Fill(ref input, count))
        {
            value = 0;
            return false;
        }

        value = Peek(count);
        Consume(count);
        return true;
    }
}
