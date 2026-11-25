using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lokad.GZip;

// Managed adaptation of zlib-ng's Intel medium match policy.
internal sealed partial class CompressedEncoder
{
    private const int MaximumShiftedMatchLength = 256;

    private readonly record struct MediumMatch(int Start, int Source, int Length, int OriginalStart);

    private bool EncodeMedium(DeflateDictionary dictionary, ref ReadOnlySpan<byte> input, ref Span<byte> output, DeflateOperation operation)
    {
        SearchSettings settings = Settings;
        bool useNext = _level >= 5;
        MediumMatch next = default;
        while (true)
        {
            if (_lookahead < MinimumLookahead)
            {
                if (!HaveLookahead(ref input, operation))
                    break;
                next = default;
            }

            MediumMatch current = useNext && next.Length > 0 ? next : FindMediumMatch(dictionary, settings);
            if (useNext && current.Length == 1 && _lookahead >= MinimumLookahead + 64 && _blocks.RemainingSymbols > 1)
                current = BatchLiterals(dictionary, current, settings);
            if (useNext && current.Length == 1 && _blocks.RemainingSymbols > 1)
            {
                // With room left in the block, the next lookup can wait for the next iteration.
                _blocks.AddLiteral(_window[current.Start]);
                Advance(1);
                next = default;
                continue;
            }
            if (current.Length > 1)
            {
                if (useNext && current.Length == DeflateCodes.MaximumLength && current.Source == current.Start - 1 &&
                    _lookahead >= DeflateCodes.MaximumLength + MinimumLookahead)
                    current = BatchMediumRun(dictionary, current);
                InsertMediumMatch(dictionary, current, settings);
            }
            if (useNext && _lookahead > MinimumLookahead &&
                current.Start + current.Length < 2 * _windowSize - MinimumLookahead)
            {
                _position = current.Start + current.Length;
                next = FindMediumMatch(dictionary, settings);
                if (current.Length > 1 && next.Length is >= WantedLength and < MaximumShiftedMatchLength)
                    ShiftMatch(ref current, ref next);
                _position = current.Start;
            }
            else
            {
                next = default;
            }

            bool full;
            if (current.Length >= WantedLength)
            {
                full = _blocks.AddMatch(current.Length, current.Start - current.Source);
            }
            else
            {
                // A match shift can leave a single literal or consume the entire current match.
                Debug.Assert(current.Length is 0 or 1);
                full = current.Length == 1 && _blocks.AddLiteral(_window[current.Start]);
            }
            Advance(current.Length);
            if (full && WriteBlock(ref output, final: false))
                return false;
        }
        PrepareNextWrite(operation);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MediumMatch FindMediumMatch(DeflateDictionary dictionary, SearchSettings settings)
    {
        if (_lookahead < WantedLength)
            return new(_position, 0, 1, _position);

        uint word = BinaryPrimitives.ReadUInt32LittleEndian(_window.AsSpan(_position));
        int candidate = dictionary.InsertWord(word, _position);
        if (!InRange(candidate))
            return new(_position, 0, 1, _position);

        int length = _level >= 5
            ? SearchMediumChain(dictionary, candidate, settings, word)
            : LongestMatch(dictionary, candidate, settings);
        if (length < WantedLength || _matchStart >= _position)
            length = 1;
        return new(_position, _matchStart, length, _position);
    }

    private int SearchMediumChain(DeflateDictionary dictionary, int candidate, SearchSettings settings, uint word)
    {
        int position = _position;
        int limit = Math.Max(position - MaximumDistance, 0);
        int chain = settings.Chain;
        // Levels 5 and 6 only emit matches of four bytes or longer.
        int best = WantedLength - 1;
        ReadOnlySpan<byte> window = _window;
        do
        {
            if (window[candidate + best] == window[position + best] &&
                word == BinaryPrimitives.ReadUInt32LittleEndian(window[candidate..]))
            {
                int length = CommonLength(position, candidate);
                if (length > best)
                {
                    _matchStart = candidate;
                    if (length > _lookahead)
                        return _lookahead;
                    best = length;
                    if (best >= settings.Nice)
                        return best;
                }
            }
            candidate = dictionary.Previous(candidate);
        } while (--chain > 0 && candidate > limit && candidate < position);
        return best;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private MediumMatch BatchLiterals(DeflateDictionary dictionary, MediumMatch current, SearchSettings settings)
    {
        int start = current.Start;
        int finalSearch = 2 * _windowSize - MinimumLookahead - 1;
        // Stop with a prefetched token and leave its block slot and lookahead intact.
        int limit = Math.Min(finalSearch, start + Math.Min(_lookahead - MinimumLookahead, _blocks.RemainingSymbols - 1));
        int position = start;
        ReadOnlySpan<byte> window = _window;
        while (position < limit)
        {
            position++;
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(window[position..]);
            int candidate = dictionary.InsertWord(word, position);
            int oldest = Math.Max(position - MaximumDistance, 0);
            if (candidate <= 0 || candidate < oldest || candidate >= position)
                continue;

            int remaining = settings.Chain;
            do
            {
                // A medium match must have four equal bytes. A shorter candidate only uses a chain step.
                if (word == BinaryPrimitives.ReadUInt32LittleEndian(window[candidate..]))
                {
                    _blocks.AddLiterals(window[start..position]);
                    Advance(position - start);
                    int length = SearchMediumChain(dictionary, candidate, settings with { Chain = remaining }, word);
                    return new(position, _matchStart, length, position);
                }
                candidate = dictionary.Previous(candidate);
            } while (--remaining > 0 && candidate > oldest && candidate < position);
        }

        _blocks.AddLiterals(window[start..position]);
        Advance(position - start);
        return new(position, 0, 1, position);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private MediumMatch BatchMediumRun(DeflateDictionary dictionary, MediumMatch current)
    {
        const int length = DeflateCodes.MaximumLength;
        int lastSearch = 2 * _windowSize - MinimumLookahead - 1;
        // Leave the last match prefetched and let the normal loop handle the next refill or block boundary.
        int matches = Math.Min(_blocks.RemainingSymbols - 1,
            Math.Min((_lookahead - MinimumLookahead) / length, (lastSearch - current.Start) / length));
        if (matches <= 0)
            return current;

        // The full distance-one match already establishes the byte value of the run.
        int mismatch = _window.AsSpan(current.Start + length, matches * length).IndexOfAnyExcept(_window[current.Start]);
        if (mismatch >= 0)
            matches = mismatch / length;
        if (matches == 0)
            return current;

        dictionary.InsertRunBoundaries(_window, current.Start, matches);
        int position = current.Start + matches * length;
        _blocks.AddRepeatedMatch(length, 1, matches);
        Advance(matches * length);
        _matchStart = position - 1;
        return new(position, _matchStart, length, position);
    }

    private void InsertMediumMatch(DeflateDictionary dictionary, MediumMatch match, SearchSettings settings)
    {
        if (_lookahead <= match.Length + WantedLength)
            return;

        int end = match.Start + match.Length;
        if (match.Length <= 16 * settings.Lazy)
        {
            int start = Math.Max(match.Start + 1, match.OriginalStart);
            dictionary.Insert(_window, start, end - start);
        }
        else if (end > 0)
        {
            dictionary.Insert(_window, end - 1);
        }
    }

    private void ShiftMatch(ref MediumMatch current, ref MediumMatch next)
    {
        // A shift is useful only if it leaves at most one byte of the current match.
        int shift = current.Length - 1;
        if (shift > MaximumShiftedMatchLength - next.Length || shift > next.Source || shift > next.Start ||
            _window[next.Source - shift] != _window[next.Start - shift])
            return;

        int limit = Math.Max(next.Start - MaximumDistance, 0);
        int available = Math.Min(next.Start - limit,
            Math.Min(MaximumShiftedMatchLength - next.Length, next.Source - 1));
        if (available < shift || !_window.AsSpan(next.Source - shift, shift).SequenceEqual(_window.AsSpan(next.Start - shift, shift)))
            return;

        int moved = shift;
        if (available >= current.Length && _window[next.Source - current.Length] == _window[next.Start - current.Length])
            moved++;

        current = current with { Length = current.Length - moved };
        next = new(next.Start - moved, next.Source - moved, next.Length + moved, next.OriginalStart + 1);
    }
}
