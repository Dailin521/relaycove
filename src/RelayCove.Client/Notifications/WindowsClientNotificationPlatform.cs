using System.Text;
using Microsoft.Extensions.Logging;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Notifications;

internal sealed class WindowsClientNotificationPlatform : IClientNotificationPlatform
{
    private readonly IWindowsAppNotificationManager manager;
    private readonly ILogger<WindowsClientNotificationPlatform> logger;
    private readonly object stateGate = new();
    private readonly TimeSpan nativeSubmissionTimeout;
    private readonly TimeSpan nativeRemovalTimeout;
    private readonly TimeSpan settingsCacheDuration;
    private readonly TimeSpan settingsProbeTimeout;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim submissionGate = new(1, 1);
    private ClientNotificationSettingsSnapshot settingsSnapshot =
        ClientNotificationSettingsSnapshot.Unavailable;
    private DateTimeOffset settingsSnapshotExpiresAt = DateTimeOffset.MinValue;
    private Task<ClientNotificationSettingsSnapshot>? settingsRefresh;
    private Task<bool>? uncertainSubmissionCleanup;
    private string? uncertainSubmissionGroup;

    public WindowsClientNotificationPlatform(
        IWindowsAppNotificationManager manager,
        ILogger<WindowsClientNotificationPlatform> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? nativeSubmissionTimeout = null,
        TimeSpan? nativeRemovalTimeout = null,
        TimeSpan? settingsCacheDuration = null,
        TimeSpan? settingsProbeTimeout = null)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.nativeSubmissionTimeout = nativeSubmissionTimeout ?? TimeSpan.FromSeconds(10);
        this.nativeRemovalTimeout = nativeRemovalTimeout ?? TimeSpan.FromSeconds(10);
        this.settingsCacheDuration = settingsCacheDuration ?? TimeSpan.FromSeconds(1);
        this.settingsProbeTimeout = settingsProbeTimeout ?? TimeSpan.FromMilliseconds(250);
        if (this.nativeSubmissionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeSubmissionTimeout));
        }

        if (this.nativeRemovalTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeRemovalTimeout));
        }

        if (this.settingsCacheDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settingsCacheDuration));
        }

        if (this.settingsProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settingsProbeTimeout));
        }
    }

    public ClientNotificationSettingsSnapshot GetSettingsSnapshot()
    {
        Task<ClientNotificationSettingsSnapshot> refresh;
        var attachCompletion = false;
        lock (stateGate)
        {
            var now = timeProvider.GetUtcNow();
            if (settingsSnapshotExpiresAt > now)
            {
                return settingsSnapshot;
            }

            if (settingsRefresh is not null)
            {
                settingsSnapshotExpiresAt = now.Add(settingsCacheDuration);
                return settingsSnapshot;
            }

            settingsRefresh = Task.Factory.StartNew(
                ReadSettingsSnapshot,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            settingsSnapshotExpiresAt = now.Add(settingsCacheDuration);
            attachCompletion = true;

            refresh = settingsRefresh;
        }

        if (attachCompletion)
        {
            _ = refresh.ContinueWith(
                static (completed, state) =>
                    ((WindowsClientNotificationPlatform)state!)
                        .CompleteSettingsRefresh(completed),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        try
        {
            refresh.Wait(settingsProbeTimeout);
        }
        catch (AggregateException)
        {
            // ReadSettingsSnapshot catches ordinary platform exceptions. A fault here is
            // treated as unavailable and is observed by the completion continuation.
        }

        if (refresh.IsCompleted)
        {
            CompleteSettingsRefresh(refresh);
        }

        lock (stateGate)
        {
            return settingsSnapshot;
        }
    }

    public async Task<ClientNotificationPlatformResult> SubmitAsync(
        ClientNotificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var notification = CreateNotification(request);
        await submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                if (uncertainSubmissionCleanup is not null)
                {
                    return ClientNotificationPlatformResult.TransientFailure;
                }
            }

            Task? submission = null;
            try
            {
                var availability = GetSettingsSnapshot().PlatformAvailability;
                if (availability == ClientNotificationPlatformAvailability.Disabled)
                {
                    return ClientNotificationPlatformResult.PermanentlyUnavailable;
                }

                if (availability != ClientNotificationPlatformAvailability.Available)
                {
                    return ClientNotificationPlatformResult.TransientFailure;
                }

                cancellationToken.ThrowIfCancellationRequested();
                submission = manager.ShowAsync(notification, cancellationToken);
                await submission
                    .WaitAsync(nativeSubmissionTimeout, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return ClientNotificationPlatformResult.Accepted;
            }
            catch (OperationCanceledException)
            {
                if (submission is not null)
                {
                    TrackUncertainSubmission(submission, notification);
                }

                throw;
            }
            catch (TimeoutException exception)
            {
                TrackUncertainSubmission(submission!, notification);
                logger.LogWarning(
                    "Submitting a Windows notification timed out; errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientNotificationPlatformResult.TransientFailure;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Submitting a Windows notification failed transiently; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientNotificationPlatformResult.TransientFailure;
            }
        }
        finally
        {
            submissionGate.Release();
        }
    }

    public async Task<ClientNotificationPlatformResult> ClearConversationAsync(
        string accountScopeId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var group = WindowsNotificationIdentity.GetConversationGroup(
            accountScopeId,
            conversationId);
        return await RemoveAsync(
                tag: null,
                group,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<ClientNotificationPlatformResult> ClearSummaryAsync(
        string accountScopeId,
        CancellationToken cancellationToken)
    {
        WindowsNotificationIdentity.ValidateAccountScopeId(accountScopeId);
        return RemoveAsync(
            WindowsNotificationIdentity.SummaryTag,
            WindowsNotificationIdentity.GetSummaryGroup(accountScopeId),
            cancellationToken);
    }

    private async Task<ClientNotificationPlatformResult> RemoveAsync(
        string? tag,
        string group,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateGate)
            {
                if (string.Equals(
                        uncertainSubmissionGroup,
                        group,
                        StringComparison.Ordinal) &&
                    uncertainSubmissionCleanup is { IsCompleted: false })
                {
                    return ClientNotificationPlatformResult.TransientFailure;
                }
            }

            if (GetSettingsSnapshot().PlatformAvailability ==
                ClientNotificationPlatformAvailability.Unavailable)
            {
                return ClientNotificationPlatformResult.TransientFailure;
            }

            var removal = tag is null
                ? manager.RemoveByGroupAsync(group, cancellationToken)
                : manager.RemoveByTagAndGroupAsync(tag, group, cancellationToken);
            await removal
                .WaitAsync(nativeRemovalTimeout, timeProvider, cancellationToken)
                .ConfigureAwait(false);
            ClearCompletedUncertainSubmission(group);
            return ClientNotificationPlatformResult.Accepted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(
                "Removing a Windows notification group timed out; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientNotificationPlatformResult.TransientFailure;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Removing a Windows notification group failed transiently; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientNotificationPlatformResult.TransientFailure;
        }
        finally
        {
            submissionGate.Release();
        }
    }

    private WindowsClientNotification CreateNotification(ClientNotificationRequest request)
    {
        WindowsNotificationIdentity.ValidateAccountScopeId(request.AccountScopeId);
        ArgumentNullException.ThrowIfNull(request.Messages);
        if (request.Messages.Count == 0 ||
            request.Messages.Any(message => message is null))
        {
            throw new ArgumentException(
                "A Windows notification requires at least one message.",
                nameof(request));
        }

        return request.Policy switch
        {
            NotificationPolicy.PerMessage when request.Messages.Count == 1 =>
                CreatePerMessageNotification(request.AccountScopeId, request.Messages[0]),
            NotificationPolicy.Summary => CreateSummaryNotification(
                request.AccountScopeId,
                request.Messages),
            _ => throw new ArgumentException(
                "The Windows notification policy does not match its message payload.",
                nameof(request)),
        };
    }

    private WindowsClientNotification CreatePerMessageNotification(
        string accountScopeId,
        ClientNotificationMessage message)
    {
        if (message.MessageId <= 0 || message.ConversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A per-message notification has an invalid identity.",
                nameof(message));
        }

        var title = LimitText(message.ConversationName, 64, "RelayCove");
        var content = string.IsNullOrWhiteSpace(message.Content)
            ? "新消息"
            : message.Content;
        var body = LimitText(
            message.SenderDisplayName + ": " + content,
            256,
            "新消息");
        var target = ClientNotificationActivationTarget.Message(
            accountScopeId,
            message.ConversationId,
            message.MessageId);
        return new WindowsClientNotification(
            title,
            body,
            WindowsNotificationActivationCodec.Encode(target),
            WindowsNotificationIdentity.GetMessageTag(message.MessageId),
            WindowsNotificationIdentity.GetConversationGroup(
                accountScopeId,
                message.ConversationId),
            timeProvider.GetUtcNow().AddDays(3),
            ExpiresOnReboot: true);
    }

    private WindowsClientNotification CreateSummaryNotification(
        string accountScopeId,
        IReadOnlyList<ClientNotificationMessage> messages)
    {
        var target = ClientNotificationActivationTarget.UnreadOverview(accountScopeId);
        return new WindowsClientNotification(
            "RelayCove",
            $"{messages.Count} 条未读消息",
            WindowsNotificationActivationCodec.Encode(target),
            WindowsNotificationIdentity.SummaryTag,
            WindowsNotificationIdentity.GetSummaryGroup(accountScopeId),
            timeProvider.GetUtcNow().AddDays(3),
            ExpiresOnReboot: true);
    }

    private static string LimitText(string? value, int maximumRunes, string fallback)
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

    private void TrackUncertainSubmission(
        Task submission,
        WindowsClientNotification notification)
    {
        Task<bool> cleanup;
        lock (stateGate)
        {
            if (uncertainSubmissionCleanup is not null)
            {
                return;
            }

            cleanup = CleanupUncertainSubmissionAsync(
                submission,
                notification.Tag,
                notification.Group);
            uncertainSubmissionCleanup = cleanup;
            uncertainSubmissionGroup = notification.Group;
        }

        _ = cleanup.ContinueWith(
            static (completed, state) =>
            {
                var platform = (WindowsClientNotificationPlatform)state!;
                if (completed.Status != TaskStatus.RanToCompletion || !completed.Result)
                {
                    return;
                }

                lock (platform.stateGate)
                {
                    if (ReferenceEquals(platform.uncertainSubmissionCleanup, completed))
                    {
                        platform.uncertainSubmissionCleanup = null;
                        platform.uncertainSubmissionGroup = null;
                    }
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<bool> CleanupUncertainSubmissionAsync(
        Task submission,
        string tag,
        string group)
    {
        try
        {
            await submission.ConfigureAwait(false);
        }
        catch
        {
            return true;
        }

        try
        {
            await manager.RemoveByTagAndGroupAsync(
                    tag,
                    group,
                    CancellationToken.None)
                .WaitAsync(nativeRemovalTimeout, timeProvider)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Cleaning an uncertain Windows notification submission failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return false;
        }
    }

    private void ClearCompletedUncertainSubmission(string group)
    {
        lock (stateGate)
        {
            if (string.Equals(
                    uncertainSubmissionGroup,
                    group,
                    StringComparison.Ordinal) &&
                uncertainSubmissionCleanup is { IsCompleted: true })
            {
                uncertainSubmissionCleanup = null;
                uncertainSubmissionGroup = null;
            }
        }
    }

    private ClientNotificationSettingsSnapshot ReadSettingsSnapshot()
    {
        try
        {
            if (!manager.IsSupported() || !manager.IsRegistered)
            {
                return ClientNotificationSettingsSnapshot.Unavailable;
            }

            return manager.Setting == WindowsClientNotificationSetting.Enabled
                ? ClientNotificationSettingsSnapshot.Enabled
                : new ClientNotificationSettingsSnapshot(
                    ClientNotificationPlatformAvailability.Disabled,
                    IsDoNotDisturbEnabled: false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Reading Windows notification availability failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientNotificationSettingsSnapshot.Unavailable;
        }
    }

    private void CompleteSettingsRefresh(Task<ClientNotificationSettingsSnapshot> refresh)
    {
        lock (stateGate)
        {
            if (!ReferenceEquals(settingsRefresh, refresh))
            {
                return;
            }

            settingsSnapshot = refresh.Status == TaskStatus.RanToCompletion
                ? refresh.Result
                : ClientNotificationSettingsSnapshot.Unavailable;
            settingsSnapshotExpiresAt = timeProvider.GetUtcNow().Add(settingsCacheDuration);
            settingsRefresh = null;
        }
    }
}
