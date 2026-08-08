using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
    public async Task NavigationRail_WhenKeyboardFocused_ExposesFocusableAccessibleButtons()
    {
        await RunOnStaAsync(() =>
        {
            var rail = new NavigationRailControl();
            var window = CreateHostWindow(rail);
            try
            {
                window.Show();
                window.Activate();
                window.UpdateLayout();

                var buttons = new (Button Button, string ToolTip, string AutomationName)[]
                {
                    (rail.AccountButton, "账户与设置", "打开账户与设置"),
                    (rail.ChatButton, "聊天", "聊天"),
                    (rail.ContactsButton, "联系人（暂未开放）", "联系人，暂未开放"),
                    (rail.ChannelsButton, "频道", "频道"),
                    (rail.NotificationsButton, "通知中心（暂未开放）", "通知中心，暂未开放"),
                    (rail.FilesButton, "文件中心（暂未开放）", "文件中心，暂未开放"),
                    (rail.SettingsButton, "设置", "设置"),
                    (rail.MoreButton, "更多（暂未开放）", "更多，暂未开放"),
                };

                foreach (var (button, toolTip, automationName) in buttons)
                {
                    Assert.True(button.IsEnabled);
                    Assert.True(button.Focusable);
                    Assert.True(button.IsTabStop);
                    Assert.Equal(toolTip, button.ToolTip);
                    Assert.Equal(automationName, AutomationProperties.GetName(button));
                    Assert.True(button.Focus());
                    Assert.Same(button, Keyboard.FocusedElement);
                }
            }
            finally
            {
                window.Close();
            }
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

    [Fact]
    public async Task NavigationRail_WhenUnavailableFeatureIsRepeated_ResetsNoticeDurationWithoutNavigationSideEffects()
    {
        await RunOnStaAsync(() =>
        {
            var rail = new NavigationRailControl
            {
                SelectedSection = ClientNavigationSection.Channels,
            };
            var host = new UiNoticeHost();
            var navigationRequests = 0;
            var unavailableFeatureIds = new List<ClientUiFeatureId>();
            rail.NavigationRequested += (_, _) => navigationRequests++;
            rail.UnavailableFeatureRequested += (_, e) =>
            {
                unavailableFeatureIds.Add(e.FeatureId);
                host.ShowUnavailableFeature(e.DisplayName);
            };

            Click(rail.ContactsButton);
            PumpDispatcher(TimeSpan.FromMilliseconds(2100));
            Click(rail.ContactsButton);

            // The original approximately-three-second deadline has passed, but the second
            // activation must keep the non-modal notice visible for its own full duration.
            PumpDispatcher(TimeSpan.FromMilliseconds(1200));

            Assert.True(host.IsNoticeVisible);
            Assert.Equal("联系人功能暂未开放", host.Message);
            Assert.Equal(ClientNavigationSection.Channels, rail.SelectedSection);
            Assert.Equal(0, navigationRequests);
            Assert.Equal(
            [
                ClientUiFeatureId.Contacts,
                ClientUiFeatureId.Contacts,
            ],
            unavailableFeatureIds);

            PumpDispatcher(TimeSpan.FromMilliseconds(2100));

            Assert.False(host.IsNoticeVisible);
            Assert.Equal(string.Empty, host.Message);
            Assert.Equal(ClientNavigationSection.Channels, rail.SelectedSection);
            Assert.Equal(0, navigationRequests);
        });
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Window CreateHostWindow(object content) =>
        new()
        {
            Content = content,
            Width = 240,
            Height = 600,
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
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
