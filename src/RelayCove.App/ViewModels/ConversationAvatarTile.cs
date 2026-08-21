namespace RelayCove.App.ViewModels;

public sealed record ConversationAvatarTile(
    long UserId,
    string Name,
    string? AvatarUrl,
    bool IsBot,
    int Row,
    int Column)
{
    public string Initial => AvatarInitials.Create(Name, IsBot);

    public Brush ToneBrush => new SolidColorBrush(Color.FromArgb(TonePalette[StableToneIndex(UserId)]));

    private static readonly string[] TonePalette =
    [
        "#2F9BFF", "#8A63D2", "#2B9A78", "#E28A39", "#D65B78", "#367FC4"
    ];

    private static int StableToneIndex(long value) => (int)(Math.Abs(value) % TonePalette.Length);
}
