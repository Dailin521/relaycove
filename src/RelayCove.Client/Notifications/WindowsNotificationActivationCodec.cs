using System.Globalization;

namespace RelayCove.Client.Notifications;

internal static class WindowsNotificationActivationCodec
{
    private const int MaximumArgumentLength = 2048;
    private const string Version = "1";

    public static IReadOnlyList<KeyValuePair<string, string>> Encode(
        ClientNotificationActivationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind switch
        {
            ClientNotificationActivationKind.Message =>
            [
                KeyValuePair.Create("v", Version),
                KeyValuePair.Create("target", "message"),
                KeyValuePair.Create("account", target.AccountScopeId),
                KeyValuePair.Create(
                    "conversation",
                    target.ConversationId!.Value.ToString("D").ToLowerInvariant()),
                KeyValuePair.Create(
                    "message",
                    target.MessageId!.Value.ToString(CultureInfo.InvariantCulture)),
            ],
            ClientNotificationActivationKind.UnreadOverview =>
            [
                KeyValuePair.Create("v", Version),
                KeyValuePair.Create("target", "unread"),
                KeyValuePair.Create("account", target.AccountScopeId),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    internal static string EncodeToArgument(ClientNotificationActivationTarget target) =>
        string.Join(
            '&',
            Encode(target).Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    public static bool TryDecode(
        string? argument,
        out ClientNotificationActivationTarget? target)
    {
        target = null;
        if (string.IsNullOrEmpty(argument) ||
            argument.Length > MaximumArgumentLength ||
            argument.Contains('+', StringComparison.Ordinal))
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in argument.Split('&'))
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 ||
                separator == segment.Length - 1 ||
                segment.IndexOf('=', separator + 1) >= 0)
            {
                return false;
            }

            var encodedKey = segment[..separator];
            var encodedValue = segment[(separator + 1)..];
            if (!HasValidPercentEncoding(encodedKey) ||
                !HasValidPercentEncoding(encodedValue))
            {
                return false;
            }

            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(encodedKey);
                value = Uri.UnescapeDataString(encodedValue);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (!string.Equals(encodedKey, Uri.EscapeDataString(key), StringComparison.Ordinal) ||
                !string.Equals(
                    encodedValue,
                    Uri.EscapeDataString(value),
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!values.TryAdd(key, value))
            {
                return false;
            }
        }

        if (!values.TryGetValue("v", out var version) ||
            !string.Equals(version, Version, StringComparison.Ordinal) ||
            !values.TryGetValue("target", out var targetKind) ||
            !values.TryGetValue("account", out var accountScopeId) ||
            !WindowsNotificationIdentity.IsValidAccountScopeId(accountScopeId))
        {
            return false;
        }

        if (string.Equals(targetKind, "unread", StringComparison.Ordinal))
        {
            if (values.Count != 3)
            {
                return false;
            }

            target = ClientNotificationActivationTarget.UnreadOverview(accountScopeId);
            return true;
        }

        if (!string.Equals(targetKind, "message", StringComparison.Ordinal) ||
            values.Count != 5 ||
            !values.TryGetValue("conversation", out var conversationValue) ||
            !Guid.TryParseExact(conversationValue, "D", out var conversationId) ||
            conversationId == Guid.Empty ||
            !string.Equals(
                conversationValue,
                conversationId.ToString("D").ToLowerInvariant(),
                StringComparison.Ordinal) ||
            !values.TryGetValue("message", out var messageValue) ||
            !long.TryParse(
                messageValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var messageId) ||
            messageId <= 0 ||
            !string.Equals(
                messageValue,
                messageId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return false;
        }

        target = ClientNotificationActivationTarget.Message(
            accountScopeId,
            conversationId,
            messageId);
        return true;
    }

    private static bool HasValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length ||
                !Uri.IsHexDigit(value[index + 1]) ||
                !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }
}
