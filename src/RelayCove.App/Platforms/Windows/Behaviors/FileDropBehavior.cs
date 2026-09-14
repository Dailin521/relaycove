using CommunityToolkit.Mvvm.Input;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using RelayCove.App.Services;
using Windows.ApplicationModel.DataTransfer;
using WinDataPackageOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using WinUiDragEventArgs = Microsoft.UI.Xaml.DragEventArgs;
using WinUiDragEventHandler = Microsoft.UI.Xaml.DragEventHandler;
using WinUiElement = Microsoft.UI.Xaml.UIElement;
using WinUiFrameworkElement = Microsoft.UI.Xaml.FrameworkElement;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class FileDropBehavior : Behavior<Border>
{
    private readonly WinUiDragEventHandler _dragEnterHandler;
    private readonly WinUiDragEventHandler _dragOverHandler;
    private readonly WinUiDragEventHandler _dragLeaveHandler;
    private readonly WinUiDragEventHandler _dropHandler;
    private WinUiFrameworkElement? _platformView;
    private Border? _border;
    private NativeFileDropTarget? _nativeDropTarget;
    private nint _windowHandle;

    public FileDropBehavior()
    {
        _dragEnterHandler = OnDragEnter;
        _dragOverHandler = OnDragOver;
        _dragLeaveHandler = OnDragLeave;
        _dropHandler = OnDrop;
    }

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command),
        typeof(IAsyncRelayCommand),
        typeof(FileDropBehavior));

    public static readonly BindableProperty IsDropEnabledProperty = BindableProperty.Create(
        nameof(IsDropEnabled),
        typeof(bool),
        typeof(FileDropBehavior),
        false,
        propertyChanged: static (bindable, _, value) =>
        {
            if (!(bool)value) ((FileDropBehavior)bindable).IsDragActive = false;
        });

    public static readonly BindableProperty IsDragActiveProperty = BindableProperty.Create(
        nameof(IsDragActive),
        typeof(bool),
        typeof(FileDropBehavior),
        false,
        BindingMode.TwoWay);

    public IAsyncRelayCommand? Command
    {
        get => (IAsyncRelayCommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public bool IsDropEnabled
    {
        get => (bool)GetValue(IsDropEnabledProperty);
        set => SetValue(IsDropEnabledProperty, value);
    }

    public bool IsDragActive
    {
        get => (bool)GetValue(IsDragActiveProperty);
        set => SetValue(IsDragActiveProperty, value);
    }

    protected override void OnAttachedTo(Border bindable)
    {
        base.OnAttachedTo(bindable);
        _border = bindable;
        bindable.HandlerChanged += OnHandlerChanged;
        AttachNativeView(bindable.Handler?.PlatformView as WinUiFrameworkElement);
    }

    protected override void OnDetachingFrom(Border bindable)
    {
        bindable.HandlerChanged -= OnHandlerChanged;
        DetachNativeView();
        _border = null;
        base.OnDetachingFrom(bindable);
    }

    private void OnHandlerChanged(object? sender, EventArgs eventArgs) =>
        AttachNativeView((sender as Border)?.Handler?.PlatformView as WinUiFrameworkElement);

    private void AttachNativeView(WinUiFrameworkElement? platformView)
    {
        DetachNativeView();
        if (platformView is null) return;
        _platformView = platformView;
        platformView.Loaded += OnNativeLoaded;
        platformView.Unloaded += OnNativeUnloaded;
        platformView.AllowDrop = true;
        // RichEditBox and attachment controls may already have handled the routed event.
        platformView.AddHandler(WinUiElement.DragEnterEvent, _dragEnterHandler, true);
        platformView.AddHandler(WinUiElement.DragOverEvent, _dragOverHandler, true);
        platformView.AddHandler(WinUiElement.DragLeaveEvent, _dragLeaveHandler, true);
        platformView.AddHandler(WinUiElement.DropEvent, _dropHandler, true);
        if (platformView.IsLoaded) AttachCompatibilityDropTarget();
    }

    private void DetachNativeView()
    {
        if (_platformView is null) return;
        DetachCompatibilityDropTarget();
        _platformView.Loaded -= OnNativeLoaded;
        _platformView.Unloaded -= OnNativeUnloaded;
        _platformView.RemoveHandler(WinUiElement.DragEnterEvent, _dragEnterHandler);
        _platformView.RemoveHandler(WinUiElement.DragOverEvent, _dragOverHandler);
        _platformView.RemoveHandler(WinUiElement.DragLeaveEvent, _dragLeaveHandler);
        _platformView.RemoveHandler(WinUiElement.DropEvent, _dropHandler);
        _platformView.AllowDrop = false;
        _platformView = null;
        IsDragActive = false;
    }

    private void OnNativeLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs eventArgs) =>
        AttachCompatibilityDropTarget();

    private void OnNativeUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs eventArgs) =>
        DetachCompatibilityDropTarget();

    private void DetachCompatibilityDropTarget()
    {
        _nativeDropTarget?.Dispose();
        _nativeDropTarget = null;
        _windowHandle = 0;
    }

    private void AttachCompatibilityDropTarget()
    {
        if (_nativeDropTarget is not null || !WindowsProcessEnvironment.IsElevated() ||
            _border?.Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window) return;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _nativeDropTarget = new NativeFileDropTarget(CanDropAtScreenPoint,
            active => IsDragActive = active, AcceptNativeFiles);
        _nativeDropTarget.Attach(_windowHandle);
    }

    private bool CanDropAtScreenPoint(int x, int y)
    {
        if (!IsDropEnabled || Command?.CanExecute(null) != true ||
            _platformView?.XamlRoot is not { } root) return false;
        var point = new NativeDropPoint { X = x, Y = y };
        if (!ScreenToClient(_windowHandle, ref point)) return false;
        var position = new global::Windows.Foundation.Point(
            point.X / root.RasterizationScale, point.Y / root.RasterizationScale);
        // Use the frontmost visible hit so a settings/modal overlay cannot accept files
        // into the Composer beneath it, even though both share the same native HWND.
        var hit = VisualTreeHelper.FindElementsInHostCoordinates(position, root.Content).FirstOrDefault();
        for (Microsoft.UI.Xaml.DependencyObject? current = hit; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, _platformView)) return true;
        }
        return false;
    }

    private async void AcceptNativeFiles(Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>> readAsync)
    {
        if (!IsDropEnabled || Command is not { } command || !command.CanExecute(readAsync)) return;
        // Execute immediately to capture the current account and draft before any await.
        try { await command.ExecuteAsync(readAsync); }
        catch { IsDragActive = false; }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref NativeDropPoint point);

    private void OnDragEnter(object sender, WinUiDragEventArgs eventArgs) => UpdateDragState(eventArgs);

    private void OnDragOver(object sender, WinUiDragEventArgs eventArgs) => UpdateDragState(eventArgs);

    private void UpdateDragState(WinUiDragEventArgs eventArgs)
    {
        var canDrop = IsDropEnabled && Command?.CanExecute(null) == true &&
            eventArgs.DataView.Contains(StandardDataFormats.StorageItems);
        eventArgs.AcceptedOperation = canDrop ? WinDataPackageOperation.Copy : WinDataPackageOperation.None;
        IsDragActive = canDrop;
        eventArgs.Handled = true;
    }

    private void OnDragLeave(object sender, WinUiDragEventArgs eventArgs)
    {
        IsDragActive = false;
        eventArgs.Handled = true;
    }

    private async void OnDrop(object sender, WinUiDragEventArgs eventArgs)
    {
        IsDragActive = false;
        eventArgs.Handled = true;
        eventArgs.AcceptedOperation = WinDataPackageOperation.None;
        if (!IsDropEnabled || Command is not { } command ||
            !eventArgs.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var dataView = eventArgs.DataView;
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>> readAsync = token =>
            ClipboardFileAttachmentFactory.CreateAsync(dataView, token);
        if (!command.CanExecute(readAsync)) return;
        eventArgs.AcceptedOperation = WinDataPackageOperation.Copy;
        var deferral = eventArgs.GetDeferral();

        try
        {
            await command.ExecuteAsync(readAsync);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
