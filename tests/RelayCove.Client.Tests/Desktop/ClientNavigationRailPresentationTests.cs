using System.Windows;
using System.Windows.Controls;
using RelayCove.Client.Controls;
using RelayCove.Client.Presentation;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class ClientNavigationRailPresentationTests
{
    [Fact]
    public async Task NavigationRail_WhenChatAndChannelsAreClicked_RaisesTheirExactNavigationIntent()
    {
        await RunOnStaAsync(() =>
        {
            var rail = new NavigationRailControl();
            var requested = new List<ClientNavigationSection>();
            rail.NavigationRequested += (_, e) => requested.Add(e.Section);

            Click(rail.ChatButton);
            Click(rail.ChannelsButton);

            Assert.Equal(
            [
                ClientNavigationSection.Chat,
                ClientNavigationSection.Channels,
            ],
            requested);
        });
    }

    [Fact]
    public async Task NavigationRail_WhenUnavailableEntriesAreClicked_RaisesTheirExactFeatureIds()
    {
        await RunOnStaAsync(() =>
        {
            var rail = new NavigationRailControl();
            var requested = new List<ClientUiFeatureId>();
            rail.UnavailableFeatureRequested += (_, e) => requested.Add(e.FeatureId);

            Click(rail.ContactsButton);
            Click(rail.NotificationsButton);
            Click(rail.FilesButton);
            Click(rail.MoreButton);

            Assert.Equal(
            [
                ClientUiFeatureId.Contacts,
                ClientUiFeatureId.NotificationCenter,
                ClientUiFeatureId.FileCenter,
                ClientUiFeatureId.MoreNavigation,
            ],
            requested);
        });
    }

    [Fact]
    public async Task NavigationRail_WhenUnavailableEntryIsClicked_DoesNotChangeSelectedSection()
    {
        await RunOnStaAsync(() =>
        {
            var rail = new NavigationRailControl
            {
                SelectedSection = ClientNavigationSection.Channels,
            };

            Click(rail.ContactsButton);

            Assert.Equal(ClientNavigationSection.Channels, rail.SelectedSection);
        });
    }

    [Fact]
    public async Task UiNoticeHost_WhenUnavailableFeatureIsShown_UsesAccessibleNonModalMessageAndCanBeDismissed()
    {
        await RunOnStaAsync(() =>
        {
            var host = new UiNoticeHost();

            host.ShowUnavailableFeature("联系人");

            Assert.True(host.IsNoticeVisible);
            Assert.Equal("联系人功能暂未开放", host.Message);
            Assert.False(host.IsHitTestVisible);
            Assert.Equal(
                "Polite",
                System.Windows.Automation.AutomationProperties.GetLiveSetting(host.NoticeText)
                    .ToString());

            host.HideNotice();

            Assert.False(host.IsNoticeVisible);
            Assert.Equal(string.Empty, host.Message);
        });
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
