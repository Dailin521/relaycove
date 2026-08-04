namespace RelayCove.Shared.Messages;

public sealed record SearchResponse(
    IReadOnlyList<SearchResultDto> Results,
    bool HasMore)
{
    public override string ToString() =>
        $"{nameof(SearchResponse)} {{ Results = [REDACTED], HasMore = {HasMore} }}";
}
