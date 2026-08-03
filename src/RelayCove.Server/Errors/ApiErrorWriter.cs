using RelayCove.Shared.Errors;

namespace RelayCove.Server.Errors;

public static class ApiErrorWriter
{
    public static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? details = null,
        CancellationToken cancellationToken = default)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(
            new ApiErrorResponse(code, message, context.TraceIdentifier, details),
            cancellationToken);
    }

    public static IResult Result(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? details = null)
    {
        return Results.Json(
            new ApiErrorResponse(code, message, context.TraceIdentifier, details),
            statusCode: statusCode);
    }
}
