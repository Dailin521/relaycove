using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Mentions;
using RelayCove.Client.Storage;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed class ClientMentionCandidateHttpTransport
{
    private const long MaximumSuccessPayloadBytes = 64 * 1024;
    private const long MaximumErrorPayloadBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri serverBaseUri;
    private readonly HttpClient httpClient;
    private readonly IClientAuthenticationSession authenticationSession;
    private readonly ILogger logger;

    public ClientMentionCandidateHttpTransport(
        AccountScopeIdentity identity,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(identity);
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.authenticationSession = authenticationSession ??
            throw new ArgumentNullException(nameof(authenticationSession));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        serverBaseUri = identity.CanonicalServerBaseUri;
    }

    public async Task<ClientMentionCandidateHttpResult> SearchAsync(
        Guid conversationId,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (conversationId == Guid.Empty ||
            !ClientMentionPolicy.IsValidQuery(query) ||
            limit is < 1 or > 50)
        {
            throw new ArgumentException("The mention candidate query is invalid.");
        }

        var relativeUri = string.Create(
            CultureInfo.InvariantCulture,
            $"api/conversations/{conversationId:D}/mention-candidates?query={Uri.EscapeDataString(query)}&limit={limit}");
        var requestUri = new Uri(serverBaseUri, relativeUri);
        var refreshAttempted = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? accessToken = null;
            try
            {
                accessToken = await authenticationSession
                    .GetAccessTokenAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(accessToken) ||
                    !AuthenticationHeaderValue.TryParse(
                        $"Bearer {accessToken}",
                        out var authorization))
                {
                    return ClientMentionCandidateHttpResult.Failure(
                        ClientMentionCandidateHttpStatus.AuthenticationRequired);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Authorization = authorization;
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return await ReadSuccessAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (refreshAttempted)
                    {
                        return ClientMentionCandidateHttpResult.Failure(
                            ClientMentionCandidateHttpStatus.AuthenticationRequired);
                    }

                    refreshAttempted = true;
                    var refreshed = await authenticationSession
                        .TryRefreshAccessTokenAsync(accessToken, cancellationToken)
                        .ConfigureAwait(false);
                    if (!refreshed)
                    {
                        return ClientMentionCandidateHttpResult.Failure(
                            ClientMentionCandidateHttpStatus.AuthenticationRequired);
                    }

                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var errorCode = await ReadErrorCodeAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                    return ClientMentionCandidateHttpResult.Failure(
                        string.Equals(
                            errorCode,
                            ApiErrorCodes.ConversationAccessRevoked,
                            StringComparison.Ordinal)
                            ? ClientMentionCandidateHttpStatus.AccessRevoked
                            : ClientMentionCandidateHttpStatus.AccessDenied);
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return ClientMentionCandidateHttpResult.Failure(
                        ClientMentionCandidateHttpStatus.ProtocolError);
                }

                return ClientMentionCandidateHttpResult.Failure(
                    IsTransient(response.StatusCode)
                        ? ClientMentionCandidateHttpStatus.TransientFailure
                        : ClientMentionCandidateHttpStatus.RemoteFailure);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ClientMentionCandidateHttpResult.Failure(
                    ClientMentionCandidateHttpStatus.TransientFailure);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                logger.LogWarning(
                    "Mention candidate HTTP request failed transiently; errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientMentionCandidateHttpResult.Failure(
                    ClientMentionCandidateHttpStatus.TransientFailure);
            }
        }
    }

    private static async Task<ClientMentionCandidateHttpResult> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await response.Content
                .LoadIntoBufferAsync(MaximumSuccessPayloadBytes, cancellationToken)
                .ConfigureAwait(false);
            var value = await response.Content
                .ReadFromJsonAsync<MentionCandidateListResponse>(
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            return value is null
                ? ClientMentionCandidateHttpResult.Failure(
                    ClientMentionCandidateHttpStatus.ProtocolError)
                : ClientMentionCandidateHttpResult.Success(value);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return ClientMentionCandidateHttpResult.Failure(
                ClientMentionCandidateHttpStatus.ProtocolError);
        }
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await response.Content
                .LoadIntoBufferAsync(MaximumErrorPayloadBytes, cancellationToken)
                .ConfigureAwait(false);
            var error = await response.Content
                .ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return error?.Code;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return null;
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
}
