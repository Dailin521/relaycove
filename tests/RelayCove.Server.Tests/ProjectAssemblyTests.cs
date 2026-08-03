namespace RelayCove.Server.Tests;

public sealed class ProjectAssemblyTests
{
    [Fact]
    public void ProjectAssembly_WhenLoaded_HasExpectedName()
    {
        var assemblyName = typeof(Program).Assembly.GetName().Name;

        Assert.Equal("RelayCove.Server", assemblyName);
    }
}
