using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using RelayCove.Client.Accounts;
using RelayCove.Client.Controls;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class MessageListControlPresentationTests
{
    [Fact]
    public async Task MessageListControl_WhenMessageHasReplyLinksAndActions_RendersThemAndForwardsCompatibleIntents()
    {
        await RunOnStaAsync(() =>
        {
            var clientMessageId = Guid.NewGuid();
            var link = new ClientMessageLinkPresentation("https://relaycove.example.test/ui", "https://relaycove.example.test/ui");
            var control = new MessageListControl();
            AddRc25Resources(control);
            control.List.ItemsSource = new[]
            {
                CreateMessage(
                    clientMessageId,
                    canRetry: true,
                    links: new[] { link },
                    hasReply: true),
            };
            var interactions = new List<ClientControlInteractionRequestedEventArgs>();
            control.InteractionRequested += (_, e) => interactions.Add(e);
            var host = CreateHost(control);
            try
            {
                host.Show();
                host.UpdateLayout();

                Assert.Contains(FindVisualDescendants<TextBlock>(control), textBlock => textBlock.Text == "发送失败");
                Assert.Contains(FindVisualDescendants<TextBlock>(control), textBlock => textBlock.Text == "程远");
                Assert.Contains(FindVisualDescendants<TextBlock>(control), textBlock => textBlock.Text == "上条消息的摘要");

                var linkButton = Assert.Single(FindVisualDescendants<Button>(control), button => Equals(button.DataContext, link));
                var replyReferenceButton = Assert.Single(FindVisualDescendants<Button>(control), button => Equals(button.Tag, 41L));
                var replyButton = Assert.Single(FindVisualDescendants<Button>(control), button => Equals(button.Content, "回复"));
                var copyButton = Assert.Single(FindVisualDescendants<Button>(control), button => Equals(button.Content, "复制"));
                var retryButton = Assert.Single(FindVisualDescendants<Button>(control), button => Equals(button.Content, "重试"));

                Assert.Equal(42L, replyButton.Tag);
                Assert.Equal(clientMessageId, retryButton.Tag);
                Assert.Equal("定位被回复的消息", replyReferenceButton.ToolTip);
                Assert.Equal(link.DisplayText, AutomationProperties.GetName(linkButton));

                interactions.Clear();
                RaiseClick(replyButton);
                RaiseClick(copyButton);
                RaiseClick(retryButton);
                RaiseClick(replyReferenceButton);
                RaiseClick(linkButton);

                Assert.Equal(
                ["ReplyRequested", "CopyRequested", "RetryRequested", "ReplyReferenceClicked", "LinkClicked"],
                interactions.Select(interaction => interaction.Interaction));
                Assert.Same(replyButton, interactions[0].InteractionSource);
                Assert.Same(replyReferenceButton, interactions[3].InteractionSource);
                Assert.Same(linkButton, interactions[4].InteractionSource);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public async Task MessageListControl_WhenMessageCanReplyOrCopyButCannotRetry_KeepsActionsAvailableThroughHover()
    {
        await RunOnStaAsync(() =>
        {
            var control = new MessageListControl();
            AddRc25Resources(control);
            control.List.ItemsSource = new[]
            {
                CreateMessage(Guid.NewGuid(), canRetry: false, links: Array.Empty<ClientMessageLinkPresentation>(), hasReply: false),
            };
            var host = CreateHost(control);
            try
            {
                host.Show();
                host.UpdateLayout();

                var buttons = FindVisualDescendants<Button>(control).ToArray();
                var actionBar = Assert.Single(FindVisualDescendants<Border>(control), border => border.Name == "MessageActionBar");
                Assert.Equal(Visibility.Visible, Assert.Single(buttons, button => Equals(button.Content, "回复")).Visibility);
                Assert.Equal(Visibility.Visible, Assert.Single(buttons, button => Equals(button.Content, "复制")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.Single(buttons, button => Equals(button.Content, "重试")).Visibility);
                Assert.Equal(Visibility.Collapsed, actionBar.Visibility);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public async Task MessageListControl_WhenMessageRowIsSelected_KeepsRowPresentationNeutral()
    {
        await RunOnStaAsync(() =>
        {
            var control = new MessageListControl();
            AddRc25Resources(control);
            var message = CreateMessage(
                Guid.NewGuid(),
                canRetry: false,
                links: Array.Empty<ClientMessageLinkPresentation>(),
                hasReply: false);
            control.List.ItemsSource = new[] { message };
            var host = CreateHost(control);
            try
            {
                host.Show();
                host.UpdateLayout();
                control.List.SelectedItem = message;

                var container = Assert.IsType<ListBoxItem>(
                    control.List.ItemContainerGenerator.ContainerFromItem(message));
                var background = Assert.IsType<SolidColorBrush>(container.Background);
                var border = Assert.IsType<SolidColorBrush>(container.BorderBrush);

                Assert.Equal(Colors.Transparent, background.Color);
                Assert.Equal(Colors.Transparent, border.Color);
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static ClientMessageListItemPresentation CreateMessage(
        Guid clientMessageId,
        bool canRetry,
        IReadOnlyList<ClientMessageLinkPresentation> links,
        bool hasReply) =>
        new(
            ServerMessageId: 42,
            ClientMessageId: clientMessageId,
            SenderLabel: "林乔",
            Content: "消息正文",
            Timestamp: "10:42",
            DateSeparatorLabel: string.Empty,
            ShowDateSeparator: false,
            ShowNewMessageSeparator: false,
            IsMergedWithPrevious: false,
            IsOwnMessage: true,
            SendStatus: canRetry ? MessageSendStatus.Failed : MessageSendStatus.Sent,
            SendStatusLabel: canRetry ? "发送失败" : "已发送",
            CanRetry: canRetry,
            ReplyToMessageId: hasReply ? 41 : null,
            ReplySenderLabel: "程远",
            ReplyContent: "上条消息的摘要",
            HasReply: hasReply,
            IsReplyTargetAvailable: hasReply,
            CanReply: true,
            CanCopy: true,
            Links: links,
            HasLinks: links.Count != 0,
            Attachments: [],
            HasAttachments: false);

    private static Window CreateHost(FrameworkElement content) =>
        new()
        {
            Width = 720,
            Height = 420,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = content,
        };

    private static void AddRc25Resources(FrameworkElement element)
    {
        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/RelayCove.Client;component/Resources/ClientTheme.xaml", UriKind.Relative),
        });
        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/RelayCove.Client;component/Resources/ClientControls.xaml", UriKind.Relative),
        });
    }

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
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
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
