namespace RelayCove.Client.Accounts;

internal sealed record ClientActivitySnapshot(
    bool IsMainWindowVisible,
    bool IsMainWindowMinimized,
    bool HasForegroundFocus,
    Guid? OpenConversationId)
{
    public static ClientActivitySnapshot Inactive { get; } =
        new(false, false, false, OpenConversationId: null);

    public bool IsMainWindowForeground =>
        IsMainWindowVisible &&
        !IsMainWindowMinimized &&
        HasForegroundFocus;

    public Guid? ForegroundConversationId =>
        IsMainWindowForeground &&
        OpenConversationId is { } conversationId &&
        conversationId != Guid.Empty
            ? conversationId
            : null;

    public override string ToString() =>
        $"{nameof(ClientActivitySnapshot)} {{ IsMainWindowVisible = {IsMainWindowVisible}, " +
        $"IsMainWindowMinimized = {IsMainWindowMinimized}, " +
        $"HasForegroundFocus = {HasForegroundFocus}, OpenConversationId = [REDACTED] }}";
}
