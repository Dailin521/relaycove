using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using RelayCove.Client.Controls;
using ShapePath = System.Windows.Shapes.Path;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class SettingsPanelControlPresentationTests
{
    [Fact]
    public async Task SettingsPanelControl_WhenPresentationPropertiesAreSet_ExposesBoundAccountAndActionState()
    {
        await RunOnStaAsync(() =>
        {
            var panel = new SettingsPanelControl
            {
                DisplayName = "林乔",
                ServerAddress = "https://relay.example.com/",
                ConnectionStatus = "实时连接：已连接",
                SyncStatus = "同步：已完成",
                NotificationStatus = "系统通知：可用",
                UpdateStatus = "更新：已是最新版本",
                CanCheckForUpdates = true,
                CanReconnect = true,
                CanExitAccount = true,
                HasOptionalUpdateAction = true,
                UpdateActionLabel = "关闭并更新",
                IsUpdateActionEnabled = true,
            };

            Assert.Equal("林乔", panel.DisplayName);
            Assert.Equal("https://relay.example.com/", panel.ServerAddress);
            Assert.Equal("实时连接：已连接", panel.ConnectionStatus);
            Assert.Equal("同步：已完成", panel.SyncStatus);
            Assert.Equal("系统通知：可用", panel.NotificationStatus);
            Assert.Equal("更新：已是最新版本", panel.UpdateStatus);
            Assert.True(panel.CheckForUpdatesButton.IsEnabled);
            Assert.True(panel.ReconnectButton.IsEnabled);
            Assert.True(panel.ExitAccountButton.IsEnabled);
            Assert.True(panel.HasOptionalUpdateAction);
            Assert.Equal("关闭并更新", panel.UpdateActionLabel);
            Assert.True(panel.OptionalUpdateActionButton.IsEnabled);
        });
    }

    [Fact]
    public async Task SettingsPanelControl_WhenActionButtonsAreClicked_RaisesCoordinatorIntent()
    {
        await RunOnStaAsync(() =>
        {
            var panel = new SettingsPanelControl
            {
                CanCheckForUpdates = true,
                CanReconnect = true,
                CanExitAccount = true,
            };
            var closeRequests = 0;
            var updateRequests = 0;
            var reconnectRequests = 0;
            var exitRequests = 0;
            var optionalUpdateRequests = 0;
            panel.CloseRequested += (_, _) => closeRequests++;
            panel.CheckForUpdatesRequested += (_, _) => updateRequests++;
            panel.ReconnectRequested += (_, _) => reconnectRequests++;
            panel.ExitAccountRequested += (_, _) => exitRequests++;
            panel.OptionalUpdateActionRequested += (_, _) => optionalUpdateRequests++;

            RaiseClick(panel.CloseButton);
            RaiseClick(panel.CheckForUpdatesButton);
            RaiseClick(panel.ReconnectButton);
            RaiseClick(panel.ExitAccountButton);
            RaiseClick(panel.OptionalUpdateActionButton);

            Assert.Equal(1, closeRequests);
            Assert.Equal(1, updateRequests);
            Assert.Equal(1, reconnectRequests);
            Assert.Equal(1, exitRequests);
            Assert.Equal(1, optionalUpdateRequests);
        });
    }

    [Fact]
    public async Task SettingsPanelControl_WhenRendered_UsesAccessibleIconOnlyCloseButton()
    {
        await RunOnStaAsync(() =>
        {
            var panel = new SettingsPanelControl();
            var closeButton = panel.CloseButton;

            var icon = Assert.IsType<ShapePath>(closeButton.Content);
            Assert.Equal(32d, closeButton.Width);
            Assert.Equal(32d, closeButton.Height);
            Assert.Equal("关闭", closeButton.ToolTip);
            Assert.Equal("关闭账户与设置侧栏", AutomationProperties.GetName(closeButton));
            Assert.NotEqual(DependencyProperty.UnsetValue, icon.ReadLocalValue(ShapePath.DataProperty));
        });
    }

    private static void RaiseClick(Button button) =>
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
