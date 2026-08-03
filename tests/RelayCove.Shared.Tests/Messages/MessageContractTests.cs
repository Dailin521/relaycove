using System.Text.Json;
using RelayCove.Shared.Messages;

namespace RelayCove.Shared.Tests.Messages;

public sealed class MessageContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MessageType_WhenSerialized_UsesStableNumericValues()
    {
        Assert.Equal(1, (int)MessageType.Text);
        Assert.Equal(2, (int)MessageType.Image);
        Assert.Equal(3, (int)MessageType.File);
        Assert.Equal(4, (int)MessageType.System);
        Assert.Equal([1, 2, 3, 4], Enum.GetValues<MessageType>().Select(value => (int)value));
    }

    [Fact]
    public void SendMessageRequest_WhenRoundTripped_PreservesExactPayloadAndShape()
    {
        var request = new SendMessageRequest(
            Guid.Parse("6072ce46-8b5b-49be-be93-04ea8e8bb5b5"),
            Guid.Parse("0f42cc0e-a5c5-49ec-80c8-487a2bdcad22"),
            MessageType.Text,
            "  exact 🛰️ text\r\n",
            42,
            [],
            [Guid.Parse("7557da2e-62df-4195-b8ff-9473e6b96918")]);

        var json = JsonSerializer.Serialize(request, WebJson);
        using var document = JsonDocument.Parse(json);
        var roundTripped = JsonSerializer.Deserialize<SendMessageRequest>(json, WebJson);

        Assert.Equal(
            ["clientMessageId", "conversationId", "type", "content", "replyToMessageId", "attachmentIds", "mentionUserIds"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.NotNull(roundTripped);
        Assert.Equal(request.ClientMessageId, roundTripped.ClientMessageId);
        Assert.Equal(request.ConversationId, roundTripped.ConversationId);
        Assert.Equal(request.Type, roundTripped.Type);
        Assert.Equal(request.Content, roundTripped.Content);
        Assert.Equal(request.ReplyToMessageId, roundTripped.ReplyToMessageId);
        Assert.Equal(request.AttachmentIds, roundTripped.AttachmentIds);
        Assert.Equal(request.MentionUserIds, roundTripped.MentionUserIds);
    }

    [Fact]
    public void MessageResponses_WhenRoundTripped_PreserveHistoryCursorAndAscendingMessages()
    {
        var message = new MessageDto(
            43,
            Guid.Parse("b1102584-5519-408b-8c99-73eac92b5be2"),
            Guid.Parse("0f42cc0e-a5c5-49ec-80c8-487a2bdcad22"),
            Guid.Parse("afce19a7-86d8-46a8-9fd0-aae360a9d531"),
            "Alice",
            MessageType.Text,
            "payload",
            null,
            [],
            [],
            new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero));
        var response = new MessageHistoryResponse([message], 43, HasMore: true);

        var json = JsonSerializer.Serialize(response, WebJson);
        using var document = JsonDocument.Parse(json);
        var roundTripped = JsonSerializer.Deserialize<MessageHistoryResponse>(json, WebJson);

        Assert.Equal(["messages", "nextBeforeMessageId", "hasMore"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.NotNull(roundTripped);
        Assert.Equal(response.NextBeforeMessageId, roundTripped.NextBeforeMessageId);
        Assert.Equal(response.HasMore, roundTripped.HasMore);
        var roundTrippedMessage = Assert.Single(roundTripped.Messages);
        Assert.Equal(message.Id, roundTrippedMessage.Id);
        Assert.Equal(message.ClientMessageId, roundTrippedMessage.ClientMessageId);
        Assert.Equal(message.ConversationId, roundTrippedMessage.ConversationId);
        Assert.Equal(message.SenderId, roundTrippedMessage.SenderId);
        Assert.Equal(message.SenderDisplayName, roundTrippedMessage.SenderDisplayName);
        Assert.Equal(message.Type, roundTrippedMessage.Type);
        Assert.Equal(message.Content, roundTrippedMessage.Content);
        Assert.Equal(message.ReplyToMessageId, roundTrippedMessage.ReplyToMessageId);
        Assert.Equal(message.Attachments, roundTrippedMessage.Attachments);
        Assert.Equal(message.MentionUserIds, roundTrippedMessage.MentionUserIds);
        Assert.Equal(message.CreatedAt, roundTrippedMessage.CreatedAt);
    }

    [Fact]
    public void MessageContracts_WhenFormatted_DoNotExposeSensitivePayloadCollectionsOrReply()
    {
        const string secret = "message-secret-e1976e";
        var conversationId = Guid.NewGuid();
        var clientMessageId = Guid.NewGuid();
        var mentionedUserId = Guid.NewGuid();
        var request = new SendMessageRequest(
            clientMessageId,
            conversationId,
            MessageType.Text,
            secret,
            987654,
            [Guid.NewGuid()],
            [mentionedUserId]);
        var response = new MessageDto(
            1,
            clientMessageId,
            conversationId,
            Guid.NewGuid(),
            "Sensitive display name",
            MessageType.Text,
            secret,
            987654,
            [],
            [mentionedUserId],
            DateTimeOffset.UtcNow);

        Assert.DoesNotContain(secret, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(mentionedUserId.ToString("D"), request.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("987654", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, response.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive display name", response.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(mentionedUserId.ToString("D"), response.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("987654", response.ToString(), StringComparison.Ordinal);
    }
}
