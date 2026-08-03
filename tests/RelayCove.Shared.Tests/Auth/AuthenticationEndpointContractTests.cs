using System.Text.Json;
using RelayCove.Shared.Auth;

namespace RelayCove.Shared.Tests.Auth;

public sealed class AuthenticationEndpointContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TokenRequest_WhenSerialized_UsesStableShapeAndRedactsSecret(bool isRefresh)
    {
        const string secret = "token-that-must-not-appear";
        object request = isRefresh
            ? new RefreshTokenRequest(secret)
            : new LogoutRequest(secret);

        var json = JsonSerializer.Serialize(request, request.GetType(), WebJson);
        using var document = JsonDocument.Parse(json);
        var text = request.ToString()!;

        Assert.Equal(["refreshToken"], document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(secret, document.RootElement.GetProperty("refreshToken").GetString());
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains("RefreshToken = [REDACTED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentUserResponse_WhenRoundTripped_PreservesStableShape()
    {
        var response = new CurrentUserResponse(
            Guid.Parse("8b9334bb-2407-419a-95cd-58c2b29ed0c7"),
            "Alice",
            "Alice Chen",
            true);

        var json = JsonSerializer.Serialize(response, WebJson);
        using var document = JsonDocument.Parse(json);
        var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(["userId", "userName", "displayName", "isAdmin"], propertyNames);
        Assert.Equal(response, JsonSerializer.Deserialize<CurrentUserResponse>(json, WebJson));
    }

    [Fact]
    public void AuthenticationEndpointContracts_WhenConstructorParametersChange_SecurityPolicyMustBeReviewed()
    {
        Assert.Equal(
            ["RefreshToken"],
            typeof(RefreshTokenRequest).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(
            ["RefreshToken"],
            typeof(LogoutRequest).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(
            ["UserId", "UserName", "DisplayName", "IsAdmin"],
            typeof(CurrentUserResponse).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name));
    }
}
