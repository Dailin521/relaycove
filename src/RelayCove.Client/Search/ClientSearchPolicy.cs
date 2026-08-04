using System.Buffers;
using System.Globalization;
using System.Text;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Search;

internal static class ClientSearchPolicy
{
    public const int MaximumKeywordScalars = 64;
    public const int MaximumConversationNameScalars = 100;
    public const int MaximumSenderNameScalars = 100;
    public const int MaximumSnippetScalars = 160;
    public const int MaximumAttachmentFileNameScalars = 255;

    public static bool TryNormalizeKeyword(string? keyword, out string normalizedKeyword)
    {
        normalizedKeyword = string.Empty;
        if (keyword is null)
        {
            return false;
        }

        if (!IsWellFormedUtf16(keyword))
        {
            return false;
        }

        normalizedKeyword = keyword.Trim();
        return IsValidKeyword(normalizedKeyword);
    }

    public static bool IsValidKeyword(string? keyword) =>
        IsValidText(
            keyword,
            MaximumKeywordScalars,
            requireNonWhitespace: true,
            requireTrimmed: true);

    public static bool IsValidResult(SearchResultDto? result) =>
        result is not null &&
        result.MessageId > 0 &&
        result.ConversationId != Guid.Empty &&
        result.CreatedAt != default &&
        IsValidText(
            result.ConversationName,
            MaximumConversationNameScalars,
            requireNonWhitespace: true,
            requireTrimmed: false) &&
        IsValidText(
            result.SenderName,
            MaximumSenderNameScalars,
            requireNonWhitespace: true,
            requireTrimmed: false) &&
        IsValidOptionalSnippet(result.Snippet, result.MatchedAttachmentFileName) &&
        (result.MatchedAttachmentFileName is null ||
         IsValidAttachmentFileName(result.MatchedAttachmentFileName));

    private static bool IsValidOptionalSnippet(string? snippet, string? matchedAttachmentFileName) =>
        snippet is not null &&
        (snippet.Length != 0 || matchedAttachmentFileName is not null) &&
        IsValidText(
            snippet,
            MaximumSnippetScalars,
            requireNonWhitespace: snippet.Length != 0,
            requireTrimmed: false,
            allowMessageWhitespaceControls: true);

    private static bool IsValidAttachmentFileName(string fileName) =>
        IsValidText(
            fileName,
            MaximumAttachmentFileNameScalars,
            requireNonWhitespace: true,
            requireTrimmed: true) &&
        !string.Equals(fileName, ".", StringComparison.Ordinal) &&
        !string.Equals(fileName, "..", StringComparison.Ordinal) &&
        !fileName.EnumerateRunes().Any(rune => rune.Value is '/' or '\\' ||
            Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format);

    private static bool IsValidText(
        string? value,
        int maximumScalars,
        bool requireNonWhitespace,
        bool requireTrimmed,
        bool allowMessageWhitespaceControls = false)
    {
        if (value is null ||
            requireTrimmed && !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var scalarCount = 0;
        var hasNonWhitespace = false;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) !=
                OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control &&
                (!allowMessageWhitespaceControls || rune.Value is not '\t' and not '\r' and not '\n'))
            {
                return false;
            }

            scalarCount++;
            if (scalarCount > maximumScalars)
            {
                return false;
            }

            hasNonWhitespace |= !Rune.IsWhiteSpace(rune);
            remaining = remaining[consumed..];
        }

        return scalarCount != 0 && (!requireNonWhitespace || hasNonWhitespace);
    }

    private static bool IsWellFormedUtf16(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }
}
