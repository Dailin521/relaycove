namespace RelayCove.Server.Tests.Packaging;

internal static class ArtifactCleanupSafety
{
    public static void AssertSafeRecursiveDelete(string path)
    {
        var artifactsRoot = System.IO.Path.GetFullPath(
            PackagingTestPaths.GetRepositoryPath("artifacts"))
            .TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var resolvedPath = System.IO.Path.GetFullPath(path)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var artifactsPrefix = artifactsRoot + System.IO.Path.DirectorySeparatorChar;

        if (!resolvedPath.StartsWith(artifactsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to recursively clean a path outside repository artifacts: {resolvedPath}");
        }

        var current = new DirectoryInfo(resolvedPath);
        while (current is not null)
        {
            if (current.Exists &&
                (current.Attributes & FileAttributes.ReparsePoint) is not 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to recursively clean through a reparse point: {current.FullName}");
            }

            current = current.Parent;
        }
    }
}
