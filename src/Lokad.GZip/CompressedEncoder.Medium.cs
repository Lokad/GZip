namespace Lokad.GZip;

// Managed adaptation of zlib-ng's Intel medium match policy.
internal sealed partial class CompressedEncoder
{
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
            InsertMediumMatch(dictionary, current, settings);
            if (useNext && _lookahead > MinimumLookahead &&
                current.Start + current.Length < 2 * _windowSize - MinimumLookahead)
            {
                _position = current.Start + current.Length;
                next = FindMediumMatch(dictionary, settings);
                if (next.Length >= WantedLength)
                    ShiftMatch(ref current, ref next);
                _position = current.Start;
            }
            else
            {
                next = default;
            }

            bool full = false;
            if (current.Length >= WantedLength)
            {
                full = _blocks.AddMatch(current.Length, current.Start - current.Source);
            }
            else
            {
                for (int position = current.Start; position < current.Start + current.Length; position++)
                    full |= _blocks.AddLiteral(_window[position]);
            }
            Advance(current.Length);
            if (full && WriteBlock(ref output, final: false))
                return false;
        }
        PrepareNextWrite(operation);
        return true;
    }

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
        int shift = current.Length - 1;
        if (shift <= 0 || shift > next.Source || shift > next.Start ||
            _window[next.Source - shift] != _window[next.Start - shift])
            return;

        int limit = Math.Max(next.Start - MaximumDistance, 0);
        int moved = 0;
        int source = next.Source;
        int start = next.Start;
        int length = next.Length;
        while (_window[source - 1] == _window[start - 1])
        {
            if (current.Length == moved || start <= limit || length >= 256 || source <= 1)
                break;
            source--;
            start--;
            length++;
            moved++;
        }

        if (moved > 0 && current.Length - moved <= 1 && length != 2)
        {
            current = current with { Length = current.Length - moved };
            next = new(start, source, length, next.OriginalStart + 1);
        }
    }
}
