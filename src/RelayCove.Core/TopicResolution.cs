using System.Text.RegularExpressions;

namespace RelayCove.Core;

public static partial class TopicResolution
{
    public const string ResolvedPrefix = "✔ ";

    public static bool IsResolved(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return topic.StartsWith(ResolvedPrefix, StringComparison.Ordinal);
    }

    public static string Resolve(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return IsResolved(topic) ? topic : ResolvedPrefix + topic;
    }

    public static string Unresolve(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return ResolvedPrefixPattern().Replace(topic, string.Empty, 1);
    }

    [GeneratedRegex("^✔ [ ✔]*", RegexOptions.CultureInvariant)]
    private static partial Regex ResolvedPrefixPattern();
}
