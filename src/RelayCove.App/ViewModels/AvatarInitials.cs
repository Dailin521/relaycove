using System.Globalization;

namespace RelayCove.App.ViewModels;

internal static class AvatarInitials
{
    public static string Create(string? displayName, bool isBot = false)
    {
        if (isBot) return "BOT";
        if (string.IsNullOrWhiteSpace(displayName)) return "?";

        return StringInfo.GetNextTextElement(displayName.Trim()).ToUpperInvariant();
    }
}
