using Microsoft.AspNetCore.Authorization;

namespace RelayCove.Server.Authorization;

public sealed class AdministratorRequirement : IAuthorizationRequirement
{
    public static AdministratorRequirement Instance { get; } = new();

    private AdministratorRequirement()
    {
    }
}
