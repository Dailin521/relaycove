using System.Text.Json;
using RelayCove.Shared.Messages;

namespace RelayCove.Shared.Tests.Messages;

public sealed class SyncContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SyncResponse_WhenRoundTripped_PreservesStableShape()
    {
        var response = new SyncResponse(
            [new MessageDto(
                7,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Sender",
                MessageType.Text,
                "content",
                null,
                [],
                [],
                new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero))],
            7,
            9,
            true);

        var json = JsonSerializer.Serialize(response, WebJson);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["messages", "nextCursor", "snapshotUpperBound", "hasMore"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        var roundTrip = JsonSerializer.Deserialize<SyncResponse>(json, WebJson)!;
        Assert.Equal(response.NextCursor, roundTrip.NextCursor);
        Assert.Equal(response.SnapshotUpperBound, roundTrip.SnapshotUpperBound);
        Assert.Equal(response.HasMore, roundTrip.HasMore);
        Assert.Equal([7L], roundTrip.Messages.Select(message => message.Id));
    }

    [Fact]
    public void SyncResponse_WhenFormatted_RedactsMessagesAndCursors()
    {
        const string secret = "sync-response-secret-a8e251";
        const long sensitiveCursor = 9_876_543_210;
        var response = new SyncResponse(
            [new MessageDto(
                sensitiveCursor,
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
            sensitiveCursor,
            sensitiveCursor,
            false);

        Assert.DoesNotContain(secret, response.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveCursor.ToString(), response.ToString(), StringComparison.Ordinal);
        Assert.Contains("Messages = [REDACTED]", response.ToString(), StringComparison.Ordinal);
        Assert.Contains("NextCursor = [REDACTED]", response.ToString(), StringComparison.Ordinal);
        Assert.Contains("SnapshotUpperBound = [REDACTED]", response.ToString(), StringComparison.Ordinal);
    }
}
