namespace RelayCove.Core;

public sealed record SubscribeChannelResult(
    IReadOnlyList<string> Subscribed,
    IReadOnlyList<string> AlreadySubscribed,
    IReadOnlyList<string> Unauthorized)
{
    public bool Confirms(string name) =>
        Subscribed.Contains(name, StringComparer.Ordinal) ||
        AlreadySubscribed.Contains(name, StringComparer.Ordinal);
}
