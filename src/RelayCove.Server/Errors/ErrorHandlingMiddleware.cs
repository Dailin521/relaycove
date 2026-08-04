using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Services;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Errors;

public sealed class ErrorHandlingMiddleware(
    RequestDelegate next,
    ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException exception) when (
            exception.StatusCode == StatusCodes.Status413PayloadTooLarge &&
            context.Request.Path.Equals("/api/attachments", StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Attachment request body rejected for {Method} {Path}: {ExceptionType}.",
                context.Request.Method,
                context.Request.Path,
                exception.GetType().Name);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                ApiErrorCodes.AttachmentTooLarge,
                "The attachment exceeds the configured size limit.",
                cancellationToken: context.RequestAborted);
        }
        catch (BadHttpRequestException exception)
        {
            logger.LogInformation("Request body rejected for {Method} {Path}: {ExceptionType}.",
                context.Request.Method,
                context.Request.Path,
                exception.GetType().Name);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.ValidationFailed,
                "The request is invalid.",
                cancellationToken: context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request was canceled for {Method} {Path}.", context.Request.Method, context.Request.Path);
        }
        catch (AuthenticationStorageUnavailableException exception)
        {
            logger.LogWarning(exception, "Authentication storage is temporarily unavailable for {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.ServiceUnavailable,
                "The service is temporarily unavailable.",
                cancellationToken: context.RequestAborted);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            logger.LogWarning(exception, "SQLite storage is temporarily unavailable for {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.ServiceUnavailable,
                "The service is temporarily unavailable.",
                cancellationToken: context.RequestAborted);
        }
        catch (DbUpdateException exception) when (ContainsBusyOrLockedSqlite(exception))
        {
            logger.LogWarning(exception, "SQLite storage is temporarily unavailable for {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.ServiceUnavailable,
                "The service is temporarily unavailable.",
                cancellationToken: context.RequestAborted);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled server error for {Method} {Path}.", context.Request.Method, context.Request.Path);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalServerError,
                "An internal server error occurred.",
                cancellationToken: context.RequestAborted);
        }
    }

    private static bool ContainsBusyOrLockedSqlite(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                return true;
            }
        }

        return false;
    }
}
