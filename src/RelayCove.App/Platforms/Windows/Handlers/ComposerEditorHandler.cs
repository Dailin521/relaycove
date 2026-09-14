using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RelayCove.App.Controls;
using RelayCove.App.Platforms.Windows;
using RelayCove.App.Platforms.Windows.Behaviors;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using WinUiGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinUiKeyboardAccelerator = Microsoft.UI.Xaml.Input.KeyboardAccelerator;
using WinUiHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinUiSolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using WinUiThickness = Microsoft.UI.Xaml.Thickness;
using WinUiVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using WinDataPackageView = Windows.ApplicationModel.DataTransfer.DataPackageView;

namespace RelayCove.App.Platforms.Windows.Handlers;

public sealed class ComposerEditorHandler : ViewHandler<ComposerEditor, WinUiGrid>
{
    public static readonly IPropertyMapper<ComposerEditor, ComposerEditorHandler> Mapper =
        new PropertyMapper<ComposerEditor, ComposerEditorHandler>(ViewMapper)
        {
            [nameof(ComposerEditor.Text)] = MapText,
            [nameof(ComposerEditor.RealmEmojis)] = MapRealmEmojis,
            [nameof(ComposerEditor.TextColor)] = MapTextColor,
            [nameof(ComposerEditor.FontFamily)] = MapFontFamily,
            [nameof(ComposerEditor.FontSize)] = MapFontSize,
            [nameof(ComposerEditor.CursorPosition)] = MapSelection,
            [nameof(ComposerEditor.SelectionLength)] = MapSelection,
            [nameof(ComposerEditor.FocusRequest)] = MapFocusRequest,
            [nameof(ComposerEditor.IsEnabled)] = MapIsEnabled
        };

    private readonly RichEditBox _editor = new();
    private readonly WinUiSolidColorBrush _textBrush = new();
    private WinUiKeyboardAccelerator? _sendAccelerator;
    private WinUiKeyboardAccelerator? _newlineAccelerator;
    private bool _updatingFromPlatform;
    private bool _updatingPlatform;
    private bool _isTextCompositionActive;
    private int _lastFocusRequest;
    private CancellationTokenSource? _emojiCancellation;
    private bool _connected;
    private bool _refreshEmojiCatalogAfterComposition;

    public ComposerEditorHandler() : base(Mapper)
    {
    }

    protected override WinUiGrid CreatePlatformView()
    {
        var transparent = new WinUiSolidColorBrush(Microsoft.UI.Colors.Transparent);
        _editor.AcceptsReturn = true;
        // The native text surface must also be a drop target for events to reach the composer.
        _editor.AllowDrop = true;
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
        // Keep the shortcuts without WinUI's automatic floating "Enter" tooltip.
        _editor.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        _editor.IsSpellCheckEnabled = true;
        _editor.IsTextPredictionEnabled = true;
        _editor.DisabledFormattingAccelerators = DisabledFormattingAccelerators.All;
        _editor.ClipboardCopyFormat = RichEditClipboardFormat.PlainText;
        // Native visual states override Foreground with these resources. Share one
        // brush so an active state also changes color when the app theme changes.
        _editor.Foreground = _textBrush;
        _editor.Resources["TextControlForeground"] = _textBrush;
        _editor.Resources["TextControlForegroundPointerOver"] = _textBrush;
        _editor.Resources["TextControlForegroundFocused"] = _textBrush;
        _editor.Resources["TextControlForegroundDisabled"] = _textBrush;
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
        _connected = true;
        _editor.TextChanged += OnTextChanged;
        _editor.SelectionChanged += OnSelectionChanged;
        _editor.PreviewKeyDown += OnPreviewKeyDown;
        _editor.Paste += OnPaste;
        _editor.CopyingToClipboard += OnCopyingToClipboard;
        _editor.CuttingToClipboard += OnCuttingToClipboard;
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
        _connected = false;
        _emojiCancellation?.Cancel();
        _emojiCancellation?.Dispose();
        _emojiCancellation = null;
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
        _editor.PreviewKeyDown -= OnPreviewKeyDown;
        _editor.Paste -= OnPaste;
        _editor.CopyingToClipboard -= OnCopyingToClipboard;
        _editor.CuttingToClipboard -= OnCuttingToClipboard;
        _editor.SelectionChanged -= OnSelectionChanged;
        _editor.TextChanged -= OnTextChanged;
        _isTextCompositionActive = false;
        base.DisconnectHandler(platformView);
    }

    private static void MapText(ComposerEditorHandler handler, ComposerEditor view)
    {
        if (handler._updatingFromPlatform) return;
        var desired = ToDocumentText(view.Text ?? string.Empty);
        if (ToDocumentText(handler.ReadDocument().RawText) == desired) return;

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
        handler.ScheduleEmojiRendering();
    }

    private static void MapRealmEmojis(ComposerEditorHandler handler, ComposerEditor view)
    {
        if (handler._isTextCompositionActive)
        {
            handler._refreshEmojiCatalogAfterComposition = true;
            return;
        }
        handler._refreshEmojiCatalogAfterComposition = false;
        // An account/catalog change must not keep an image from the old catalog.
        var snapshot = handler.ReadDocument();
        if (snapshot.NativeText.Contains('\uFFFC'))
        {
            handler._updatingPlatform = true;
            try { handler._editor.Document.SetText(TextSetOptions.None, ToDocumentText(snapshot.RawText)); }
            finally { handler._updatingPlatform = false; }
            handler.ApplySelection(view);
        }
        handler.ScheduleEmojiRendering();
    }

    private static void MapTextColor(ComposerEditorHandler handler, ComposerEditor view)
    {
        handler._textBrush.Color = view.TextColor.ToWindowsColor();
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
        MapRealmEmojis(handler, view);
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
            var text = ReadDocument().RawText;
            var changed = !string.Equals(ToDocumentText(VirtualView.Text), ToDocumentText(text), StringComparison.Ordinal);
            VirtualView.Text = text;
            PublishSelection();
            // Undoing only image formatting leaves identical raw text; do not
            // immediately redo that conversion and trap the native undo stack.
            if (changed) ScheduleEmojiRendering();
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
        var documentText = ReadDocument();
        var selection = _editor.Document.Selection;
        var start = Math.Min(selection.StartPosition, selection.EndPosition);
        var end = Math.Max(selection.StartPosition, selection.EndPosition);
        VirtualView.CursorPosition = documentText.ToTextIndex(start);
        VirtualView.SelectionLength =
            documentText.ToTextIndex(end) - VirtualView.CursorPosition;
    }

    private void ApplySelection(ComposerEditor view)
    {
        if (_updatingPlatform) return;
        var snapshot = ReadDocument();
        var text = view.Text ?? string.Empty;
        var cursor = Math.Clamp(view.CursorPosition, 0, text.Length);
        var endCursor = Math.Clamp(cursor + view.SelectionLength, cursor, text.Length);
        var start = snapshot.ToDocumentIndex(FromDocumentText(ToDocumentText(text[..cursor])).Length);
        var end = snapshot.ToDocumentIndex(FromDocumentText(ToDocumentText(text[..endCursor])).Length);

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
        _emojiCancellation?.Cancel();
    }

    private void OnTextCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs eventArgs)
    {
        _isTextCompositionActive = false;
        if (_refreshEmojiCatalogAfterComposition && VirtualView is { } view) MapRealmEmojis(this, view);
        else ScheduleEmojiRendering();
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

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        if (eventArgs.Key != VirtualKey.V ||
            !IsControlPressed() ||
            _isTextCompositionActive)
        {
            return;
        }
        if (TryPasteAttachments()) eventArgs.Handled = true;
    }

    private void OnPaste(object sender, TextControlPasteEventArgs eventArgs)
    {
        if (!_isTextCompositionActive && TryPasteAttachments()) eventArgs.Handled = true;
    }

    private bool TryPasteAttachments()
    {
        if (VirtualView is not { IsEnabled: true, PasteAttachmentsCommand: { } command }) return false;
        WinDataPackageView dataView;
        try
        {
            dataView = WinClipboard.GetContent();
            if (!dataView.Contains(StandardDataFormats.StorageItems) && !dataView.Contains(StandardDataFormats.Bitmap)) return false;
        }
        catch
        {
            return false;
        }

        // Explorer may offer a bitmap alongside a copied image file. Keep the original file only.
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>> readAsync = async token =>
        {
            if (dataView.Contains(StandardDataFormats.StorageItems))
                return await ClipboardFileAttachmentFactory.CreateAsync(dataView, token);
            var image = await ClipboardImageAttachmentFactory.CreateAsync(dataView, DateTimeOffset.Now);
            token.ThrowIfCancellationRequested();
            return [image];
        };
        if (command.CanExecute(readAsync)) command.Execute(readAsync);
        return true;
    }

    private string GetDocumentText()
    {
        _editor.Document.GetText(TextGetOptions.None, out var text);
        return text.EndsWith('\r') ? text[..^1] : text;
    }

    private ComposerDocumentText ReadDocument()
    {
        var text = GetDocumentText();
        var images = new Dictionary<int, string>();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\uFFFC') continue;
            _editor.Document.GetRange(index, index + 1).GetText(TextGetOptions.UseObjectText, out var alternate);
            images[index] = alternate;
        }
        return new ComposerDocumentText(text, images);
    }

    private void OnCopyingToClipboard(RichEditBox sender, TextControlCopyingToClipboardEventArgs args)
    {
        args.Handled = CopySelection(cut: false);
    }

    private void OnCuttingToClipboard(RichEditBox sender, TextControlCuttingToClipboardEventArgs args)
    {
        args.Handled = CopySelection(cut: true);
    }

    private bool CopySelection(bool cut)
    {
        var selection = _editor.Document.Selection;
        if (selection.StartPosition == selection.EndPosition) return false;
        selection.GetText(TextGetOptions.None, out var nativeText);
        if (!nativeText.Contains('\uFFFC')) return false;
        selection.GetText(TextGetOptions.UseObjectText, out var text);
        var data = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(FromDocumentText(text));
        WinClipboard.SetContent(data);
        if (cut) selection.SetText(TextSetOptions.None, string.Empty);
        return true;
    }

    private void ScheduleEmojiRendering()
    {
        _emojiCancellation?.Cancel();
        _emojiCancellation?.Dispose();
        _emojiCancellation = null;
        if (!_connected || _isTextCompositionActive || VirtualView?.RealmEmojis is not { Count: > 0 }) return;
        var cancellation = _emojiCancellation = new CancellationTokenSource();
        _ = RenderEmojisAsync(cancellation.Token);
    }

    private async Task RenderEmojisAsync(CancellationToken cancellationToken)
    {
        var images = new List<(int Start, int End, string Shortcode, global::Windows.Storage.Streams.IRandomAccessStream Content)>();
        try
        {
            // Allow bindings to finish updating text and caret before reading them.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var view = VirtualView;
            var service = MauiContext?.Services.GetService(typeof(IRealmMediaService)) as IRealmMediaService;
            var session = MauiContext?.Services.GetService(typeof(IClientSession)) as IClientSession;
            if (view is null || service is null || session?.AccountId is not { } accountId) return;
            var snapshot = ReadDocument();
            var raw = snapshot.RawText;
            var rawIndex = 0;
            foreach (var run in EmojiShortcodeCatalog.CreateRuns(raw, view.RealmEmojis, replaceUnicode: false))
            {
                var start = snapshot.ToDocumentIndex(rawIndex);
                rawIndex += run.Text.Length;
                if (run.EmojiSourceUrl is null || start < snapshot.NativeText.Length && snapshot.NativeText[start] == '\uFFFC') continue;
                if (await service.GetImageAsync(run.EmojiSourceUrl, RealmMediaKind.Emoji, cancellationToken) is not StreamImageSource source) continue;
                using var stream = await source.Stream(cancellationToken);
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes, cancellationToken);
                var imageStream = await ComposerEmojiImageStream.CreateAsync(bytes.ToArray(), cancellationToken);
                images.Add((start, snapshot.ToDocumentIndex(rawIndex), run.Text, imageStream));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (images.Count == 0 || !_connected || _isTextCompositionActive || session.AccountId != accountId ||
                !ReferenceEquals(view, VirtualView) || GetDocumentText() != snapshot.NativeText || ReadDocument().RawText != raw) return;

            _updatingPlatform = true;
            _editor.Document.BeginUndoGroup();
            _editor.Document.BatchDisplayUpdates();
            try
            {
                foreach (var image in images.AsEnumerable().Reverse())
                {
                    var range = _editor.Document.GetRange(image.Start, image.End);
                    range.InsertImage(EmojiInlineLayout.ComposerSize, EmojiInlineLayout.ComposerSize,
                        EmojiInlineLayout.ComposerSize - EmojiInlineLayout.Descent(view.FontSize),
                        VerticalCharacterAlignment.Baseline, image.Shortcode, image.Content);
                }
            }
            catch
            {
                // InsertImage can leave an empty object before throwing. Restore
                // the full raw draft so failed images remain readable shortcodes.
                _editor.Document.SetText(TextSetOptions.None, ToDocumentText(raw));
            }
            finally
            {
                _editor.Document.ApplyDisplayUpdates();
                _editor.Document.EndUndoGroup();
                _updatingPlatform = false;
                ApplySelection(view);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // Keep the literal shortcode if safe media loading or native decoding fails.
        }
        finally
        {
            // Keep every native stream alive until batched display updates finish.
            foreach (var image in images) image.Content.Dispose();
        }
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

    internal static (int Start, int End) GetDocumentSelection(
        string text,
        int cursorPosition,
        int selectionLength)
    {
        var startTextIndex = Math.Clamp(cursorPosition, 0, text.Length);
        var endTextIndex = Math.Clamp(startTextIndex + selectionLength, startTextIndex, text.Length);
        return (
            TextIndexToDocumentIndex(text, startTextIndex),
            TextIndexToDocumentIndex(text, endTextIndex));
    }

    private static bool IsControlPressed()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

}
