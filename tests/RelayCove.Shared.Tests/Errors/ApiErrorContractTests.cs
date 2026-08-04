using System.Reflection;
using System.Text.Json;
using RelayCove.Shared.Errors;

namespace RelayCove.Shared.Tests.Errors;

public sealed class ApiErrorContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] ExpectedCodes =
    [
        "ValidationFailed",
        "AuthenticationFailed",
        "AuthenticationRequired",
        "AccessDenied",
        "RateLimitExceeded",
        "ServiceUnavailable",
        "InternalServerError",
        "UserNameAlreadyExists",
        "UserNotFound",
        "ConversationTypeConflict",
        "SyncCursorInvalid",
        "IdempotencyKeyReuse",
        "ConversationAccessRevoked",
        "MessageTypeUnsupported",
        "AttachmentTooLarge",
    ];

    [Fact]
    public void ApiErrorCodes_WhenReflected_AreUniqueStableStrings()
    {
        var fields = typeof(ApiErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToArray();
        var values = fields
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .ToArray();

        Assert.Equal(ExpectedCodes.Length, values.Length);
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ExpectedCodes.Order(StringComparer.Ordinal), values.Order(StringComparer.Ordinal));
        Assert.All(fields, field => Assert.Equal(field.Name, field.GetRawConstantValue()));
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
        using var document = JsonDocument.Parse(json);
        var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        var roundTripped = JsonSerializer.Deserialize<ApiErrorResponse>(json, WebJson);

        Assert.Equal(["code", "message", "traceId", "details"], propertyNames);
        Assert.Equal(response.Code, roundTripped!.Code);
        Assert.Equal(response.Message, roundTripped.Message);
        Assert.Equal(response.TraceId, roundTripped.TraceId);
        Assert.Equal(response.Details!["userName"], roundTripped.Details!["userName"]);
    }

    [Fact]
    public void AuthenticationErrorCodes_WhenInspected_DoNotExposeAuthenticationState()
    {
        string[] values =
        [
            ApiErrorCodes.AuthenticationFailed,
            ApiErrorCodes.AuthenticationRequired,
        ];
        string[] forbiddenAuthenticationStateFragments =
        [
            "UserNotFound",
            "AccountNotFound",
            "Disabled",
            "Locked",
            "InvalidPassword",
            "WrongPassword",
            "PasswordIncorrect",
        ];

        foreach (var value in values)
        {
            foreach (var fragment in forbiddenAuthenticationStateFragments)
            {
                Assert.DoesNotContain(fragment, value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
