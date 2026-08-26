using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RelayCove.App.Services;
using Windows.Graphics;
using Windows.UI.Text;
using WinRT.Interop;
using WinUiBorder = Microsoft.UI.Xaml.Controls.Border;
using WinUiColumnDefinition = Microsoft.UI.Xaml.Controls.ColumnDefinition;
using WinUiCornerRadius = Microsoft.UI.Xaml.CornerRadius;
using WinUiFontWeight = Windows.UI.Text.FontWeight;
using WinUiGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinUiGridLength = Microsoft.UI.Xaml.GridLength;
using WinUiGridUnitType = Microsoft.UI.Xaml.GridUnitType;
using WinUiHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinUiImage = Microsoft.UI.Xaml.Controls.Image;
using WinUiRowDefinition = Microsoft.UI.Xaml.Controls.RowDefinition;
using WinUiSolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using WinUiStretch = Microsoft.UI.Xaml.Media.Stretch;
using WinUiThickness = Microsoft.UI.Xaml.Thickness;
using WinUiVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using WinUiVisibility = Microsoft.UI.Xaml.Visibility;

namespace RelayCove.App.Platforms.Windows;

internal sealed class WindowsTrayIconController : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 42;
    private const uint NotifyIconId = 1;
    private const uint NotifyIconVersion4 = 4;
    private const uint NotifyIconAdd = 0;
    private const uint NotifyIconModify = 1;
    private const uint NotifyIconDelete = 2;
    private const uint NotifyIconSetFocus = 3;
    private const uint NotifyIconSetVersion = 4;
    private const uint NotifyIconMessage = 0x00000001;
    private const uint NotifyIconIcon = 0x00000002;
    private const uint NotifyIconTip = 0x00000004;
    private const uint NotifyIconGuid = 0x00000020;
    private const uint NotifySelect = 0x0400;
    private const uint NotifyKeySelect = 0x0401;
    private const uint NotifyPopupOpen = 0x0406;
    private const uint NotifyPopupClose = 0x0407;
    private const uint MouseMove = 0x0200;
    private const uint LeftButtonUp = 0x0202;
    private const uint LeftButtonDoubleClick = 0x0203;
    private const uint WindowContextMenu = 0x007B;
    private const uint MenuString = 0x00000000;
    private const uint TrackPopupRightButton = 0x0002;
    private const uint TrackPopupReturnCommand = 0x0100;
    private const uint ExitMenuCommand = 1;
    private const uint WindowGetIcon = 0x007F;
    private const nint IconSmall = 0;
    private const nint IconBig = 1;
    private const nint IconSmall2 = 2;
    private const int ExtendedWindowStyleIndex = -20;
    private const long ExtendedWindowToolWindow = 0x00000080L;
    private const long ExtendedWindowNoActivate = 0x08000000L;
    private const nint WindowTopMost = -1;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 2;
    private const int PreviewWidthDip = 360;
    private const int PreviewHeightDip = 112;
    private static readonly Guid TrayIconGuid = new("8B6EF624-2B24-4FB2-B647-4B42221686EA");

    private readonly Action<string?> _activateWindow;
    private readonly Action _exitApplication;
    private readonly WindowProcedure _windowProcedure;
    private readonly string _windowClassName = $"RelayCove.Tray.{Environment.ProcessId}";
    private DispatcherQueue? _dispatcherQueue;
    private DispatcherQueueTimer? _blinkTimer;
    private DispatcherQueueTimer? _hoverTimer;
    private Microsoft.UI.Xaml.Window? _previewWindow;
    private AppWindow? _previewAppWindow;
    private TextBlock? _previewInitial;
    private WinUiImage? _previewAvatar;
    private TextBlock? _previewTitle;
    private TextBlock? _previewBody;
    private WinUiBorder? _previewBadge;
    private TextBlock? _previewBadgeText;
    private nint _mainWindowHandle;
    private nint _messageWindowHandle;
    private nint _iconHandle;
    private nint _transparentIconHandle;
    private nint _moduleHandle;
    private uint _taskbarCreatedMessage;
    private int _unreadCount;
    private bool _unreadIsTruncated;
    private bool _iconAdded;
    private bool _iconShowingArtwork;
    private bool _flashRequested;
    private bool _hovering;
    private bool _activationQueued;
    private bool _exitQueued;
    private bool _previewVisibilityQueued;
    private bool _previewVisibilityRequested;
    private bool _disposed;
    private AppMessageNotification? _previewNotification;
    private Uri? _previewAvatarUri;

    internal WindowsTrayIconController(Action<string?> activateWindow, Action exitApplication)
    {
        _activateWindow = activateWindow ?? throw new ArgumentNullException(nameof(activateWindow));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
        _windowProcedure = OnWindowMessage;
    }

    internal void Attach(nint mainWindowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mainWindowHandle == 0 || _mainWindowHandle == mainWindowHandle && _messageWindowHandle != 0) return;

        _mainWindowHandle = mainWindowHandle;
        _dispatcherQueue ??= DispatcherQueue.GetForCurrentThread();
        EnsureMessageWindow();
        EnsureIconHandle();
        DeleteStaleIcon();
        AddIcon();
    }

    internal void UpdateUnread(int count, bool isTruncated)
    {
        var previousCount = _unreadCount;
        _unreadCount = Math.Max(0, count);
        _unreadIsTruncated = isTruncated;
        if (_unreadCount < previousCount)
        {
            _previewNotification = null;
            _previewAvatarUri = null;
        }

        if (!HasUnread)
        {
            _previewNotification = null;
            _previewAvatarUri = null;
            StopFlashing();
            QueuePreviewVisibility(visible: false);
        }
        else if (_flashRequested)
        {
            ResumeFlashing();
        }

        ModifyIcon();
        UpdatePreviewContent();
    }

    internal void UpdatePreview(AppMessageNotification notification, Uri? avatarUri)
    {
        ArgumentNullException.ThrowIfNull(notification);
        _previewNotification = notification;
        _previewAvatarUri = avatarUri is { IsFile: true } ? avatarUri : null;
        UpdatePreviewContent();
    }

    internal void StartFlashing()
    {
        if (_disposed) return;
        _flashRequested = true;
        ResumeFlashing();
    }

    internal void StopFlashing()
    {
        _flashRequested = false;
        _blinkTimer?.Stop();
        EnsureIconVisible();
    }

    internal static string FormatTooltip(int count, bool isTruncated) => count switch
    {
        > 99 => "RelayCove · 99+ 条未读消息",
        > 0 => $"RelayCove · {count} 条未读消息",
        _ when isTruncated => "RelayCove · 有未读消息",
        _ => "RelayCove"
    };

    private bool HasUnread => ShouldShowPreview(_unreadCount, _unreadIsTruncated);

    internal static bool ShouldShowPreview(int count, bool isTruncated) =>
        count > 0 || isTruncated;

    private void ResumeFlashing()
    {
        if (!_flashRequested || !HasUnread || _hovering || _disposed) return;
        EnsureIconVisible();
        if (_blinkTimer is null)
        {
            var dispatcherQueue = _dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
            if (dispatcherQueue is null) return;
            _blinkTimer = dispatcherQueue.CreateTimer();
            _blinkTimer.Interval = TimeSpan.FromMilliseconds(500);
            _blinkTimer.IsRepeating = true;
            _blinkTimer.Tick += OnBlinkTimerTick;
        }
        _blinkTimer.Start();
    }

    private void OnBlinkTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_flashRequested || !HasUnread || _hovering)
        {
            sender.Stop();
            EnsureIconVisible();
            return;
        }

        SetIconArtwork(!_iconShowingArtwork);
    }

    private void EnsureMessageWindow()
    {
        if (_messageWindowHandle != 0) return;
        _moduleHandle = GetModuleHandle(null);
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Instance = _moduleHandle,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            ClassName = _windowClassName
        };
        if (RegisterClassEx(ref windowClass) == 0) return;

        _messageWindowHandle = CreateWindowEx(
            0,
            _windowClassName,
            "RelayCove tray messages",
            0,
            0,
            0,
            0,
            0,
            new nint(-3),
            0,
            _moduleHandle,
            0);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    private void EnsureIconHandle()
    {
        if (_iconHandle != 0) return;
        var currentWindowIcon = SendMessage(_mainWindowHandle, WindowGetIcon, IconSmall2, 0);
        if (currentWindowIcon == 0) currentWindowIcon = SendMessage(_mainWindowHandle, WindowGetIcon, IconSmall, 0);
        if (currentWindowIcon == 0) currentWindowIcon = SendMessage(_mainWindowHandle, WindowGetIcon, IconBig, 0);
        if (currentWindowIcon != 0) _iconHandle = CopyIcon(currentWindowIcon);

        if (_iconHandle == 0 && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            var large = new nint[1];
            var small = new nint[1];
            if (ExtractIconEx(Environment.ProcessPath, 0, large, small, 1) > 0)
            {
                _iconHandle = small[0] != 0 ? small[0] : large[0];
                var unused = _iconHandle == small[0] ? large[0] : small[0];
                if (unused != 0) _ = DestroyIcon(unused);
            }
        }

        if (_iconHandle == 0) return;
        var andMask = Enumerable.Repeat(byte.MaxValue, 32).ToArray();
        var xorMask = new byte[32];
        _transparentIconHandle = CreateIcon(_moduleHandle, 16, 16, 1, 1, andMask, xorMask);
    }

    private void AddIcon()
    {
        if (_disposed || _iconAdded || _messageWindowHandle == 0 || _iconHandle == 0) return;
        var data = CreateIconData(NotifyIconMessage | NotifyIconIcon | NotifyIconTip);
        if (!ShellNotifyIcon(NotifyIconAdd, ref data)) return;

        var version = CreateIconData(0);
        version.Version = NotifyIconVersion4;
        _ = ShellNotifyIcon(NotifyIconSetVersion, ref version);
        _iconAdded = true;
        _iconShowingArtwork = true;
    }

    private void DeleteStaleIcon()
    {
        if (_messageWindowHandle == 0) return;
        var data = CreateIconData(0);
        _ = ShellNotifyIcon(NotifyIconDelete, ref data);
        _iconAdded = false;
        _iconShowingArtwork = false;
    }

    private void ModifyIcon()
    {
        if (!_iconAdded) return;
        var data = CreateIconData(NotifyIconIcon | NotifyIconTip);
        data.IconHandle = _iconShowingArtwork || _transparentIconHandle == 0
            ? _iconHandle
            : _transparentIconHandle;
        _ = ShellNotifyIcon(NotifyIconModify, ref data);
    }

    private void SetIconArtwork(bool visible)
    {
        if (!_iconAdded || _transparentIconHandle == 0) return;
        _iconShowingArtwork = visible;
        ModifyIcon();
    }

    private void RemoveIcon()
    {
        if (!_iconAdded || _messageWindowHandle == 0) return;
        var data = CreateIconData(0);
        _ = ShellNotifyIcon(NotifyIconDelete, ref data);
        _iconAdded = false;
        _iconShowingArtwork = false;
        HidePreview();
    }

    private void EnsureIconVisible()
    {
        if (!_iconAdded) AddIcon();
        else if (!_iconShowingArtwork) SetIconArtwork(true);
    }

    private NotifyIconData CreateIconData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _messageWindowHandle,
        Id = NotifyIconId,
        Flags = flags | NotifyIconGuid,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        Tip = FormatTooltip(_unreadCount, _unreadIsTruncated),
        Info = string.Empty,
        InfoTitle = string.Empty,
        Guid = TrayIconGuid
    };

    private nint OnWindowMessage(nint windowHandle, uint message, nint wordParameter, nint longParameter)
    {
        try
        {
            return ProcessWindowMessage(windowHandle, message, wordParameter, longParameter);
        }
        catch (Exception)
        {
            // No managed exception may escape an unmanaged window callback. Windows terminates
            // the process with STATUS_FATAL_USER_CALLBACK_EXCEPTION when that boundary is crossed.
            return 0;
        }
    }

    private nint ProcessWindowMessage(nint windowHandle, uint message, nint wordParameter, nint longParameter)
    {
        if (message == _taskbarCreatedMessage && message != 0)
        {
            _iconAdded = false;
            _iconShowingArtwork = false;
            AddIcon();
            if (_flashRequested) ResumeFlashing();
            return 0;
        }

        if (message != CallbackMessage) return DefWindowProc(windowHandle, message, wordParameter, longParameter);
        var notification = unchecked((uint)longParameter.ToInt64()) & 0xFFFFu;
        if (IsPreviewOpenCallback(notification))
        {
            if (HasUnread) QueuePreviewVisibility(visible: true);
            return 0;
        }

        switch (notification)
        {
            case NotifyPopupClose:
                QueuePreviewVisibility(visible: false);
                break;
            case NotifySelect:
            case NotifyKeySelect:
            case LeftButtonUp:
            case LeftButtonDoubleClick:
                QueueWindowActivation();
                break;
            case WindowContextMenu:
                ShowContextMenu();
                break;
        }
        return 0;
    }

    internal static bool IsPreviewOpenCallback(uint notification) =>
        notification is NotifyPopupOpen or MouseMove;

    private void QueuePreviewVisibility(bool visible)
    {
        if (_disposed) return;
        _previewVisibilityRequested = visible;
        if (_previewVisibilityQueued) return;

        var dispatcherQueue = _dispatcherQueue;
        if (dispatcherQueue is null) return;
        _previewVisibilityQueued = true;
        if (dispatcherQueue.TryEnqueue(() =>
            {
                _previewVisibilityQueued = false;
                if (_disposed) return;
                var show = _previewVisibilityRequested;
                if (show) StartHoverTracking();
                else _hoverTimer?.Stop();
                if (!ShouldApplyPreviewVisibility(show, _hovering)) return;
                var succeeded = TryInvokeCallback(show ? ShowPreview : HidePreview);
                if (!succeeded && show)
                {
                    _hovering = false;
                    ResumeFlashing();
                }
            }))
        {
            return;
        }

        _previewVisibilityQueued = false;
    }

    internal static bool ShouldApplyPreviewVisibility(bool requestedVisible, bool currentlyVisible) =>
        requestedVisible != currentlyVisible;

    private void StartHoverTracking()
    {
        if (_hoverTimer is null)
        {
            var dispatcherQueue = _dispatcherQueue;
            if (dispatcherQueue is null) return;
            _hoverTimer = dispatcherQueue.CreateTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(150);
            _hoverTimer.IsRepeating = true;
            _hoverTimer.Tick += OnHoverTimerTick;
        }
        _hoverTimer.Start();
    }

    private void OnHoverTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || !IsCursorOverIcon())
        {
            sender.Stop();
            QueuePreviewVisibility(visible: false);
        }
    }

    private bool IsCursorOverIcon()
    {
        try
        {
            var identifier = CreateIconIdentifier();
            if (ShellNotifyIconGetRect(ref identifier, out var rectangle) < 0 ||
                !GetCursorPos(out var cursor))
            {
                return false;
            }

            return cursor.X >= rectangle.Left && cursor.X < rectangle.Right &&
                   cursor.Y >= rectangle.Top && cursor.Y < rectangle.Bottom;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void QueueWindowActivation()
    {
        if (_disposed || _activationQueued) return;
        var dispatcherQueue = _dispatcherQueue;
        if (dispatcherQueue is null) return;
        var conversationKey = ResolveActivationConversation(_previewNotification, HasUnread);
        _activationQueued = true;
        if (dispatcherQueue.TryEnqueue(() =>
            {
                _activationQueued = false;
                if (_disposed) return;
                _ = TryInvokeCallback(() =>
                {
                    HidePreview();
                    _activateWindow(conversationKey);
                });
            }))
        {
            return;
        }

        _activationQueued = false;
    }

    internal static string? ResolveActivationConversation(
        AppMessageNotification? previewNotification,
        bool hasUnread) =>
        hasUnread ? previewNotification?.ConversationKey : null;

    internal static bool IsExitMenuCommand(uint command) => command == ExitMenuCommand;

    internal static bool TryInvokeCallback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        try
        {
            callback();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ReturnFocusToTray()
    {
        if (!_iconAdded) return;
        var data = CreateIconData(0);
        _ = ShellNotifyIcon(NotifyIconSetFocus, ref data);
    }

    private void ShowContextMenu()
    {
        if (_disposed || _messageWindowHandle == 0) return;
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            if (!AppendMenu(menu, MenuString, ExitMenuCommand, "退出 RelayCove") ||
                !GetCursorPos(out var cursor))
            {
                return;
            }

            _ = SetForegroundWindow(_messageWindowHandle);
            var command = TrackPopupMenuEx(
                menu,
                TrackPopupRightButton | TrackPopupReturnCommand,
                cursor.X,
                cursor.Y,
                _messageWindowHandle,
                0);
            if (IsExitMenuCommand(command)) QueueExitApplication();
        }
        finally
        {
            _ = DestroyMenu(menu);
            ReturnFocusToTray();
        }
    }

    private void QueueExitApplication()
    {
        if (_disposed || _exitQueued || _dispatcherQueue is null) return;
        _exitQueued = true;
        if (_dispatcherQueue.TryEnqueue(() =>
            {
                _exitQueued = false;
                if (_disposed) return;
                _ = TryInvokeCallback(() =>
                {
                    HidePreview();
                    _exitApplication();
                });
            }))
        {
            return;
        }

        _exitQueued = false;
    }

    private void ShowPreview()
    {
        if (_disposed || !HasUnread) return;
        _blinkTimer?.Stop();
        EnsureIconVisible();
        EnsurePreviewWindow();
        UpdatePreviewContent();
        PositionPreviewWindow();
        if (_previewWindow is null || _previewAppWindow is null) return;
        var handle = WindowNative.GetWindowHandle(_previewWindow);
        _previewAppWindow.Show(activateWindow: false);
        _ = SetWindowPos(
            handle,
            WindowTopMost,
            0,
            0,
            0,
            0,
            SetWindowPositionNoActivate | SetWindowPositionShowWindow | 0x0001 | 0x0002);
        _hovering = true;
    }

    private void HidePreview()
    {
        var wasHovering = _hovering;
        _hovering = false;
        if (_previewAppWindow is not null)
        {
            _previewAppWindow.Hide();
        }
        if (wasHovering) ResumeFlashing();
    }

    private void EnsurePreviewWindow()
    {
        if (_previewWindow is not null) return;

        var background = new WinUiSolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 248, 248, 248));
        var borderBrush = new WinUiSolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 215, 215, 215));
        var muted = new WinUiSolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 103, 108, 116));
        var root = new WinUiBorder
        {
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new WinUiThickness(1),
            CornerRadius = new WinUiCornerRadius(12),
            Padding = new WinUiThickness(14)
        };
        var content = new WinUiGrid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new WinUiColumnDefinition { Width = new WinUiGridLength(56) });
        content.ColumnDefinitions.Add(new WinUiColumnDefinition { Width = new WinUiGridLength(1, WinUiGridUnitType.Star) });

        var avatarContainer = new WinUiBorder
        {
            Width = 56,
            Height = 56,
            CornerRadius = new WinUiCornerRadius(10),
            Background = new WinUiSolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 47, 155, 255))
        };
        var avatarLayer = new WinUiGrid();
        _previewInitial = new TextBlock
        {
            Text = "R",
            Foreground = new WinUiSolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 22,
            FontWeight = new WinUiFontWeight { Weight = 600 },
            HorizontalAlignment = WinUiHorizontalAlignment.Center,
            VerticalAlignment = WinUiVerticalAlignment.Center
        };
        _previewAvatar = new WinUiImage { Stretch = WinUiStretch.UniformToFill };
        avatarLayer.Children.Add(_previewInitial);
        avatarLayer.Children.Add(_previewAvatar);
        avatarContainer.Child = avatarLayer;

        var textColumn = new WinUiGrid { RowSpacing = 4 };
        textColumn.RowDefinitions.Add(new WinUiRowDefinition { Height = WinUiGridLength.Auto });
        textColumn.RowDefinitions.Add(new WinUiRowDefinition { Height = WinUiGridLength.Auto });
        var titleRow = new WinUiGrid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new WinUiColumnDefinition { Width = new WinUiGridLength(1, WinUiGridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new WinUiColumnDefinition { Width = WinUiGridLength.Auto });
        _previewTitle = new TextBlock
        {
            FontSize = 17,
            FontWeight = new WinUiFontWeight { Weight = 600 },
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = WinUiVerticalAlignment.Center
        };
        _previewBadgeText = new TextBlock
        {
            Foreground = new WinUiSolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 12,
            HorizontalAlignment = WinUiHorizontalAlignment.Center,
            VerticalAlignment = WinUiVerticalAlignment.Center
        };
        _previewBadge = new WinUiBorder
        {
            MinWidth = 22,
            Height = 22,
            Padding = new WinUiThickness(6, 0, 6, 0),
            CornerRadius = new WinUiCornerRadius(11),
            Background = new WinUiSolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 250, 81, 81)),
            Child = _previewBadgeText
        };
        WinUiGrid.SetColumn(_previewBadge, 1);
        titleRow.Children.Add(_previewTitle);
        titleRow.Children.Add(_previewBadge);

        _previewBody = new TextBlock
        {
            Foreground = muted,
            FontSize = 13,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        WinUiGrid.SetRow(_previewBody, 1);
        textColumn.Children.Add(titleRow);
        textColumn.Children.Add(_previewBody);

        WinUiGrid.SetColumn(textColumn, 1);
        content.Children.Add(avatarContainer);
        content.Children.Add(textColumn);
        root.Child = content;

        _previewWindow = new Microsoft.UI.Xaml.Window { Content = root };
        var handle = WindowNative.GetWindowHandle(_previewWindow);
        var style = GetWindowLongPtr(handle, ExtendedWindowStyleIndex).ToInt64();
        _ = SetWindowLongPtr(
            handle,
            ExtendedWindowStyleIndex,
            new nint(style | ExtendedWindowToolWindow | ExtendedWindowNoActivate));
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        _previewAppWindow = AppWindow.GetFromWindowId(windowId);
        _previewAppWindow.IsShownInSwitchers = false;
        if (_previewAppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
    }

    private void UpdatePreviewContent()
    {
        if (_previewTitle is null || _previewBody is null || _previewBadge is null ||
            _previewBadgeText is null || _previewInitial is null || _previewAvatar is null)
        {
            return;
        }

        var title = HasUnread ? _previewNotification?.Title ?? "RelayCove" : "RelayCove";
        _previewTitle.Text = title;
        _previewBody.Text = HasUnread
            ? _previewNotification?.Body ?? "有未读消息"
            : "暂无未读消息";
        _previewBadge.Visibility = HasUnread ? WinUiVisibility.Visible : WinUiVisibility.Collapsed;
        _previewBadgeText.Text = _unreadCount switch
        {
            > 99 => "99+",
            > 0 => _unreadCount.ToString(),
            _ => "•"
        };
        _previewInitial.Text = GetInitial(title);
        if (_previewAvatarUri is { IsFile: true } avatarUri)
        {
            _previewAvatar.Source = new BitmapImage(avatarUri);
            _previewAvatar.Visibility = WinUiVisibility.Visible;
        }
        else
        {
            _previewAvatar.Source = null;
            _previewAvatar.Visibility = WinUiVisibility.Collapsed;
        }
    }

    private static string GetInitial(string title)
    {
        var value = title.Trim();
        if (value.Length == 0) return "R";
        return value[..1].ToUpperInvariant();
    }

    private void PositionPreviewWindow()
    {
        if (_previewAppWindow is null) return;
        var identifier = CreateIconIdentifier();
        NativeRectangle iconRectangle;
        if (ShellNotifyIconGetRect(ref identifier, out iconRectangle) < 0)
        {
            _ = GetCursorPos(out var cursor);
            iconRectangle = new NativeRectangle
            {
                Left = cursor.X,
                Top = cursor.Y,
                Right = cursor.X + 1,
                Bottom = cursor.Y + 1
            };
        }

        var dpi = GetDpiForWindow(_mainWindowHandle);
        if (dpi == 0) dpi = 96;
        var width = ScaleDipToPixels(PreviewWidthDip, dpi);
        var height = ScaleDipToPixels(PreviewHeightDip, dpi);
        var monitor = MonitorFromRect(ref iconRectangle, MonitorDefaultToNearest);
        var monitorInfo = new NativeMonitorInfo { Size = (uint)Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var work = monitorInfo.WorkArea;
        var x = (iconRectangle.Left + iconRectangle.Right - width) / 2;
        x = Math.Clamp(x, work.Left + 6, Math.Max(work.Left + 6, work.Right - width - 6));
        var y = iconRectangle.Top - height - 8;
        if (y < work.Top + 6) y = iconRectangle.Bottom + 8;
        y = Math.Clamp(y, work.Top + 6, Math.Max(work.Top + 6, work.Bottom - height - 6));
        _previewAppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private NotifyIconIdentifier CreateIconIdentifier() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
        WindowHandle = _messageWindowHandle,
        Id = NotifyIconId,
        Guid = TrayIconGuid
    };

    internal static int ScaleDipToPixels(int dip, uint dpi) =>
        checked((int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _blinkTimer?.Stop();
        if (_blinkTimer is not null) _blinkTimer.Tick -= OnBlinkTimerTick;
        _blinkTimer = null;
        _hoverTimer?.Stop();
        if (_hoverTimer is not null) _hoverTimer.Tick -= OnHoverTimerTick;
        _hoverTimer = null;
        HidePreview();
        _previewWindow?.Close();
        _previewWindow = null;
        _previewAppWindow = null;
        RemoveIcon();
        if (_iconHandle != 0) _ = DestroyIcon(_iconHandle);
        _iconHandle = 0;
        if (_transparentIconHandle != 0) _ = DestroyIcon(_transparentIconHandle);
        _transparentIconHandle = 0;
        if (_messageWindowHandle != 0) _ = DestroyWindow(_messageWindowHandle);
        _messageWindowHandle = 0;
        if (_moduleHandle != 0) _ = UnregisterClass(_windowClassName, _moduleHandle);
        _moduleHandle = 0;
        GC.KeepAlive(_windowProcedure);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRectangle MonitorArea;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint windowHandle, uint message, nint wordParameter, nint longParameter);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect", ExactSpelling = true)]
    private static extern int ShellNotifyIconGetRect(
        ref NotifyIconIdentifier identifier,
        out NativeRectangle iconLocation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        nint[] largeIcons,
        nint[] smallIcons,
        uint iconCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint windowHandle, uint message, nint wordParameter, nint longParameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    [DllImport("user32.dll")]
    private static extern nint CopyIcon(nint iconHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint windowHandle, uint message, nint wordParameter, nint longParameter);

    [DllImport("user32.dll")]
    private static extern nint CreateIcon(
        nint instance,
        int width,
        int height,
        byte planes,
        byte bitsPerPixel,
        byte[] andBits,
        byte[] xorBits);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRectangle rectangle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint item, string text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        nint menu,
        uint flags,
        int x,
        int y,
        nint windowHandle,
        nint parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}
