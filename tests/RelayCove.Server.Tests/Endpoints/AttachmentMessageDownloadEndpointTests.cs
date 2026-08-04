using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayCove.Server.Data;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class AttachmentMessageDownloadEndpointTests
{
    private const string ExistingPassword = "a secure attachment message phrase";

    [Fact]
    public async Task SendImage_WhenAttachmentsAndMentionExist_BindsAndProjectsCanonicalPayloadEverywhere()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var adminName = CreateUserName("attachment-projection-admin");
        var readerName = CreateUserName("attachment-projection-reader");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var readerId = await factory.CreateUserAsync(readerName, ExistingPassword);
        using var client = await CreateAuthenticatedClientAsync(factory, adminName);
        var conversation = await CreateChannelAsync(client, ConversationType.PublicChannel, "Attachment projection");
        var first = await UploadAsync(client, [1, 2, 3], "第一张-🛰️.png", "image/png");
        var second = await UploadAsync(client, [4, 5], "second.jpg", "image/jpeg");
        var request = CreateAttachmentRequest(
            conversation.Id,
            MessageType.Image,
            "two images",
            [second.Id, first.Id]) with
        {
            MentionUserIds = [readerId],
        };

        using var sendResponse = await client.PostAsJsonAsync("/api/messages", request);

        Assert.Equal(HttpStatusCode.Created, sendResponse.StatusCode);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<MessageDto>())!;
        var expectedIds = new[] { first.Id, second.Id }.Order().ToArray();
        Assert.Equal(expectedIds, sent.Attachments.Select(attachment => attachment.Id));
        Assert.Equal([readerId], sent.MentionUserIds);
        Assert.Equal(MessageType.Image, sent.Type);
        Assert.Equal("two images", sent.Content);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            Assert.Equal(
                [sent.Id, sent.Id],
                await dbContext.Attachments
                    .Where(attachment => expectedIds.Contains(attachment.Id))
                    .OrderBy(attachment => attachment.Id)
                    .Select(attachment => attachment.MessageId!.Value)
                    .ToArrayAsync());
        }

        using var historyResponse = await client.GetAsync(
            $"/api/conversations/{conversation.Id:D}/messages");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var history = (await historyResponse.Content.ReadFromJsonAsync<MessageHistoryResponse>())!;
        AssertAttachmentPayload(Assert.Single(history.Messages), sent, expectedIds, readerId);

        using var aroundResponse = await client.GetAsync(
            $"/api/conversations/{conversation.Id:D}/messages/around/{sent.Id}?before=0&after=0");
        Assert.Equal(HttpStatusCode.OK, aroundResponse.StatusCode);
        var around = (await aroundResponse.Content.ReadFromJsonAsync<MessageAroundResponse>())!;
        AssertAttachmentPayload(Assert.Single(around.Messages), sent, expectedIds, readerId);

        using var syncResponse = await client.GetAsync("/api/sync?cursor=0&limit=100");
        Assert.Equal(HttpStatusCode.OK, syncResponse.StatusCode);
        var sync = (await syncResponse.Content.ReadFromJsonAsync<SyncResponse>())!;
        AssertAttachmentPayload(
            Assert.Single(sync.Messages, message => message.Id == sent.Id),
            sent,
            expectedIds,
            readerId);
    }

    [Fact]
    public async Task SendFile_WhenMaximumAttachmentCountIsUsed_BindsAllInCanonicalOrder()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userName = CreateUserName("attachment-maximum-admin");
        await factory.CreateUserAsync(userName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(factory, userName);
        var conversation = await CreateChannelAsync(
            client,
            ConversationType.PublicChannel,
            "Maximum attachment count");
        var uploaded = new List<AttachmentDto>();
        for (var index = 0; index < 10; index++)
        {
            uploaded.Add(await UploadAsync(
                client,
                [(byte)(index + 1)],
                $"maximum-{index}.bin",
                "application/octet-stream"));
        }

        var request = CreateAttachmentRequest(
            conversation.Id,
            MessageType.File,
            "ten files",
            uploaded.Select(attachment => attachment.Id).Reverse().ToArray());
        using var response = await client.PostAsJsonAsync("/api/messages", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<MessageDto>())!;
        var expectedIds = uploaded.Select(attachment => attachment.Id).Order().ToArray();
        Assert.Equal(expectedIds, message.Attachments.Select(attachment => attachment.Id));
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.Equal(
            10,
            await dbContext.Attachments.CountAsync(attachment =>
                expectedIds.Contains(attachment.Id) && attachment.MessageId == message.Id));
    }

    [Fact]
    public async Task SendFile_WhenReplayedOrRaced_AttachesOnceAndComparesExactAttachmentSet()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userName = CreateUserName("attachment-race-admin");
        await factory.CreateUserAsync(userName, ExistingPassword, isAdmin: true);
        using var firstClient = await CreateAuthenticatedClientAsync(factory, userName);
        using var secondClient = await CreateAuthenticatedClientAsync(factory, userName);
        var conversation = await CreateChannelAsync(firstClient, ConversationType.PublicChannel, "Attachment race");
        var racedAttachment = await UploadAsync(
            firstClient,
            [9, 8, 7],
            "raced.bin",
            "application/octet-stream");
        var firstRequest = CreateAttachmentRequest(
            conversation.Id,
            MessageType.File,
            null,
            [racedAttachment.Id]);
        var secondRequest = firstRequest with { ClientMessageId = Guid.NewGuid() };

        var firstTask = firstClient.PostAsJsonAsync("/api/messages", firstRequest);
        var secondTask = secondClient.PostAsJsonAsync("/api/messages", secondRequest);
        using var firstResponse = await firstTask;
        using var secondResponse = await secondTask;
        var responses = new[]
        {
            (Response: firstResponse, Request: firstRequest),
            (Response: secondResponse, Request: secondRequest),
        };
        var winner = Assert.Single(responses, pair => pair.Response.StatusCode == HttpStatusCode.Created);
        var loser = Assert.Single(responses, pair => pair.Response.StatusCode != HttpStatusCode.Created);
        Assert.Contains(
            loser.Response.StatusCode,
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.ServiceUnavailable });
        var created = (await winner.Response.Content.ReadFromJsonAsync<MessageDto>())!;
        Assert.Equal([racedAttachment.Id], created.Attachments.Select(attachment => attachment.Id));

        using var replayResponse = await firstClient.PostAsJsonAsync("/api/messages", winner.Request);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayed = (await replayResponse.Content.ReadFromJsonAsync<MessageDto>())!;
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal([racedAttachment.Id], replayed.Attachments.Select(attachment => attachment.Id));

        using var retryLoser = await firstClient.PostAsJsonAsync("/api/messages", loser.Request);
        await AssertErrorAsync(retryLoser, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);

        var other = await UploadAsync(firstClient, [6], "other.bin", "application/octet-stream");
        using var changedReplay = await firstClient.PostAsJsonAsync(
            "/api/messages",
            winner.Request with { AttachmentIds = [other.Id] });
        await AssertErrorAsync(
            changedReplay,
            HttpStatusCode.Conflict,
            ApiErrorCodes.IdempotencyKeyReuse);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.Equal(created.Id, (await dbContext.Attachments.SingleAsync(
            attachment => attachment.Id == racedAttachment.Id)).MessageId);
        Assert.Equal(1, await dbContext.Messages.CountAsync(message =>
            message.ClientMessageId == firstRequest.ClientMessageId ||
            message.ClientMessageId == secondRequest.ClientMessageId));
        Assert.Null((await dbContext.Attachments.SingleAsync(
            attachment => attachment.Id == other.Id)).MessageId);
    }

    [Fact]
    public async Task SendAttachment_WhenShapeOwnerOrDeclaredImageTypeIsInvalid_FailsWithoutBinding()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var adminName = CreateUserName("attachment-validation-admin");
        var otherName = CreateUserName("attachment-validation-other");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        await factory.CreateUserAsync(otherName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(factory, adminName);
        using var otherClient = await CreateAuthenticatedClientAsync(factory, otherName);
        var conversation = await CreateChannelAsync(adminClient, ConversationType.PublicChannel, "Attachment validation");
        var otherAttachment = await UploadAsync(otherClient, [1], "foreign.bin", "application/octet-stream");
        var nonImage = await UploadAsync(adminClient, [2], "not-image.bin", "application/octet-stream");

        var invalidRequests = new[]
        {
            CreateAttachmentRequest(conversation.Id, MessageType.File, null, []),
            CreateAttachmentRequest(conversation.Id, MessageType.File, null, [otherAttachment.Id]),
            CreateAttachmentRequest(conversation.Id, MessageType.Image, null, [nonImage.Id]),
            CreateAttachmentRequest(conversation.Id, MessageType.Text, "text", [nonImage.Id]),
            CreateAttachmentRequest(
                conversation.Id,
                MessageType.File,
                null,
                Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToArray()),
        };
        foreach (var request in invalidRequests)
        {
            using var response = await adminClient.PostAsJsonAsync("/api/messages", request);
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }

        using var unsupported = await adminClient.PostAsJsonAsync(
            "/api/messages",
            CreateAttachmentRequest(conversation.Id, MessageType.System, "system", []));
        await AssertErrorAsync(
            unsupported,
            HttpStatusCode.Conflict,
            ApiErrorCodes.MessageTypeUnsupported);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.Empty(await dbContext.Messages.Where(
            message => message.ConversationId == conversation.Id).ToArrayAsync());
        Assert.Null((await dbContext.Attachments.SingleAsync(
            attachment => attachment.Id == nonImage.Id)).MessageId);
    }

    [Fact]
    public async Task MetadataAndDownload_WhenConversationTypesAndAuthorizationChange_FailClosedAndSupportRange()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var adminName = CreateUserName("attachment-access-admin");
        var memberName = CreateUserName("attachment-access-member");
        var outsiderName = CreateUserName("attachment-access-outsider");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, ExistingPassword);
        await factory.CreateUserAsync(outsiderName, ExistingPassword);
        using var adminClient = await CreateAuthenticatedClientAsync(factory, adminName);
        using var memberClient = await CreateAuthenticatedClientAsync(factory, memberName);
        using var outsiderClient = await CreateAuthenticatedClientAsync(factory, outsiderName);

        var publicConversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PublicChannel,
            "Public attachment access");
        var publicAttachment = await UploadAndBindAsync(
            adminClient,
            publicConversation.Id,
            "报告-🛰️.bin",
            [0, 1, 2, 3, 4, 5]);
        await AssertMetadataAndFullDownloadAsync(outsiderClient, publicAttachment, [0, 1, 2, 3, 4, 5]);

        using (var rangeRequest = new HttpRequestMessage(
                   HttpMethod.Get,
                   publicAttachment.DownloadUrl))
        {
            rangeRequest.Headers.Range = new RangeHeaderValue(2, 4);
            using var rangeResponse = await outsiderClient.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal([2, 3, 4], await rangeResponse.Content.ReadAsByteArrayAsync());
            Assert.Equal("bytes", rangeResponse.Content.Headers.ContentRange!.Unit);
            Assert.Equal(2, rangeResponse.Content.Headers.ContentRange.From);
            Assert.Equal(4, rangeResponse.Content.Headers.ContentRange.To);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            var stored = await dbContext.Conversations.SingleAsync(candidate =>
                candidate.Id == publicConversation.Id);
            stored.MarkDeleted(stored.UpdatedAt);
            await dbContext.SaveChangesAsync();
        }

        await AssertAttachmentDeniedAsync(adminClient, publicAttachment.Id);
        await AssertAttachmentDeniedAsync(outsiderClient, publicAttachment.Id);

        var privateConversation = await CreateChannelAsync(
            adminClient,
            ConversationType.PrivateChannel,
            "Private attachment access");
        await UpsertMemberAsync(adminClient, privateConversation.Id, memberId);
        var privateAttachment = await UploadAndBindAsync(
            adminClient,
            privateConversation.Id,
            "private.bin",
            [6, 7, 8]);
        await AssertMetadataAndFullDownloadAsync(memberClient, privateAttachment, [6, 7, 8]);
        await AssertAttachmentDeniedAsync(outsiderClient, privateAttachment.Id);
        using (var removeMember = await adminClient.DeleteAsync(
                   $"/api/conversations/{privateConversation.Id:D}/members/{memberId:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removeMember.StatusCode);
        }

        await AssertAttachmentDeniedAsync(memberClient, privateAttachment.Id);

        var directConversation = await CreateDirectAsync(adminClient, memberId);
        var directAttachment = await UploadAndBindAsync(
            adminClient,
            directConversation.Id,
            "direct.bin",
            [9]);
        await AssertMetadataAndFullDownloadAsync(memberClient, directAttachment, [9]);
        await AssertAttachmentDeniedAsync(outsiderClient, directAttachment.Id);

        var unbound = await UploadAsync(adminClient, [10], "unbound.bin", "application/octet-stream");
        await AssertAttachmentDeniedAsync(adminClient, unbound.Id);
        await AssertAttachmentDeniedAsync(adminClient, Guid.NewGuid());
        using var anonymous = factory.CreateClient();
        using var anonymousDownload = await anonymous.GetAsync(publicAttachment.DownloadUrl);
        await AssertErrorAsync(
            anonymousDownload,
            HttpStatusCode.Unauthorized,
            ApiErrorCodes.AuthenticationRequired);
    }

    [Fact]
    public async Task Download_WhenPhysicalFileDisappears_ReturnsRedacted500()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var adminName = CreateUserName("attachment-missing-admin");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(factory, adminName);
        var conversation = await CreateChannelAsync(client, ConversationType.PublicChannel, "Missing attachment");
        const string originalFileName = "missing-secret-name.bin";
        var attachment = await UploadAndBindAsync(
            client,
            conversation.Id,
            originalFileName,
            [1, 3, 5]);
        string storedFileName;
        string sha256;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            var stored = await dbContext.Attachments
                .Where(candidate => candidate.Id == attachment.Id)
                .Select(candidate => new { candidate.StoredFileName, candidate.Sha256 })
                .SingleAsync();
            storedFileName = stored.StoredFileName;
            sha256 = stored.Sha256;
        }

        File.Delete(Path.Combine(factory.UploadsPath, storedFileName));
        var logOffset = factory.LogMessages.Count;

        using var response = await client.GetAsync(attachment.DownloadUrl);

        await AssertErrorAsync(
            response,
            HttpStatusCode.InternalServerError,
            ApiErrorCodes.InternalServerError);
        var logs = string.Join('\n', factory.LogMessages.Skip(logOffset));
        Assert.DoesNotContain(originalFileName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(storedFileName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(sha256, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.UploadsPath, logs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Download_WhenPhysicalFileCannotBeOpened_ReturnsRedacted500()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var adminName = CreateUserName("attachment-locked-admin");
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(factory, adminName);
        var conversation = await CreateChannelAsync(client, ConversationType.PublicChannel, "Locked attachment");
        const string originalFileName = "locked-secret-name.bin";
        var attachment = await UploadAndBindAsync(
            client,
            conversation.Id,
            originalFileName,
            [2, 4, 6]);
        string storedFileName;
        string sha256;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            var stored = await dbContext.Attachments
                .Where(candidate => candidate.Id == attachment.Id)
                .Select(candidate => new { candidate.StoredFileName, candidate.Sha256 })
                .SingleAsync();
            storedFileName = stored.StoredFileName;
            sha256 = stored.Sha256;
        }

        var path = Path.Combine(factory.UploadsPath, storedFileName);
        var logOffset = factory.LogMessages.Count;
        await using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var response = await client.GetAsync(attachment.DownloadUrl);

        await AssertErrorAsync(
            response,
            HttpStatusCode.InternalServerError,
            ApiErrorCodes.InternalServerError);
        var logs = string.Join('\n', factory.LogMessages.Skip(logOffset));
        Assert.DoesNotContain(originalFileName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(storedFileName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(sha256, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.UploadsPath, logs, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AttachmentDto> UploadAndBindAsync(
        HttpClient client,
        Guid conversationId,
        string fileName,
        byte[] bytes)
    {
        var uploaded = await UploadAsync(client, bytes, fileName, "application/octet-stream");
        using var response = await client.PostAsJsonAsync(
            "/api/messages",
            CreateAttachmentRequest(conversationId, MessageType.File, null, [uploaded.Id]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.Single((await response.Content.ReadFromJsonAsync<MessageDto>())!.Attachments);
    }

    private static async Task<AttachmentDto> UploadAsync(
        HttpClient client,
        byte[] bytes,
        string fileName,
        string contentType)
    {
        using var form = new MultipartFormDataContent($"relaycove-{Guid.NewGuid():N}");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        using var response = await client.PostAsync("/api/attachments", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AttachmentDto>())!;
    }

    private static async Task AssertMetadataAndFullDownloadAsync(
        HttpClient client,
        AttachmentDto expected,
        byte[] expectedBytes)
    {
        using (var metadataResponse = await client.GetAsync($"/api/attachments/{expected.Id:D}"))
        {
            Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
            Assert.Equal(expected, await metadataResponse.Content.ReadFromJsonAsync<AttachmentDto>());
            Assert.True(metadataResponse.Headers.CacheControl!.Private);
            Assert.True(metadataResponse.Headers.CacheControl.NoStore);
            Assert.Equal("nosniff", metadataResponse.Headers.GetValues("X-Content-Type-Options").Single());
        }

        using var downloadResponse = await client.GetAsync(expected.DownloadUrl);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal(expectedBytes, await downloadResponse.Content.ReadAsByteArrayAsync());
        Assert.True(downloadResponse.Headers.CacheControl!.Private);
        Assert.True(downloadResponse.Headers.CacheControl.NoStore);
        Assert.Equal("nosniff", downloadResponse.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("attachment", downloadResponse.Content.Headers.ContentDisposition!.DispositionType);
        Assert.False(string.IsNullOrWhiteSpace(
            downloadResponse.Content.Headers.ContentDisposition.FileNameStar ??
            downloadResponse.Content.Headers.ContentDisposition.FileName));
    }

    private static async Task AssertAttachmentDeniedAsync(HttpClient client, Guid attachmentId)
    {
        using var metadata = await client.GetAsync($"/api/attachments/{attachmentId:D}");
        await AssertErrorAsync(
            metadata,
            HttpStatusCode.Forbidden,
            ApiErrorCodes.ConversationAccessRevoked);
        using var download = await client.GetAsync($"/api/attachments/{attachmentId:D}/download");
        await AssertErrorAsync(
            download,
            HttpStatusCode.Forbidden,
            ApiErrorCodes.ConversationAccessRevoked);
    }

    private static void AssertAttachmentPayload(
        MessageDto actual,
        MessageDto expected,
        Guid[] expectedAttachmentIds,
        Guid mentionedUserId)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expectedAttachmentIds, actual.Attachments.Select(attachment => attachment.Id));
        Assert.Equal([mentionedUserId], actual.MentionUserIds);
        Assert.Equal(actual.Attachments.Count, actual.Attachments.Select(attachment => attachment.Id).Distinct().Count());
    }

    private static SendMessageRequest CreateAttachmentRequest(
        Guid conversationId,
        MessageType type,
        string? content,
        IReadOnlyList<Guid> attachmentIds) =>
        new(
            Guid.NewGuid(),
            conversationId,
            type,
            content,
            null,
            attachmentIds,
            []);

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(
        RelayCoveWebApplicationFactory factory,
        string userName)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, ExistingPassword, "attachment-message-test", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static async Task<ConversationDto> CreateChannelAsync(
        HttpClient client,
        ConversationType type,
        string name)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(type, name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task<ConversationDto> CreateDirectAsync(
        HttpClient client,
        Guid participantUserId)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: participantUserId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task UpsertMemberAsync(
        HttpClient client,
        Guid conversationId,
        Guid userId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId:D}/members",
            new UpsertConversationMemberRequest(userId, ConversationMemberRole.Member));
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Created });
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
}
