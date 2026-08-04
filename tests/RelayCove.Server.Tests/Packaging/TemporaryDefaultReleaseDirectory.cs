namespace RelayCove.Server.Tests.Packaging;

internal sealed class TemporaryDefaultReleaseDirectory : IDisposable
{
    private readonly string artifactsRoot;

    public TemporaryDefaultReleaseDirectory()
    {
        Version = $"0.0.0-packaging-test.{Guid.NewGuid():N}";
        artifactsRoot = System.IO.Path.GetFullPath(PackagingTestPaths.GetRepositoryPath("artifacts"));
        Path = System.IO.Path.Combine(artifactsRoot, "server", Version);
        Assert.False(Directory.Exists(Path), $"Unique test release path already exists: {Path}");
    }

    public string Version { get; }

    public string Path { get; }

    public string OutputRoot => artifactsRoot;

    public void Dispose()
    {
        var resolvedPath = System.IO.Path.GetFullPath(Path);
        var expectedParent = System.IO.Path.Combine(artifactsRoot, "server")
            .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!resolvedPath.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !System.IO.Path.GetFileName(resolvedPath).StartsWith(
                "0.0.0-packaging-test.",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to clean unsafe default release path: {resolvedPath}");
        }

        if (Directory.Exists(resolvedPath))
        {
            ArtifactCleanupSafety.AssertSafeRecursiveDelete(resolvedPath);
            Directory.Delete(resolvedPath, recursive: true);
        }
    }
}
