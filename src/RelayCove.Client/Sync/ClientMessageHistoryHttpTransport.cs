using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Storage;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed class ClientMessageHistoryHttpTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri serverBaseUri;
    private readonly HttpClient httpClient;
    private readonly IClientAuthenticationSession authenticationSession;
    private readonly ILogger logger;

    public ClientMessageHistoryHttpTransport(
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

    public Task<ClientMessageHistoryHttpResult<MessageHistoryResponse>> GetHistoryAsync(
        Guid conversationId,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateConversationId(conversationId);
        if (beforeMessageId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beforeMessageId));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var relativeUri = beforeMessageId.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"api/conversations/{conversationId:D}/messages?beforeMessageId={beforeMessageId.Value}&limit={limit}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"api/conversations/{conversationId:D}/messages?limit={limit}");
        return SendAsync<MessageHistoryResponse>(
            new Uri(serverBaseUri, relativeUri),
            cancellationToken);
    }

    public Task<ClientMessageHistoryHttpResult<MessageAroundResponse>> GetAroundAsync(
        Guid conversationId,
        long messageId,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        ValidateConversationId(conversationId);
        if (messageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(messageId));
        }

        if (before is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(before));
        }

        if (after is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(after));
        }

        var relativeUri = string.Create(
            CultureInfo.InvariantCulture,
            $"api/conversations/{conversationId:D}/messages/around/{messageId}?before={before}&after={after}");
        return SendAsync<MessageAroundResponse>(
            new Uri(serverBaseUri, relativeUri),
            cancellationToken);
    }

    private async Task<ClientMessageHistoryHttpResult<T>> SendAsync<T>(
        Uri requestUri,
        CancellationToken cancellationToken)
        where T : class
    {
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
                    return ClientMessageHistoryHttpResult<T>.Failure(
                        ClientMessageHistoryHttpStatus.AuthenticationRequired);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Authorization = authorization;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return await ReadSuccessAsync<T>(response, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (refreshAttempted)
                    {
                        return ClientMessageHistoryHttpResult<T>.Failure(
                            ClientMessageHistoryHttpStatus.AuthenticationRequired);
                    }

                    refreshAttempted = true;
                    var refreshed = await authenticationSession
                        .TryRefreshAccessTokenAsync(accessToken, cancellationToken)
                        .ConfigureAwait(false);
                    if (!refreshed)
                    {
                        return ClientMessageHistoryHttpResult<T>.Failure(
                            ClientMessageHistoryHttpStatus.AuthenticationRequired);
                    }

                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var errorCode = await ReadErrorCodeAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                    return ClientMessageHistoryHttpResult<T>.Failure(
                        string.Equals(
                            errorCode,
                            ApiErrorCodes.ConversationAccessRevoked,
                            StringComparison.Ordinal)
                            ? ClientMessageHistoryHttpStatus.AccessRevoked
                            : ClientMessageHistoryHttpStatus.AccessDenied);
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return ClientMessageHistoryHttpResult<T>.Failure(
                        ClientMessageHistoryHttpStatus.ProtocolError);
                }

                return ClientMessageHistoryHttpResult<T>.Failure(
                    IsTransient(response.StatusCode)
                        ? ClientMessageHistoryHttpStatus.TransientFailure
                        : ClientMessageHistoryHttpStatus.RemoteFailure);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ClientMessageHistoryHttpResult<T>.Failure(
                    ClientMessageHistoryHttpStatus.TransientFailure);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                logger.LogWarning(
                    "Message history HTTP request failed transiently; errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientMessageHistoryHttpResult<T>.Failure(
                    ClientMessageHistoryHttpStatus.TransientFailure);
            }
        }
    }

    private static async Task<ClientMessageHistoryHttpResult<T>> ReadSuccessAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return value is null
                ? ClientMessageHistoryHttpResult<T>.Failure(
                    ClientMessageHistoryHttpStatus.ProtocolError)
                : ClientMessageHistoryHttpResult<T>.Success(value);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return ClientMessageHistoryHttpResult<T>.Failure(
                ClientMessageHistoryHttpStatus.ProtocolError);
        }
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content
                .ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return error?.Code;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
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

    private static void ValidateConversationId(Guid conversationId)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A conversation ID cannot be empty.",
                nameof(conversationId));
        }
    }
}
