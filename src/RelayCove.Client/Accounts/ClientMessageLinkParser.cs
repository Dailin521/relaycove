using System.Globalization;

namespace RelayCove.Client.Accounts;

internal static class ClientMessageLinkParser
{
    internal const int MaxLinksPerMessage = 8;
    internal const int MaxLinkLength = 2048;

    public static IReadOnlyList<ClientMessageLinkPresentation> Parse(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Array.Empty<ClientMessageLinkPresentation>();
        }

        var links = new List<ClientMessageLinkPresentation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var searchStart = 0;
        while (searchStart < content.Length && links.Count < MaxLinksPerMessage)
        {
            var linkStart = FindNextScheme(content, searchStart);
            if (linkStart < 0)
            {
                break;
            }

            var linkEnd = linkStart;
            while (linkEnd < content.Length && !IsLinkDelimiter(content[linkEnd]))
            {
                linkEnd++;
            }

            var candidateLength = TrimTrailingPunctuation(
                content.AsSpan(linkStart, linkEnd - linkStart));
            if (candidateLength > 0 && candidateLength <= MaxLinkLength)
            {
                var displayText = content.Substring(linkStart, candidateLength);
                if (TryNormalizeAbsoluteHttpLink(displayText, out var absoluteUri) &&
                    seen.Add(absoluteUri))
                {
                    links.Add(new ClientMessageLinkPresentation(displayText, absoluteUri));
                }
            }

            searchStart = Math.Max(linkEnd, linkStart + 1);
        }

        return links.AsReadOnly();
    }

    internal static bool TryNormalizeAbsoluteHttpLink(
        string? candidate,
        out string absoluteUri)
    {
        absoluteUri = string.Empty;
        if (string.IsNullOrEmpty(candidate) ||
            candidate.Length > MaxLinkLength ||
            candidate.Contains('\\', StringComparison.Ordinal) ||
            candidate.Any(character =>
                char.IsWhiteSpace(character) ||
                char.IsControl(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format) ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var normalized = uri.AbsoluteUri;
        if (normalized.Length > MaxLinkLength)
        {
            return false;
        }

        absoluteUri = normalized;
        return true;
    }

    private static int FindNextScheme(string content, int searchStart)
    {
        for (var index = searchStart; index < content.Length; index++)
        {
            if (content[index] is not ('h' or 'H') ||
                index != 0 && !IsValidLeadingBoundary(content[index - 1]))
            {
                continue;
            }

            var remaining = content.AsSpan(index);
            if (remaining.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                remaining.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsValidLeadingBoundary(char character) =>
        !IsAsciiLetterOrDigit(character) &&
        character is not '_' and not '+' and not '-' and not '.' and not ':' and not '/' and
            not '@';

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9';

    private static bool IsLinkDelimiter(char character) =>
        char.IsWhiteSpace(character) ||
        char.IsControl(character) ||
        character is '"' or '\'' or '<' or '>' or '“' or '”' or '‘' or '’';

    private static int TrimTrailingPunctuation(ReadOnlySpan<char> candidate)
    {
        var length = candidate.Length;
        var roundBalance = 0;
        var squareBalance = 0;
        var braceBalance = 0;
        var fullWidthRoundBalance = 0;
        var fullWidthSquareBalance = 0;
        foreach (var character in candidate)
        {
            UpdateBalance(character, '(', ')', ref roundBalance);
            UpdateBalance(character, '[', ']', ref squareBalance);
            UpdateBalance(character, '{', '}', ref braceBalance);
            UpdateBalance(character, '（', '）', ref fullWidthRoundBalance);
            UpdateBalance(character, '【', '】', ref fullWidthSquareBalance);
        }

        while (length > 0)
        {
            var last = candidate[length - 1];
            if (last is '.' or ',' or '!' or '?' or ';' or ':' or
                '，' or '。' or '！' or '？' or '；' or '：' or '、' or '…')
            {
                length--;
                continue;
            }

            if (last == ')' && roundBalance < 0)
            {
                length--;
                roundBalance++;
                continue;
            }

            if (last == ']' && squareBalance < 0)
            {
                length--;
                squareBalance++;
                continue;
            }

            if (last == '}' && braceBalance < 0)
            {
                length--;
                braceBalance++;
                continue;
            }

            if (last == '）' && fullWidthRoundBalance < 0)
            {
                length--;
                fullWidthRoundBalance++;
                continue;
            }

            if (last == '】' && fullWidthSquareBalance < 0)
            {
                length--;
                fullWidthSquareBalance++;
                continue;
            }

            break;
        }

        return length;
    }

    private static void UpdateBalance(
        char value,
        char opening,
        char closing,
        ref int balance) =>
        balance += value == opening ? 1 : value == closing ? -1 : 0;
}
