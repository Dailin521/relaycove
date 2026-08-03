namespace RelayCove.Client.Tests;

public sealed class ProjectAssemblyTests
{
    [Fact]
    public void ProjectAssembly_WhenLoaded_HasExpectedName()
    {
        var assemblyName = typeof(global::RelayCove.Client.App).Assembly.GetName().Name;

        Assert.Equal("RelayCove.Client", assemblyName);
    }
}
