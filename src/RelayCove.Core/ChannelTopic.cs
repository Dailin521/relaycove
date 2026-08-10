using System.Text;

namespace RelayCove.Core;

public sealed record ChannelTopic : ConversationKey
{
    public ChannelTopic(long channelId, string topic)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ArgumentNullException.ThrowIfNull(topic);
        ChannelId = channelId;
        Topic = topic;
    }

    public long ChannelId { get; }
    public string Topic { get; }
    public override string CanonicalKey => $"channel:{ChannelId}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(Topic)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
}
