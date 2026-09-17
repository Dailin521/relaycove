using System.Reflection;

namespace RelayCove.Preview.NativeTests;

public class FixtureSession : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name is "get_AccountId" or "add_StateChanged" or "remove_StateChanged") return null;
        throw new InvalidOperationException($"The isolated preview must not call session operation {targetMethod?.Name}.");
    }
}
