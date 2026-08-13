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
        if (string.IsNullOrEmpty(content)) return null;
        using var reader = new StringReader(content);
        var header = reader.ReadLine();
        var opening = reader.ReadLine();
        if (header is null || opening is null || !opening.EndsWith("quote", StringComparison.Ordinal)) return null;
        var fence = opening[..^"quote".Length].TrimEnd();
        if (fence.Length < 3 || fence.Any(character => character != '`')) return null;
        var senderMatch = Regex.Match(
            header,
            "^(?:@_\\*\\*(?<mention>(?:\\\\.|[^*\\r\\n])*)\\|[^*\\r\\n]+\\*\\*|\\*\\*(?<plain>(?:\\\\.|[^*\\r\\n])*)\\*\\*)\\s+\\[said\\]\\((?<link>[^)\\r\\n]+)\\):$",
            RegexOptions.CultureInvariant);
        if (!senderMatch.Success) return null;

        var quoted = new StringBuilder();
        string? line;
        var closed = false;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.Equals(line, fence, StringComparison.Ordinal))
            {
                closed = true;
                break;
            }
            if (quoted.Length > 0) quoted.Append('\n');
            quoted.Append(line);
        }
        if (!closed) return null;

        var remainder = reader.ReadToEnd().TrimStart('\r', '\n');
        var sender = (senderMatch.Groups["mention"].Success
                ? senderMatch.Groups["mention"].Value
                : senderMatch.Groups["plain"].Value)
            .Replace("\\|", "|", StringComparison.Ordinal)
            .Replace("\\*", "*", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
        var link = senderMatch.Groups["link"].Value;
        return new MessageQuote(sender, quoted.ToString().Trim(), remainder, link == "#" ? null : link);
    }
}
