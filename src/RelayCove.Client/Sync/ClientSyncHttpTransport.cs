using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed class ClientSyncHttpTransport
{
    private const int MaximumTransientRetries = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumBackoffDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri conversationListUri;
    private readonly Uri serverBaseUri;
    private readonly HttpClient httpClient;
    private readonly IClientAuthenticationSession authenticationSession;
    private readonly ILogger logger;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Func<double> nextJitter;
    private readonly TimeProvider timeProvider;

    public ClientSyncHttpTransport(
        AccountScopeIdentity identity,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<double>? nextJitter = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.authenticationSession = authenticationSession ??
            throw new ArgumentNullException(nameof(authenticationSession));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.delayAsync = delayAsync ?? Task.Delay;
        this.nextJitter = nextJitter ?? Random.Shared.NextDouble;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        serverBaseUri = identity.CanonicalServerBaseUri;
        conversationListUri = new Uri(serverBaseUri, "api/conversations");
    }

    public Task<ClientSyncHttpResult<ConversationListResponse>> GetConversationSnapshotAsync(
        CancellationToken cancellationToken) =>
        SendAsync<ConversationListResponse>(
            conversationListUri,
            operation: "ConversationSnapshot",
            recognizeCursorInvalid: false,
            cancellationToken);

    public Task<ClientSyncHttpResult<SyncResponse>> GetSyncPageAsync(
        long cursor,
        long? snapshotUpperBound,
        CancellationToken cancellationToken)
    {
        var relativeUri = snapshotUpperBound.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"api/sync?cursor={cursor}&snapshotUpperBound={snapshotUpperBound.Value}&limit=100")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"api/sync?cursor={cursor}&limit=100");
        return SendAsync<SyncResponse>(
            new Uri(serverBaseUri, relativeUri),
            operation: "SyncPage",
            recognizeCursorInvalid: true,
            cancellationToken);
    }

    private async Task<ClientSyncHttpResult<T>> SendAsync<T>(
        Uri requestUri,
        string operation,
        bool recognizeCursorInvalid,
        CancellationToken cancellationToken)
        where T : class
    {
        var transientRetries = 0;
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
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    return ClientSyncHttpResult<T>.Failure(
                        ClientSyncHttpStatus.AuthenticationRequired);
                }

                if (!AuthenticationHeaderValue.TryParse(
                        $"Bearer {accessToken}",
                        out var authorization))
                {
                    return ClientSyncHttpResult<T>.Failure(
                        ClientSyncHttpStatus.AuthenticationRequired);
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
                    return await ReadSuccessAsync<T>(response, cancellationToken).ConfigureAwait(false);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (refreshAttempted)
                    {
                        return ClientSyncHttpResult<T>.Failure(
                            ClientSyncHttpStatus.AuthenticationRequired);
                    }

                    refreshAttempted = true;
                    var refreshed = await authenticationSession
                        .TryRefreshAccessTokenAsync(accessToken, cancellationToken)
                        .ConfigureAwait(false);
                    if (!refreshed)
                    {
                        return ClientSyncHttpResult<T>.Failure(
                            ClientSyncHttpStatus.AuthenticationRequired);
                    }

                    continue;
                }

                if (recognizeCursorInvalid && response.StatusCode == HttpStatusCode.Conflict)
                {
                    var errorCode = await ReadErrorCodeAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                    return ClientSyncHttpResult<T>.Failure(
                        string.Equals(
                            errorCode,
                            ApiErrorCodes.SyncCursorInvalid,
                            StringComparison.Ordinal)
                            ? ClientSyncHttpStatus.CursorInvalid
                            : ClientSyncHttpStatus.ProtocolError);
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return ClientSyncHttpResult<T>.Failure(ClientSyncHttpStatus.ProtocolError);
                }

                if (!IsTransient(response.StatusCode))
                {
                    return ClientSyncHttpResult<T>.Failure(ClientSyncHttpStatus.RemoteFailure);
                }

                if (transientRetries >= MaximumTransientRetries)
                {
                    return ClientSyncHttpResult<T>.Failure(ClientSyncHttpStatus.TransientFailure);
                }

                transientRetries++;
                var retryAfter = GetRetryAfter(response);
                await DelayBeforeRetryAsync(
                        operation,
                        transientRetries,
                        retryAfter,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (transientRetries >= MaximumTransientRetries)
                {
                    return ClientSyncHttpResult<T>.Failure(ClientSyncHttpStatus.TransientFailure);
                }

                transientRetries++;
                await DelayBeforeRetryAsync(
                        operation,
                        transientRetries,
                        retryAfter: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                if (transientRetries >= MaximumTransientRetries)
                {
                    return ClientSyncHttpResult<T>.Failure(ClientSyncHttpStatus.TransientFailure);
                }

                transientRetries++;
                logger.LogWarning(
                    "Sync HTTP request failed transiently; operation={Operation}; retry={Retry}; errorType={ErrorType}.",
                    operation,
                    transientRetries,
                    exception.GetType().Name);
                await DelayBeforeRetryAsync(
                        operation,
                        transientRetries,
                        retryAfter: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<ClientSyncHttpResult<T>> ReadSuccessAsync<T>(
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
                ? ClientSyncHttpResult<T>.Failure(ClientSyncHttpStatus.ProtocolError)
                : ClientSyncHttpResult<T>.Success(value);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return ClientSyncHttpResult<T>.Failure(ClientSyncHttpStatus.ProtocolError);
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

    private async Task DelayBeforeRetryAsync(
        string operation,
        int retryNumber,
        TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        var delay = CalculateRetryDelay(retryNumber, retryAfter);
        logger.LogInformation(
            "Sync HTTP request scheduled a transient retry; operation={Operation}; retry={Retry}; delayMs={DelayMs}.",
            operation,
            retryNumber,
            (long)delay.TotalMilliseconds);
        await delayAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    private TimeSpan CalculateRetryDelay(int retryNumber, TimeSpan? retryAfter)
    {
        var exponentialMilliseconds = Math.Min(
            MaximumBackoffDelay.TotalMilliseconds,
            InitialRetryDelay.TotalMilliseconds * Math.Pow(2, retryNumber - 1));
        var jitter = Math.Clamp(nextJitter(), 0, 1) * 0.25;
        var backoff = TimeSpan.FromMilliseconds(exponentialMilliseconds * (1 + jitter));
        var selected = retryAfter.HasValue && retryAfter.Value > backoff
            ? retryAfter.Value
            : backoff;
        return selected > MaximumRetryDelay ? MaximumRetryDelay : selected;
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is not { } date)
        {
            return null;
        }

        var until = date - timeProvider.GetUtcNow();
        return until < TimeSpan.Zero ? TimeSpan.Zero : until;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
}
