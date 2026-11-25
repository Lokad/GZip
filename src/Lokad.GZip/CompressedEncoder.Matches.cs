using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace Lokad.GZip;

internal sealed partial class CompressedEncoder
{
    private const int WantedLength = 4;
    private readonly record struct SearchSettings(int Good, int Lazy, int Nice, int Chain);

    private int _matchStart;
    private int _previousLength;
    private int _insert;
    private int _quickBlock;
    private bool _matchAvailable;

    private int MaximumDistance => _windowSize - MinimumLookahead;

    private SearchSettings Settings => _level switch
    {
        2 => new(4, 4, 8, 4),
        3 => new(4, 6, 16, 6),
        4 => new(4, 12, 32, 24),
        5 => new(8, 16, 32, 32),
        6 => new(8, 16, 128, 128),
        7 => new(8, 32, 128, 256),
        8 => new(32, 128, 258, 1024),
        _ => new(32, 258, 258, 4096),
    };

    private bool EncodeQuick(DeflateDictionary dictionary, ref ReadOnlySpan<byte> input, ref Span<byte> output, DeflateOperation operation)
    {
        bool final = operation == DeflateOperation.Finish;
        if (final && _quickBlock != 2)
        {
            if (EndQuickBlock(ref output, final: false))
                return false;
            StartQuickBlock(final: true);
        }
        else if (_quickBlock == 0 && _lookahead > 0)
        {
            StartQuickBlock(final);
        }

        while (true)
        {
            if (_bits.Pending >= DeflateBits.BufferSize - sizeof(ulong) && Drain(ref output))
                return false;
            if (_lookahead < MinimumLookahead)
            {
                FillWindow(ref input);
                if (_lookahead < MinimumLookahead && operation == DeflateOperation.Write)
                    return true;
                if (_lookahead == 0)
                    break;
                if (_quickBlock == 0)
                    StartQuickBlock(final);
            }

            int length = 0;
            int distance = 0;
            if (_lookahead >= WantedLength)
            {
                int candidate = dictionary.InsertHead(_window, _position);
                distance = _position - candidate;
                if (distance > 0 && distance <= MaximumDistance)
                    length = CommonLength(_position, candidate);
            }
            if (length >= WantedLength)
            {
                length = Math.Min(length, _lookahead);
                DeflateBlockWriter.WriteFixedMatch(_bits, length, distance);
                Advance(length);
            }
            else
            {
                DeflateBlockWriter.WriteFixedLiteral(_bits, _window[_position]);
                Advance(1);
            }
        }

        _insert = Math.Min(_position, DeflateCodes.MinimumLength - 1);
        _finalBlockWritten = final;
        return !EndQuickBlock(ref output, final);
    }

    private void StartQuickBlock(bool final)
    {
        DeflateBlockWriter.StartFixed(_bits, final);
        _quickBlock = final ? 2 : 1;
        _blockStart = _position;
    }

    private bool EndQuickBlock(ref Span<byte> output, bool final)
    {
        if (_quickBlock == 0)
            return false;
        DeflateBlockWriter.EndFixed(_bits, final);
        _quickBlock = 0;
        _blockStart = _position;
        return Drain(ref output);
    }

    private bool EncodeFast(DeflateDictionary dictionary, ref ReadOnlySpan<byte> input, ref Span<byte> output, DeflateOperation operation)
    {
        SearchSettings settings = Settings;
        while (HaveLookahead(ref input, operation))
        {
            int candidate = _lookahead >= WantedLength ? dictionary.Insert(_window, _position) : 0;
            int length = InRange(candidate) ? LongestMatch(dictionary, candidate, settings) : 0;
            bool full;
            if (length >= WantedLength)
            {
                full = _blocks.AddMatch(length, _position - _matchStart);
                if (length <= settings.Lazy && _lookahead - length >= WantedLength)
                    dictionary.Insert(_window, _position + 1, length - 1);
                else
                    dictionary.Insert(_window, _position + length - 1);
                Advance(length);
            }
            else
            {
                full = _blocks.AddLiteral(_window[_position]);
                Advance(1);
            }

            if (full && WriteBlock(ref output, final: false))
                return false;
        }
        PrepareNextWrite(operation);
        return true;
    }

    private bool EncodeSlow(DeflateDictionary dictionary, ref ReadOnlySpan<byte> input, ref Span<byte> output, DeflateOperation operation)
    {
        SearchSettings settings = Settings;
        while (HaveLookahead(ref input, operation))
        {
            int candidate = _lookahead >= WantedLength ? dictionary.Insert(_window, _position) : 0;
            int previousMatch = _matchStart;
            int length = DeflateCodes.MinimumLength - 1;
            if (_previousLength < settings.Lazy && InRange(candidate))
            {
                length = LongestMatch(dictionary, candidate, settings);
                if (_strategy == ZLibCompressionStrategy.Filtered && length <= 5)
                    length = DeflateCodes.MinimumLength - 1;
            }

            if (_previousLength >= DeflateCodes.MinimumLength && length <= _previousLength)
            {
                bool full = _blocks.AddMatch(_previousLength, _position - 1 - previousMatch);
                int advance = _previousLength - 1;
                int insert = Math.Min(advance - 1, _lookahead - DeflateCodes.MinimumLength);
                dictionary.Insert(_window, _position + 1, insert);
                _previousLength = 0;
                _matchAvailable = false;
                Advance(advance);
                if (full && WriteBlock(ref output, final: false))
                    return false;
            }
            else
            {
                bool exhausted = _matchAvailable && _blocks.AddLiteral(_window[_position - 1]) &&
                    WriteBlock(ref output, final: false);
                _matchAvailable = true;
                _previousLength = length;
                Advance(1);
                if (exhausted)
                    return false;
            }
        }

        if (operation != DeflateOperation.Write && _matchAvailable)
        {
            _blocks.AddLiteral(_window[_position - 1]);
            _matchAvailable = false;
        }
        PrepareNextWrite(operation);
        return true;
    }

    private bool HaveLookahead(ref ReadOnlySpan<byte> input, DeflateOperation operation)
    {
        if (_lookahead < MinimumLookahead)
            FillWindow(ref input);
        return _lookahead > 0 && (_lookahead >= MinimumLookahead || operation != DeflateOperation.Write);
    }

    private void PrepareNextWrite(DeflateOperation operation)
    {
        if (operation != DeflateOperation.Write)
            _insert = Math.Min(_position, DeflateCodes.MinimumLength - 1);
    }

    private bool InRange(int candidate) => candidate > 0 && _position > candidate && _position - candidate <= MaximumDistance;

    private int LongestMatch(DeflateDictionary dictionary, int candidate, SearchSettings settings)
    {
        int best = _previousLength > 0 ? _previousLength : DeflateCodes.MinimumLength - 1;
        int chain = best >= settings.Good ? settings.Chain / 4 : settings.Chain;
        int baseLimit = Math.Max(_position - MaximumDistance, 0);
        int offset = 0;
        if (_level == 9 && best >= DeflateCodes.MinimumLength)
        {
            int hash = DeflateDictionary.UpdateHash(_window[_position + 1], _window[_position + 2]);
            for (int i = 3; i <= best; i++)
            {
                hash = DeflateDictionary.UpdateHash(hash, _window[_position + i]);
                int head = dictionary.Head(hash);
                if (head < candidate)
                {
                    candidate = head;
                    offset = i - 2;
                }
            }
            if (candidate <= baseLimit + offset)
                return Math.Min(best, _lookahead);
        }

        while (candidate < _position)
        {
            int start = candidate - offset;
            if (CanImprove(start, best))
            {
                int length = CommonLength(_position, start);
                if (length > best)
                {
                    _matchStart = start;
                    if (length > _lookahead)
                        return _lookahead;
                    best = length;
                    if (best >= settings.Nice)
                        return best;
                    if (_level == 9 && length > DeflateCodes.MinimumLength && start + length < _position)
                    {
                        if (!FindLongerChain(dictionary, start, length, baseLimit, out candidate, out offset))
                            return Math.Min(best, _lookahead);
                        continue;
                    }
                }
                else if (_level < 5)
                {
                    break;
                }
            }

            if (--chain == 0)
                break;
            candidate = dictionary.Previous(candidate);
            if (candidate <= baseLimit + offset)
                break;
        }
        return best;
    }

    private bool FindLongerChain(DeflateDictionary dictionary, int start, int length, int baseLimit, out int candidate, out int offset)
    {
        candidate = start;
        offset = 0;
        int count = length - DeflateCodes.MinimumLength + 1;
        if (DeflateDictionary.CanFindMinimumPrevious && count >= 24)
        {
            offset = dictionary.FindMinimumPrevious(start, count, ref candidate);
            // Each replacement decreases the position and increases its offset.
            // Once a candidate is too far back, subsequent replacements cannot make it valid.
            if (candidate <= baseLimit + offset)
                return false;
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                int previous = dictionary.Previous(start + i);
                if (previous < candidate)
                {
                    if (previous <= baseLimit + i)
                        return false;
                    candidate = previous;
                    offset = i;
                }
            }
        }

        int suffix = length - (DeflateCodes.MinimumLength + 1);
        int position = _position + suffix;
        int hash = DeflateDictionary.UpdateHash(_window[position], _window[position + 1]);
        int head = dictionary.Head(DeflateDictionary.UpdateHash(hash, _window[position + 2]));
        if (head < candidate)
        {
            candidate = head;
            offset = suffix;
        }
        return candidate > baseLimit + offset;
    }

    private bool CanImprove(int candidate, int best)
    {
        ReadOnlySpan<byte> previous = _window.AsSpan(candidate);
        ReadOnlySpan<byte> current = _window.AsSpan(_position);
        if (best < sizeof(ulong))
        {
            // For short matches, the two checks cover the first best + 1 bytes.
            ulong difference = BinaryPrimitives.ReadUInt64LittleEndian(previous) ^ BinaryPrimitives.ReadUInt64LittleEndian(current);
            return (difference << (8 * (sizeof(ulong) - 1 - best))) == 0;
        }

        int offset = best - (sizeof(ulong) - 1);
        return BinaryPrimitives.ReadUInt64LittleEndian(previous[offset..]) == BinaryPrimitives.ReadUInt64LittleEndian(current[offset..]) &&
            BinaryPrimitives.ReadUInt64LittleEndian(previous) == BinaryPrimitives.ReadUInt64LittleEndian(current);
    }

    private int CommonLength(int left, int right)
    {
        const int maximum = DeflateCodes.MaximumLength;
        ReadOnlySpan<byte> first = _window.AsSpan(left, maximum);
        ReadOnlySpan<byte> second = _window.AsSpan(right, maximum);
        ulong difference = BinaryPrimitives.ReadUInt64LittleEndian(first) ^ BinaryPrimitives.ReadUInt64LittleEndian(second);
        if (difference != 0)
            return BitOperations.TrailingZeroCount(difference) / 8;

        int offset = sizeof(ulong);
        if (Vector256.IsHardwareAccelerated)
        {
            for (; offset <= maximum - Vector256<byte>.Count; offset += Vector256<byte>.Count)
            {
                Vector256<byte> equal = Vector256.Equals(Vector256.Create(first[offset..]), Vector256.Create(second[offset..]));
                uint unequal = ~equal.ExtractMostSignificantBits();
                if (unequal != 0)
                    return offset + BitOperations.TrailingZeroCount(unequal);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; offset <= maximum - Vector128<byte>.Count; offset += Vector128<byte>.Count)
            {
                Vector128<byte> equal = Vector128.Equals(Vector128.Create(first[offset..]), Vector128.Create(second[offset..]));
                uint unequal = equal.ExtractMostSignificantBits() ^ 0xffff;
                if (unequal != 0)
                    return offset + BitOperations.TrailingZeroCount(unequal);
            }
        }

        for (; offset <= maximum - sizeof(ulong); offset += sizeof(ulong))
        {
            difference = BinaryPrimitives.ReadUInt64LittleEndian(first[offset..]) ^
                BinaryPrimitives.ReadUInt64LittleEndian(second[offset..]);
            if (difference != 0)
                return offset + BitOperations.TrailingZeroCount(difference) / 8;
        }
        while (offset < maximum && first[offset] == second[offset])
            offset++;
        return offset;
    }
}
