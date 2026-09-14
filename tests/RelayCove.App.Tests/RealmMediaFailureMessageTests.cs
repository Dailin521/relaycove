using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class RealmMediaFailureMessageTests
{
    [Theory]
    [InlineData(GatewayErrorCode.MediaAddressNotAllowed, "图片地址无效或不允许加载")]
    [InlineData(GatewayErrorCode.UnsupportedImageType, "图片格式不支持，或服务器未返回图片")]
    [InlineData(GatewayErrorCode.MediaTooLarge, "图片过大，超过加载限制")]
    [InlineData(GatewayErrorCode.EmptyMediaContent, "服务器返回的图片内容为空")]
    [InlineData(GatewayErrorCode.RedirectNotAllowed, "图片地址发生跳转，已阻止加载")]
    [InlineData(GatewayErrorCode.RequestTimedOut, "图片加载超时，请重试")]
    [InlineData(GatewayErrorCode.NetworkError, "网络连接异常，无法下载图片")]
    [InlineData(GatewayErrorCode.InvalidResponse, "图片服务器返回的数据异常")]
    public void FromException_WhenGatewayHasSpecificReason_ShowsCorrespondingMessage(
        GatewayErrorCode code,
        string expected)
    {
        var exception = new GatewayException(GatewayErrorKind.Protocol, code);

        Assert.Equal(expected, RealmMediaFailureMessage.FromException(exception));
    }

    [Theory]
    [InlineData(401, "登录已失效，请重新登录后查看图片")]
    [InlineData(403, "没有权限查看这张图片")]
    [InlineData(404, "图片不存在或已被删除")]
    [InlineData(410, "图片不存在或已被删除")]
    [InlineData(408, "图片加载超时，请重试")]
    [InlineData(504, "图片加载超时，请重试")]
    [InlineData(400, "图片请求被拒绝（HTTP 400）")]
    [InlineData(503, "图片服务器异常（HTTP 503），请稍后重试")]
    public void FromException_WhenServerReturnsStatus_ShowsSpecificReason(int status, string expected)
    {
        var exception = new GatewayException(GatewayErrorKind.RequestFailed, GatewayErrorCode.RequestFailed, status);

        Assert.Equal(expected, RealmMediaFailureMessage.FromException(exception));
    }

    [Theory]
    [InlineData(GatewayErrorKind.ReauthRequired, "登录已失效，请重新登录后查看图片")]
    [InlineData(GatewayErrorKind.AuthenticationFailed, "登录已失效，请重新登录后查看图片")]
    [InlineData(GatewayErrorKind.RateLimited, "图片请求过于频繁，请稍后重试")]
    [InlineData(GatewayErrorKind.Offline, "网络连接异常，无法下载图片")]
    [InlineData(GatewayErrorKind.Server, "图片服务器异常，请稍后重试")]
    public void FromException_WhenStatusIsUnavailable_UsesErrorKind(GatewayErrorKind kind, string expected)
    {
        var exception = new GatewayException(kind, GatewayErrorCode.RequestFailed);

        Assert.Equal(expected, RealmMediaFailureMessage.FromException(exception));
    }

    [Fact]
    public void FromException_WhenTransportOrUnknownErrorOccurs_DoesNotExposeRawDetails()
    {
        const string details = "https://private.example/image.png?private-data=redacted";
        Assert.Equal("图片加载超时，请重试", RealmMediaFailureMessage.FromException(new OperationCanceledException(details)));
        Assert.Equal("图片加载超时，请重试", RealmMediaFailureMessage.FromException(new TimeoutException(details)));
        Assert.Equal("网络连接异常，无法下载图片", RealmMediaFailureMessage.FromException(new HttpRequestException(details)));
        Assert.Equal("图片数据读取失败，请重试", RealmMediaFailureMessage.FromException(new IOException(details)));
        Assert.Equal("图片加载失败：未知原因", RealmMediaFailureMessage.FromException(new InvalidOperationException(details)));
        Assert.Equal("图片加载失败：未知原因", RealmMediaFailureMessage.FromException(new Exception(details)));
    }
}
