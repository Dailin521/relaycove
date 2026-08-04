using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed record ClientSearchHttpResult(
    ClientSearchHttpStatus Status,
    SearchResponse? Response,
    int? RetryAfterSeconds)
{
    public static ClientSearchHttpResult Success(SearchResponse response) =>
        new(ClientSearchHttpStatus.Success, response, RetryAfterSeconds: null);

    public static ClientSearchHttpResult Failure(
        ClientSearchHttpStatus status,
        int? retryAfterSeconds = null) =>
        new(status, Response: null, retryAfterSeconds);

    public override string ToString() =>
        $"{nameof(ClientSearchHttpResult)} {{ Status = {Status}, Response = [REDACTED], " +
        $"RetryAfterSeconds = {RetryAfterSeconds} }}";
}
