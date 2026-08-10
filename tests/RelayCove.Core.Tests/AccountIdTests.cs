using RelayCove.Core;

namespace RelayCove.Core.Tests;

public sealed class AccountIdTests
{
    [Fact]
    public void Create_WhenEquivalentRealmInput_ProducesStableUrlSafeValue()
    {
        var one = AccountId.Create(RealmEndpoint.Parse("https://EXAMPLE.test:443/"), 42);
        var two = AccountId.Create(RealmEndpoint.Parse("https://example.test/"), 42);

        Assert.Equal(one, two);
        Assert.DoesNotContain('+', one.Value);
        Assert.DoesNotContain('/', one.Value);
        Assert.DoesNotContain('=', one.Value);
        Assert.Equal(64, one.Value.Length);
        Assert.Matches("^[0-9a-f]{64}$", one.Value);
    }
}
