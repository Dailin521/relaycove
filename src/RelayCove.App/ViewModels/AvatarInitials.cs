using System.Globalization;

namespace RelayCove.App.ViewModels;

internal static class AvatarInitials
{
    public static string Create(string? displayName, bool isBot = false)
    {
        if (isBot) return "BOT";
        if (string.IsNullOrWhiteSpace(displayName)) return "?";

        var trimmed = displayName.Trim();
        var firstElement = StringInfo.GetNextTextElement(trimmed);
        if (firstElement.Any(character => character > 127)) return firstElement;

        var firstWord = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var elements = StringInfo.GetTextElementEnumerator(firstWord);
        var initials = new List<string>(2);
        while (elements.MoveNext() && initials.Count < 2)
        {
            initials.Add(elements.GetTextElement());
        }

        return string.Concat(initials).ToUpperInvariant();
    }
}
