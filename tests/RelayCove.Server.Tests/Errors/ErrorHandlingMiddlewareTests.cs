using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Server.Errors;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Tests.Errors;

public sealed class ErrorHandlingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenAttachmentRequestIsRejectedByHostForSize_ReturnsStable413()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethod.Post.Method;
        context.Request.Path = "/api/attachments";
        context.Response.Body = new MemoryStream();
        var middleware = new ErrorHandlingMiddleware(
            _ => throw new BadHttpRequestException(
                "Request body too large.",
                StatusCodes.Status413PayloadTooLarge),
            NullLogger<ErrorHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var response = await JsonSerializer.DeserializeAsync<ApiErrorResponse>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(ApiErrorCodes.AttachmentTooLarge, response!.Code);
        Assert.False(string.IsNullOrWhiteSpace(response.TraceId));
    }

    [Fact]
    public async Task InvokeAsync_WhenOtherRequestIsRejectedByHostForSize_PreservesGeneric400Boundary()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethod.Post.Method;
        context.Request.Path = "/api/messages";
        context.Response.Body = new MemoryStream();
        var middleware = new ErrorHandlingMiddleware(
            _ => throw new BadHttpRequestException(
                "Request body too large.",
                StatusCodes.Status413PayloadTooLarge),
            NullLogger<ErrorHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var response = await JsonSerializer.DeserializeAsync<ApiErrorResponse>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(ApiErrorCodes.ValidationFailed, response!.Code);
    }
}
