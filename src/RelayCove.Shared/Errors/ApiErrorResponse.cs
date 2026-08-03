namespace RelayCove.Shared.Errors;

public sealed record ApiErrorResponse(
    string Code,
    string Message,
    string? TraceId = null,
    IReadOnlyDictionary<string, string[]>? Details = null);
