using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ChatHeaderView : ContentView
{
    public ChatHeaderView()
    {
        InitializeComponent();
    }

    internal void FocusDetailsButton() => DetailsButton.Focus();
    internal void FocusSearchButton() => SearchButton.Focus();
    internal void FocusTopicMenuButton() => TopicMenuButton.Focus();

    private void OnTopicMenuClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not VisualElement trigger ||
            BindingContext is not ShellViewModel { SelectedTopic: { } topic } viewModel)
        {
            return;
        }

        var anchor = GetTopicMenuAnchor(trigger);
        viewModel.OpenTopicMenuAtCommand.Execute(new TopicMenuRequest(topic, anchor.X, anchor.Y, RestoreFocusToHeader: true));
    }

    private static Point GetTopicMenuAnchor(VisualElement trigger)
    {
#if WINDOWS
        var source = trigger.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
        var pageRoot = Application.Current?.Windows
            .Select(window => window.Page?.Handler?.PlatformView)
            .OfType<Microsoft.UI.Xaml.FrameworkElement>()
            .FirstOrDefault();
        if (source is not null && pageRoot is not null)
        {
            try
            {
                var point = source.TransformToVisual(pageRoot)
                    .TransformPoint(new Windows.Foundation.Point(source.ActualWidth, source.ActualHeight));
                return new Point(point.X, point.Y);
            }
            catch (InvalidOperationException)
            {
            }
        }
#endif
        return new Point(12d, 68d);
    }
}
