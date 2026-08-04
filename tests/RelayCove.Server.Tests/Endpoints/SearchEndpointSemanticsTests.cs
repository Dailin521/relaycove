using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class SearchEndpointSemanticsTests(RelayCoveWebApplicationFactory factory) :
    IClassFixture<RelayCoveWebApplicationFactory>,
    IAsyncLifetime
{
    private const string ExistingPassword = "a secure search semantics test phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Search_WhenCaseBehaviorDiffersByCharacterSet_UsesSqliteMatchForSnippet()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var adminName = $"search-semantics-{suffix}";
        await factory.CreateUserAsync(adminName, ExistingPassword, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        var conversation = await CreateChannelAsync(
            client,
            $"Search semantics {suffix}");

        const string asciiFirstMarker = "ASCII-FIRST-MATCH";
        const string asciiLateMarker = "ASCII-LATE-MATCH";
        var asciiMessage = await SendTextAsync(
            client,
            conversation.Id,
            $"{new string('a', 120)}{asciiFirstMarker}-FiRsT-{new string('b', 220)}" +
            $"{asciiLateMarker}-first-END");

        var asciiResult = Assert.Single(
            (await SearchAsync(client, conversation.Id, "first")).Results);
        Assert.Equal(asciiMessage.Id, asciiResult.MessageId);
        Assert.Contains("FiRsT", asciiResult.Snippet, StringComparison.Ordinal);
        Assert.Contains(asciiFirstMarker, asciiResult.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain(asciiLateMarker, asciiResult.Snippet, StringComparison.Ordinal);

        const string nonAsciiEarlyMarker = "NONASCII-EARLY";
        const string nonAsciiTargetMarker = "NONASCII-TARGET";
        var nonAsciiMessage = await SendTextAsync(
            client,
            conversation.Id,
            $"{nonAsciiEarlyMarker}-Ä-{new string('x', 220)}" +
            $"{nonAsciiTargetMarker}-ä-{new string('y', 120)}");

        var nonAsciiResult = Assert.Single(
            (await SearchAsync(client, conversation.Id, "ä")).Results);
        Assert.Equal(nonAsciiMessage.Id, nonAsciiResult.MessageId);
        Assert.Contains($"{nonAsciiTargetMarker}-ä", nonAsciiResult.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain(nonAsciiEarlyMarker, nonAsciiResult.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("Ä", nonAsciiResult.Snippet, StringComparison.Ordinal);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string userName)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, ExistingPassword, "search-semantics-test", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static async Task<ConversationDto> CreateChannelAsync(
        HttpClient client,
        string name)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(ConversationType.PublicChannel, name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task<MessageDto> SendTextAsync(
        HttpClient client,
        Guid conversationId,
        string content)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/messages",
            new SendMessageRequest(
                Guid.NewGuid(),
                conversationId,
                MessageType.Text,
                content,
                ReplyToMessageId: null,
                AttachmentIds: [],
                MentionUserIds: []));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageDto>())!;
    }

    private static async Task<SearchResponse> SearchAsync(
        HttpClient client,
        Guid conversationId,
        string keyword)
    {
        using var response = await client.GetAsync(
            $"/api/search?keyword={Uri.EscapeDataString(keyword)}" +
            $"&conversationId={conversationId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SearchResponse>())!;
    }
}
