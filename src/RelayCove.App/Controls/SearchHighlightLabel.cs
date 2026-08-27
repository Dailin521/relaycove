namespace RelayCove.App.Controls;

public sealed class SearchHighlightLabel : Label
{
    public static readonly BindableProperty SourceTextProperty = BindableProperty.Create(
        nameof(SourceText),
        typeof(string),
        typeof(SearchHighlightLabel),
        string.Empty,
        propertyChanged: OnContentChanged);

    public static readonly BindableProperty HighlightQueryProperty = BindableProperty.Create(
        nameof(HighlightQuery),
        typeof(string),
        typeof(SearchHighlightLabel),
        string.Empty,
        propertyChanged: OnContentChanged);

    public string SourceText
    {
        get => (string?)GetValue(SourceTextProperty) ?? string.Empty;
        set => SetValue(SourceTextProperty, value);
    }

    public string HighlightQuery
    {
        get => (string?)GetValue(HighlightQueryProperty) ?? string.Empty;
        set => SetValue(HighlightQueryProperty, value);
    }

    internal static IReadOnlyList<(string Text, bool IsMatch)> Split(string source, string query)
    {
        if (source.Length == 0) return [(string.Empty, false)];
        var needle = query.Trim();
        if (needle.Length == 0) return [(source, false)];

        var parts = new List<(string Text, bool IsMatch)>();
        var offset = 0;
        while (offset < source.Length)
        {
            var match = source.IndexOf(needle, offset, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                parts.Add((source[offset..], false));
                break;
            }
            if (match > offset) parts.Add((source[offset..match], false));
            parts.Add((source.Substring(match, needle.Length), true));
            offset = match + needle.Length;
        }
        return parts;
    }

    private static void OnContentChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((SearchHighlightLabel)bindable).Rebuild();

    private void Rebuild()
    {
        var parts = Split(SourceText, HighlightQuery);
        if (!parts.Any(part => part.IsMatch))
        {
            FormattedText = null;
            Text = SourceText;
            return;
        }

        Text = null;
        var formatted = new FormattedString();
        var accent = Application.Current?.Resources.TryGetValue("AccentColor", out var value) == true && value is Color color
            ? color
            : Colors.DodgerBlue;
        foreach (var part in parts)
        {
            formatted.Spans.Add(new Span
            {
                Text = part.Text,
                FontAttributes = part.IsMatch ? FontAttributes.Bold : FontAttributes.None,
                TextColor = part.IsMatch ? accent : null
            });
        }
        FormattedText = formatted;
    }
}
