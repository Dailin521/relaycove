using System.Buffers;
using System.Globalization;
using System.Text;

namespace RelayCove.Client.Storage;

internal static class ClientTextMessageContentValidator
{
    public const int MaximumScalarCount = 4_000;

    public static bool IsValid(string? content)
    {
        if (content is null)
        {
            return false;
        }

        var scalarCount = 0;
        var hasNonWhitespace = false;
        var remaining = content.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var charsConsumed);
            if (status != OperationStatus.Done)
            {
                return false;
            }

            scalarCount++;
            if (scalarCount > MaximumScalarCount)
            {
                return false;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control &&
                rune.Value is not '\t' and not '\r' and not '\n')
            {
                return false;
            }

            hasNonWhitespace |= !Rune.IsWhiteSpace(rune);
            remaining = remaining[charsConsumed..];
        }

        return scalarCount != 0 && hasNonWhitespace;
    }
}
