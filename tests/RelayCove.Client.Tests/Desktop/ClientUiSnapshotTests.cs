using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelayCove.Client.Accounts;
using RelayCove.Client.Attachments;
using RelayCove.Client.Updates;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Updates;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class ClientUiSnapshotTests
{
    [Fact]
    public async Task MainWindow_WhenSearchOverlayIsOpen_KeepsExplicitSearchControlsReachable()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.Width = 1600;
                window.Height = 900;
                window.SearchPanel.Visibility = Visibility.Visible;
                window.Show();
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                AssertWithinBounds(root, window.CloseSearchButton);
                AssertWithinBounds(root, window.MessageSearchTextBox);
                AssertWithinBounds(root, window.MessageSearchScopeComboBox);
                AssertWithinBounds(root, window.RunSearchButton);
                Assert.Same(root, VisualTreeHelper.GetParent(window.SearchPanel));
                Assert.Equal(1, Grid.GetRow(window.SearchPanel));
                Assert.Equal(1, Grid.GetColumn(window.SearchPanel));
                Assert.True(window.SearchPanel.ActualWidth >= window.MessageSearchTextBox.ActualWidth);
                var searchCard = Assert.IsType<Border>(VisualTreeHelper.GetChild(window.SearchPanel, 0));
                Assert.InRange(searchCard.ActualWidth, 756, 764);
                Assert.InRange(searchCard.ActualHeight, 360, 640);
                Assert.True(window.MessageSearchResultList.ActualHeight >= 180);
                Assert.Equal(Visibility.Visible, window.MessageSearchEmptyStatePanel.Visibility);
                Assert.True(searchCard.ActualHeight <= 640);

                SaveSnapshotWhenRequested(root, "search-1600x900.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenMemberDrawerIsOpen_UsesChatOverlayRatherThanFourthColumn()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 1600;
                window.Height = 900;
                ShowRepresentativeMemberDrawer(window);
                window.Show();
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                Assert.Equal(1, Grid.GetColumn(window.ChannelOverlay));
                Assert.Equal(Visibility.Visible, window.ChannelOverlay.Visibility);
                Assert.InRange(window.ChannelDrawerSurfacePanel.ActualWidth, 378, 382);
                Assert.True(window.ChannelDrawerSurfacePanel.ActualWidth < root.ActualWidth / 2);
                AssertWithinBounds(root, window.ChannelMemberSearchTextBox);

                SaveSnapshotWhenRequested(root, "members-1600x900.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenSettingsDrawerIsOpen_KeepsAccountActionsReachable()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.Width = 1280;
                window.Height = 720;
                window.SettingsOverlay.Visibility = Visibility.Visible;
                window.Show();
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                AssertWithinBounds(root, window.CloseSettingsButton);
                Assert.InRange(window.SettingsOverlay.ActualWidth, 395, 397);
                Assert.True(window.SettingsOverlay.ActualWidth < root.ActualWidth / 2);

                SaveSnapshotWhenRequested(root, "settings-1280x720.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenOptionalUpdateIsAvailable_ShowsActionInSettingsDrawer()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.Width = 1280;
                window.Height = 720;
                window.BindUpdateActions(
                    _ => Task.FromResult(true),
                    () => Task.CompletedTask,
                    () => Task.CompletedTask,
                    static () => { },
                    () => Task.CompletedTask,
                    static () => { });
                window.ApplyUpdateState(CreateOptionalUpdateState());
                window.SettingsOverlay.Visibility = Visibility.Visible;
                window.Show();
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                Assert.True(window.SettingsOverlay.HasOptionalUpdateAction);
                Assert.Equal("下载更新", window.SettingsOverlay.UpdateActionLabel);
                AssertWithinBounds(root, window.SettingsOverlay.OptionalUpdateActionButton);

                SaveSnapshotWhenRequested(root, "settings-optional-update-1280x720.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(900, 520, "mandatory-update-900x520.png")]
    [InlineData(1280, 720, "mandatory-update-1280x720.png")]
    public async Task MainWindow_WhenMandatoryUpdateIsPresented_KeepsActionsAndNotesReachable(
        int width,
        int height,
        string snapshotFileName)
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = width;
                window.Height = height;
                window.BindUpdateActions(
                    _ => Task.FromResult(true),
                    () => Task.CompletedTask,
                    () => Task.CompletedTask,
                    static () => { },
                    () => Task.CompletedTask,
                    static () => { });
                window.Show();
                window.ApplyUpdateState(CreateMandatoryUpdateState(CreateRepresentativeReleaseNotes()));
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                Assert.Equal(Visibility.Visible, window.MandatoryUpdateOverlay.Visibility);
                Assert.Equal(2, Grid.GetColumnSpan(window.MandatoryUpdateOverlay));
                AssertWithinBounds(root, window.RetryMandatoryUpdateButton);
                AssertWithinBounds(root, window.DownloadMandatoryUpdateButton);
                AssertWithinBounds(root, window.ExitMandatoryUpdateButton);
                Assert.True(window.MandatoryUpdateDetailText.ActualHeight > 0);
                Assert.InRange(
                    window.MandatoryUpdateNotesScrollViewer.ActualHeight,
                    1,
                    root.ActualHeight - 120);

                SaveSnapshotWhenRequested(root, snapshotFileName);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1280, 720, "image-viewer-1280x720.png")]
    [InlineData(1600, 900, "image-viewer-1600x900.png")]
    public async Task MainWindow_WhenImageViewerIsPresented_KeepsPreviewAndCloseActionReachable(
        int width,
        int height,
        string snapshotFileName)
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = width;
                window.Height = height;
                window.AttachmentImageViewerTitleText.Text = "rc.25-界面评审参考图.png";
                window.AttachmentImageViewerStatusText.Text = "图片预览已加载；显示仍受 25 MiB 安全上限保护。";
                window.AttachmentImageViewerImage.Source = CreateRepresentativeImagePreview();
                window.AttachmentImageViewerOverlay.Visibility = Visibility.Visible;
                window.Show();
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                Assert.Equal(Visibility.Visible, window.AttachmentImageViewerOverlay.Visibility);
                Assert.Equal(2, Grid.GetColumnSpan(window.AttachmentImageViewerOverlay));
                AssertWithinBounds(root, window.AttachmentImageViewerTitleText);
                AssertWithinBounds(root, window.CloseAttachmentImageViewerButton);
                AssertWithinBounds(root, window.AttachmentImageViewerImage);
                AssertWithinBounds(root, window.AttachmentImageViewerStatusText);
                Assert.NotNull(window.AttachmentImageViewerImage.Source);
                Assert.True(window.AttachmentImageViewerImage.ActualWidth >= 862);
                Assert.True(window.AttachmentImageViewerImage.ActualHeight >= 483);

                SaveSnapshotWhenRequested(root, snapshotFileName);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(
        "正在准备图片预览",
        "加载完成后会在这里显示。安全限制会在显示前完成检查。",
        "正在加载受限图片预览…",
        "image-viewer-loading-1280x720.png")]
    [InlineData(
        "无法预览此图片",
        "图片未通过安全显示检查，或暂时无法从本地缓存读取。请关闭后稍后重试。",
        "图片文件不可用，未显示预览。",
        "image-viewer-error-1280x720.png")]
    public async Task MainWindow_WhenImageViewerHasNoSafeImage_ExplainsLoadingOrFailure(
        string emptyTitle,
        string emptyDetail,
        string status,
        string snapshotFileName)
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.Width = 1280;
                window.Height = 720;
                window.AttachmentImageViewerTitleText.Text = "设计稿参考图.png";
                window.AttachmentImageViewerEmptyTitleText.Text = emptyTitle;
                window.AttachmentImageViewerEmptyDetailText.Text = emptyDetail;
                window.AttachmentImageViewerStatusText.Text = status;
                window.AttachmentImageViewerImage.Source = null;
                window.AttachmentImageViewerOverlay.Visibility = Visibility.Visible;
                window.Show();
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                Assert.Null(window.AttachmentImageViewerImage.Source);
                Assert.Equal(emptyTitle, window.AttachmentImageViewerEmptyTitleText.Text);
                Assert.Equal(emptyDetail, window.AttachmentImageViewerEmptyDetailText.Text);
                AssertWithinBounds(root, window.CloseAttachmentImageViewerButton);
                AssertWithinBounds(root, window.AttachmentImageViewerStatusText);
                SaveSnapshotWhenRequested(root, snapshotFileName);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(640, 360, "direct-image-preview-landscape-1280x720.png")]
    [InlineData(640, 640, "direct-image-preview-square-1280x720.png")]
    [InlineData(360, 640, "direct-image-preview-portrait-1280x720.png")]
    public async Task MainWindow_WhenSingleImageMessageIsReady_RendersDirectPreviewWithoutDocumentChrome(
        int sourceWidth,
        int sourceHeight,
        string snapshotFileName)
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 1280;
                window.Height = 720;
                window.MessageList.ItemsSource = new[] { CreateReadyImagePreviewMessage(sourceWidth, sourceHeight) };
                window.Show();
                window.UpdateLayout();

                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var previewCard = Assert.Single(
                    FindVisualDescendants<Border>(window.MessageList),
                    candidate => candidate.MaxWidth == 360 && candidate.MaxHeight == 280);
                var previewImage = Assert.Single(
                    FindVisualDescendants<System.Windows.Controls.Image>(window.MessageList),
                    candidate => candidate.Source is not null);
                Assert.Equal(new CornerRadius(8), previewCard.CornerRadius);
                Assert.True(previewCard.ClipToBounds);
                Assert.InRange(previewCard.ActualWidth, 1, 360.5);
                Assert.InRange(previewCard.ActualHeight, 1, 280.5);
                Assert.InRange(
                    previewCard.ActualWidth / previewCard.ActualHeight,
                    (sourceWidth / (double)sourceHeight) - 0.02,
                    (sourceWidth / (double)sourceHeight) + 0.02);
                AssertWithinBounds(root, previewCard);
                AssertWithinBounds(root, previewImage);
                var imageMetadata = Assert.Single(
                    FindVisualDescendants<TextBlock>(window.MessageList),
                    candidate => candidate.Text == "团队白板参考图.png" && candidate.ActualHeight > 0);
                AssertDoesNotOverlap(root, previewCard, imageMetadata);

                SaveSnapshotWhenRequested(root, snapshotFileName);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(900, 520, false)]
    [InlineData(1280, 720, true)]
    public async Task MainWindow_WhenRenderedAtLoginSizes_KeepsLoginFormReachable(
        int width,
        int height,
        bool expectsBrandPanel)
    {
        await RunOnStaAsync(() =>
        {
            var window = new MainWindow
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Width = width,
                Height = height,
            };
            AddRc25Resources(window);
            try
            {
                window.Show();
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                Assert.Equal(
                    expectsBrandPanel ? Visibility.Visible : Visibility.Collapsed,
                    window.LoginBrandPanel.Visibility);
                AssertWithinBounds(root, window.ServerAddressTextBox);
                AssertWithinBounds(root, window.UserNameTextBox);
                AssertWithinBounds(root, window.PasswordInput);
                AssertWithinBounds(root, window.LoginButton);
                Assert.True(window.LoginButton.ActualHeight >= 38);
                Assert.Equal(
                    width == 900 ? Visibility.Collapsed : Visibility.Visible,
                    window.LoginPanelHeadingText.Visibility);
                Assert.Equal(
                    width == 900 ? Visibility.Collapsed : Visibility.Visible,
                    window.LoginPanelSubtitleText.Visibility);

                SaveSnapshotWhenRequested(root, $"login-{width}x{height}.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenMemberDrawerIsOpenAndWindowNarrows_CollapsesDrawer()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 1600;
                window.Height = 900;
                ShowRepresentativeMemberDrawer(window);
                window.Show();
                window.UpdateLayout();

                Assert.Equal(Visibility.Visible, window.ChannelOverlay.Visibility);
                Assert.Equal(new Thickness(0), window.ConversationChatPanel.Margin);

                window.Width = 1280;
                window.UpdateLayout();

                Assert.Equal(Visibility.Collapsed, window.ChannelOverlay.Visibility);
                Assert.Equal(new Thickness(0), window.ConversationChatPanel.Margin);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenNarrowMemberButtonIsClicked_KeepsComposerReachable()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 1280;
                window.Height = 720;
                window.OpenChannelPanelButton.IsEnabled = true;
                window.Show();
                window.UpdateLayout();
                window.MessageComposerTextBox.IsEnabled = true;
                window.SelectAttachmentsButton.IsEnabled = true;
                window.MentionPickerButton.IsEnabled = true;
                window.SendMessageButton.IsEnabled = true;

                window.OpenChannelPanelButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent, window.OpenChannelPanelButton));
                window.UpdateLayout();

                Assert.Equal(Visibility.Collapsed, window.ChannelOverlay.Visibility);
                Assert.Equal(new Thickness(0), window.ConversationChatPanel.Margin);
                Assert.True(window.MessageComposerTextBox.IsEnabled);
                Assert.True(window.SelectAttachmentsButton.IsEnabled);
                Assert.True(window.MentionPickerButton.IsEnabled);
                Assert.True(window.SendMessageButton.IsEnabled);
                Assert.Equal("窗口较窄时请扩大窗口后再管理成员。", window.ChannelLiveRegionText.Text);
                Assert.True(window.UnavailableFeatureNotice.IsNoticeVisible);
                Assert.Equal("窗口较窄时请扩大窗口后再管理成员。", window.UnavailableFeatureNotice.Message);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(900, 520)]
    [InlineData(1280, 720)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    public async Task MainWindow_WhenRenderedAtCommonSize_KeepsComposerAndActionsVisible(
        int width,
        int height)
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = width;
                window.Height = height;
                if (width >= 1600)
                {
                    ShowRepresentativeMemberDrawer(window);
                }
                else if (width == 1280)
                {
                    ShowReplyAndAttachmentsState(window);
                }

                window.Show();
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                if (root is System.Windows.Controls.Panel panel)
                {
                    panel.Background = window.Background;
                    root.UpdateLayout();
                }

                AssertWithinBounds(root, window.MessageComposerTextBox);
                AssertWithinBounds(root, window.SelectAttachmentsButton);
                AssertWithinBounds(root, window.MentionPickerButton);
                AssertWithinBounds(root, window.SendMessageButton);
                Assert.True(window.MessageComposerTextBox.ActualHeight > 0);
                Assert.True(window.SendMessageButton.ActualWidth > 0);
                Assert.True(window.MessageList.ActualHeight >= 80);
                if (width == 1280)
                {
                    Assert.Equal(Visibility.Collapsed, window.ChannelOverlay.Visibility);
                }
                if (width >= 1600)
                {
                    AssertWithinBounds(root, window.ChannelMemberSearchTextBox);
                    var memberActions = FindVisualDescendants<Button>(window.ChannelOverlay)
                        .Where(button => button.Content is "拉入" or "移除")
                        .ToArray();
                    Assert.Contains(memberActions, button => Equals(button.Content, "拉入"));
                    Assert.Contains(memberActions, button => Equals(button.Content, "移除"));
                    foreach (var action in memberActions)
                    {
                        AssertWithinBounds(root, action);
                    }
                }

                SaveSnapshotWhenRequested(root, $"main-window-outer-{width}x{height}.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    public async Task MainWindow_WhenRenderedWithoutOverlayAtWideSize_KeepsCleanChatHierarchy(
        int width,
        int height)
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = width;
                window.Height = height;
                window.Show();
                window.UpdateLayout();

                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                Assert.Equal(Visibility.Collapsed, window.ChannelOverlay.Visibility);
                Assert.Equal(Visibility.Collapsed, window.SettingsOverlay.Visibility);
                Assert.True(window.MessageList.ActualHeight >= 120);
                AssertWithinBounds(root, window.MessageComposerTextBox);
                AssertWithinBounds(root, window.SendMessageButton);

                SaveSnapshotWhenRequested(root, $"main-window-clean-{width}x{height}.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenRenderedAtMinimumSize_KeepsCoreMessageActionsReachable()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 900;
                window.Height = 520;
                window.MessageList.ItemsSource = new object[]
                {
                    CreateMessage(
                        "林乔",
                        "最小窗口也必须保留复制、回复与重试入口。",
                        "10:42",
                        isOwnMessage: true,
                        sendStatusLabel: "发送失败",
                        canRetry: true),
                };
                window.Show();
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                var actions = FindVisualDescendants<Button>(window.MessageList)
                    .Where(button => button.Content is "复制" or "回复" or "重试")
                    .ToArray();
                Assert.Equal(3, actions.Length);
                foreach (var action in actions)
                {
                    Assert.True(action.IsVisible);
                    AssertWithinBounds(root, action);
                }

                SaveSnapshotWhenRequested(root, "message-actions-900x520.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenNarrowComposerUsesReachableStates_KeepsActionsConsistent()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 1280;
                window.Height = 720;
                ShowReplyAndMentionState(window);
                window.Show();
                window.UpdateLayout();

                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                AssertWithinBounds(root, window.MessageComposerTextBox);
                AssertWithinBounds(root, window.SelectAttachmentsButton);
                AssertWithinBounds(root, window.MentionPickerButton);
                AssertWithinBounds(root, window.SendMessageButton);
                Assert.True(window.MessageComposerTextBox.IsEnabled);
                Assert.False(window.SelectAttachmentsButton.IsEnabled);
                Assert.True(window.MentionPickerButton.IsEnabled);
                Assert.True(window.SendMessageButton.IsEnabled);
                Assert.True(window.MessageList.ActualHeight >= 80);

                ShowReplyAndAttachmentsState(window);
                window.UpdateLayout();

                AssertWithinBounds(root, window.MessageComposerTextBox);
                AssertWithinBounds(root, window.SelectAttachmentsButton);
                AssertWithinBounds(root, window.MentionPickerButton);
                AssertWithinBounds(root, window.SendMessageButton);
                Assert.False(window.MessageComposerTextBox.IsEnabled);
                Assert.False(window.SelectAttachmentsButton.IsEnabled);
                Assert.False(window.MentionPickerButton.IsEnabled);
                Assert.True(window.SendMessageButton.IsEnabled);
                Assert.True(window.MessageList.ActualHeight >= 80);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ComposerResizeThumb_WhenDragged_ExpandsAndClampsTheInputArea()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 1280;
                window.Height = 720;
                window.Show();
                window.UpdateLayout();

                var initialTextHeight = window.MessageComposerTextBox.ActualHeight;
                Assert.Equal(System.Windows.Input.Cursors.SizeNS, window.ComposerResizeThumb.Cursor);

                window.ComposerResizeThumb.RaiseEvent(new DragDeltaEventArgs(0, -140)
                {
                    RoutedEvent = Thumb.DragDeltaEvent,
                });
                window.UpdateLayout();

                Assert.True(window.ComposerRow.Height.IsAuto);
                Assert.InRange(window.MessageComposerTextBox.Height, 58, 200);
                Assert.True(window.MessageComposerTextBox.ActualHeight > initialTextHeight);
                Assert.True(window.MessageList.ActualHeight >= 120);

                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                SaveSnapshotWhenRequested(root, "composer-resized-1280x720.png");

                window.ComposerResizeThumb.RaiseEvent(new DragDeltaEventArgs(0, 10000)
                {
                    RoutedEvent = Thumb.DragDeltaEvent,
                });
                window.UpdateLayout();

                Assert.Equal(58, window.MessageComposerTextBox.Height);
                AssertWithinBounds(root, window.SendMessageButton);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ComposerResizeThumb_WhenExpandedAtMinimumWindowSize_KeepsMessageListVisible()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 900;
                window.Height = 520;
                window.Show();
                window.UpdateLayout();

                window.ComposerResizeThumb.RaiseEvent(new DragDeltaEventArgs(0, -10000)
                {
                    RoutedEvent = Thumb.DragDeltaEvent,
                });
                window.UpdateLayout();

                Assert.InRange(window.MessageComposerTextBox.Height, 58, 200);
                Assert.True(window.MessageList.ActualHeight >= 120);
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                AssertWithinBounds(root, window.SendMessageButton);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ComposerResizeThumb_WhenCollapsedWithComplexState_KeepsComposerActionsReachable()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateRepresentativeWindow();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Width = 1280;
                window.Height = 720;
                ShowReplyAndAttachmentsState(window);
                window.Show();
                window.UpdateLayout();

                window.ComposerResizeThumb.RaiseEvent(new DragDeltaEventArgs(0, 10000)
                {
                    RoutedEvent = Thumb.DragDeltaEvent,
                });
                window.UpdateLayout();

                Assert.Equal(58, window.MessageComposerTextBox.Height);
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                AssertWithinBounds(root, window.ReplyComposerPanel);
                AssertWithinBounds(root, window.SelectedAttachmentPanel);
                AssertWithinBounds(root, window.SelectAttachmentsButton);
                AssertWithinBounds(root, window.MentionPickerButton);
                AssertWithinBounds(root, window.SendMessageButton);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static MainWindow CreateRepresentativeWindow()
    {
        var window = new MainWindow
        {
            ShowActivated = false,
        };
        AddRc25Resources(window);

        window.LoginPanel.Visibility = Visibility.Collapsed;
        window.AccountPanel.Visibility = Visibility.Visible;
        window.LoginBrandPanel.Visibility = Visibility.Collapsed;
        window.LoginBrandColumn.Width = new GridLength(0);
        window.MainContentPanel.Margin = new Thickness(16);
        window.HeadingText.Visibility = Visibility.Collapsed;
        window.DetailText.Visibility = Visibility.Collapsed;
        window.MainWorkspace.Margin = new Thickness(0, 0, 0, 10);
        window.HeadingText.Text = "产品设计";
        window.DetailText.Text = "公开频道 · 8 位成员";
        window.SidebarDisplayNameText.Text = "林乔";
        window.SidebarServerText.Text = "https://relay.example.com/relaycove/";
        window.SidebarConnectionText.Text = "已连接";
        window.SidebarSyncText.Text = string.Empty;
        window.SidebarNotificationText.Text = "系统通知：可用";
        window.SidebarUnreadText.Text = "未读计数：7";
        window.ConversationHeadingText.Text = "产品设计";
        window.NavigationNoticeText.Text = "讨论客户端体验和 rc.23 视觉改造";
        window.ConversationMembersSummaryText.Text = "成员：8 人";
        window.MessageComposerTextBox.IsEnabled = true;
        window.SelectAttachmentsButton.IsEnabled = true;
        window.MentionPickerButton.IsEnabled = true;
        window.MessageComposerTextBox.Text = "整理一下今天的评审结论…";
        window.SendMessageButton.IsEnabled = true;

        window.ConversationEmptyText.Visibility = Visibility.Collapsed;
        var conversations = new object[]
        {
            new
            {
                AvatarText = "产",
                Name = "产品设计",
                TypeLabel = "公开频道",
                TypeIcon = "#",
                GroupTitle = "公开频道",
                Preview = "程远：附件卡片可以更紧凑",
                Timestamp = "10:42",
                UnreadText = "4",
                HasUnread = true,
                MutedLabel = string.Empty,
            },
            new
            {
                AvatarText = "发",
                Name = "发布准备",
                TypeLabel = "私有频道",
                TypeIcon = "◆",
                GroupTitle = "私有频道",
                Preview = "林乔：Release 构建已通过",
                Timestamp = "09:18",
                UnreadText = "2",
                HasUnread = true,
                MutedLabel = string.Empty,
            },
            new
            {
                AvatarText = "程",
                Name = "程远",
                TypeLabel = "私聊",
                TypeIcon = "●",
                GroupTitle = "私聊",
                Preview = "收到，我来补截图",
                Timestamp = "昨天",
                UnreadText = "1",
                HasUnread = true,
                MutedLabel = string.Empty,
            },
        };
        var groupedConversations = CollectionViewSource.GetDefaultView(conversations);
        groupedConversations.GroupDescriptions.Add(new PropertyGroupDescription("GroupTitle"));
        window.ConversationList.ItemsSource = groupedConversations;

        window.MessageEmptyText.Visibility = Visibility.Collapsed;
        window.MessageList.Visibility = Visibility.Visible;
        window.MessageList.ItemsSource = new object[]
        {
            CreateMessage(
                "程远",
                "新的频道分组已经整理好了，公开、私有和私聊会更容易扫描。",
                "10:36",
                showDateSeparator: true,
                dateSeparatorLabel: "今天"),
            CreateMessage(
                "程远",
                "我也把小窗口下的成员栏自动收起纳入验收。",
                "10:37",
                isMergedWithPrevious: true),
            CreateMessage(
                "林乔",
                "很好。附件卡片和发送失败重试要继续靠近消息本身。",
                "10:40",
                isOwnMessage: true,
                hasReply: true,
                replySenderLabel: "程远",
                replyContent: "小窗口下的成员栏自动收起"),
            CreateMessage(
                "林乔",
                "这条消息模拟发送失败，重试入口应清晰但不抢占正文。",
                "10:42",
                isOwnMessage: true,
                sendStatusLabel: "发送失败",
                canRetry: true),
        };

        return window;
    }

    private static ClientUpdateState CreateMandatoryUpdateState(string releaseNotes)
    {
        var manifest = new UpdateManifestDto(
            SchemaVersion: UpdateConstants.SchemaVersion,
            Channel: UpdateConstants.Channel,
            Version: "1.0.0-rc.25",
            MinimumSupportedVersion: "1.0.0-rc.25",
            Mandatory: true,
            Artifact: new UpdateArtifactDto(
                Type: UpdateConstants.ArtifactTypePortableZip,
                Url: "https://updates.example.test/RelayCove-1.0.0-rc.25.zip",
                SizeBytes: 123,
                Sha256: new string('a', 64)),
            ReleaseNotes: releaseNotes);
        return new ClientUpdateState(
            ClientUpdatePhase.MandatoryAvailable,
            CurrentVersion: "1.0.0-rc.24",
            manifest,
            UpdateDecisionKind.Mandatory,
            Progress: null,
            ArchivePath: null,
            ClientUpdateFailure.None);
    }

    private static ClientUpdateState CreateOptionalUpdateState()
    {
        var manifest = new UpdateManifestDto(
            SchemaVersion: UpdateConstants.SchemaVersion,
            Channel: UpdateConstants.Channel,
            Version: "1.0.0-rc.25",
            MinimumSupportedVersion: "1.0.0-rc.24",
            Mandatory: false,
            Artifact: new UpdateArtifactDto(
                Type: UpdateConstants.ArtifactTypePortableZip,
                Url: "https://updates.example.test/RelayCove-1.0.0-rc.25.zip",
                SizeBytes: 123,
                Sha256: new string('b', 64)),
            ReleaseNotes: "本次更新优化了聊天界面与窗口布局。");
        return new ClientUpdateState(
            ClientUpdatePhase.OptionalAvailable,
            CurrentVersion: "1.0.0-rc.24",
            manifest,
            UpdateDecisionKind.Optional,
            Progress: null,
            ArchivePath: null,
            ClientUpdateFailure.None);
    }

    private static string CreateRepresentativeReleaseNotes() =>
        "本次更新优化了聊天界面与窗口布局。" + Environment.NewLine + Environment.NewLine +
        "• 输入卡片支持垂直拉伸，工具栏与发送操作保持在底部。" + Environment.NewLine +
        "• 成员管理与账户设置以覆盖层展示，不再增加额外的主界面列。" + Environment.NewLine +
        "• 修复高 DPI 下的标题栏、搜索与附件预览细节。";

    private static BitmapSource CreateRepresentativeImagePreview(int width = 640, int height = 360)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var wave = (byte)((x + y) % 48);
                pixels[offset] = (byte)(190 + wave / 5);
                pixels[offset + 1] = (byte)(119 + wave / 4);
                pixels[offset + 2] = (byte)(22 + wave / 8);
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        var preview = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            width * 4);
        preview.Freeze();
        return preview;
    }

    private static void AddRc25Resources(FrameworkElement element)
    {
        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/RelayCove.Client;component/Resources/ClientTheme.xaml",
                UriKind.Relative),
        });
        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/RelayCove.Client;component/Resources/ClientIcons.xaml",
                UriKind.Relative),
        });
        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/RelayCove.Client;component/Resources/ClientControls.xaml",
                UriKind.Relative),
        });
    }

    private static void SaveSnapshotWhenRequested(FrameworkElement root, string fileName)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("RELAYCOVE_UI_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        SavePng(
            root,
            (int)Math.Ceiling(root.ActualWidth),
            (int)Math.Ceiling(root.ActualHeight),
            Path.Combine(outputDirectory, fileName));
    }

    private static void ShowRepresentativeMemberDrawer(MainWindow window)
    {
        window.ChannelOverlay.Visibility = Visibility.Visible;
        window.ConversationChatPanel.Margin = new Thickness(0);
        window.ChannelCurrentHeadingText.Text = "当前会话成员（3）";
        window.ChannelMemberHelpText.Text = "你可以搜索、添加或移除私有频道成员。";
        window.ChannelParticipantList.ItemsSource = new object[]
        {
            CreateMember("林", "林乔", "linqiao", "可管理成员", canRemove: false),
            CreateMember("程", "程远", "chengyuan", "频道成员", canRemove: true),
            CreateMember("许", "许言", "xuyan", "频道成员", canRemove: true),
        };
        window.ChannelUserDirectoryList.ItemsSource = new object[]
        {
            CreateMember("周", "周沐", "zhoumu", "可添加", canInvite: true),
            CreateMember("宋", "宋然", "songran", "可添加", canInvite: true),
        };
    }

    private static void ShowReplyAndAttachmentsState(MainWindow window)
    {
        window.MessageComposerTextBox.Text = string.Empty;
        window.MessageComposerTextBox.IsEnabled = false;
        window.SelectAttachmentsButton.IsEnabled = false;
        window.MentionPickerButton.IsEnabled = false;
        window.SendMessageButton.IsEnabled = true;
        window.ReplyComposerPanel.Visibility = Visibility.Visible;
        window.ReplyComposerSenderText.Text = "回复 程远";
        window.ReplyComposerContentText.Text = "小窗口下的输入区仍应完整可用。";
        window.MentionPickerPanel.Visibility = Visibility.Visible;
        window.MentionCandidateList.ItemsSource = new object[]
        {
            new { DisplayName = "程远", UserName = "chengyuan" },
            new { DisplayName = "许言", UserName = "xuyan" },
        };
        window.SelectedMentionHeadingText.Text = "已选 2/20";
        window.SelectedMentionList.ItemsSource = new object[]
        {
            new { UserName = "chengyuan" },
            new { UserName = "xuyan" },
        };
        window.SelectedAttachmentPanel.Visibility = Visibility.Visible;
        window.SelectedAttachmentHeadingText.Text = "已选 10/10 个附件";
        window.SelectedAttachmentList.ItemsSource = Enumerable.Range(1, 10)
            .Select(index => new
            {
                DisplayName = $"评审附件-{index}.png",
                DisplaySize = "128 KB",
            })
            .ToArray();
        window.MentionPickerPanel.Visibility = Visibility.Collapsed;
        window.MentionCandidateList.ItemsSource = null;
        window.SelectedMentionList.ItemsSource = null;
    }

    private static void ShowReplyAndMentionState(MainWindow window)
    {
        ShowReplyAndAttachmentsState(window);
        window.MessageComposerTextBox.Text = "Mention reply";
        window.MessageComposerTextBox.IsEnabled = true;
        window.SelectAttachmentsButton.IsEnabled = false;
        window.MentionPickerButton.IsEnabled = true;
        window.SendMessageButton.IsEnabled = true;
        window.MentionPickerPanel.Visibility = Visibility.Visible;
        window.MentionCandidateList.ItemsSource = new object[]
        {
            new { DisplayName = "Cheng Yuan", UserName = "chengyuan" },
            new { DisplayName = "Xu Yan", UserName = "xuyan" },
        };
        window.SelectedMentionList.ItemsSource = new object[]
        {
            new { UserName = "chengyuan" },
            new { UserName = "xuyan" },
        };
        window.SelectedAttachmentPanel.Visibility = Visibility.Collapsed;
        window.SelectedAttachmentList.ItemsSource = null;
    }

    private static object CreateMember(
        string avatarText,
        string displayName,
        string userName,
        string roleLabel,
        bool canInvite = false,
        bool canRemove = false) =>
        new
        {
            AvatarText = avatarText,
            DisplayName = displayName,
            UserName = userName,
            RoleLabel = roleLabel,
            CanInvite = canInvite,
            CanRemove = canRemove,
        };

    private static object CreateMessage(
        string senderLabel,
        string content,
        string timestamp,
        bool isOwnMessage = false,
        bool showDateSeparator = false,
        string dateSeparatorLabel = "",
        bool hasReply = false,
        string replySenderLabel = "",
        string replyContent = "",
        string sendStatusLabel = "已发送",
        bool canRetry = false,
        bool isMergedWithPrevious = false) =>
        new
        {
            ServerMessageId = 1L,
            ClientMessageId = Guid.NewGuid(),
            SenderLabel = senderLabel,
            Content = content,
            Timestamp = timestamp,
            DateSeparatorLabel = dateSeparatorLabel,
            ShowDateSeparator = showDateSeparator,
            ShowNewMessageSeparator = false,
            IsOwnMessage = isOwnMessage,
            IsMergedWithPrevious = isMergedWithPrevious,
            SendStatusLabel = sendStatusLabel,
            CanRetry = canRetry,
            ReplyToMessageId = hasReply ? (long?)1L : null,
            ReplySenderLabel = replySenderLabel,
            ReplyContent = replyContent,
            HasReply = hasReply,
            IsReplyTargetAvailable = hasReply,
            CanReply = true,
            CanCopy = true,
            Links = Array.Empty<object>(),
            HasLinks = false,
            Attachments = Array.Empty<object>(),
            HasAttachments = false,
        };

    private static ClientMessageListItemPresentation CreateReadyImagePreviewMessage(
        int sourceWidth = 640,
        int sourceHeight = 360)
    {
        var conversationId = Guid.NewGuid();
        var messageClientId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var imageState = new ClientAttachmentImageViewState(
            new ClientAttachmentDownloadContext(
                conversationId,
                messageClientId,
                attachmentId,
                contextVersion: 1),
            "团队白板参考图.png",
            eligible: true);
        Assert.True(imageState.TryBeginLoad());
        Assert.True(imageState.TryApplyLoaded(CreateRepresentativeImagePreview(sourceWidth, sourceHeight)));
        var attachment = new ClientMessageAttachmentPresentation(
            messageClientId,
            attachmentId,
            "团队白板参考图.png",
            "186 KB",
            IsImage: true,
            IsDownloaded: true)
        {
            ImageState = imageState,
        };
        return new ClientMessageListItemPresentation(
            ServerMessageId: 42,
            ClientMessageId: messageClientId,
            SenderLabel: "程远",
            Content: "我把本轮界面参考图发在这里。",
            Timestamp: "10:45",
            DateSeparatorLabel: "今天",
            ShowDateSeparator: true,
            ShowNewMessageSeparator: false,
            IsMergedWithPrevious: false,
            IsOwnMessage: false,
            SendStatus: MessageSendStatus.Sent,
            SendStatusLabel: "已发送",
            CanRetry: false,
            ReplyToMessageId: null,
            ReplySenderLabel: string.Empty,
            ReplyContent: string.Empty,
            HasReply: false,
            IsReplyTargetAvailable: false,
            CanReply: true,
            CanCopy: true,
            Links: Array.Empty<ClientMessageLinkPresentation>(),
            HasLinks: false,
            Attachments: [attachment],
            HasAttachments: true);
    }

    private static void AssertWithinBounds(FrameworkElement root, FrameworkElement element)
    {
        var position = element.TransformToAncestor(root).Transform(new Point(0, 0));
        Assert.InRange(position.X, 0, root.ActualWidth);
        Assert.InRange(position.Y, 0, root.ActualHeight);
        Assert.True(position.X + element.ActualWidth <= root.ActualWidth + 0.5);
        Assert.True(position.Y + element.ActualHeight <= root.ActualHeight + 0.5);
    }

    private static void AssertDoesNotOverlap(
        FrameworkElement root,
        FrameworkElement first,
        FrameworkElement second)
    {
        var firstPosition = first.TransformToAncestor(root).Transform(new Point(0, 0));
        var secondPosition = second.TransformToAncestor(root).Transform(new Point(0, 0));
        var firstBounds = new Rect(firstPosition, first.RenderSize);
        var secondBounds = new Rect(secondPosition, second.RenderSize);

        Assert.False(firstBounds.IntersectsWith(secondBounds));
    }

    private static void SavePng(
        FrameworkElement root,
        int width,
        int height,
        string outputPath)
    {
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
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
