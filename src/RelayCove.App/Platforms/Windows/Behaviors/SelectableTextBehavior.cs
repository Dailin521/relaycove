using WinUiTextBlock = Microsoft.UI.Xaml.Controls.TextBlock;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class SelectableTextBehavior : Behavior<Label>
{
    private WinUiTextBlock? _platformView;

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
        platformView.IsTextSelectionEnabled = true;
        ClearSelection();
    }

    private void DetachNativeView()
    {
        if (_platformView is null) return;
        ClearSelection();
        _platformView.IsTextSelectionEnabled = false;
        _platformView = null;
    }

    private void ClearSelection()
    {
        if (_platformView?.ContentStart is not { } start) return;
        _platformView.Select(start, start);
    }
}
