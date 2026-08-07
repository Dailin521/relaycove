using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RelayCove.Client.Accounts;
using RelayCove.Client.Attachments;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Attachments;

public sealed class MainWindowAttachmentImagePresentationTests
{
    [Fact]
    public async Task ApplyMessageListSnapshot_WhenItemsSourceMaterializes_PublishesNewSnapshotFirst()
    {
        await RunOnStaAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                ClientMessageListSnapshot? observedSnapshot = null;
                window.MessageList.ItemContainerGenerator.ItemsChanged += (_, _) =>
                {
                    observedSnapshot = GetPrivateField("displayedMessageSnapshot")
                        .GetValue(window) as ClientMessageListSnapshot;
                };
                var snapshot = CreateSnapshot(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    isImage: true,
                    isDownloaded: true,
                    revision: 1);

                window.ApplyMessageListSnapshot(snapshot);

                Assert.NotNull(observedSnapshot);
                Assert.Equal(snapshot.Revision, observedSnapshot.Revision);
                Assert.Same(GetOnlyImageState(window), GetOnlyAttachment(window).ImageState);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ApplyMessageListSnapshot_WhenDownloadedImageIsPresented_ShowsOnlySafeAccessiblePreviewState()
    {
        await RunOnStaAsync(() =>
        {
            var conversationId = Guid.NewGuid();
            var messageClientId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var window = CreateVisibleWindow();
            try
            {
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationId,
                    messageClientId,
                    attachmentId,
                    isImage: true,
                    isDownloaded: true,
                    revision: 1));
                Layout(window);

                var attachment = GetOnlyAttachment(window);
                var state = Assert.IsType<ClientAttachmentImageViewState>(attachment.ImageState);
                Assert.True(state.IsEligible);
                Assert.False(state.CanView);
                Assert.Contains("待加载", state.AutomationName);
                Assert.DoesNotContain(attachmentId.ToString("D"), state.AutomationName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("https://", state.AutomationName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("cache", state.AutomationName, StringComparison.OrdinalIgnoreCase);

                var thumbnail = Assert.Single(
                    FindVisualDescendants<System.Windows.Controls.Image>(window.MessageList),
                    candidate => ReferenceEquals(candidate.DataContext, attachment));
                Assert.Null(thumbnail.Source);
                Assert.Equal(state.AutomationName, AutomationProperties.GetName(thumbnail));
                var viewButton = Assert.Single(
                    FindVisualDescendants<Button>(window.MessageList),
                    candidate => ReferenceEquals(candidate.DataContext, attachment) &&
                        Equals(candidate.Content, "查看图片"));
                Assert.False(viewButton.IsEnabled);

                var frozen = CreateFrozenBitmap();
                Assert.True(state.TryBeginLoad());
                Assert.True(state.TryApplyLoaded(frozen));
                Layout(window);

                Assert.Same(frozen, thumbnail.Source);
                Assert.True(Assert.IsAssignableFrom<BitmapSource>(thumbnail.Source).IsFrozen);
                Assert.True(viewButton.IsEnabled);
                Assert.Equal("查看图片：旅行照片.png", AutomationProperties.GetName(viewButton));

                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    isImage: false,
                    isDownloaded: true,
                    revision: 2));
                Layout(window);

                var fileAttachment = GetOnlyAttachment(window);
                Assert.False(Assert.IsType<ClientAttachmentImageViewState>(fileAttachment.ImageState).IsEligible);
                var hiddenImageButton = Assert.Single(
                    FindVisualDescendants<Button>(window.MessageList),
                    candidate => ReferenceEquals(candidate.DataContext, fileAttachment) &&
                        Equals(candidate.Content, "查看图片"));
                Assert.False(hiddenImageButton.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ApplyMessageListSnapshot_WhenImageIsNotDownloaded_ExposesAutomaticPreviewSlot()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    isImage: true,
                    isDownloaded: false,
                    revision: 1));
                Layout(window);

                var attachment = GetOnlyAttachment(window);
                var state = Assert.IsType<ClientAttachmentImageViewState>(attachment.ImageState);
                Assert.True(state.IsEligible);
                Assert.True(state.ShowPreview);
                Assert.Equal(ClientAttachmentDownloadPhase.Idle, attachment.DownloadState?.Phase);
                Assert.Contains(
                    FindVisualDescendants<System.Windows.Controls.Image>(window.MessageList),
                    candidate => ReferenceEquals(candidate.DataContext, attachment));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task AttachmentThumbnailOperations_WhenConversationMovesAtoBtoA_CancelOldAndRejectLateResult()
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
                    isImage: true,
                    isDownloaded: true,
                    revision: 1));
                var oldState = GetOnlyImageState(window);
                Assert.True(oldState.TryBeginLoad());
                var key = CreateAttachmentViewKey(messageClientId, attachmentId);
                var oldOperation = CreateThumbnailOperation(oldState);
                var operations = GetPrivateDictionary(window, "attachmentThumbnailOperations");
                operations.Add(key, oldOperation);

                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationB,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    isImage: true,
                    isDownloaded: true,
                    revision: 2));
                Assert.True(GetOperationCancellation(oldOperation).IsCancellationRequested);
                Assert.Empty(operations);
                // The old state object can no longer reach the displayed snapshot;
                // completion must be gated by IsCurrentAttachmentThumbnailOperation.
                Assert.True(oldState.TryApplyLoaded(CreateFrozenBitmap()));

                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationA,
                    messageClientId,
                    attachmentId,
                    isImage: true,
                    isDownloaded: true,
                    revision: 3));
                var newState = GetOnlyImageState(window);
                var newOperation = CreateThumbnailOperation(newState);
                operations.Add(key, newOperation);

                Assert.NotSame(oldState, newState);
                Assert.False((bool)InvokePrivate(
                    window,
                    "IsCurrentAttachmentThumbnailOperation",
                    key,
                    oldState,
                    oldOperation)!);
                Assert.True((bool)InvokePrivate(
                    window,
                    "IsCurrentAttachmentThumbnailOperation",
                    key,
                    newState,
                    newOperation)!);

                // An A→B→A late callback must not remove or mutate the new A operation.
                InvokePrivate(window, "CompleteAttachmentThumbnailOperation", key, oldOperation);
                Assert.Same(newOperation, operations[key]);
                InvokePrivate(window, "CompleteAttachmentThumbnailOperation", key, newOperation);
                Assert.Empty(operations);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task AttachmentImageViewer_WhenEscapeIsPressed_CancelsSingleViewerAndClearsPreview()
    {
        await RunOnStaAsync(() =>
        {
            var conversationId = Guid.NewGuid();
            var messageClientId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var window = CreateVisibleWindow();
            try
            {
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationId,
                    messageClientId,
                    attachmentId,
                    isImage: true,
                    isDownloaded: true,
                    revision: 1));
                Layout(window);
                var state = GetOnlyImageState(window);
                var viewerOperation = CreateViewerOperation(state);
                SetPrivateField(window, "attachmentImageViewerOperation", viewerOperation);
                window.AttachmentImageViewerImage.Source = CreateFrozenBitmap();
                window.AttachmentImageViewerTitleText.Text = state.DisplayName;
                window.AttachmentImageViewerStatusText.Text = "正在加载受限图片预览…";
                window.AttachmentImageViewerOverlay.Visibility = Visibility.Visible;
                Layout(window);

                Assert.Equal(2, Grid.GetColumnSpan(window.AttachmentImageViewerOverlay));
                Assert.True(window.AttachmentImageViewerOverlay.ActualWidth > 270);

                var escape = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window) ??
                    throw new InvalidOperationException("Expected visible window presentation source."),
                    Environment.TickCount,
                    Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                window.RaiseEvent(escape);

                Assert.True(escape.Handled);
                Assert.True(GetOperationCancellation(viewerOperation).IsCancellationRequested);
                Assert.Null(GetPrivateField("attachmentImageViewerOperation").GetValue(window));
                Assert.Equal(Visibility.Collapsed, window.AttachmentImageViewerOverlay.Visibility);
                Assert.Null(window.AttachmentImageViewerImage.Source);
                Assert.Equal(string.Empty, window.AttachmentImageViewerTitleText.Text);
                Assert.Equal(string.Empty, window.AttachmentImageViewerStatusText.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task AttachmentImageViewer_WhenDownloadedStateIsLost_ClosesCompletedViewerAndClearsSource()
    {
        await RunOnStaAsync(() =>
        {
            var conversationId = Guid.NewGuid();
            var messageClientId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var window = CreateVisibleWindow();
            try
            {
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationId,
                    messageClientId,
                    attachmentId,
                    isImage: true,
                    isDownloaded: true,
                    revision: 1));
                Layout(window);
                var state = GetOnlyImageState(window);
                var viewerOperation = CreateViewerOperation(state);
                SetPrivateField(window, "attachmentImageViewerOperation", viewerOperation);
                window.AttachmentImageViewerImage.Source = CreateFrozenBitmap();
                window.AttachmentImageViewerTitleText.Text = state.DisplayName;
                window.AttachmentImageViewerStatusText.Text = "图片预览已加载；显示仍受安全上限保护。";
                window.AttachmentImageViewerOverlay.Visibility = Visibility.Visible;
                Layout(window);
                window.Activate();
                Assert.True(window.CloseAttachmentImageViewerButton.Focus());
                Assert.True(window.AttachmentImageViewerOverlay.IsKeyboardFocusWithin);

                // A durable same-conversation refresh says the attachment is no
                // longer downloaded. The still-visible completed viewer must be
                // torn down, not left displaying its old strong image reference.
                window.ApplyMessageListSnapshot(CreateSnapshot(
                    conversationId,
                    messageClientId,
                    attachmentId,
                    isImage: true,
                    isDownloaded: false,
                    revision: 2));

                Assert.True(GetOperationCancellation(viewerOperation).IsCancellationRequested);
                Assert.Null(GetPrivateField("attachmentImageViewerOperation").GetValue(window));
                Assert.Equal(Visibility.Collapsed, window.AttachmentImageViewerOverlay.Visibility);
                Assert.Null(window.AttachmentImageViewerImage.Source);
                Assert.Equal(string.Empty, window.AttachmentImageViewerTitleText.Text);
                Assert.Equal(string.Empty, window.AttachmentImageViewerStatusText.Text);
                Assert.False(window.AttachmentImageViewerOverlay.IsKeyboardFocusWithin);
                Assert.True(
                    window.MessageList.IsKeyboardFocusWithin ||
                    window.MessageComposerTextBox.IsKeyboardFocusWithin);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task AttachmentThumbnailOperations_WhenDataContextIsRecycled_CancelsAndClearsStrongReference()
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
                    isImage: true,
                    isDownloaded: true,
                    revision: 1));
                var state = GetOnlyImageState(window);
                Assert.True(state.TryBeginLoad());
                Assert.True(state.TryApplyLoaded(CreateFrozenBitmap()));
                var key = CreateAttachmentViewKey(messageClientId, attachmentId);
                var operation = CreateThumbnailOperation(state);
                var operations = GetPrivateDictionary(window, "attachmentThumbnailOperations");
                operations.Add(key, operation);
                var viewerOperation = CreateViewerOperation(state);
                SetPrivateField(window, "attachmentImageViewerOperation", viewerOperation);
                SetPrivateField(window, "attachmentImageViewerRestoreFocus", new Button());
                window.AttachmentImageViewerImage.Source = CreateFrozenBitmap();
                window.AttachmentImageViewerOverlay.Visibility = Visibility.Visible;
                var oldAttachment = GetOnlyAttachment(window);
                var recycledThumbnail = new System.Windows.Controls.Image
                {
                    DataContext = oldAttachment,
                };

                InvokePrivate(
                    window,
                    "OnAttachmentThumbnailDataContextChanged",
                    recycledThumbnail,
                    new DependencyPropertyChangedEventArgs(
                        FrameworkElement.DataContextProperty,
                        oldAttachment,
                        newValue: null));

                Assert.True(GetOperationCancellation(operation).IsCancellationRequested);
                Assert.True(GetOperationCancellation(viewerOperation).IsCancellationRequested);
                Assert.Empty(operations);
                Assert.Null(state.Thumbnail);
                Assert.False(state.IsLoading);
                Assert.False(state.CanView);
                Assert.Equal("图片缩略图待加载。", state.StatusText);
                Assert.Null(GetPrivateField("attachmentImageViewerOperation").GetValue(window));
                Assert.Null(GetPrivateField("attachmentImageViewerRestoreFocus").GetValue(window));
                Assert.Equal(Visibility.Collapsed, window.AttachmentImageViewerOverlay.Visibility);
                Assert.Null(window.AttachmentImageViewerImage.Source);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static MainWindow CreateVisibleWindow()
    {
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
        window.LoginPanel.Visibility = Visibility.Collapsed;
        window.AccountPanel.Visibility = Visibility.Visible;
        window.Show();
        return window;
    }

    private static ClientMessageListSnapshot CreateSnapshot(
        Guid conversationId,
        Guid messageClientId,
        Guid attachmentId,
        bool isImage,
        bool isDownloaded,
        long revision)
    {
        var message = new MessageDto(
            10,
            messageClientId,
            conversationId,
            Guid.NewGuid(),
            "发送者",
            isImage ? MessageType.Image : MessageType.File,
            Content: null,
            ReplyToMessageId: null,
            Attachments:
            [
                new AttachmentDto(
                    attachmentId,
                    isImage ? "旅行照片.png" : "安全文档.pdf",
                    isImage ? "image/png" : "application/pdf",
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

    private static ClientMessageAttachmentPresentation GetOnlyAttachment(MainWindow window) =>
        Assert.Single(Assert.Single(window.MessageList.ItemsSource
            .Cast<ClientMessageListItemPresentation>()).Attachments);

    private static ClientAttachmentImageViewState GetOnlyImageState(MainWindow window) =>
        Assert.IsType<ClientAttachmentImageViewState>(GetOnlyAttachment(window).ImageState);

    private static BitmapSource CreateFrozenBitmap()
    {
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels: new byte[] { 0, 0, 0, 255 },
            stride: 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static object CreateAttachmentViewKey(Guid messageClientId, Guid attachmentId) =>
        Activator.CreateInstance(
            GetNestedMainWindowType("ClientAttachmentViewKey"),
            messageClientId,
            attachmentId) ?? throw new InvalidOperationException("Unable to create attachment view key.");

    private static object CreateThumbnailOperation(ClientAttachmentImageViewState state) =>
        Activator.CreateInstance(
            GetNestedMainWindowType("ClientAttachmentImageOperation"),
            state.Context,
            state,
            new CancellationTokenSource()) ??
        throw new InvalidOperationException("Unable to create thumbnail operation.");

    private static object CreateViewerOperation(ClientAttachmentImageViewState state) =>
        Activator.CreateInstance(
            GetNestedMainWindowType("ClientAttachmentImageViewerOperation"),
            state.Context,
            state,
            new CancellationTokenSource()) ??
        throw new InvalidOperationException("Unable to create viewer operation.");

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

    private static FieldInfo GetPrivateField(string fieldName) =>
        typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Expected field '{fieldName}'.");

    private static Type GetNestedMainWindowType(string typeName) =>
        typeof(MainWindow).GetNestedType(typeName, BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Expected nested type '{typeName}'.");

    private static void SetPrivateField(MainWindow window, string fieldName, object? value) =>
        GetPrivateField(fieldName).SetValue(window, value);

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
