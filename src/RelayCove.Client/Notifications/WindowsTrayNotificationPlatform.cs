using System.Text;
using RelayCove.Client.Desktop;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Notifications;

internal sealed class WindowsTrayNotificationPlatform : IClientNotificationPlatform
{
    private readonly Func<ClientTrayHost?> getTrayHost;

    public WindowsTrayNotificationPlatform(Func<ClientTrayHost?> getTrayHost)
    {
        this.getTrayHost = getTrayHost ?? throw new ArgumentNullException(nameof(getTrayHost));
    }

    public bool IsAvailable => getTrayHost()?.IsAvailable == true;

    public Task<ClientNotificationPlatformResult> SubmitAsync(
        ClientNotificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (title, message) = Format(request);
        var accepted = getTrayHost()?.TryShowNotification(title, message) == true;
        return Task.FromResult(
            accepted
                ? ClientNotificationPlatformResult.Accepted
                : ClientNotificationPlatformResult.TransientFailure);
    }

    public Task<ClientNotificationPlatformResult> ClearConversationAsync(
        string accountScopeId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ClientNotificationPlatformResult.Accepted);
    }

    public Task<ClientNotificationPlatformResult> ClearSummaryAsync(
        string accountScopeId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ClientNotificationPlatformResult.Accepted);
    }

    private static (string Title, string Message) Format(ClientNotificationRequest request)
    {
        if (request.Policy == NotificationPolicy.Summary || request.Messages.Count != 1)
        {
            return ("RelayCove", $"{request.Messages.Count} 条未读消息");
        }

        var item = request.Messages[0];
        var content = string.IsNullOrWhiteSpace(item.Content) ? "新消息" : item.Content;
        return (
            Limit(item.ConversationName, 63, "RelayCove"),
            Limit(item.SenderDisplayName + ": " + content, 255, "新消息"));
    }

    private static string Limit(string? value, int maximumRunes, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes().Take(maximumRunes))
        {
            builder.Append(rune.ToString());
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }
}
