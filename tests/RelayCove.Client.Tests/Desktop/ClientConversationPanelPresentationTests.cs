using System.Windows;
using System.Windows.Controls;
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
                window.MessageComposerTextBox.Text = tokenAlreadyExists ? "请确认 @alice " : "请确认 ";
                window.MessageComposerTextBox.SelectionStart = window.MessageComposerTextBox.Text.Length;
                window.MentionPickerPanel.Visibility = Visibility.Visible;
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
                Assert.Same(window.MessageComposerTextBox, System.Windows.Input.Keyboard.FocusedElement);
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
