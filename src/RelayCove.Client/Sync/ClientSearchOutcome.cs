using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed record ClientSearchOutcome(
    ClientSearchStatus Status,
    IReadOnlyList<SearchResultDto> Results,
    bool HasMore,
    int? RetryAfterSeconds)
{
    public static ClientSearchOutcome Failure(
        ClientSearchStatus status,
        int? retryAfterSeconds = null) =>
        new(status, Array.Empty<SearchResultDto>(), HasMore: false, retryAfterSeconds);

    public override string ToString() =>
        $"{nameof(ClientSearchOutcome)} {{ Status = {Status}, Results = [REDACTED], " +
        $"HasMore = {HasMore}, RetryAfterSeconds = {RetryAfterSeconds} }}";
}
