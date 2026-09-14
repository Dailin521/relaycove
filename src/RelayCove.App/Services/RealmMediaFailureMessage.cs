using RelayCove.Core;

namespace RelayCove.App.Services;

internal static class RealmMediaFailureMessage
{
    public static string FromException(Exception exception) => exception switch
    {
        GatewayException { Code: GatewayErrorCode.MediaAddressNotAllowed } => "图片地址无效或不允许加载",
        GatewayException { Code: GatewayErrorCode.UnsupportedImageType } => "图片格式不支持，或服务器未返回图片",
        GatewayException { Code: GatewayErrorCode.MediaTooLarge } => "图片过大，超过加载限制",
        GatewayException { Code: GatewayErrorCode.EmptyMediaContent } => "服务器返回的图片内容为空",
        GatewayException { Code: GatewayErrorCode.RedirectNotAllowed } => "图片地址发生跳转，已阻止加载",
        GatewayException { Code: GatewayErrorCode.RequestTimedOut } => "图片加载超时，请重试",
        GatewayException { Code: GatewayErrorCode.NetworkError } => "网络连接异常，无法下载图片",
        GatewayException { Kind: GatewayErrorKind.AuthenticationFailed or GatewayErrorKind.ReauthRequired } => "登录已失效，请重新登录后查看图片",
        GatewayException { StatusCode: 401 } => "登录已失效，请重新登录后查看图片",
        GatewayException { StatusCode: 403 } => "没有权限查看这张图片",
        GatewayException { StatusCode: 404 or 410 } => "图片不存在或已被删除",
        GatewayException { StatusCode: 408 or 504 } => "图片加载超时，请重试",
        GatewayException { Kind: GatewayErrorKind.RateLimited } => "图片请求过于频繁，请稍后重试",
        GatewayException { Code: GatewayErrorCode.InvalidResponse } => "图片服务器返回的数据异常",
        GatewayException { StatusCode: >= 500 and <= 599 } error => $"图片服务器异常（HTTP {error.StatusCode}），请稍后重试",
        GatewayException { StatusCode: >= 400 and <= 499 } error => $"图片请求被拒绝（HTTP {error.StatusCode}）",
        GatewayException { Kind: GatewayErrorKind.Offline } => "网络连接异常，无法下载图片",
        GatewayException { Kind: GatewayErrorKind.Server } => "图片服务器异常，请稍后重试",
        OperationCanceledException or TimeoutException => "图片加载超时，请重试",
        HttpRequestException => "网络连接异常，无法下载图片",
        IOException => "图片数据读取失败，请重试",
        _ => "图片加载失败：未知原因"
    };
}
