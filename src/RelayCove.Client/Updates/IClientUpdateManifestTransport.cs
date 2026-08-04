namespace RelayCove.Client.Updates;

internal interface IClientUpdateManifestTransport
{
    Task<ClientUpdateManifestFetchOutcome> FetchAsync(
        Uri serverBaseUri,
        CancellationToken cancellationToken = default);
}
