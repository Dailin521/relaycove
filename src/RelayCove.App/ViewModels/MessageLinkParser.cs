using System.Text.RegularExpressions;

namespace RelayCove.App.ViewModels;

internal static partial class MessageLinkParser
{
    [GeneratedRegex("""(?<![\p{L}\p{N}_@/])(?:https?://|www\.)[^\s\p{C}<>"'`\\，。！？；：、（）【】]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebUrl();

    internal static IReadOnlyList<(string Text, Uri? Uri)> Split(string source)
    {
        var parts = new List<(string Text, Uri? Uri)>();
        var offset = 0;
        foreach (Match match in WebUrl().Matches(source))
        {
            var text = TrimTrailingPunctuation(match.Value);
            var target = text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + text : text;
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
                !uri.IsWellFormedOriginalString()) continue;

            if (match.Index > offset) parts.Add((source[offset..match.Index], null));
            parts.Add((text, uri));
            offset = match.Index + text.Length;
        }
        if (offset < source.Length) parts.Add((source[offset..], null));
        return parts;
    }

    private static string TrimTrailingPunctuation(string text)
    {
        while (text.Length > 0)
        {
            var last = text[^1];
            var opening = last switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
            if (last is '.' or ',' or '!' or '?' or ';' or ':' ||
                opening != '\0' && text.Count(character => character == last) > text.Count(character => character == opening))
            {
                text = text[..^1];
                continue;
            }
            break;
        }
        return text;
    }
}
