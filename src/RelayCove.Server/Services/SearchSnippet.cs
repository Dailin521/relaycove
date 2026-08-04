using System.Buffers;
using System.Text;

namespace RelayCove.Server.Services;

internal static class SearchSnippet
{
    internal const int MaximumScalarLength = 160;
    private const char Ellipsis = '\u2026';

    public static string Create(
        string? content,
        string normalizedKeyword,
        bool contentMatched)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedKeyword);
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var contentRunes = DecodeRunes(content);
        if (contentRunes.Count <= MaximumScalarLength)
        {
            return content;
        }

        if (!contentMatched)
        {
            return BuildSnippet(contentRunes, 0, MaximumScalarLength - 1, includeSuffix: true);
        }

        var matchStartCharacter = FindSqliteLiteralMatch(content, normalizedKeyword);
        if (matchStartCharacter < 0)
        {
            return BuildSnippet(contentRunes, 0, MaximumScalarLength - 1, includeSuffix: true);
        }

        var matchEndCharacter = matchStartCharacter + normalizedKeyword.Length;
        var matchStartScalar = FindScalarBoundary(contentRunes, matchStartCharacter);
        var matchEndScalar = FindScalarBoundary(contentRunes, matchEndCharacter);
        if (matchStartScalar < 0 || matchEndScalar <= matchStartScalar)
        {
            return BuildSnippet(contentRunes, 0, MaximumScalarLength - 1, includeSuffix: true);
        }

        var start = matchStartScalar;
        var end = matchEndScalar;
        while (TryExpandWindow(contentRunes.Count, matchStartScalar, matchEndScalar, ref start, ref end))
        {
        }

        return BuildSnippet(
            contentRunes,
            start,
            end,
            includePrefix: start > 0,
            includeSuffix: end < contentRunes.Count);
    }

    private static bool TryExpandWindow(
        int scalarCount,
        int matchStart,
        int matchEnd,
        ref int start,
        ref int end)
    {
        var canExpandBefore = start > 0;
        var canExpandAfter = end < scalarCount;
        if (!canExpandBefore && !canExpandAfter)
        {
            return false;
        }

        var beforeContext = matchStart - start;
        var afterContext = end - matchEnd;
        var preferBefore = canExpandBefore && (!canExpandAfter || beforeContext <= afterContext);
        if (preferBefore && CanUseWindow(scalarCount, start - 1, end))
        {
            start--;
            return true;
        }

        if (canExpandAfter && CanUseWindow(scalarCount, start, end + 1))
        {
            end++;
            return true;
        }

        if (canExpandBefore && CanUseWindow(scalarCount, start - 1, end))
        {
            start--;
            return true;
        }

        return false;
    }

    private static bool CanUseWindow(int scalarCount, int start, int end) =>
        end - start + (start > 0 ? 1 : 0) + (end < scalarCount ? 1 : 0) <=
        MaximumScalarLength;

    private static int FindSqliteLiteralMatch(string content, string keyword)
    {
        if (keyword.Length > content.Length)
        {
            return -1;
        }

        for (var start = 0; start <= content.Length - keyword.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < keyword.Length; offset++)
            {
                if (!SqliteLikeCharactersEqual(content[start + offset], keyword[offset]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return start;
            }
        }

        return -1;
    }

    private static bool SqliteLikeCharactersEqual(char left, char right)
    {
        if (left == right)
        {
            return true;
        }

        return IsAsciiLetter(left) &&
            IsAsciiLetter(right) &&
            ToAsciiUpper(left) == ToAsciiUpper(right);
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static char ToAsciiUpper(char value) =>
        value is >= 'a' and <= 'z' ? (char)(value - ('a' - 'A')) : value;

    private static List<RuneSlice> DecodeRunes(string value)
    {
        var result = new List<RuneSlice>();
        var remaining = value.AsSpan();
        var characterIndex = 0;
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("Search snippet content must contain valid Unicode.", nameof(value));
            }

            result.Add(new RuneSlice(rune, characterIndex));
            remaining = remaining[consumed..];
            characterIndex += consumed;
        }

        return result;
    }

    private static int FindScalarBoundary(IReadOnlyList<RuneSlice> runes, int characterIndex)
    {
        if (runes.Count == 0)
        {
            return characterIndex == 0 ? 0 : -1;
        }

        for (var index = 0; index < runes.Count; index++)
        {
            if (runes[index].CharacterIndex == characterIndex)
            {
                return index;
            }
        }

        var final = runes[^1];
        return final.CharacterIndex + final.Value.Utf16SequenceLength == characterIndex
            ? runes.Count
            : -1;
    }

    private static string BuildSnippet(
        IReadOnlyList<RuneSlice> runes,
        int start,
        int end,
        bool includePrefix = false,
        bool includeSuffix = false)
    {
        var builder = new StringBuilder();
        if (includePrefix)
        {
            builder.Append(Ellipsis);
        }

        for (var index = start; index < end; index++)
        {
            builder.Append(runes[index].Value);
        }

        if (includeSuffix)
        {
            builder.Append(Ellipsis);
        }

        return builder.ToString();
    }

    private readonly record struct RuneSlice(Rune Value, int CharacterIndex);
}
