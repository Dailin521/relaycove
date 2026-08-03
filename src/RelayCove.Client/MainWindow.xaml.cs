using System.Windows;
using RelayCove.Client.Accounts;
using RelayCove.Client.Notifications;

namespace RelayCove.Client;

public partial class MainWindow : Window
{
    private ClientAccountShellCoordinator? accountShell;

    public MainWindow()
    {
        InitializeComponent();
    }

    internal void BindAccountShell(ClientAccountShellCoordinator coordinator)
    {
        accountShell = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        ApplyAccountShellSnapshot(coordinator.Snapshot);
    }

    internal void ApplyAccountShellSnapshot(ClientAccountShellSnapshot snapshot)
    {
        var presentation = ClientAccountShellPresenter.Present(snapshot);
        HeadingText.Text = presentation.Heading;
        DetailText.Text = presentation.Detail;
        SidebarConnectionText.Text = presentation.ConnectionLabel;
        SidebarSyncText.Text = presentation.SyncLabel;
        SidebarDisplayNameText.Text = string.IsNullOrWhiteSpace(presentation.DisplayName)
            ? "尚未登录"
            : presentation.DisplayName;
        SidebarServerText.Text = string.IsNullOrWhiteSpace(presentation.ServerAddress)
            ? "—"
            : presentation.ServerAddress;
        LoginPanel.Visibility = presentation.ShowLogin
            ? Visibility.Visible
            : Visibility.Collapsed;
        AccountPanel.Visibility = presentation.ShowLogin
            ? Visibility.Collapsed
            : Visibility.Visible;
        BusyIndicator.Visibility = presentation.IsBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoginButton.IsEnabled = !presentation.IsBusy;
        ServerAddressTextBox.IsEnabled = !presentation.IsBusy;
        UserNameTextBox.IsEnabled = !presentation.IsBusy;
        PasswordInput.IsEnabled = !presentation.IsBusy;
        RetryButton.IsEnabled = presentation.CanRetry;
        LogoutButton.IsEnabled = presentation.CanLogout;
    }

    internal void ShowAuthorizedNotificationTarget(
        ClientNotificationActivationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        NavigationNoticeText.Text = target.Kind switch
        {
            ClientNotificationActivationKind.Message =>
                "通知目标已通过账户与缓存授权；消息视图将在下一切片接入。",
            _ => "未读通知已通过账户与缓存授权；会话列表将在下一切片接入。",
        };
    }

    internal void SetNotificationAvailability(bool? isAvailable)
    {
        SidebarNotificationText.Text =
            ClientAccountShellPresenter.DescribeNotificationAvailability(isAvailable);
    }

    private async void OnLoginClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var coordinator = accountShell;
        if (coordinator is null)
        {
            return;
        }

        var password = PasswordInput.Password;
        PasswordInput.Clear();
        try
        {
            await coordinator.LoginAsync(
                ServerAddressTextBox.Text,
                UserNameTextBox.Text,
                password);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            if (accountShell is not null)
            {
                await accountShell.RetryAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void OnLogoutClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            if (accountShell is not null)
            {
                await accountShell.LogoutAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
