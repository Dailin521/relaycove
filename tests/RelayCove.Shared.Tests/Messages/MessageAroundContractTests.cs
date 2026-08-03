using System.Text.Json;
using RelayCove.Shared.Messages;

namespace RelayCove.Shared.Tests.Messages;

public sealed class MessageAroundContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MessageAroundResponse_WhenRoundTripped_PreservesStableShape()
    {
        var message = new MessageDto(
            42,
            Guid.Parse("3eeeb26c-9292-452a-8b27-f2f3d5939d47"),
            Guid.Parse("327ed894-a0ea-4501-a470-4549c1507a20"),
            Guid.Parse("77a014cb-13f3-4428-b932-64e5aa6e6a92"),
            "Sender",
            MessageType.Text,
            "secret content",
            null,
            [],
            [],
            new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var response = new MessageAroundResponse([message], 42, true, false);

        var json = JsonSerializer.Serialize(response, WebJson);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["messages", "targetMessageId", "hasMoreBefore", "hasMoreAfter"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        var roundTrip = JsonSerializer.Deserialize<MessageAroundResponse>(json, WebJson)!;
        Assert.Equal(response.TargetMessageId, roundTrip.TargetMessageId);
        Assert.Equal(response.HasMoreBefore, roundTrip.HasMoreBefore);
        Assert.Equal(response.HasMoreAfter, roundTrip.HasMoreAfter);
        var roundTrippedMessage = Assert.Single(roundTrip.Messages);
        Assert.Equal(message.Id, roundTrippedMessage.Id);
        Assert.Equal(message.ClientMessageId, roundTrippedMessage.ClientMessageId);
        Assert.Equal(message.ConversationId, roundTrippedMessage.ConversationId);
        Assert.Equal(message.SenderId, roundTrippedMessage.SenderId);
        Assert.Equal(message.Content, roundTrippedMessage.Content);
    }

    [Fact]
    public void MessageAroundResponse_WhenFormatted_RedactsMessagesAndTarget()
    {
        const long targetMessageId = 9_876_543_210;
        const string secret = "around-response-secret-8c917f";
        var response = new MessageAroundResponse(
            [new MessageDto(
                targetMessageId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Sender",
                MessageType.Text,
                secret,
                null,
                [],
                [],
                DateTimeOffset.UtcNow)],
            targetMessageId,
            false,
            false);

        Assert.DoesNotContain(secret, response.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(targetMessageId.ToString(), response.ToString(), StringComparison.Ordinal);
        Assert.Contains("Messages = [REDACTED]", response.ToString(), StringComparison.Ordinal);
        Assert.Contains("TargetMessageId = [REDACTED]", response.ToString(), StringComparison.Ordinal);
    }
}
