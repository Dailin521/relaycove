using System.Text;
using System.Text.RegularExpressions;

namespace RelayCove.App.ViewModels;

public sealed record MessageQuote(string Sender, string Body, string Remainder, string? Permalink)
{
    public static string Build(MessageItem message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var raw = string.IsNullOrWhiteSpace(message.Content) ? "消息" : message.Content.Trim();
        var longest = Regex.Matches(raw, "`+").Select(match => match.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        var escapedSender = message.Sender
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
        var header = message.Permalink is not null && message.SenderId is { } senderId
            ? $"@_**{escapedSender}|{senderId}** [said]({message.Permalink}):"
            : $"**{escapedSender}** [said](#):";
        return $"{header}\n{fence}quote\n{raw}\n{fence}\n\n";
    }

    public static MessageQuote? ParseLeading(string content)
    {
        if (!TryParseLeadingBlock(NormalizeNewlines(content), out var quote, out _)) return null;
        return quote;
    }

    public static IReadOnlyList<MessageQuote> ParseLeadingSequence(string content, out string remainder)
    {
        if (string.IsNullOrEmpty(content))
        {
            remainder = content;
            return [];
        }

        var source = NormalizeNewlines(content);
        var quotes = new List<MessageQuote>();
        while (TryParseLeadingBlock(source, out var quote, out var next))
        {
            quotes.Add(quote);
            source = next.TrimStart('\n');
        }

        remainder = quotes.Count == 0 ? content : source;
        return quotes;
    }

    private static bool TryParseLeadingBlock(
        string content,
        out MessageQuote quote,
        out string remainder)
    {
        quote = null!;
        remainder = content;
        if (string.IsNullOrEmpty(content)) return false;
        using var reader = new StringReader(content);
        var header = reader.ReadLine();
        var opening = reader.ReadLine();
        if (header is null || opening is null || !opening.EndsWith("quote", StringComparison.Ordinal)) return false;
        var fence = opening[..^"quote".Length].TrimEnd();
        if (fence.Length < 3 || fence.Any(character => character != '`')) return false;
        var senderMatch = Regex.Match(
            header,
            "^(?:@_\\*\\*(?<mention>(?:\\\\.|[^*\\r\\n])*)\\|[^*\\r\\n]+\\*\\*|\\*\\*(?<plain>(?:\\\\.|[^*\\r\\n])*)\\*\\*)\\s+\\[said\\]\\((?<link>[^)\\r\\n]+)\\):$",
            RegexOptions.CultureInvariant);
        if (!senderMatch.Success) return false;

        var quoted = new StringBuilder();
        var inlineRemainder = string.Empty;
        string? line;
        var closed = false;
        while ((line = reader.ReadLine()) is not null)
        {
            var fenceIndex = line.IndexOf(fence, StringComparison.Ordinal);
            if (fenceIndex >= 0)
            {
                closed = true;
                inlineRemainder = string.Concat(
                    line.AsSpan(0, fenceIndex),
                    line.AsSpan(fenceIndex + fence.Length));
                break;
            }
            if (quoted.Length > 0) quoted.Append('\n');
            quoted.Append(line);
        }
        if (!closed) return false;

        var trailing = reader.ReadToEnd().TrimStart('\r', '\n');
        remainder = inlineRemainder.Length == 0
            ? trailing
            : trailing.Length == 0
                ? inlineRemainder
                : $"{inlineRemainder}\n{trailing}";
        var sender = (senderMatch.Groups["mention"].Success
                ? senderMatch.Groups["mention"].Value
                : senderMatch.Groups["plain"].Value)
            .Replace("\\|", "|", StringComparison.Ordinal)
            .Replace("\\*", "*", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
        var link = senderMatch.Groups["link"].Value;
        quote = new MessageQuote(sender, quoted.ToString().Trim(), remainder, link == "#" ? null : link);
        return true;
    }

    private static string NormalizeNewlines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
