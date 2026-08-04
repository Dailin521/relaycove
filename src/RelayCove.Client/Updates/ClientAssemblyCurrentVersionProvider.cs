using System.Reflection;

namespace RelayCove.Client.Updates;

internal sealed class ClientAssemblyCurrentVersionProvider : IClientCurrentVersionProvider
{
    public string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ClientAssemblyCurrentVersionProvider).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            throw new InvalidOperationException("The client informational version is unavailable.");
        }

        var buildMetadataIndex = informationalVersion.IndexOf('+');
        return buildMetadataIndex < 0
            ? informationalVersion
            : informationalVersion[..buildMetadataIndex];
    }
}
