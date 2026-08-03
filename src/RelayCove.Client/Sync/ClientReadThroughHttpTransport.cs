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

internal sealed class ClientReadThroughHttpTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri serverBaseUri;
    private readonly HttpClient httpClient;
    private readonly IClientAuthenticationSession authenticationSession;
    private readonly ILogger logger;

    public ClientReadThroughHttpTransport(
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

    public async Task<ClientReadThroughHttpResult> MarkReadAsync(
        Guid conversationId,
        long messageId,
        CancellationToken cancellationToken)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("A conversation ID cannot be empty.", nameof(conversationId));
        }

        if (messageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(messageId));
        }

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
                    return ClientReadThroughHttpResult.Failure(
                        ClientReadThroughHttpStatus.AuthenticationRequired);
                }

                var relativeUri = $"api/conversations/{conversationId:D}/read";
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(serverBaseUri, relativeUri));
                request.Headers.Authorization = authorization;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = JsonContent.Create(
                    new MarkConversationReadRequest(messageId),
                    options: JsonOptions);
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
                        return ClientReadThroughHttpResult.Failure(
                            ClientReadThroughHttpStatus.AuthenticationRequired);
                    }

                    refreshAttempted = true;
                    var refreshed = await authenticationSession
                        .TryRefreshAccessTokenAsync(accessToken, cancellationToken)
                        .ConfigureAwait(false);
                    if (!refreshed)
                    {
                        return ClientReadThroughHttpResult.Failure(
                            ClientReadThroughHttpStatus.AuthenticationRequired);
                    }

                    continue;
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return ClientReadThroughHttpResult.Failure(
                        ClientReadThroughHttpStatus.ProtocolError);
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var errorCode = await ReadErrorCodeAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                    return ClientReadThroughHttpResult.Failure(
                        string.Equals(
                            errorCode,
                            ApiErrorCodes.ConversationAccessRevoked,
                            StringComparison.Ordinal)
                            ? ClientReadThroughHttpStatus.AccessRevoked
                            : ClientReadThroughHttpStatus.AccessDenied);
                }

                return ClientReadThroughHttpResult.Failure(
                    IsTransient(response.StatusCode)
                        ? ClientReadThroughHttpStatus.TransientFailure
                        : ClientReadThroughHttpStatus.RemoteFailure);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ClientReadThroughHttpResult.Failure(
                    ClientReadThroughHttpStatus.TransientFailure);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                logger.LogWarning(
                    "Read-through HTTP request failed transiently; errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientReadThroughHttpResult.Failure(
                    ClientReadThroughHttpStatus.TransientFailure);
            }
        }
    }

    private static async Task<ClientReadThroughHttpResult> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await response.Content
                .ReadFromJsonAsync<ConversationReadReceipt>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return receipt is null
                ? ClientReadThroughHttpResult.Failure(ClientReadThroughHttpStatus.ProtocolError)
                : ClientReadThroughHttpResult.Success(receipt);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return ClientReadThroughHttpResult.Failure(
                ClientReadThroughHttpStatus.ProtocolError);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

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
}
