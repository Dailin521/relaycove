namespace RelayCove.Client.Tests.Packaging;

internal static class PackagingTestPaths
{
    private static readonly Lazy<string> RepositoryRootValue = new(FindRepositoryRoot);

    public static string RepositoryRoot => RepositoryRootValue.Value;

    public static string GetRepositoryPath(params string[] segments)
    {
        return Path.Combine([RepositoryRoot, .. segments]);
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
