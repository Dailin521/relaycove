namespace RelayCove.Updater.Tests;

public sealed class ProjectAssemblyTests
{
    [Fact]
    public void ProjectAssembly_WhenLoaded_HasExpectedName()
    {
        var assemblyName = typeof(global::RelayCove.Updater.Program).Assembly.GetName().Name;

        Assert.Equal("RelayCove.Updater", assemblyName);
    }
}
