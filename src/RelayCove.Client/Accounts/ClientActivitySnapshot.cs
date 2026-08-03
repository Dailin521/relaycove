namespace RelayCove.Client.Accounts;

internal sealed record ClientActivitySnapshot(
    bool IsMainWindowVisible,
    bool IsMainWindowMinimized,
    bool HasForegroundFocus,
    Guid? OpenConversationId)
{
    public static ClientActivitySnapshot Inactive { get; } =
        new(false, false, false, OpenConversationId: null);

    public Guid? ForegroundConversationId =>
        IsMainWindowVisible &&
        !IsMainWindowMinimized &&
        HasForegroundFocus &&
        OpenConversationId is { } conversationId &&
        conversationId != Guid.Empty
            ? conversationId
            : null;

    public override string ToString() =>
        $"{nameof(ClientActivitySnapshot)} {{ IsMainWindowVisible = {IsMainWindowVisible}, " +
        $"IsMainWindowMinimized = {IsMainWindowMinimized}, " +
        $"HasForegroundFocus = {HasForegroundFocus}, OpenConversationId = [REDACTED] }}";
}
