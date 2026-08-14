using WinUiButton = Microsoft.UI.Xaml.Controls.Button;
using WinUiHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinUiVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class LeftAlignedButtonContentBehavior : Behavior<Button>
{
    protected override void OnAttachedTo(Button bindable)
    {
        base.OnAttachedTo(bindable);
        bindable.HandlerChanged += OnHandlerChanged;
        Apply(bindable);
    }

    protected override void OnDetachingFrom(Button bindable)
    {
        bindable.HandlerChanged -= OnHandlerChanged;
        base.OnDetachingFrom(bindable);
    }

    private static void OnHandlerChanged(object? sender, EventArgs eventArgs)
    {
        if (sender is Button button) Apply(button);
    }

    private static void Apply(Button button)
    {
        if (button.Handler?.PlatformView is not WinUiButton platformButton) return;
        platformButton.HorizontalContentAlignment = WinUiHorizontalAlignment.Left;
        platformButton.VerticalContentAlignment = WinUiVerticalAlignment.Center;
    }
}
