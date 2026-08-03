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

internal sealed class ClientMessageSendHttpTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri sendUri;
    private readonly Guid userId;
    private readonly HttpClient httpClient;
    private readonly IClientAuthenticationSession authenticationSession;
    private readonly ILogger logger;

    public ClientMessageSendHttpTransport(
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
        sendUri = new Uri(identity.CanonicalServerBaseUri, "api/messages");
        userId = identity.UserId;
    }

    public async Task<ClientMessageSendHttpResult> SendAsync(
        SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var refreshAttempted = false;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ClientMessageSendHttpResult.Failure(ClientMessageSendHttpStatus.Canceled);
            }

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
                    return ClientMessageSendHttpResult.Failure(
                        ClientMessageSendHttpStatus.AuthenticationRequired);
                }

                using var message = new HttpRequestMessage(HttpMethod.Post, sendUri)
                {
                    Content = JsonContent.Create(request, options: JsonOptions),
                };
                message.Headers.Authorization = authorization;
                message.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await httpClient.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
                {
                    return await ReadSuccessAsync(
                            response,
                            request,
                            userId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (response.IsSuccessStatusCode)
                {
                    return ClientMessageSendHttpResult.Failure(
                        ClientMessageSendHttpStatus.ProtocolError);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (refreshAttempted)
                    {
                        return ClientMessageSendHttpResult.Failure(
                            ClientMessageSendHttpStatus.AuthenticationRequired);
                    }

                    refreshAttempted = true;
                    var refreshed = await authenticationSession
                        .TryRefreshAccessTokenAsync(accessToken, cancellationToken)
                        .ConfigureAwait(false);
                    if (!refreshed)
                    {
                        return ClientMessageSendHttpResult.Failure(
                            ClientMessageSendHttpStatus.AuthenticationRequired);
                    }

                    continue;
                }

                var errorCode = await ReadErrorCodeAsync(response, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ClientMessageSendHttpResult.Failure(
                        string.Equals(
                            errorCode,
                            ApiErrorCodes.ConversationAccessRevoked,
                            StringComparison.Ordinal)
                            ? ClientMessageSendHttpStatus.AccessRevoked
                            : ClientMessageSendHttpStatus.AccessDenied);
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return ClientMessageSendHttpResult.Failure(
                        ClientMessageSendHttpStatus.ValidationFailed);
                }

                if (response.StatusCode == HttpStatusCode.Conflict &&
                    string.Equals(
                        errorCode,
                        ApiErrorCodes.IdempotencyKeyReuse,
                        StringComparison.Ordinal))
                {
                    return ClientMessageSendHttpResult.Failure(
                        ClientMessageSendHttpStatus.IdempotencyConflict);
                }

                return ClientMessageSendHttpResult.Failure(
                    IsTransient(response.StatusCode)
                        ? ClientMessageSendHttpStatus.TransientFailure
                        : ClientMessageSendHttpStatus.RemoteFailure);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ClientMessageSendHttpResult.Failure(ClientMessageSendHttpStatus.Canceled);
            }
            catch (OperationCanceledException)
            {
                return ClientMessageSendHttpResult.Failure(
                    ClientMessageSendHttpStatus.TransientFailure);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                logger.LogWarning(
                    "Message send HTTP request failed transiently; errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientMessageSendHttpResult.Failure(
                    ClientMessageSendHttpStatus.TransientFailure);
            }
        }
    }

    private static async Task<ClientMessageSendHttpResult> ReadSuccessAsync(
        HttpResponseMessage response,
        SendMessageRequest request,
        Guid expectedSenderId,
        CancellationToken cancellationToken)
    {
        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<MessageDto>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return IsValidResponse(value, request, expectedSenderId)
                ? ClientMessageSendHttpResult.Success(value!)
                : ClientMessageSendHttpResult.Failure(
                    ClientMessageSendHttpStatus.ProtocolError);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return ClientMessageSendHttpResult.Failure(
                ClientMessageSendHttpStatus.ProtocolError);
        }
    }

    private static bool IsValidResponse(
        MessageDto? value,
        SendMessageRequest request,
        Guid expectedSenderId) =>
        value is not null &&
        value.Id > 0 &&
        value.ClientMessageId == request.ClientMessageId &&
        value.ConversationId == request.ConversationId &&
        value.SenderId == expectedSenderId &&
        value.SenderDisplayName is not null &&
        value.Type == request.Type &&
        string.Equals(value.Content, request.Content, StringComparison.Ordinal) &&
        value.ReplyToMessageId == request.ReplyToMessageId &&
        value.Attachments is not null &&
        value.Attachments.Count == 0 &&
        value.MentionUserIds is not null &&
        value.MentionUserIds.SequenceEqual(request.MentionUserIds) &&
        value.CreatedAt != default;

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

    private static void ValidateRequest(SendMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ClientMessageId == Guid.Empty ||
            request.ConversationId == Guid.Empty ||
            request.Type != MessageType.Text ||
            !ClientTextMessageContentValidator.IsValid(request.Content) ||
            request.ReplyToMessageId is not null ||
            request.AttachmentIds is null ||
            request.AttachmentIds.Count != 0 ||
            request.MentionUserIds is null ||
            request.MentionUserIds.Count != 0)
        {
            throw new ArgumentException(
                "The Text message request is invalid or unsupported.",
                nameof(request));
        }
    }
}
