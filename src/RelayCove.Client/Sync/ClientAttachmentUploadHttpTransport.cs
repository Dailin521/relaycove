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

internal sealed class ClientAttachmentUploadHttpTransport
{
    private const long MaximumSuccessPayloadBytes = 64 * 1024;
    private const long MaximumAuthenticationErrorPayloadBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri uploadUri;
    private readonly HttpClient httpClient;
    private readonly IClientAuthenticationSession authenticationSession;
    private readonly ILogger logger;

    public ClientAttachmentUploadHttpTransport(
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
        uploadUri = new Uri(identity.CanonicalServerBaseUri, "api/attachments");
    }

    public async Task<ClientAttachmentUploadHttpResult> UploadAsync(
        ClientAttachmentUploadSource source,
        CancellationToken cancellationToken,
        Action<long>? bytesCopied = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var refreshAttempted = false;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ClientAttachmentUploadHttpResult.Failure(
                    ClientAttachmentUploadHttpStatus.Canceled);
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
                    return ClientAttachmentUploadHttpResult.Failure(
                        ClientAttachmentUploadHttpStatus.AuthenticationRequired);
                }

                var opened = await OpenValidatedStreamAsync(source, cancellationToken)
                    .ConfigureAwait(false);
                if (opened.Stream is null)
                {
                    return ClientAttachmentUploadHttpResult.Failure(opened.Status);
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, uploadUri);
                using var multipart = new MultipartFormDataContent();
                request.Content = multipart;
                request.Headers.Authorization = authorization;
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                var streamContent = new StreamContent(new ProgressReportingStream(
                    opened.Stream,
                    bytesCopied is null
                        ? null
                        : bytes => ReportBytesCopied(bytesCopied, bytes)));
                streamContent.Headers.ContentType = new MediaTypeHeaderValue(source.ContentType);
                multipart.Add(streamContent, "file", source.OriginalFileName);

                using var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Created)
                {
                    return await ReadSuccessAsync(response, source, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (response.IsSuccessStatusCode)
                {
                    return ClientAttachmentUploadHttpResult.Failure(
                        ClientAttachmentUploadHttpStatus.ProtocolError);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var hasStableAuthenticationRequired =
                        await HasStableAuthenticationRequiredAsync(response, cancellationToken)
                            .ConfigureAwait(false);
                    if (!hasStableAuthenticationRequired)
                    {
                        return ClientAttachmentUploadHttpResult.Failure(
                            ClientAttachmentUploadHttpStatus.ProtocolError);
                    }

                    if (refreshAttempted)
                    {
                        return ClientAttachmentUploadHttpResult.Failure(
                            ClientAttachmentUploadHttpStatus.AuthenticationRequired);
                    }

                    refreshAttempted = true;
                    var refreshed = await authenticationSession
                        .TryRefreshAccessTokenAsync(accessToken, cancellationToken)
                        .ConfigureAwait(false);
                    if (!refreshed)
                    {
                        return ClientAttachmentUploadHttpResult.Failure(
                            ClientAttachmentUploadHttpStatus.AuthenticationRequired);
                    }

                    continue;
                }

                return ClientAttachmentUploadHttpResult.Failure(
                    ClassifyFailure(response.StatusCode));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ClientAttachmentUploadHttpResult.Failure(
                    ClientAttachmentUploadHttpStatus.Canceled);
            }
            catch (OperationCanceledException)
            {
                return ClientAttachmentUploadHttpResult.Failure(
                    ClientAttachmentUploadHttpStatus.TransientFailure);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                logger.LogWarning(
                    "Attachment upload HTTP request failed transiently; errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientAttachmentUploadHttpResult.Failure(
                    ClientAttachmentUploadHttpStatus.TransientFailure);
            }
        }
    }

    private async Task<(ClientAttachmentUploadHttpStatus Status, Stream? Stream)> OpenValidatedStreamAsync(
        ClientAttachmentUploadSource source,
        CancellationToken cancellationToken)
    {
        Stream? stream = null;
        try
        {
            stream = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                return (ClientAttachmentUploadHttpStatus.SourceUnavailable, null);
            }

            if (!stream.CanRead || !stream.CanSeek ||
                stream.Position < 0 ||
                stream.Length < stream.Position ||
                stream.Length - stream.Position != source.Size)
            {
                DisposeSourceStream(stream);
                stream = null;
                return (ClientAttachmentUploadHttpStatus.SourceUnavailable, null);
            }

            var validatedStream = stream;
            stream = null;
            return (ClientAttachmentUploadHttpStatus.Success, validatedStream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DisposeSourceStream(stream);
            return (ClientAttachmentUploadHttpStatus.Canceled, null);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            DisposeSourceStream(stream);
            logger.LogWarning(
                "Attachment upload source could not be opened; errorType={ErrorType}.",
                exception.GetType().Name);
            return (ClientAttachmentUploadHttpStatus.SourceUnavailable, null);
        }
    }

    private void DisposeSourceStream(Stream? stream)
    {
        try
        {
            stream?.Dispose();
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            logger.LogWarning(
                "Attachment upload source disposal failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private void ReportBytesCopied(Action<long> bytesCopied, long copied)
    {
        try
        {
            bytesCopied(copied);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            logger.LogWarning(
                "Attachment upload progress callback failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private static async Task<ClientAttachmentUploadHttpResult> ReadSuccessAsync(
        HttpResponseMessage response,
        ClientAttachmentUploadSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsJsonContent(response.Content))
            {
                return ClientAttachmentUploadHttpResult.Failure(
                    ClientAttachmentUploadHttpStatus.ProtocolError);
            }

            await response.Content
                .LoadIntoBufferAsync(MaximumSuccessPayloadBytes, cancellationToken)
                .ConfigureAwait(false);
            var attachment = await response.Content
                .ReadFromJsonAsync<AttachmentDto>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return IsValidCreatedAttachment(response.Headers.Location, attachment, source)
                ? ClientAttachmentUploadHttpResult.Success(attachment!)
                : ClientAttachmentUploadHttpResult.Failure(
                    ClientAttachmentUploadHttpStatus.ProtocolError);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return ClientAttachmentUploadHttpResult.Failure(
                ClientAttachmentUploadHttpStatus.ProtocolError);
        }
    }

    private static async Task<bool> HasStableAuthenticationRequiredAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsJsonContent(response.Content))
            {
                return false;
            }

            await response.Content
                .LoadIntoBufferAsync(MaximumAuthenticationErrorPayloadBytes, cancellationToken)
                .ConfigureAwait(false);
            var error = await response.Content
                .ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return error is not null &&
                string.Equals(
                    error.Code,
                    ApiErrorCodes.AuthenticationRequired,
                    StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(error.Message);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return false;
        }
    }

    private static bool IsValidCreatedAttachment(
        Uri? location,
        AttachmentDto? attachment,
        ClientAttachmentUploadSource source)
    {
        if (attachment is null ||
            attachment.Id == Guid.Empty ||
            attachment.Size != source.Size ||
            attachment.ThumbnailUrl is not null ||
            !string.Equals(
                attachment.OriginalFileName,
                source.OriginalFileName,
                StringComparison.Ordinal) ||
            !string.Equals(attachment.ContentType, source.ContentType, StringComparison.Ordinal) ||
            !ClientAttachmentMetadataPolicy.IsValid(attachment) ||
            !string.Equals(
                attachment.DownloadUrl,
                $"/api/attachments/{attachment.Id:D}/download",
                StringComparison.Ordinal) ||
            location is null)
        {
            return false;
        }

        return string.Equals(
            location.OriginalString,
            $"/api/attachments/{attachment.Id:D}",
            StringComparison.Ordinal);
    }

    private static bool IsJsonContent(HttpContent content) =>
        content.Headers.ContentType?.MediaType is { } mediaType &&
        string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static ClientAttachmentUploadHttpStatus ClassifyFailure(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.BadRequest => ClientAttachmentUploadHttpStatus.ValidationFailed,
            HttpStatusCode.RequestEntityTooLarge => ClientAttachmentUploadHttpStatus.AttachmentTooLarge,
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
                HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                ClientAttachmentUploadHttpStatus.TransientFailure,
            _ => ClientAttachmentUploadHttpStatus.RemoteFailure,
        };

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class ProgressReportingStream(Stream inner, Action<long>? reportBytesCopied) : Stream
    {
        private readonly Stream inner = inner ?? throw new ArgumentNullException(nameof(inner));
        private readonly Action<long>? reportBytesCopied = reportBytesCopied;
        private long bytesCopied;
        private int disposed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Report(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Report(read);
            return read;
        }

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0)
            {
                Report(1);
            }

            return value;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken)
                .ConfigureAwait(false);
            Report(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Report(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }

            GC.SuppressFinalize(this);
        }

        private void Report(int read)
        {
            if (read <= 0 || reportBytesCopied is null)
            {
                return;
            }

            reportBytesCopied(Interlocked.Add(ref bytesCopied, read));
        }
    }
}
