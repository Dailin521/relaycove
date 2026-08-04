using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Search;
using RelayCove.Client.Storage;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed class ClientSearchHttpTransport
{
    // System.Text.Json may escape supplementary Unicode scalars as surrogate pairs.
    // 512 KiB admits 50 maximum-size result rows even in that wire representation.
    private const long MaximumSuccessPayloadBytes = 512 * 1024;
    private const long MaximumErrorPayloadBytes = 16 * 1024;
    private const int MaximumRetryAfterSeconds = 3_600;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri serverBaseUri;
    private readonly HttpClient httpClient;
    private readonly IClientAuthenticationSession authenticationSession;
    private readonly ILogger logger;

    public ClientSearchHttpTransport(
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

    public async Task<ClientSearchHttpResult> SearchAsync(
        string keyword,
        Guid? conversationId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!ClientSearchPolicy.IsValidKeyword(keyword) ||
            limit is < 1 or > ClientSearchCoordinator.MaximumLimit ||
            conversationId == Guid.Empty)
        {
            throw new ArgumentException("The search request is invalid.");
        }

        var relativeUri = conversationId.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"api/search?keyword={Uri.EscapeDataString(keyword)}&conversationId={conversationId.Value:D}&limit={limit}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"api/search?keyword={Uri.EscapeDataString(keyword)}&limit={limit}");
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
                    return ClientSearchHttpResult.Failure(
                        ClientSearchHttpStatus.AuthenticationRequired);
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
                    return await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (refreshAttempted)
                    {
                        return ClientSearchHttpResult.Failure(
                            ClientSearchHttpStatus.AuthenticationRequired);
                    }

                    refreshAttempted = true;
                    if (!await authenticationSession
                            .TryRefreshAccessTokenAsync(accessToken, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return ClientSearchHttpResult.Failure(
                            ClientSearchHttpStatus.AuthenticationRequired);
                    }

                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var errorCode = await ReadErrorCodeAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                    return ClientSearchHttpResult.Failure(
                        conversationId.HasValue && string.Equals(
                            errorCode,
                            ApiErrorCodes.ConversationAccessRevoked,
                            StringComparison.Ordinal)
                            ? ClientSearchHttpStatus.AccessRevoked
                            : ClientSearchHttpStatus.AccessDenied);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ClientSearchHttpResult.Failure(
                        ClientSearchHttpStatus.RateLimited,
                        GetBoundedRetryAfterSeconds(response));
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return ClientSearchHttpResult.Failure(ClientSearchHttpStatus.ProtocolError);
                }

                return ClientSearchHttpResult.Failure(
                    IsTransient(response.StatusCode)
                        ? ClientSearchHttpStatus.TransientFailure
                        : ClientSearchHttpStatus.RemoteFailure);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ClientSearchHttpResult.Failure(ClientSearchHttpStatus.Timeout);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                logger.LogWarning(
                    "Search HTTP request failed transiently; errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientSearchHttpResult.Failure(ClientSearchHttpStatus.TransientFailure);
            }
        }
    }

    private static async Task<ClientSearchHttpResult> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await response.Content
                .LoadIntoBufferAsync(MaximumSuccessPayloadBytes, cancellationToken)
                .ConfigureAwait(false);
            var value = await response.Content
                .ReadFromJsonAsync<SearchResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return value is null
                ? ClientSearchHttpResult.Failure(ClientSearchHttpStatus.ProtocolError)
                : ClientSearchHttpResult.Success(value);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return ClientSearchHttpResult.Failure(ClientSearchHttpStatus.ProtocolError);
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

    private static int? GetBoundedRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ??
            (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is null || delay <= TimeSpan.Zero || delay > TimeSpan.FromSeconds(MaximumRetryAfterSeconds))
        {
            return null;
        }

        var seconds = Math.Ceiling(delay.Value.TotalSeconds);
        return seconds is >= 1 and <= MaximumRetryAfterSeconds ? (int)seconds : null;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
}
