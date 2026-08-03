using System.Text.Json;
using RelayCove.Shared.Messages;

namespace RelayCove.Shared.Tests.Messages;

public sealed class MentionCandidateContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MentionCandidateResponse_WhenRoundTripped_PreservesMinimalShape()
    {
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var response = new MentionCandidateListResponse(
            conversationId,
            [new MentionCandidateDto(userId, "Alice_1", "Alice")],
            HasMore: true);

        var json = JsonSerializer.Serialize(response, WebJson);
        using var document = JsonDocument.Parse(json);
        var roundTripped = JsonSerializer.Deserialize<MentionCandidateListResponse>(
            json,
            WebJson);

        Assert.Equal(
            ["conversationId", "candidates", "hasMore"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        var candidateElement = Assert.Single(
            document.RootElement.GetProperty("candidates").EnumerateArray());
        Assert.Equal(
            ["userId", "userName", "displayName"],
            candidateElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(conversationId, roundTripped!.ConversationId);
        Assert.True(roundTripped.HasMore);
        var candidate = Assert.Single(roundTripped.Candidates);
        Assert.Equal(userId, candidate.UserId);
        Assert.Equal("Alice_1", candidate.UserName);
        Assert.Equal("Alice", candidate.DisplayName);
    }

    [Fact]
    public void MentionCandidateContracts_WhenFormatted_RedactIdentityAndCollections()
    {
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string userName = "secret_user";
        const string displayName = "Secret Display";
        var candidate = new MentionCandidateDto(userId, userName, displayName);
        var response = new MentionCandidateListResponse(
            conversationId,
            [candidate],
            HasMore: false);

        Assert.DoesNotContain(userId.ToString("D"), candidate.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userName, candidate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(displayName, candidate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(conversationId.ToString("D"), response.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userName, response.ToString(), StringComparison.Ordinal);
    }
}
