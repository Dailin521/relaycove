using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Shared.Updates;

namespace RelayCove.Client.Updates;

internal sealed class ClientUpdateManifestHttpTransport : IClientUpdateManifestTransport
{
    private const long MaximumManifestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly ILogger logger;

    public ClientUpdateManifestHttpTransport(HttpClient httpClient, ILogger logger)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ClientUpdateManifestFetchOutcome> FetchAsync(
        Uri serverBaseUri,
        CancellationToken cancellationToken = default)
    {
        var manifestUri = new Uri(ClientUpdateServerUri.Canonicalize(serverBaseUri), "api/updates/manifest");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || !HasOriginalRequestUri(response, manifestUri))
            {
                return ClientUpdateManifestFetchOutcome.Failure(
                    IsTransient(response.StatusCode)
                        ? ClientUpdateFetchStatus.TransientFailure
                        : ClientUpdateFetchStatus.RemoteFailure);
            }

            if (response.Content.Headers.ContentLength is > MaximumManifestBytes ||
                !IsJsonContent(response.Content.Headers.ContentType))
            {
                return ClientUpdateManifestFetchOutcome.Failure(ClientUpdateFetchStatus.ProtocolError);
            }

            UpdateManifestDto? manifest;
            try
            {
                await response.Content.LoadIntoBufferAsync(MaximumManifestBytes, cancellationToken)
                    .ConfigureAwait(false);
                manifest = await response.Content.ReadFromJsonAsync<UpdateManifestDto>(
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
            {
                return ClientUpdateManifestFetchOutcome.Failure(ClientUpdateFetchStatus.ProtocolError);
            }

            return UpdateManifestValidator.TryValidate(manifest, out _)
                ? ClientUpdateManifestFetchOutcome.Success(manifest!)
                : ClientUpdateManifestFetchOutcome.Failure(ClientUpdateFetchStatus.ProtocolError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClientUpdateManifestFetchOutcome.Failure(ClientUpdateFetchStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            return ClientUpdateManifestFetchOutcome.Failure(ClientUpdateFetchStatus.TransientFailure);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            logger.LogWarning(
                "Update manifest request failed transiently; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientUpdateManifestFetchOutcome.Failure(ClientUpdateFetchStatus.TransientFailure);
        }
    }

    private static bool HasOriginalRequestUri(HttpResponseMessage response, Uri expectedRequestUri) =>
        response.RequestMessage?.RequestUri is { } effectiveRequestUri &&
        Uri.Compare(
            effectiveRequestUri,
            expectedRequestUri,
            UriComponents.AbsoluteUri,
            UriFormat.UriEscaped,
            StringComparison.Ordinal) == 0;

    private static bool IsJsonContent(MediaTypeHeaderValue? contentType) =>
        string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
