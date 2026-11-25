namespace Lokad.GZip;

// The heap order and length limiting follow zlib-ng's block tree builder.
internal sealed class DeflateCodeTree(int symbols)
{
    private const int MaximumBits = 15;
    // A DEFLATE tree has at most 288 leaves. The low bits hold depth for weight ties.
    private const int DepthBits = 9;
    private const uint DepthMask = (1u << DepthBits) - 1;
    private readonly byte[] _lengths = new byte[symbols];
    private readonly ushort[] _codes = new ushort[symbols];

    internal ReadOnlySpan<byte> Lengths => _lengths;

    internal ushort Code(int symbol) => _codes[symbol];

    internal void Send(DeflateBits bits, int symbol)
        => bits.Write(_codes[symbol], _lengths[symbol]);

    internal int CountBits(ReadOnlySpan<int> frequencies)
    {
        int count = 0;
        for (int i = 0; i < frequencies.Length; i++)
            count += frequencies[i] * _lengths[i];
        return count;
    }

    internal void SetLengths(ReadOnlySpan<byte> lengths)
    {
        lengths.CopyTo(_lengths);
        AssignCodes();
    }

    internal int Build(ReadOnlySpan<int> frequencies, int limit = MaximumBits)
    {
        int nodes = 2 * _lengths.Length + 1;
        Span<int> heap = stackalloc int[nodes];
        Span<uint> priorities = stackalloc uint[nodes];
        Span<int> parents = stackalloc int[nodes];
        Span<int> nodeLengths = stackalloc int[nodes];
        Span<int> counts = stackalloc int[MaximumBits + 1];
        counts.Clear();
        Array.Clear(_lengths);

        int heapSize = 0;
        int lastCode = -1;
        for (int symbol = 0; symbol < frequencies.Length; symbol++)
        {
            if (frequencies[symbol] != 0)
            {
                heap[++heapSize] = lastCode = symbol;
                priorities[symbol] = (uint)frequencies[symbol] << DepthBits;
            }
        }
        while (heapSize < 2)
        {
            int dummy = lastCode < 2 ? ++lastCode : 0;
            heap[++heapSize] = dummy;
            priorities[dummy] = 1u << DepthBits;
        }

        for (int index = heapSize / 2; index >= 1; index--)
            SiftDown(heap, priorities, heapSize, index);

        int sorted = heap.Length;
        int parent = _lengths.Length;
        do
        {
            int first = heap[1];
            heap[1] = heap[heapSize--];
            SiftDown(heap, priorities, heapSize, 1);
            int second = heap[1];
            heap[--sorted] = first;
            heap[--sorted] = second;

            uint left = priorities[first];
            uint right = priorities[second];
            uint weight = (left & ~DepthMask) + (right & ~DepthMask);
            priorities[parent] = weight + Math.Max(left & DepthMask, right & DepthMask) + 1;
            parents[first] = parents[second] = parent;
            heap[1] = parent++;
            SiftDown(heap, priorities, heapSize, 1);
        } while (heapSize >= 2);

        nodeLengths[heap[1]] = 0;
        int overflow = 0;
        for (int index = sorted; index < heap.Length; index++)
        {
            int node = heap[index];
            int length = nodeLengths[parents[node]] + 1;
            if (length > limit)
            {
                length = limit;
                overflow++;
            }
            nodeLengths[node] = length;
            if (node <= lastCode)
            {
                counts[length]++;
                _lengths[node] = (byte)length;
            }
        }

        if (overflow > 0)
            LimitLengths(heap, counts, limit, lastCode, overflow);
        AssignCodes();
        return lastCode + 1;
    }

    private void LimitLengths(ReadOnlySpan<int> heap, Span<int> counts, int limit, int lastCode, int overflow)
    {
        do
        {
            int length = limit - 1;
            while (counts[length] == 0)
                length--;
            counts[length]--;
            counts[length + 1] += 2;
            counts[limit]--;
            overflow -= 2;
        } while (overflow > 0);

        int index = heap.Length;
        for (int length = limit; length > 0; length--)
        {
            for (int remaining = counts[length]; remaining > 0;)
            {
                int node = heap[--index];
                if (node <= lastCode)
                {
                    _lengths[node] = (byte)length;
                    remaining--;
                }
            }
        }
    }

    private void AssignCodes()
    {
        Span<int> counts = stackalloc int[MaximumBits + 1];
        counts.Clear();
        foreach (int length in _lengths)
        {
            if (length > 0)
                counts[length]++;
        }

        Span<int> next = stackalloc int[MaximumBits + 1];
        int code = 0;
        for (int length = 1; length <= MaximumBits; length++)
        {
            code = (code + counts[length - 1]) << 1;
            next[length] = code;
        }

        for (int symbol = 0; symbol < _lengths.Length; symbol++)
        {
            int length = _lengths[symbol];
            _codes[symbol] = length == 0 ? (ushort)0 : (ushort)DeflateCodes.Reverse(next[length]++, length);
        }
    }

    private static void SiftDown(Span<int> heap, ReadOnlySpan<uint> priorities, int size, int index)
    {
        int node = heap[index];
        uint priority = priorities[node];
        int child = 2 * index;
        while (child <= size)
        {
            int selected = heap[child];
            uint selectedPriority = priorities[selected];
            if (child < size)
            {
                int right = heap[child + 1];
                uint rightPriority = priorities[right];
                if (rightPriority <= selectedPriority)
                {
                    child++;
                    selected = right;
                    selectedPriority = rightPriority;
                }
            }
            if (priority <= selectedPriority)
                break;
            heap[index] = selected;
            index = child;
            child *= 2;
        }
        heap[index] = node;
    }
}
