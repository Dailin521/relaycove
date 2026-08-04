using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayCove.Server.Data;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class AttachmentUploadEndpointTests(
    RelayCoveWebApplicationFactory factory) :
    IClassFixture<RelayCoveWebApplicationFactory>,
    IAsyncLifetime
{
    private const string ExistingPassword = "a secure attachment upload phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Upload_WhenValid_PersistsOpaqueBytesHashAndMetadataWithoutLoggingNames()
    {
        var userName = CreateUserName("attachment-valid");
        var userId = await factory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(factory, userName);
        var payload = new byte[] { 0, 1, 2, 3, 128, 254, 255 };
        const string originalFileName = "报告-🛰️.png";
        var logOffset = factory.LogMessages.Count;

        using var response = await UploadAsync(
            client,
            payload,
            originalFileName,
            "IMAGE/PNG; charset=binary");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<AttachmentDto>())!;
        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal(originalFileName, dto.OriginalFileName);
        Assert.Equal("image/png", dto.ContentType);
        Assert.Equal(payload.Length, dto.Size);
        Assert.Equal($"/api/attachments/{dto.Id:D}/download", dto.DownloadUrl);
        Assert.Null(dto.ThumbnailUrl);
        Assert.Equal(new Uri($"/api/attachments/{dto.Id:D}", UriKind.Relative), response.Headers.Location);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var attachment = await dbContext.Attachments.AsNoTracking().SingleAsync(candidate => candidate.Id == dto.Id);
        Assert.Null(attachment.MessageId);
        Assert.Equal(userId, attachment.UploaderUserId);
        Assert.Equal(originalFileName, attachment.OriginalFileName);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal(payload.Length, attachment.Size);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            attachment.Sha256);
        Assert.Equal(DateTimeKind.Utc, attachment.CreatedAt.Kind);
        Assert.Equal(0, attachment.CreatedAt.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Matches("^[0-9a-f]{32}_[0-9a-f]{32}$", attachment.StoredFileName);
        Assert.StartsWith(dto.Id.ToString("N"), attachment.StoredFileName, StringComparison.Ordinal);
        Assert.DoesNotContain("报告", attachment.StoredFileName, StringComparison.Ordinal);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(factory.UploadsPath, attachment.StoredFileName)));

        var logs = string.Join('\n', factory.LogMessages.Skip(logOffset));
        Assert.DoesNotContain(originalFileName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(attachment.StoredFileName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(attachment.Sha256, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_WhenContentTypeIsMissing_UsesOctetStream()
    {
        var userName = CreateUserName("attachment-default-type");
        await factory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(factory, userName);

        using var response = await UploadAsync(client, [1, 2, 3], "archive.bin", contentType: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<AttachmentDto>())!;
        Assert.Equal("application/octet-stream", dto.ContentType);
    }

    [Fact]
    public async Task Upload_WhenAnonymousOrActorBecomesDisabled_Returns401WithoutArtifacts()
    {
        using (var anonymous = factory.CreateClient())
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                using var anonymousResponse = await UploadAsync(
                    anonymous,
                    [1],
                    "anonymous.txt",
                    "text/plain");
                await AssertErrorAsync(
                    anonymousResponse,
                    HttpStatusCode.Unauthorized,
                    ApiErrorCodes.AuthenticationRequired);
            }
        }

        var userName = CreateUserName("attachment-disabled");
        var userId = await factory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(factory, userName);
        await factory.SetUserDisabledAsync(userId, isDisabled: true);

        using var disabledResponse = await UploadAsync(client, [2], "disabled.txt", "text/plain");

        await AssertErrorAsync(
            disabledResponse,
            HttpStatusCode.Unauthorized,
            ApiErrorCodes.AuthenticationRequired);
        await AssertNoAttachmentsForUserAsync(factory, userId);
    }

    [Fact]
    public async Task Upload_WhenFileIsExactlyAtLimit_SucceedsAndLimitPlusOneReturns413WithoutLeak()
    {
        using var limitedFactory = CreateFactory(new Dictionary<string, string?>
        {
            ["Uploads:MaximumFileBytes"] = "8",
            ["Uploads:PermitLimit"] = "100",
        });
        await limitedFactory.InitializeDatabaseAsync();
        var userName = CreateUserName("attachment-limit");
        var userId = await limitedFactory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(limitedFactory, userName);

        using (var exact = await UploadAsync(client, new byte[8], "exact.bin", "application/octet-stream"))
        {
            Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
        }

        using var oversized = await UploadAsync(client, new byte[9], "oversized.bin", "application/octet-stream");

        await AssertErrorAsync(oversized, HttpStatusCode.RequestEntityTooLarge, ApiErrorCodes.AttachmentTooLarge);
        await using var scope = limitedFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.Equal(1, await dbContext.Attachments.CountAsync(candidate => candidate.UploaderUserId == userId));
        Assert.Single(Directory.EnumerateFiles(limitedFactory.UploadsPath));
    }

    [Fact]
    public async Task Upload_WhenLengthIsUnknown_CannotBypassStreamingLimit()
    {
        using var limitedFactory = CreateFactory(new Dictionary<string, string?>
        {
            ["Uploads:MaximumFileBytes"] = "8",
            ["Uploads:PermitLimit"] = "100",
        });
        await limitedFactory.InitializeDatabaseAsync();
        var userName = CreateUserName("attachment-chunked-limit");
        var userId = await limitedFactory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(limitedFactory, userName);
        var boundary = $"relaycove-{Guid.NewGuid():N}";
        using var content = new UnknownLengthContent(
            CreateRawMultipart(boundary, new byte[9]),
            $"multipart/form-data; boundary={boundary}");

        using var response = await client.PostAsync("/api/attachments", content);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, ApiErrorCodes.AttachmentTooLarge);
        await AssertNoAttachmentsForUserAsync(limitedFactory, userId);
        Assert.Empty(Directory.EnumerateFiles(limitedFactory.UploadsPath));
    }

    [Fact]
    public async Task Upload_WhenMultipartShapeIsInvalid_Returns400WithoutArtifacts()
    {
        var userName = CreateUserName("attachment-shape");
        var userId = await factory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(factory, userName);

        using (var missingBoundaryContent = new ByteArrayContent([1, 2]))
        {
            missingBoundaryContent.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data");
            using var response = await client.PostAsync("/api/attachments", missingBoundaryContent);
            await AssertValidationErrorAsync(response);
        }

        using (var wrongField = CreateForm([1], "wrong", "file.txt", "text/plain"))
        using (var response = await client.PostAsync("/api/attachments", wrongField))
        {
            await AssertValidationErrorAsync(response);
        }

        using (var extraField = CreateForm([1], "file", "file.txt", "text/plain"))
        {
            extraField.Add(new StringContent("extra"), "description");
            using var response = await client.PostAsync("/api/attachments", extraField);
            await AssertValidationErrorAsync(response);
        }

        using (var extraFile = CreateForm([1], "file", "first.txt", "text/plain"))
        {
            var second = new ByteArrayContent([2]);
            second.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            extraFile.Add(second, "file", "second.txt");
            using var response = await client.PostAsync("/api/attachments", extraFile);
            await AssertValidationErrorAsync(response);
        }

        await AssertNoAttachmentsForUserAsync(factory, userId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData("folder\\secret.txt")]
    [InlineData("leading.txt ")]
    [InlineData("bidi\u202Etxt.exe")]
    public async Task Upload_WhenFileNameIsUnsafe_Returns400WithoutArtifacts(string fileName)
    {
        var userName = CreateUserName("attachment-name");
        var userId = await factory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(factory, userName);

        using var response = await UploadAsync(client, [1], fileName, "text/plain");

        await AssertValidationErrorAsync(response);
        await AssertNoAttachmentsForUserAsync(factory, userId);
    }

    [Fact]
    public async Task Upload_WhenFileNameOrBoundaryExceedsParserLimit_Returns400WithoutArtifacts()
    {
        var userName = CreateUserName("attachment-header-limit");
        var userId = await factory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(factory, userName);

        using (var longName = await UploadAsync(
                   client,
                   [1],
                   new string('a', 256),
                   "text/plain"))
        {
            await AssertValidationErrorAsync(longName);
        }

        var boundary = new string('b', 129);
        using var invalidBoundary = new ByteArrayContent(CreateRawMultipart(boundary, [1]));
        invalidBoundary.Headers.TryAddWithoutValidation(
            "Content-Type",
            $"multipart/form-data; boundary={boundary}");
        using var boundaryResponse = await client.PostAsync("/api/attachments", invalidBoundary);
        await AssertValidationErrorAsync(boundaryResponse);
        await AssertNoAttachmentsForUserAsync(factory, userId);
    }

    [Fact]
    public async Task Upload_WhenFileIsEmptyOrMediaTypeIsInvalid_Returns400WithoutArtifacts()
    {
        var userName = CreateUserName("attachment-metadata");
        var userId = await factory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(factory, userName);

        using (var empty = await UploadAsync(client, [], "empty.txt", "text/plain"))
        {
            await AssertValidationErrorAsync(empty);
        }

        using (var form = CreateForm([1], "file", "invalid.txt", contentType: null))
        {
            var part = Assert.Single(form.OfType<ByteArrayContent>());
            part.Headers.TryAddWithoutValidation("Content-Type", "not a media type");
            using var invalidType = await client.PostAsync("/api/attachments", form);
            await AssertValidationErrorAsync(invalidType);
        }

        await AssertNoAttachmentsForUserAsync(factory, userId);
    }

    [Fact]
    public async Task Upload_WhenRateLimitIsExceeded_ReturnsStable429PartitionedBySubject()
    {
        using var rateFactory = CreateFactory(new Dictionary<string, string?>
        {
            ["Uploads:PermitLimit"] = "1",
            ["Uploads:RateLimitWindowSeconds"] = "300",
        });
        await rateFactory.InitializeDatabaseAsync();
        var firstName = CreateUserName("attachment-rate-a");
        var secondName = CreateUserName("attachment-rate-b");
        await rateFactory.CreateUserAsync(firstName, ExistingPassword);
        await rateFactory.CreateUserAsync(secondName, ExistingPassword);
        using var firstClient = await CreateAuthenticatedClientAsync(rateFactory, firstName);
        using var secondClient = await CreateAuthenticatedClientAsync(rateFactory, secondName);

        using (var accepted = await UploadAsync(firstClient, [1], "first.txt", "text/plain"))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        using var limited = await UploadAsync(firstClient, [2], "limited.txt", "text/plain");
        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimitExceeded);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        using var independent = await UploadAsync(secondClient, [3], "second.txt", "text/plain");
        Assert.Equal(HttpStatusCode.Created, independent.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenSqliteIsBusy_Returns503AndCleansStagingFile()
    {
        using var busyFactory = new RelayCoveWebApplicationFactory(
            1_000,
            1_000,
            databaseTimeoutSeconds: 1,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Uploads:PermitLimit"] = "100",
            });
        await busyFactory.InitializeDatabaseAsync();
        var userName = CreateUserName("attachment-busy");
        var userId = await busyFactory.CreateUserAsync(userName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(busyFactory, userName);
        await using var lockConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = busyFactory.DatabasePath,
            DefaultTimeout = 1,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        await lockConnection.OpenAsync();
        await using var lockTransaction = lockConnection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        using var response = await UploadAsync(client, [1, 2, 3], "busy.txt", "text/plain");

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ServiceUnavailable);
        await AssertNoAttachmentsForUserAsync(busyFactory, userId);
    }

    private static RelayCoveWebApplicationFactory CreateFactory(
        IReadOnlyDictionary<string, string?> overrides) =>
        new(1_000, 1_000, configurationOverrides: overrides);

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(
        RelayCoveWebApplicationFactory applicationFactory,
        string userName)
    {
        var client = applicationFactory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, ExistingPassword, "attachment-test", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        byte[] content,
        string fileName,
        string? contentType)
    {
        using var form = CreateForm(content, "file", fileName, contentType);
        var response = await client.PostAsync("/api/attachments", form);
        return response;
    }

    private static MultipartFormDataContent CreateForm(
        byte[] content,
        string fieldName,
        string fileName,
        string? contentType)
    {
        var form = new MultipartFormDataContent($"relaycove-{Guid.NewGuid():N}");
        var file = new ByteArrayContent(content);
        if (contentType is not null)
        {
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            file.Headers.TryAddWithoutValidation(
                "Content-Disposition",
                $"form-data; name=\"{fieldName}\"; filename=\"{fileName}\"");
            form.Add(file);
        }
        else
        {
            form.Add(file, fieldName, fileName);
        }

        return form;
    }

    private static byte[] CreateRawMultipart(string boundary, byte[] content)
    {
        var prefix = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"file\"; filename=\"stream.bin\"\r\n" +
            "Content-Type: application/octet-stream\r\n\r\n");
        var suffix = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");
        return [.. prefix, .. content, .. suffix];
    }

    private static async Task AssertNoAttachmentsForUserAsync(
        RelayCoveWebApplicationFactory applicationFactory,
        Guid userId)
    {
        await using var scope = applicationFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.Empty(await dbContext.Attachments
            .AsNoTracking()
            .Where(attachment => attachment.UploaderUserId == userId)
            .ToArrayAsync());
        if (Directory.Exists(applicationFactory.UploadsPath))
        {
            Assert.DoesNotContain(
                Directory.EnumerateFiles(applicationFactory.UploadsPath),
                path => Path.GetFileName(path).StartsWith(".upload_", StringComparison.Ordinal));
        }
    }

    private static async Task AssertValidationErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(ApiErrorCodes.ValidationFailed, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
        Assert.Contains("file", error.Details!.Keys);
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(expectedCode, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
    }

    private static string CreateUserName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] content;

        public UnknownLengthContent(byte[] content, string contentType)
        {
            this.content = content;
            Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        public override string ToString() => nameof(UnknownLengthContent);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }
}
