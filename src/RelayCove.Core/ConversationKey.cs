namespace RelayCove.Core;

public abstract record ConversationKey
{
    public abstract string CanonicalKey { get; }
}
