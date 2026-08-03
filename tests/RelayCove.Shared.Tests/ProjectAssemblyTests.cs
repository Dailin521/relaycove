using System.Reflection;

namespace RelayCove.Shared.Tests;

public sealed class ProjectAssemblyTests
{
    [Fact]
    public void ProjectAssembly_WhenLoaded_HasExpectedName()
    {
        var assemblyName = Assembly.Load("RelayCove.Shared").GetName().Name;

        Assert.Equal("RelayCove.Shared", assemblyName);
    }
}
