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
        int candidate = _lookahead >= WantedLength ? dictionary.Insert(_window, _position) : 0;
        if (!InRange(candidate))
            return new(_position, 0, 1, _position);

        int length = LongestMatch(dictionary, candidate, settings);
        if (length < WantedLength || _matchStart >= _position)
            length = 1;
        return new(_position, _matchStart, length, _position);
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
