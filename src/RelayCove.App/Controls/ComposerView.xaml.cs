using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ComposerView : ContentView
{
    public ComposerView()
    {
        InitializeComponent();
    }

    public ShellViewModel? ViewModel => BindingContext as ShellViewModel;

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        OnPropertyChanged(nameof(ViewModel));
    }

    private void OnEmojiPickerClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not VisualElement trigger || ViewModel is null) return;
        var anchor = GetPositionOnPage(trigger);
        ViewModel.OpenComposerEmojiPickerAtCommand.Execute(new PopoverAnchorRequest(anchor.X, anchor.Y));
    }

    private static Point GetPositionOnPage(VisualElement element)
    {
#if WINDOWS
        if (element.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement platformView &&
            Microsoft.Maui.Controls.Application.Current?.Windows
                .Select(window => window.Page?.Handler?.PlatformView)
                .OfType<Microsoft.UI.Xaml.FrameworkElement>()
                .FirstOrDefault() is { } pageRoot)
        {
            var point = platformView.TransformToVisual(pageRoot)
                .TransformPoint(new Windows.Foundation.Point(0d, 0d));
            return new Point(point.X, point.Y);
        }
#endif

        var x = 0d;
        var y = 0d;
        for (Element? current = element; current is VisualElement visual; current = visual.Parent)
        {
            x += visual.X;
            y += visual.Y;
        }

        return new Point(x, y);
    }
}
