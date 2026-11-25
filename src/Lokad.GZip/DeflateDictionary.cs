using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace Lokad.GZip;

internal sealed class DeflateDictionary : IDisposable
{
    private readonly ushort[] _heads;
    private readonly ushort[] _previous;
    private readonly int _headCount;
    private readonly int _windowSize;
    private readonly int _mask;
    private readonly bool _rolling;
    private int _rollingHash;

    internal DeflateDictionary(int windowBits, bool rolling)
    {
        _rolling = rolling;
        _headCount = rolling ? 32768 : 65536;
        _windowSize = 1 << windowBits;
        _mask = _windowSize - 1;
        _heads = ArrayPool<ushort>.Shared.Rent(_headCount);
        _previous = ArrayPool<ushort>.Shared.Rent(_windowSize);
        _heads.AsSpan(0, _headCount).Clear();
        // Rolling searches can inspect positions that were skipped by insertion.
        if (rolling)
            _previous.AsSpan(0, _windowSize).Clear();
    }

    public void Dispose()
    {
        ArrayPool<ushort>.Shared.Return(_previous);
        ArrayPool<ushort>.Shared.Return(_heads);
    }

    internal int Head(int hash) => _heads[hash];

    internal int Previous(int position) => _previous[position & _mask];

    internal static int UpdateHash(int hash, byte value) => ((hash << 5) ^ value) & 32767;

    internal void Seed(ReadOnlySpan<byte> window, int position)
        => _rollingHash = UpdateHash(window[position], window[position + 1]);

    internal int Insert(ReadOnlySpan<byte> window, int position)
    {
        int hash;
        if (_rolling)
        {
            _rollingHash = UpdateHash(_rollingHash, window[position + 2]);
            hash = _rollingHash;
        }
        else
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(window[position..]);
            hash = (int)(unchecked(value * 2654435761u) >> 16);
        }

        int head = _heads[hash];
        if (head != position)
        {
            _previous[position & _mask] = (ushort)head;
            _heads[hash] = (ushort)position;
        }
        return head;
    }

    internal void Insert(ReadOnlySpan<byte> window, int position, int length)
    {
        for (int i = 0; i < length; i++)
            Insert(window, position + i);
    }

    internal void Slide()
    {
        Slide(_heads.AsSpan(0, _headCount), _windowSize);
        Slide(_previous.AsSpan(0, _windowSize), _windowSize);
    }

    private static void Slide(Span<ushort> indices, int amount)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<ushort> delta = Vector256.Create((ushort)amount);
            for (; i <= indices.Length - Vector256<ushort>.Count; i += Vector256<ushort>.Count)
            {
                Vector256<ushort> values = Vector256.Create((ReadOnlySpan<ushort>)indices[i..]);
                Vector256.SubtractSaturate(values, delta).CopyTo(indices[i..]);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<ushort> delta = Vector128.Create((ushort)amount);
            for (; i <= indices.Length - Vector128<ushort>.Count; i += Vector128<ushort>.Count)
            {
                Vector128<ushort> values = Vector128.Create((ReadOnlySpan<ushort>)indices[i..]);
                Vector128.SubtractSaturate(values, delta).CopyTo(indices[i..]);
            }
        }

        for (; i < indices.Length; i++)
            indices[i] = (ushort)Math.Max(indices[i] - amount, 0);
    }
}
