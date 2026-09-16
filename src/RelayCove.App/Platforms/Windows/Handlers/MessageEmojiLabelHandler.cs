using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using RelayCove.App.Controls;
using RelayCove.App.ViewModels;
using RelayCove.Core;
using NativeRun = Microsoft.UI.Xaml.Documents.Run;

namespace RelayCove.App.Platforms.Windows.Handlers;

public sealed class MessageEmojiLabelHandler : ViewHandler<MessageEmojiLabel, RichTextBlock>
{
    public static readonly IPropertyMapper<MessageEmojiLabel, MessageEmojiLabelHandler> Mapper =
        new PropertyMapper<MessageEmojiLabel, MessageEmojiLabelHandler>(ViewMapper)
        {
            [nameof(MessageEmojiLabel.Runs)] = MapRuns,
            [nameof(MessageEmojiLabel.RealmEmojis)] = MapRuns,
            [nameof(MessageEmojiLabel.HighlightQuery)] = MapRuns,
            [nameof(MessageEmojiLabel.EmojiSize)] = MapRuns,
            [nameof(MessageEmojiLabel.IsTextSelectionEnabled)] = MapRuns,
            [nameof(Label.Text)] = MapRuns,
            [nameof(Label.FontSize)] = MapRuns,
            [nameof(Label.TextColor)] = MapRuns,
            [nameof(Label.FontFamily)] = MapRuns,
            [nameof(Label.LineHeight)] = MapRuns,
            [nameof(Label.MaxLines)] = MapRuns,
            [nameof(Label.LineBreakMode)] = MapRuns
        };
    private readonly List<RealmMediaImageView> _images = [];

    public MessageEmojiLabelHandler() : base(Mapper) { }

    protected override RichTextBlock CreatePlatformView() => new()
    {
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap,
        IsTabStop = false,
        ContextFlyout = null
    };

    protected override void ConnectHandler(RichTextBlock platformView)
    {
        base.ConnectHandler(platformView);
        platformView.ContextMenuOpening += OnContextMenuOpening;
    }

    protected override void DisconnectHandler(RichTextBlock platformView)
    {
        platformView.ContextMenuOpening -= OnContextMenuOpening;
        ClearImages();
        base.DisconnectHandler(platformView);
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs args) => args.Handled = true;

    private void ClearImages()
    {
        PlatformView.Blocks.Clear();
        foreach (var image in _images) image.Handler?.DisconnectHandler();
        _images.Clear();
    }

    private static void MapAppearance(MessageEmojiLabelHandler handler, MessageEmojiLabel view)
    {
        handler.PlatformView.FontSize = view.FontSize;
        handler.PlatformView.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            (view.TextColor ?? Colors.Black).ToWindowsColor());
        if (!string.IsNullOrWhiteSpace(view.FontFamily))
            handler.PlatformView.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(view.FontFamily);
    }

    private static void MapRuns(MessageEmojiLabelHandler handler, MessageEmojiLabel view)
    {
        if (handler.MauiContext is null) return;
        handler.ClearImages();
        MapAppearance(handler, view);
        handler.PlatformView.IsTextSelectionEnabled = view.IsTextSelectionEnabled;
        handler.PlatformView.MaxLines = view.MaxLines < 0 ? 0 : view.MaxLines;
        handler.PlatformView.TextLineBounds = TextLineBounds.Full;
        handler.PlatformView.ClearValue(RichTextBlock.LineHeightProperty);
        if (view.MaxLines != 1 && view.LineHeight > 0)
            handler.PlatformView.LineHeight = view.FontSize * view.LineHeight;
        handler.PlatformView.TextWrapping = view.MaxLines == 1 ? TextWrapping.NoWrap : TextWrapping.Wrap;
        handler.PlatformView.TextTrimming = view.LineBreakMode == LineBreakMode.TailTruncation
            ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        var paragraph = new Paragraph();
        foreach (var part in view.Runs ?? EmojiShortcodeCatalog.CreateRuns(view.Text ?? string.Empty, view.RealmEmojis))
        {
            if (part.EmojiSourceUrl is { } url)
            {
                var image = new RealmMediaImageView
                {
                    SourceUrl = url, MediaKind = RealmMediaKind.Emoji,
                    WidthRequest = view.EmojiSize, HeightRequest = view.EmojiSize,
                    TranslationY = view.MaxLines == 1 ? EmojiInlineLayout.Descent(view.FontSize) : 0,
                    ShowFailureText = false, InputTransparent = true
                };
                SemanticProperties.SetDescription(image, part.Text);
                handler._images.Add(image);
                var native = image.ToPlatform(handler.MauiContext);
                native.Width = view.EmojiSize;
                native.Height = view.EmojiSize;
                if (view.MaxLines != 1)
                {
                    // InlineUIContainer puts the child's measured bottom on the
                    // baseline. Include the descent in layout rather than moving
                    // the pixels down and leaving empty space above the image.
                    native.Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, -EmojiInlineLayout.Descent(view.FontSize));
                }
                ToolTipService.SetToolTip(native, part.Text);
                paragraph.Inlines.Add(new InlineUIContainer { Child = native });
                continue;
            }
            foreach (var highlight in SearchHighlightLabel.Split(part.Text, view.HighlightQuery))
            {
                foreach (var linkPart in view.IsTextSelectionEnabled
                    ? MessageLinkParser.Split(highlight.Text) : [(highlight.Text, (Uri?)null)])
                {
                    var run = new NativeRun { Text = linkPart.Text };
                    if (highlight.IsMatch)
                    {
                        run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                        run.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
                    }
                    if (linkPart.Uri is null) paragraph.Inlines.Add(run);
                    else
                    {
                        var link = new Hyperlink { NavigateUri = linkPart.Uri, IsTabStop = false };
                        link.Inlines.Add(run);
                        paragraph.Inlines.Add(link);
                    }
                }
            }
        }
        handler.PlatformView.Blocks.Add(paragraph);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(handler.PlatformView, view.Text ?? string.Empty);
        view.InvalidateMeasure();
    }
}
