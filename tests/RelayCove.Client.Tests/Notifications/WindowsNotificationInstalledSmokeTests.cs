using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.AppNotifications;
using RelayCove.Client.Notifications;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Notifications;

public sealed class WindowsNotificationInstalledSmokeTests
{
    private const string EnableEnvironmentVariable =
        "RELAYCOVE_WINDOWS_NOTIFICATION_SMOKE";
    private const string AccountScopeId =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task InstalledRuntime_WhenExplicitlyEnabled_RegistersShowsQueriesAndRemoves()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var conversationId = Guid.NewGuid();
        var messageId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tag = WindowsNotificationIdentity.GetMessageTag(messageId);
        var group = WindowsNotificationIdentity.GetConversationGroup(
            AccountScopeId,
            conversationId);
        var manager = WindowsAppSdkNotificationManager.Shared;
        using var host = new WindowsClientNotificationHost(
            manager,
            _ => { },
            NullLogger<WindowsClientNotificationHost>.Instance);
        Assert.True(host.TryStart());
        await AppNotificationManager.Default.RemoveAllAsync();
        var platform = new WindowsClientNotificationPlatform(
            manager,
            NullLogger<WindowsClientNotificationPlatform>.Instance);

        try
        {
            var result = await platform.SubmitAsync(
                new ClientNotificationRequest(
                    AccountScopeId,
                    NotificationPolicy.PerMessage,
                    [new ClientNotificationMessage(
                        messageId,
                        conversationId,
                        ConversationType.Direct,
                        "RelayCove 安装态验证",
                        Guid.NewGuid(),
                        "RelayCove",
                        MessageType.Text,
                        "Windows App SDK 2.3.1 通知提交与清理验证。",
                        DateTimeOffset.UtcNow)]),
                CancellationToken.None);
            Assert.Equal(ClientNotificationPlatformStatus.Accepted, result.Status);
            Assert.True(await WaitForNotificationStateAsync(
                tag,
                group,
                expectedPresent: true));

            var clear = await platform.ClearConversationAsync(
                AccountScopeId,
                conversationId,
                CancellationToken.None);
            Assert.Equal(ClientNotificationPlatformStatus.Accepted, clear.Status);
            Assert.True(await WaitForNotificationStateAsync(
                tag,
                group,
                expectedPresent: false));

            var summaryTag = WindowsNotificationIdentity.SummaryTag;
            var summaryGroup = WindowsNotificationIdentity.GetSummaryGroup(AccountScopeId);
            var summary = await platform.SubmitAsync(
                new ClientNotificationRequest(
                    AccountScopeId,
                    NotificationPolicy.Summary,
                    [
                        CreateMessage(messageId + 1, conversationId),
                        CreateMessage(messageId + 2, conversationId),
                    ]),
                CancellationToken.None);
            Assert.Equal(ClientNotificationPlatformStatus.Accepted, summary.Status);
            Assert.True(await WaitForNotificationStateAsync(
                summaryTag,
                summaryGroup,
                expectedPresent: true));

            var clearSummary = await platform.ClearSummaryAsync(
                AccountScopeId,
                CancellationToken.None);
            Assert.Equal(ClientNotificationPlatformStatus.Accepted, clearSummary.Status);
            Assert.True(await WaitForNotificationStateAsync(
                summaryTag,
                summaryGroup,
                expectedPresent: false));
        }
        finally
        {
            await AppNotificationManager.Default.RemoveAllAsync();
        }
    }

    private static ClientNotificationMessage CreateMessage(
        long messageId,
        Guid conversationId) =>
        new(
            messageId,
            conversationId,
            ConversationType.Direct,
            "RelayCove 安装态验证",
            Guid.NewGuid(),
            "RelayCove",
            MessageType.Text,
            "Windows App SDK 2.3.1 Summary 通知提交与清理验证。",
            DateTimeOffset.UtcNow);

    private static async Task<bool> WaitForNotificationStateAsync(
        string tag,
        string group,
        bool expectedPresent)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            var notifications = await AppNotificationManager.Default.GetAllAsync();
            var present = notifications.Any(notification =>
                string.Equals(notification.Tag, tag, StringComparison.Ordinal) &&
                string.Equals(notification.Group, group, StringComparison.Ordinal));
            if (present == expectedPresent)
            {
                return true;
            }

            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }
}
