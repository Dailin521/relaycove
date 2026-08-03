using System.Reflection;
using System.Text.Json;
using RelayCove.Shared.Errors;

namespace RelayCove.Shared.Tests.Errors;

public sealed class ApiErrorContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ApiErrorCodes_WhenReflected_AreUniqueStableStrings()
    {
        var values = typeof(ApiErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .ToArray();

        Assert.Equal(7, values.Length);
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(ApiErrorCodes.SyncCursorInvalid, values);
        Assert.Contains(ApiErrorCodes.IdempotencyKeyReuse, values);
        Assert.Contains(ApiErrorCodes.ConversationAccessRevoked, values);
    }

    [Fact]
    public void ApiErrorResponse_WhenRoundTripped_PreservesFieldErrors()
    {
        var response = new ApiErrorResponse(
            ApiErrorCodes.ValidationFailed,
            "The request is invalid.",
            "trace-123",
            new Dictionary<string, string[]>
            {
                ["userName"] = ["Required.", "Too long."],
            });

        var json = JsonSerializer.Serialize(response, WebJson);
        var roundTripped = JsonSerializer.Deserialize<ApiErrorResponse>(json, WebJson);

        Assert.Equal(response.Code, roundTripped!.Code);
        Assert.Equal(response.Message, roundTripped.Message);
        Assert.Equal(response.TraceId, roundTripped.TraceId);
        Assert.Equal(response.Details!["userName"], roundTripped.Details!["userName"]);
    }

    [Fact]
    public void AuthenticationFailed_WhenUsed_IsSingleGenericPublicCode()
    {
        Assert.Equal("AuthenticationFailed", ApiErrorCodes.AuthenticationFailed);
        Assert.DoesNotContain("Disabled", ApiErrorCodes.AuthenticationFailed, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", ApiErrorCodes.AuthenticationFailed, StringComparison.Ordinal);
        Assert.DoesNotContain("UserNotFound", ApiErrorCodes.AuthenticationFailed, StringComparison.Ordinal);
    }
}
