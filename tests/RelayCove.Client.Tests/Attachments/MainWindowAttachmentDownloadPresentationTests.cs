using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RelayCove.Client.Accounts;
using RelayCove.Client.Attachments;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Attachments;

public sealed class MainWindowAttachmentDownloadPresentationTests
{
    [Fact]
    public async Task ApplyMessageListSnapshot_WhenAttachmentStateChanges_KeepsSingleAccessibleAction()
    {
        await RunOnStaAsync(() =>
        {
            var conversationId = Guid.NewGuid();
            var messageClientId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var window = new MainWindow
            {
                Width = 1200,
                Height = 800,
                ShowInTaskbar = false,
                ShowActivated = false,
                Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            try
            {
                window.LoginPanel.Visibility = Visibility.Collapsed;
                window.AccountPanel.Visibility = Visibility.Visible;
                window.Show();
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationId,
                    messageClientId,
                    attachmentId,
                    isDownloaded: false,
                    revision: 1));
                Layout(window);

                var item = Assert.Single(window.MessageList.ItemsSource
                    .Cast<ClientMessageListItemPresentation>());
                var attachment = Assert.Single(item.Attachments);
                var state = Assert.IsType<ClientAttachmentDownloadViewState>(
                    attachment.DownloadState);
                var button = Assert.Single(
                    FindVisualDescendants<Button>(window.MessageList),
                    candidate => ReferenceEquals(candidate.DataContext, attachment));
                Assert.Equal("下载", button.Content);
                Assert.True(button.IsEnabled);
                Assert.Equal(
                    "下载附件：安全文档.pdf",
                    AutomationProperties.GetName(button));

                Assert.True(state.TryBeginDownload(
                    ready: true,
                    conversationId,
                    messageClientId,
                    attachmentId,
                    state.Context.ContextVersion,
                    out var flight));
                Layout(window);

                var sameButton = Assert.Single(
                    FindVisualDescendants<Button>(window.MessageList),
                    candidate => ReferenceEquals(candidate.DataContext, attachment));
                Assert.Same(button, sameButton);
                Assert.Equal("取消", button.Content);
                Assert.True(button.IsEnabled);
                var progressBar = Assert.Single(
                    FindVisualDescendants<ProgressBar>(window.MessageList));
                Assert.Equal(Visibility.Visible, progressBar.Visibility);

                Assert.True(state.TryApplyProgress(
                    ready: true,
                    conversationId,
                    messageClientId,
                    attachmentId,
                    state.Context.ContextVersion,
                    flight!,
                    flight,
                    new ClientAttachmentDownloadProgress(33, 100)));
                Layout(window);
                Assert.Equal(33, progressBar.Value);
                var liveStatus = Assert.Single(
                    FindVisualDescendants<TextBlock>(window.MessageList),
                    textBlock => textBlock.Text == "正在下载… 30%" &&
                        AutomationProperties.GetLiveSetting(textBlock) ==
                        AutomationLiveSetting.Polite);
                typeof(MainWindow)
                    .GetMethod(
                        "RaiseAttachmentDownloadLiveRegion",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [state]);
                Assert.NotNull(UIElementAutomationPeer.FromElement(liveStatus));

                Assert.True(state.TryCancel(
                    ready: true,
                    conversationId,
                    messageClientId,
                    attachmentId,
                    state.Context.ContextVersion,
                    flight!,
                    flight));
                Layout(window);
                Assert.Same(
                    button,
                    Assert.Single(
                        FindVisualDescendants<Button>(window.MessageList),
                        candidate => ReferenceEquals(candidate.DataContext, attachment)));
                Assert.Equal("正在取消…", button.Content);
                Assert.False(button.IsEnabled);

                Assert.True(state.TryApplyOutcome(
                    ready: true,
                    conversationId,
                    messageClientId,
                    attachmentId,
                    state.Context.ContextVersion,
                    flight!,
                    flight,
                    ClientAttachmentDownloadOutcome.Failure(
                        ClientAttachmentDownloadStatus.Canceled)));
                Layout(window);
                Assert.Equal("重试", button.Content);
                Assert.True(button.IsEnabled);

                Assert.True(state.TryBeginDownload(
                    ready: true,
                    conversationId,
                    messageClientId,
                    attachmentId,
                    state.Context.ContextVersion,
                    out var retryFlight));
                Assert.True(state.TryApplyOutcome(
                    ready: true,
                    conversationId,
                    messageClientId,
                    attachmentId,
                    state.Context.ContextVersion,
                    retryFlight!,
                    retryFlight,
                    new ClientAttachmentDownloadOutcome(
                        ClientAttachmentDownloadStatus.Completed,
                        "redacted.cache")));
                Layout(window);
                Assert.Same(
                    button,
                    Assert.Single(
                        FindVisualDescendants<Button>(window.MessageList),
                        candidate => ReferenceEquals(candidate.DataContext, attachment)));
                Assert.Equal("在文件夹中显示", button.Content);
                Assert.True(button.IsEnabled);
                Assert.Equal(
                    "在文件夹中显示附件：安全文档.pdf",
                    AutomationProperties.GetName(button));
                Assert.Equal(Visibility.Collapsed, progressBar.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ApplyMessageListSnapshot_WhenConversationMovesAtoBtoA_DoesNotReuseOldState()
    {
        await RunOnStaAsync(() =>
        {
            var conversationA = Guid.NewGuid();
            var conversationB = Guid.NewGuid();
            var messageClientId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var window = new MainWindow();
            try
            {
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationA,
                    messageClientId,
                    attachmentId,
                    isDownloaded: false,
                    revision: 1));
                var oldState = GetOnlyState(window);
                Assert.True(oldState.TryBeginDownload(
                    ready: true,
                    conversationA,
                    messageClientId,
                    attachmentId,
                    oldState.Context.ContextVersion,
                    out var oldFlight));

                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationB,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    isDownloaded: false,
                    revision: 2));
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationA,
                    messageClientId,
                    attachmentId,
                    isDownloaded: true,
                    revision: 3));
                var newState = GetOnlyState(window);

                Assert.NotSame(oldState, newState);
                Assert.NotEqual(
                    oldState.Context.ContextVersion,
                    newState.Context.ContextVersion);
                Assert.Equal(ClientAttachmentDownloadPhase.Downloaded, newState.Phase);
                Assert.False(oldState.TryApplyProgress(
                    ready: true,
                    conversationA,
                    messageClientId,
                    attachmentId,
                    newState.Context.ContextVersion,
                    oldFlight!,
                    oldFlight,
                    new ClientAttachmentDownloadProgress(90, 100)));
                Assert.Equal(ClientAttachmentDownloadPhase.Downloaded, newState.Phase);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ApplyMessageListSnapshot_WhenDownloadedProjectionArrivesDuringFlight_UsesItAfterFailedOutcome()
    {
        await RunOnStaAsync(() =>
        {
            var conversationId = Guid.NewGuid();
            var messageClientId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var window = new MainWindow();
            try
            {
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationId,
                    messageClientId,
                    attachmentId,
                    isDownloaded: false,
                    revision: 1));
                var state = GetOnlyState(window);
                Assert.True(state.TryBeginDownload(
                    ready: true,
                    conversationId,
                    messageClientId,
                    attachmentId,
                    state.Context.ContextVersion,
                    out var flight));

                var key = CreateAttachmentViewKey(messageClientId, attachmentId);
                var operation = CreateDownloadOperation(flight!);
                GetPrivateDictionary(window, "attachmentDownloadOperations").Add(key, operation);

                // This durable projection is authoritative, but the active state
                // cannot accept it until the current flight has settled.
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationId,
                    messageClientId,
                    attachmentId,
                    isDownloaded: true,
                    revision: 2));

                InvokePrivate(
                    window,
                    "ApplyAttachmentDownloadOutcome",
                    key,
                    state,
                    operation,
                    ClientAttachmentDownloadOutcome.Failure(
                        ClientAttachmentDownloadStatus.Canceled));

                Assert.Equal(ClientAttachmentDownloadPhase.Downloaded, state.Phase);
                Assert.Equal("已下载。", state.StatusText);
                GetPrivateDictionary(window, "attachmentDownloadOperations").Remove(key);
                DisposeOperation(operation);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task AttachmentRevealOperations_WhenConversationMovesAtoBtoA_CancelOldAndRetainNewIdentity()
    {
        await RunOnStaAsync(() =>
        {
            var conversationA = Guid.NewGuid();
            var conversationB = Guid.NewGuid();
            var messageClientId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var window = new MainWindow();
            try
            {
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationA,
                    messageClientId,
                    attachmentId,
                    isDownloaded: true,
                    revision: 1));
                var oldState = GetOnlyState(window);
                var key = CreateAttachmentViewKey(messageClientId, attachmentId);
                var oldOperation = CreateRevealOperation(oldState);
                var revealOperations = GetPrivateDictionary(window, "attachmentRevealOperations");
                revealOperations.Add(key, oldOperation);

                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationB,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    isDownloaded: true,
                    revision: 2));
                Assert.True(GetOperationCancellation(oldOperation).IsCancellationRequested);
                Assert.Empty(revealOperations);

                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationA,
                    messageClientId,
                    attachmentId,
                    isDownloaded: true,
                    revision: 3));
                var newState = GetOnlyState(window);
                var newOperation = CreateRevealOperation(newState);
                revealOperations.Add(key, newOperation);

                Assert.False((bool)InvokePrivate(
                    window,
                    "IsCurrentAttachmentRevealOperation",
                    key,
                    oldState,
                    oldOperation)!);
                Assert.True((bool)InvokePrivate(
                    window,
                    "IsCurrentAttachmentRevealOperation",
                    key,
                    newState,
                    newOperation)!);

                // Simulate the old A finally block completing after A was reopened.
                InvokePrivate(window, "CompleteAttachmentRevealOperation", key, oldOperation);
                Assert.Same(newOperation, revealOperations[key]);
                InvokePrivate(window, "CompleteAttachmentRevealOperation", key, newOperation);
                Assert.Empty(revealOperations);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ClientMessageListSnapshot CreateSnapshot(
        Guid conversationId,
        Guid messageClientId,
        Guid attachmentId,
        bool isDownloaded,
        long revision)
    {
        var message = new MessageDto(
            10,
            messageClientId,
            conversationId,
            Guid.NewGuid(),
            "发送者",
            MessageType.File,
            Content: null,
            ReplyToMessageId: null,
            Attachments:
            [
                new AttachmentDto(
                    attachmentId,
                    "安全文档.pdf",
                    "application/pdf",
                    1024,
                    $"/api/attachments/{attachmentId:D}/download",
                    ThumbnailUrl: null),
            ],
            MentionUserIds: Array.Empty<Guid>(),
            DateTimeOffset.UtcNow);
        var downloaded = isDownloaded
            ? new HashSet<Guid> { attachmentId }
            : new HashSet<Guid>();
        var items = ClientMessageListPresenter.Present(
            [message],
            Array.Empty<RelayCove.Client.Storage.LocalPendingMessage>(),
            Guid.NewGuid(),
            newMessageSeparatorBeforeMessageId: null,
            downloaded);
        return new ClientMessageListSnapshot(
            ClientMessageListStatus.Ready,
            conversationId,
            items,
            IsLoading: false,
            HasMoreBefore: false,
            HasMoreAfter: false,
            TargetMessageId: null,
            LastLoadStatus: null,
            revision);
    }

    private static ClientAttachmentDownloadViewState GetOnlyState(MainWindow window)
    {
        var item = Assert.Single(window.MessageList.ItemsSource
            .Cast<ClientMessageListItemPresentation>());
        return Assert.IsType<ClientAttachmentDownloadViewState>(
            Assert.Single(item.Attachments).DownloadState);
    }

    private static object CreateAttachmentViewKey(Guid messageClientId, Guid attachmentId) =>
        Activator.CreateInstance(
            GetNestedMainWindowType("ClientAttachmentViewKey"),
            messageClientId,
            attachmentId) ?? throw new InvalidOperationException("Unable to create attachment view key.");

    private static object CreateDownloadOperation(ClientAttachmentDownloadFlight flight) =>
        Activator.CreateInstance(
            GetNestedMainWindowType("ClientAttachmentDownloadOperation"),
            flight,
            new CancellationTokenSource()) ??
        throw new InvalidOperationException("Unable to create attachment download operation.");

    private static object CreateRevealOperation(ClientAttachmentDownloadViewState state) =>
        Activator.CreateInstance(
            GetNestedMainWindowType("ClientAttachmentRevealOperation"),
            state.Context,
            state,
            new CancellationTokenSource()) ??
        throw new InvalidOperationException("Unable to create attachment reveal operation.");

    private static IDictionary GetPrivateDictionary(MainWindow window, string fieldName) =>
        GetPrivateField(fieldName).GetValue(window) as IDictionary ??
        throw new InvalidOperationException($"Expected dictionary field '{fieldName}'.");

    private static CancellationTokenSource GetOperationCancellation(object operation) =>
        operation.GetType().GetProperty("Cancellation", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(operation) as CancellationTokenSource ??
        throw new InvalidOperationException("Expected operation cancellation source.");

    private static object? InvokePrivate(MainWindow window, string methodName, params object[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException($"Expected method '{methodName}'.");
        return method.Invoke(window, arguments);
    }

    private static void DisposeOperation(object operation)
    {
        var method = operation.GetType().GetMethod(
            "Dispose",
            BindingFlags.Instance | BindingFlags.Public) ??
            throw new InvalidOperationException("Expected operation dispose method.");
        method.Invoke(operation, null);
    }

    private static FieldInfo GetPrivateField(string fieldName) =>
        typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Expected field '{fieldName}'.");

    private static Type GetNestedMainWindowType(string typeName) =>
        typeof(MainWindow).GetNestedType(typeName, BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Expected nested type '{typeName}'.");

    private static void Layout(MainWindow window)
    {
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        if (window.MessageList.Items.Count != 0)
        {
            window.MessageList.ScrollIntoView(window.MessageList.Items[0]);
            window.MessageList.UpdateLayout();
        }

        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
