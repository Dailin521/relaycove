using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RelayCove.Client.Notifications;

internal static class WindowsNotificationIdentity
{
    public const string SummaryTag = "unread-summary";
    private const int AccountScopeIdLength = 43;

    public static string GetConversationGroup(string accountScopeId, Guid conversationId)
    {
        ValidateAccountScopeId(accountScopeId);
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A notification conversation ID cannot be empty.",
                nameof(conversationId));
        }

        return Hash(
            accountScopeId +
            "\n" +
            conversationId.ToString("D").ToLowerInvariant());
    }

    public static string GetSummaryGroup(string accountScopeId)
    {
        ValidateAccountScopeId(accountScopeId);
        return Hash(accountScopeId + "\nsummary");
    }

    public static string GetMessageTag(long messageId)
    {
        if (messageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(messageId));
        }

        return messageId.ToString(CultureInfo.InvariantCulture);
    }

    public static bool IsValidAccountScopeId(string? accountScopeId)
    {
        if (accountScopeId is not { Length: AccountScopeIdLength } ||
            !accountScopeId.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(
                accountScopeId.Replace('-', '+').Replace('_', '/') + "=");
            return bytes.Length == SHA256.HashSizeInBytes &&
                string.Equals(
                    Convert.ToBase64String(bytes)
                        .TrimEnd('=')
                        .Replace('+', '-')
                        .Replace('/', '_'),
                    accountScopeId,
                    StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static void ValidateAccountScopeId(string? accountScopeId)
    {
        if (!IsValidAccountScopeId(accountScopeId))
        {
            throw new ArgumentException(
                "The account scope ID is not a canonical RelayCove scope identifier.",
                nameof(accountScopeId));
        }
    }

    private static string Hash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
