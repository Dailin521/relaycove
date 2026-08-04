using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Storage;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed class ClientAttachmentDownloadHttpTransport
{
    private const int CopyBufferSize = 80 * 1024;
    private const long MaximumErrorPayloadBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri serverBaseUri;
    private readonly HttpClient httpClient;
    private readonly IClientAuthenticationSession authenticationSession;
    private readonly ILogger logger;

    public ClientAttachmentDownloadHttpTransport(
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

    public async Task<ClientAttachmentDownloadHttpResult> DownloadAsync(
        AttachmentDto attachment,
        Stream staging,
        CancellationToken cancellationToken,
        Action<ClientAttachmentDownloadProgress>? progress = null)
    {
        if (!ClientAttachmentMetadataPolicy.IsValid(attachment))
        {
            throw new ArgumentException("Attachment metadata is invalid.", nameof(attachment));
        }

        ArgumentNullException.ThrowIfNull(staging);
        if (!staging.CanWrite || !staging.CanSeek)
        {
            throw new ArgumentException(
                "The attachment staging stream must be writable and seekable.",
                nameof(staging));
        }

        var refreshAttempted = false;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ClientAttachmentDownloadHttpResult.Failure(
                    ClientAttachmentDownloadHttpStatus.Canceled);
            }

            string? accessToken = null;
            try
            {
                if (!TryResetStaging(staging))
                {
                    return ClientAttachmentDownloadHttpResult.Failure(
                        ClientAttachmentDownloadHttpStatus.ProtocolError);
                }

                accessToken = await authenticationSession
                    .GetAccessTokenAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(accessToken) ||
                    !AuthenticationHeaderValue.TryParse(
                        $"Bearer {accessToken}",
                        out var authorization))
                {
                    return ClientAttachmentDownloadHttpResult.Failure(
                        ClientAttachmentDownloadHttpStatus.AuthenticationRequired);
                }

                var requestUri = new Uri(serverBaseUri, attachment.DownloadUrl);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Authorization = authorization;
                using var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (!HasOriginalRequestUri(response, requestUri))
                    {
                        return ClientAttachmentDownloadHttpResult.Failure(
                            ClientAttachmentDownloadHttpStatus.ProtocolError);
                    }

                    return await CopyVerifiedContentAsync(
                            response,
                            attachment,
                            staging,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (response.IsSuccessStatusCode)
                {
                    return ClientAttachmentDownloadHttpResult.Failure(
                        ClientAttachmentDownloadHttpStatus.ProtocolError);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (!await HasStableErrorAsync(
                            response,
                            ApiErrorCodes.AuthenticationRequired,
                            cancellationToken).ConfigureAwait(false))
                    {
                        return ClientAttachmentDownloadHttpResult.Failure(
                            ClientAttachmentDownloadHttpStatus.ProtocolError);
                    }

                    if (refreshAttempted)
                    {
                        return ClientAttachmentDownloadHttpResult.Failure(
                            ClientAttachmentDownloadHttpStatus.AuthenticationRequired);
                    }

                    refreshAttempted = true;
                    var refreshed = await authenticationSession
                        .TryRefreshAccessTokenAsync(accessToken, cancellationToken)
                        .ConfigureAwait(false);
                    if (!refreshed)
                    {
                        return ClientAttachmentDownloadHttpResult.Failure(
                            ClientAttachmentDownloadHttpStatus.AuthenticationRequired);
                    }

                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var revoked = await HasStableErrorAsync(
                            response,
                            ApiErrorCodes.ConversationAccessRevoked,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return ClientAttachmentDownloadHttpResult.Failure(
                        revoked
                            ? ClientAttachmentDownloadHttpStatus.AccessRevoked
                            : ClientAttachmentDownloadHttpStatus.AccessDenied);
                }

                return ClientAttachmentDownloadHttpResult.Failure(
                    IsTransient(response.StatusCode)
                        ? ClientAttachmentDownloadHttpStatus.TransientFailure
                        : ClientAttachmentDownloadHttpStatus.RemoteFailure);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ClientAttachmentDownloadHttpResult.Failure(
                    ClientAttachmentDownloadHttpStatus.Canceled);
            }
            catch (OperationCanceledException)
            {
                return ClientAttachmentDownloadHttpResult.Failure(
                    ClientAttachmentDownloadHttpStatus.TransientFailure);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                logger.LogWarning(
                    "Attachment download HTTP request failed transiently; errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientAttachmentDownloadHttpResult.Failure(
                    ClientAttachmentDownloadHttpStatus.TransientFailure);
            }
        }
    }

    private async Task<ClientAttachmentDownloadHttpResult> CopyVerifiedContentAsync(
        HttpResponseMessage response,
        AttachmentDto attachment,
        Stream staging,
        Action<ClientAttachmentDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var expectedSha256 = GetQuotedSha256(response.Headers);
        if (!HasExpectedHeaders(response, attachment, expectedSha256))
        {
            return ClientAttachmentDownloadHttpResult.Failure(
                ClientAttachmentDownloadHttpStatus.ProtocolError);
        }

        try
        {
            await using var content = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyBufferSize];
            long bytesWritten = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (read > attachment.Size - bytesWritten)
                {
                    return ClientAttachmentDownloadHttpResult.Failure(
                        ClientAttachmentDownloadHttpStatus.ProtocolError);
                }

                await staging.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                bytesWritten += read;
                ReportProgress(progress, bytesWritten, attachment.Size);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return bytesWritten == attachment.Size &&
                string.Equals(actualHash, expectedSha256, StringComparison.Ordinal)
                ? ClientAttachmentDownloadHttpResult.Success(actualHash, bytesWritten)
                : ClientAttachmentDownloadHttpResult.Failure(
                    ClientAttachmentDownloadHttpStatus.ProtocolError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClientAttachmentDownloadHttpResult.Failure(
                ClientAttachmentDownloadHttpStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            return ClientAttachmentDownloadHttpResult.Failure(
                ClientAttachmentDownloadHttpStatus.TransientFailure);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or NotSupportedException or
                ObjectDisposedException)
        {
            return ClientAttachmentDownloadHttpResult.Failure(
                ClientAttachmentDownloadHttpStatus.TransientFailure);
        }
    }

    private static bool HasExpectedHeaders(
        HttpResponseMessage response,
        AttachmentDto attachment,
        string? expectedSha256)
    {
        if ((response.Content.Headers.ContentLength is { } contentLength &&
             contentLength != attachment.Size) ||
            response.Content.Headers.ContentRange is not null ||
            response.Content.Headers.ContentEncoding.Count != 0)
        {
            return false;
        }

        var contentType = response.Content.Headers.ContentType;
        if (contentType is not null &&
            (!ClientAttachmentMetadataPolicy.TryCanonicalizeContentType(
                    contentType.ToString(),
                    out var canonicalContentType) ||
             !string.Equals(
                 canonicalContentType,
                 attachment.ContentType,
                 StringComparison.Ordinal)))
        {
            return false;
        }

        return expectedSha256 is not null;
    }

    private static bool HasOriginalRequestUri(
        HttpResponseMessage response,
        Uri expectedRequestUri) =>
        response.RequestMessage?.RequestUri is not { } effectiveRequestUri ||
        Uri.Compare(
            effectiveRequestUri,
            expectedRequestUri,
            UriComponents.AbsoluteUri,
            UriFormat.UriEscaped,
            StringComparison.Ordinal) == 0;

    private static string? GetQuotedSha256(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("ETag", out var values))
        {
            return null;
        }

        var tags = values.Take(2).ToArray();
        if (tags.Length != 1)
        {
            return null;
        }

        var value = tags[0];
        if (value.Length != 66 ||
            value[0] != '"' || value[^1] != '"')
        {
            return null;
        }

        var hash = value[1..^1];
        foreach (var character in hash)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return null;
            }
        }

        return hash;
    }

    private static async Task<bool> HasStableErrorAsync(
        HttpResponseMessage response,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentType?.MediaType is not { } mediaType ||
                !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            await response.Content
                .LoadIntoBufferAsync(MaximumErrorPayloadBytes, cancellationToken)
                .ConfigureAwait(false);
            var error = await response.Content
                .ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return error is not null &&
                string.Equals(error.Code, expectedCode, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(error.Message);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return false;
        }
    }

    private void ReportProgress(
        Action<ClientAttachmentDownloadProgress>? progress,
        long bytesWritten,
        long totalBytes)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            progress(new ClientAttachmentDownloadProgress(bytesWritten, totalBytes));
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            logger.LogWarning(
                "Attachment download progress callback failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private static bool TryResetStaging(Stream staging)
    {
        try
        {
            staging.Position = 0;
            staging.SetLength(0);
            return true;
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            return false;
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
