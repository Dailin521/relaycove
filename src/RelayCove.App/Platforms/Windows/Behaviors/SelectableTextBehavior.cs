using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUiFlyoutBase = Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase;
using WinUiTextBlock = Microsoft.UI.Xaml.Controls.TextBlock;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class SelectableTextBehavior : Behavior<Label>
{
    private WinUiTextBlock? _platformView;
    private WinUiFlyoutBase? _originalContextFlyout;
    private TextLineBounds _originalTextLineBounds;

    protected override void OnAttachedTo(Label bindable)
    {
        base.OnAttachedTo(bindable);
        bindable.HandlerChanged += OnHandlerChanged;
        bindable.BindingContextChanged += OnBindingContextChanged;
        AttachNativeView(bindable.Handler?.PlatformView as WinUiTextBlock);
    }

    protected override void OnDetachingFrom(Label bindable)
    {
        bindable.HandlerChanged -= OnHandlerChanged;
        bindable.BindingContextChanged -= OnBindingContextChanged;
        DetachNativeView();
        base.OnDetachingFrom(bindable);
    }

    private void OnHandlerChanged(object? sender, EventArgs eventArgs) =>
        AttachNativeView((sender as Label)?.Handler?.PlatformView as WinUiTextBlock);

    private void OnBindingContextChanged(object? sender, EventArgs eventArgs) => ClearSelection();

    private void AttachNativeView(WinUiTextBlock? platformView)
    {
        DetachNativeView();
        if (platformView is null) return;

        _platformView = platformView;
        _originalContextFlyout = platformView.ContextFlyout;
        _originalTextLineBounds = platformView.TextLineBounds;
        // Remove the font's extra top leading; the bubble supplies equal padding.
        platformView.TextLineBounds = TextLineBounds.TrimToCapHeight;
        platformView.ContextFlyout = null;
        platformView.ContextMenuOpening += OnContextMenuOpening;
        platformView.IsTextSelectionEnabled = true;
        ClearSelection();
    }

    private void DetachNativeView()
    {
        if (_platformView is null) return;
        _platformView.ContextMenuOpening -= OnContextMenuOpening;
        _platformView.ContextFlyout = _originalContextFlyout;
        _platformView.TextLineBounds = _originalTextLineBounds;
        _originalContextFlyout = null;
        ClearSelection();
        _platformView.IsTextSelectionEnabled = false;
        _platformView = null;
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs eventArgs)
    {
        // The message bubble supplies the context menu; suppress native text commands.
        eventArgs.Handled = true;
    }

    private void ClearSelection()
    {
        if (_platformView?.ContentStart is not { } start) return;
        _platformView.Select(start, start);
    }
}
