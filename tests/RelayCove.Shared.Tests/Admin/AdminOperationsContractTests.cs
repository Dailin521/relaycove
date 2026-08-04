using System.Text.Json;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Conversations;

namespace RelayCove.Shared.Tests.Admin;

public sealed class AdminOperationsContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AdminChannelResponse_WhenRoundTripped_PreservesOnlyChannelMetadata()
    {
        var response = new AdminChannelResponse(
            Guid.Parse("6d25331b-dbb0-4b3d-811e-64c34cda9f74"),
            ConversationType.PrivateChannel,
            "Operations",
            new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 4, 12, 1, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(response, WebJson);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["id", "type", "name", "createdAt", "updatedAt"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(response, JsonSerializer.Deserialize<AdminChannelResponse>(json, WebJson));
    }

    [Fact]
    public void UploadSettingsContracts_WhenRoundTripped_PreserveByteLimit()
    {
        var request = new UpdateUploadSettingsRequest(100L * 1024 * 1024);
        var response = new UploadSettingsResponse(25L * 1024 * 1024);

        Assert.Equal(request, JsonSerializer.Deserialize<UpdateUploadSettingsRequest>(
            JsonSerializer.Serialize(request, WebJson),
            WebJson));
        Assert.Equal(response, JsonSerializer.Deserialize<UploadSettingsResponse>(
            JsonSerializer.Serialize(response, WebJson),
            WebJson));
    }
}
