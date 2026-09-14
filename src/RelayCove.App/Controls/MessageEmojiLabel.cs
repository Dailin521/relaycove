using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Controls;

public sealed class MessageEmojiLabel : Label
{
    public static readonly BindableProperty RunsProperty = BindableProperty.Create(
        nameof(Runs), typeof(IReadOnlyList<MessageTextRun>), typeof(MessageEmojiLabel));
    public static readonly BindableProperty RealmEmojisProperty = BindableProperty.Create(
        nameof(RealmEmojis), typeof(IReadOnlyDictionary<string, RealmEmoji>), typeof(MessageEmojiLabel));
    public static readonly BindableProperty HighlightQueryProperty = BindableProperty.Create(
        nameof(HighlightQuery), typeof(string), typeof(MessageEmojiLabel), string.Empty);
    public static readonly BindableProperty IsTextSelectionEnabledProperty = BindableProperty.Create(
        nameof(IsTextSelectionEnabled), typeof(bool), typeof(MessageEmojiLabel), true);
    public static readonly BindableProperty EmojiSizeProperty = BindableProperty.Create(
        nameof(EmojiSize), typeof(double), typeof(MessageEmojiLabel), 19d);

    public IReadOnlyDictionary<string, RealmEmoji>? RealmEmojis
    {
        get => (IReadOnlyDictionary<string, RealmEmoji>?)GetValue(RealmEmojisProperty);
        set => SetValue(RealmEmojisProperty, value);
    }
    public string HighlightQuery
    {
        get => (string)GetValue(HighlightQueryProperty);
        set => SetValue(HighlightQueryProperty, value);
    }
    public bool IsTextSelectionEnabled
    {
        get => (bool)GetValue(IsTextSelectionEnabledProperty);
        set => SetValue(IsTextSelectionEnabledProperty, value);
    }
    public double EmojiSize
    {
        get => (double)GetValue(EmojiSizeProperty);
        set => SetValue(EmojiSizeProperty, value);
    }

    public IReadOnlyList<MessageTextRun>? Runs
    {
        get => (IReadOnlyList<MessageTextRun>?)GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }
}
