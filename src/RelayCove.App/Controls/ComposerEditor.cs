using System.Windows.Input;

namespace RelayCove.App.Controls;

public sealed class ComposerEditor : View
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text),
        typeof(string),
        typeof(ComposerEditor),
        string.Empty,
        BindingMode.TwoWay);

    public static readonly BindableProperty TextColorProperty = BindableProperty.Create(
        nameof(TextColor),
        typeof(Color),
        typeof(ComposerEditor),
        Colors.Black);

    public static readonly BindableProperty FontFamilyProperty = BindableProperty.Create(
        nameof(FontFamily),
        typeof(string),
        typeof(ComposerEditor),
        null);

    public static readonly BindableProperty FontSizeProperty = BindableProperty.Create(
        nameof(FontSize),
        typeof(double),
        typeof(ComposerEditor),
        14d);

    public static readonly BindableProperty CursorPositionProperty = BindableProperty.Create(
        nameof(CursorPosition),
        typeof(int),
        typeof(ComposerEditor),
        0,
        BindingMode.TwoWay);

    public static readonly BindableProperty SelectionLengthProperty = BindableProperty.Create(
        nameof(SelectionLength),
        typeof(int),
        typeof(ComposerEditor),
        0,
        BindingMode.TwoWay);

    public static readonly BindableProperty FocusRequestProperty = BindableProperty.Create(
        nameof(FocusRequest),
        typeof(int),
        typeof(ComposerEditor),
        0);

    public static readonly BindableProperty SendCommandProperty = BindableProperty.Create(
        nameof(SendCommand),
        typeof(ICommand),
        typeof(ComposerEditor));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Color TextColor
    {
        get => (Color)GetValue(TextColorProperty);
        set => SetValue(TextColorProperty, value);
    }

    public string? FontFamily
    {
        get => (string?)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public int CursorPosition
    {
        get => (int)GetValue(CursorPositionProperty);
        set => SetValue(CursorPositionProperty, value);
    }

    public int SelectionLength
    {
        get => (int)GetValue(SelectionLengthProperty);
        set => SetValue(SelectionLengthProperty, value);
    }

    public int FocusRequest
    {
        get => (int)GetValue(FocusRequestProperty);
        set => SetValue(FocusRequestProperty, value);
    }

    public ICommand? SendCommand
    {
        get => (ICommand?)GetValue(SendCommandProperty);
        set => SetValue(SendCommandProperty, value);
    }
}
