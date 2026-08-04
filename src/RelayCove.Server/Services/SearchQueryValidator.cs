using System.Buffers;
using System.Globalization;
using System.Text;

namespace RelayCove.Server.Services;

public sealed class SearchQueryValidator
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 50;
    public const int MaximumKeywordLength = 64;

    public IReadOnlyDictionary<string, string[]> Validate(string? keyword, int? limit)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (keyword is null || !IsValidNormalizedKeyword(NormalizeKeyword(keyword)))
        {
            errors["keyword"] =
                [$"The keyword must contain 1 to {MaximumKeywordLength} valid characters."];
        }

        if (limit is < 1 or > MaximumLimit)
        {
            errors["limit"] = [$"The limit must be between 1 and {MaximumLimit}."];
        }

        return errors;
    }

    public static string NormalizeKeyword(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        return keyword.Trim();
    }

    public static bool IsValidNormalizedKeyword(string? keyword)
    {
        if (string.IsNullOrEmpty(keyword) ||
            !string.Equals(keyword, keyword.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var scalarCount = 0;
        var hasNonWhitespace = false;
        var remaining = keyword.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) !=
                OperationStatus.Done)
            {
                return false;
            }

            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                return false;
            }

            scalarCount++;
            if (scalarCount > MaximumKeywordLength)
            {
                return false;
            }

            hasNonWhitespace |= !Rune.IsWhiteSpace(rune);
            remaining = remaining[consumed..];
        }

        return hasNonWhitespace;
    }
}
