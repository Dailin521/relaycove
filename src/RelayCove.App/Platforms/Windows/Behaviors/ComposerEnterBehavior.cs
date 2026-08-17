using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using WinUiKeyboardAccelerator = Microsoft.UI.Xaml.Input.KeyboardAccelerator;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class ComposerEnterBehavior : PlatformBehavior<Editor, Microsoft.UI.Xaml.Controls.TextBox>
{
    private WinUiKeyboardAccelerator? _sendAccelerator;
    private WinUiKeyboardAccelerator? _newlineAccelerator;
    private TextBox? _platformView;
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
        _platformView = platformView;
        // KeyboardAccelerator otherwise matches Enter even while Ctrl is down
        // on some WinUI TextBox paths. Limit this command to plain Enter so
        // Ctrl+Enter remains the native multi-line newline gesture.
        _sendAccelerator = new WinUiKeyboardAccelerator
        {
            Key = VirtualKey.Enter,
            Modifiers = VirtualKeyModifiers.None
        };
        _sendAccelerator.Invoked += OnSendAcceleratorInvoked;
        platformView.KeyboardAccelerators.Add(_sendAccelerator);
        _newlineAccelerator = new WinUiKeyboardAccelerator
        {
            Key = VirtualKey.Enter,
            Modifiers = VirtualKeyModifiers.Control
        };
        _newlineAccelerator.Invoked += OnNewlineAcceleratorInvoked;
        platformView.KeyboardAccelerators.Add(_newlineAccelerator);
        platformView.TextCompositionStarted += OnTextCompositionStarted;
        platformView.TextCompositionEnded += OnTextCompositionEnded;
    }

    protected override void OnDetachedFrom(Editor bindable, Microsoft.UI.Xaml.Controls.TextBox platformView)
    {
        if (_sendAccelerator is not null)
        {
            _sendAccelerator.Invoked -= OnSendAcceleratorInvoked;
            platformView.KeyboardAccelerators.Remove(_sendAccelerator);
            _sendAccelerator = null;
        }
        if (_newlineAccelerator is not null)
        {
            _newlineAccelerator.Invoked -= OnNewlineAcceleratorInvoked;
            platformView.KeyboardAccelerators.Remove(_newlineAccelerator);
            _newlineAccelerator = null;
        }

        platformView.TextCompositionStarted -= OnTextCompositionStarted;
        platformView.TextCompositionEnded -= OnTextCompositionEnded;
        _isTextCompositionActive = false;
        _platformView = null;
        base.OnDetachedFrom(bindable, platformView);
    }

    private void OnSendAcceleratorInvoked(
        WinUiKeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs eventArgs)
    {
        if (!ShouldSend(VirtualKey.Enter, IsControlPressed(), _isTextCompositionActive)) return;

        eventArgs.Handled = true;
        if (Command?.CanExecute(null) == true)
        {
            Command.Execute(null);
        }
    }

    private void OnNewlineAcceleratorInvoked(
        WinUiKeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs eventArgs)
    {
        if (_isTextCompositionActive || _platformView is null) return;

        var text = _platformView.Text ?? string.Empty;
        var (content, cursor) = InsertNewLine(
            text,
            _platformView.SelectionStart,
            _platformView.SelectionLength);
        _platformView.Text = content;
        _platformView.SelectionStart = cursor;
        _platformView.SelectionLength = 0;
        eventArgs.Handled = true;
    }

    internal static bool ShouldSend(VirtualKey key, bool isControlPressed, bool isTextCompositionActive) =>
        key == VirtualKey.Enter && !isControlPressed && !isTextCompositionActive;

    internal static (string Content, int CursorPosition) InsertNewLine(
        string text,
        int cursorPosition,
        int selectionLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = Math.Clamp(cursorPosition, 0, text.Length);
        var length = Math.Clamp(selectionLength, 0, text.Length - start);
        var content = string.Concat(
            text.AsSpan(0, start),
            Environment.NewLine,
            text.AsSpan(start + length));
        return (content, start + Environment.NewLine.Length);
    }

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
