namespace RelayCove.Client.Mentions;

internal sealed record ClientMentionTextEdit(string Text, int CaretIndex)
{
    public static ClientMentionTextEdit Empty { get; } = new(string.Empty, 0);

    public override string ToString() =>
        $"{nameof(ClientMentionTextEdit)} {{ Text = [REDACTED], " +
        $"CaretIndex = {CaretIndex} }}";
}
