using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
using RelayCove.App.Controls;
using WinUiColor = Windows.UI.Color;

namespace RelayCove.App.Platforms.Windows.Handlers;

public sealed class NativeColorPickerHandler : ViewHandler<NativeColorPicker, ColorPicker>
{
    public static readonly IPropertyMapper<NativeColorPicker, NativeColorPickerHandler> Mapper =
        new PropertyMapper<NativeColorPicker, NativeColorPickerHandler>(ViewMapper)
        {
            [nameof(NativeColorPicker.HexColor)] = MapHexColor
        };

    private bool _updating;

    public NativeColorPickerHandler() : base(Mapper)
    {
    }

    protected override ColorPicker CreatePlatformView() => new()
    {
        IsAlphaEnabled = false,
        IsAlphaSliderVisible = false,
        IsAlphaTextInputVisible = false,
        IsColorChannelTextInputVisible = true,
        IsColorPreviewVisible = true,
        IsColorSliderVisible = true,
        IsColorSpectrumVisible = true,
        IsHexInputVisible = true,
        ColorSpectrumShape = ColorSpectrumShape.Box
    };

    protected override void ConnectHandler(ColorPicker platformView)
    {
        base.ConnectHandler(platformView);
        platformView.ColorChanged += OnColorChanged;
    }

    protected override void DisconnectHandler(ColorPicker platformView)
    {
        platformView.ColorChanged -= OnColorChanged;
        base.DisconnectHandler(platformView);
    }

    private void OnColorChanged(ColorPicker sender, ColorChangedEventArgs eventArgs)
    {
        if (_updating) return;
        var color = eventArgs.NewColor;
        VirtualView.HexColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static void MapHexColor(NativeColorPickerHandler handler, NativeColorPicker view)
    {
        if (!TryParse(view.HexColor, out var color) || handler.PlatformView.Color == color) return;
        handler._updating = true;
        try
        {
            handler.PlatformView.Color = color;
        }
        finally
        {
            handler._updating = false;
        }
    }

    private static bool TryParse(string? value, out WinUiColor color)
    {
        color = default;
        if (value is null) return false;
        var text = value.Trim();
        if (text.Length != 7 || text[0] != '#' ||
            !byte.TryParse(text.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var red) ||
            !byte.TryParse(text.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var green) ||
            !byte.TryParse(text.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
        {
            return false;
        }

        color = WinUiColor.FromArgb(255, red, green, blue);
        return true;
    }
}
