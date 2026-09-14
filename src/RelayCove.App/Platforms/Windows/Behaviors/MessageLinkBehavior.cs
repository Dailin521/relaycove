using Microsoft.Maui.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using RelayCove.App.ViewModels;
using WinPoint = Windows.Foundation.Point;
using WinRect = Windows.Foundation.Rect;
using WinUiRun = Microsoft.UI.Xaml.Documents.Run;
using WinUiTextBlock = Microsoft.UI.Xaml.Controls.TextBlock;
using WinTextDecorations = Windows.UI.Text.TextDecorations;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class MessageLinkBehavior : Behavior<Label>
{
    private Label? _label;
    private WinUiTextBlock? _platformView;
    private long _textCallbackToken;
    private bool _isUpdating;
    private readonly List<WinUiRun> _linkRuns = [];
    private readonly List<(WinUiRun Run, WinRect Bounds)> _linkBounds = [];
    private WinUiRun? _hoveredRun;

    protected override void OnAttachedTo(Label bindable)
    {
        base.OnAttachedTo(bindable);
        _label = bindable;
        bindable.HandlerChanged += OnHandlerChanged;
        AttachNativeView(bindable.Handler?.PlatformView as WinUiTextBlock);
    }

    protected override void OnDetachingFrom(Label bindable)
    {
        bindable.HandlerChanged -= OnHandlerChanged;
        DetachNativeView();
        _label = null;
        base.OnDetachingFrom(bindable);
    }

    private void OnHandlerChanged(object? sender, EventArgs eventArgs) =>
        AttachNativeView(_label?.Handler?.PlatformView as WinUiTextBlock);

    private void AttachNativeView(WinUiTextBlock? platformView)
    {
        DetachNativeView();
        if (platformView is null) return;
        _platformView = platformView;
        _textCallbackToken = platformView.RegisterPropertyChangedCallback(WinUiTextBlock.TextProperty, OnTextChanged);
        platformView.PointerMoved += OnPointerMoved;
        platformView.PointerExited += OnPointerExited;
        platformView.SizeChanged += OnSizeChanged;
        Rebuild();
    }

    private void DetachNativeView()
    {
        if (_platformView is not { } platformView) return;
        platformView.UnregisterPropertyChangedCallback(WinUiTextBlock.TextProperty, _textCallbackToken);
        platformView.PointerMoved -= OnPointerMoved;
        platformView.PointerExited -= OnPointerExited;
        platformView.SizeChanged -= OnSizeChanged;
        _hoveredRun = null;
        _linkRuns.Clear();
        _linkBounds.Clear();
        platformView.Inlines.Clear();
        platformView.Text = _label?.Text ?? string.Empty;
        _platformView = null;
    }

    private void OnTextChanged(DependencyObject sender, DependencyProperty property) => Rebuild();

    private void Rebuild()
    {
        if (_isUpdating || _platformView is not { } platformView) return;
        _isUpdating = true;
        try
        {
            _hoveredRun = null;
            _linkRuns.Clear();
            _linkBounds.Clear();
            var parts = MessageLinkParser.Split(platformView.Text ?? string.Empty);
            if (!parts.Any(part => part.Uri is not null)) return;

            var color = Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue("AccentColor", out var value) == true && value is Color accent
                ? accent
                : Colors.DodgerBlue;
            platformView.Inlines.Clear();
            foreach (var part in parts)
            {
                var run = new WinUiRun { Text = part.Text };
                if (part.Uri is null)
                {
                    platformView.Inlines.Add(run);
                    continue;
                }
                var link = new Hyperlink
                {
                    NavigateUri = part.Uri,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color.ToWindowsColor()),
                    UnderlineStyle = UnderlineStyle.None,
                    IsTabStop = false
                };
                link.Inlines.Add(run);
                platformView.Inlines.Add(link);
                _linkRuns.Add(run);
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_platformView is not { } platformView || _linkRuns.Count == 0) return;
        if (_linkBounds.Count == 0) MeasureLinkBounds();
        var pointer = eventArgs.GetCurrentPoint(platformView);
        SetHoveredRun(_linkBounds.FirstOrDefault(item => Contains(item.Bounds, pointer.Position)).Run,
            pointer.Properties.IsLeftButtonPressed);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_platformView is not { } platformView) return;
        SetHoveredRun(null, eventArgs.GetCurrentPoint(platformView).Properties.IsLeftButtonPressed);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        SetHoveredRun(null);
        _linkBounds.Clear();
    }

    private void MeasureLinkBounds()
    {
        // TextBlock has no point-to-text API. Use native character positions so
        // each wrapped line is hit-tested without underlining adjacent text.
        foreach (var run in _linkRuns)
        {
            for (var index = 0; index < run.Text.Length; index++)
            {
                var start = run.ContentStart.GetPositionAtOffset(index, LogicalDirection.Forward);
                var end = run.ContentStart.GetPositionAtOffset(index + 1, LogicalDirection.Backward);
                if (start is null || end is null) continue;
                var first = start.GetCharacterRect(LogicalDirection.Forward);
                var last = end.GetCharacterRect(LogicalDirection.Backward);
                if (first.Height <= 0 || last.Height <= 0 || Math.Abs(first.Y - last.Y) > 1) continue;
                var left = Math.Min(first.Left, last.Left);
                var right = Math.Max(first.Right, last.Right);
                if (right > left)
                    _linkBounds.Add((run, new WinRect(left, first.Y, right - left, first.Height)));
            }
        }
    }

    internal static bool ShouldUpdateHover(bool isLeftButtonPressed, string? selectedText) =>
        !isLeftButtonPressed && string.IsNullOrEmpty(selectedText);

    private void SetHoveredRun(WinUiRun? run, bool isLeftButtonPressed = false)
    {
        // WinUI clears the selection when an inline's TextDecorations changes.
        // Keep decorations stable during a drag and until the user clears selection.
        if (!ShouldUpdateHover(isLeftButtonPressed, _platformView?.SelectedText)) return;
        if (ReferenceEquals(_hoveredRun, run)) return;
        if (_hoveredRun is not null) _hoveredRun.TextDecorations = WinTextDecorations.None;
        _hoveredRun = run;
        // Decorate the run, since WinUI changes the Hyperlink's own decorations
        // during pointer-over and pressed states.
        if (run is not null) run.TextDecorations = WinTextDecorations.Underline;
    }

    private static bool Contains(WinRect bounds, WinPoint point) =>
        point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
}
