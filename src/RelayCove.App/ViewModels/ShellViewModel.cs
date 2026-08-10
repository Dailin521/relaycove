using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IClientSession _session;
    private readonly ILastRealmStore _lastRealmStore;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenSource? _navigationCancellation;
    private int _initialized;
    private int _loginInFlight;
    private bool _disposed;

    public ShellViewModel(IClientSession session, ILastRealmStore lastRealmStore, IUiDispatcher dispatcher)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _lastRealmStore = lastRealmStore ?? throw new ArgumentNullException(nameof(lastRealmStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Realm = _lastRealmStore.Get();
        _session.StateChanged += OnStateChanged;
        Project(_session.State);
    }

    public ObservableCollection<ChannelItem> Channels { get; } = [];
    public ObservableCollection<TopicItem> Topics { get; } = [];
    public ObservableCollection<NavigationItem> DirectMessages { get; } = [];
    public ObservableCollection<MessageItem> Messages { get; } = [];

    [ObservableProperty]
    public partial string Realm { get; set; } = PreferencesLastRealmStore.DefaultRealm;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ComposerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? LoginError { get; set; }

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "已注销";

    [ObservableProperty]
    public partial bool IsLoggedIn { get; set; }

    [ObservableProperty]
    public partial bool ClearCacheConfirmationVisible { get; set; }

    [ObservableProperty]
    public partial ChannelItem? SelectedChannel { get; set; }

    [ObservableProperty]
    public partial TopicItem? SelectedTopic { get; set; }

    [ObservableProperty]
    public partial NavigationItem? SelectedDirectMessage { get; set; }

    public bool LoginVisible => !IsLoggedIn;
    public bool MainVisible => IsLoggedIn;
    public bool HasSelectedConversation => _session.SelectedConversation is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        try
        {
            await _session.RestoreAsync(cancellationToken);
        }
        catch (CredentialVaultException)
        {
            LoginError = "保存的登录凭据不可用，请重新登录。";
        }
        catch (GatewayException exception)
        {
            LoginError = DescribeGatewayFailure(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            LoginError = "本地缓存不可用，请重新登录。";
        }
        finally
        {
            Project(_session.State);
        }
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _loginInFlight, 1) != 0) return;
        LoginError = null;
        try
        {
            await _session.LoginAsync(Realm, Email, Password, cancellationToken);
            _lastRealmStore.Set(Realm);
        }
        catch (CredentialVaultException)
        {
            LoginError = "无法安全保存登录凭据。";
        }
        catch (GatewayException exception)
        {
            LoginError = DescribeGatewayFailure(exception);
        }
        catch (ArgumentException)
        {
            LoginError = "请输入有效的 HTTPS Realm、邮箱和密码。";
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("active", StringComparison.OrdinalIgnoreCase))
        {
            LoginError = "当前已有活动会话，请先注销。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoginError = "登录已取消。";
        }
        catch (Exception)
        {
            LoginError = "本地缓存不可用，请重启应用后重试。";
        }
        finally
        {
            Password = string.Empty;
            Project(_session.State);
            Volatile.Write(ref _loginInFlight, 0);
        }
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task LoadOlderAsync(CancellationToken cancellationToken)
    {
        if (_session.SelectedConversation is null) return;
        await ExecuteSessionActionAsync(() => _session.LoadOlderAsync(cancellationToken));
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        var content = ComposerText;
        if (string.IsNullOrWhiteSpace(content)) return;
        await ExecuteSessionActionAsync(async () =>
        {
            await _session.SendAsync(content, cancellationToken);
            ComposerText = string.Empty;
        });
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private Task MarkReadAsync(CancellationToken cancellationToken) =>
        ExecuteSessionActionAsync(() => _session.MarkDisplayedReadAsync(cancellationToken));

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task LogoutAsync()
    {
        await ExecuteSessionActionAsync(
            () => _session.LogoutAsync(),
            "注销未完全完成，请重试以安全删除凭据并锁定本地缓存。");
    }

    [RelayCommand]
    private void RequestClearCache() => ClearCacheConfirmationVisible = true;

    [RelayCommand]
    private void CancelClearCache() => ClearCacheConfirmationVisible = false;

    [RelayCommand]
    private void RecoverOutbox(MessageItem? message)
    {
        if (message?.CanRecover == true)
        {
            ComposerText = message.Content;
        }
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task ConfirmClearCacheAsync(CancellationToken cancellationToken)
    {
        if (!ClearCacheConfirmationVisible) return;
        await ExecuteSessionActionAsync(() => _session.ClearLocalCacheAsync(cancellationToken));
        ClearCacheConfirmationVisible = false;
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(LoginVisible));
        OnPropertyChanged(nameof(MainVisible));
    }

    partial void OnSelectedChannelChanged(ChannelItem? value)
    {
        _ = SelectChannelAsync(value);
    }

    partial void OnSelectedTopicChanged(TopicItem? value)
    {
        if (value is not null)
        {
            _ = SelectConversationAsync(new ChannelTopic(value.ChannelId, value.Topic));
        }
    }

    partial void OnSelectedDirectMessageChanged(NavigationItem? value)
    {
        if (value is not null)
        {
            _ = SelectConversationAsync(value.Conversation);
        }
    }

    private async Task SelectChannelAsync(ChannelItem? channel)
    {
        CancelNavigation();
        LoginError = null;
        if (channel is null)
        {
            Topics.Clear();
            return;
        }

        _navigationCancellation = new CancellationTokenSource();
        try
        {
            var topics = await _session.LoadTopicsAsync(channel.ChannelId, _navigationCancellation.Token);
            if (_navigationCancellation.IsCancellationRequested || SelectedChannel != channel) return;
            _dispatcher.Dispatch(() =>
            {
                Topics.Clear();
                foreach (var topic in topics.OrderByDescending(item => item.MaxMessageId).ThenBy(item => item.Topic, StringComparer.Ordinal))
                {
                    Topics.Add(new TopicItem(topic.ChannelId, topic.Topic, topic.MaxMessageId));
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (GatewayException exception)
        {
            _dispatcher.Dispatch(() => LoginError = DescribeGatewayFailure(exception));
        }
        catch (InvalidOperationException exception)
        {
            _dispatcher.Dispatch(() => LoginError = exception.Message);
        }
        catch (Exception)
        {
            _dispatcher.Dispatch(() => LoginError = "无法读取频道话题，请稍后重试。");
        }
    }

    private Task SelectConversationAsync(ConversationKey conversation) =>
        ExecuteSessionActionAsync(() => _session.SelectConversationAsync(conversation));

    private async Task ExecuteSessionActionAsync(Func<Task> action, string? failureMessage = null)
    {
        LoginError = null;
        try
        {
            await action();
        }
        catch (CredentialVaultException)
        {
            LoginError = "凭据存储不可用，请重新登录。";
        }
        catch (GatewayException exception)
        {
            LoginError = DescribeGatewayFailure(exception);
        }
        catch (OperationCanceledException)
        {
            // Navigation cancellation is expected and must not leave an error banner.
        }
        catch (InvalidOperationException exception)
        {
            LoginError = exception.Message;
        }
        catch (Exception)
        {
            LoginError = failureMessage ?? "本地缓存操作失败，请稍后重试。";
        }
        finally
        {
            Project(_session.State);
        }
    }

    private void OnStateChanged(object? sender, ClientStateChangedEventArgs eventArgs) =>
        _dispatcher.Dispatch(() => Project(eventArgs.State));

    private void Project(ClientState state)
    {
        IsLoggedIn = state.Connection.Status is
            RelayCove.Core.ConnectionStatus.Connected or
            RelayCove.Core.ConnectionStatus.Offline or
            RelayCove.Core.ConnectionStatus.Reconnecting or
            RelayCove.Core.ConnectionStatus.RateLimited ||
            state.Connection.Status == RelayCove.Core.ConnectionStatus.Faulted && _session.AccountId is not null;
        ConnectionStatus = DescribeConnection(state.Connection);
        Replace(Channels, state.Subscriptions.Values
            .Where(subscription => subscription.IsActive)
            .OrderBy(subscription => subscription.Name, StringComparer.Ordinal)
            .Select(subscription => new ChannelItem(subscription.ChannelId, subscription.Name)));
        Replace(DirectMessages, _session.RecentDirectMessages
            .OfType<DirectMessage>()
            .OrderBy(item => item.CanonicalKey, StringComparer.Ordinal)
            .Select(item => new NavigationItem(item, DescribeDirectMessage(item, state.Users))));

        var selected = _session.SelectedConversation;
        Replace(Messages, state.Messages.Values
            .Where(message => selected is not null && message.Conversation == selected)
            .OrderBy(message => message.Id)
            .Select(message => new MessageItem(
                message.Id.ToString(),
                message.SenderDisplayName ?? state.Users.GetValueOrDefault(message.SenderId)?.FullName ?? $"用户 {message.SenderId}",
                message.Content,
                message.Timestamp.LocalDateTime.ToString("g"))));
        foreach (var entry in state.Outbox.Values
            .Where(entry => selected is not null && entry.Conversation == selected && entry.State != OutboxState.Hidden)
            .OrderBy(entry => entry.CreatedAt))
        {
            Messages.Add(new MessageItem(
                $"local-{entry.LocalId}",
                "你",
                entry.Content,
                entry.CreatedAt.LocalDateTime.ToString("g"),
                DescribeOutbox(entry.State),
                entry.State is OutboxState.WaitExpired or OutboxState.Failed,
                RecoverOutboxCommand));
        }

        OnPropertyChanged(nameof(HasSelectedConversation));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var materialized = items.ToArray();
        if (target.SequenceEqual(materialized)) return;
        target.Clear();
        foreach (var item in materialized) target.Add(item);
    }

    private static string DescribeGatewayFailure(GatewayException exception) => exception.Kind switch
    {
        GatewayErrorKind.IncompatibleRealm => "此 Realm 与 RelayCove 不兼容。",
        GatewayErrorKind.AuthenticationFailed or GatewayErrorKind.ReauthRequired => "邮箱、密码或 API 凭据无效。",
        GatewayErrorKind.RateLimited => "服务器正在限流，请稍后再试。",
        GatewayErrorKind.Offline => "无法连接到服务器，请检查网络和 Realm 地址。",
        _ => "服务器请求失败，请稍后再试。"
    };

    private static string DescribeConnection(ConnectionState state) => state.Status switch
    {
        RelayCove.Core.ConnectionStatus.SignedOut => "已注销",
        RelayCove.Core.ConnectionStatus.Locked => "本地缓存已锁定",
        RelayCove.Core.ConnectionStatus.Offline => "离线缓存",
        RelayCove.Core.ConnectionStatus.Connecting => "正在连接",
        RelayCove.Core.ConnectionStatus.Connected => "已连接",
        RelayCove.Core.ConnectionStatus.Reconnecting => "正在重连",
        RelayCove.Core.ConnectionStatus.RateLimited => "服务器限流中",
        RelayCove.Core.ConnectionStatus.ReauthRequired => "需要重新认证",
        _ => "连接故障"
    };

    private static string DescribeDirectMessage(DirectMessage message, IReadOnlyDictionary<long, UserProfile> users) =>
        message.OtherUserIds.Count == 0
            ? "给自己"
            : string.Join(", ", message.OtherUserIds.Select(id => users.GetValueOrDefault(id)?.FullName ?? $"用户 {id}"));

    private static string DescribeOutbox(OutboxState state) => state switch
    {
        OutboxState.Waiting => "正在等待服务器事件",
        OutboxState.WaitExpired => "结果不确定；手动重试可能产生重复消息",
        OutboxState.Failed => "发送失败；恢复内容后手动重试可能产生重复消息",
        _ => string.Empty
    };

    private void CancelNavigation()
    {
        var cancellation = Interlocked.Exchange(ref _navigationCancellation, null);
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelNavigation();
        _session.StateChanged -= OnStateChanged;
    }
}
