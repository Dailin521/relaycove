using RelayCove.Core;

namespace RelayCove.App.Services;

public sealed class AvatarChangedEventArgs(AccountId accountId, string sourceUrl) : EventArgs
{
    public AccountId AccountId { get; } = accountId;
    public string SourceUrl { get; } = sourceUrl;
}
