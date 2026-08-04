using System.Text.Json;
using RelayCove.Shared.Messages;

namespace RelayCove.Shared.Tests.Messages;

public sealed class SearchContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SearchContracts_WhenRoundTripped_PreserveStableShape()
    {
        var result = new SearchResultDto(
            42,
            Guid.NewGuid(),
            "Private discussion",
            "Alice",
            "Sensitive result snippet",
            DateTimeOffset.Parse("2026-08-04T00:00:00Z"),
            "sensitive-file.txt");
        var response = new SearchResponse([result], HasMore: true);

        var json = JsonSerializer.Serialize(response, WebJson);
        using var document = JsonDocument.Parse(json);
        var roundTripped = JsonSerializer.Deserialize<SearchResponse>(json, WebJson)!;

        Assert.Equal(
            ["results", "hasMore"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        var resultElement = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(
            [
                "messageId",
                "conversationId",
                "conversationName",
                "senderName",
                "snippet",
                "createdAt",
                "matchedAttachmentFileName",
            ],
            resultElement.EnumerateObject().Select(property => property.Name));
        Assert.True(roundTripped.HasMore);
        Assert.Equal(result, Assert.Single(roundTripped.Results));
    }

    [Fact]
    public void SearchContracts_WhenFormatted_RedactAllSearchData()
    {
        var conversationId = Guid.NewGuid();
        const long messageId = 42;
        const string conversationName = "Private discussion";
        const string senderName = "Alice";
        const string snippet = "Sensitive result snippet";
        const string fileName = "sensitive-file.txt";
        var createdAt = DateTimeOffset.Parse("2026-08-04T00:00:00Z");
        var result = new SearchResultDto(
            messageId,
            conversationId,
            conversationName,
            senderName,
            snippet,
            createdAt,
            fileName);
        var response = new SearchResponse([result], HasMore: true);

        var formattedResult = result.ToString();
        var formattedResponse = response.ToString();
        foreach (var sensitiveValue in new[]
                 {
                     messageId.ToString(),
                     conversationId.ToString("D"),
                     conversationName,
                     senderName,
                     snippet,
                     createdAt.ToString("O"),
                     fileName,
                 })
        {
            Assert.DoesNotContain(sensitiveValue, formattedResult, StringComparison.Ordinal);
            Assert.DoesNotContain(sensitiveValue, formattedResponse, StringComparison.Ordinal);
        }

        Assert.Equal("SearchResultDto { [REDACTED] }", formattedResult);
        Assert.Equal("SearchResponse { Results = [REDACTED], HasMore = True }", formattedResponse);
    }
}
