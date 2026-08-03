using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Notifications;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Notifications;

public sealed class WindowsClientNotificationPlatformTests
{
    private const string AccountScopeId =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid ConversationId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-03T20:00:00Z");

    [Fact]
    public async Task SubmitPerMessage_WhenEnabled_BuildsStableNativeEnvelope()
    {
        var manager = new FakeWindowsAppNotificationManager();
        var platform = CreatePlatform(manager);
        var request = new ClientNotificationRequest(
            AccountScopeId,
            NotificationPolicy.PerMessage,
            [CreateMessage(42)]);

        Assert.Equal(
            ClientNotificationPlatformAvailability.Available,
            platform.GetSettingsSnapshot().PlatformAvailability);

        var result = await platform.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(ClientNotificationPlatformStatus.Accepted, result.Status);
        var notification = Assert.Single(manager.Shown);
        Assert.Equal("42", notification.Tag);
        Assert.Equal(
            "xzXuM4KwnuCXvzCWGT65Ptnpk0W6_yV678dFK5IRi20",
            notification.Group);
        Assert.Equal(Now.AddDays(3), notification.Expiration);
        Assert.True(notification.ExpiresOnReboot);
        Assert.Contains("Conversation", notification.Title, StringComparison.Ordinal);
        Assert.Contains("sensitive body", notification.Body, StringComparison.Ordinal);
        Assert.True(WindowsNotificationActivationCodec.TryDecode(
            Serialize(notification.ActivationArguments),
            out var target));
        Assert.Equal(ClientNotificationActivationKind.Message, target!.Kind);
        Assert.Equal(AccountScopeId, target.AccountScopeId);
        Assert.Equal(ConversationId, target.ConversationId);
        Assert.Equal(42, target.MessageId);
        Assert.DoesNotContain(AccountScopeId, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive body", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive body", notification.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitSummary_WhenEnabled_UsesUnreadOverviewAndFixedIdentity()
    {
        var manager = new FakeWindowsAppNotificationManager();
        var platform = CreatePlatform(manager);

        var result = await platform.SubmitAsync(
            new ClientNotificationRequest(
                AccountScopeId,
                NotificationPolicy.Summary,
                [CreateMessage(1), CreateMessage(2)]),
            CancellationToken.None);

        Assert.Equal(ClientNotificationPlatformStatus.Accepted, result.Status);
        var notification = Assert.Single(manager.Shown);
        Assert.Equal(WindowsNotificationIdentity.SummaryTag, notification.Tag);
        Assert.Equal(
            "ixtbwSB8U_2_R3Yb4lTASV38xQVX5opLhGkUGXOymEY",
            notification.Group);
        Assert.Equal("2 条未读消息", notification.Body);
        Assert.True(WindowsNotificationActivationCodec.TryDecode(
            Serialize(notification.ActivationArguments),
            out var target));
        Assert.Equal(ClientNotificationActivationKind.UnreadOverview, target!.Kind);
        Assert.Null(target.ConversationId);
        Assert.Null(target.MessageId);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task Submit_WhenPlatformIsUnsupportedOrDisabled_ClassifiesRecoverySemantics(
        bool isSupported,
        int setting)
    {
        var manager = new FakeWindowsAppNotificationManager
        {
            IsSupportedValue = isSupported,
            Setting = (WindowsClientNotificationSetting)setting,
        };
        var platform = CreatePlatform(manager);

        var result = await platform.SubmitAsync(
            new ClientNotificationRequest(
                AccountScopeId,
                NotificationPolicy.PerMessage,
                [CreateMessage(1)]),
            CancellationToken.None);

        Assert.Equal(
            isSupported
                ? ClientNotificationPlatformStatus.PermanentlyUnavailable
                : ClientNotificationPlatformStatus.TransientFailure,
            result.Status);
        Assert.Empty(manager.Shown);
    }

    [Fact]
    public async Task Submit_WhenManagerThrowsUnknownError_IsTransientAndLogsNoPayload()
    {
        var manager = new FakeWindowsAppNotificationManager
        {
            ShowException = new IOException("sensitive body"),
        };
        var logger = new RecordingLogger<WindowsClientNotificationPlatform>();
        var platform = CreatePlatform(manager, logger);

        var result = await platform.SubmitAsync(
            new ClientNotificationRequest(
                AccountScopeId,
                NotificationPolicy.PerMessage,
                [CreateMessage(1)]),
            CancellationToken.None);

        Assert.Equal(ClientNotificationPlatformStatus.TransientFailure, result.Status);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("sensitive body", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains(AccountScopeId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Submit_WhenRuntimeClassIsMissing_IsTransientForPostInstallRecovery()
    {
        var manager = new FakeWindowsAppNotificationManager
        {
            ShowException = new COMException(
                "class not registered",
                unchecked((int)0x80040154)),
        };
        var platform = CreatePlatform(manager);

        var result = await platform.SubmitAsync(
            new ClientNotificationRequest(
                AccountScopeId,
                NotificationPolicy.PerMessage,
                [CreateMessage(1)]),
            CancellationToken.None);

        Assert.Equal(ClientNotificationPlatformStatus.TransientFailure, result.Status);
    }

    [Fact]
    public async Task Submit_WhenNativeShowDoesNotComplete_TimesOutAndBlocksSameIdentityRetry()
    {
        var never = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new FakeWindowsAppNotificationManager
        {
            ShowAction = (_, _) => never.Task,
        };
        var platform = CreatePlatform(
            manager,
            nativeSubmissionTimeout: TimeSpan.FromMilliseconds(20));
        var request = new ClientNotificationRequest(
            AccountScopeId,
            NotificationPolicy.PerMessage,
            [CreateMessage(1)]);

        var first = await platform.SubmitAsync(request, CancellationToken.None);
        var retry = await platform.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(ClientNotificationPlatformStatus.TransientFailure, first.Status);
        Assert.Equal(ClientNotificationPlatformStatus.TransientFailure, retry.Status);
        Assert.Equal(1, manager.ShowCount);
    }

    [Fact]
    public async Task Submit_WhenCanceledDuringNativeShow_CleansLateToastBeforeRetryingIdentity()
    {
        var show = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new FakeWindowsAppNotificationManager
        {
            ShowAction = (_, _) => show.Task,
        };
        var platform = CreatePlatform(manager);
        var request = new ClientNotificationRequest(
            AccountScopeId,
            NotificationPolicy.PerMessage,
            [CreateMessage(1)]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            platform.SubmitAsync(request, cancellation.Token));
        show.SetResult();
        await WaitUntilAsync(() => manager.RemovedTagGroups.Count == 1);
        var retry = await platform.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(
            [("1", "xzXuM4KwnuCXvzCWGT65Ptnpk0W6_yV678dFK5IRi20")],
            manager.RemovedTagGroups);
        Assert.Equal(ClientNotificationPlatformStatus.Accepted, retry.Status);
        Assert.Equal(2, manager.ShowCount);
    }

    [Fact]
    public async Task ClearConversation_WhenSupported_RemovesOnlyStableScopedGroup()
    {
        var manager = new FakeWindowsAppNotificationManager
        {
            Setting = WindowsClientNotificationSetting.Disabled,
        };
        var platform = CreatePlatform(manager);

        var result = await platform.ClearConversationAsync(
            AccountScopeId,
            ConversationId,
            CancellationToken.None);

        Assert.Equal(ClientNotificationPlatformStatus.Accepted, result.Status);
        Assert.Equal(
            ["xzXuM4KwnuCXvzCWGT65Ptnpk0W6_yV678dFK5IRi20"],
            manager.RemovedGroups);
    }

    [Fact]
    public async Task ClearSummary_WhenSupported_RemovesOnlyScopedSummaryIdentity()
    {
        var manager = new FakeWindowsAppNotificationManager();
        var platform = CreatePlatform(manager);

        var result = await platform.ClearSummaryAsync(
            AccountScopeId,
            CancellationToken.None);

        Assert.Equal(ClientNotificationPlatformStatus.Accepted, result.Status);
        Assert.Equal(
            [(WindowsNotificationIdentity.SummaryTag,
                "ixtbwSB8U_2_R3Yb4lTASV38xQVX5opLhGkUGXOymEY")],
            manager.RemovedTagGroups);
    }

    [Fact]
    public async Task ClearConversation_WhenPlatformIsUnsupported_RemainsPending()
    {
        var manager = new FakeWindowsAppNotificationManager
        {
            IsSupportedValue = false,
        };
        var platform = CreatePlatform(manager);

        var result = await platform.ClearConversationAsync(
            AccountScopeId,
            ConversationId,
            CancellationToken.None);

        Assert.Equal(ClientNotificationPlatformStatus.TransientFailure, result.Status);
        Assert.Empty(manager.RemovedGroups);
    }

    [Fact]
    public async Task ClearConversation_WhenNativeRemovalDoesNotComplete_TimesOutTransiently()
    {
        var never = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new FakeWindowsAppNotificationManager
        {
            RemoveAction = (_, _) => never.Task,
        };
        var platform = CreatePlatform(
            manager,
            nativeRemovalTimeout: TimeSpan.FromMilliseconds(20));

        var result = await platform.ClearConversationAsync(
            AccountScopeId,
            ConversationId,
            CancellationToken.None);

        Assert.Equal(ClientNotificationPlatformStatus.TransientFailure, result.Status);
    }

    [Fact]
    public async Task ClearConversation_WhenCallerCancels_DoesNotConvertCancellationToSuccess()
    {
        var never = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new FakeWindowsAppNotificationManager
        {
            RemoveAction = (_, _) => never.Task,
        };
        var platform = CreatePlatform(manager);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            platform.ClearConversationAsync(
                AccountScopeId,
                ConversationId,
                cancellation.Token));
    }

    [Fact]
    public void GetSettingsSnapshot_WhenReadingManagerFails_ReturnsTransientUnavailable()
    {
        var manager = new FakeWindowsAppNotificationManager
        {
            IsSupportedException = new COMException("probe failed"),
        };
        var platform = CreatePlatform(manager);

        var settings = platform.GetSettingsSnapshot();

        Assert.Equal(
            ClientNotificationPlatformAvailability.Unavailable,
            settings.PlatformAvailability);
    }

    [Fact]
    public void GetSettingsSnapshot_WhenCacheIsFresh_DoesNotRepeatNativeProbe()
    {
        var manager = new FakeWindowsAppNotificationManager();
        var platform = CreatePlatform(manager);

        var first = platform.GetSettingsSnapshot();
        var second = platform.GetSettingsSnapshot();

        Assert.Equal(ClientNotificationSettingsSnapshot.Enabled, first);
        Assert.Equal(first, second);
        Assert.Equal(1, manager.IsSupportedCount);
    }

    [Fact]
    public void GetSettingsSnapshot_WhenNativeProbeIsSlow_ReturnsBoundedCachedUnavailable()
    {
        var manager = new FakeWindowsAppNotificationManager
        {
            IsSupportedAction = () =>
            {
                Thread.Sleep(100);
                return true;
            },
        };
        var platform = CreatePlatform(
            manager,
            settingsProbeTimeout: TimeSpan.FromMilliseconds(20));
        var startedAt = DateTime.UtcNow;

        var first = platform.GetSettingsSnapshot();
        var second = platform.GetSettingsSnapshot();

        Assert.Equal(
            ClientNotificationPlatformAvailability.Unavailable,
            first.PlatformAvailability);
        Assert.Equal(
            ClientNotificationPlatformAvailability.Unavailable,
            second.PlatformAvailability);
        Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(1));
        Assert.Equal(1, manager.IsSupportedCount);
    }

    private static WindowsClientNotificationPlatform CreatePlatform(
        FakeWindowsAppNotificationManager manager,
        RecordingLogger<WindowsClientNotificationPlatform>? logger = null,
        TimeSpan? nativeSubmissionTimeout = null,
        TimeSpan? nativeRemovalTimeout = null,
        TimeSpan? settingsProbeTimeout = null) =>
        new(
            manager,
            logger ?? new RecordingLogger<WindowsClientNotificationPlatform>(),
            new FixedTimeProvider(),
            nativeSubmissionTimeout,
            nativeRemovalTimeout,
            settingsCacheDuration: null,
            settingsProbeTimeout);

    private static ClientNotificationMessage CreateMessage(long messageId) =>
        new(
            messageId,
            ConversationId,
            ConversationType.Direct,
            "Conversation",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "Sender",
            MessageType.Text,
            "sensitive body",
            Now);

    private static string Serialize(
        IReadOnlyList<KeyValuePair<string, string>> arguments) =>
        string.Join('&', arguments.Select(pair => pair.Key + "=" + pair.Value));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected native cleanup was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeWindowsAppNotificationManager : IWindowsAppNotificationManager
    {
        private readonly ConcurrentQueue<WindowsClientNotification> shown = new();
        private readonly ConcurrentQueue<string> removedGroups = new();
        private readonly ConcurrentQueue<(string Tag, string Group)> removedTagGroups = new();
        private int showCount;
        private int isSupportedCount;

        public event Action<string>? NotificationInvoked;

        public bool IsSupportedValue { get; init; } = true;

        public Exception? IsSupportedException { get; init; }

        public Func<bool>? IsSupportedAction { get; init; }

        public WindowsClientNotificationSetting Setting { get; init; } =
            WindowsClientNotificationSetting.Enabled;

        public Exception? ShowException { get; init; }

        public Func<WindowsClientNotification, CancellationToken, Task>? ShowAction
        {
            get;
            init;
        }

        public Func<string, CancellationToken, Task>? RemoveAction { get; init; }

        public IReadOnlyCollection<WindowsClientNotification> Shown => shown.ToArray();

        public IReadOnlyCollection<string> RemovedGroups => removedGroups.ToArray();

        public IReadOnlyCollection<(string Tag, string Group)> RemovedTagGroups =>
            removedTagGroups.ToArray();

        public int ShowCount => Volatile.Read(ref showCount);

        public int IsSupportedCount => Volatile.Read(ref isSupportedCount);

        public bool IsSupported()
        {
            Interlocked.Increment(ref isSupportedCount);
            if (IsSupportedException is not null)
            {
                throw IsSupportedException;
            }

            return IsSupportedAction?.Invoke() ?? IsSupportedValue;
        }

        public void Register()
        {
        }

        public string? GetCurrentActivationArgument() => null;

        public void Unregister()
        {
        }

        public async Task ShowAsync(
            WindowsClientNotification notification,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref showCount);
            if (ShowException is not null)
            {
                throw ShowException;
            }

            shown.Enqueue(notification);
            if (ShowAction is not null)
            {
                await ShowAction(notification, cancellationToken);
            }
        }

        public async Task RemoveByGroupAsync(
            string group,
            CancellationToken cancellationToken)
        {
            removedGroups.Enqueue(group);
            if (RemoveAction is not null)
            {
                await RemoveAction(group, cancellationToken);
            }
        }

        public Task RemoveByTagAndGroupAsync(
            string tag,
            string group,
            CancellationToken cancellationToken)
        {
            removedTagGroups.Enqueue((tag, group));
            return Task.CompletedTask;
        }

        public void Raise(string argument) => NotificationInvoked?.Invoke(argument);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(formatter(state, exception));
    }
}
