using Microsoft.Maui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class ComposerSelectionBehavior : PlatformBehavior<Editor, Microsoft.UI.Xaml.Controls.TextBox>
{
    private Editor? _editor;
    private Microsoft.UI.Xaml.Controls.TextBox? _platformView;
    private bool _updating;

    public static readonly BindableProperty CursorPositionProperty = BindableProperty.Create(
        nameof(CursorPosition),
        typeof(int),
        typeof(ComposerSelectionBehavior),
        0,
        BindingMode.TwoWay,
        propertyChanged: OnSelectionPropertyChanged);

    public static readonly BindableProperty SelectionLengthProperty = BindableProperty.Create(
        nameof(SelectionLength),
        typeof(int),
        typeof(ComposerSelectionBehavior),
        0,
        BindingMode.TwoWay,
        propertyChanged: OnSelectionPropertyChanged);

    public static readonly BindableProperty FocusRequestProperty = BindableProperty.Create(
        nameof(FocusRequest),
        typeof(int),
        typeof(ComposerSelectionBehavior),
        0,
        propertyChanged: OnFocusRequestChanged);

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

    protected override void OnAttachedTo(Editor bindable, Microsoft.UI.Xaml.Controls.TextBox platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        _editor = bindable;
        _platformView = platformView;
        platformView.SelectionChanged += OnSelectionChanged;
    }

    protected override void OnDetachedFrom(Editor bindable, Microsoft.UI.Xaml.Controls.TextBox platformView)
    {
        platformView.SelectionChanged -= OnSelectionChanged;
        _platformView = null;
        _editor = null;
        base.OnDetachedFrom(bindable, platformView);
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_updating || sender is not Microsoft.UI.Xaml.Controls.TextBox textBox) return;
        _updating = true;
        try
        {
            CursorPosition = textBox.SelectionStart;
            SelectionLength = textBox.SelectionLength;
        }
        finally
        {
            _updating = false;
        }
    }

    private static void OnSelectionPropertyChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((ComposerSelectionBehavior)bindable).ApplySelection();

    private static void OnFocusRequestChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((ComposerSelectionBehavior)bindable).FocusEditor();

    private void ApplySelection()
    {
        var textBox = _platformView;
        if (_updating || textBox is null) return;
        _updating = true;
        try
        {
            var length = textBox.Text?.Length ?? 0;
            textBox.SelectionStart = Math.Clamp(CursorPosition, 0, length);
            textBox.SelectionLength = Math.Clamp(SelectionLength, 0, length - textBox.SelectionStart);
        }
        finally
        {
            _updating = false;
        }
    }

    private void FocusEditor()
    {
        if (_editor is null || _platformView is null) return;
        _editor.Dispatcher.Dispatch(() =>
        {
            _platformView.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
            ApplySelection();
        });
    }
}
