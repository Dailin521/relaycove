using System.Text.Json;
using RelayCove.Shared.Auth;

namespace RelayCove.Shared.Tests.Auth;

public sealed class LoginContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void LoginRequest_WhenSerialized_UsesStableWebJsonShape()
    {
        var request = new LoginRequest("alice", "password-value", "workstation", "1.0.0");

        var json = JsonSerializer.Serialize(request, WebJson);
        using var document = JsonDocument.Parse(json);
        var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(["userName", "password", "deviceName", "clientVersion"], propertyNames);
        Assert.Equal(request, JsonSerializer.Deserialize<LoginRequest>(json, WebJson));
    }

    [Fact]
    public void LoginRequest_ToString_RedactsPassword()
    {
        const string password = "password-that-must-not-appear";
        var request = new LoginRequest("alice", password, "workstation", "1.0.0");

        var text = request.ToString();

        Assert.DoesNotContain(password, text, StringComparison.Ordinal);
        Assert.Contains("Password = [REDACTED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginResponse_WhenRoundTripped_PreservesFieldsAndOffset()
    {
        var response = new LoginResponse(
            Guid.Parse("8b9334bb-2407-419a-95cd-58c2b29ed0c7"),
            "Alice",
            "access-token-value",
            "refresh-token-value",
            new DateTimeOffset(2026, 8, 3, 12, 30, 0, TimeSpan.FromHours(8)),
            "1.0.0",
            "1.0.0");

        var json = JsonSerializer.Serialize(response, WebJson);
        var roundTripped = JsonSerializer.Deserialize<LoginResponse>(json, WebJson);

        Assert.Equal(response, roundTripped);
        Assert.Equal(TimeSpan.FromHours(8), roundTripped!.ExpiresAt.Offset);
    }

    [Fact]
    public void LoginResponse_ToString_RedactsTokens()
    {
        const string accessToken = "access-token-that-must-not-appear";
        const string refreshToken = "refresh-token-that-must-not-appear";
        var response = new LoginResponse(
            Guid.Parse("8b9334bb-2407-419a-95cd-58c2b29ed0c7"),
            "Alice",
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddMinutes(15),
            "1.0.0",
            "1.0.0");

        var text = response.ToString();

        Assert.DoesNotContain(accessToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
    }
}
