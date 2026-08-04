using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Auth;

namespace RelayCove.Client.Admin;

internal sealed class ClientAdminTransport
{
    private const long MaximumPayloadBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly ClientAuthenticationSession session;
    private readonly ILogger logger;

    public ClientAdminTransport(
        HttpClient httpClient,
        ClientAuthenticationSession session,
        ILogger logger)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<ClientAdminRequestResult<T>> GetAsync<T>(
        string relativeUri,
        CancellationToken cancellationToken) =>
        SendAsync<object, T>(HttpMethod.Get, relativeUri, null, cancellationToken);

    public Task<ClientAdminRequestResult<TResponse>> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string relativeUri,
        TRequest? content,
        CancellationToken cancellationToken)
        where TRequest : class =>
        SendCoreAsync<TResponse>(method, relativeUri, content, cancellationToken);

    public Task<ClientAdminRequestResult<bool>> SendNoContentAsync<TRequest>(
        HttpMethod method,
        string relativeUri,
        TRequest? content,
        CancellationToken cancellationToken)
        where TRequest : class =>
        SendCoreAsync<bool>(method, relativeUri, content, cancellationToken);

    private async Task<ClientAdminRequestResult<T>> SendCoreAsync<T>(
        HttpMethod method,
        string relativeUri,
        object? content,
        CancellationToken cancellationToken)
    {
        var refreshed = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? accessToken = null;
            try
            {
                accessToken = await session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(accessToken) || !AuthenticationHeaderValue.TryParse(
                        $"Bearer {accessToken}", out var authorization))
                {
                    return ClientAdminRequestResult<T>.Failure(
                        ClientAdminRequestStatus.AuthenticationRequired);
                }

                using var request = new HttpRequestMessage(method, new Uri(session.ServerBaseUri, relativeUri));
                request.Headers.Authorization = authorization;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (content is not null)
                {
                    request.Content = JsonContent.Create(content, options: JsonOptions);
                }

                using var response = await httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    if (typeof(T) == typeof(bool) || response.Content.Headers.ContentLength == 0)
                    {
                        return ClientAdminRequestResult<T>.Success((T)(object)true);
                    }

                    await response.Content.LoadIntoBufferAsync(MaximumPayloadBytes, cancellationToken)
                        .ConfigureAwait(false);
                    var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    return value is null
                        ? ClientAdminRequestResult<T>.Failure(ClientAdminRequestStatus.ProtocolError)
                        : ClientAdminRequestResult<T>.Success(value);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (refreshed || !await session.TryRefreshAccessTokenAsync(accessToken, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return ClientAdminRequestResult<T>.Failure(
                            ClientAdminRequestStatus.AuthenticationRequired);
                    }

                    refreshed = true;
                    continue;
                }

                return ClientAdminRequestResult<T>.Failure(response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => ClientAdminRequestStatus.AccessDenied,
                    HttpStatusCode.BadRequest => ClientAdminRequestStatus.ValidationFailed,
                    HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or
                        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                        ClientAdminRequestStatus.TransientFailure,
                    _ => ClientAdminRequestStatus.RemoteFailure,
                });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ClientAdminRequestResult<T>.Failure(ClientAdminRequestStatus.TransientFailure);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or
                JsonException or NotSupportedException)
            {
                logger.LogWarning("Admin HTTP request failed; method={Method}; errorType={ErrorType}.",
                    method.Method, exception.GetType().Name);
                return ClientAdminRequestResult<T>.Failure(exception is JsonException or NotSupportedException
                    ? ClientAdminRequestStatus.ProtocolError
                    : ClientAdminRequestStatus.TransientFailure);
            }
        }
    }
}
