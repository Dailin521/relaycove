using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class ClientUiSnapshotTests
{
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
                Assert.Equal(new Thickness(0, 0, 372, 0), window.ConversationChatPanel.Margin);

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

    [Theory]
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
                    ShowExpandedComposerState(window);
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

                var outputDirectory = Environment.GetEnvironmentVariable(
                    "RELAYCOVE_UI_SNAPSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                    SavePng(
                        root,
                        (int)Math.Ceiling(root.ActualWidth),
                        (int)Math.Ceiling(root.ActualHeight),
                        Path.Combine(outputDirectory, $"main-window-{width}x{height}.png"));
                }
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

    private static void ShowRepresentativeMemberDrawer(MainWindow window)
    {
        window.ChannelOverlay.Visibility = Visibility.Visible;
        window.ConversationChatPanel.Margin = new Thickness(0, 0, 372, 0);
        window.ChannelCurrentHeadingText.Text = "当前会话成员（3）";
        window.ChannelMemberHelpText.Text = "你可以搜索、添加或移除私有频道成员。";
        window.ChannelParticipantList.ItemsSource = new object[]
        {
            CreateMember("林", "林乔", "linqiao", "频道管理员", canRemove: false),
            CreateMember("程", "程远", "chengyuan", "成员", canRemove: true),
            CreateMember("许", "许言", "xuyan", "成员", canRemove: true),
        };
        window.ChannelUserDirectoryList.ItemsSource = new object[]
        {
            CreateMember("周", "周沐", "zhoumu", "可添加", canInvite: true),
            CreateMember("宋", "宋然", "songran", "可添加", canInvite: true),
        };
    }

    private static void ShowExpandedComposerState(MainWindow window)
    {
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

    private static void AssertWithinBounds(FrameworkElement root, FrameworkElement element)
    {
        var position = element.TransformToAncestor(root).Transform(new Point(0, 0));
        Assert.InRange(position.X, 0, root.ActualWidth);
        Assert.InRange(position.Y, 0, root.ActualHeight);
        Assert.True(position.X + element.ActualWidth <= root.ActualWidth + 0.5);
        Assert.True(position.Y + element.ActualHeight <= root.ActualHeight + 0.5);
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
