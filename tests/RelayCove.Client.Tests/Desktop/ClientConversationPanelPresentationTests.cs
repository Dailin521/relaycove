using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RelayCove.Client;
using RelayCove.Client.Controls;
using RelayCove.Client.Presentation;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class ClientConversationPanelPresentationTests
{
    [Fact]
    public async Task MainWindow_WhenConversationSearchShortcutIsApplied_FocusesLocalConversationSearch()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                window.ConversationSearchTextBox.Text = "retain selection";
                window.FocusConversationSearch();

                Assert.Same(
                    window.ConversationSearchTextBox,
                    System.Windows.Input.Keyboard.FocusedElement);
                Assert.Equal("retain selection", window.ConversationSearchTextBox.SelectedText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ConversationFilter_WhenItHidesSelectedConversation_PreservesSelectedConversationAndHeader()
    {
        await RunOnStaAsync(() =>
        {
            var directId = Guid.NewGuid();
            var window = CreateVisibleWindow();
            try
            {
                SetPrivateField(window, "selectedConversationId", directId);
                window.ApplyConversationListSnapshot(new LocalConversationListReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    [
                        CreateConversation(directId, ConversationType.Direct, "Alice"),
                        CreateConversation(Guid.NewGuid(), ConversationType.PublicChannel, "General"),
                    ],
                    TotalUnreadCount: 0,
                    Revision: 1));

                Assert.Equal(directId, window.SelectedConversationId);
                Assert.Equal("Alice", window.ConversationHeadingText.Text);

                window.ChannelConversationFilterButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

                Assert.Equal(directId, window.SelectedConversationId);
                Assert.Equal("Alice", window.ConversationHeadingText.Text);
                Assert.Null(window.ConversationList.SelectedItem);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenSettingsNavigationIsRequested_OpensSettingsDrawerWithoutChangingConversation()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                window.ApplicationNavigation.RaiseEvent(new ClientNavigationRequestedEventArgs(
                    NavigationRailControl.NavigationRequestedEvent,
                    window.ApplicationNavigation,
                    ClientNavigationSection.Settings));

                Assert.Equal(Visibility.Visible, window.SettingsOverlay.Visibility);
                Assert.Equal(ClientNavigationSection.Settings, window.ApplicationNavigation.SelectedSection);

                window.CloseSettingsButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

                Assert.Equal(Visibility.Collapsed, window.SettingsOverlay.Visibility);
                Assert.Equal(ClientNavigationSection.Chat, window.ApplicationNavigation.SelectedSection);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ChatHeader_WhenConversationIsSelected_ShowsOnlyConversationTitle()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                window.ConversationHeadingText.Text = "公开测试频道";
                window.NavigationNoticeText.Text = "正在读取真实消息。";
                window.ConversationMembersSummaryText.Text = "成员（5）：dal、lq";
                window.UpdateLayout();

                Assert.Equal(Visibility.Visible, window.ConversationHeadingText.Visibility);
                Assert.Equal(Visibility.Collapsed, window.NavigationNoticeText.Visibility);
                Assert.Equal(Visibility.Collapsed, window.ConversationMembersSummaryText.Visibility);
                Assert.True(window.OpenChannelPanelButton.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ConversationGroupHeader_WhenCheckedStateChanges_TogglesTheMatchingGroup()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                window.ApplyConversationListSnapshot(new LocalConversationListReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    [CreateConversation(Guid.NewGuid(), ConversationType.PublicChannel, "General")],
                    TotalUnreadCount: 0,
                    Revision: 1));
                window.UpdateLayout();

                var group = FindVisualDescendants<Expander>(window.ConversationList)
                    .Single(candidate => string.Equals(candidate.Tag as string, "公开频道", StringComparison.Ordinal));
                var header = FindVisualDescendants<ToggleButton>(group).Single();

                Assert.True(group.IsExpanded);
                header.IsChecked = false;
                Assert.False(group.IsExpanded);
                header.IsChecked = true;
                Assert.True(group.IsExpanded);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainWindow_WhenMentionCandidateIsSelected_ClosesPickerAndKeepsComposerFocused(
        bool tokenAlreadyExists)
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                var candidate = new MentionCandidateDto(Guid.NewGuid(), "alice", "Alice");
                SetPrivateField(window, "composerAvailable", true);
                window.MessageComposerTextBox.IsEnabled = true;
                window.MessageComposerTextBox.Text = tokenAlreadyExists ? "请确认 @alice " : "请确认 @";
                window.MessageComposerTextBox.SelectionStart = window.MessageComposerTextBox.Text.Length;
                window.MentionPickerPanel.Visibility = Visibility.Visible;
                window.MentionPickerPopup.IsOpen = true;
                window.MentionCandidateList.ItemsSource = new[] { candidate };
                window.UpdateLayout();

                var selectButton = new Button { DataContext = candidate };
                InvokePrivate(
                    window,
                    "OnMentionCandidateClicked",
                    selectButton,
                    new RoutedEventArgs(Button.ClickEvent));

                Assert.Contains("@alice", window.MessageComposerTextBox.Text, StringComparison.Ordinal);
                Assert.Equal(Visibility.Collapsed, window.MentionPickerPanel.Visibility);
                Assert.False(window.MentionPickerPopup.IsOpen);
                Assert.Same(window.MessageComposerTextBox, System.Windows.Input.Keyboard.FocusedElement);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenMentionButtonIsClicked_OpensStablePickerAndTogglesIt()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                SetPrivateField(window, "composerAvailable", true);
                window.MessageComposerTextBox.IsEnabled = true;
                window.MentionPickerButton.IsEnabled = true;

                window.MentionPickerButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));

                Assert.True(window.MentionPickerPopup.IsOpen);
                Assert.Equal(Visibility.Visible, window.MentionPickerPanel.Visibility);
                Assert.Equal("@", window.MessageComposerTextBox.Text);

                window.MentionPickerButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));

                Assert.False(window.MentionPickerPopup.IsOpen);
                Assert.Equal(Visibility.Collapsed, window.MentionPickerPanel.Visibility);
                Assert.Equal("@", window.MessageComposerTextBox.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static LocalConversationListItem CreateConversation(
        Guid id,
        ConversationType type,
        string name) =>
        new(
            id,
            type,
            name,
            null,
            0,
            MessageType.Text,
            "Preview",
            DateTimeOffset.Parse("2026-08-09T12:00:00Z"),
            1,
            false,
            DateTimeOffset.Parse("2026-08-09T12:00:00Z"));

    private static MainWindow CreateVisibleWindow()
    {
        var window = new MainWindow
        {
            Width = 1280,
            Height = 720,
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        window.LoginPanel.Visibility = Visibility.Collapsed;
        window.AccountPanel.Visibility = Visibility.Visible;
        window.Show();
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        return window;
    }

    private static void SetPrivateField(MainWindow window, string fieldName, object? value)
    {
        var field = typeof(MainWindow).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException($"Expected field '{fieldName}'.");
        field.SetValue(window, value);
    }

    private static void InvokePrivate(MainWindow window, string methodName, params object[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException($"Expected method '{methodName}'.");
        method.Invoke(window, arguments);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
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
