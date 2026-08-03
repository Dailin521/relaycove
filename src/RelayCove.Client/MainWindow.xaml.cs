using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using RelayCove.Client.Accounts;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;

namespace RelayCove.Client;

public partial class MainWindow : Window
{
    private ClientAccountShellCoordinator? accountShell;
    private Guid? pendingConversationSelectionId;
    private long lastConversationRevision;

    public MainWindow()
    {
        InitializeComponent();
    }

    internal void BindAccountShell(ClientAccountShellCoordinator coordinator)
    {
        accountShell = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        ApplyAccountShellSnapshot(coordinator.Snapshot);
        ApplyConversationListSnapshot(coordinator.ConversationList);
    }

    internal Guid? SelectedConversationId =>
        (ConversationList.SelectedItem as ClientConversationListItemPresentation)?.Id;

    internal void ApplyAccountShellSnapshot(ClientAccountShellSnapshot snapshot)
    {
        if (!snapshot.HasActiveAccount)
        {
            pendingConversationSelectionId = null;
        }

        var presentation = ClientAccountShellPresenter.Present(snapshot);
        SetLiveText(HeadingText, presentation.Heading);
        SetLiveText(DetailText, presentation.Detail);
        SetLiveText(SidebarConnectionText, presentation.ConnectionLabel);
        SetLiveText(SidebarSyncText, presentation.SyncLabel);
        SetLiveText(SidebarDisplayNameText, string.IsNullOrWhiteSpace(presentation.DisplayName)
            ? "尚未登录"
            : presentation.DisplayName);
        SetLiveText(SidebarServerText, string.IsNullOrWhiteSpace(presentation.ServerAddress)
            ? "—"
            : presentation.ServerAddress);
        SetLiveText(SidebarUnreadText, $"未读计数：{snapshot.TotalUnreadCount}");
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

    internal void ApplyConversationListSnapshot(LocalConversationListReadOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Revision < lastConversationRevision)
        {
            return;
        }

        lastConversationRevision = outcome.Revision;
        var previousSelectionId = SelectedConversationId;
        var items = ClientConversationListPresenter.Present(outcome);
        ConversationList.ItemsSource = items;
        ConversationList.IsEnabled = outcome.Status == LocalCacheOperationStatus.Ready;
        ConversationEmptyText.Visibility = items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetLiveText(ConversationEmptyText, outcome.Status switch
        {
            LocalCacheOperationStatus.Ready => "暂无会话",
            LocalCacheOperationStatus.AuthoritativeSnapshotRequired =>
                "正在等待权威会话同步…",
            LocalCacheOperationStatus.FatalScope =>
                "本地会话缓存不可用；不会显示旧账户数据。",
            _ => "会话列表暂时不可用，请稍后重试。",
        });

        var selectionId = pendingConversationSelectionId ?? previousSelectionId;
        if (selectionId is { } candidateId)
        {
            var selected = items.FirstOrDefault(item => item.Id == candidateId);
            if (selected is not null)
            {
                ConversationList.SelectedItem = selected;
                ConversationList.ScrollIntoView(selected);
                pendingConversationSelectionId = null;
            }
        }

        ApplySelectedConversation(
            ConversationList.SelectedItem as ClientConversationListItemPresentation);
    }

    internal void ShowAuthorizedNotificationTarget(
        ClientNotificationActivationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == ClientNotificationActivationKind.Message &&
            target.ConversationId is { } conversationId)
        {
            pendingConversationSelectionId = conversationId;
            var selected = ConversationList.Items
                .OfType<ClientConversationListItemPresentation>()
                .FirstOrDefault(item => item.Id == conversationId);
            if (selected is not null)
            {
                ConversationList.SelectedItem = selected;
                ConversationList.ScrollIntoView(selected);
                pendingConversationSelectionId = null;
            }
        }
        else
        {
            ConversationList.Focus();
        }

        SetLiveText(NavigationNoticeText, target.Kind switch
        {
            ClientNotificationActivationKind.Message =>
                "通知目标已通过账户与缓存授权；已选择会话，消息列表将在下一切片接入。",
            _ => "未读通知已通过账户与缓存授权；已打开真实会话列表。",
        });
    }

    internal void SetNotificationAvailability(bool? isAvailable)
    {
        SetLiveText(
            SidebarNotificationText,
            ClientAccountShellPresenter.DescribeNotificationAvailability(isAvailable));
    }

    private static void SetLiveText(TextBlock textBlock, string value)
    {
        if (string.Equals(textBlock.Text, value, StringComparison.Ordinal))
        {
            return;
        }

        textBlock.Text = value;
        UIElementAutomationPeer.FromElement(textBlock)?.RaiseAutomationEvent(
            AutomationEvents.LiveRegionChanged);
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

    private void OnConversationSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        var selected = ConversationList.SelectedItem as
            ClientConversationListItemPresentation;
        ApplySelectedConversation(selected);
    }

    private void ApplySelectedConversation(
        ClientConversationListItemPresentation? selected)
    {
        SetLiveText(
            ConversationHeadingText,
            selected is null ? "请选择会话" : selected.Name);
        SetLiveText(
            NavigationNoticeText,
            selected is null
                ? "选择左侧真实会话以准备消息视图。"
                : $"已选择{selected.TypeLabel}；消息列表将在下一切片接入，本切片不会伪造消息内容或推进已读。");
    }
}
