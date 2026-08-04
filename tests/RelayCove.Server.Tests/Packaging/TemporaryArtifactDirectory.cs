namespace RelayCove.Server.Tests.Packaging;

internal sealed class TemporaryArtifactDirectory : IDisposable
{
    private readonly string artifactsRoot;

    public TemporaryArtifactDirectory(string purpose)
    {
        artifactsRoot = System.IO.Path.GetFullPath(PackagingTestPaths.GetRepositoryPath("artifacts"))
            .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        Path = System.IO.Path.Combine(
            artifactsRoot,
            "packaging-tests",
            $"{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        var resolvedPath = System.IO.Path.GetFullPath(Path);
        if (!resolvedPath.StartsWith(artifactsRoot, StringComparison.OrdinalIgnoreCase) ||
            !resolvedPath.Contains(
                $"{System.IO.Path.DirectorySeparatorChar}packaging-tests{System.IO.Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to clean unsafe test path: {resolvedPath}");
        }

        if (Directory.Exists(resolvedPath))
        {
            ArtifactCleanupSafety.AssertSafeRecursiveDelete(resolvedPath);
            Directory.Delete(resolvedPath, recursive: true);
        }
    }
}
