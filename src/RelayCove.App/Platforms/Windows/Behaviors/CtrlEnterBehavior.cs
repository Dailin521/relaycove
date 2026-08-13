using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class CtrlEnterBehavior : PlatformBehavior<Editor, Microsoft.UI.Xaml.Controls.TextBox>
{
    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command),
        typeof(ICommand),
        typeof(CtrlEnterBehavior));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnAttachedTo(Editor bindable, Microsoft.UI.Xaml.Controls.TextBox platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        platformView.KeyDown += OnKeyDown;
    }

    protected override void OnDetachedFrom(Editor bindable, Microsoft.UI.Xaml.Controls.TextBox platformView)
    {
        platformView.KeyDown -= OnKeyDown;
        base.OnDetachedFrom(bindable, platformView);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        if (eventArgs.Key != VirtualKey.Enter || !IsControlPressed()) return;
        if (Command?.CanExecute(null) != true) return;

        eventArgs.Handled = true;
        Command.Execute(null);
    }

    private static bool IsControlPressed()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }
}
