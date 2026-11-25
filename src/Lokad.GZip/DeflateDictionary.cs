using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
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

    internal DeflateDictionary(int windowBits, int level)
    {
        _rolling = level == 9;
        _headCount = _rolling ? 32768 : 65536;
        _windowSize = 1 << windowBits;
        _mask = _windowSize - 1;
        _heads = ArrayPool<ushort>.Shared.Rent(_headCount);
        // Level 1 probes the most recent position for each hash.
        _previous = level == 1 ? [] : ArrayPool<ushort>.Shared.Rent(_windowSize);
        _heads.AsSpan(0, _headCount).Clear();
        // Rolling searches can inspect positions that were skipped by insertion.
        if (_rolling)
            _previous.AsSpan(0, _windowSize).Clear();
    }

    public void Dispose()
    {
        if (_previous.Length != 0)
            ArrayPool<ushort>.Shared.Return(_previous);
        ArrayPool<ushort>.Shared.Return(_heads);
    }

    internal int Head(int hash) => _heads[hash];

    internal int Previous(int position) => _previous[position & _mask];

    internal static int UpdateHash(int hash, byte value) => ((hash << 5) ^ value) & 32767;

    private static int HashWord(ReadOnlySpan<byte> window, int position)
        => (int)(unchecked(BinaryPrimitives.ReadUInt32LittleEndian(window[position..]) * 2654435761u) >> 16);

    internal int InsertHead(ReadOnlySpan<byte> window, int position)
    {
        int hash = HashWord(window, position);
        int head = _heads[hash];
        _heads[hash] = (ushort)position;
        return head;
    }

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
            hash = HashWord(window, position);
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

    internal void Reinsert(ReadOnlySpan<byte> window, int start, int length)
    {
        if (_previous.Length == 0)
        {
            for (int position = Math.Max(start - 1, 0); position < start + length; position++)
                InsertHead(window, position);
            return;
        }

        if (_rolling)
            _rollingHash = UpdateHash(window[start], window[start + 1]);
        else if (start > 0)
            Insert(window, start - 1);
        Insert(window, start, length);
    }

    internal void Slide()
    {
        Slide(_heads.AsSpan(0, _headCount), _windowSize);
        if (_previous.Length != 0)
            Slide(_previous.AsSpan(0, _windowSize), _windowSize);
    }

    private static void Slide(Span<ushort> indices, int amount)
    {
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            Vector512<ushort> delta = Vector512.Create((ushort)amount);
            Span<Vector512<ushort>> groups = MemoryMarshal.Cast<ushort, Vector512<ushort>>(indices);
            foreach (ref Vector512<ushort> group in groups)
                group = Vector512.SubtractSaturate(group, delta);
            i = groups.Length * Vector512<ushort>.Count;
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            Vector256<ushort> delta = Vector256.Create((ushort)amount);
            Span<Vector256<ushort>> groups = MemoryMarshal.Cast<ushort, Vector256<ushort>>(indices);
            foreach (ref Vector256<ushort> group in groups)
                group = Vector256.SubtractSaturate(group, delta);
            i = groups.Length * Vector256<ushort>.Count;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<ushort> delta = Vector128.Create((ushort)amount);
            Span<Vector128<ushort>> groups = MemoryMarshal.Cast<ushort, Vector128<ushort>>(indices);
            foreach (ref Vector128<ushort> group in groups)
                group = Vector128.SubtractSaturate(group, delta);
            i = groups.Length * Vector128<ushort>.Count;
        }

        for (; i < indices.Length; i++)
            indices[i] = (ushort)Math.Max(indices[i] - amount, 0);
    }
}
