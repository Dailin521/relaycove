using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RelayCove.App.Controls;

namespace RelayCove.App.Platforms.Windows;

public sealed class ComposerResizeHandleHandler : ViewHandler<ComposerResizeHandle, ContentControl>
{
    public static readonly IPropertyMapper<ComposerResizeHandle, ComposerResizeHandleHandler> Mapper =
        new PropertyMapper<ComposerResizeHandle, ComposerResizeHandleHandler>(ViewHandler.ViewMapper);

    public ComposerResizeHandleHandler()
        : base(Mapper)
    {
    }

    protected override ContentControl CreatePlatformView() => new()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
        VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
        IsTabStop = true
    };
}
