namespace RelayCove.Core.Tests;

public sealed class GatewayExceptionTests
{
    [Fact]
    public void ToString_WhenInnerMessageContainsSecrets_DoesNotExposeRawInnerException()
    {
        const string secret = "https://private.example/api/v1/events Authorization: Basic secret-value";
        var inner = new HttpRequestException(secret);

        var exception = new GatewayException(
            GatewayErrorKind.Offline,
            GatewayErrorCode.NetworkError,
            innerException: inner);

        Assert.Null(exception.InnerException);
        Assert.Equal(typeof(HttpRequestException).FullName, exception.CauseTypeName);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private.example", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", exception.ToString(), StringComparison.Ordinal);
    }
}
