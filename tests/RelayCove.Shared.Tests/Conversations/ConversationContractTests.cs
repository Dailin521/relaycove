using System.Text.Json;
using RelayCove.Shared.Conversations;

namespace RelayCove.Shared.Tests.Conversations;

public sealed class ConversationContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CreateConversationRequest_WhenRoundTripped_PreservesDiscriminatorShape()
    {
        var participantUserId = Guid.Parse("ccf1e0c5-2e10-414a-8249-a497914641af");
        var request = new CreateConversationRequest(
            ConversationType.Direct,
            ParticipantUserId: participantUserId);

        var json = JsonSerializer.Serialize(request, WebJson);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["type", "name", "participantUserId"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal((int)ConversationType.Direct, document.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(request, JsonSerializer.Deserialize<CreateConversationRequest>(json, WebJson));
    }

    [Fact]
    public void ConversationListResponse_WhenRoundTripped_PreservesCompleteAndWatermarks()
    {
        var conversation = new ConversationDto(
            Guid.Parse("11e474c3-3c60-486a-97d8-ce31bf99727f"),
            ConversationType.PrivateChannel,
            "Planning",
            null,
            new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 3, 8, 1, 0, TimeSpan.Zero),
            0,
            0,
            0);
        var response = new ConversationListResponse([conversation], Complete: true);

        var json = JsonSerializer.Serialize(response, WebJson);
        using var document = JsonDocument.Parse(json);
        var roundTripped = JsonSerializer.Deserialize<ConversationListResponse>(json, WebJson);

        Assert.Equal(["conversations", "complete"], document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.True(roundTripped!.Complete);
        Assert.Equal(conversation, Assert.Single(roundTripped.Conversations));
    }

    [Fact]
    public void ConversationMemberContracts_WhenRoundTripped_PreserveRoleAndJoinState()
    {
        var userId = Guid.Parse("85c3711d-4a03-45cd-b198-f9c2edb28a8a");
        var request = new UpsertConversationMemberRequest(userId, ConversationMemberRole.Administrator);
        var member = new ConversationMemberDto(
            userId,
            "alice",
            "Alice",
            ConversationMemberRole.Administrator,
            new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero),
            0,
            false);
        var response = new ConversationMemberListResponse(Guid.NewGuid(), [member]);

        Assert.Equal(request, JsonSerializer.Deserialize<UpsertConversationMemberRequest>(
            JsonSerializer.Serialize(request, WebJson),
            WebJson));
        var roundTripped = JsonSerializer.Deserialize<ConversationMemberListResponse>(
            JsonSerializer.Serialize(response, WebJson),
            WebJson);
        Assert.Equal(response.ConversationId, roundTripped!.ConversationId);
        Assert.Equal(member, Assert.Single(roundTripped.Members));
    }
}
