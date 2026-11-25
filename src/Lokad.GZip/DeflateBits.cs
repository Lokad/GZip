using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace Lokad.GZip;

internal sealed class DeflateBits : IDisposable
{
    internal const int BufferSize = 65536;

    private readonly byte[] _pending = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _start;
    private int _end;
    private ulong _bits;
    private int _count;

    internal int Pending => _end - _start;

    public void Dispose() => ArrayPool<byte>.Shared.Return(_pending);

    internal void Write(ulong value, int count)
    {
        Debug.Assert(count is >= 0 and < 64 && (value >> count) == 0);
        _bits |= value << _count;
        int total = _count + count;
        if (total >= 64)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_pending.AsSpan(_end), _bits);
            _end += sizeof(ulong);
            _bits = value >> (64 - _count);
            total -= 64;
        }
        _count = total;
    }

    internal void Align()
    {
        FlushBytes();
        if (_count > 0)
            _pending[_end++] = (byte)_bits;
        _bits = 0;
        _count = 0;
    }

    internal Span<byte> ReserveAligned(int count)
    {
        Debug.Assert(_count == 0 && _start == 0);
        Span<byte> destination = _pending.AsSpan(_end, count);
        _end += count;
        return destination;
    }

    internal void Drain(ref Span<byte> destination)
    {
        FlushBytes();
        int count = Math.Min(Pending, destination.Length);
        _pending.AsSpan(_start, count).CopyTo(destination);
        destination = destination[count..];
        _start += count;
        if (_start == _end)
            _start = _end = 0;
    }

    private void FlushBytes()
    {
        while (_count >= 8)
        {
            _pending[_end++] = (byte)_bits;
            _bits >>= 8;
            _count -= 8;
        }
    }
}
