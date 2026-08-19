using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RelayCove.App.Controls;
using RelayCove.App.Platforms.Windows.Behaviors;
using Windows.System;
using Windows.UI.Core;
using WinUiGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinUiKeyboardAccelerator = Microsoft.UI.Xaml.Input.KeyboardAccelerator;
using WinUiHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinUiSolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using WinUiThickness = Microsoft.UI.Xaml.Thickness;
using WinUiVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;

namespace RelayCove.App.Platforms.Windows.Handlers;

public sealed class ComposerEditorHandler : ViewHandler<ComposerEditor, WinUiGrid>
{
    public static readonly IPropertyMapper<ComposerEditor, ComposerEditorHandler> Mapper =
        new PropertyMapper<ComposerEditor, ComposerEditorHandler>(ViewMapper)
        {
            [nameof(ComposerEditor.Text)] = MapText,
            [nameof(ComposerEditor.TextColor)] = MapTextColor,
            [nameof(ComposerEditor.FontFamily)] = MapFontFamily,
            [nameof(ComposerEditor.FontSize)] = MapFontSize,
            [nameof(ComposerEditor.CursorPosition)] = MapSelection,
            [nameof(ComposerEditor.SelectionLength)] = MapSelection,
            [nameof(ComposerEditor.FocusRequest)] = MapFocusRequest,
            [nameof(ComposerEditor.IsEnabled)] = MapIsEnabled
        };

    private readonly RichEditBox _editor = new();
    private WinUiKeyboardAccelerator? _sendAccelerator;
    private WinUiKeyboardAccelerator? _newlineAccelerator;
    private bool _updatingFromPlatform;
    private bool _updatingPlatform;
    private bool _isTextCompositionActive;
    private int _lastFocusRequest;

    public ComposerEditorHandler() : base(Mapper)
    {
    }

    protected override WinUiGrid CreatePlatformView()
    {
        var transparent = new WinUiSolidColorBrush(Microsoft.UI.Colors.Transparent);
        _editor.AcceptsReturn = true;
        _editor.TextWrapping = TextWrapping.Wrap;
        _editor.HorizontalAlignment = WinUiHorizontalAlignment.Stretch;
        _editor.VerticalAlignment = WinUiVerticalAlignment.Stretch;
        _editor.HorizontalContentAlignment = WinUiHorizontalAlignment.Stretch;
        _editor.VerticalContentAlignment = WinUiVerticalAlignment.Top;
        _editor.Background = transparent;
        _editor.BorderBrush = transparent;
        _editor.BorderThickness = new WinUiThickness(0);
        _editor.Padding = new WinUiThickness(13d, 7d, 13d, 7d);
        _editor.UseSystemFocusVisuals = false;
        _editor.IsSpellCheckEnabled = true;
        _editor.IsTextPredictionEnabled = true;
        _editor.DisabledFormattingAccelerators = DisabledFormattingAccelerators.All;
        _editor.ClipboardCopyFormat = RichEditClipboardFormat.PlainText;
        _editor.Resources["TextControlBackground"] = transparent;
        _editor.Resources["TextControlBackgroundPointerOver"] = transparent;
        _editor.Resources["TextControlBackgroundFocused"] = transparent;
        _editor.Resources["TextControlBorderBrush"] = transparent;
        _editor.Resources["TextControlBorderBrushPointerOver"] = transparent;
        _editor.Resources["TextControlBorderBrushFocused"] = transparent;
        _editor.Document.CaretType = CaretType.Normal;

        var root = new WinUiGrid();
        root.Children.Add(_editor);
        return root;
    }

    protected override void ConnectHandler(WinUiGrid platformView)
    {
        base.ConnectHandler(platformView);
        _editor.TextChanged += OnTextChanged;
        _editor.SelectionChanged += OnSelectionChanged;
        _editor.TextCompositionStarted += OnTextCompositionStarted;
        _editor.TextCompositionEnded += OnTextCompositionEnded;

        _sendAccelerator = new WinUiKeyboardAccelerator
        {
            Key = VirtualKey.Enter,
            Modifiers = VirtualKeyModifiers.None
        };
        _sendAccelerator.Invoked += OnSendAcceleratorInvoked;
        _editor.KeyboardAccelerators.Add(_sendAccelerator);

        _newlineAccelerator = new WinUiKeyboardAccelerator
        {
            Key = VirtualKey.Enter,
            Modifiers = VirtualKeyModifiers.Control
        };
        _newlineAccelerator.Invoked += OnNewlineAcceleratorInvoked;
        _editor.KeyboardAccelerators.Add(_newlineAccelerator);

    }

    protected override void DisconnectHandler(WinUiGrid platformView)
    {
        if (_sendAccelerator is not null)
        {
            _sendAccelerator.Invoked -= OnSendAcceleratorInvoked;
            _editor.KeyboardAccelerators.Remove(_sendAccelerator);
            _sendAccelerator = null;
        }

        if (_newlineAccelerator is not null)
        {
            _newlineAccelerator.Invoked -= OnNewlineAcceleratorInvoked;
            _editor.KeyboardAccelerators.Remove(_newlineAccelerator);
            _newlineAccelerator = null;
        }

        _editor.TextCompositionEnded -= OnTextCompositionEnded;
        _editor.TextCompositionStarted -= OnTextCompositionStarted;
        _editor.SelectionChanged -= OnSelectionChanged;
        _editor.TextChanged -= OnTextChanged;
        _isTextCompositionActive = false;
        base.DisconnectHandler(platformView);
    }

    private static void MapText(ComposerEditorHandler handler, ComposerEditor view)
    {
        if (handler._updatingFromPlatform) return;
        var desired = ToDocumentText(view.Text ?? string.Empty);
        if (handler.GetDocumentText() == desired) return;

        handler._updatingPlatform = true;
        try
        {
            handler._editor.Document.SetText(TextSetOptions.None, desired);
        }
        finally
        {
            handler._updatingPlatform = false;
        }

        handler.ApplySelection(view);
    }

    private static void MapTextColor(ComposerEditorHandler handler, ComposerEditor view)
    {
        var brush = view.TextColor.ToPlatform();
        handler._editor.Foreground = brush;
    }

    private static void MapFontFamily(ComposerEditorHandler handler, ComposerEditor view)
    {
        if (!string.IsNullOrWhiteSpace(view.FontFamily))
        {
            handler._editor.FontFamily = new FontFamily(view.FontFamily);
        }
    }

    private static void MapFontSize(ComposerEditorHandler handler, ComposerEditor view)
    {
        handler._editor.FontSize = view.FontSize;
    }

    private static void MapSelection(ComposerEditorHandler handler, ComposerEditor view)
    {
        if (!handler._updatingFromPlatform)
        {
            handler.ApplySelection(view);
        }
    }

    private static void MapFocusRequest(ComposerEditorHandler handler, ComposerEditor view)
    {
        if (handler._lastFocusRequest == view.FocusRequest) return;
        handler._lastFocusRequest = view.FocusRequest;
        handler._editor.Focus(FocusState.Keyboard);
        handler.ApplySelection(view);
    }

    private static void MapIsEnabled(ComposerEditorHandler handler, ComposerEditor view) =>
        handler._editor.IsEnabled = view.IsEnabled;

    private void OnTextChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_updatingPlatform || VirtualView is null) return;
        _updatingFromPlatform = true;
        try
        {
            VirtualView.Text = FromDocumentText(GetDocumentText());
            PublishSelection();
        }
        finally
        {
            _updatingFromPlatform = false;
        }

    }

    private void OnSelectionChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_updatingPlatform)
        {
            _updatingFromPlatform = true;
            try
            {
                PublishSelection();
            }
            finally
            {
                _updatingFromPlatform = false;
            }
        }

    }

    private void PublishSelection()
    {
        if (VirtualView is null) return;
        var documentText = GetDocumentText();
        var selection = _editor.Document.Selection;
        var start = Math.Min(selection.StartPosition, selection.EndPosition);
        var end = Math.Max(selection.StartPosition, selection.EndPosition);
        VirtualView.CursorPosition = DocumentIndexToTextIndex(documentText, start);
        VirtualView.SelectionLength =
            DocumentIndexToTextIndex(documentText, end) - VirtualView.CursorPosition;
    }

    private void ApplySelection(ComposerEditor view)
    {
        if (_updatingPlatform) return;
        var documentText = GetDocumentText();
        var text = FromDocumentText(documentText);
        var startTextIndex = Math.Clamp(view.CursorPosition, 0, text.Length);
        var endTextIndex = Math.Clamp(startTextIndex + view.SelectionLength, startTextIndex, text.Length);
        var start = TextIndexToDocumentIndex(text, startTextIndex);
        var end = TextIndexToDocumentIndex(text, endTextIndex);

        _updatingPlatform = true;
        try
        {
            _editor.Document.Selection.SetRange(start, end);
        }
        finally
        {
            _updatingPlatform = false;
        }
    }

    private void OnTextCompositionStarted(RichEditBox sender, TextCompositionStartedEventArgs eventArgs)
    {
        _isTextCompositionActive = true;
    }

    private void OnTextCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs eventArgs)
    {
        _isTextCompositionActive = false;
    }

    private void OnSendAcceleratorInvoked(
        WinUiKeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs eventArgs)
    {
        if (!ComposerEnterBehavior.ShouldSend(
                VirtualKey.Enter,
                IsControlPressed(),
                _isTextCompositionActive))
        {
            return;
        }

        eventArgs.Handled = true;
        if (VirtualView?.SendCommand?.CanExecute(null) == true)
        {
            VirtualView.SendCommand.Execute(null);
        }
    }

    private void OnNewlineAcceleratorInvoked(
        WinUiKeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs eventArgs)
    {
        if (_isTextCompositionActive) return;
        var selection = _editor.Document.Selection;
        var insertionPosition = Math.Min(selection.StartPosition, selection.EndPosition);
        selection.SetText(TextSetOptions.None, "\r");
        selection.SetRange(insertionPosition + 1, insertionPosition + 1);
        eventArgs.Handled = true;
    }

    private string GetDocumentText()
    {
        _editor.Document.GetText(TextGetOptions.None, out var text);
        return text.EndsWith('\r') ? text[..^1] : text;
    }

    internal static string ToDocumentText(string text) =>
        text.Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace('\n', '\r');

    internal static string FromDocumentText(string text) =>
        text.Replace("\r", Environment.NewLine, StringComparison.Ordinal);

    internal static int DocumentIndexToTextIndex(string documentText, int documentIndex)
    {
        var limit = Math.Clamp(documentIndex, 0, documentText.Length);
        var extra = 0;
        for (var index = 0; index < limit; index++)
        {
            if (documentText[index] == '\r') extra++;
        }

        return limit + extra;
    }

    internal static int TextIndexToDocumentIndex(string text, int textIndex)
    {
        var limit = Math.Clamp(textIndex, 0, text.Length);
        var documentIndex = 0;
        for (var index = 0; index < limit; index++)
        {
            if (text[index] == '\r' && index + 1 < limit && text[index + 1] == '\n')
            {
                index++;
            }

            documentIndex++;
        }

        return documentIndex;
    }

    private static bool IsControlPressed()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

}
