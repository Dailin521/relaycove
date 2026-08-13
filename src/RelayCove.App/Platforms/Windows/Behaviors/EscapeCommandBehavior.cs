using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinUiKeyboardAccelerator = Microsoft.UI.Xaml.Input.KeyboardAccelerator;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class EscapeCommandBehavior : PlatformBehavior<Button, Microsoft.UI.Xaml.Controls.Button>
{
    private WinUiKeyboardAccelerator? _accelerator;

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command),
        typeof(ICommand),
        typeof(EscapeCommandBehavior));

    public static readonly BindableProperty TrapFocusProperty = BindableProperty.Create(
        nameof(TrapFocus),
        typeof(bool),
        typeof(EscapeCommandBehavior));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public bool TrapFocus
    {
        get => (bool)GetValue(TrapFocusProperty);
        set => SetValue(TrapFocusProperty, value);
    }

    protected override void OnAttachedTo(Button bindable, Microsoft.UI.Xaml.Controls.Button platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        _accelerator = new WinUiKeyboardAccelerator { Key = VirtualKey.Escape };
        _accelerator.Invoked += OnInvoked;
        platformView.KeyboardAccelerators.Add(_accelerator);
        platformView.KeyDown += OnKeyDown;
    }

    protected override void OnDetachedFrom(Button bindable, Microsoft.UI.Xaml.Controls.Button platformView)
    {
        if (_accelerator is not null)
        {
            _accelerator.Invoked -= OnInvoked;
            platformView.KeyboardAccelerators.Remove(_accelerator);
            _accelerator = null;
        }

        platformView.KeyDown -= OnKeyDown;

        base.OnDetachedFrom(bindable, platformView);
    }

    private void OnInvoked(WinUiKeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs eventArgs)
    {
        if (Command?.CanExecute(null) != true) return;
        Command.Execute(null);
        eventArgs.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        if (!TrapFocus || eventArgs.Key != VirtualKey.Tab) return;
        if (sender is Microsoft.UI.Xaml.Controls.Button button)
        {
            button.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        }

        eventArgs.Handled = true;
    }
}
