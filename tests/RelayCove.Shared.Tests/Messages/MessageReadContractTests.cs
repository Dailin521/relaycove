using System.Text.Json;
using RelayCove.Shared.Messages;

namespace RelayCove.Shared.Tests.Messages;

public sealed class MessageReadContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MessageReadContracts_WhenRoundTripped_PreserveStableShapes()
    {
        var request = new MarkConversationReadRequest(42);
        var receipt = new ConversationReadReceipt(
            Guid.Parse("ca8febc2-ff81-4d5a-9b34-1622393929c8"),
            42);

        var requestJson = JsonSerializer.Serialize(request, WebJson);
        var receiptJson = JsonSerializer.Serialize(receipt, WebJson);
        using var requestDocument = JsonDocument.Parse(requestJson);
        using var receiptDocument = JsonDocument.Parse(receiptJson);

        Assert.Equal(["messageId"],
            requestDocument.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["conversationId", "lastReadMessageId"],
            receiptDocument.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(request, JsonSerializer.Deserialize<MarkConversationReadRequest>(requestJson, WebJson));
        Assert.Equal(receipt, JsonSerializer.Deserialize<ConversationReadReceipt>(receiptJson, WebJson));
    }

    [Fact]
    public void MessageReadContracts_WhenFormatted_RedactWatermarks()
    {
        const long sensitiveMessageId = 9_876_543_210;
        var request = new MarkConversationReadRequest(sensitiveMessageId);
        var receipt = new ConversationReadReceipt(Guid.NewGuid(), sensitiveMessageId);

        Assert.DoesNotContain(sensitiveMessageId.ToString(), request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMessageId.ToString(), receipt.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", request.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", receipt.ToString(), StringComparison.Ordinal);
    }
}
