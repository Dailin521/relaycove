using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUiStyle = Microsoft.UI.Xaml.Style;
using WinUiSetter = Microsoft.UI.Xaml.Setter;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class CompactEmojiGridBehavior : PlatformBehavior<CollectionView, GridView>
{
    private WinUiStyle? _originalStyle;
    private WinUiStyle? _compactStyle;

    protected override void OnAttachedTo(CollectionView bindable, GridView platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        _originalStyle = platformView.ItemContainerStyle;
        _compactStyle = new WinUiStyle(typeof(GridViewItem)) { BasedOn = _originalStyle };
        // MAUI's grid style retains WinUI's default minimum item size, which
        // otherwise adds blank space around the compact emoji template.
        _compactStyle.Setters.Add(new WinUiSetter(FrameworkElement.MinWidthProperty, 0d));
        _compactStyle.Setters.Add(new WinUiSetter(FrameworkElement.MinHeightProperty, 0d));
        platformView.ItemContainerStyle = _compactStyle;
    }

    protected override void OnDetachedFrom(CollectionView bindable, GridView platformView)
    {
        if (ReferenceEquals(platformView.ItemContainerStyle, _compactStyle))
            platformView.ItemContainerStyle = _originalStyle;
        _originalStyle = null;
        _compactStyle = null;
        base.OnDetachedFrom(bindable, platformView);
    }
}
