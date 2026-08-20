namespace RelayCove.App.Controls;

public sealed class NativeColorPicker : View
{
    public static readonly BindableProperty HexColorProperty = BindableProperty.Create(
        nameof(HexColor),
        typeof(string),
        typeof(NativeColorPicker),
        "#000000",
        BindingMode.TwoWay);

    public string HexColor
    {
        get => (string)GetValue(HexColorProperty);
        set => SetValue(HexColorProperty, value);
    }
}
