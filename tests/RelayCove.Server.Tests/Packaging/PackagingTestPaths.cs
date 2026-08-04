namespace RelayCove.Server.Tests.Packaging;

internal static class PackagingTestPaths
{
    private static readonly Lazy<string> RepositoryRootValue = new(FindRepositoryRoot);

    public static string RepositoryRoot => RepositoryRootValue.Value;

    public static string GetRepositoryPath(params string[] segments)
    {
        var pathSegments = new[] { RepositoryRoot }.Concat(segments).ToArray();
        return Path.Combine(pathSegments);
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);

        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "RelayCove.sln")) &&
                File.Exists(Path.Combine(candidate.FullName, "AGENTS.md")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the RelayCove repository from '{AppContext.BaseDirectory}'.");
    }
}
