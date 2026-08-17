using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class ComposerEnterBehavior : PlatformBehavior<Editor, Microsoft.UI.Xaml.Controls.TextBox>
{
    private bool _isTextCompositionActive;

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command),
        typeof(ICommand),
        typeof(ComposerEnterBehavior));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnAttachedTo(Editor bindable, Microsoft.UI.Xaml.Controls.TextBox platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        platformView.KeyDown += OnKeyDown;
        platformView.TextCompositionStarted += OnTextCompositionStarted;
        platformView.TextCompositionEnded += OnTextCompositionEnded;
    }

    protected override void OnDetachedFrom(Editor bindable, Microsoft.UI.Xaml.Controls.TextBox platformView)
    {
        platformView.KeyDown -= OnKeyDown;
        platformView.TextCompositionStarted -= OnTextCompositionStarted;
        platformView.TextCompositionEnded -= OnTextCompositionEnded;
        _isTextCompositionActive = false;
        base.OnDetachedFrom(bindable, platformView);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        if (!ShouldSend(eventArgs.Key, IsControlPressed(), _isTextCompositionActive)) return;

        eventArgs.Handled = true;
        if (Command?.CanExecute(null) == true)
        {
            Command.Execute(null);
        }
    }

    internal static bool ShouldSend(VirtualKey key, bool isControlPressed, bool isTextCompositionActive) =>
        key == VirtualKey.Enter && !isControlPressed && !isTextCompositionActive;

    private void OnTextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs eventArgs) =>
        _isTextCompositionActive = true;

    private void OnTextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs eventArgs) =>
        _isTextCompositionActive = false;

    private static bool IsControlPressed()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }
}
