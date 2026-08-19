namespace RelayCove.Core;

public sealed record SetTopicVisibilityPolicyRequest(CredentialEnvelope Credentials, ChannelTopic Topic, TopicVisibilityPolicy Policy)
{
    public override string ToString() => "SetTopicVisibilityPolicyRequest { Credentials = [redacted], Topic = [redacted] }";
}

public sealed record MarkTopicReadRequest(CredentialEnvelope Credentials, ChannelTopic Topic, long? AnchorMessageId = null)
{
    public override string ToString() => "MarkTopicReadRequest { Credentials = [redacted], Topic = [redacted] }";
}

public sealed record TopicReadResult(long? LastProcessedMessageId, bool FoundNewest);

public sealed record ResolveTopicAnchorRequest(CredentialEnvelope Credentials, ChannelTopic Topic)
{
    public override string ToString() => "ResolveTopicAnchorRequest { Credentials = [redacted], Topic = [redacted] }";
}

public sealed record TopicAnchorResult(long? MessageId);

public sealed record MoveTopicRequest(CredentialEnvelope Credentials, ChannelTopic Source, long AnchorMessageId, ChannelTopic Destination)
{
    public override string ToString() => "MoveTopicRequest { Credentials = [redacted], Source = [redacted], Destination = [redacted] }";
}

public sealed record DeleteTopicRequest(CredentialEnvelope Credentials, ChannelTopic Topic)
{
    public override string ToString() => "DeleteTopicRequest { Credentials = [redacted], Topic = [redacted] }";
}

public sealed record TopicDeleteResult(bool Complete);
