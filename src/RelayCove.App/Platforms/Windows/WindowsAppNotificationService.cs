using System.Runtime.InteropServices;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.BadgeNotifications;
using RelayCove.App.Services;
using RelayCove.Core;
using WinRT.Interop;

namespace RelayCove.App.Platforms.Windows;

public sealed class WindowsAppNotificationService : IAppNotificationService
{
    private const uint FlashWindowStop = 0x00000000;
    private const uint FlashWindowTray = 0x00000002;
    private const uint FlashWindowTimerNoForeground = 0x0000000C;
    private const int ShowWindowRestore = 9;
    private const string DesktopToastGroup = "relaycove-live";
    private const string DesktopToastAppId = "com.relaycove.client.desktop";
    private readonly TaskbarUnreadOverlay _taskbarUnreadOverlay = new();
    private readonly WindowsNotificationSoundPlayer _notificationSound = new();
    private readonly INotificationAvatarFileStore _notificationAvatarFileStore;
    private readonly IUiDispatcher _dispatcher;
    private readonly IWindowShellAdapter _windowShellAdapter;
    private readonly IClientSession? _session;
    private AccountId? _notificationAccountId;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private WindowsTrayIconController? _trayIconController;
    private Window? _window;
    private AppNotificationManager? _manager;
    private ToastNotifierCompat? _desktopNotifier;
    private bool _desktopActivationSubscribed;
    private AccountId? _desktopNotificationAccountId;
    private string _desktopActivationToken = Guid.NewGuid().ToString("N");
    private nint _windowHandle;
    private int _pendingUnreadCount;
    private bool _pendingUnreadIsTruncated;
    private int _pendingTrayUnreadCount;
    private bool _pendingTrayUnreadIsTruncated;
    private long _trayPreviewGeneration;
    private AppMessageNotification? _trayPreviewNotification;
    private bool _registered;
    private bool _disposed;
    private string _systemNotificationStatus = "等待窗口初始化。";
    private string _taskbarBadgeStatus = "等待窗口初始化。";

    public event EventHandler? StateChanged;
    public event EventHandler<RelayCove.App.Services.AppNotificationActivatedEventArgs>? NotificationActivated;

    public bool IsSystemNotificationSupported =>
        _registered && (_desktopNotifier is not null
            ? _desktopNotifier.Setting == global::Windows.UI.Notifications.NotificationSetting.Enabled
            : _manager?.Setting == AppNotificationSetting.Enabled);

    public string SystemNotificationStatus => _systemNotificationStatus;
    public string TaskbarBadgeStatus => _taskbarBadgeStatus;

    internal static void PrepareProcessNotificationIdentity()
    {
        // Windows requires an explicit identity before creating windows. Registration
        // below checks the result again and reports failure without preventing startup.
        if (WindowsProcessEnvironment.IsElevated()) _ = SetCurrentProcessExplicitAppUserModelID(DesktopToastAppId);
    }

    public WindowsAppNotificationService(
        INotificationAvatarFileStore notificationAvatarFileStore,
        IUiDispatcher dispatcher,
        IWindowShellAdapter windowShellAdapter,
        IClientSession? session = null)
    {
        _notificationAvatarFileStore = notificationAvatarFileStore ??
                                       throw new ArgumentNullException(nameof(notificationAvatarFileStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _windowShellAdapter = windowShellAdapter ?? throw new ArgumentNullException(nameof(windowShellAdapter));
        _notificationAvatarFileStore.AvatarChanged += OnAvatarChanged;
        _session = session;
        _notificationAccountId = session?.AccountId;
        if (_session is not null) _session.StateChanged += OnSessionStateChanged;
    }

    private void OnSessionStateChanged(object? sender, ClientStateChangedEventArgs args) => _dispatcher.Dispatch(() =>
    {
        RefreshDesktopNotificationAccount();
        if (_disposed || _notificationAccountId == _session?.AccountId) return;
        _notificationAccountId = _session?.AccountId;
        _trayPreviewNotification = null;
        Interlocked.Increment(ref _trayPreviewGeneration);
        UpdateTrayUnread(0, false);
    });

    private void OnAvatarChanged(object? sender, AvatarChangedEventArgs args) => _dispatcher.Dispatch(() =>
    {
        if (_disposed || args.AccountId != _notificationAccountId || args.AccountId != _session?.AccountId ||
            _trayPreviewNotification is not { } notification ||
            notification.SenderAvatarUrl != args.SourceUrl) return;
        _ = UpdateTrayPreviewAvatarAsync(notification, Interlocked.Read(ref _trayPreviewGeneration));
    });

    public void Attach(Window window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        if (ReferenceEquals(_window, window)) return;

        DetachWindow();
        _window = window;
        _window.HandlerChanged += OnHandlerChanged;
        _window.Destroying += OnWindowDestroying;
        TryInitializeNativeWindow();
    }

    public void ShowMessageNotification(AppMessageNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (_disposed || !IsSystemNotificationSupported) return;

        _ = ShowMessageNotificationAsync(notification);
    }

    public void UpdateTrayPreview(AppMessageNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (_disposed) return;

        var generation = Interlocked.Increment(ref _trayPreviewGeneration);
        _notificationAccountId = _session?.AccountId;
        _trayPreviewNotification = notification;
        _trayIconController?.UpdatePreview(notification, null);
        _ = UpdateTrayPreviewAvatarAsync(notification, generation);
    }

    private async Task UpdateTrayPreviewAvatarAsync(
        AppMessageNotification notification,
        long generation)
    {
        var accountId = _session?.AccountId;
        Uri? avatarUri = null;
        if (!string.IsNullOrWhiteSpace(notification.SenderAvatarUrl))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                avatarUri = await _notificationAvatarFileStore.GetAvatarUriAsync(
                    notification.SenderAvatarUrl,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                avatarUri = null;
            }
        }

        _dispatcher.Dispatch(() =>
        {
            if (_disposed || accountId != _session?.AccountId || generation != Interlocked.Read(ref _trayPreviewGeneration)) return;
            _trayIconController?.UpdatePreview(notification, avatarUri);
        });
    }

    private async Task ShowMessageNotificationAsync(AppMessageNotification notification)
    {
        var accountId = _session?.AccountId;
        Uri? avatarUri = null;
        if (!string.IsNullOrWhiteSpace(notification.SenderAvatarUrl))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                avatarUri = await _notificationAvatarFileStore.GetAvatarUriAsync(
                    notification.SenderAvatarUrl,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                avatarUri = null;
            }
        }

        _dispatcher.Dispatch(() =>
        {
            if (accountId == _session?.AccountId) TryShowNotification(notification, avatarUri);
        });
    }

    private void TryShowNotification(AppMessageNotification notification, Uri? avatarUri)
    {
        if (_disposed || !IsSystemNotificationSupported) return;
        try
        {
            if (_desktopNotifier is not null)
            {
                RefreshDesktopNotificationAccount();
                var xml = new global::Windows.Data.Xml.Dom.XmlDocument();
                xml.LoadXml(WindowsDesktopToastPayload.Build(notification, avatarUri, _desktopActivationToken));
                var toast = new global::Windows.UI.Notifications.ToastNotification(xml)
                {
                    Group = DesktopToastGroup,
                    Tag = Guid.NewGuid().ToString("N")[..16]
                };
                toast.Failed += OnDesktopToastFailed;
                _desktopNotifier.Show(toast);
            }
            else
            {
                _manager!.Show(BuildNotification(notification, avatarUri));
            }
            _ = _notificationSound.TryPlay();
        }
        catch (Exception)
        {
            SetStatus("系统通知发送失败；任务栏提醒仍可继续使用。");
        }
    }

    internal static AppNotification BuildNotification(AppMessageNotification notification, Uri? avatarUri)
    {
        var builder = new AppNotificationBuilder()
            .AddText(notification.Title)
            .AddText(notification.Body)
            .MuteAudio()
            .AddArgument("conversation", notification.ConversationKey);
        if (CanUseAvatarUri(avatarUri))
        {
            builder.SetAppLogoOverride(avatarUri!, AppNotificationImageCrop.Circle, notification.Title);
        }
        return builder.BuildNotification();
    }

    internal static bool CanUseAvatarUri(Uri? avatarUri) => avatarUri is { IsFile: true };

    public void UpdateTrayUnread(int count, bool isTruncated)
    {
        var previousCount = _pendingTrayUnreadCount;
        _pendingTrayUnreadCount = Math.Max(0, count);
        _pendingTrayUnreadIsTruncated = isTruncated;
        if (_pendingTrayUnreadCount < previousCount ||
            _pendingTrayUnreadCount == 0 && !_pendingTrayUnreadIsTruncated)
        {
            Interlocked.Increment(ref _trayPreviewGeneration);
            _trayPreviewNotification = null;
        }
        _trayIconController?.UpdateUnread(_pendingTrayUnreadCount, _pendingTrayUnreadIsTruncated);
    }

    public void UpdateUnreadBadge(int count, bool isTruncated)
    {
        _pendingUnreadCount = Math.Max(0, count);
        _pendingUnreadIsTruncated = isTruncated;
        if (_disposed) return;
        _trayIconController?.UpdateBadgeVisibility(_pendingUnreadCount > 0 || _pendingUnreadIsTruncated);
        if (_windowHandle == 0) return;

        var mode = ResolveBadgeMode(_pendingUnreadCount, _pendingUnreadIsTruncated);
        var systemBadgeUpdated = false;
        try
        {
            switch (mode)
            {
                case TaskbarBadgeMode.Unknown:
                    BadgeNotificationManager.Current.SetBadgeAsGlyph(BadgeNotificationGlyph.NewMessage);
                    break;
                case TaskbarBadgeMode.Count:
                    BadgeNotificationManager.Current.SetBadgeAsCount((uint)_pendingUnreadCount);
                    break;
                default:
                    BadgeNotificationManager.Current.ClearBadge();
                    break;
            }
            systemBadgeUpdated = true;
        }
        catch (Exception)
        {
            // The WinAppSDK badge can be unavailable or target a different
            // unpackaged identity. The HWND overlay below remains authoritative
            // for the visible running-window taskbar button.
        }

        var windowOverlayUpdated = mode == TaskbarBadgeMode.Clear
            ? _taskbarUnreadOverlay.TryClear(_windowHandle)
            : _taskbarUnreadOverlay.TryApply(
                _windowHandle,
                _pendingUnreadCount,
                _pendingUnreadIsTruncated);
        if (systemBadgeUpdated || windowOverlayUpdated)
        {
            SetTaskbarBadgeStatus("任务栏未读数量已接入。");
            return;
        }

        try
        {
            BadgeNotificationManager.Current.ClearBadge();
        }
        catch (Exception)
        {
            // A stale platform badge may remain when Windows rejects both operations.
        }
        _ = _taskbarUnreadOverlay.TryClear(_windowHandle);
        SetTaskbarBadgeStatus("任务栏徽标更新失败；请检查 Windows 任务栏通知设置。");
    }

    internal static TaskbarBadgeMode ResolveBadgeMode(int count, bool isTruncated) =>
        count > 0
            ? TaskbarBadgeMode.Count
            : isTruncated ? TaskbarBadgeMode.Unknown : TaskbarBadgeMode.Clear;

    public void FlashTaskbar()
    {
        if (_disposed) return;
        _trayIconController?.StartFlashing();
        if (_windowHandle == 0) return;
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            WindowHandle = _windowHandle,
            Flags = FlashWindowTray | FlashWindowTimerNoForeground,
            Count = uint.MaxValue,
            Timeout = 0
        };
        _ = FlashWindowEx(ref info);
    }

    public void StopTaskbarFlash()
    {
        if (_windowHandle == 0) return;
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            WindowHandle = _windowHandle,
            Flags = FlashWindowStop,
            Count = 0,
            Timeout = 0
        };
        _ = FlashWindowEx(ref info);
    }

    public void StopTrayFlash() => _trayIconController?.StopFlashing();

    private void OnHandlerChanged(object? sender, EventArgs eventArgs) => TryInitializeNativeWindow();

    private void TryInitializeNativeWindow()
    {
        if (_disposed || _window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow) return;
        _windowHandle = WindowNative.GetWindowHandle(nativeWindow);
        _trayIconController ??= new WindowsTrayIconController(
            ActivateTrayIcon,
            _windowShellAdapter.RequestExit);
        _trayIconController.Attach(_windowHandle);
        _trayIconController.UpdateUnread(_pendingTrayUnreadCount, _pendingTrayUnreadIsTruncated);
        UpdateUnreadBadge(_pendingUnreadCount, _pendingUnreadIsTruncated);
#if DEBUG
        if (NativeShellPreviewSession.IsRequested)
        {
            SetStatus("离线预览不会发送系统通知。", force: true);
            return;
        }
#endif
        RegisterSystemNotifications();
    }

    private void RegisterSystemNotifications()
    {
        if (_registered || _disposed) return;
        // WinAppSDK refuses every elevated process. The classic desktop Toast API
        // supports our unpackaged app and still displays Windows banners/Action Center.
        if (WindowsProcessEnvironment.IsElevated())
        {
            RegisterDesktopSystemNotifications();
            return;
        }
        AppNotificationManager? manager = null;
        try
        {
            manager = AppNotificationManager.Default;
            if (!AppNotificationManager.IsSupported())
            {
                SetStatus("当前 Windows 环境不支持系统通知。", force: true);
                return;
            }

            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _manager = manager;
            _registered = true;
            SetStatus(DescribeSetting(manager.Setting), force: true);
        }
        catch (Exception)
        {
            if (manager is not null) manager.NotificationInvoked -= OnNotificationInvoked;
            _manager = null;
            _registered = false;
            SetStatus("系统通知注册失败；请确认应用未以管理员身份运行。", force: true);
        }
    }

    private void RegisterDesktopSystemNotifications()
    {
        try
        {
            // Give the desktop notifier and the existing sound policy the same identity.
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(DesktopToastAppId));
            ToastNotificationManagerCompat.OnActivated += OnDesktopToastActivated;
            _desktopActivationSubscribed = true;
            _desktopNotifier = ToastNotificationManagerCompat.CreateToastNotifier();
            _registered = true;
            ClearDesktopNotifications();
            SetStatus(DescribeDesktopSetting(_desktopNotifier.Setting), force: true);
        }
        catch
        {
            DetachDesktopNotifications();
            _registered = false;
            SetStatus("系统通知注册失败；请检查 Windows 通知设置。", force: true);
        }
    }

    private void RefreshDesktopNotificationAccount()
    {
        if (_desktopNotificationAccountId == _session?.AccountId) return;
        ClearDesktopNotifications();
        _desktopNotificationAccountId = _session?.AccountId;
        _desktopActivationToken = Guid.NewGuid().ToString("N");
    }

    private void OnDesktopToastActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        _dispatcher.Dispatch(() =>
        {
            if (_disposed || _desktopNotificationAccountId != _session?.AccountId) return;
            var conversation = WindowsDesktopToastPayload.GetConversation(args.Argument, _desktopActivationToken);
            if (conversation is not null) ActivateTrayIcon(conversation);
        });
    }

    private void OnDesktopToastFailed(global::Windows.UI.Notifications.ToastNotification sender,
        global::Windows.UI.Notifications.ToastFailedEventArgs args) => _dispatcher.Dispatch(() =>
    {
        if (!_disposed) SetStatus("Windows 未能显示系统通知；请检查系统通知设置。");
    });

    internal static string DescribeDesktopSetting(global::Windows.UI.Notifications.NotificationSetting setting) => setting switch
    {
        global::Windows.UI.Notifications.NotificationSetting.Enabled => "系统通知已接入；实际横幅仍受 Windows 通知设置控制。",
        global::Windows.UI.Notifications.NotificationSetting.DisabledForApplication => "Windows 已关闭 RichChat 的系统通知。",
        global::Windows.UI.Notifications.NotificationSetting.DisabledForUser => "Windows 已关闭当前用户的系统通知。",
        global::Windows.UI.Notifications.NotificationSetting.DisabledByGroupPolicy => "系统通知已被 Windows 组策略关闭。",
        global::Windows.UI.Notifications.NotificationSetting.DisabledByManifest => "当前应用清单未启用系统通知。",
        _ => "当前 Windows 环境不支持系统通知。"
    };

    private void DetachDesktopNotifications()
    {
        ClearDesktopNotifications();
        if (_desktopActivationSubscribed) ToastNotificationManagerCompat.OnActivated -= OnDesktopToastActivated;
        _desktopActivationSubscribed = false;
        _desktopNotifier = null;
    }

    private void ClearDesktopNotifications()
    {
        if (_desktopNotifier is null) return;
        try { ToastNotificationManagerCompat.History.RemoveGroup(DesktopToastGroup); }
        catch { /* Cleanup must not interrupt account changes or application shutdown. */ }
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs eventArgs)
    {
        if (_disposed || !eventArgs.Arguments.TryGetValue("conversation", out var conversationKey) ||
            string.IsNullOrWhiteSpace(conversationKey)) return;

        ActivateWindow();
        NotificationActivated?.Invoke(this, new RelayCove.App.Services.AppNotificationActivatedEventArgs(conversationKey));
    }

    private void ActivateWindow()
    {
        if (_windowHandle == 0) return;
        _ = ShowWindow(_windowHandle, ShowWindowRestore);
        _ = SetForegroundWindow(_windowHandle);
        StopTaskbarFlash();
        StopTrayFlash();
    }

    private void ActivateTrayIcon(string? conversationKey)
    {
        ActivateWindow();
        if (string.IsNullOrWhiteSpace(conversationKey)) return;
        NotificationActivated?.Invoke(
            this,
            new RelayCove.App.Services.AppNotificationActivatedEventArgs(conversationKey));
    }

    private static string DescribeSetting(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.Enabled => "系统通知已接入；实际横幅仍受 Windows 通知设置控制。",
        AppNotificationSetting.DisabledForApplication => "Windows 已关闭 RichChat 的系统通知。",
        AppNotificationSetting.DisabledForUser => "Windows 已关闭当前用户的系统通知。",
        AppNotificationSetting.DisabledByGroupPolicy => "系统通知已被 Windows 组策略关闭。",
        AppNotificationSetting.DisabledByManifest => "当前应用清单未启用系统通知。",
        _ => "当前 Windows 环境不支持系统通知。"
    };

    private void SetStatus(string status, bool force = false)
    {
        if (!force && string.Equals(_systemNotificationStatus, status, StringComparison.Ordinal)) return;
        _systemNotificationStatus = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetTaskbarBadgeStatus(string status)
    {
        if (string.Equals(_taskbarBadgeStatus, status, StringComparison.Ordinal)) return;
        _taskbarBadgeStatus = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowDestroying(object? sender, EventArgs eventArgs) => DetachWindow();

    private void DetachWindow()
    {
        StopTaskbarFlash();
        _trayIconController?.Dispose();
        _trayIconController = null;
        if (_window is not null)
        {
            _window.HandlerChanged -= OnHandlerChanged;
            _window.Destroying -= OnWindowDestroying;
        }
        _window = null;
        _windowHandle = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notificationAvatarFileStore.AvatarChanged -= OnAvatarChanged;
        if (_session is not null) _session.StateChanged -= OnSessionStateChanged;
        _trayPreviewNotification = null;
        _lifetimeCancellation.Cancel();
        DetachWindow();
        DetachDesktopNotifications();
        if (_manager is not null)
        {
            try
            {
                _manager.NotificationInvoked -= OnNotificationInvoked;
                if (_registered) _manager.Unregister();
            }
            catch (Exception)
            {
            }
        }
        _manager = null;
        _registered = false;
        _notificationSound.Dispose();
        _taskbarUnreadOverlay.Dispose();
        _lifetimeCancellation.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo flashWindowInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int commandShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

internal enum TaskbarBadgeMode
{
    Clear,
    Count,
    Unknown
}
