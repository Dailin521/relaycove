using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Shared.Auth;

namespace RelayCove.Client.Auth;

public sealed class ClientAuthenticationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly ILogger<ClientAuthenticationClient> logger;
    private readonly TimeProvider timeProvider;
    private readonly Uri loginUri;

    public ClientAuthenticationClient(
        Uri serverBaseUri,
        HttpClient httpClient,
        ILogger<ClientAuthenticationClient> logger,
        TimeProvider? timeProvider = null)
    {
        ServerBaseUri = ClientAuthenticationUri.CanonicalizeServerBaseUri(serverBaseUri);
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        loginUri = new Uri(ServerBaseUri, "api/auth/login");
    }

    public Uri ServerBaseUri { get; }

    public override string ToString() =>
        $"{nameof(ClientAuthenticationClient)} {{ ServerBaseUri = [REDACTED] }}";

    public async Task<ClientLoginOutcome> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, loginUri)
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return await CreateAuthenticatedOutcomeAsync(response, cancellationToken)
                    .ConfigureAwait(false);
            }

            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest => ClientLoginOutcome.Failure(
                    ClientLoginStatus.ValidationFailed),
                HttpStatusCode.Unauthorized => ClientLoginOutcome.Failure(
                    ClientLoginStatus.AuthenticationFailed),
                HttpStatusCode.TooManyRequests => ClientLoginOutcome.Failure(
                    ClientLoginStatus.RateLimited,
                    GetRetryAfter(response.Headers.RetryAfter)),
                HttpStatusCode.RequestTimeout => ClientLoginOutcome.Failure(
                    ClientLoginStatus.ServiceUnavailable),
                >= HttpStatusCode.InternalServerError => ClientLoginOutcome.Failure(
                    ClientLoginStatus.ServiceUnavailable),
                _ => ClientLoginOutcome.Failure(ClientLoginStatus.RemoteFailure),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ClientLoginOutcome.Failure(ClientLoginStatus.ServiceUnavailable);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            logger.LogWarning(
                "Authentication HTTP request failed; operation={Operation}; errorType={ErrorType}.",
                "Login",
                exception.GetType().Name);
            return ClientLoginOutcome.Failure(ClientLoginStatus.ServiceUnavailable);
        }
    }

    private async Task<ClientLoginOutcome> CreateAuthenticatedOutcomeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        LoginResponse? loginResponse;
        try
        {
            loginResponse = await response.Content
                .ReadFromJsonAsync<LoginResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return ClientLoginOutcome.Failure(ClientLoginStatus.ProtocolError);
        }

        if (!ClientAuthenticationResponseValidator.IsValid(
                loginResponse,
                timeProvider.GetUtcNow()))
        {
            return ClientLoginOutcome.Failure(ClientLoginStatus.ProtocolError);
        }

        return ClientLoginOutcome.Authenticated(
            new ClientAuthenticationSession(
                ServerBaseUri,
                httpClient,
                logger,
                loginResponse!,
                timeProvider));
    }

    private TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is not { } date)
        {
            return null;
        }

        var delay = date - timeProvider.GetUtcNow();
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}
