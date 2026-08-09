using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using RelayCove.Client;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Users;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class MainWindowNavigationPresentationTests
{
    [Fact]
    public async Task ApplyChannelParticipantPresentation_WhenDirectConversationIsSelected_ShowsAllParticipants()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                var first = new UserDirectoryEntryDto(Guid.NewGuid(), "alice", "Alice");
                var second = new UserDirectoryEntryDto(Guid.NewGuid(), "bob", "Bob");
                SetPrivateField(
                    window,
                    "channelParticipants",
                    new ConversationParticipantListResponse(
                        Guid.NewGuid(),
                        ConversationType.Direct,
                        CanManageMembers: false,
                        Participants: [first, second]));

                InvokePrivate(window, "ApplyChannelParticipantPresentation");

                var participants = window.ChannelParticipantList.ItemsSource!
                    .Cast<object>()
                    .ToArray();
                Assert.Equal(2, participants.Length);
                Assert.Equal(Visibility.Collapsed, window.ChannelUserDirectorySection.Visibility);
                Assert.Equal("私聊成员（2）", window.ChannelCurrentHeadingText.Text);
                Assert.Equal("私聊显示全部参与成员。", window.ChannelMemberHelpText.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ChannelMemberOperation_WhenDrawerClosesOrConversationChanges_RejectsStaleRefresh()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                var conversationA = Guid.NewGuid();
                var conversationB = Guid.NewGuid();
                var participants = new ConversationParticipantListResponse(
                    conversationA,
                    ConversationType.PrivateChannel,
                    CanManageMembers: true,
                    Participants: Array.Empty<UserDirectoryEntryDto>());
                SetPrivateField(window, "selectedConversationId", conversationA);
                window.ChannelOverlay.Visibility = Visibility.Visible;

                Assert.True(InvokePrivate<bool>(
                    window,
                    "IsCurrentChannelMemberOperation",
                    participants,
                    null!));

                window.ChannelOverlay.Visibility = Visibility.Collapsed;
                Assert.False(InvokePrivate<bool>(
                    window,
                    "IsCurrentChannelMemberOperation",
                    participants,
                    null!));

                window.ChannelOverlay.Visibility = Visibility.Visible;
                SetPrivateField(window, "selectedConversationId", conversationB);
                Assert.False(InvokePrivate<bool>(
                    window,
                    "IsCurrentChannelMemberOperation",
                    participants,
                    null!));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ConversationGroupExpander_WhenCollapsed_RemembersStateAcrossMaterialization()
    {
        await RunOnStaAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                var first = new Expander { Tag = "公开频道" };
                InvokePrivate(window, "OnConversationGroupExpanderLoaded", first, new RoutedEventArgs());
                Assert.True(first.IsExpanded);

                InvokePrivate(window, "OnConversationGroupCollapsed", first, new RoutedEventArgs());

                var rematerialized = new Expander { Tag = "公开频道" };
                InvokePrivate(
                    window,
                    "OnConversationGroupExpanderLoaded",
                    rematerialized,
                    new RoutedEventArgs());
                Assert.False(rematerialized.IsExpanded);

                InvokePrivate(
                    window,
                    "OnConversationGroupExpanded",
                    rematerialized,
                    new RoutedEventArgs());
                var expandedAgain = new Expander { Tag = "公开频道" };
                InvokePrivate(
                    window,
                    "OnConversationGroupExpanderLoaded",
                    expandedAgain,
                    new RoutedEventArgs());
                Assert.True(expandedAgain.IsExpanded);
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
            ShowActivated = false,
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
        return window;
    }

    private static void SetPrivateField(MainWindow window, string fieldName, object value) =>
        GetPrivateField(fieldName).SetValue(window, value);

    private static FieldInfo GetPrivateField(string fieldName) =>
        typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Expected field '{fieldName}'.");

    private static void InvokePrivate(MainWindow window, string methodName, params object[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Expected method '{methodName}'.");
        method.Invoke(window, arguments);
    }

    private static T InvokePrivate<T>(MainWindow window, string methodName, params object[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Expected method '{methodName}'.");
        return Assert.IsType<T>(method.Invoke(window, arguments));
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
