using System.ComponentModel;
using System.Runtime.CompilerServices;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record EmojiChoice(
    string Emoji,
    string Label,
    string EmojiName,
    string EmojiCode,
    string CategoryKey,
    string ReactionType = "unicode_emoji") : INotifyPropertyChanged
{
    private bool _isComposerSelected;
    private bool _isPointerOver;
    private bool _isReactionSelected;

    public EmojiReactionIdentity Identity => new(EmojiName, EmojiCode, ReactionType);
    public string AccessibleLabel => $"{Label} {Emoji}";

    public bool IsComposerSelected
    {
        get => _isComposerSelected;
        set => SetSelection(ref _isComposerSelected, value);
    }

    public bool IsReactionSelected
    {
        get => _isReactionSelected;
        set => SetSelection(ref _isReactionSelected, value);
    }

    public bool IsPointerOver
    {
        get => _isPointerOver;
        set => SetSelection(ref _isPointerOver, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetSelection(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
