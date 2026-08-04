using Microsoft.Net.Http.Headers;
using RelayCove.Server.Services;

namespace RelayCove.Server.Endpoints;

public static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/updates/manifest", GetManifestAsync);
        endpoints.MapGet($"{UpdateHostingService.ArtifactRoutePrefix}{{fileName}}", DownloadArtifactAsync);
        return endpoints;
    }

    private static async Task<IResult> GetManifestAsync(
        HttpContext context,
        UpdateHostingService updateHostingService,
        CancellationToken cancellationToken)
    {
        var manifest = await updateHostingService.GetManifestAsync(cancellationToken);
        if (manifest is null)
        {
            return Results.NotFound();
        }

        SetManifestHeaders(context);
        return Results.Ok(manifest);
    }

    private static async Task<IResult> DownloadArtifactAsync(
        string fileName,
        HttpContext context,
        UpdateHostingService updateHostingService,
        CancellationToken cancellationToken)
    {
        var artifact = await updateHostingService.OpenCurrentArtifactAsync(fileName, cancellationToken);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        SetArtifactHeaders(context);
        return Results.File(
            artifact.Stream,
            "application/zip",
            fileName,
            entityTag: new EntityTagHeaderValue($"\"{artifact.Sha256}\""),
            enableRangeProcessing: true);
    }

    private static void SetManifestHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static void SetArtifactHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
}
