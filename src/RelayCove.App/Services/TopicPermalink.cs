namespace RelayCove.App.Services;

public static class TopicPermalink
{
    public static string Build(string realmOrigin, long channelId, string channelName, string topic, long? messageId = null)
    {
        var realm = new Uri(realmOrigin, UriKind.Absolute);
        return Build(realm, channelId, channelName, topic, messageId);
    }

    public static string Build(Uri realm, long channelId, string channelName, string topic, long? messageId = null)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        ArgumentNullException.ThrowIfNull(topic);
        var slug = EncodeHashComponent($"{channelId}-{channelName.Replace(' ', '-')}");
        var hash = $"#narrow/channel/{slug}/topic/{EncodeHashComponent(topic)}";
        if (messageId is > 0) hash += $"/with/{messageId}";
        return new Uri(realm, hash).AbsoluteUri;
    }

    public static string EncodeHashComponent(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Zulip's hash encoder escapes literal dots and uses dots rather than percent signs.
        var escaped = Uri.EscapeDataString(value);
        var builder = new System.Text.StringBuilder(escaped.Length);
        for (var index = 0; index < escaped.Length; index++)
        {
            var character = escaped[index];
            if (character == '%' && index + 2 < escaped.Length)
            {
                builder.Append('.').Append(escaped[index + 1]).Append(escaped[index + 2]);
                index += 2;
            }
            else if (character == '.') builder.Append(".2E");
            else if (character == '(') builder.Append(".28");
            else if (character == ')') builder.Append(".29");
            else builder.Append(character);
        }
        return builder.ToString();
    }
}
