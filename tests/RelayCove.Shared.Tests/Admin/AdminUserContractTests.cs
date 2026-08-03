using System.Text.Json;
using RelayCove.Shared.Admin;

namespace RelayCove.Shared.Tests.Admin;

public sealed class AdminUserContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CreateUserRequest_WhenSerialized_UsesStableShapeAndRedactsPassword()
    {
        const string password = "secret-that-must-not-appear";
        var request = new CreateUserRequest("alice", "Alice", password, true);

        var json = JsonSerializer.Serialize(request, WebJson);
        using var document = JsonDocument.Parse(json);
        var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(["userName", "displayName", "password", "isAdmin"], propertyNames);
        Assert.Equal(password, document.RootElement.GetProperty("password").GetString());
        Assert.DoesNotContain(password, request.ToString(), StringComparison.Ordinal);
        Assert.Contains("Password = [REDACTED]", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AdminUserResponse_WhenRoundTripped_PreservesStableShape()
    {
        var response = new AdminUserResponse(
            Guid.Parse("ccf1e0c5-2e10-414a-8249-a497914641af"),
            "alice",
            "Alice",
            true,
            false,
            new DateTimeOffset(2026, 8, 3, 12, 30, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(response, WebJson);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["userId", "userName", "displayName", "isAdmin", "isDisabled", "createdAt"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(response, JsonSerializer.Deserialize<AdminUserResponse>(json, WebJson));
    }
}
